#include "config_loader.hpp"
#include "device_identity.hpp"
#include "hooks.hpp"
#include "ipc.hpp"
#include "startup_retry.hpp"
#include <filesystem>
#include <fstream>
#include <cstdio>
#include <memory>
#include <regex>
#include <string>
#include <mutex>
#include <atomic>
namespace
{
constexpr char SupportedBuild[] = "25330290";
std::string Read(const std::filesystem::path &p)
{
    std::ifstream f(p, std::ios::binary);
    if (!f)
        return {};
    return {std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>()};
}
std::string RuntimeBuild()
{
    wchar_t executable[MAX_PATH]{};
    GetModuleFileNameW(nullptr, executable, MAX_PATH);
    auto p = std::filesystem::path(executable);
    if (_wcsicmp(p.filename().c_str(), L"vrserver.exe"))
        return {};
    for (auto dir = p.parent_path(); !dir.empty(); dir = dir.parent_path())
    {
        auto manifest = Read(dir / L"appmanifest_250820.acf");
        std::smatch match;
        if (std::regex_search(manifest, match, std::regex("\\\"buildid\\\"\\s*\\\"([0-9]+)\\\"")))
            return match[1];
        if (dir == dir.parent_path())
            break;
    }
    return {};
}
class Provider final : public vr::IServerTrackedDeviceProvider
{
  public:
    vr::EVRInitError Init(vr::IVRDriverContext *context) override
    {
        std::lock_guard lifecycle(lifecycle_);
        stopping_.store(false, std::memory_order_release);
        try
        {
            return InitInternal(context);
        }
        catch (...)
        {
            reason_ = sw::Error::RuntimeMismatch;
            Log("Driver initialization exception; hooks refused/removed.");
            if (hooks_)
            {
                if (hooks_->Remove())
                    hooks_.reset();
                else
                {
                    hooks_.release();
                    return vr::VRInitError_None;
                }
            }
            mapping_.Close();
            return vr::VRInitError_Driver_Failed;
        }
    }
    vr::EVRInitError InitInternal(vr::IVRDriverContext *context)
    {
        auto init = vr::InitServerDriverContext(context);
        if (init != vr::VRInitError_None)
            return init;
        log_ = vr::VRDriverLog();
        lastConfigurationLog_.clear();
        lastFilesystemLog_.clear();
        frequency_ = sw::QpcFrequency();
        startup_ = sw::StartupRetry(frequency_);
        if (!mapping_.Open(true))
        {
            reason_ = sw::Error::IpcUnavailable;
            Log("IPC mapping creation failed; no hooks installed");
            return vr::VRInitError_None;
        }
        TryStartup(sw::QpcNow());
        Publish();
        return vr::VRInitError_None;
    }
    void Cleanup() override
    {
        stopping_.store(true, std::memory_order_release);
        std::lock_guard lifecycle(lifecycle_);
        startup_.Cancel();
        if (hooks_ && !hooks_->Remove())
        {
            Log(hooks_->failure());
            hooks_.release();
            return;
        }
        hooks_.reset();
        mapping_.Close();
        vr::CleanupDriverContext();
    }
    const char *const *GetInterfaceVersions() override
    {
        return vr::k_InterfaceVersions;
    }
    void RunFrame() override
    {
        std::lock_guard lifecycle(lifecycle_);
        if (stopping_.load(std::memory_order_acquire)) return;
        auto now = sw::QpcNow();
        if (!mapping_.request()) return;
        if (now - lastFrame_ < frequency_ / 120) return;
        lastFrame_ = now;
        if (!hooks_) TryStartup(now);
        if (hooks_)
        {
            if (reason_ == sw::Error::HookConflict)
            {
                Publish();
                return;
            }
            if (now - lastDiscovery_ > frequency_)
            {
                Discover();
                lastDiscovery_ = now;
                if (!hooks_->CheckIntegrity())
                {
                    reason_ = sw::Error::HookConflict;
                    Log(hooks_->failure());
                    Log("Method ownership or approved companion lifecycle changed; synthesis disabled.");
                    hooks_->Remove();
                    Publish();
                    return;
                }
            }
            hooks_->Tick(now);
            if (!poseReadyLogged_ && hooks_->PoseReady())
            {
                poseReadyLogged_ = true;
                Log(hooks_->failure());
                if (hooks_->ChainedPose())
                    Log("Approved SpaceCalibrator SHA256 17b8510ee9cfc3cd017deb5a40ab6ef2ac5e5e1412f0f00038788fa6b0898314; raw calibration feed retained, capture after calibration before output.");
            }
        }
        Publish();
    }
    bool ShouldBlockStandbyMode() override
    {
        std::lock_guard lifecycle(lifecycle_);
        if (stopping_.load(std::memory_order_acquire) || !hooks_) return false;
        auto status = hooks_->StatusNow(sw::QpcNow());
        return status.actualMode == 1;
    }
    void EnterStandby() override {}
    void LeaveStandby() override {}

  private:
    void TryStartup(int64_t now)
    {
        if (stopping_.load(std::memory_order_acquire) || !startup_.BeginCheck(now)) return;
        try { StartupCheck(); }
        catch (...)
        {
            if (startup_.state() == sw::StartupRetry::State::Attempted)
            {
                reason_ = sw::Error::HookConflict;
                Log("Startup exception after hook installation began; no installation retry in this runtime session.");
                if (hooks_)
                {
                    if (hooks_->Remove()) hooks_.reset();
                    else hooks_.release();
                }
            }
            else
            {
                reason_ = sw::Error::StartupConfigFailure;
                Log("Startup configuration check exception; strict gates remain closed, bounded read retry pending.");
                startup_.Retry(sw::QpcNow());
            }
        }
    }
    void StartupCheck()
    {
        auto config = sw::LoadCurrentUserConfig();
        char configurationLog[400]{};
        std::snprintf(configurationLog, sizeof(configurationLog),
                      "Startup configuration: stage=%s systemError=%u bytes=%u schema=%s; "
                      "pathFingerprint=%s processSidKnown=%u effectiveSidKnown=%u "
                      "effectiveMatchesProcess=%u threadImpersonated=%u effectiveTokenError=%u; "
                      "explicit process-token Windows PROFILE known folder.",
                      sw::ConfigStageName(config.stage), config.systemError, config.bytes,
                      config.ready() ? "valid" : config.stage == sw::ConfigLoadStage::Schema
                                                    ? "invalid" : "not-read",
                      config.pathFingerprint[0] ? config.pathFingerprint.data() : "unknown",
                      unsigned(config.processSidKnown), unsigned(config.effectiveSidKnown),
                      unsigned(config.effectiveMatchesProcess), unsigned(config.threadImpersonated),
                      config.effectiveTokenError);
        if (lastConfigurationLog_ != configurationLog)
        {
            Log(configurationLog);
            lastConfigurationLog_ = configurationLog;
        }
        char fileLog[768]{};
        std::snprintf(fileLog, sizeof(fileLog),
                      "Startup filesystem: pathChars=%u absolute=%u embeddedNul=%u rawPathFingerprint=%s "
                      "normalStage=%s normalOpenError=%u extendedAttempted=%u extendedStage=%s extendedOpenError=%u "
                      "normalAttrs=%u,%u,%u normalAttrErrors=%u,%u,%u extendedAttrs=%u,%u,%u extendedAttrErrors=%u,%u,%u "
                      "parentCaseKnown=%u,%u parentCaseFlags=%u,%u parentCaseErrors=%u,%u.",
                      config.pathCharacters, unsigned(config.pathAbsolute), unsigned(config.pathEmbeddedNul),
                      config.rawPathFingerprint[0] ? config.rawPathFingerprint.data() : "unknown",
                      sw::ConfigStageName(config.normalStage), config.normalOpenError,
                      unsigned(config.extendedAttempted), sw::ConfigStageName(config.extendedStage), config.extendedOpenError,
                      config.normalAttributes[0], config.normalAttributes[1], config.normalAttributes[2],
                      config.normalAttributeErrors[0], config.normalAttributeErrors[1], config.normalAttributeErrors[2],
                      config.extendedAttributes[0], config.extendedAttributes[1], config.extendedAttributes[2],
                      config.extendedAttributeErrors[0], config.extendedAttributeErrors[1], config.extendedAttributeErrors[2],
                      unsigned(config.parentCaseKnown[0]), unsigned(config.parentCaseKnown[1]),
                      config.parentCaseFlags[0], config.parentCaseFlags[1], config.parentCaseErrors[0], config.parentCaseErrors[1]);
        if (lastFilesystemLog_ != fileLog)
        {
            Log(fileLog);
            lastFilesystemLog_ = fileLog;
        }
        if (!config.ready())
        {
            reason_ = config.stage == sw::ConfigLoadStage::Schema
                          ? sw::Error::InvalidRequest : sw::Error::StartupConfigFailure;
            startup_.Retry(sw::QpcNow());
            return;
        }
        auto &parsed = config.config;
        if (!parsed.experimentalOptIn)
        {
            reason_ = sw::Error::OptInDisabled;
            startup_.Retry(sw::QpcNow());
            return;
        }
        if (parsed.approvedRuntimeBuild != SupportedBuild || RuntimeBuild() != SupportedBuild)
        {
            reason_ = sw::Error::RuntimeMismatch;
            startup_.Retry(sw::QpcNow());
            return;
        }
        auto *host = vr::VRServerDriverHost();
        auto *input = vr::VRDriverInput();
        if (!host || !input || !vr::VRProperties())
        {
            reason_ = sw::Error::RuntimeMismatch;
            startup_.Retry(sw::QpcNow());
            return;
        }
        if (stopping_.load(std::memory_order_acquire)) { startup_.Cancel(); return; }
        if (!startup_.BeginInstall()) return;
        hooks_ = std::make_unique<sw::HookRuntime>(router_, mapping_.request());
        if (!hooks_->Install(host, input))
        {
            reason_ = sw::Error::HookConflict;
            Log(hooks_->failure());
            if (hooks_->Remove())
                hooks_.reset();
            else
                hooks_.release();
            return;
        }
        reason_ = sw::Error::None;
        Discover();
        Log("Experimental input creation capture installed; pose validation deferred during companion startup. "
            "No HMD/display registered or restarted. Real-headset validation is pending.");
        Log("Startup read retries completed before the single hook installation attempt; components created earlier remain unobserved until recreated by their owner.");
    }
    void Log(const char *text)
    {
        if (log_)
            log_->Log(text);
    }
    void Discover()
    {
        auto *p = vr::VRProperties();
        if (!p)
            return;
        std::array<sw::DeviceRole, vr::k_unMaxTrackedDeviceCount> roles{};
        std::array<uint64_t, vr::k_unMaxTrackedDeviceCount> containers{};
        std::array<bool, vr::k_unMaxTrackedDeviceCount> bodyTracked{};
        std::array<bool, vr::k_unMaxTrackedDeviceCount> genericTracked{};
        int left = 0, right = 0;
        for (uint32_t index = 0; index < vr::k_unMaxTrackedDeviceCount; ++index)
        {
            auto container = p->TrackedDeviceToPropertyContainer(index);
            containers[index] = container;
            vr::ETrackedPropertyError error{};
            auto deviceClass = p->GetInt32Property(container, vr::Prop_DeviceClass_Int32, &error);
            auto &role = roles[index];
            role = sw::DeviceRole::Other;
            if (error == vr::TrackedProp_Success &&
                sw::EligibleBodyTrackedClass(vr::ETrackedDeviceClass(deviceClass)))
            {
                // Generic body sources are vendor-neutral: the public class,
                // current nonzero container and independently captured pose are
                // authority. Optional model/vendor strings never grant access.
                genericTracked[index] = container &&
                    sw::EligibleGenericTrackerClass(vr::ETrackedDeviceClass(deviceClass));
                if (!genericTracked[index] && !sw::EligiblePhysicalRoleIdentity(*p, container))
                    continue;
                bodyTracked[index] = true;
            }
            if (error == vr::TrackedProp_Success && index == 0 &&
                deviceClass == vr::TrackedDeviceClass_HMD)
                role = sw::DeviceRole::Head;
            if (deviceClass == vr::TrackedDeviceClass_Controller &&
                error == vr::TrackedProp_Success)
            {
                auto hand =
                    p->GetInt32Property(container, vr::Prop_ControllerRoleHint_Int32, &error);
                if (error == vr::TrackedProp_Success && hand == vr::TrackedControllerRole_LeftHand)
                {
                    role = sw::DeviceRole::Left;
                    ++left;
                }
                if (error == vr::TrackedProp_Success && hand == vr::TrackedControllerRole_RightHand)
                {
                    role = sw::DeviceRole::Right;
                    ++right;
                }
            }
        }
        for (uint32_t index = 0; index < roles.size(); ++index)
        {
            auto role = roles[index];
            if ((role == sw::DeviceRole::Left && left != 1) ||
                (role == sw::DeviceRole::Right && right != 1))
                role = sw::DeviceRole::Other;
            router_.SetRole(index, role, containers[index]);
            router_.SetBodySpinEligible(index, bodyTracked[index]);
            router_.SetGenericTracker(index, genericTracked[index]);
        }
    }
    void Publish()
    {
        if (!mapping_.status())
            return;
        auto now = sw::QpcNow();
        sw::Status status{};
        if (hooks_ && reason_ == sw::Error::None)
            status = hooks_->StatusNow(now);
        else
        {
            status.timestamp = now;
            status.error = uint32_t(reason_);
            status.headAgeMilliseconds = -1;
        }
        sw::WriteBlock(mapping_.status(), status);
    }
    sw::Router router_;
    sw::Mapping mapping_;
    std::unique_ptr<sw::HookRuntime> hooks_;
    vr::IVRDriverLog *log_{};
    sw::Error reason_{sw::Error::OptInDisabled};
    int64_t frequency_{}, lastFrame_{}, lastDiscovery_{};
    bool poseReadyLogged_{};
    sw::StartupRetry startup_;
    std::mutex lifecycle_;
    std::atomic<bool> stopping_{true};
    std::string lastConfigurationLog_;
    std::string lastFilesystemLog_;
};
Provider provider;
} // namespace
extern "C" __declspec(dllexport) void *HmdDriverFactory(const char *name, int *error) noexcept
{
    if (name && std::strcmp(name, vr::IServerTrackedDeviceProvider_Version) == 0)
    {
        if (error)
            *error = vr::VRInitError_None;
        return &provider;
    }
    if (error)
        *error = vr::VRInitError_Init_InterfaceNotFound;
    return nullptr;
}
