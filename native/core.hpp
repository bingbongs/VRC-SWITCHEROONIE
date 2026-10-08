#pragma once
#include "openvr_driver.h"
#include "protocol.hpp"
#include <array>
#include <atomic>
#include <string_view>
namespace sw
{
enum class DeviceRole : uint32_t
{
    Other,
    Head,
    Left,
    Right
};
enum class InputKind : uint32_t
{
    Boolean,
    Scalar,
    Skeleton,
    Pose
};
enum class Control : uint32_t
{
    Unknown,
    Trigger,
    Grip,
    Menu,
    StickX,
    StickY,
    Jump,
    Run,
    Touch,
    Proximity
};
struct PoseSnapshot
{
    vr::DriverPose_t pose{};
    int64_t timestamp{};
    uint64_t epoch{};
};
class PoseStore
{
  public:
    bool Write(const PoseSnapshot &) noexcept;
    bool Read(PoseSnapshot &) const noexcept;

  private:
    std::atomic_flag writer_ = ATOMIC_FLAG_INIT;
    std::atomic<uint64_t> sequence_{};
    std::array<std::atomic<uint64_t>, (sizeof(PoseSnapshot) + 7) / 8> payload_{};
};
class Router
{
  public:
    void SetRole(uint32_t index, DeviceRole role, uint64_t container = 0) noexcept;
    void SetBodySpinEligible(uint32_t index, bool eligible) noexcept;
    bool BodySpinEligible(uint32_t index) const noexcept;
    DeviceRole Role(uint32_t index) const noexcept;
    DeviceRole ContainerRole(uint64_t container) const noexcept;
    void Capture(uint32_t index, const vr::DriverPose_t &original, int64_t now) noexcept;
    bool RoutePose(uint32_t index, const Request &, bool requestRead, int64_t now,
                   int64_t frequency, vr::DriverPose_t &output, bool *spinApplied = nullptr) noexcept;
    static bool BodySpinActive(const Request &, bool requestRead, int64_t now, int64_t frequency) noexcept;
    bool BodySpinPermitted(const Request &, bool requestRead, int64_t now, int64_t frequency) const noexcept;
    bool OriginalForRestore(uint32_t index, int64_t now, int64_t frequency,
                            vr::DriverPose_t &output) const noexcept;
    bool Desktop(const Request &, bool requestRead, int64_t now, int64_t frequency) noexcept;
    Status GetStatus(const Request &, bool requestRead, int64_t now, int64_t frequency) noexcept;
    bool Physical(uint32_t index, PoseSnapshot &out) const noexcept;
    void CountRouted() noexcept
    {
        routed_.fetch_add(1, std::memory_order_relaxed);
    }
    void CountBodySpin(uint64_t generation) noexcept
    {
        bodySpinGeneration_.store(generation, std::memory_order_release);
        bodySpinSamples_.fetch_add(1, std::memory_order_relaxed);
    }
    void RecordSynthetic(uint32_t index, const vr::DriverPose_t &pose, uint64_t epoch,
                          int64_t now) noexcept;
    static bool ValidRequest(const Request &, int64_t now, int64_t frequency, Error &) noexcept;

  private:
    bool Evaluate(const Request &, bool, int64_t, int64_t) noexcept;
    bool Synthetic(DeviceRole, const PoseSnapshot &, const PoseSnapshot &, const Request &,
                   vr::DriverPose_t &) noexcept;
    std::array<std::atomic<DeviceRole>, vr::k_unMaxTrackedDeviceCount> roles_{};
    std::array<std::atomic<uint64_t>, vr::k_unMaxTrackedDeviceCount> containers_{};
    std::array<std::atomic<uint64_t>, vr::k_unMaxTrackedDeviceCount> bodySpinContainers_{};
    std::array<PoseStore, vr::k_unMaxTrackedDeviceCount> poses_{};
    std::array<std::atomic<bool>, vr::k_unMaxTrackedDeviceCount> poseValid_{};
    std::atomic<int64_t> headTimestamp_{};
    std::atomic<bool> headValid_{};
    std::atomic_flag entry_ = ATOMIC_FLAG_INIT;
    PoseStore anchor_{};
    PoseStore syntheticHead_{};
    std::atomic<uint64_t> anchorEpoch_{};
    std::atomic<bool> anchorValid_{};
    std::atomic<bool> desktop_{};
    std::atomic<uint64_t> epoch_{};
    std::atomic<Error> error_{Error::NoHead};
    std::atomic<uint64_t> routed_{}, physical_{};
    std::atomic<uint64_t> bodySpinGeneration_{}, bodySpinSamples_{};
};
Control ClassifyControl(const char *name) noexcept;
float DesiredValue(Control, DeviceRole, const Request &) noexcept;
} // namespace sw
