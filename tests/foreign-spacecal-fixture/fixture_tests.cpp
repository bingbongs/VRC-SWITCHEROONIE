#include "fixture_api.hpp"
#include "fake_calls.hpp"
#include "hooks.hpp"
#include "ipc.hpp"
#include <array>
#include <cmath>
#include <iostream>
#include <thread>
#include <atomic>
#include <string>
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
struct OutputBarrier
{
    std::atomic<HANDLE> reached{}, resume{};
    std::atomic<uint64_t> selected{};
    void Pause(uint64_t index)
    {
        auto entered = reached.load(std::memory_order_acquire);
        auto release = resume.load(std::memory_order_acquire);
        if (entered && release && index == selected.load(std::memory_order_relaxed))
        {
            SetEvent(entered);
            WaitForSingleObject(release, 5000);
        }
    }
    void Set(HANDLE entered, HANDLE release, uint64_t index)
    {
        selected.store(index, std::memory_order_relaxed);
        resume.store(release, std::memory_order_release);
        reached.store(entered, std::memory_order_release);
    }
};
struct Host final : vr::IVRServerDriverHost
{
    std::array<vr::DriverPose_t, 64> output{};
    std::atomic<uint64_t> calls{};
    OutputBarrier barrier;
    bool TrackedDeviceAdded(const char *, vr::ETrackedDeviceClass,
                            vr::ITrackedDeviceServerDriver *) override
    {
        return false;
    }
    __declspec(noinline) void TrackedDevicePoseUpdated(uint32_t index, const vr::DriverPose_t &p,
                                                       uint32_t size) override
    {
        calls.fetch_add(1, std::memory_order_relaxed);
        barrier.Pause(index);
        if (size == sizeof(p) && index < output.size())
            output[index] = p;
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
struct Input final : vr::IVRDriverInput
{
    std::atomic<uint64_t> calls{};
    OutputBarrier barrier;
    uint64_t next = 1;
    std::array<float, 256> values{};
    __declspec(noinline) vr::EVRInputError CreateBooleanComponent(uint64_t, const char *,
                                                                  uint64_t *h) override
    {
        calls = calls + 3;
        *h = next++;
        return vr::VRInputError_None;
    }
    __declspec(noinline) vr::EVRInputError UpdateBooleanComponent(uint64_t h, bool v,
                                                                  double) override
    {
        calls = calls + 5;
        barrier.Pause(h);
        if (h < values.size())
            values[h] = v ? 1.f : 0.f;
        return vr::VRInputError_None;
    }
    __declspec(noinline) vr::EVRInputError CreateScalarComponent(uint64_t, const char *,
                                                                 uint64_t *h, vr::EVRScalarType,
                                                                 vr::EVRScalarUnits) override
    {
        calls = calls + 7;
        *h = next++;
        return vr::VRInputError_None;
    }
    __declspec(noinline) vr::EVRInputError UpdateScalarComponent(uint64_t h, float v,
                                                                 double) override
    {
        calls = calls + 11;
        if (h < values.size())
            values[h] = v;
        return vr::VRInputError_None;
    }
    vr::EVRInputError CreateHapticComponent(uint64_t, const char *, uint64_t *h) override
    {
        *h = next++;
        return vr::VRInputError_None;
    }
    __declspec(noinline) vr::EVRInputError CreateSkeletonComponent(uint64_t, const char *,
                                                                   const char *, const char *,
                                                                   vr::EVRSkeletalTrackingLevel,
                                                                   const vr::VRBoneTransform_t *,
                                                                   uint32_t, uint64_t *h) override
    {
        calls = calls + 13;
        *h = next++;
        return vr::VRInputError_None;
    }
    __declspec(noinline) vr::EVRInputError UpdateSkeletonComponent(uint64_t h,
                                                                   vr::EVRSkeletalMotionRange,
                                                                   const vr::VRBoneTransform_t *,
                                                                   uint32_t) override
    {
        calls = calls + 17;
        if (h < values.size())
            values[h] = 17;
        return vr::VRInputError_None;
    }
    __declspec(noinline) vr::EVRInputError CreatePoseComponent(uint64_t, const char *,
                                                               uint64_t *h) override
    {
        calls = calls + 19;
        *h = next++;
        return vr::VRInputError_None;
    }
    __declspec(noinline) vr::EVRInputError UpdatePoseComponent(uint64_t h,
                                                               const vr::HmdMatrix34_t *,
                                                               double) override
    {
        calls = calls + 23;
        if (h < values.size())
            values[h] = 23;
        return vr::VRInputError_None;
    }
    vr::EVRInputError CreateEyeTrackingComponent(uint64_t, const char *, uint64_t *h) override
    {
        *h = next++;
        return vr::VRInputError_None;
    }
    vr::EVRInputError UpdateEyeTrackingComponent(uint64_t, const vr::VREyeTrackingData_t *,
                                                 double) override
    {
        return vr::VRInputError_None;
    }
};
}

int main(int argc, char **argv)
{
    if (argc < 2 || argc > 3)
    {
        std::cerr << "Pass the isolated fixture DLL path and optional raw/tick/submit/input scenario. No SteamVR is used.\n";
        return 1;
    }
    const std::string scenario = argc == 3 ? argv[2] : "raw";
    if (scenario != "raw" && scenario != "tick" && scenario != "submit" && scenario != "input")
    {
        std::cerr << "Unknown cleanup scenario.\n";
        return 1;
    }
    auto module = LoadLibraryA(argv[1]);
    if (!module)
    {
        std::cerr << "Could not load fixture DLL, Win32 " << GetLastError() << '\n';
        return 1;
    }
    auto install = reinterpret_cast<FixtureInstallFn>(GetProcAddress(module, "FixtureInstall"));
    auto raw = reinterpret_cast<FixtureReadPoseFn>(GetProcAddress(module, "FixtureReadRaw"));
    auto calibrated = reinterpret_cast<FixtureReadPoseFn>(GetProcAddress(module, "FixtureReadCalibrated"));
    auto barrier = reinterpret_cast<FixtureSetBarrierFn>(GetProcAddress(module, "FixtureSetBarrier"));
    auto original = reinterpret_cast<FixtureOriginalFn>(GetProcAddress(module, "FixtureOriginal"));
    auto rawCalls = reinterpret_cast<FixtureCountFn>(GetProcAddress(module, "FixtureRawCalls"));
    auto cleanupCalls = reinterpret_cast<FixtureCountFn>(GetProcAddress(module, "FixtureCleanupCalls"));
    auto inFlight = reinterpret_cast<FixtureCountFn>(GetProcAddress(module, "FixtureInFlight"));
    auto factory = reinterpret_cast<FixtureFactoryFn>(GetProcAddress(module, "HmdDriverFactory"));
    if (!install || !raw || !calibrated || !barrier || !original || !rawCalls ||
        !cleanupCalls || !inFlight || !factory)
    {
        std::cerr << "Fixture exports missing.\n";
        FreeLibrary(module);
        return 1;
    }
    int error{};
    auto provider = static_cast<vr::IServerTrackedDeviceProvider *>(factory(vr::IServerTrackedDeviceProvider_Version, &error));
    if (!provider || error)
        return 1;
    auto cleanupTarget = (*reinterpret_cast<void ***>(provider))[1];
    Host host;
    Input input;
    auto target = (*reinterpret_cast<void ***>(&host))[1];
    std::array<unsigned char, 5> unpatched{}, outerPatch{};
    std::memcpy(unpatched.data(), target, unpatched.size());
    if (!install(target))
    {
        std::cerr << "Foreign DLL MinHook installation failed.\n";
        return 1;
    }
    std::memcpy(outerPatch.data(), target, outerPatch.size());
    void *inner{};
    Check(sw::ResolveOriginalPoseTrampoline(target, module, inner),
          "bounded HDE resolver identifies global original pointer in a real foreign detour");
    Check(inner && inner == original(), "resolved trampoline equals independently exported ground truth");
    if (!inner)
    {
        provider->Cleanup();
        FreeLibrary(module);
        return 1;
    }
    alignas(8) std::byte memory[512]{};
    sw::Request request{};
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    sw::Router router;
    router.SetRole(0, sw::DeviceRole::Head, 100);
    router.SetRole(1, sw::DeviceRole::Left, 101);
    router.SetRole(2, sw::DeviceRole::Right, 102);
    router.SetRole(3, sw::DeviceRole::Other, 103);
    sw::HookRuntime hooks(router, memory);
    // Test-only approval bypasses binary provenance, never the trampoline resolver.
    sw::PoseChainApproval approval{module, cleanupTarget};
    if (!hooks.Install(&host, &input, false, &approval))
    {
        std::cerr << "Could not install isolated inner chain: " << hooks.failure() << '\n';
        provider->Cleanup();
        FreeLibrary(module);
        return 1;
    }
    Check(hooks.ChainedPose() && hooks.CheckIntegrity(), "inner hooks and foreign lifetime guards installed");
    Check(std::memcmp(target, outerPatch.data(), outerPatch.size()) == 0,
          "runtime entry remains owned by the foreign calibrator");
    CallPose(&host, 0, Pose());
    CallPose(&host, 1, Pose(-.2));
    CallPose(&host, 2, Pose(.2));
    CallPose(&host, 3, Pose(7));
    vr::DriverPose_t rawPose{}, calibratedPose{};
    Check(raw(0, &rawPose) && rawPose.vecPosition[0] == 0 && rawPose.vecWorldFromDriverTranslation[0] == 0,
          "foreign calibration feed captures raw physical input before transform");
    Check(host.output[0].vecWorldFromDriverTranslation[0] == 10,
          "physical passthrough retains foreign world transform");
    uint64_t trigger{}, axis{}, menu{};
    CallCreateBool(&input, 102, "/input/trigger/click", &trigger);
    CallCreateScalar(&input, 101, "/input/joystick/y", &axis);
    CallCreateBool(&input, 101, "/input/y/click", &menu);
    request.requestedMode = 1;
    request.armed = 1;
    request.epoch = 1;
    request.height = -.4;
    request.forward = .6;
    request.actions = 1 | 4;
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    auto feedBeforeTick = rawCalls();
    hooks.Tick(request.timestamp);
    Check(rawCalls() == feedBeforeTick, "generated Tick output bypasses calibration raw feed");
    CallPose(&host, 0, Pose(4));
    sw::PoseSnapshot captured{};
    Check(raw(0, &rawPose) && rawPose.vecPosition[0] == 4 && rawPose.vecWorldFromDriverTranslation[0] == 0,
          "moving physical head reaches foreign raw feed while desktop output remains synthetic");
    Check(calibrated(0, &calibratedPose) && calibratedPose.vecPosition[0] == 4 && calibratedPose.vecWorldFromDriverTranslation[0] == 10,
          "foreign transform occurs before owned capture");
    Check(router.Physical(0, captured) && captured.pose.vecPosition[0] == 4 && captured.pose.vecWorldFromDriverTranslation[0] == 10,
          "owned physical store captures calibrated physical head, independent of output");
    auto status = hooks.StatusNow(sw::QpcNow());
    Check(std::abs(status.position[0] - 14) < 1e-9,
          "captured physical world position reflects calibration once");
    Check(std::abs(host.output[0].vecPosition[0] - 10) < 1e-9 && host.output[0].vecWorldFromDriverTranslation[0] == 0 &&
              std::abs(host.output[0].vecPosition[1] - 1.3) < 1e-9,
          "desktop anchor has one calibration transform and one height offset");
    CallPose(&host, 3, Pose(8));
    Check(host.output[3].vecPosition[0] == 8 && host.output[3].vecWorldFromDriverTranslation[0] == 10,
          "generic tracker remains in calibrated physical pipeline");
    CallUpdateBool(&input, trigger, false);
    CallUpdateScalar(&input, axis, -.9f);
    Check(input.values[trigger] == 1 && std::abs(input.values[axis] - .6f) < 1e-6 && input.values[menu] == 1,
          "independent owned input hooks synthesize desktop controls");
    for (int cycle = 0; cycle < 25; ++cycle)
    {
        request.requestedMode = 0;
        ++request.epoch;
        request.timestamp = sw::QpcNow();
        sw::WriteBlock(memory, request);
        hooks.Tick(request.timestamp);
        CallPose(&host, 0, Pose(cycle + .5));
        Check(host.output[0].vecPosition[0] == cycle + .5 && host.output[0].vecWorldFromDriverTranslation[0] == 10,
              "physical return preserves one foreign transform across 25 cycles");
        request.requestedMode = 1;
        ++request.epoch;
        request.timestamp = sw::QpcNow();
        sw::WriteBlock(memory, request);
        hooks.Tick(request.timestamp);
        CallPose(&host, 0, Pose(cycle + 2));
        Check(raw(0, &rawPose) && rawPose.vecPosition[0] == cycle + 2 && rawPose.vecWorldFromDriverTranslation[0] == 0,
              "foreign raw feed stays physical across 25 desktop cycles");
    }
    Check(hooks.Remove(), "owned hooks removed before foreign hook");
    Check(std::memcmp(target, outerPatch.data(), outerPatch.size()) == 0,
          "owned removal leaves foreign runtime entry intact");
    CallPose(&host, 0, Pose(20));
    Check(host.output[0].vecPosition[0] == 20 && host.output[0].vecWorldFromDriverTranslation[0] == 10,
          "foreign raw capture and calibration continue after owned removal");
    sw::HookRuntime draining(router, memory);
    if (!draining.Install(&host, &input, false, &approval))
    {
        std::cerr << "Reinstall before cleanup test failed: " << draining.failure() << '\n';
        provider->Cleanup();
        FreeLibrary(module);
        return 1;
    }
    // Re-register component handles with this new hook instance. The fake device
    // creation simulates startup, without assuming old per-instance bookkeeping.
    CallCreateBool(&input, 102, "/input/trigger/click", &trigger);
    CallCreateScalar(&input, 101, "/input/joystick/y", &axis);
    CallCreateBool(&input, 101, "/input/y/click", &menu);
    request.timestamp = sw::QpcNow();
    sw::WriteBlock(memory, request);
    CallPose(&host, 0, Pose(21));
    draining.Tick(sw::QpcNow());
    HANDLE reached = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE resume = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!reached || !resume)
        return 1;
    if (scenario == "raw")
        barrier(reached, resume);
    else if (scenario == "input")
        input.barrier.Set(reached, resume, trigger);
    else
        host.barrier.Set(reached, resume, 0);
    std::thread poseThread([&] {
        if (scenario == "raw")
            CallPose(&host, 0, Pose(21));
        else if (scenario == "submit")
            draining.Submit(0, Pose(21));
        else
            draining.Tick(sw::QpcNow());
    });
    const bool paused = WaitForSingleObject(reached, 2000) == WAIT_OBJECT_0;
    Check(paused && (scenario == "raw" ? inFlight() == 1 : inFlight() == 0),
          "selected raw/Tick/Submit/input operation is paused inside its real forwarding path");
    std::atomic<bool> cleanupStarted{}, cleanupFinished{};
    std::thread cleanupThread([&] {
        cleanupStarted.store(true, std::memory_order_release);
        provider->Cleanup();
        cleanupFinished.store(true, std::memory_order_release);
    });
    while (!cleanupStarted.load(std::memory_order_acquire))
        Sleep(1);
    const auto cleanupWaitStart = GetTickCount64();
    while (draining.CheckIntegrity() && GetTickCount64() - cleanupWaitStart < 1000)
        Sleep(1);
    Sleep(60);
    Check(!cleanupFinished.load(std::memory_order_acquire) && cleanupCalls() == 0,
          "foreign Cleanup waits for pending raw/Tick/Submit/input forwarding before freeing trampolines");
    const auto feedWhileStopping = rawCalls();
    const auto hostCallsWhileStopping = host.calls.load();
    const auto inputCallsWhileStopping = input.calls.load();
    std::atomic<bool> newPoseFinished{};
    std::thread newPoseThread([&] {
        CallPose(&host, 0, Pose(99));
        newPoseFinished.store(true, std::memory_order_release);
    });
    const auto newPoseWaitStart = GetTickCount64();
    while (!newPoseFinished.load(std::memory_order_acquire) && GetTickCount64() - newPoseWaitStart < 1000)
        Sleep(1);
    Check(newPoseFinished.load(std::memory_order_acquire) && rawCalls() == feedWhileStopping,
          "stopping lifetime guard rejects new foreign calls while draining an earlier callback");
    std::atomic<bool> newOutputFinished{};
    std::thread newOutputThread([&] {
        draining.Tick(sw::QpcNow());
        draining.Submit(0, Pose(100));
        newOutputFinished.store(true, std::memory_order_release);
    });
    const auto newOutputWaitStart = GetTickCount64();
    while (!newOutputFinished.load(std::memory_order_acquire) && GetTickCount64() - newOutputWaitStart < 1000)
        Sleep(1);
    Check(newOutputFinished.load(std::memory_order_acquire) &&
              host.calls.load() == hostCallsWhileStopping && input.calls.load() == inputCallsWhileStopping,
          "new public Tick and Submit refuse all output while cleanup drains an earlier operation");
    SetEvent(resume);
    poseThread.join();
    newPoseThread.join();
    newOutputThread.join();
    cleanupThread.join();
    barrier(nullptr, nullptr);
    host.barrier.Set(nullptr, nullptr, 0);
    input.barrier.Set(nullptr, nullptr, 0);
    Check(cleanupFinished.load() && cleanupCalls() == 1 && inFlight() == 0,
          "cleanup drains complete foreign call before forwarding provider Cleanup exactly once");
    Check(std::memcmp(target, unpatched.data(), unpatched.size()) == 0 && original() == nullptr,
          "foreign cleanup restores runtime entry and frees foreign trampoline safely");
    Check(!draining.CheckIntegrity() && !draining.Remove(),
          "cleanup observation disables routing and intentionally retains the stopping lifetime guard");
    const auto hostCallsAfterCleanup = host.calls.load();
    const auto inputCallsAfterCleanup = input.calls.load();
    draining.Tick(sw::QpcNow());
    draining.Submit(0, Pose(101));
    Check(host.calls.load() == hostCallsAfterCleanup && input.calls.load() == inputCallsAfterCleanup,
          "public Tick and Submit do not call freed pose or input trampolines after cleanup");
    CallPose(&host, 0, Pose(22));
    Check(host.output[0].vecPosition[0] == 22 && host.output[0].vecWorldFromDriverTranslation[0] == 0,
          "runtime remains callable after both hook libraries clean up");
    CloseHandle(reached);
    CloseHandle(resume);
    // The guarded native cleanup deliberately pins relevant code/state until process exit.
    // This last scenario therefore ends the isolated process rather than reusing the runtime.
    std::cout << "Isolated foreign calibration chain (" << scenario << "): " << checks << " checks, " << failures
              << " failures. Separate DLL/MinHook ownership; no SteamVR, VRChat, registration, or hardware.\n";
    return failures ? 1 : 0;
}

