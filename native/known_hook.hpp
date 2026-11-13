#pragma once
#include <windows.h>
namespace sw
{
// Experimental local binary approval, never a generic endorsement of foreign detours.
inline constexpr char ApprovedSpaceCalibratorSha256[] =
    "17b8510ee9cfc3cd017deb5a40ab6ef2ac5e5e1412f0f00038788fa6b0898314";
struct PoseChainApproval
{
    HMODULE module{};
    void *cleanupTarget{};
    void *innerTrampoline{};
    void *detourTarget{};
};
bool ResolveMinHookDestination(void *entry, void *&destination) noexcept;
bool ResolveOriginalPoseTrampoline(void *entry, HMODULE owner, void *&trampoline) noexcept;
bool ApproveSpaceCalibrator(void *entry, PoseChainApproval &approval);
} // namespace sw
