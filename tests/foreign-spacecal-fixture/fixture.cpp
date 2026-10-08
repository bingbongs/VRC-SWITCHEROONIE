#include "fixture_api.hpp"
#include "MinHook.h"
#include <array>
#include <atomic>
#include <cstring>

namespace
{
using PoseFn = void (*)(void *, uint32_t, const vr::DriverPose_t &, uint32_t);
// Intentionally a module-global RIP-relative indirect call, matching the reviewed
// SpaceCalibrator 1.5 source's Hook::originalFunc field usage. Not an actual SC DLL.
PoseFn OriginalPose{};
void *PoseTarget{};
std::array<vr::DriverPose_t, vr::k_unMaxTrackedDeviceCount> Raw{}, Calibrated{};
std::array<bool, vr::k_unMaxTrackedDeviceCount> Present{};
SRWLOCK StoreLock = SRWLOCK_INIT;
std::atomic<HANDLE> BarrierReached{}, BarrierResume{};
std::atomic<uint32_t> RawCalls{}, CleanupCalls{}, InFlight{};

__declspec(noinline) void DetourPose(void *host, uint32_t index,
                                     const vr::DriverPose_t &incoming, uint32_t size)
{
    InFlight.fetch_add(1, std::memory_order_acq_rel);
    auto pose = incoming;
    if (index < Raw.size() && size == sizeof(pose))
    {
        // Capture independently before changing the world transform. The real SC
        // shared memory feed occupies this same position in its pipeline.
        AcquireSRWLockExclusive(&StoreLock);
        Raw[index] = pose;
        pose.vecWorldFromDriverTranslation[0] += 10;
        Calibrated[index] = pose;
        Present[index] = true;
        ReleaseSRWLockExclusive(&StoreLock);
        RawCalls.fetch_add(1, std::memory_order_relaxed);
    }
    auto reached = BarrierReached.load(std::memory_order_acquire);
    auto resume = BarrierResume.load(std::memory_order_acquire);
    if (reached && resume)
    {
        SetEvent(reached);
        WaitForSingleObject(resume, 5000);
    }
    OriginalPose(host, index, pose, size);
    InFlight.fetch_sub(1, std::memory_order_release);
}

class Provider final : public vr::IServerTrackedDeviceProvider
{
  public:
    vr::EVRInitError Init(vr::IVRDriverContext *) override { return vr::VRInitError_None; }
    __declspec(noinline) void Cleanup() override
    {
        CleanupCalls.fetch_add(1, std::memory_order_relaxed);
        if (PoseTarget)
        {
            MH_RemoveHook(PoseTarget);
            MH_Uninitialize();
            PoseTarget = nullptr;
            OriginalPose = nullptr;
        }
    }
    const char *const *GetInterfaceVersions() override { return vr::k_InterfaceVersions; }
    void RunFrame() override {}
    bool ShouldBlockStandbyMode() override { return false; }
    void EnterStandby() override {}
    void LeaveStandby() override {}
} FixtureProvider;
}

extern "C" __declspec(dllexport) BOOL FixtureInstall(void *target)
{
    if (PoseTarget || !target || MH_Initialize() != MH_OK)
        return FALSE;
    if (MH_CreateHook(target, reinterpret_cast<void *>(DetourPose),
                      reinterpret_cast<void **>(&OriginalPose)) != MH_OK ||
        MH_EnableHook(target) != MH_OK)
    {
        MH_Uninitialize();
        OriginalPose = nullptr;
        return FALSE;
    }
    PoseTarget = target;
    return TRUE;
}
extern "C" __declspec(dllexport) void *HmdDriverFactory(const char *version, int *error)
{
    const bool supported = version && std::strcmp(version, vr::IServerTrackedDeviceProvider_Version) == 0;
    if (error)
        *error = supported ? vr::VRInitError_None : vr::VRInitError_Init_InterfaceNotFound;
    return supported ? &FixtureProvider : nullptr;
}
extern "C" __declspec(dllexport) BOOL FixtureReadRaw(uint32_t index, vr::DriverPose_t *out)
{
    if (!out || index >= Raw.size())
        return FALSE;
    AcquireSRWLockShared(&StoreLock);
    const bool present = Present[index];
    if (present)
        *out = Raw[index];
    ReleaseSRWLockShared(&StoreLock);
    return present;
}
extern "C" __declspec(dllexport) BOOL FixtureReadCalibrated(uint32_t index, vr::DriverPose_t *out)
{
    if (!out || index >= Calibrated.size())
        return FALSE;
    AcquireSRWLockShared(&StoreLock);
    const bool present = Present[index];
    if (present)
        *out = Calibrated[index];
    ReleaseSRWLockShared(&StoreLock);
    return present;
}
extern "C" __declspec(dllexport) void FixtureSetBarrier(HANDLE reached, HANDLE resume)
{
    BarrierResume.store(resume, std::memory_order_release);
    BarrierReached.store(reached, std::memory_order_release);
}
extern "C" __declspec(dllexport) void *FixtureOriginal()
{
    return reinterpret_cast<void *>(OriginalPose);
}
extern "C" __declspec(dllexport) uint32_t FixtureRawCalls() { return RawCalls.load(); }
extern "C" __declspec(dllexport) uint32_t FixtureCleanupCalls() { return CleanupCalls.load(); }
extern "C" __declspec(dllexport) uint32_t FixtureInFlight() { return InFlight.load(); }
