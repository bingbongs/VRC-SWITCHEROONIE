#include "config.hpp"
#include "config_loader.hpp"
#include "device_identity.hpp"
#include "core.hpp"
#include "pointer_model.hpp"
#include <cmath>
#include <iostream>
#include <limits>
#include <thread>
#include <vector>
#include <windows.h>
namespace
{
int failures = 0, checks = 0;
void Check(bool value, const char *name)
{
    ++checks;
    if (!value)
    {
        ++failures;
        std::cerr << "FAIL " << name << '\n';
    }
}
vr::DriverPose_t Pose(double x = 0, double y = 1.7, double z = 0)
{
    vr::DriverPose_t p{};
    p.qWorldFromDriverRotation = {1, 0, 0, 0};
    p.qDriverFromHeadRotation = {1, 0, 0, 0};
    p.qRotation = {1, 0, 0, 0};
    p.vecPosition[0] = x;
    p.vecPosition[1] = y;
    p.vecPosition[2] = z;
    p.poseIsValid = true;
    p.deviceIsConnected = true;
    p.result = vr::TrackingResult_Running_OK;
    return p;
}
struct TemporaryConfig
{
    std::filesystem::path path;
    TemporaryConfig()
    {
        wchar_t folder[MAX_PATH]{}, file[MAX_PATH]{};
        if (!GetTempPathW(MAX_PATH, folder) || !GetTempFileNameW(folder, L"swr", 0, file))
            throw std::runtime_error("Unable to create owned configuration fixture.");
        path = file;
    }
    ~TemporaryConfig() { DeleteFileW(path.c_str()); }
    void Write(std::string_view text)
    {
        HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, TRUNCATE_EXISTING,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            throw std::runtime_error("Unable to open owned configuration fixture.");
        DWORD written{};
        bool complete = WriteFile(file, text.data(), DWORD(text.size()), &written, nullptr) &&
                        written == text.size();
        CloseHandle(file);
        if (!complete) throw std::runtime_error("Unable to write configuration fixture.");
    }
};
void ConfigurationLoaderChecks()
{
    std::filesystem::path canonical, changed;
    uint32_t error{};
    Check(sw::CurrentUserConfigPath(canonical, error) && !error && canonical.is_absolute() &&
              canonical.filename() == L"driver.json" &&
              canonical.parent_path().filename() == L"config" &&
              canonical.parent_path().parent_path().filename() == L"VRC-SWITCHEROONIE",
          "canonical configuration resolves the account profile and single shared config layout");
    DWORD required = GetEnvironmentVariableW(L"LOCALAPPDATA", nullptr, 0);
    std::wstring original(required, L'\0');
    if (required) original.resize(GetEnvironmentVariableW(L"LOCALAPPDATA", original.data(), required));
    SetEnvironmentVariableW(L"LOCALAPPDATA", nullptr);
    Check(sw::CurrentUserConfigPath(changed, error) && changed == canonical && !error,
          "missing LOCALAPPDATA cannot redirect or remove canonical configuration resolution");
    SetEnvironmentVariableW(L"LOCALAPPDATA", L"Z:\\not-the-opt-in-account");
    Check(sw::CurrentUserConfigPath(changed, error) && changed == canonical && !error,
          "wrong LOCALAPPDATA cannot redirect canonical opt-in to another directory");
    SetEnvironmentVariableW(L"LOCALAPPDATA", required ? original.c_str() : nullptr);
    TemporaryConfig file;
    file.Write("\xEF\xBB\xBF{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\"}");
    auto loaded = sw::LoadConfigFile(file.path);
    Check(loaded.ready() && loaded.config.experimentalOptIn &&
              loaded.config.approvedRuntimeBuild == "25330290" && loaded.bytes == 63 &&
              !loaded.systemError,
          "bounded Win32 reader loads and validates UTF8 BOM activation file");
    HANDLE locked = CreateFileW(file.path.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING,
                                FILE_ATTRIBUTE_NORMAL, nullptr);
    loaded = sw::LoadConfigFile(file.path);
    Check(locked != INVALID_HANDLE_VALUE && loaded.stage == sw::ConfigLoadStage::Open &&
              loaded.systemError == ERROR_SHARING_VIOLATION && !loaded.ready(),
          "inaccessible configuration returns exact open error and never enables hooks");
    if (locked != INVALID_HANDLE_VALUE) CloseHandle(locked);
    locked = CreateFileW(file.path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE,
                         nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    loaded = sw::LoadConfigFile(file.path);
    Check(locked != INVALID_HANDLE_VALUE && loaded.stage == sw::ConfigLoadStage::Open &&
              loaded.systemError == ERROR_SHARING_VIOLATION && !loaded.ready(),
          "an in-place writer cannot share a configuration read even for same-length edits");
    if (locked != INVALID_HANDLE_VALUE) CloseHandle(locked);
    loaded = sw::LoadConfigFile(file.path.wstring() + L".missing");
    Check(loaded.stage == sw::ConfigLoadStage::Open &&
              loaded.systemError == ERROR_FILE_NOT_FOUND && !loaded.ready(),
          "absent file is startup I/O failure, separate from malformed schema");
    loaded = sw::LoadConfigFile(L"\\\\.\\NUL");
    Check(loaded.stage == sw::ConfigLoadStage::Read &&
              loaded.systemError == ERROR_INVALID_FUNCTION && !loaded.ready(),
          "non-file read metadata reports bounded startup read failure, never schema success");
    file.Write(std::string(sw::MaxConfigBytes + 1, ' '));
    loaded = sw::LoadConfigFile(file.path);
    Check(loaded.stage == sw::ConfigLoadStage::TooLarge &&
              loaded.systemError == ERROR_FILE_TOO_LARGE && !loaded.ready() && !loaded.bytes,
          "configuration read is bounded and rejects files over4096bytes");
    file.Write("{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\",\"unknown\":1}");
    loaded = sw::LoadConfigFile(file.path);
    Check(loaded.stage == sw::ConfigLoadStage::Schema && !loaded.systemError &&
              loaded.bytes && !loaded.ready(),
          "readable malformed schema remains a distinct strict-parser failure");
    file.Write("");
    loaded = sw::LoadConfigFile(file.path);
    Check(loaded.stage == sw::ConfigLoadStage::Schema && !loaded.bytes && !loaded.ready(),
          "empty readable opt-in never supplies implicit activation defaults");
    Check(uint32_t(sw::Error::StartupConfigFailure) == 10 &&
              uint32_t(sw::Error::InvalidRequest) == 4 &&
              uint32_t(sw::Error::UnmappedControllers) == 9,
          "startup error is additive and retains all previous wire error values");
}
struct IdentityProperties final : vr::IVRProperties
{
    std::string system{"oculus"}, serial{"vendor-hmd-a"}, model{"Oculus Quest2"},
                renderModel{"oculus_quest2_controller_right"};
    bool missing{}, wrongType{}, unterminated{};
    uint32_t reads{}, writes{};
    vr::ETrackedPropertyError ReadPropertyBatch(uint64_t, vr::PropertyRead_t *batch,
                                                uint32_t count) override
    {
        for (uint32_t i = 0; i < count; ++i)
        {
            ++reads;
            auto &property = batch[i];
            const std::string *value = property.prop == vr::Prop_TrackingSystemName_String ? &system :
                                       property.prop == vr::Prop_SerialNumber_String ? &serial :
                                       property.prop == vr::Prop_ModelNumber_String ? &model :
                                       property.prop == vr::Prop_RenderModelName_String ? &renderModel : nullptr;
            property.unTag = wrongType ? vr::k_unBoolPropertyTag : vr::k_unStringPropertyTag;
            property.unRequiredBufferSize = value ? uint32_t(value->size() + 1) : 0;
            if (missing || !value) property.eError = vr::TrackedProp_UnknownProperty;
            else if (property.unBufferSize < property.unRequiredBufferSize)
                property.eError = vr::TrackedProp_BufferTooSmall;
            else
            {
                property.eError = vr::TrackedProp_Success;
                std::memcpy(property.pvBuffer, value->c_str(), value->size() + 1);
                if (unterminated)
                    static_cast<char *>(property.pvBuffer)[value->size()] = 'x';
            }
        }
        return vr::TrackedProp_Success;
    }
    vr::ETrackedPropertyError WritePropertyBatch(uint64_t, vr::PropertyWrite_t *, uint32_t) override
    { ++writes; return vr::TrackedProp_InvalidOperation; }
    const char *GetPropErrorNameFromEnum(vr::ETrackedPropertyError) override { return "fixture"; }
    uint64_t TrackedDeviceToPropertyContainer(uint32_t index) override { return 100 + index; }
};
void IdentityPropertyChecks()
{
    IdentityProperties fixture;
    vr::CVRPropertyHelpers properties(&fixture);
    Check(sw::EligiblePhysicalRoleIdentity(properties, 100) && fixture.reads == 3,
          "production property reader admits complete vendor identity via real OpenVR helper");
    fixture.system = "switcheroonie_persistent_synthetic";
    Check(!sw::EligiblePhysicalRoleIdentity(properties, 100),
          "production property reader excludes owned synthetic tracking-system");
    fixture.system = "oculus"; fixture.serial = "switcheroonie-persistent-research-v1";
    Check(!sw::EligiblePhysicalRoleIdentity(properties, 100),
          "production property reader preserves exclusion when a shim rewrites tracking-system");
    fixture.serial = "vendor-hmd-a"; fixture.model = "Switcheroonie Persistent Research HMD";
    Check(!sw::EligiblePhysicalRoleIdentity(properties, 100),
          "production property reader preserves exclusion through owned model prefix");
    fixture.model = "Oculus Quest2"; fixture.missing = true;
    Check(!sw::EligiblePhysicalRoleIdentity(properties, 100),
          "missing identity properties fail closed without invented source identity");
    fixture.missing = false; fixture.serial.assign(600, 's');
    Check(!sw::EligiblePhysicalRoleIdentity(properties, 100),
          "bounded property buffer rejects truncated identity instead of matching a prefix fragment");
    fixture.serial = "vendor-hmd-a"; fixture.unterminated = true;
    Check(!sw::EligiblePhysicalRoleIdentity(properties, 100),
          "malformed unterminated identity cannot establish physical source eligibility");
    fixture.unterminated = false; fixture.wrongType = true;
    Check(!sw::EligiblePhysicalRoleIdentity(properties, 100) && !fixture.writes,
          "wrong property type is rejected and source identification writes no properties");
}
bool Near(double a, double b) { return std::abs(a - b) < 1e-9; }
vr::HmdQuaternion_t Multiply(vr::HmdQuaternion_t a, vr::HmdQuaternion_t b)
{
    return {a.w*b.w-a.x*b.x-a.y*b.y-a.z*b.z, a.w*b.x+a.x*b.w+a.y*b.z-a.z*b.y,
            a.w*b.y-a.x*b.z+a.y*b.w+a.z*b.x, a.w*b.z+a.x*b.y-a.y*b.x+a.z*b.w};
}
bool SameRotation(vr::HmdQuaternion_t a, vr::HmdQuaternion_t b)
{
    // q and -q describe the same rotation.
    const auto dot = a.w*b.w+a.x*b.x+a.y*b.y+a.z*b.z;
    return std::abs(std::abs(dot)-1) < 1e-9;
}
bool SamePose(const vr::DriverPose_t &a, const vr::DriverPose_t &b)
{
    for (int i = 0; i < 3; ++i)
        if (!Near(a.vecPosition[i], b.vecPosition[i])) return false;
    return Near(a.qRotation.w, b.qRotation.w) && Near(a.qRotation.x, b.qRotation.x) &&
           Near(a.qRotation.y, b.qRotation.y) && Near(a.qRotation.z, b.qRotation.z);
}
void HandAndPostureChecks()
{
    constexpr int64_t now = 10000000, frequency = 1000000;
    sw::Router router;
    router.SetRole(0, sw::DeviceRole::Head, 100);
    router.SetRole(1, sw::DeviceRole::Left, 101);
    router.SetRole(2, sw::DeviceRole::Right, 102);
    router.SetRole(3, sw::DeviceRole::Other, 103);
    router.Capture(0, Pose(4, 1.7, 2), now);
    router.Capture(1, Pose(-.2, 1.2, -.3), now);
    router.Capture(2, Pose(.2, 1.2, -.3), now);
    router.Capture(3, Pose(7, 1.2, -.3), now);
    sw::Request request;
    request.timestamp = now;
    request.epoch = 1;
    request.requestedMode = request.armed = 1;
    request.height = .15;
    vr::DriverPose_t head{}, left{}, right{}, neutralLeft{}, neutralRight{};
    Check(router.RoutePose(0, request, true, now, frequency, head) &&
              router.RoutePose(1, request, true, now, frequency, neutralLeft) &&
              router.RoutePose(2, request, true, now, frequency, neutralRight),
          "resting pose starts from one committed physical head anchor");
    Check(Near(head.vecPosition[1], 1.85) && Near(request.height, .15),
          "configured positive desktop height remains standing baseline");
    Check(Near(neutralLeft.vecPosition[0], 3.8) && Near(neutralRight.vecPosition[0], 4.2) &&
              Near(neutralLeft.vecPosition[1], 1.1) && Near(neutralRight.vecPosition[1], 1.1) &&
              Near(neutralLeft.vecPosition[2], 2.04) && Near(neutralRight.vecPosition[2], 2.04),
          "idle hands are symmetric near hips and not held forward at chest height");
    const auto &q = neutralRight.qRotation;
    Check(Near(q.w * q.w + q.x * q.x + q.y * q.y + q.z * q.z, 1) &&
              Near(q.w, 1) && Near(q.x, 0) && Near(q.y, 0) && Near(q.z, 0),
          "resting raw wrists stay straight and level without assuming a tip-component angle");
    request.pitch = 1.2; request.handPitch = 1.1; request.handYaw = .7;
    request.handPreset = 1;
    Check(router.RoutePose(1, request, true, now, frequency, left) &&
              router.RoutePose(2, request, true, now, frequency, right) &&
              SamePose(left, neutralLeft) && SamePose(right, neutralRight),
          "normal head-look preset and pitch do not raise or aim idle wrists");
    request.actions = sw::Menu;
    Check(router.RoutePose(1, request, true, now, frequency, left) &&
              router.RoutePose(2, request, true, now, frequency, right) &&
              SamePose(left, neutralLeft) && SamePose(right, neutralRight),
          "a menu click bit does not implicitly extend either resting arm");
    for (auto action : {sw::Trigger, sw::Grip})
    {
        request.actions = action;
        Check(router.RoutePose(1, request, true, now, frequency, left) &&
                  router.RoutePose(2, request, true, now, frequency, right) &&
                  SamePose(left, neutralLeft) && right.vecPosition[2] < head.vecPosition[2] - .5 &&
                  right.vecPosition[1] > head.vecPosition[1] - .2,
              "trigger or pickup extends only the active right interaction hand");
        request.actions = 0;
        Check(router.RoutePose(2, request, true, now, frequency, right) &&
                  SamePose(right, neutralRight),
              "interaction release restores the resting right hand without accumulated offset");
    }
    request.handPreset = 2;
    Check(router.RoutePose(1, request, true, now, frequency, left) &&
              router.RoutePose(2, request, true, now, frequency, right) &&
              head.vecPosition[2] - left.vecPosition[2] < .2 &&
              head.vecPosition[2] - right.vecPosition[2] > .4,
          "explicit pointer mode extends right hand while the menu wrist remains near the body");
    Check(std::abs(right.qRotation.y) > .1 && std::abs(right.qRotation.x) > .1 &&
              Near(left.qRotation.x, 0) && Near(left.qRotation.y, 0),
          "fine pointer aim moves right wrist without coupling the menu wrist to pointer or head pitch");
    request.armed = 0; request.actions = sw::Prone;
    Check(router.RoutePose(0, request, true, now, frequency, head) &&
              router.RoutePose(1, request, true, now, frequency, left) &&
              router.RoutePose(2, request, true, now, frequency, right) &&
              Near(head.vecPosition[1], 1.85) && SamePose(left, neutralLeft) &&
              SamePose(right, neutralRight),
          "focus or release disarm restores standing height and both rest hands despite stale preset/actions");
    request.armed = 1; request.handPreset = 0;
    request.pitch = request.handPitch = request.handYaw = 0;
    request.actions = sw::Crouch;
    Check(router.RoutePose(0, request, true, now, frequency, head) &&
              head.vecPosition[1] > .9 && head.vecPosition[1] < 1.2 &&
              Near(head.vecPosition[0], 4) && Near(head.vecPosition[2], 2),
          "held crouch lowers only vertical desktop pose without changing anchor or locomotion");
    auto crouchY = head.vecPosition[1];
    Check(router.RoutePose(1, request, true, now, frequency, left) &&
              left.vecPosition[1] >= .08 && left.vecPosition[1] < head.vecPosition[1] &&
              Near(left.vecPosition[2], neutralLeft.vecPosition[2]),
          "crouch rest hands move down with body while staying above conservative WORLD bound");
    request.actions = sw::Prone;
    Check(router.RoutePose(0, request, true, now, frequency, head) &&
              head.vecPosition[1] >= .15 && head.vecPosition[1] < crouchY - .4,
          "held prone is lower than crouch without crossing conservative WORLD head bound");
    auto proneY = head.vecPosition[1];
    Check(router.RoutePose(2, request, true, now, frequency, right) &&
              right.vecPosition[1] >= .08 && right.vecPosition[1] < proneY &&
              Near(right.vecPosition[2], neutralRight.vecPosition[2]),
          "prone rest hand remains near body and adapts above the conservative WORLD hand bound");
    request.actions = sw::Crouch | sw::Prone;
    Check(router.RoutePose(0, request, true, now, frequency, head) &&
              Near(head.vecPosition[1], proneY),
          "simultaneous posture holds deterministically choose prone");
    for (auto control : {sw::Control::Trigger, sw::Control::Grip, sw::Control::Menu,
                        sw::Control::Jump, sw::Control::Run, sw::Control::StickX,
                        sw::Control::StickY, sw::Control::Touch})
        for (auto role : {sw::DeviceRole::Left, sw::DeviceRole::Right})
            Check(sw::DesiredValue(control, role, request) == 0,
                  "posture bits never synthesize unrelated controller button, axis or gesture actions");
    for (int cycle = 0; cycle < 20; ++cycle)
    {
        request.actions = sw::Crouch;
        Check(router.RoutePose(0, request, true, now, frequency, head) &&
                  Near(head.vecPosition[1], crouchY), "repeated held crouch has no accumulating offset");
        request.actions = sw::Prone;
        Check(router.RoutePose(0, request, true, now, frequency, head) &&
                  Near(head.vecPosition[1], proneY), "repeated held prone has no accumulating offset");
        request.actions = 0;
        Check(router.RoutePose(0, request, true, now, frequency, head) &&
                  Near(head.vecPosition[1], 1.85) && Near(request.height, .15),
              "posture key release restores configured height without modifying the preference");
        Check(router.RoutePose(2, request, true, now, frequency, right) &&
                  SamePose(right, neutralRight), "posture release restores unchanged rest-hand pose");
    }
    sw::Error error{};
    request.actions = sw::AllowedActions;
    Check(sw::AllowedActions == 127 && sw::Router::ValidRequest(request, now, frequency, error),
          "unchanged action word accepts only the seven explicitly defined action bits");
    request.actions = 128;
    Check(!sw::Router::ValidRequest(request, now, frequency, error) &&
              error == sw::Error::InvalidRequest,
          "undefined future action bit still fails closed");
    request.actions = sw::Prone; request.requestedMode = 0; ++request.epoch;
    sw::PoseSnapshot physical{};
    Check(!router.RoutePose(0, request, true, now, frequency, head) &&
              !router.RoutePose(2, request, true, now, frequency, right) &&
              router.Physical(0, physical) && Near(physical.pose.vecPosition[1], 1.7),
          "physical selection bypasses all posture, configured desktop height and rest-hand adjustments");
    Check(!router.RoutePose(3, request, true, now, frequency, right),
          "posture never routes external tracker poses");
    for (auto y : {.16, .15, .10, -1.0})
    {
        sw::Router low;
        low.SetRole(0, sw::DeviceRole::Head, 100);
        low.SetRole(2, sw::DeviceRole::Right, 102);
        low.Capture(0, Pose(0, y, 0), now);
        low.Capture(2, Pose(), now);
        request.requestedMode = 1; request.height = 0;
        for (auto action : {sw::Crouch, sw::Prone})
        {
            request.actions = action;
            Check(low.RoutePose(0, request, true, now, frequency, head) &&
                      head.vecPosition[1] <= y && head.vecPosition[1] >= y - 1.20 &&
                      (y <= .15 || head.vecPosition[1] >= .15),
                  "low or negative world-origin posture never raises head and remains a bounded relative offset");
            Check(low.RoutePose(2, request, true, now, frequency, right) &&
                      right.vecPosition[1] < head.vecPosition[1] &&
                      right.vecPosition[1] >= head.vecPosition[1] - .75,
                  "low-origin hand stays below head without an invented upward floor correction");
        }
    }
}
void PointerModelChecks()
{
    // Mathematical reference only. Production uses VRChat's raw pose binding;
    // the measured tip transform is not applied to actual controller routing.
    Check(sw::ValidPointerTipRotation(sw::Quest2RightTipRotation),
          "publicly measured reference tip quaternion is a finite unit rotation");
    for (auto q : {vr::HmdQuaternion_t{}, vr::HmdQuaternion_t{2,0,0,0},
                  vr::HmdQuaternion_t{std::numeric_limits<double>::quiet_NaN(),0,0,0}})
        Check(!sw::ValidPointerTipRotation(q), "reference quaternion rejects malformed values");
    const auto tip = sw::Quest2RightTipRotation;
    const vr::HmdQuaternion_t inverse{tip.w,-tip.x,-tip.y,-tip.z};
    for (const auto &angles : {std::array<double,2>{0,0},{.6,.3},{-1.2,-.6},{3.1,1.2},{-3.1,-1.2}})
    {
        const auto desired = Multiply({std::cos(angles[0]/2),0,std::sin(angles[0]/2),0},
                                      {std::cos(angles[1]/2),std::sin(angles[1]/2),0,0});
        Check(SameRotation(Multiply(Multiply(desired,inverse),tip),desired),
              "reference local-tip inverse composition is mathematically correct without asserting application component choice");
    }
}
#include "body_controls_test_cases.hpp"
} // namespace
int main()
{
    HandAndPostureChecks();
    PointerModelChecks();
    BodyControlsChecks();
    ConfigurationLoaderChecks();
    IdentityPropertyChecks();
    Check(sw::EligiblePhysicalRoleIdentity(true, "oculus", "vendor-hmd-a", "Oculus Quest2"),
          "complete vendor HMD identity remains eligible for a captured physical role");
    Check(!sw::EligiblePhysicalRoleIdentity(true, "switcheroonie_persistent_synthetic",
                                           "vendor-renamed", "vendor-renamed"),
          "owned research tracking-system identity is excluded from head/controller source roles");
    Check(!sw::EligiblePhysicalRoleIdentity(true, "oculus",
                                           "switcheroonie-persistent-research-v1", "Oculus Quest2"),
          "immutable owned serial excludes research source even if tracking/model are rewritten");
    Check(!sw::EligiblePhysicalRoleIdentity(true, "oculus", "vendor-renamed",
                                           "Switcheroonie Persistent Research HMD"),
          "owned model prefix excludes research source if other identities are rewritten");
    Check(!sw::EligiblePhysicalRoleIdentity(true, "SWITCHEROONIE_PERSISTENT_SYNTHETIC",
                                           "vendor-renamed", "vendor-renamed") &&
              !sw::EligiblePhysicalRoleIdentity(true, "oculus", "SWITCHEROONIE-PERSISTENT-V2",
                                                "vendor-renamed"),
          "ASCII identity case changes cannot admit the owned synthetic display");
    Check(!sw::EligiblePhysicalRoleIdentity(false, "oculus", "vendor-hmd-a", "Oculus Quest2"),
          "missing or truncated identity properties cannot establish physical readiness");
    sw::Router syntheticIdentity;
    auto syntheticRole = sw::EligiblePhysicalRoleIdentity(
                             true, "switcheroonie_persistent_synthetic",
                             "switcheroonie-persistent-research-v1", "Switcheroonie Persistent Research HMD")
                             ? sw::DeviceRole::Head : sw::DeviceRole::Other;
    syntheticIdentity.SetRole(0, syntheticRole, 100);
    syntheticIdentity.SetRole(1, syntheticRole, 101);
    syntheticIdentity.SetRole(2, syntheticRole, 102);
    syntheticIdentity.Capture(0, Pose(), 10000000);
    syntheticIdentity.Capture(1, Pose(), 10000000);
    syntheticIdentity.Capture(2, Pose(), 10000000);
    sw::Request excludedRequest;
    excludedRequest.timestamp = 10000000;
    excludedRequest.requestedMode = 1;
    excludedRequest.epoch = 1;
    auto excludedStatus = syntheticIdentity.GetStatus(excludedRequest, true, 10000000, 1000000);
    Check(!excludedStatus.hasHead && !excludedStatus.hasLeft && !excludedStatus.hasRight &&
              !excludedStatus.proximityKnown &&
              excludedStatus.leftPhysicalAgeMilliseconds == -1 &&
              excludedStatus.rightPhysicalAgeMilliseconds == -1 && !excludedStatus.actualMode &&
              excludedStatus.error == uint32_t(sw::Error::NoHead),
          "fresh owned synthetic pose cannot establish physical head readiness or desktop commit");
    sw::DriverConfig config;
    Check(sw::ParseConfig("{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\"}",
                          config) &&
              config.experimentalOptIn,
          "strict valid driver config");
    Check(sw::ParseConfig(
              "\xEF\xBB\xBF{\"approvedRuntimeBuild\":\"25330290\",\"experimentalOptIn\":false}",
              config) &&
              !config.experimentalOptIn,
          "UTF8 BOM config tolerated");
    Check(!sw::ParseConfig("{\"experimentalOptIn\":false,\"experimentalOptIn\":true,"
                           "\"approvedRuntimeBuild\":\"25330290\"}",
                           config),
          "duplicate activation rejected");
    Check(
        !sw::ParseConfig(
            "{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\"} trailing", config),
        "trailing config rejected");
    Check(!sw::ParseConfig(
              "{/*bad*/\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\"}", config),
          "config comments rejected");
    Check(!sw::ParseConfig("{\"experimentalOptIn\":trueX,\"approvedRuntimeBuild\":\"25330290\"}",
                           config),
          "malformed boolean rejected");
    constexpr int64_t frequency = 1000000, now = 10000000;
    sw::Request request{};
    request.timestamp = now;
    request.epoch = 1;
    request.requestedMode = 1;
    request.armed = 1;
    request.height = -.5;
    sw::Router router;
    router.SetRole(0, sw::DeviceRole::Head, 100);
    router.SetRole(1, sw::DeviceRole::Left, 101);
    router.SetRole(2, sw::DeviceRole::Right, 102);
    router.SetRole(3, sw::DeviceRole::Other, 103);
    auto original = Pose();
    router.Capture(0, original, now);
    router.Capture(1, Pose(-.2, 1.2, -.3), now);
    router.Capture(2, Pose(.2, 1.2, -.3), now);
    router.Capture(3, Pose(4, 1, 2), now);
    vr::DriverPose_t synthetic{};
    Check(router.RoutePose(0, request, true, now, frequency, synthetic), "desktop head routes");
    Check(std::abs(synthetic.vecPosition[1] - 1.2) < 1e-9, "height offset applies desktop only");
    original = Pose(2, 2, 3);
    router.Capture(0, original, now + 1);
    Check(router.RoutePose(0, request, true, now + 1, frequency, synthetic) &&
              std::abs(synthetic.vecPosition[0]) < 1e-9,
          "moving physical source does not move held synthetic head");
    sw::PoseSnapshot captured{};
    Check(router.Physical(0, captured) && captured.pose.vecPosition[0] == 2,
          "independent original physical capture");
    request.yaw = .6;
    Check(router.RoutePose(0, request, true, now + 1, frequency, synthetic) &&
              std::abs(synthetic.qRotation.y) > .1,
          "synthetic yaw routes");
    Check(router.Physical(0, captured) && captured.pose.qRotation.y == 0,
          "synthetic rotation does not contaminate source");
    Check(!router.RoutePose(3, request, true, now + 1, frequency, synthetic),
          "tracker never overridden");
    Check(router.ContainerRole(103) == sw::DeviceRole::Other, "tracker inputs untouched");
    request.armed = 0;
    Check(router.RoutePose(0, request, true, now + 1, frequency, synthetic),
          "disarmed desktop retains pose");
    request.actions = 31;
    request.forward = 1;
    Check(sw::DesiredValue(sw::Control::Trigger, sw::DeviceRole::Right, request) == 0 &&
              sw::DesiredValue(sw::Control::StickY, sw::DeviceRole::Left, request) == 0,
          "disarmed desktop neutralizes input");
    request.armed = 1;
    Check(!router.Desktop(request, true, now + 201000, frequency), "broker timeout within 200ms");
    request.timestamp = now + 201000;
    Check(!router.Desktop(request, true, now + 201000, frequency),
          "stale physical source refuses desktop");
    request.timestamp = now;
    request.height = std::numeric_limits<double>::quiet_NaN();
    sw::Error err{};
    Check(!sw::Router::ValidRequest(request, now, frequency, err), "NaN rejected");
    request.height = 0;
    request.timestamp = now + 26000;
    Check(!sw::Router::ValidRequest(request, now, frequency, err), "future timestamp rejected");
    request.timestamp = now;
    for (int cycle = 0; cycle < 100; ++cycle)
    {
        request.requestedMode = 0;
        ++request.epoch;
        Check(!router.Desktop(request, true, now, frequency), "physical selection");
        auto p = Pose(double(cycle), 1.7, 0);
        router.Capture(0, p, now);
        request.requestedMode = 1;
        ++request.epoch;
        Check(router.RoutePose(0, request, true, now, frequency, synthetic) &&
                  synthetic.vecPosition[0] == cycle,
              "100 switches capture latest physical anchor");
        request.requestedMode = 0;
        ++request.epoch;
        Check(!router.RoutePose(0, request, true, now, frequency, synthetic),
              "physical return bypasses synthetic");
        Check(router.Physical(0, captured) && captured.pose.vecPosition[0] == cycle,
              "physical return uses fresh source");
    }
    request.requestedMode = 1;
    ++request.epoch;
    request.actions = 0;
    router.Capture(0, Pose(), now);
    Check(router.Desktop(request, true, now, frequency),
          "desktop committed before concurrency test");
    std::atomic<int> leaked{};
    std::vector<std::thread> threads;
    threads.emplace_back([&] {
        for (int i = 0; i < 50000; ++i)
            router.Capture(0, Pose(double(i % 1000)), now);
    });
    for (int i = 0; i < 8; ++i)
        threads.emplace_back([&, i] {
            for (int j = 0; j < 10000; ++j)
            {
                vr::DriverPose_t p{};
                if (i % 3 == 0)
                {
                    if (!router.Desktop(request, true, now, frequency))
                        ++leaked;
                }
                else if (i % 3 == 1)
                {
                    if (!router.RoutePose(0, request, true, now, frequency, p))
                        ++leaked;
                }
                else if (router.GetStatus(request, true, now, frequency).actualMode != 1)
                    ++leaked;
            }
        });
    for (auto &t : threads)
        t.join();
    Check(leaked == 0, "concurrent physical capture/input/status/pose callbacks never leak "
                       "physical after desktop commit");
    sw::Router racingClock;
    racingClock.SetRole(0, sw::DeviceRole::Head, 100);
    racingClock.Capture(0, Pose(10, 1.7, 0), now);
    Check(racingClock.RoutePose(0, request, true, now, frequency, synthetic),
          "advancing-clock test commits initial desktop anchor");
    racingClock.Capture(0, Pose(20, 1.7, 0), now + 1);
    Check(racingClock.Desktop(request, true, now, frequency),
          "concurrent head publication newer than caller clock does not revoke desktop");
    Check(racingClock.RoutePose(0, request, true, now + 2, frequency, synthetic) &&
              synthetic.vecPosition[0] == 10,
          "same-epoch head publication race does not recapture moved physical anchor");
    auto clockStatus = racingClock.GetStatus(request, true, now, frequency);
    Check(clockStatus.actualMode == 1 && clockStatus.hasHead &&
              clockStatus.headAgeMilliseconds == 0 && clockStatus.anchorEpoch == request.epoch,
          "future publication tolerance also applies to status validity and clamped age");
    racingClock.Capture(0, Pose(21), now + 26000);
    Check(!racingClock.Desktop(request, true, now, frequency),
          "head timestamps genuinely beyond future tolerance fail closed");
    Check(racingClock.RoutePose(0, request, true, now + 26000, frequency, synthetic) &&
              synthetic.vecPosition[0] == 10,
          "same-epoch recovery from future head timestamp preserves committed anchor");
    Check(!racingClock.Desktop(request, false, now + 26000, frequency),
          "unreadable command still falls back to physical");
    Check(racingClock.RoutePose(0, request, true, now + 26000, frequency, synthetic) &&
              synthetic.vecPosition[0] == 10,
          "same-epoch recovery from unreadable command preserves anchor");
    auto invalid = request;
    invalid.pitch = std::numeric_limits<double>::quiet_NaN();
    Check(!racingClock.Desktop(invalid, true, now + 26000, frequency),
          "invalid command falls back without erasing anchor");
    Check(racingClock.RoutePose(0, request, true, now + 26000, frequency, synthetic) &&
              synthetic.vecPosition[0] == 10,
          "same-epoch recovery from invalid command preserves anchor");
    auto broken = Pose(30);
    broken.poseIsValid = false;
    racingClock.Capture(0, broken, now + 27000);
    Check(!racingClock.Desktop(request, true, now + 27000, frequency),
          "invalid physical head invokes physical fallback");
    racingClock.Capture(0, Pose(31), now + 28000);
    Check(racingClock.RoutePose(0, request, true, now + 28000, frequency, synthetic) &&
              synthetic.vecPosition[0] == 10,
          "same-epoch recovery from invalid head preserves anchor");
    Check(!racingClock.Desktop(request, true, now + 201000, frequency),
          "same-epoch expired broker still falls back at200ms");
    auto refreshed = request;
    refreshed.timestamp = now + 201000;
    racingClock.Capture(0, Pose(40), refreshed.timestamp);
    Check(racingClock.RoutePose(0, refreshed, true, refreshed.timestamp, frequency, synthetic) &&
              synthetic.vecPosition[0] == 10,
          "same-epoch fresh broker recovery preserves original anchor");
    refreshed.timestamp = now + 402000;
    Check(!racingClock.Desktop(refreshed, true, refreshed.timestamp, frequency),
          "fresh broker cannot bypass physical source200ms watchdog");
    racingClock.Capture(0, Pose(41), refreshed.timestamp);
    Check(racingClock.RoutePose(0, refreshed, true, refreshed.timestamp, frequency, synthetic) &&
              synthetic.vecPosition[0] == 10,
          "same-epoch physical stream recovery preserves original anchor");
    auto nextEpoch = refreshed;
    ++nextEpoch.epoch;
    Check(racingClock.RoutePose(0, nextEpoch, true, nextEpoch.timestamp, frequency, synthetic) &&
              synthetic.vecPosition[0] == 41,
          "new desktop epoch while already desktop captures one new anchor");
    racingClock.Capture(0, Pose(42), nextEpoch.timestamp + 1);
    Check(!racingClock.RoutePose(0, refreshed, true, nextEpoch.timestamp + 1, frequency, synthetic),
          "older epoch cannot emit or replace newly committed anchor");
    Check(racingClock.RoutePose(0, nextEpoch, true, nextEpoch.timestamp + 1, frequency, synthetic) &&
              synthetic.vecPosition[0] == 41,
          "new epoch anchor stays fixed after old request and head movement");
    racingClock.RecordSynthetic(0, synthetic, nextEpoch.epoch, nextEpoch.timestamp + 1);
    auto recorded = racingClock.GetStatus(nextEpoch, true, nextEpoch.timestamp + 2, frequency);
    Check(recorded.actualMode == 1 && recorded.anchorEpoch == nextEpoch.epoch &&
              recorded.syntheticHeadValid && recorded.syntheticHeadEpoch == nextEpoch.epoch &&
              recorded.syntheticPosition[0] == 41 && recorded.position[0] == 42 &&
              recorded.syntheticHeadQpc == nextEpoch.timestamp + 1,
          "status distinguishes actually routed synthetic head from fresh captured head");
    racingClock.RecordSynthetic(0, synthetic, refreshed.epoch, nextEpoch.timestamp + 2);
    Check(!racingClock.GetStatus(nextEpoch, true, nextEpoch.timestamp + 2, frequency)
               .syntheticHeadValid,
          "stale-epoch synthetic status is explicitly invalid");
    auto pending = nextEpoch;
    ++pending.epoch;
    racingClock.Capture(0, broken, nextEpoch.timestamp + 3);
    auto pendingStatus = racingClock.GetStatus(pending, true, nextEpoch.timestamp + 3, frequency);
    Check(!pendingStatus.actualMode && pendingStatus.ackEpoch == pending.epoch &&
              pendingStatus.anchorEpoch == nextEpoch.epoch,
          "accepted request epoch is distinct from successful anchor commit epoch");
    racingClock.Capture(0, Pose(50), nextEpoch.timestamp + 4);
    Check(racingClock.RoutePose(0, pending, true, nextEpoch.timestamp + 4, frequency, synthetic) &&
              synthetic.vecPosition[0] == 50,
          "pending desktop epoch commits after physical validity recovers");
    // Exercise advancing publication clocks rather than the frozen clock above.
    sw::Router advancing;
    advancing.SetRole(0, sw::DeviceRole::Head, 100);
    auto movingRequest = request;
    movingRequest.yaw = movingRequest.pitch = movingRequest.height = 0;
    movingRequest.timestamp = now;
    advancing.Capture(0, Pose(60), now);
    Check(advancing.RoutePose(0, movingRequest, true, now, frequency, synthetic),
          "advancing publication stress starts from a committed anchor");
    std::atomic<int64_t> publicationClock{now};
    std::atomic<int> drifted{};
    threads.clear();
    threads.emplace_back([&] {
        for (int i = 1; i <= 50000; ++i)
        {
            auto moving = Pose(100 + double(i % 1000));
            moving.qRotation = {std::cos(i * .0001), 0, std::sin(i * .0001), 0};
            advancing.Capture(0, moving, now + i);
            publicationClock.store(now + i, std::memory_order_release);
        }
    });
    for (int i = 0; i < 8; ++i)
        threads.emplace_back([&] {
            for (int j = 0; j < 10000; ++j)
            {
                auto callerClock = publicationClock.load(std::memory_order_acquire);
                vr::DriverPose_t p{};
                if (advancing.RoutePose(0, movingRequest, true, callerClock, frequency, p) &&
                    (p.vecPosition[0] != 60 || p.qRotation.w != 1 || p.qRotation.y != 0))
                    ++drifted;
            }
        });
    for (auto &t : threads)
        t.join();
    Check(drifted == 0 &&
              advancing.RoutePose(0, movingRequest, true,
                                  publicationClock.load(std::memory_order_acquire), frequency,
                                  synthetic) && synthetic.vecPosition[0] == 60 &&
              synthetic.qRotation.w == 1 && synthetic.qRotation.y == 0,
          "advancing concurrent head publication and fallback recovery never recapture anchor");
    alignas(8) std::byte block[512]{};
    sw::WriteBlock(block, request);
    sw::Request copy{};
    Check(sw::ReadBlock(block, copy) && copy.epoch == request.epoch, "atomic IPC roundtrip");
    std::atomic_ref<uint64_t>(*reinterpret_cast<uint64_t *>(block)).store(3);
    Check(!sw::ReadBlock(block, copy), "in-progress IPC read bounded rejection");
    Check(sw::ClassifyControl("/input/system/click") == sw::Control::Unknown,
          "system dashboard button is not synthesized as menu");
    Check(sw::ClassifyControl("/input/y/click") == sw::Control::Menu &&
              sw::ClassifyControl("/input/b/click") == sw::Control::Menu,
          "observed VRChat menu paths mapped");
    Check(sw::ClassifyControl("/input/thumbstick/click") == sw::Control::Run,
          "run uses left stick click");
    for (const auto *path : {"/input/y/touch", "/input/b/touch", "/input/a/touch",
                             "/input/trigger/touch", "/input/grip/touch"})
        Check(sw::ClassifyControl(path) == sw::Control::Touch &&
                  sw::DesiredValue(sw::Control::Touch, sw::DeviceRole::Left, request) == 0,
              "gesture touch is a distinct neutral component, never a click or pull");
    Check(sw::ClassifyControl("/input/not_trigger/value") == sw::Control::Unknown &&
              sw::ClassifyControl("/input/y/click/alias") == sw::Control::Unknown &&
              sw::ClassifyControl("/input/application_menu/touch") == sw::Control::Touch,
          "component mapping uses complete paths and excludes menu touch aliases");
    std::cout
        << "Native routing: " << checks << " checks, " << failures
        << " failures. Evidence: isolated automated only; no SteamVR or physical headset used.\n";
    return failures ? 1 : 0;
}
