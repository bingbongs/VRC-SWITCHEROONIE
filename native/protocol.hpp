#pragma once
#include <atomic>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <type_traits>

namespace sw
{
inline constexpr uint32_t Magic = 0x53575243;
inline constexpr uint32_t Version = 1;
inline constexpr size_t MappingBytes = 4096, StatusOffset = 2048, BlockBytes = 512;
inline constexpr int WatchdogMilliseconds = 200;
enum Action : uint32_t
{
    Trigger = 1u,
    Grip = 2u,
    Menu = 4u,
    Jump = 8u,
    Run = 16u,
    Crouch = 32u,
    Prone = 64u
};
inline constexpr uint32_t AllowedActions = Trigger | Grip | Menu | Jump | Run | Crouch | Prone;
enum class Error : uint32_t
{
    None,
    NoHead,
    StalePhysical,
    StaleRequest,
    InvalidRequest,
    HookConflict,
    RuntimeMismatch,
    OptInDisabled,
    IpcUnavailable,
    UnmappedControllers,
    StartupConfigFailure
};
enum InputCoverage : uint32_t
{
    LeftMoveXScalar = 1u,
    LeftMoveYScalar = 2u,
    RightTriggerAction = 4u,
    RightGripAction = 8u,
    SelectedMenuClick = 16u,
    RightJumpClick = 32u,
    LeftRunClick = 64u,
    HeadProximityBoolean = 128u
};
enum class MenuPath : uint32_t
{
    None,
    LeftY,
    LeftB,
    RightB,
    LeftApplicationMenu
};
enum class HandPreset : uint32_t
{
    Rest = 0,
    HeadLookRest = 1,
    RightPointer = 2,
    LeftMenuNavigation = 3
};
inline constexpr uint32_t BodySpinCapability = 64u;
inline constexpr uint32_t GenericTrackerSuspensionCapability = 128u;
enum class BodySpinBlockReason : uint32_t
{
    None,
    StalePhysicalRig,
    DesktopRigIdentityChanged,
    NativeAdmissionFault
};
struct alignas(8) Request
{
    uint64_t sequence{};
    uint32_t magic{Magic}, version{Version};
    int64_t timestamp{};
    uint64_t epoch{};
    uint32_t requestedMode{}, armed{};
    double height{}, yaw{}, pitch{}, handYaw{}, handPitch{};
    uint32_t handPreset{}, actions{};
    double forward{}, strafe{};
    // OSC supervisor extension is broker-owned and is not interpreted by native routing.
    uint32_t oscEnabled{}, oscPort{};
    double oscForward{}, oscStrafe{};
    uint32_t oscActions{}, oscReserved{};
    uint32_t bodySpinActive{}, bodySpinReserved{};
    int64_t bodySpinLeaseQpc{};
    double bodySpinQuaternion[4]{}, bodySpinPivot[3]{};
    uint64_t bodySpinGeneration{};
    std::byte reserved[296]{};
};
struct alignas(8) Status
{
    uint64_t sequence{};
    uint32_t magic{Magic}, version{Version};
    int64_t timestamp{};
    uint64_t ackEpoch{};
    uint32_t actualMode{}, error{};
    double headAgeMilliseconds{};
    uint32_t hasHead{}, hasLeft{}, hasRight{}, reserved0{};
    double position[3]{};
    double quaternion[4]{};
    uint64_t routedSamples{}, physicalSamples{};
    uint64_t anchorEpoch{};
    uint32_t syntheticHeadValid{}, reserved1{};
    uint64_t syntheticHeadEpoch{};
    double syntheticPosition[3]{}, syntheticQuaternion[4]{};
    int64_t syntheticHeadQpc{};
    uint32_t inputCoverage{}, selectedMenuPath{};
    uint64_t menuRisingEdges{};
    uint32_t menuPressed{}, lastInputError{}, effectiveNativeActions{}, inputArmed{};
    uint32_t proximityKnown{}, proximityActive{};
    double leftPhysicalAgeMilliseconds{-1}, rightPhysicalAgeMilliseconds{-1};
    double proximityAgeMilliseconds{-1};
    uint32_t bodySpinActive{}, bodySpinBlockReason{};
    uint64_t bodySpinGeneration{}, bodySpinSamples{};
    uint32_t genericTrackerAvailable{}, genericTrackerSuspended{};
    uint64_t bodySpinAttemptGeneration{};
    std::byte reserved[184]{};
};
static_assert(sizeof(Request) == 512 && sizeof(Status) == 512);
static_assert(offsetof(Request, forward) == 88 && offsetof(Status, position) == 64 &&
              offsetof(Status, physicalSamples) == 128);
static_assert(offsetof(Status, anchorEpoch) == 136 &&
              offsetof(Status, syntheticPosition) == 160 &&
              offsetof(Status, syntheticHeadQpc) == 216);
static_assert(offsetof(Status, inputCoverage) == 224 &&
              offsetof(Status, selectedMenuPath) == 228 &&
              offsetof(Status, menuRisingEdges) == 232 && offsetof(Status, menuPressed) == 240 &&
              offsetof(Status, lastInputError) == 244 &&
              offsetof(Status, effectiveNativeActions) == 248 &&
              offsetof(Status, inputArmed) == 252);
static_assert(offsetof(Status, proximityKnown) == 256 &&
              offsetof(Status, proximityActive) == 260 &&
              offsetof(Status, leftPhysicalAgeMilliseconds) == 264 &&
              offsetof(Status, rightPhysicalAgeMilliseconds) == 272 &&
              offsetof(Status, proximityAgeMilliseconds) == 280);
static_assert(offsetof(Request, oscEnabled) == 104 && offsetof(Request, oscActions) == 128 &&
              offsetof(Request, bodySpinActive) == 136 && offsetof(Request, bodySpinReserved) == 140 &&
              offsetof(Request, bodySpinLeaseQpc) == 144 &&
              offsetof(Request, bodySpinQuaternion) == 152 && offsetof(Request, bodySpinPivot) == 184 &&
              offsetof(Request, bodySpinGeneration) == 208);
static_assert(offsetof(Status, bodySpinActive) == 288 &&
              offsetof(Status, bodySpinBlockReason) == 292 &&
              offsetof(Status, bodySpinGeneration) == 296 && offsetof(Status, bodySpinSamples) == 304);
static_assert(offsetof(Status, genericTrackerAvailable) == 312 &&
              offsetof(Status, genericTrackerSuspended) == 316);
static_assert(offsetof(Status, bodySpinAttemptGeneration) == 320);
static_assert(std::atomic_ref<uint64_t>::is_always_lock_free);

// Interprocess protocol consists exclusively of aligned, atomic 64-bit words.
// The writer publishes odd sequence, payload, even sequence; readers are bounded.
template <class T> bool ReadBlock(const void *memory, T &out) noexcept
{
    static_assert(std::is_trivially_copyable_v<T> && sizeof(T) % 8 == 0);
    if (!memory)
        return false;
    auto words = static_cast<uint64_t *>(const_cast<void *>(memory));
    std::atomic_ref<uint64_t> sequence(words[0]);
    for (int retry = 0; retry < 3; ++retry)
    {
        const auto before = sequence.load(std::memory_order_acquire);
        if (before & 1)
            continue;
        uint64_t copy[sizeof(T) / 8]{};
        copy[0] = before;
        for (size_t i = 1; i < sizeof(T) / 8; ++i)
            copy[i] = std::atomic_ref<uint64_t>(words[i]).load(std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_acquire);
        if (before == sequence.load(std::memory_order_acquire))
        {
            std::memcpy(&out, copy, sizeof(T));
            return true;
        }
    }
    return false;
}
template <class T> void WriteBlock(void *memory, const T &value) noexcept
{
    auto words = static_cast<uint64_t *>(memory);
    std::atomic_ref<uint64_t> sequence(words[0]);
    auto previous = sequence.load(std::memory_order_relaxed) & ~uint64_t(1);
    sequence.store(previous + 1, std::memory_order_seq_cst);
    uint64_t payload[sizeof(T) / 8];
    std::memcpy(payload, &value, sizeof(T));
    for (size_t i = 1; i < sizeof(T) / 8; ++i)
        std::atomic_ref<uint64_t>(words[i]).store(payload[i], std::memory_order_relaxed);
    sequence.store(previous + 2, std::memory_order_release);
}
} // namespace sw
