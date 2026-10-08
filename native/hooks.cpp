#include "hooks.hpp"
#include "MinHook.h"
#include "ipc.hpp"
#include <bit>
#include <cmath>
#include <cstring>
#include <filesystem>
namespace sw
{
HookRuntime *HookRuntime::instance_{};
namespace
{
bool ExecutableUnpatched(void *address, bool enforceOwner)
{
    MEMORY_BASIC_INFORMATION region{};
    if (!VirtualQuery(address, &region, sizeof(region)) || region.State != MEM_COMMIT)
        return false;
    if (!(region.Protect &
          (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))
        return false;
    auto p = static_cast<unsigned char *>(address);
    // Refuse pre-existing detours and indirect forwarders; do not guess a chain order.
    if (p[0] == 0xE9 || p[0] == 0xEB || (p[0] == 0xFF && p[1] == 0x25) ||
        (p[0] == 0x48 && p[1] == 0xB8) || (p[0] == 0x68))
        return false;
    if (!enforceOwner)
        return true;
    HMODULE owner{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(address), &owner))
        return false;
    wchar_t module[MAX_PATH]{}, process[MAX_PATH]{};
    GetModuleFileNameW(owner, module, MAX_PATH);
    GetModuleFileNameW(nullptr, process, MAX_PATH);
    // Both host and input method implementation must live inside this exact SteamVR install.
    auto runtime = std::filesystem::path(process).parent_path().parent_path().parent_path();
    auto relative = std::filesystem::path(module).lexically_relative(runtime);
    return !relative.empty() && *relative.begin() != L"..";
}
struct Active
{
    std::atomic<uint32_t> &count;
    Active(std::atomic<uint32_t> &c) : count(c)
    {
        count.fetch_add(1, std::memory_order_acquire);
    }
    ~Active()
    {
        count.fetch_sub(1, std::memory_order_release);
    }
};
} // namespace
bool HookRuntime::Install(vr::IVRServerDriverHost *host, vr::IVRDriverInput *input,
                          bool enforceOwner, const PoseChainApproval *isolatedFixture)
{
    if (instance_)
    {
        failure_ = "another switcheroonie hook instance is active";
        return false;
    }
    frequency_ = QpcFrequency();
    enforceOwner_ = enforceOwner;
    installTime_ = QpcNow();
    if (!host || !input)
    {
        failure_ = "required interface unavailable";
        return false;
    }
    auto hostVtable = *reinterpret_cast<void ***>(host);
    auto inputVtable = *reinterpret_cast<void ***>(input);
    runtimePoseEntry_ = hostVtable[1];
    targets_ = {nullptr, inputVtable[0], inputVtable[1], inputVtable[2], inputVtable[3],
                inputVtable[5], inputVtable[6], inputVtable[7], inputVtable[8], nullptr, nullptr};
    void *detours[] = {
        nullptr, reinterpret_cast<void *>(CreateBoolHook), reinterpret_cast<void *>(UpdateBoolHook),
        reinterpret_cast<void *>(CreateScalarHook), reinterpret_cast<void *>(UpdateScalarHook),
        reinterpret_cast<void *>(CreateSkeletonHook), reinterpret_cast<void *>(UpdateSkeletonHook),
        reinterpret_cast<void *>(CreatePoseHook), reinterpret_cast<void *>(UpdatePoseHook)};
    for (size_t i = 1; i < 9; ++i)
        if (!ExecutableUnpatched(targets_[i], enforceOwner))
        {
            failure_ = "input method target belongs to another module, existing detour, or unsupported forwarder";
            return false;
        }
    if (MH_Initialize() != MH_OK)
    {
        failure_ = "MinHook already initialized or unavailable; refusing shared ownership";
        return false;
    }
    initialized_ = true;
    instance_ = this;
    created_ = targets_.size();
    for (size_t i = 1; i < 9; ++i)
    {
        std::memcpy(prepared_[i].data(), targets_[i], 5);
        if (MH_CreateHook(targets_[i], detours[i], &original_[i]) != MH_OK)
        {
            failure_ = "failed to prepare every required input method hook";
            Remove();
            return false;
        }
        createdMask_ |= 1u << i;
    }
    for (size_t i = 1; i < 9; ++i)
        if (MH_QueueEnableHook(targets_[i]) != MH_OK)
        {
            failure_ = "failed to queue input method hooks";
            Remove();
            return false;
        }
    auto enabledInputs = MH_ApplyQueued();
    for (size_t i = 1; i < 9; ++i)
    {
        if (std::memcmp(prepared_[i].data(), targets_[i], 5) == 0)
            continue;
        void *destination{};
        if (!ResolveMinHookDestination(targets_[i], destination) || destination != detours[i])
        {
            unownedDuringInstall_ = true;
            continue;
        }
        std::memcpy(patched_[i].data(), targets_[i], 5);
        enabledMask_.fetch_or(1u << i, std::memory_order_release);
    }
    enabled_ = enabledMask_.load(std::memory_order_acquire) != 0;
    if (enabledInputs != MH_OK || unownedDuringInstall_ ||
        enabledMask_.load(std::memory_order_acquire) != createdMask_.load(std::memory_order_acquire))
    {
        failure_ = "failed to enable input hook transaction";
        Remove();
        return false;
    }
    running_.store(true, std::memory_order_release);
    if (!enforceOwner)
    {
        if (!InstallPose(false, isolatedFixture))
        {
            Remove();
            return false;
        }
    }
    else
    {
        deferredPose_ = true;
        failure_ = "input capture installed; awaiting stable post-calibration pose interface";
    }
    return true;
}
bool HookRuntime::InstallPose(bool enforceOwner, const PoseChainApproval *isolatedFixture)
{
    PoseChainApproval approval{};
    bool bare = ExecutableUnpatched(runtimePoseEntry_, enforceOwner);
    if (!bare)
    {
        bool approved = enforceOwner ? ApproveSpaceCalibrator(runtimePoseEntry_, approval) : false;
        if (!enforceOwner && isolatedFixture)
        {
            void *destination{};
            HMODULE owner{};
            approved = ResolveMinHookDestination(runtimePoseEntry_, destination) &&
                GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                      GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                   reinterpret_cast<LPCWSTR>(destination), &owner) &&
                owner == isolatedFixture->module;
            approval = *isolatedFixture;
            approval.detourTarget = destination;
            if (approved)
                approved = ResolveOriginalPoseTrampoline(runtimePoseEntry_, owner,
                                                         approval.innerTrampoline);
        }
        if (!approved || !ExecutableUnpatched(approval.cleanupTarget, false) ||
            !ExecutableUnpatched(approval.detourTarget, false) ||
            !ExecutableUnpatched(approval.innerTrampoline, false))
        {
            failure_ = "pose chain is not the exact approved post-calibration SpaceCalibrator trampoline";
            return false;
        }
        targets_[0] = approval.innerTrampoline;
        targets_[9] = approval.cleanupTarget;
        targets_[10] = approval.detourTarget;
        chainedPose_ = true;
    }
    else
        targets_[0] = runtimePoseEntry_;
    size_t indices[] = {0, 9, 10};
    void *detours[] = {reinterpret_cast<void *>(PoseHook),
                       reinterpret_cast<void *>(ForeignCleanupHook),
                       reinterpret_cast<void *>(ForeignPoseGuardHook)};
    size_t count = chainedPose_ ? 3 : 1;
    for (size_t n = 0; n < count; ++n)
    {
        auto i = indices[n];
        std::memcpy(prepared_[i].data(), targets_[i], 5);
        if (MH_CreateHook(targets_[i], detours[n], &original_[i]) != MH_OK)
        {
            failure_ = "failed to prepare post-calibration pose/lifetime hooks";
            return false;
        }
        createdMask_ |= 1u << i;
    }
    for (size_t n = 0; n < count; ++n)
        if (MH_QueueEnableHook(targets_[indices[n]]) != MH_OK)
        {
            failure_ = "failed to queue post-calibration pose/lifetime hooks";
            return false;
        }
    auto enabledPose = MH_ApplyQueued();
    for (size_t n = 0; n < count; ++n)
    {
        auto i = indices[n];
        if (std::memcmp(prepared_[i].data(), targets_[i], 5) == 0)
            continue;
        void *destination{};
        if (!ResolveMinHookDestination(targets_[i], destination) || destination != detours[n])
        {
            unownedDuringInstall_ = true;
            continue;
        }
        std::memcpy(patched_[i].data(), targets_[i], 5);
        enabledMask_.fetch_or(1u << i, std::memory_order_release);
    }
    if (enabledPose != MH_OK || unownedDuringInstall_ ||
        enabledMask_.load(std::memory_order_acquire) != createdMask_.load(std::memory_order_acquire))
    {
        failure_ = "failed to enable post-calibration pose/lifetime transaction";
        return false;
    }
    deferredPose_ = false;
    failure_ = chainedPose_ ? "approved SpaceCalibrator post-calibration routing installed" :
                             "unpatched runtime pose routing installed";
    poseReady_.store(true, std::memory_order_release);
    return true;
}
void HookRuntime::TryDeferredPose(int64_t now) noexcept
{
    if (!deferredPose_ || !running_.load(std::memory_order_acquire))
        return;
    // Server Init must return so other providers can initialize. Input creation capture is
    // already active. No startup presence claim or vendor load-order change is made.
    try
    {
        if (ExecutableUnpatched(runtimePoseEntry_, enforceOwner_) &&
            now - installTime_ < 3 * frequency_)
            return;
        if (InstallPose(enforceOwner_, nullptr))
            return;
    }
    catch (...)
    {
        failure_ = "post-calibration pose validation exception; routing refused";
    }
    deferredPose_ = false;
    poseInstallFailed_.store(true, std::memory_order_release);
    running_.store(false, std::memory_order_release);
}
bool HookRuntime::CheckIntegrity() const noexcept
{
    Active guard{activeCalls_};
    if (unownedDuringInstall_ || foreignCleanupObserved_.load(std::memory_order_acquire) ||
        poseInstallFailed_.load(std::memory_order_acquire))
        return false;
    for (size_t i = 0; i < created_; ++i)
        if ((enabledMask_ & (1u << i)) &&
            std::memcmp(targets_[i], patched_[i].data(), 5) != 0)
            return false;
    return true;
}
bool HookRuntime::Remove()
{
    std::lock_guard<std::mutex> shutdownLock(removeMutex_);
    if (!initialized_)
        return true;
    if (foreignCleanupObserved_.load(std::memory_order_acquire))
    {
        failure_ = "foreign cleanup complete; stopped outer lifetime gate pinned until process exit";
        return false;
    }
    running_.store(false, std::memory_order_release);
    stoppingForeign_.store(true, std::memory_order_release);
    bool patchedStillOwned = true;
    // Keep the outer guard installed and gated while calls already in the foreign body drain.
    while (activeCalls_.load(std::memory_order_acquire))
        Sleep(1);
    // Shutdown alone uses this private path, under removeMutex_ after counted work drains.
    // Public Tick/Submit are gated; these neutral input calls still have live trampolines.
    TickInternal(QpcNow());
    for (size_t i = 0; i < created_; ++i)
        if (enabledMask_ & (1u << i))
            patchedStillOwned = patchedStillOwned &&
                std::memcmp(targets_[i], patched_[i].data(), 5) == 0;
    if (unownedDuringInstall_ || (enabled_ && !patchedStillOwned))
    {
        // Never overwrite a hook installed after us. Keep our module/state alive for any
        // external trampoline that still references us, and remain physical passthrough.
        HMODULE pinned{};
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                           reinterpret_cast<LPCWSTR>(PoseHook), &pinned);
        failure_ = "hook changed after installation; module pinned, restart required for cleanup";
        return false;
    }
    for (size_t i = 0; i < created_; ++i)
        if (createdMask_ & (1u << i))
            if (MH_QueueDisableHook(targets_[i]) != MH_OK)
                return RetainForShutdown("failed to queue hook disable; stopped state retained");
    if (MH_ApplyQueued() != MH_OK)
        return RetainForShutdown("hook disable transaction failed; trampolines/state retained");
    // Shutdown can wait; real-time callbacks never wait or acquire blocking locks.
    while (activeCalls_.load(std::memory_order_acquire))
        Sleep(1);
    for (size_t i = 0; i < created_; ++i)
        if (createdMask_ & (1u << i))
            if (MH_RemoveHook(targets_[i]) != MH_OK)
                return RetainForShutdown("hook removal failed; stopped state retained");
    if (MH_Uninitialize() != MH_OK)
        return RetainForShutdown("hook library cleanup failed; stopped state retained");
    created_ = 0;
    createdMask_ = 0;
    enabledMask_ = 0;
    poseReady_.store(false, std::memory_order_release);
    initialized_ = false;
    enabled_ = false;
    instance_ = nullptr;
    return true;
}
bool HookRuntime::RetainForShutdown(const char *message) noexcept
{
    failure_.store(message, std::memory_order_release);
    poseInstallFailed_.store(true, std::memory_order_release);
    HMODULE pinned{};
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                       reinterpret_cast<LPCWSTR>(ForeignPoseGuardHook), &pinned);
    return false;
}
void HookRuntime::ForeignCleanupHook(void *provider) noexcept
{
    auto self = instance_;
    if (!self)
        return;
    std::lock_guard<std::mutex> shutdownLock(self->removeMutex_);
    auto cleanup = reinterpret_cast<void (*)(void *)>(self->targets_[9]);
    self->stoppingForeign_.store(true, std::memory_order_release);
    self->running_.store(false, std::memory_order_release);
    self->foreignCleanupObserved_.store(true, std::memory_order_release);
    self->failure_ = "approved SpaceCalibrator cleanup observed; routing stopped before foreign trampoline release";
    self->RetainForShutdown(self->failure());
    while (self->activeCalls_.load(std::memory_order_acquire))
        Sleep(1);
    self->TickInternal(QpcNow());
    for (size_t i = 0; i < self->created_; ++i)
        if ((self->createdMask_ & (1u << i)) &&
            std::memcmp(self->targets_[i], self->patched_[i].data(), 5))
        {
            self->failure_ = "foreign cleanup blocked by changed hook ownership; live chain retained";
            return;
        }
    // The lifetime-only outer guard remains installed, stopping any queued/new entry.
    // All other own hooks are restored before foreign MinHook frees its original buffer.
    for (size_t i = 0; i < 10; ++i)
        if (self->createdMask_ & (1u << i))
            if (MH_QueueDisableHook(self->targets_[i]) != MH_OK)
            {
                self->RetainForShutdown("foreign cleanup hook disable queue failed; live chain retained");
                return;
            }
    if (MH_ApplyQueued() != MH_OK)
    {
        self->RetainForShutdown("foreign cleanup disable transaction failed; live chain retained");
        return;
    }
    while (self->activeCalls_.load(std::memory_order_acquire))
        Sleep(1);
    for (size_t i = 0; i < 10; ++i)
        if (self->createdMask_ & (1u << i))
        {
            if (MH_RemoveHook(self->targets_[i]) != MH_OK)
            {
                self->RetainForShutdown("foreign cleanup inner removal failed; live chain retained");
                return;
            }
            self->createdMask_ &= ~(1u << i);
            self->enabledMask_ &= ~(1u << i);
        }
    self->poseReady_.store(false, std::memory_order_release);
    HMODULE pinned{};
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                       reinterpret_cast<LPCWSTR>(ForeignPoseGuardHook), &pinned);
    cleanup(provider); // restored foreign entry, never a freed owned trampoline
}
void HookRuntime::ForeignPoseGuardHook(void *host, uint32_t index, const vr::DriverPose_t &pose,
                                      uint32_t size) noexcept
{
    auto self = instance_;
    Active guard{self->activeCalls_};
    if (self->stoppingForeign_.load(std::memory_order_acquire))
        return;
    reinterpret_cast<PoseFn>(self->original_[10])(host, index, pose, size);
}
HookRuntime::Component *HookRuntime::Find(uint64_t handle) noexcept
{
    if (!handle)
        return nullptr;
    size_t at = handle % components_.size();
    for (size_t i = 0; i < components_.size(); ++i)
    {
        auto &e = components_[(at + i) % components_.size()];
        auto key = e.handle.load(std::memory_order_acquire);
        if (!key)
            return nullptr;
        if (key == handle)
            return e.ready.load(std::memory_order_acquire) ? &e : nullptr;
    }
    return nullptr;
}
void HookRuntime::Remember(uint64_t handle, uint64_t container, const char *name,
                           InputKind kind, void *inputSelf) noexcept
{
    if (!handle)
        return;
    uint32_t proximityPath = name && std::strcmp(name, "/input/proximity") == 0 ? 1u :
                             name && std::strcmp(name, "/input/proximity/click") == 0 ? 2u : 0u;
    size_t at = handle % components_.size();
    for (size_t i = 0; i < components_.size(); ++i)
    {
        auto &e = components_[(at + i) % components_.size()];
        uint64_t empty = 0;
        if (e.handle.compare_exchange_strong(empty, handle, std::memory_order_acq_rel))
        {
            e.container.store(container, std::memory_order_relaxed);
            e.inputSelf.store(inputSelf, std::memory_order_relaxed);
            e.proximityPath.store(proximityPath, std::memory_order_relaxed);
            e.kind.store(uint32_t(kind), std::memory_order_relaxed);
            e.control.store(uint32_t(ClassifyControl(name)), std::memory_order_relaxed);
            e.menuSource.store(name && std::strcmp(name, "/input/y/click") == 0 ? 1u :
                               name && std::strcmp(name, "/input/b/click") == 0 ? 2u :
                               name && std::strcmp(name, "/input/application_menu/click") == 0
                                   ? 3u : 0u, std::memory_order_relaxed);
            e.ready.store(1, std::memory_order_release);
            return;
        }
        if (empty == handle)
        {
            if (!e.ready.load(std::memory_order_acquire))
                e.proximityIdentityAmbiguous.store(true, std::memory_order_release);
            else if (e.proximityPath.load(std::memory_order_relaxed) || proximityPath)
            {
                if (e.container.load(std::memory_order_relaxed) != container ||
                    e.inputSelf.load(std::memory_order_relaxed) != inputSelf ||
                    e.kind.load(std::memory_order_relaxed) != uint32_t(kind) ||
                    e.proximityPath.load(std::memory_order_relaxed) != proximityPath)
                    e.proximityIdentityAmbiguous.store(true, std::memory_order_release);
                // Even a repeated identity needs a new original update before
                // it can establish source state after component recreation.
                e.rawProximity.store(0, std::memory_order_release);
            }
            return;
        }
    }
}
bool HookRuntime::ReadRequest(Request &r) noexcept
{
    if (!ReadBlock(requestMemory_, r))
        return ReadBlock(cachedRequest_.data(), r);
    if (r.sequence > cachedExternalSequence_.load(std::memory_order_acquire) &&
        !cacheWriter_.test_and_set(std::memory_order_acquire))
    {
        if (r.sequence > cachedExternalSequence_.load(std::memory_order_relaxed))
        {
            WriteBlock(cachedRequest_.data(), r);
            cachedExternalSequence_.store(r.sequence, std::memory_order_release);
        }
        cacheWriter_.clear(std::memory_order_release);
    }
    return true;
}
bool HookRuntime::RequestNow(Request &r, int64_t now) noexcept
{
    if (!running_.load(std::memory_order_acquire) || !poseReady_.load(std::memory_order_acquire))
        return false;
    bool read = ReadRequest(r);
    return router_.Desktop(r, read, std::max(now, QpcNow()), frequency_);
}
float HookRuntime::RouteValue(Component *c, float value, bool desktop, const Request &r) noexcept
{
    if (!c)
        return value;
    auto role = router_.ContainerRole(c->container.load(std::memory_order_relaxed));
    if (role == DeviceRole::Head)
        return desktop && Control(c->control.load(std::memory_order_relaxed)) == Control::Proximity
                   ? 1.f
                   : value;
    if (role != DeviceRole::Left && role != DeviceRole::Right)
    {
        if (c->ownedOutput.exchange(false, std::memory_order_acq_rel))
        {
            c->releaseGate.store(false, std::memory_order_release);
            return 0;
        }
        return value;
    }
    if (desktop)
    {
        c->ownedOutput.store(true, std::memory_order_release);
        c->releaseGate.store(true, std::memory_order_release);
        auto control = Control(c->control.load(std::memory_order_relaxed));
        if (control == Control::Menu)
            return r.armed && (r.actions & 4) &&
                           c->handle.load(std::memory_order_relaxed) ==
                               menuHandle_.load(std::memory_order_acquire)
                       ? 1.f
                       : 0.f;
        return DesiredValue(control, role, r);
    }
    c->ownedOutput.store(false, std::memory_order_release);
    if (c->releaseGate.load(std::memory_order_acquire))
    {
        if (std::abs(value) <= .05f)
            c->releaseGate.store(false, std::memory_order_release);
        return 0;
    }
    return value;
}
vr::EVRInputError HookRuntime::ForwardBoolean(void *self, uint64_t handle, bool value,
                                             double offset, bool synthetic) noexcept
{
    auto error = reinterpret_cast<UpdateBoolFn>(original_[2])(self, handle, value, offset);
    if (error != vr::VRInputError_None)
        lastInputError_.store(uint32_t(error), std::memory_order_release);
    else if (handle == menuHandle_.load(std::memory_order_acquire))
    {
        if (synthetic)
        {
            bool previous = menuPressed_.exchange(value, std::memory_order_acq_rel);
            if (value && !previous)
                menuRisingEdges_.fetch_add(1, std::memory_order_relaxed);
        }
        else if (!value)
            menuPressed_.store(false, std::memory_order_release);
    }
    return error;
}
vr::EVRInputError HookRuntime::ForwardScalar(void *self, uint64_t handle, float value,
                                            double offset) noexcept
{
    auto error = reinterpret_cast<UpdateScalarFn>(original_[4])(self, handle, value, offset);
    if (error != vr::VRInputError_None)
        lastInputError_.store(uint32_t(error), std::memory_order_release);
    return error;
}
void HookRuntime::RoutePoseCall(void *self, uint32_t index, const vr::DriverPose_t &pose,
                                uint32_t size) noexcept
{
    auto original = reinterpret_cast<PoseFn>(original_[0]);
    if (size != sizeof(vr::DriverPose_t) || !running_.load(std::memory_order_acquire))
    {
        original(self, index, pose, size);
        return;
    }
    const auto now = QpcNow();
    if (index < poseHosts_.size())
        poseHosts_[index].store(self, std::memory_order_release);
    router_.Capture(index, pose, now);
    Request r{};
    bool read = ReadRequest(r);
    vr::DriverPose_t routed{};
    bool spinApplied = false;
    if (router_.RoutePose(index, r, read, QpcNow(), frequency_, routed, &spinApplied))
    {
        original(self, index, routed, sizeof(routed));
        if (index < vr::k_unMaxTrackedDeviceCount)
        {
            if (spinApplied)
            {
                spunDevices_.fetch_or(uint64_t(1) << index, std::memory_order_acq_rel);
                router_.CountBodySpin(r.bodySpinGeneration);
            }
            else
                spunDevices_.fetch_and(~(uint64_t(1) << index), std::memory_order_acq_rel);
        }
        router_.RecordSynthetic(index, routed, r.epoch, QpcNow());
        router_.CountRouted();
    }
    else
    {
        original(self, index, pose, size);
        if (index < vr::k_unMaxTrackedDeviceCount)
            spunDevices_.fetch_and(~(uint64_t(1) << index), std::memory_order_acq_rel);
    }
}
void HookRuntime::Submit(uint32_t index, const vr::DriverPose_t &p) noexcept
{
    Active guard{activeCalls_};
    if (stoppingForeign_.load(std::memory_order_acquire) ||
        !running_.load(std::memory_order_acquire) || !poseReady_.load(std::memory_order_acquire))
        return;
    Request request{};
    if (!ReadRequest(request))
        return;
    SubmitInternal(index, p, request.epoch);
}
void HookRuntime::SubmitInternal(uint32_t index, const vr::DriverPose_t &p, uint64_t epoch) noexcept
{
    if (index >= poseHosts_.size())
        return;
    auto *owner = poseHosts_[index].load(std::memory_order_acquire);
    if (!owner)
        return; // A role/property alone does not supply a vendor-owned host context.
    reinterpret_cast<PoseFn>(original_[0])(owner, index, p, sizeof(p));
    router_.RecordSynthetic(index, p, epoch, QpcNow());
    router_.CountRouted();
}
void HookRuntime::Tick(int64_t now) noexcept
{
    Active guard{activeCalls_};
    if (stoppingForeign_.load(std::memory_order_acquire) || !running_.load(std::memory_order_acquire))
        return;
    TryDeferredPose(now);
    if (stoppingForeign_.load(std::memory_order_acquire))
        return;
    TickInternal(now);
}
void HookRuntime::TickInternal(int64_t now) noexcept
{
    if (!created_)
        return;
    Request r{};
    const bool desktop = RequestNow(r, now);
    const bool spin = running_.load(std::memory_order_acquire) &&
                      poseReady_.load(std::memory_order_acquire) &&
                      router_.BodySpinPermitted(r, true, std::max(now, QpcNow()), frequency_);
    const bool previous = wasDesktop_.exchange(desktop, std::memory_order_acq_rel);
    uint64_t menuHandle = 0;
    uint32_t menuPath = 0;
    for (auto &c : components_)
    {
        if (!c.ready.load(std::memory_order_acquire) ||
            Control(c.control.load(std::memory_order_relaxed)) != Control::Menu ||
            InputKind(c.kind.load(std::memory_order_relaxed)) != InputKind::Boolean)
            continue;
        auto role = router_.ContainerRole(c.container.load(std::memory_order_relaxed));
        auto source = c.menuSource.load(std::memory_order_relaxed);
        uint32_t path = role == DeviceRole::Left && source == 1 ? 1u :
                        role == DeviceRole::Left && source == 2 ? 2u :
                        role == DeviceRole::Right && source == 2 ? 3u :
                        role == DeviceRole::Left && source == 3 ? 4u : 0u;
        if (path && (!menuPath || path < menuPath))
        {
            menuPath = path;
            menuHandle = c.handle.load(std::memory_order_relaxed);
        }
    }
    menuPath_.store(menuPath, std::memory_order_release);
    if (menuHandle != menuHandle_.load(std::memory_order_acquire))
        menuPressed_.store(false, std::memory_order_release);
    menuHandle_.store(menuHandle, std::memory_order_release);
    if (desktop || spin || spunDevices_.load(std::memory_order_acquire))
        for (uint32_t i = 0; i < vr::k_unMaxTrackedDeviceCount; ++i)
        {
            const auto bit = uint64_t(1) << i;
            const bool wasSpun = (spunDevices_.load(std::memory_order_acquire) & bit) != 0;
            if (!wasSpun && !spin && (!desktop || router_.Role(i) == DeviceRole::Other))
                continue;
            vr::DriverPose_t pose{};
            bool spinApplied = false;
            if (!stoppingForeign_.load(std::memory_order_acquire) && (desktop || spin) &&
                router_.RoutePose(i, r, true, std::max(now, QpcNow()), frequency_, pose, &spinApplied))
            {
                SubmitInternal(i, pose, r.epoch);
                if (spinApplied)
                {
                    spunDevices_.fetch_or(bit, std::memory_order_acq_rel);
                    router_.CountBodySpin(r.bodySpinGeneration);
                }
                else
                    spunDevices_.fetch_and(~bit, std::memory_order_acq_rel);
            }
            else if (wasSpun && router_.OriginalForRestore(i, std::max(now, QpcNow()), frequency_, pose))
            {
                // Lease release restores the captured original even without a new
                // vendor callback. Old captures are marked invalid, never fresh.
                SubmitInternal(i, pose, r.epoch);
                spunDevices_.fetch_and(~bit, std::memory_order_acq_rel);
            }
        }
    for (auto &c : components_)
    {
        if (!c.ready.load(std::memory_order_acquire))
            continue;
        auto role = router_.ContainerRole(c.container.load(std::memory_order_relaxed));
        auto kind = InputKind(c.kind.load(std::memory_order_relaxed));
        auto *inputSelf = c.inputSelf.load(std::memory_order_relaxed);
        if (!inputSelf)
            continue;
        if (role == DeviceRole::Head && kind == InputKind::Boolean &&
            Control(c.control.load(std::memory_order_relaxed)) == Control::Proximity)
        {
            if (desktop || previous)
                ForwardBoolean(
                    inputSelf, c.handle.load(std::memory_order_relaxed),
                    desktop || c.original.load(std::memory_order_relaxed) != 0, 0, desktop || previous);
            continue;
        }
        if (kind != InputKind::Boolean && kind != InputKind::Scalar)
            continue;
        if (role != DeviceRole::Left && role != DeviceRole::Right)
        {
            if (c.ownedOutput.exchange(false, std::memory_order_acq_rel))
            {
                c.releaseGate.store(false, std::memory_order_release);
                if (kind == InputKind::Boolean)
                    ForwardBoolean(inputSelf, c.handle.load(std::memory_order_relaxed), false, 0, true);
                else
                    ForwardScalar(
                        inputSelf, c.handle.load(std::memory_order_relaxed), 0, 0);
            }
            continue;
        }
        auto handle = c.handle.load(std::memory_order_relaxed);
        if (desktop)
        {
            c.releaseGate.store(true, std::memory_order_release);
            auto value = RouteValue(&c, 0, true, r);
            if (kind == InputKind::Boolean)
                ForwardBoolean(inputSelf, handle, value != 0, 0, true);
            else
                ForwardScalar(inputSelf, handle, value, 0);
        }
        else if (previous)
        {
            c.ownedOutput.store(false, std::memory_order_release);
            c.releaseGate.store(true, std::memory_order_release);
            if (kind == InputKind::Boolean)
                ForwardBoolean(inputSelf, handle, false, 0, true);
            else
                ForwardScalar(inputSelf, handle, 0, 0);
        }
    }
}
Status HookRuntime::StatusNow(int64_t now) noexcept
{
    Request r{};
    bool read = ReadRequest(r);
    auto status = router_.GetStatus(r, read, now, frequency_);
    bool left = false, right = false, menu = false, proximity = false;
    uint32_t proximityCount = 0;
    uint64_t rawProximity = 0;
    bool proximityAmbiguous = false;
    const auto selectedMenu = menuHandle_.load(std::memory_order_acquire);
    for (auto &c : components_)
    {
        if (!c.ready.load(std::memory_order_acquire))
            continue;
        auto role = router_.ContainerRole(c.container.load(std::memory_order_relaxed));
        auto control = Control(c.control.load(std::memory_order_relaxed));
        auto kind = InputKind(c.kind.load(std::memory_order_relaxed));
        if (role == DeviceRole::Left && (control == Control::StickX || control == Control::StickY))
            left = true;
        if (role == DeviceRole::Right && control == Control::Trigger)
            right = true;
        if ((role == DeviceRole::Left || role == DeviceRole::Right) && control == Control::Menu &&
            kind == InputKind::Boolean && c.handle.load(std::memory_order_relaxed) == selectedMenu)
            menu = true;
        if (role == DeviceRole::Head && control == Control::Proximity)
            proximity = true;
        if (role == DeviceRole::Left && control == Control::StickX && kind == InputKind::Scalar)
            status.inputCoverage |= LeftMoveXScalar;
        if (role == DeviceRole::Left && control == Control::StickY && kind == InputKind::Scalar)
            status.inputCoverage |= LeftMoveYScalar;
        if (role == DeviceRole::Right && control == Control::Trigger &&
            (kind == InputKind::Boolean || kind == InputKind::Scalar))
            status.inputCoverage |= RightTriggerAction;
        if (role == DeviceRole::Right && control == Control::Grip &&
            (kind == InputKind::Boolean || kind == InputKind::Scalar))
            status.inputCoverage |= RightGripAction;
        if (menu && c.handle.load(std::memory_order_relaxed) == selectedMenu)
            status.inputCoverage |= SelectedMenuClick;
        if (role == DeviceRole::Right && control == Control::Jump && kind == InputKind::Boolean)
            status.inputCoverage |= RightJumpClick;
        if (role == DeviceRole::Left && control == Control::Run && kind == InputKind::Boolean)
            status.inputCoverage |= LeftRunClick;
        if (role == DeviceRole::Head && control == Control::Proximity && kind == InputKind::Boolean)
        {
            status.inputCoverage |= HeadProximityBoolean;
            ++proximityCount;
            rawProximity = c.rawProximity.load(std::memory_order_acquire);
            proximityAmbiguous |= c.proximityIdentityAmbiguous.load(std::memory_order_acquire);
        }
    }
    if (proximityCount == 1 && !proximityAmbiguous && rawProximity)
    {
        auto stamp = int64_t(rawProximity >> 1);
        double age = (static_cast<double>(now) - stamp) * 1000 / frequency_;
        if (stamp > 0 && age >= -25)
        {
            status.proximityKnown = 1;
            status.proximityActive = uint32_t(rawProximity & 1);
            status.proximityAgeMilliseconds = std::max(age, 0.0);
        }
    }
    status.hasLeft = status.hasLeft && left;
    status.hasRight = status.hasRight && right;
    status.selectedMenuPath = menuPath_.load(std::memory_order_acquire);
    status.menuRisingEdges = menuRisingEdges_.load(std::memory_order_relaxed);
    status.menuPressed = menuPressed_.load(std::memory_order_acquire) ? 1u : 0u;
    status.lastInputError = lastInputError_.load(std::memory_order_acquire);
    status.inputArmed = status.actualMode && r.armed ? 1u : 0u;
    status.effectiveNativeActions = status.inputArmed ? r.actions : 0u;
    status.reserved0 =
        (poseReady_.load(std::memory_order_acquire) ? 1u | BodySpinCapability : 0u) |
        ((enabledMask_ & 0x1FEu) ? 2 : 0) |
        (left ? 4 : 0) | (right ? 8 : 0) | (menu ? 16 : 0) | (proximity ? 32 : 0);
    if (!CheckIntegrity())
    {
        status.actualMode = 0;
        status.error = uint32_t(Error::HookConflict);
        status.reserved0 &= ~(1u | BodySpinCapability);
        status.bodySpinActive = 0;
        status.proximityKnown = status.proximityActive = 0;
        status.proximityAgeMilliseconds = -1;
    }
    if (status.actualMode && (!status.hasLeft || !status.hasRight || !menu))
        status.error = uint32_t(Error::UnmappedControllers);
    if (!status.actualMode)
    {
        status.inputArmed = 0;
        status.effectiveNativeActions = 0;
    }
    return status;
}
void HookRuntime::PoseHook(void *self, uint32_t index, const vr::DriverPose_t &p,
                           uint32_t size) noexcept
{
    auto *s = instance_;
    Active guard{s->activeCalls_};
    s->RoutePoseCall(self, index, p, size);
}
vr::EVRInputError HookRuntime::CreateBoolHook(void *self, uint64_t c, const char *n,
                                              uint64_t *h) noexcept
{
    auto *s = instance_;
    Active guard{s->activeCalls_};
    auto e = reinterpret_cast<CreateBoolFn>(s->original_[1])(self, c, n, h);
    if (e == vr::VRInputError_None && h)
        s->Remember(*h, c, n, InputKind::Boolean, self);
    return e;
}
vr::EVRInputError HookRuntime::CreateScalarHook(void *self, uint64_t c, const char *n, uint64_t *h,
                                                vr::EVRScalarType t, vr::EVRScalarUnits u) noexcept
{
    auto *s = instance_;
    Active guard{s->activeCalls_};
    auto e = reinterpret_cast<CreateScalarFn>(s->original_[3])(self, c, n, h, t, u);
    if (e == vr::VRInputError_None && h)
        s->Remember(*h, c, n, InputKind::Scalar, self);
    return e;
}
vr::EVRInputError HookRuntime::UpdateBoolHook(void *self, uint64_t h, bool v,
                                              double offset) noexcept
{
    auto *s = instance_;
    Active guard{s->activeCalls_};
    Request r{};
    auto desktop = s->RequestNow(r, QpcNow());
    auto *c = s->Find(h);
    if (c)
    {
        c->original.store(v ? 1 : 0, std::memory_order_relaxed);
        if (Control(c->control.load(std::memory_order_relaxed)) == Control::Proximity &&
            c->inputSelf.load(std::memory_order_relaxed) == self &&
            !c->proximityIdentityAmbiguous.load(std::memory_order_acquire))
        {
            auto stamp = QpcNow();
            if (stamp > 0 && uint64_t(stamp) <= (UINT64_MAX >> 1))
                c->rawProximity.store((uint64_t(stamp) << 1) | (v ? 1u : 0u),
                                      std::memory_order_release);
        }
    }
    auto value = s->RouteValue(c, v ? 1.f : 0.f, desktop, r);
    return s->ForwardBoolean(self, h, value != 0, desktop ? 0 : offset, desktop);
}
vr::EVRInputError HookRuntime::UpdateScalarHook(void *self, uint64_t h, float v,
                                                double offset) noexcept
{
    auto *s = instance_;
    Active guard{s->activeCalls_};
    Request r{};
    auto desktop = s->RequestNow(r, QpcNow());
    auto *c = s->Find(h);
    auto value = s->RouteValue(c, v, desktop, r);
    return s->ForwardScalar(self, h, value, desktop ? 0 : offset);
}
vr::EVRInputError HookRuntime::CreateSkeletonHook(void *self, uint64_t c, const char *n,
                                                  const char *path, const char *base,
                                                  vr::EVRSkeletalTrackingLevel level,
                                                  const vr::VRBoneTransform_t *limits,
                                                  uint32_t count, uint64_t *h) noexcept
{
    auto *s = instance_;
    Active guard{s->activeCalls_};
    auto e = reinterpret_cast<CreateSkeletonFn>(s->original_[5])(self, c, n, path, base, level,
                                                                 limits, count, h);
    if (e == vr::VRInputError_None && h)
        s->Remember(*h, c, n, InputKind::Skeleton, self);
    return e;
}
vr::EVRInputError HookRuntime::UpdateSkeletonHook(void *self, uint64_t h,
                                                  vr::EVRSkeletalMotionRange range,
                                                  const vr::VRBoneTransform_t *bones,
                                                  uint32_t count) noexcept
{
    auto *s = instance_;
    Active guard{s->activeCalls_};
    Request r{};
    auto *c = s->Find(h);
    auto role = c ? s->router_.ContainerRole(c->container.load(std::memory_order_relaxed))
                  : DeviceRole::Other;
    if (s->RequestNow(r, QpcNow()) && (role == DeviceRole::Left || role == DeviceRole::Right))
        return vr::VRInputError_None;
    return reinterpret_cast<UpdateSkeletonFn>(s->original_[6])(self, h, range, bones, count);
}
vr::EVRInputError HookRuntime::CreatePoseHook(void *self, uint64_t c, const char *n,
                                              uint64_t *h) noexcept
{
    auto *s = instance_;
    Active guard{s->activeCalls_};
    auto e = reinterpret_cast<CreatePoseFn>(s->original_[7])(self, c, n, h);
    if (e == vr::VRInputError_None && h)
        s->Remember(*h, c, n, InputKind::Pose, self);
    return e;
}
vr::EVRInputError HookRuntime::UpdatePoseHook(void *self, uint64_t h, const vr::HmdMatrix34_t *p,
                                              double offset) noexcept
{
    auto *s = instance_;
    Active guard{s->activeCalls_};
    Request r{};
    auto *c = s->Find(h);
    auto role = c ? s->router_.ContainerRole(c->container.load(std::memory_order_relaxed))
                  : DeviceRole::Other;
    if (s->RequestNow(r, QpcNow()) && (role == DeviceRole::Left || role == DeviceRole::Right))
        return vr::VRInputError_None;
    return reinterpret_cast<UpdatePoseFn>(s->original_[8])(self, h, p, offset);
}
} // namespace sw
