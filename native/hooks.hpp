#pragma once
#include "core.hpp"
#include "known_hook.hpp"
#include <array>
#include <atomic>
#include <windows.h>
#include <mutex>
namespace sw
{
static_assert(vr::k_unMaxTrackedDeviceCount <= 64, "Body restore mask requires at most64 tracked devices.");
class HookRuntime
{
  public:
    HookRuntime(Router &router, void *requestMemory)
        : router_(router), requestMemory_(requestMemory)
    {
    }
    bool Install(vr::IVRServerDriverHost *, vr::IVRDriverInput *, bool enforceRuntimeOwner = true,
                 const PoseChainApproval *isolatedFixture = nullptr);
    bool Remove();
    void Tick(int64_t now) noexcept;
    Status StatusNow(int64_t now) noexcept;
    void Submit(uint32_t, const vr::DriverPose_t &) noexcept;
    const char *failure() const noexcept
    {
        return failure_.load(std::memory_order_acquire);
    }
    bool CheckIntegrity() const noexcept;
    bool ChainedPose() const noexcept { return chainedPose_; }
    bool PoseReady() const noexcept { return poseReady_.load(std::memory_order_acquire); }

  private:
    struct Component
    {
        std::atomic<uint64_t> handle{}, container{};
        std::atomic<void *> inputSelf{};
        std::atomic<uint32_t> ready{}, kind{}, control{}, menuSource{};
        std::atomic<uint64_t> original{};
        // One atomic word publishes source QPC plus raw Boolean, independently
        // of synthetic writes. Zero means no observed original source update.
        std::atomic<uint64_t> rawProximity{};
        std::atomic<uint32_t> proximityPath{};
        std::atomic<bool> proximityIdentityAmbiguous{};
        std::atomic<bool> releaseGate{};
        std::atomic<bool> ownedOutput{};
    };
    Component *Find(uint64_t) noexcept;
    void Remember(uint64_t, uint64_t, const char *, InputKind, void *) noexcept;
    bool RequestNow(Request &, int64_t now) noexcept;
    bool ReadRequest(Request &) noexcept;
    float RouteValue(Component *, float, bool desktop, const Request &) noexcept;
    vr::EVRInputError ForwardBoolean(void *, uint64_t, bool, double, bool) noexcept;
    vr::EVRInputError ForwardScalar(void *, uint64_t, float, double) noexcept;
    void RoutePoseCall(void *, uint32_t, const vr::DriverPose_t &, uint32_t) noexcept;
    bool InstallPose(bool enforceOwner, const PoseChainApproval *isolatedFixture);
    void TryDeferredPose(int64_t now) noexcept;
    void TickInternal(int64_t now) noexcept;
    void SubmitInternal(uint32_t, const vr::DriverPose_t &, uint64_t epoch) noexcept;
    bool RetainForShutdown(const char *) noexcept;
    using PoseFn = void (*)(void *, uint32_t, const vr::DriverPose_t &, uint32_t);
    using CreateBoolFn = vr::EVRInputError (*)(void *, uint64_t, const char *, uint64_t *);
    using UpdateBoolFn = vr::EVRInputError (*)(void *, uint64_t, bool, double);
    using CreateScalarFn = vr::EVRInputError (*)(void *, uint64_t, const char *, uint64_t *,
                                                 vr::EVRScalarType, vr::EVRScalarUnits);
    using UpdateScalarFn = vr::EVRInputError (*)(void *, uint64_t, float, double);
    using CreateSkeletonFn = vr::EVRInputError (*)(void *, uint64_t, const char *, const char *,
                                                   const char *, vr::EVRSkeletalTrackingLevel,
                                                   const vr::VRBoneTransform_t *, uint32_t,
                                                   uint64_t *);
    using UpdateSkeletonFn = vr::EVRInputError (*)(void *, uint64_t, vr::EVRSkeletalMotionRange,
                                                   const vr::VRBoneTransform_t *, uint32_t);
    using CreatePoseFn = vr::EVRInputError (*)(void *, uint64_t, const char *, uint64_t *);
    using UpdatePoseFn = vr::EVRInputError (*)(void *, uint64_t, const vr::HmdMatrix34_t *, double);
    static void PoseHook(void *, uint32_t, const vr::DriverPose_t &, uint32_t) noexcept;
    static void ForeignCleanupHook(void *) noexcept;
    static void ForeignPoseGuardHook(void *, uint32_t, const vr::DriverPose_t &, uint32_t) noexcept;
    static vr::EVRInputError CreateBoolHook(void *, uint64_t, const char *, uint64_t *) noexcept;
    static vr::EVRInputError UpdateBoolHook(void *, uint64_t, bool, double) noexcept;
    static vr::EVRInputError CreateScalarHook(void *, uint64_t, const char *, uint64_t *,
                                              vr::EVRScalarType, vr::EVRScalarUnits) noexcept;
    static vr::EVRInputError UpdateScalarHook(void *, uint64_t, float, double) noexcept;
    static vr::EVRInputError CreateSkeletonHook(void *, uint64_t, const char *, const char *,
                                                const char *, vr::EVRSkeletalTrackingLevel,
                                                const vr::VRBoneTransform_t *, uint32_t,
                                                uint64_t *) noexcept;
    static vr::EVRInputError UpdateSkeletonHook(void *, uint64_t, vr::EVRSkeletalMotionRange,
                                                const vr::VRBoneTransform_t *, uint32_t) noexcept;
    static vr::EVRInputError CreatePoseHook(void *, uint64_t, const char *, uint64_t *) noexcept;
    static vr::EVRInputError UpdatePoseHook(void *, uint64_t, const vr::HmdMatrix34_t *,
                                            double) noexcept;
    static HookRuntime *instance_;
    Router &router_;
    void *requestMemory_{};
    int64_t frequency_{};
    std::array<Component, 2048> components_{};
    std::array<std::atomic<void *>, vr::k_unMaxTrackedDeviceCount> poseHosts_{};
    std::atomic<uint64_t> spunDevices_{};
    std::atomic<bool> running_{}, wasDesktop_{};
    mutable std::atomic<uint32_t> activeCalls_{};
    std::atomic<uint64_t> menuHandle_{};
    std::atomic<uint32_t> menuPath_{};
    std::atomic<uint64_t> menuRisingEdges_{};
    std::atomic<bool> menuPressed_{};
    std::atomic<uint32_t> lastInputError_{};
    alignas(8) std::array<std::byte, 512> cachedRequest_{};
    std::atomic_flag cacheWriter_ = ATOMIC_FLAG_INIT;
    std::atomic<uint64_t> cachedExternalSequence_{};
    std::array<void *, 11> targets_{};
    std::array<std::array<unsigned char, 5>, 11> patched_{};
    std::array<std::array<unsigned char, 5>, 11> prepared_{};
    std::array<void *, 11> original_{};
    bool chainedPose_{};
    bool deferredPose_{}, enforceOwner_{};
    bool unownedDuringInstall_{};
    void *runtimePoseEntry_{};
    int64_t installTime_{};
    std::atomic<uint32_t> createdMask_{}, enabledMask_{};
    std::atomic<bool> poseReady_{}, poseInstallFailed_{};
    std::atomic<bool> foreignCleanupObserved_{};
    std::atomic<bool> stoppingForeign_{};
    std::mutex removeMutex_;
    std::atomic<size_t> created_{};
    bool initialized_{};
    bool enabled_{};
    std::atomic<const char *> failure_{"none"};
};
} // namespace sw
