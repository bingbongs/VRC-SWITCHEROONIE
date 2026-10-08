#include "MinHook.h"
#include "fake_calls.hpp"
#include "hooks.hpp"
#include "ipc.hpp"
#include <array>
#include <cmath>
#include <iostream>
namespace
{
int failures = 0, checks = 0;
void Check(bool good, const char *message)
{
    ++checks;
    if (!good)
    {
        ++failures;
        std::cerr << "FAIL " << message << '\n';
    }
}
vr::DriverPose_t Pose(double x = 0)
{
    vr::DriverPose_t p{};
    p.qRotation = {1, 0, 0, 0};
    p.qWorldFromDriverRotation = {1, 0, 0, 0};
    p.qDriverFromHeadRotation = {1, 0, 0, 0};
    p.vecPosition[0] = x;
    p.vecPosition[1] = 1.7;
    p.poseIsValid = true;
    p.deviceIsConnected = true;
    p.result = vr::TrackingResult_Running_OK;
    return p;
}
std::array<double,3> WorldPoint(const vr::DriverPose_t &p)
{
    // Independent matrix oracle for fixture poses, whose local head offset is zero.
    auto q=p.qWorldFromDriverRotation;
    auto x=p.vecPosition[0],y=p.vecPosition[1],z=p.vecPosition[2];
    return {
        (1-2*(q.y*q.y+q.z*q.z))*x+2*(q.x*q.y-q.w*q.z)*y+2*(q.x*q.z+q.w*q.y)*z+p.vecWorldFromDriverTranslation[0],
        2*(q.x*q.y+q.w*q.z)*x+(1-2*(q.x*q.x+q.z*q.z))*y+2*(q.y*q.z-q.w*q.x)*z+p.vecWorldFromDriverTranslation[1],
        2*(q.x*q.z-q.w*q.y)*x+2*(q.y*q.z+q.w*q.x)*y+(1-2*(q.x*q.x+q.y*q.y))*z+p.vecWorldFromDriverTranslation[2]};
}
struct HostState
{
    std::array<vr::DriverPose_t, 64> output{};
    std::array<uint64_t, 64> accepted{};
    volatile uint64_t calls{};
    uint64_t wrongOwner{};
};
struct Host final : vr::IVRServerDriverHost
{
    HostState &state;
    std::array<vr::DriverPose_t, 64> &output;
    volatile uint64_t &calls;
    uint64_t ownedDevices;
    Host(HostState &shared, uint64_t allowed)
        : state(shared), output(shared.output), calls(shared.calls), ownedDevices(allowed) {}
    bool TrackedDeviceAdded(const char *, vr::ETrackedDeviceClass,
                            vr::ITrackedDeviceServerDriver *) override
    {
        return false;
    }
    __declspec(noinline) void TrackedDevicePoseUpdated(uint32_t index, const vr::DriverPose_t &p,
                                                       uint32_t size) override
    {
        calls = calls + 1;
        if (index >= output.size() || !(ownedDevices & (1ull << index)))
        {
            ++state.wrongOwner;
            return;
        }
        if (size == sizeof(p))
        {
            output[index] = p;
            ++state.accepted[index];
        }
    }
    void VsyncEvent(double) override
    {
    }
    void VendorSpecificEvent(uint32_t, vr::EVREventType, const vr::VREvent_Data_t &,
                             double) override
    {
    }
    bool IsExiting() override
    {
        return false;
    }
    bool PollNextEvent(vr::VREvent_t *, uint32_t) override
    {
        return false;
    }
    void GetRawTrackedDevicePoses(float, vr::TrackedDevicePose_t *, uint32_t) override
    {
    }
    void RequestRestart(const char *, const char *, const char *, const char *) override
    {
        Check(false, "test never asks restart");
    }
    uint32_t GetFrameTimings(vr::Compositor_FrameTiming *, uint32_t) override
    {
        return 0;
    }
    void SetDisplayEyeToHead(uint32_t, const vr::HmdMatrix34_t &,
                             const vr::HmdMatrix34_t &) override
    {
        Check(false, "display owner is untouched");
    }
    void SetDisplayProjectionRaw(uint32_t, const vr::HmdRect2_t &, const vr::HmdRect2_t &) override
    {
        Check(false, "display projections untouched");
    }
    void SetRecommendedRenderTargetSize(uint32_t, uint32_t, uint32_t) override
    {
        Check(false, "render target untouched");
    }
};
struct InputState
{
    volatile uint64_t calls{};
    uint64_t next = 1;
    uint64_t rejectHandle{};
    uint64_t reuseHandle{};
    std::array<float, 256> values{};
    std::array<const void *, 256> owners{};
    uint64_t wrongOwner{};
};
struct Input final : vr::IVRDriverInput
{
    InputState &state;
    volatile uint64_t &calls;
    uint64_t &rejectHandle;
    std::array<float, 256> &values;
    uint32_t ownedContainers;
    Input(InputState &shared, uint32_t allowed)
        : state(shared), calls(shared.calls), rejectHandle(shared.rejectHandle),
          values(shared.values), ownedContainers(allowed) {}
    vr::EVRInputError Allocate(uint64_t container, uint64_t *h)
    {
        if (container < 100 || container > 103 ||
            !(ownedContainers & (1u << (container - 100))) || !h ||
            state.next >= state.owners.size())
            return vr::VRInputError_InvalidParam;
        *h = state.reuseHandle ? state.reuseHandle : state.next++;
        if (*h >= state.owners.size()) return vr::VRInputError_InvalidHandle;
        state.owners[*h] = this;
        return vr::VRInputError_None;
    }
    bool Owns(uint64_t handle)
    {
        if (handle >= state.owners.size() || state.owners[handle] != this)
        {
            ++state.wrongOwner;
            return false;
        }
        return true;
    }
    __declspec(noinline) vr::EVRInputError CreateBooleanComponent(uint64_t container, const char *,
                                                                  uint64_t *h) override
    {
        calls = calls + 3;
        return Allocate(container, h);
    }
    __declspec(noinline) vr::EVRInputError UpdateBooleanComponent(uint64_t h, bool v,
                                                                  double) override
    {
        calls = calls + 5;
        if (!Owns(h)) return vr::VRInputError_InvalidHandle;
        if (rejectHandle && h == rejectHandle)
            return vr::VRInputError_InvalidHandle;
        if (h < values.size())
            values[h] = v ? 1.f : 0.f;
        return vr::VRInputError_None;
    }
    __declspec(noinline) vr::EVRInputError CreateScalarComponent(uint64_t container, const char *,
                                                                 uint64_t *h, vr::EVRScalarType,
                                                                 vr::EVRScalarUnits) override
    {
        calls = calls + 7;
        return Allocate(container, h);
    }
    __declspec(noinline) vr::EVRInputError UpdateScalarComponent(uint64_t h, float v,
                                                                 double) override
    {
        calls = calls + 11;
        if (!Owns(h)) return vr::VRInputError_InvalidHandle;
        if (h < values.size())
            values[h] = v;
        return vr::VRInputError_None;
    }
    vr::EVRInputError CreateHapticComponent(uint64_t container, const char *, uint64_t *h) override
    {
        return Allocate(container, h);
    }
    __declspec(noinline) vr::EVRInputError CreateSkeletonComponent(uint64_t container, const char *,
                                                                   const char *, const char *,
                                                                   vr::EVRSkeletalTrackingLevel,
                                                                   const vr::VRBoneTransform_t *,
                                                                   uint32_t, uint64_t *h) override
    {
        calls = calls + 13;
        return Allocate(container, h);
    }
    __declspec(noinline) vr::EVRInputError UpdateSkeletonComponent(uint64_t h,
                                                                   vr::EVRSkeletalMotionRange,
                                                                   const vr::VRBoneTransform_t *,
                                                                   uint32_t) override
    {
        calls = calls + 17;
        if (!Owns(h)) return vr::VRInputError_InvalidHandle;
        if (h < values.size())
            values[h] = 17;
        return vr::VRInputError_None;
    }
    __declspec(noinline) vr::EVRInputError CreatePoseComponent(uint64_t container, const char *,
                                                               uint64_t *h) override
    {
        calls = calls + 19;
        return Allocate(container, h);
    }
    __declspec(noinline) vr::EVRInputError UpdatePoseComponent(uint64_t h,
                                                               const vr::HmdMatrix34_t *,
                                                               double) override
    {
        calls = calls + 23;
        if (!Owns(h)) return vr::VRInputError_InvalidHandle;
        if (h < values.size())
            values[h] = 23;
        return vr::VRInputError_None;
    }
    vr::EVRInputError CreateEyeTrackingComponent(uint64_t container, const char *, uint64_t *h) override
    {
        return Allocate(container, h);
    }
    vr::EVRInputError UpdateEyeTrackingComponent(uint64_t, const vr::VREyeTrackingData_t *,
                                                 double) override
    {
        return vr::VRInputError_None;
    }
};
void ExternalPose(void *, uint32_t, const vr::DriverPose_t &, uint32_t)
{
}
} // namespace
int main()
{
    alignas(8) std::byte memory[512]{};
    sw::Request request{};
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    HostState hostState;
    Host ownHost(hostState, 0), host(hostState, 1), leftHost(hostState, 2),
         rightHost(hostState, 4), trackerHost(hostState, 8);
    InputState inputState;
    Input ownInput(inputState, 0), input(inputState, 4), leftInput(inputState, 2),
          headInput(inputState, 1), trackerInput(inputState, 8);
    CallPose(&ownHost, 0, Pose(123));
    Check(hostState.wrongOwner == 1 && !hostState.output[0].deviceIsConnected,
          "isolated host rejects a companion context writing another provider's HMD");
    hostState.wrongOwner = 0;
    uint64_t ownerProbe{};
    Check(CallCreateBool(&input, 102, "/input/unknown/click", &ownerProbe) == vr::VRInputError_None &&
              CallUpdateBool(&ownInput, ownerProbe, true) == vr::VRInputError_InvalidHandle &&
              inputState.wrongOwner == 1 && input.values[ownerProbe] == 0,
          "isolated input rejects another provider context updating a captured handle");
    inputState.wrongOwner = 0;
    sw::Router router;
    router.SetRole(0, sw::DeviceRole::Head, 100);
    router.SetRole(1, sw::DeviceRole::Left, 101);
    router.SetRole(2, sw::DeviceRole::Right, 102);
    router.SetRole(3, sw::DeviceRole::Other, 103);
    sw::HookRuntime hooks(router, memory);
    if (!hooks.Install(&ownHost, &ownInput, false))
    {
        std::cerr << "Could not install isolated hooks: " << hooks.failure() << '\n';
        return 1;
    }
    Check(hooks.CheckIntegrity(), "MinHook hooks actual methods from fake interface vtable");
    auto noSourceCount = hostState.accepted[0];
    hooks.Submit(0, Pose(777));
    Check(hostState.accepted[0] == noSourceCount && !hostState.wrongOwner,
          "direct submission cannot invent a vendor host context before its first physical callback");
    uint64_t trigger{}, menu{}, axis{}, tracker{}, system{}, headUnknown{}, proximity{}, run{};
    uint64_t menuTouch{}, otherMenu{}, applicationMenu{}, triggerTouch{}, grip{}, gripTouch{},
             jump{}, jumpTouch{}, axisX{};
    CallCreateBool(&input, 102, "/input/trigger/click", &trigger);
    CallCreateBool(&leftInput, 101, "/input/y/click", &menu);
    CallCreateScalar(&leftInput, 101, "/input/joystick/y", &axis);
    CallCreateBool(&trackerInput, 103, "/input/trigger/click", &tracker);
    CallCreateBool(&leftInput, 101, "/input/system/click", &system);
    CallCreateBool(&headInput, 100, "/input/unknown/click", &headUnknown);
    CallCreateBool(&headInput, 100, "/input/proximity", &proximity);
    CallCreateBool(&leftInput, 101, "/input/joystick/click", &run);
    CallCreateBool(&leftInput, 101, "/input/y/touch", &menuTouch);
    CallCreateBool(&input, 102, "/input/b/click", &otherMenu);
    CallCreateBool(&leftInput, 101, "/input/application_menu/click", &applicationMenu);
    CallCreateBool(&input, 102, "/input/trigger/touch", &triggerTouch);
    CallCreateScalar(&input, 102, "/input/grip/value", &grip);
    CallCreateBool(&input, 102, "/input/grip/touch", &gripTouch);
    CallCreateBool(&input, 102, "/input/a/click", &jump);
    CallCreateBool(&input, 102, "/input/a/touch", &jumpTouch);
    CallCreateScalar(&leftInput, 101, "/input/joystick/x", &axisX);
    CallPose(&host, 0, Pose());
    CallPose(&leftHost, 1, Pose(-.2));
    CallPose(&rightHost, 2, Pose(.2));
    CallPose(&trackerHost, 3, Pose(7));
    CallUpdateBool(&input, trigger, true);
    Check(input.values[trigger] == 1, "physical inputs pass through");
    request.requestedMode = 1;
    request.armed = 1;
    request.epoch = 1;
    request.height = -.4;
    request.actions = 1 | 4 | 16;
    request.forward = .8;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    auto headCount = hostState.accepted[0], leftCount = hostState.accepted[1],
         rightCount = hostState.accepted[2], trackerCount = hostState.accepted[3];
    hooks.Tick(request.timestamp);
    Check(hostState.accepted[0] == headCount + 1 &&
              hostState.accepted[1] == leftCount + 1 &&
              hostState.accepted[2] == rightCount + 1 &&
              hostState.accepted[3] == trackerCount && !hostState.wrongOwner,
          "frame Tick submits head and both hands through each captured vendor-owned host context");
    Check(input.values[trigger] == 1 && input.values[menu] == 1 &&
              std::abs(input.values[axis] - .8f) < 1e-6 && input.values[proximity] == 1 &&
              !inputState.wrongOwner,
          "Tick-only actions reach distinct creator contexts without waiting for vendor input updates");
    auto sourceStatus = hooks.StatusNow(sw::QpcNow());
    Check(!sourceStatus.proximityKnown && !sourceStatus.proximityActive &&
              sourceStatus.proximityAgeMilliseconds == -1 && input.values[proximity] == 1,
          "synthetic proximity true cannot invent an observed physical worn state");
    Check(sourceStatus.leftPhysicalAgeMilliseconds >= 0 &&
              sourceStatus.leftPhysicalAgeMilliseconds <= sw::WatchdogMilliseconds &&
              sourceStatus.rightPhysicalAgeMilliseconds >= 0 &&
              sourceStatus.rightPhysicalAgeMilliseconds <= sw::WatchdogMilliseconds,
          "controller ages refer to independently captured physical callbacks");
    CallPose(&host, 0, Pose(4));
    sw::PoseSnapshot source{};
    Check(router.Physical(0, source) && source.pose.vecPosition[0] == 4,
          "F1 isolated: pre-output physical stream moves independently");
    Check(host.output[0].vecPosition[0] == 0 &&
              std::abs(host.output[0].vecPosition[1] - 1.3) < 1e-9,
          "synthetic output remains anchored while real source moves");
    CallPose(&trackerHost, 3, Pose(8));
    Check(host.output[3].vecPosition[0] == 8, "generic tracker pose byte path remains physical");
    CallUpdateBool(&trackerInput, tracker, true);
    Check(input.values[tracker] == 1, "tracker trigger inputs remain physical");
    CallUpdateBool(&leftInput, system, true);
    Check(input.values[system] == 0, "physical system button suppressed and never synthesized");
    CallUpdateBool(&headInput, headUnknown, false);
    Check(input.values[headUnknown] == 0, "unknown HMD input preserved");
    CallUpdateBool(&headInput, proximity, false);
    Check(input.values[proximity] == 1, "HMD proximity only forced present in desktop");
    sourceStatus = hooks.StatusNow(sw::QpcNow());
    Check(sourceStatus.proximityKnown && !sourceStatus.proximityActive &&
              sourceStatus.proximityAgeMilliseconds >= 0 &&
              sourceStatus.proximityAgeMilliseconds <= sw::WatchdogMilliseconds,
          "raw false vendor proximity remains false while synthetic worn output is true");
    CallUpdateBool(&headInput, proximity, true);
    Check(hooks.StatusNow(sw::QpcNow()).proximityActive == 1,
          "raw vendor worn transition is independently observable");
    CallUpdateBool(&headInput, proximity, false);
    CallUpdateBool(&input, trigger, false);
    CallUpdateScalar(&leftInput, axis, -1);
    Check(input.values[trigger] == 1 && std::abs(input.values[axis] - .8f) < 1e-6,
          "native trigger/movement synthesis replaces physical events");
    Check(input.values[menu] == 1 && input.values[run] == 1,
          "observed VRChat menu and run paths driven");
    Check(input.values[menuTouch] == 0 && input.values[otherMenu] == 0 &&
              input.values[applicationMenu] == 0 && input.values[triggerTouch] == 0,
          "menu pulse selects one click channel and never synthesizes gesture touches");
    auto menuStatus = hooks.StatusNow(sw::QpcNow());
    Check(menuStatus.inputCoverage == 255 &&
              menuStatus.selectedMenuPath == uint32_t(sw::MenuPath::LeftY) &&
              menuStatus.menuPressed == 1 && menuStatus.menuRisingEdges == 1 &&
              menuStatus.inputArmed == 1 && menuStatus.effectiveNativeActions == request.actions,
          "coverage reports both axes/grip/jump/run and the accepted selected menu press");
    request.actions = 0;
    request.handPreset = 2;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    Check(input.values[menu] == 0 && input.values[menuTouch] == 0 &&
              input.values[otherMenu] == 0 && input.values[applicationMenu] == 0,
          "menu hand preset alone releases menu buttons and does not open a menu");
    menuStatus = hooks.StatusNow(sw::QpcNow());
    Check(menuStatus.menuPressed == 0 && menuStatus.menuRisingEdges == 1 &&
              menuStatus.effectiveNativeActions == 0,
          "preset-only diagnostic records release without inventing a new menu edge");
    request.actions = 4;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    input.rejectHandle = menu;
    Check(CallUpdateBool(&leftInput, menu, false) == vr::VRInputError_InvalidHandle,
          "driver preserves actual input backend rejection");
    menuStatus = hooks.StatusNow(sw::QpcNow());
    Check(menuStatus.lastInputError == uint32_t(vr::VRInputError_InvalidHandle) &&
              menuStatus.menuPressed == 0 && menuStatus.menuRisingEdges == 1,
          "rejected menu write reports error without claiming a delivered press");
    input.rejectHandle = 0;
    hooks.Tick(request.timestamp);
    Check(input.values[menu] == 1 && input.values[menuTouch] == 0,
          "menu pulse creates a fresh press after preset-only neutral");
    menuStatus = hooks.StatusNow(sw::QpcNow());
    Check(menuStatus.menuRisingEdges == 2 && menuStatus.menuPressed == 1 &&
              menuStatus.lastInputError == uint32_t(vr::VRInputError_InvalidHandle),
          "accepted retry advances edge count while retaining last nonzero input error");
    request.actions = 0;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    Check(input.values[menu] == 0, "menu pulse ends with a real Boolean release");
    request.actions = 4 | 2 | 8;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    Check(input.values[menu] == 1, "second toggle gets a fresh menu press for application close");
    Check(hooks.StatusNow(sw::QpcNow()).menuRisingEdges == 3,
          "press/release/second press produces two distinct accepted toggle edges");
    Check(input.values[grip] == 1 && input.values[jump] == 1 &&
              input.values[gripTouch] == 0 && input.values[jumpTouch] == 0,
          "grab and jump activate value/click components without gesture touches");
    request.actions = sw::Crouch;
    request.handPreset = 1;
    request.forward = request.strafe = 0;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    Check(host.output[0].vecPosition[1] < .8 && host.output[0].vecPosition[1] > .15 &&
              host.output[1].vecPosition[1] < host.output[0].vecPosition[1] &&
              host.output[2].vecPosition[1] < host.output[0].vecPosition[1] &&
              !hostState.wrongOwner,
          "held crouch reaches HMD and adapted rest hands through each actual provider owner");
    Check(input.values[menu] == 0 && input.values[trigger] == 0 && input.values[grip] == 0 &&
              input.values[jump] == 0 && input.values[run] == 0 && input.values[axis] == 0 &&
              !inputState.wrongOwner && hooks.StatusNow(sw::QpcNow()).menuRisingEdges == 3,
          "posture-only Tick releases buttons and axes without any extra menu edge or owner violation");
    request.actions = sw::Prone;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    Check(host.output[0].vecPosition[1] < .4 && host.output[0].vecPosition[1] >= .15 &&
              host.output[1].vecPosition[1] >= .08 && host.output[2].vecPosition[1] >= .08 &&
              hooks.StatusNow(sw::QpcNow()).effectiveNativeActions == sw::Prone,
          "prone hold routes bounded head/hands and reports the original posture bit");
    request.armed = 0;
    request.handPreset = 2;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    Check(std::abs(host.output[0].vecPosition[1] - 1.3) < 1e-9 &&
              host.output[1].vecPosition[2] > 0 && host.output[2].vecPosition[2] > 0 &&
              hooks.StatusNow(sw::QpcNow()).effectiveNativeActions == 0,
          "disarm ignores stale prone and pointer preset while restoring desktop height and hip hands");
    request.armed = 1;
    request.actions = 0;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    Check(std::abs(host.output[0].vecPosition[1] - 1.3) < 1e-9 &&
              host.output[1].vecPosition[2] > -.2 && host.output[2].vecPosition[2] < -.4 &&
              !hostState.wrongOwner,
          "explicit pointer extends one active hand without bringing the menu wrist far forward");
    request.handPreset=uint32_t(sw::HandPreset::LeftMenuNavigation);
    request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    Check(host.output[1].vecPosition[2]<-.25&&host.output[1].vecPosition[1]>1&&
          host.output[1].qRotation.w==1&&host.output[1].qRotation.x==0&&
          host.output[2].vecPosition[2]<-.4&&input.values[menu]==0,
          "persistent left-menu Tick raises a straight raw wrist and keeps the right pointer without opening another menu");
    request.requestedMode=0;request.armed=0;request.handPreset=0;++request.epoch;
    request.bodySpinActive=1;request.bodySpinGeneration=1;
    request.bodySpinQuaternion[0]=std::sqrt(.5);request.bodySpinQuaternion[3]=std::sqrt(.5);
    request.bodySpinPivot[1]=.95;
    request.timestamp=request.bodySpinLeaseQpc=sw::QpcNow();sw::WriteBlock(memory,request);
    for(uint32_t i=0;i<4;++i) router.SetBodySpinEligible(i,true);
    CallPose(&host,0,Pose());CallPose(&leftHost,1,Pose(-.2));
    CallPose(&rightHost,2,Pose(.2));CallPose(&trackerHost,3,Pose(7));
    hooks.Tick(request.timestamp);
    auto turnedHead=WorldPoint(host.output[0]),turnedTracker=WorldPoint(host.output[3]);
    Check(std::abs(turnedHead[0]+.75)<1e-9&&std::abs(turnedHead[1]-.95)<1e-9&&
          std::abs(turnedTracker[0]+.75)<1e-9&&std::abs(turnedTracker[1]-7.95)<1e-9&&
          !hostState.wrongOwner&&!inputState.wrongOwner,
          "Physical cartwheel callbacks and Tick transform the complete tracked body through each real vendor owner");
    auto bodyStatus=hooks.StatusNow(sw::QpcNow());
    Check(!bodyStatus.actualMode&&bodyStatus.bodySpinActive&&bodyStatus.bodySpinGeneration==1&&
          bodyStatus.bodySpinSamples>=8&&(bodyStatus.reserved0&sw::BodySpinCapability)&&
          bodyStatus.position[0]==0&&bodyStatus.position[1]==1.7,
          "Physical spin capability and submitted-generation telemetry preserve independent physical source coordinates");
    auto bodyTrackerCount=hostState.accepted[3];
    request.bodySpinActive=0;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);
    hooks.Tick(request.timestamp);
    Check(hostState.accepted[3]==bodyTrackerCount+1&&host.output[3].vecPosition[0]==7&&
          host.output[3].qWorldFromDriverRotation.w==1&&host.output[3].vecWorldFromDriverTranslation[1]==0&&
          host.output[0].qWorldFromDriverRotation.w==1&&host.output[1].qWorldFromDriverRotation.w==1&&
          host.output[2].qWorldFromDriverRotation.w==1&&!hostState.wrongOwner,
          "explicit Physical reset restores all prior transformed devices without waiting for vendor callbacks");
    request.requestedMode=1;request.armed=1;++request.epoch;
    request.bodySpinActive=1;++request.bodySpinGeneration;
    request.timestamp=request.bodySpinLeaseQpc=sw::QpcNow();sw::WriteBlock(memory,request);
    hooks.Tick(request.timestamp);
    Check(host.output[0].qWorldFromDriverRotation.z>.7&&host.output[1].qWorldFromDriverRotation.z>.7&&
          host.output[2].qWorldFromDriverRotation.z>.7&&host.output[3].qWorldFromDriverRotation.z>.7,
          "Desktop spin applies after head/hand synthesis and to the independently tracked body device");
    request.bodySpinLeaseQpc-=sw::QpcFrequency()/4;
    request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    Check(hooks.StatusNow(sw::QpcNow()).actualMode&&host.output[0].qWorldFromDriverRotation.w==1&&
          host.output[1].qWorldFromDriverRotation.w==1&&host.output[2].qWorldFromDriverRotation.w==1&&
          host.output[3].qWorldFromDriverRotation.w==1&&host.output[3].poseIsValid,
          "expired separate spin lease restores the tracker and retains unspun Desktop head/hands with a fresh broker");
    request.bodySpinActive=1;++request.bodySpinGeneration;
    request.timestamp=request.bodySpinLeaseQpc=sw::QpcNow();sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    request.requestedMode=0;request.armed=0;request.bodySpinActive=0;++request.epoch;
    request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    Check(host.output[0].qWorldFromDriverRotation.w==1&&host.output[3].qWorldFromDriverRotation.w==1,
          "combined mode/reset transaction removes the body transform from every previously spun device");
    request.requestedMode=0;request.bodySpinActive=1;++request.bodySpinGeneration;
    request.timestamp=request.bodySpinLeaseQpc=sw::QpcNow();sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    Sleep(210);
    request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    Check(host.output[0].qWorldFromDriverRotation.w==1&&!host.output[0].poseIsValid&&
          host.output[3].qWorldFromDriverRotation.w==1&&!host.output[3].poseIsValid&&
          !hooks.StatusNow(sw::QpcNow()).bodySpinActive,
          "watchdog removes held spin even when callbacks stop and marks stale restored source tracking invalid");
    request.timestamp=request.bodySpinLeaseQpc=sw::QpcNow();sw::WriteBlock(memory,request);
    CallPose(&leftHost,1,Pose(-.2));CallPose(&rightHost,2,Pose(.2));CallPose(&trackerHost,3,Pose(7));
    hooks.Tick(request.timestamp);
    Check(host.output[1].qWorldFromDriverRotation.w==1&&host.output[2].qWorldFromDriverRotation.w==1&&
          host.output[3].qWorldFromDriverRotation.w==1&&!hooks.StatusNow(sw::QpcNow()).bodySpinActive,
          "fresh independent controller/tracker callbacks and spin lease cannot continue a partial body turn after the physical head source expires");
    request.bodySpinActive=0;request.bodySpinLeaseQpc=0;request.bodySpinGeneration=0;
    std::fill(std::begin(request.bodySpinQuaternion),std::end(request.bodySpinQuaternion),0);
    std::fill(std::begin(request.bodySpinPivot),std::end(request.bodySpinPivot),0);
    request.requestedMode=1;request.armed=1;request.handPreset=0;++request.epoch;
    request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);
    CallPose(&host,0,Pose());CallPose(&leftHost,1,Pose(-.2));
    CallPose(&rightHost,2,Pose(.2));CallPose(&trackerHost,3,Pose(7));
    hooks.Tick(request.timestamp);
    request.actions = 1 | 4 | 16;
    request.forward = .8;
    request.handPreset = 0;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    auto seq = std::atomic_ref<uint64_t>(*reinterpret_cast<uint64_t *>(memory));
    auto previousSeq = seq.load();
    seq.store(previousSeq | 1);
    CallPose(&host, 0, Pose(5));
    CallUpdateBool(&input, trigger, false);
    Check(host.output[0].vecPosition[0] == 0 && input.values[trigger] == 1,
          "bounded IPC retry uses last complete heartbeat while writer is briefly odd");
    seq.store(previousSeq);
    router.SetRole(1, sw::DeviceRole::Other, 101);
    hooks.Tick(sw::QpcNow());
    Check(input.values[axis] == 0 && input.values[menu] == 0,
          "dynamic loss of selected hand role neutralizes previously owned actions");
    CallUpdateScalar(&leftInput, axis, .4f);
    Check(input.values[axis] == .4f,
          "unselected device input resumes unchanged after owned-action release");
    router.SetRole(1, sw::DeviceRole::Left, 101);
    hooks.Tick(sw::QpcNow());
    request.armed = 0;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    CallPose(&host, 0, Pose(6));
    Check(host.output[0].vecPosition[0] == 0, "disarmed desktop retains synthetic head");
    CallUpdateBool(&input, trigger, true);
    Check(input.values[trigger] == 0 && input.values[axis] == 0,
          "focus disarm releases all owned inputs");
    menuStatus = hooks.StatusNow(sw::QpcNow());
    Check(!menuStatus.inputArmed && !menuStatus.effectiveNativeActions && !menuStatus.menuPressed,
          "disarmed diagnostic neutralizes actions while desktop pose remains selected");
    request.requestedMode = 0;
    ++request.epoch;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    hooks.Tick(request.timestamp);
    CallPose(&host, 0, Pose(9));
    Check(host.output[0].vecPosition[0] == 9,
          "return physical takes fresh original, no height offset");
    CallUpdateBool(&input, trigger, true);
    Check(input.values[trigger] == 0, "held physical trigger must release before reacquiring");
    CallUpdateBool(&input, trigger, false);
    CallUpdateBool(&input, trigger, true);
    Check(input.values[trigger] == 1, "released physical trigger reacquires cleanly");
    for (int cycle = 0; cycle < 100; ++cycle)
    {
        request.requestedMode = 1;
        request.armed = 1;
        ++request.epoch;
        request.timestamp = sw::QpcNow();
        sw::WriteBlock(memory, request);
        CallPose(&host, 0, Pose(cycle));
        hooks.Tick(request.timestamp);
        request.requestedMode = 0;
        ++request.epoch;
        request.timestamp = sw::QpcNow();
        sw::WriteBlock(memory, request);
        hooks.Tick(request.timestamp);
        CallPose(&host, 0, Pose(cycle + .5));
        Check(host.output[0].vecPosition[0] == cycle + .5,
              "100 real-hook cycles preserve physical output");
    }
    request.requestedMode = 1;
    request.armed = 1;
    ++request.epoch;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    CallPose(&host, 0, Pose());
    hooks.Tick(request.timestamp);
    Sleep(210);
    CallPose(&host, 0, Pose(11));
    CallUpdateBool(&input, trigger, true);
    hooks.Tick(sw::QpcNow());
    Check(host.output[0].vecPosition[0] == 11 && input.values[trigger] == 0,
          "broker loss falls back within 200ms and no stuck actions");
    sourceStatus = hooks.StatusNow(sw::QpcNow());
    Check(sourceStatus.proximityKnown && !sourceStatus.proximityActive &&
              sourceStatus.proximityAgeMilliseconds >= sw::WatchdogMilliseconds,
          "change-only proximity retains observed state and exposes old event age honestly");
    Check(sourceStatus.leftPhysicalAgeMilliseconds >= sw::WatchdogMilliseconds &&
              sourceStatus.rightPhysicalAgeMilliseconds >= sw::WatchdogMilliseconds,
          "stale controller captures do not falsely report a fresh controller age");
    router.SetRole(0, sw::DeviceRole::Other, 100);
    router.SetRole(1, sw::DeviceRole::Other, 101);
    router.SetRole(2, sw::DeviceRole::Other, 102);
    sourceStatus = hooks.StatusNow(sw::QpcNow());
    Check(!sourceStatus.hasHead && !sourceStatus.hasLeft && !sourceStatus.hasRight &&
              !sourceStatus.proximityKnown && !(sourceStatus.inputCoverage & sw::HeadProximityBoolean),
          "excluded owned synthetic roles cannot establish head/controller/proximity readiness");
    router.SetRole(0, sw::DeviceRole::Head, 100);
    router.SetRole(1, sw::DeviceRole::Left, 101);
    router.SetRole(2, sw::DeviceRole::Right, 102);
    uint64_t recreated{};
    inputState.reuseHandle = proximity;
    CallCreateBool(&headInput, 100, "/input/proximity", &recreated);
    Check(recreated == proximity && !hooks.StatusNow(sw::QpcNow()).proximityKnown,
          "component recreation resets raw observation until a new vendor update");
    CallUpdateBool(&headInput, proximity, false);
    Check(hooks.StatusNow(sw::QpcNow()).proximityKnown,
          "same-identity component recreation can establish a new original observation");
    CallCreateBool(&headInput, 100, "/input/proximity/click", &recreated);
    CallUpdateBool(&headInput, proximity, true);
    Check(!hooks.StatusNow(sw::QpcNow()).proximityKnown,
          "a reused handle with conflicting proximity identity stays unknown");
    inputState.reuseHandle = 0;
    uint64_t secondProximity{};
    CallCreateBool(&headInput, 100, "/input/proximity/click", &secondProximity);
    CallUpdateBool(&headInput, secondProximity, true);
    Check(!hooks.StatusNow(sw::QpcNow()).proximityKnown,
          "multiple selected HMD proximity components are ambiguous rather than guessed");
    Check(!hostState.wrongOwner && !inputState.wrongOwner,
          "all routed ticks/callbacks/releases preserve distinct host and input provider ownership");
    Check(hooks.Remove(), "all owned hooks removed");
    CallPose(&host, 0, Pose(12));
    Check(host.output[0].vecPosition[0] == 12, "unhook restores original provider pose");
    CallUpdateBool(&input, trigger, true);
    Check(input.values[trigger] == 1, "unhook restores original provider input");
    // A touch-only left thumb button must not mask a real right menu click.
    sw::HookRuntime fallback(router, memory);
    Check(fallback.Install(&ownHost, &ownInput, false), "menu fallback fixture hooks installed");
    uint64_t leftTouchOnly{}, rightMenuClick{}, rightMenuTouch{}, leftMenuClick{};
    CallCreateBool(&leftInput, 101, "/input/y/touch", &leftTouchOnly);
    CallCreateBool(&input, 102, "/input/b/click", &rightMenuClick);
    CallCreateBool(&input, 102, "/input/b/touch", &rightMenuTouch);
    request.requestedMode = 1;
    request.armed = 1;
    request.actions = 4;
    request.timestamp = sw::QpcNow();
    ++request.epoch;
    sw::WriteBlock(memory, request);
    CallPose(&host, 0, Pose());
    fallback.Tick(request.timestamp);
    Check(input.values[rightMenuClick] == 1 && input.values[rightMenuTouch] == 0 &&
              input.values[leftTouchOnly] == 0,
          "touch-only left button does not mask right menu click fallback");
    auto fallbackStatus = fallback.StatusNow(sw::QpcNow());
    Check(fallbackStatus.inputCoverage == sw::SelectedMenuClick &&
              fallbackStatus.selectedMenuPath == uint32_t(sw::MenuPath::RightB) &&
              fallbackStatus.menuPressed && fallbackStatus.menuRisingEdges == 1,
          "coverage distinguishes right click fallback from touch and missing controls");
    CallCreateBool(&leftInput, 101, "/input/y/click", &leftMenuClick);
    fallback.Tick(sw::QpcNow());
    Check(input.values[leftMenuClick] == 1 && input.values[rightMenuClick] == 0,
          "late known left menu click becomes sole selected channel and releases fallback");
    Check(fallback.Remove(), "menu fallback fixture cleanly removes hooks");
    auto target = (*reinterpret_cast<void ***>(&host))[1];
    void *trampoline{};
    Check(MH_Initialize() == MH_OK &&
              MH_CreateHook(target, reinterpret_cast<void *>(ExternalPose), &trampoline) == MH_OK &&
              MH_EnableHook(target) == MH_OK,
          "isolated external-hook conflict prepared");
    sw::HookRuntime conflict(router, memory);
    Check(!conflict.Install(&ownHost, &ownInput, false),
          "existing third-party method detour fails closed");
    MH_DisableHook(target);
    MH_RemoveHook(target);
    MH_Uninitialize();
    sw::HookRuntime changed(router, memory);
    Check(changed.Install(&ownHost, &ownInput, false),
          "isolated post-install conflict fixture hooks installed");
    unsigned char ownPatch[5]{};
    std::memcpy(ownPatch, target, 5);
    DWORD protection{};
    VirtualProtect(target, 5, PAGE_EXECUTE_READWRITE, &protection);
    static_cast<unsigned char *>(target)[0] = 0xEB;
    FlushInstructionCache(GetCurrentProcess(), target, 5);
    DWORD ignored{};
    VirtualProtect(target, 5, protection, &ignored);
    Check(!changed.CheckIntegrity(), "post-install foreign patch detected");
    Check(!changed.Remove() && static_cast<unsigned char *>(target)[0] == 0xEB,
          "cleanup refuses to overwrite a foreign patch and pins its referenced state");
    VirtualProtect(target, 5, PAGE_EXECUTE_READWRITE, &protection);
    std::memcpy(target, ownPatch, 5);
    FlushInstructionCache(GetCurrentProcess(), target, 5);
    VirtualProtect(target, 5, protection, &ignored);
    Check(changed.Remove(), "isolated fixture can restore owned patch then remove safely");
    std::cout << "Isolated host/input hooking: " << checks << " checks, " << failures
              << " failures. No SteamVR process, app injection, runtime registration, or physical "
                 "hardware involved.\n";
    return failures ? 1 : 0;
}
