#include "core.hpp"
#include <algorithm>
#include <cmath>
#include <cstring>
namespace sw
{
namespace
{
using Q = vr::HmdQuaternion_t;
struct V
{
    double x, y, z;
};
Q Mul(Q a, Q b)
{
    return {a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w};
}
Q Conj(Q q)
{
    return {q.w, -q.x, -q.y, -q.z};
}
Q Normalize(Q q)
{
    auto n = std::sqrt(q.w * q.w + q.x * q.x + q.y * q.y + q.z * q.z);
    return {q.w / n, q.x / n, q.y / n, q.z / n};
}
V Rotate(Q q, V v)
{
    auto r = Mul(Mul(q, {0, v.x, v.y, v.z}), Conj(q));
    return {r.x, r.y, r.z};
}
Q YawPitch(double yaw, double pitch)
{
    auto a = yaw / 2, b = pitch / 2;
    return Mul({std::cos(a), 0, std::sin(a), 0}, {std::cos(b), std::sin(b), 0, 0});
}
bool ValidQ(Q q)
{
    auto n = q.w * q.w + q.x * q.x + q.y * q.y + q.z * q.z;
    return std::isfinite(n) && n > .8 && n < 1.2;
}
bool ValidPose(const vr::DriverPose_t &p)
{
    if (!p.poseIsValid || !p.deviceIsConnected || !ValidQ(p.qRotation) ||
        !ValidQ(p.qWorldFromDriverRotation) || !ValidQ(p.qDriverFromHeadRotation))
        return false;
    for (int i = 0; i < 3; ++i)
        if (!std::isfinite(p.vecPosition[i]) ||
            !std::isfinite(p.vecWorldFromDriverTranslation[i]) ||
            !std::isfinite(p.vecDriverFromHeadTranslation[i]) || std::abs(p.vecPosition[i]) > 10000)
            return false;
    return true;
}
V WorldPosition(const vr::DriverPose_t &p)
{
    auto local =
        Rotate(p.qRotation, {p.vecDriverFromHeadTranslation[0], p.vecDriverFromHeadTranslation[1],
                             p.vecDriverFromHeadTranslation[2]});
    auto rotated =
        Rotate(p.qWorldFromDriverRotation, {p.vecPosition[0] + local.x, p.vecPosition[1] + local.y,
                                            p.vecPosition[2] + local.z});
    return {rotated.x + p.vecWorldFromDriverTranslation[0],
            rotated.y + p.vecWorldFromDriverTranslation[1],
            rotated.z + p.vecWorldFromDriverTranslation[2]};
}
Q WorldRotation(const vr::DriverPose_t &p)
{
    return Normalize(Mul(Mul(p.qWorldFromDriverRotation, p.qRotation), p.qDriverFromHeadRotation));
}
struct Unlock
{
    std::atomic_flag &flag;
    ~Unlock()
    {
        flag.clear(std::memory_order_release);
    }
};
} // namespace
bool PoseStore::Write(const PoseSnapshot &pose, void (*publish)(void *) noexcept, void *context) noexcept
{
    if (writer_.test_and_set(std::memory_order_acquire))
        return false;
    Unlock guard{writer_};
    auto n = sequence_.load(std::memory_order_relaxed);
    sequence_.store(n + 1, std::memory_order_seq_cst);
    uint64_t words[(sizeof(PoseSnapshot) + 7) / 8]{};
    std::memcpy(words, &pose, sizeof(pose));
    for (size_t i = 0; i < payload_.size(); ++i)
        payload_[i].store(words[i], std::memory_order_relaxed);
    if (publish) publish(context);
    sequence_.store(n + 2, std::memory_order_release);
    return true;
}
bool PoseStore::Read(PoseSnapshot &out) const noexcept
{
    for (int attempt = 0; attempt < 3; ++attempt)
    {
        auto before = sequence_.load(std::memory_order_acquire);
        if (before & 1)
            continue;
        uint64_t words[(sizeof(PoseSnapshot) + 7) / 8]{};
        for (size_t i = 0; i < payload_.size(); ++i)
            words[i] = payload_[i].load(std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_acquire);
        if (before == sequence_.load(std::memory_order_acquire))
        {
            std::memcpy(&out, words, sizeof(out));
            return true;
        }
    }
    return false;
}
void Router::SetRole(uint32_t index, DeviceRole role, uint64_t container) noexcept
{
    if (index >= roles_.size())
        return;
    const auto previousRole = Role(index);
    const bool changedContainer = containers_[index].load(std::memory_order_acquire) != container;
    if ((previousRole != role || changedContainer) && BodySpinEligible(index))
        RetireCurrentSpin(GenericTracker(index) &&
                         spinDesktopGeneration_.load(std::memory_order_acquire) ==
                             spinAdmittedGeneration_.load(std::memory_order_acquire)
                             ? BodySpinBlockReason::DesktopRigIdentityChanged
                             : BodySpinBlockReason::StalePhysicalRig);
    if (changedContainer)
    {
        poseValid_[index].store(false, std::memory_order_release);
        capturedContainers_[index].store(0, std::memory_order_release);
        captureTimestamps_[index].store(0, std::memory_order_release);
    }
    if (changedContainer && (previousRole == DeviceRole::Head || role == DeviceRole::Head))
    {
        headValid_.store(false, std::memory_order_release);
        headContainer_.store(0, std::memory_order_release);
        headTimestamp_.store(0, std::memory_order_release);
    }
    if (previousRole != role || changedContainer)
    {
        captureInvalidVersions_[index].fetch_add(1, std::memory_order_acq_rel);
        bodySpinContainers_[index].store(0, std::memory_order_release);
        genericTrackerContainers_[index].store(0, std::memory_order_release);
    }
    containers_[index].store(container, std::memory_order_release);
    roles_[index].store(role, std::memory_order_release);
}
void Router::SetBodySpinEligible(uint32_t index, bool eligible) noexcept
{
    if (index < bodySpinContainers_.size())
    {
        if (eligible != BodySpinEligible(index))
            RetireCurrentSpin(BodySpinBlockReason::StalePhysicalRig);
        bodySpinContainers_[index].store(eligible ? containers_[index].load(std::memory_order_acquire) : 0,
                                        std::memory_order_release);
    }
}
bool Router::BodySpinEligible(uint32_t index) const noexcept
{
    if (index >= bodySpinContainers_.size()) return false;
    auto container = containers_[index].load(std::memory_order_acquire);
    return container && bodySpinContainers_[index].load(std::memory_order_acquire) == container;
}
void Router::SetGenericTracker(uint32_t index, bool eligible) noexcept
{
    if (index < genericTrackerContainers_.size())
    {
        if (eligible != GenericTracker(index))
            RetireCurrentSpin(spinDesktopGeneration_.load(std::memory_order_acquire) ==
                                      spinAdmittedGeneration_.load(std::memory_order_acquire)
                                  ? BodySpinBlockReason::DesktopRigIdentityChanged
                                  : BodySpinBlockReason::StalePhysicalRig);
        genericTrackerContainers_[index].store(
            eligible && Role(index) == DeviceRole::Other
                ? containers_[index].load(std::memory_order_acquire) : 0,
            std::memory_order_release);
    }
}
bool Router::GenericTracker(uint32_t index) const noexcept
{
    if (index >= genericTrackerContainers_.size() || Role(index) != DeviceRole::Other)
        return false;
    const auto container = containers_[index].load(std::memory_order_acquire);
    return container && genericTrackerContainers_[index].load(std::memory_order_acquire) == container;
}
bool Router::GenericTrackerPhysical(uint32_t index, PoseSnapshot &source) const noexcept
{
    return GenericTracker(index) && Physical(index, source) && source.container &&
           source.container == containers_[index].load(std::memory_order_acquire) && GenericTracker(index);
}
DeviceRole Router::Role(uint32_t index) const noexcept
{
    return index < roles_.size() ? roles_[index].load(std::memory_order_acquire)
                                 : DeviceRole::Other;
}
DeviceRole Router::ContainerRole(uint64_t container) const noexcept
{
    if (!container)
        return DeviceRole::Other;
    for (size_t i = 0; i < roles_.size(); ++i)
        if (containers_[i].load(std::memory_order_acquire) == container)
            return roles_[i].load(std::memory_order_acquire);
    return DeviceRole::Other;
}
void Router::Capture(uint32_t index, const vr::DriverPose_t &original, int64_t now) noexcept
{
    if (index >= poses_.size())
        return;
    const auto observedInvalidVersion = captureInvalidVersions_[index].load(std::memory_order_acquire);
    const auto container = containers_[index].load(std::memory_order_acquire);
    const bool valid = ValidPose(original);
    const auto invalidVersion = valid ? observedInvalidVersion
        : captureInvalidVersions_[index].fetch_add(1, std::memory_order_acq_rel) + 1;
    if (!valid && BodySpinEligible(index) &&
        container == containers_[index].load(std::memory_order_acquire))
        RetireCurrentSpin(BodySpinBlockReason::StalePhysicalRig);
    struct Publication
    {
        Router *router; uint32_t index; uint64_t container, invalidVersion; int64_t now; bool valid;
    } publication{this, index, container, invalidVersion, now, valid};
    // The pose and its metadata share one writer ownership. A second callback
    // cannot publish an invalid pose and then have an earlier writer's cache
    // overwrite it. Invalid events also invalidate admission on a writer miss.
    poses_[index].Write({original, now, 0, container}, [](void *context) noexcept {
        auto &p = *static_cast<Publication *>(context);
        auto &router = *p.router;
        router.capturedContainers_[p.index].store(p.container, std::memory_order_release);
        router.captureTimestamps_[p.index].store(p.now, std::memory_order_release);
        router.poseValid_[p.index].store(p.valid, std::memory_order_release);
        if (router.Role(p.index) == DeviceRole::Head)
        {
            router.headContainer_.store(p.container, std::memory_order_release);
            router.headValid_.store(p.valid, std::memory_order_release);
            router.headTimestamp_.store(p.now, std::memory_order_release);
        }
        router.capturedInvalidVersions_[p.index].store(p.invalidVersion, std::memory_order_release);
    }, &publication);
    if (Role(index) == DeviceRole::Head) physical_.fetch_add(1, std::memory_order_relaxed);
}
bool Router::Physical(uint32_t index, PoseSnapshot &out) const noexcept
{
    return index < poses_.size() && poses_[index].Read(out) && out.timestamp > 0;
}
void Router::RecordSynthetic(uint32_t index, const vr::DriverPose_t &pose, uint64_t epoch,
                             int64_t now) noexcept
{
    if (Role(index) == DeviceRole::Head)
        syntheticHead_.Write({pose, now, epoch});
}
bool Router::ValidRequest(const Request &r, int64_t now, int64_t frequency, Error &e) noexcept
{
    e = Error::InvalidRequest;
    if (frequency <= 0 || r.magic != Magic || r.version != Version || r.requestedMode > 1 ||
        r.armed > 1 || r.handPreset > 3 || (r.actions & ~AllowedActions) || !r.timestamp ||
        r.bodySpinActive > 1 || r.bodySpinReserved)
        return false;
    if (r.bodySpinActive)
    {
        double norm = 0;
        for (auto value : r.bodySpinQuaternion)
        {
            if (!std::isfinite(value)) return false;
            norm += value * value;
        }
        if (!std::isfinite(norm) || std::abs(norm - 1) > 1e-6 ||
            !r.bodySpinLeaseQpc || !r.bodySpinGeneration)
            return false;
        for (auto value : r.bodySpinPivot)
            if (!std::isfinite(value) || std::abs(value) > 10000)
                return false;
    }
    const double numeric[] = {r.height,    r.yaw,     r.pitch, r.handYaw,
                              r.handPitch, r.forward, r.strafe};
    for (auto n : numeric)
        if (!std::isfinite(n))
            return false;
    if (std::abs(r.height) > 1.8 || std::abs(r.yaw) > 1e6 || std::abs(r.pitch) > 1.5 ||
        std::abs(r.handYaw) > 1e6 || std::abs(r.handPitch) > 1.6 || std::abs(r.forward) > 1 ||
        std::abs(r.strafe) > 1)
        return false;
    auto age = (static_cast<double>(now) - static_cast<double>(r.timestamp)) * 1000 / frequency;
    if (age > WatchdogMilliseconds)
    {
        e = Error::StaleRequest;
        return false;
    }
    if (age < -25)
        return false;
    e = Error::None;
    return true;
}
bool Router::BodySpinActive(const Request &r, bool read, int64_t now, int64_t frequency) noexcept
{
    Error error{};
    if (!read || !r.bodySpinActive || !ValidRequest(r, now, frequency, error))
        return false;
    auto age = (static_cast<double>(now) - r.bodySpinLeaseQpc) * 1000 / frequency;
    return age >= -25 && age <= WatchdogMilliseconds;
}
bool Router::BodySpinPermitted(const Request &r, bool read, int64_t now, int64_t frequency) const noexcept
{
    if (read && epoch_.load(std::memory_order_acquire) != r.epoch) return false;
    if (read && !r.bodySpinActive && epoch_.load(std::memory_order_acquire) == r.epoch)
    {
        Error error{};
        if (ValidRequest(r, now, frequency, error))
            RetireSpinGeneration(r.bodySpinGeneration, BodySpinBlockReason::StalePhysicalRig);
    }
    if (!BodySpinActive(r, read, now, frequency))
    {
        // A refreshed lease cannot revive rotation after observed source loss.
        if (!read) RetireCurrentSpin(BodySpinBlockReason::StalePhysicalRig);
        else if (r.bodySpinActive && r.bodySpinGeneration &&
                 r.bodySpinGeneration <= spinAdmittedGeneration_.load(std::memory_order_acquire))
            RetireSpinGeneration(r.bodySpinGeneration, BodySpinBlockReason::StalePhysicalRig);
        return false;
    }
    if (epoch_.load(std::memory_order_acquire) != r.epoch)
        return false;
    if (!RaiseSpinFloor(spinAdmittedGeneration_, r.bodySpinGeneration) ||
        LatchedSpinBlock(r) != BodySpinBlockReason::None) return false;
    if (r.requestedMode && !RaiseSpinFloor(spinDesktopGeneration_, r.bodySpinGeneration)) return false;
    auto refuse = [&](BodySpinBlockReason reason) {
        LatchSpinBlock(r, reason); return false;
    };
    if (
        Role(0) != DeviceRole::Head || !BodySpinEligible(0) ||
        !headContainer_.load(std::memory_order_acquire) ||
        headContainer_.load(std::memory_order_acquire) != containers_[0].load(std::memory_order_acquire) ||
        !headValid_.load(std::memory_order_acquire))
        return refuse(BodySpinBlockReason::StalePhysicalRig);
    const auto stamp = headTimestamp_.load(std::memory_order_acquire);
    const auto age = (static_cast<double>(now) - stamp) * 1000 / frequency;
    if (stamp <= 0 || age < -25 || age > WatchdogMilliseconds)
        return refuse(BodySpinBlockReason::StalePhysicalRig);
    // Both modes require coherent current poses for the complete observed rig.
    // A bounded read miss cannot prove fresh whole-rig tracking and refuses the
    // activation, rather than mixing independently published cache fields.
    for (uint32_t index = 0; index < poses_.size(); ++index)
    {
        if (!BodySpinEligible(index)) continue;
        const auto container = containers_[index].load(std::memory_order_acquire);
        PoseSnapshot source{};
        if (capturedInvalidVersions_[index].load(std::memory_order_acquire) !=
                captureInvalidVersions_[index].load(std::memory_order_acquire) ||
            !Physical(index, source) || !ValidPose(source.pose) || source.container != container)
            return refuse(BodySpinBlockReason::StalePhysicalRig);
        const auto memberAge = (static_cast<double>(now) - source.timestamp) * 1000 / frequency;
        if (
            memberAge < -25 || memberAge > WatchdogMilliseconds)
            return refuse(BodySpinBlockReason::StalePhysicalRig);
    }
    if (r.requestedMode)
    {
        if (!desktop_.load(std::memory_order_acquire) ||
            anchorEpoch_.load(std::memory_order_acquire) != r.epoch)
            return refuse(BodySpinBlockReason::StalePhysicalRig);
        if (!TrackerAnchorsReady(r.epoch))
        {
            auto reason = BodySpinBlockReason::StalePhysicalRig;
            if (trackerAnchorEpoch_.load(std::memory_order_acquire) == r.epoch)
                for (uint32_t index = 0; index < poses_.size(); ++index)
                {
                    const auto expected = trackerAnchorContainers_[index].load(std::memory_order_acquire);
                    const auto current = GenericTracker(index) ? containers_[index].load(std::memory_order_acquire) : 0;
                    if (expected != current) { reason = BodySpinBlockReason::DesktopRigIdentityChanged; break; }
                }
            return refuse(reason);
        }
    }
    return LatchedSpinBlock(r) == BodySpinBlockReason::None;
}
BodySpinBlockReason Router::LatchedSpinBlock(const Request &r) const noexcept
{
    if (spinAdmissionFault_.load(std::memory_order_acquire))
        return BodySpinBlockReason::NativeAdmissionFault;
    if (r.bodySpinGeneration < spinAdmittedGeneration_.load(std::memory_order_acquire) ||
        r.bodySpinGeneration <= spinRetiredGeneration_.load(std::memory_order_acquire))
        return spinIdentityRefusalGeneration_.load(std::memory_order_acquire) >= r.bodySpinGeneration
                   ? BodySpinBlockReason::DesktopRigIdentityChanged : BodySpinBlockReason::StalePhysicalRig;
    return BodySpinBlockReason::None;
}
void Router::LatchSpinBlock(const Request &r, BodySpinBlockReason reason) const noexcept
{
    if (epoch_.load(std::memory_order_acquire) != r.epoch) return;
    RetireSpinGeneration(r.bodySpinGeneration, reason);
}
bool Router::RaiseSpinFloor(std::atomic<uint64_t> &floor, uint64_t generation) const noexcept
{
    auto current = floor.load(std::memory_order_acquire);
    for (int attempt = 0; attempt < 32; ++attempt)
    {
        if (current >= generation) return true;
        if (floor.compare_exchange_strong(current, generation, std::memory_order_acq_rel,
                                          std::memory_order_acquire)) return true;
    }
    // A refusal must never disappear through a bounded compare-exchange miss.
    // This exceptional contention state remains closed for the driver lifetime.
    spinAdmissionFault_.store(true, std::memory_order_release);
    return false;
}
void Router::RetireSpinGeneration(uint64_t generation, BodySpinBlockReason reason) const noexcept
{
    if (!generation) return;
    if (reason == BodySpinBlockReason::DesktopRigIdentityChanged)
        RaiseSpinFloor(spinIdentityRefusalGeneration_, generation);
    RaiseSpinFloor(spinRetiredGeneration_, generation);
}
void Router::RetireCurrentSpin(BodySpinBlockReason reason) const noexcept
{
    RetireSpinGeneration(spinAdmittedGeneration_.load(std::memory_order_acquire), reason);
}
void Router::CaptureTrackerAnchors(uint64_t epoch, int64_t now, int64_t frequency) noexcept
{
    trackerAnchorComplete_.store(false, std::memory_order_release);
    bool complete = true;
    for (uint32_t index = 0; index < poses_.size(); ++index)
    {
        trackerAnchorContainers_[index].store(0, std::memory_order_release);
        if (!GenericTracker(index)) continue;
        const auto container = containers_[index].load(std::memory_order_acquire);
        trackerAnchorContainers_[index].store(container, std::memory_order_release);
        PoseSnapshot source{};
        if (!Physical(index, source) || !ValidPose(source.pose) || source.container != container)
        { complete = false; continue; }
        const auto age = (static_cast<double>(now) - source.timestamp) * 1000 / frequency;
        source.epoch = epoch;
        if (age < -25 || age > WatchdogMilliseconds || !trackerAnchors_[index].Write(source) ||
            !GenericTracker(index) || containers_[index].load(std::memory_order_acquire) != container)
        { complete = false; continue; }
    }
    trackerAnchorEpoch_.store(epoch, std::memory_order_release);
    trackerAnchorComplete_.store(complete, std::memory_order_release);
}
bool Router::TrackerAnchorsReady(uint64_t epoch) const noexcept
{
    if (!trackerAnchorComplete_.load(std::memory_order_acquire) ||
        trackerAnchorEpoch_.load(std::memory_order_acquire) != epoch) return false;
    for (uint32_t index = 0; index < poses_.size(); ++index)
    {
        const auto expected = trackerAnchorContainers_[index].load(std::memory_order_acquire);
        const auto current = GenericTracker(index)
            ? containers_[index].load(std::memory_order_acquire) : 0;
        if (expected != current) return false;
    }
    return trackerAnchorComplete_.load(std::memory_order_acquire) &&
           trackerAnchorEpoch_.load(std::memory_order_acquire) == epoch;
}
BodySpinBlockReason Router::SpinBlockReason(const Request &r, bool read,
                                          int64_t now, int64_t frequency) const noexcept
{
    BodySpinPermitted(r, read, now, frequency);
    return read && r.bodySpinActive ? LatchedSpinBlock(r) : BodySpinBlockReason::None;
}
bool Router::OriginalForRestore(uint32_t index, int64_t now, int64_t frequency,
                                vr::DriverPose_t &out) const noexcept
{
    PoseSnapshot original{};
    if (frequency <= 0 || !Physical(index, original) ||
        original.container != containers_[index].load(std::memory_order_acquire)) return false;
    out = original.pose;
    const auto age = (static_cast<double>(now) - original.timestamp) * 1000 / frequency;
    if (age > WatchdogMilliseconds || age < -25 ||
        capturedInvalidVersions_[index].load(std::memory_order_acquire) !=
            captureInvalidVersions_[index].load(std::memory_order_acquire))
    {
        out.poseIsValid = false;
        out.result = vr::TrackingResult_Uninitialized;
    }
    return true;
}
bool Router::Evaluate(const Request &r, bool read, int64_t now, int64_t frequency) noexcept
{
    Error failure{};
    if (!read || !ValidRequest(r, now, frequency, failure))
    {
        desktop_.store(false, std::memory_order_release);
        error_.store(read ? failure : Error::InvalidRequest, std::memory_order_relaxed);
        return false;
    }
    auto accepted = epoch_.load(std::memory_order_acquire);
    if (r.epoch < accepted)
        return false;
    for (int attempt = 0; r.epoch > accepted && attempt < 3; ++attempt)
        if (epoch_.compare_exchange_weak(accepted, r.epoch, std::memory_order_acq_rel))
            break;
    if (r.epoch < accepted || r.epoch > epoch_.load(std::memory_order_acquire))
        return false;
    if (!r.requestedMode)
    {
        desktop_.store(false, std::memory_order_release);
        error_.store(Error::None, std::memory_order_relaxed);
        return false;
    }
    const auto headTimestamp = headTimestamp_.load(std::memory_order_acquire);
    if (capturedInvalidVersions_[0].load(std::memory_order_acquire) !=
            captureInvalidVersions_[0].load(std::memory_order_acquire) ||
        !headValid_.load(std::memory_order_acquire) || !headTimestamp ||
        Role(0) != DeviceRole::Head || !headContainer_.load(std::memory_order_acquire) ||
        headContainer_.load(std::memory_order_acquire) != containers_[0].load(std::memory_order_acquire))
    {
        desktop_.store(false, std::memory_order_release);
        error_.store(Error::NoHead, std::memory_order_relaxed);
        return false;
    }
    auto age = (static_cast<double>(now) - headTimestamp) * 1000 / frequency;
    // Another callback can publish a newer QPC after this caller captured its `now`.
    // That publication ordering is not a stale physical stream. Match IPC tolerance.
    if (age > WatchdogMilliseconds || age < -25)
    {
        desktop_.store(false, std::memory_order_release);
        error_.store(Error::StalePhysical, std::memory_order_relaxed);
        return false;
    }
    if (!anchorValid_.load(std::memory_order_acquire) ||
        anchorEpoch_.load(std::memory_order_acquire) != r.epoch)
    {
        // Only entry takes a bounded try-gate. An established desktop session has
        // immutable anchor storage and no shared callback lock to contend with.
        if (entry_.test_and_set(std::memory_order_acquire))
            return desktop_.load(std::memory_order_acquire) &&
                   anchorValid_.load(std::memory_order_acquire) &&
                   anchorEpoch_.load(std::memory_order_acquire) == r.epoch &&
                   epoch_.load(std::memory_order_acquire) == r.epoch;
        Unlock guard{entry_};
        if (epoch_.load(std::memory_order_acquire) != r.epoch)
            return false;
        if (!anchorValid_.load(std::memory_order_acquire) ||
            anchorEpoch_.load(std::memory_order_acquire) != r.epoch)
        {
            PoseSnapshot head{};
            uint32_t headIndex = vr::k_unMaxTrackedDeviceCount;
            for (uint32_t i = 0; i < roles_.size(); ++i)
                if (Role(i) == DeviceRole::Head)
                {
                    headIndex = i;
                    break;
                }
            if (!Physical(headIndex, head) || !ValidPose(head.pose) ||
                head.container != containers_[headIndex].load(std::memory_order_acquire))
                return false;
            head.epoch = r.epoch;
            if (epoch_.load(std::memory_order_acquire) != r.epoch || !anchor_.Write(head))
                return false;
            CaptureTrackerAnchors(r.epoch, now, frequency);
            anchorEpoch_.store(r.epoch, std::memory_order_release);
            anchorValid_.store(true, std::memory_order_release);
        }
    }
    if (epoch_.load(std::memory_order_acquire) != r.epoch)
        return false;
    PoseSnapshot committedHead{};
    if (!anchor_.Read(committedHead) || committedHead.epoch != r.epoch ||
        committedHead.container != containers_[0].load(std::memory_order_acquire))
    {
        desktop_.store(false, std::memory_order_release);
        error_.store(Error::NoHead, std::memory_order_relaxed);
        return false;
    }
    desktop_.store(true, std::memory_order_release);
    if (epoch_.load(std::memory_order_acquire) != r.epoch)
        return false;
    error_.store(Error::None, std::memory_order_relaxed);
    return true;
}
bool Router::Desktop(const Request &r, bool read, int64_t now, int64_t frequency) noexcept
{
    return Evaluate(r, read, now, frequency);
}
bool Router::Synthetic(DeviceRole role, const PoseSnapshot &source, const PoseSnapshot &anchor,
                       const Request &r, vr::DriverPose_t &out) noexcept
{
    if (role == DeviceRole::Other || !ValidPose(source.pose))
        return false;
    out = source.pose;
    auto head = WorldPosition(anchor.pose);
    auto original = WorldRotation(anchor.pose);
    // Retain entry yaw, remove physical pitch/roll so mouse pitch is bounded and useful.
    auto forward = Rotate(original, {0, 0, -1});
    double entryYaw = std::atan2(-forward.x, -forward.z);
    auto rotation = YawPitch(entryYaw - std::remainder(r.yaw, 6.283185307179586),
                             std::clamp(r.pitch, -1.45, 1.45));
    head.y += r.height;
    const auto standingHeadY = head.y;
    // Held posture is an ephemeral desktop pose offset, never calibration or an
    // input binding. This conservative bound is in the captured WORLD frame; it
    // does not claim to know the application's standing/chaperone floor.
    const auto actions = r.armed ? r.actions : 0u;
    if (actions & (Crouch | Prone))
    {
        const bool prone = (actions & Prone) != 0;
        // An origin below this conservative bound has no known floor relation.
        // Still provide a bounded relative posture, never jump it upward.
        head.y = standingHeadY > .15 ? std::max(.15, standingHeadY * (prone ? .25 : .55))
                                    : standingHeadY - (prone ? 1.20 : .65);
    }
    V position = head;
    if (role == DeviceRole::Left || role == DeviceRole::Right)
    {
        double side = role == DeviceRole::Left ? -1 : 1;
        // Looking around must not hold both arms out. Neutral hands stay near
        // the hips, with a level, unmodified raw wrist orientation. Only an explicit hand
        // preset or the active right interaction hand enters a forward pose.
        const auto preset = r.armed ? r.handPreset : 0u;
        const bool interacting = role == DeviceRole::Right && (actions & (Trigger | Grip));
        // Preset1 is the normal head-look capture mode. It is a REST pose too.
        const bool menuNavigation = preset == uint32_t(HandPreset::LeftMenuNavigation);
        const bool active = role == DeviceRole::Right && (preset == 2 || menuNavigation || interacting);
        V relative{side * .20, -.75, .04};
        if (interacting)
            relative = {side * .30, -.12, -.70};
        if (preset == 2)
            relative = role == DeviceRole::Right ? V{.25, -.15, -.50}
                                                : V{-.18, -.30, -.15};
        if (menuNavigation)
            relative = role == DeviceRole::Right ? V{.25, -.15, -.50}
                                                : V{-.22, -.18, -.30};
        // Keep hands below the head at low posture without crossing WORLD y=.08
        // when that bound is below the head. An origin already below the bound
        // is never raised by pretending its actual floor is known.
        const auto handY = head.y > .15
                               ? std::min(head.y - .05, std::max(.08, head.y + relative.y))
                               : head.y + relative.y;
        relative.y = handY - head.y;
        auto offset = Rotate(YawPitch(entryYaw - r.yaw, 0), relative);
        position = {head.x + offset.x, head.y + offset.y, head.z + offset.z};
        rotation = active ? YawPitch(entryYaw - r.yaw - r.handYaw, r.handPitch)
                          : YawPitch(entryYaw - r.yaw, 0);
    }
    out.qWorldFromDriverRotation = {1, 0, 0, 0};
    out.qDriverFromHeadRotation = {1, 0, 0, 0};
    out.qRotation = rotation;
    for (int i = 0; i < 3; ++i)
    {
        out.vecWorldFromDriverTranslation[i] = 0;
        out.vecDriverFromHeadTranslation[i] = 0;
        out.vecVelocity[i] = 0;
        out.vecAcceleration[i] = 0;
        out.vecAngularVelocity[i] = 0;
        out.vecAngularAcceleration[i] = 0;
    }
    out.vecPosition[0] = position.x;
    out.vecPosition[1] = position.y;
    out.vecPosition[2] = position.z;
    out.poseTimeOffset = 0;
    out.shouldApplyHeadModel = false;
    out.willDriftInYaw = false;
    out.poseIsValid = true;
    out.deviceIsConnected = true;
    out.result = vr::TrackingResult_Running_OK;
    return true;
}
bool Router::RoutePose(uint32_t index, const Request &r, bool read, int64_t now, int64_t frequency,
                       vr::DriverPose_t &out, bool *spinApplied, bool *trackerSuspended) noexcept
{
    if (spinApplied) *spinApplied = false;
    if (trackerSuspended) *trackerSuspended = false;
    const bool desktop = Evaluate(r, read, now, frequency);
    const bool spin = BodySpinPermitted(r, read, now, frequency);
    if ((!desktop && !spin) || index >= poses_.size())
        return false;
    const auto container = containers_[index].load(std::memory_order_acquire);
    const auto role = Role(index);
    // In Physical mode the viewer follows the actual headset. Spin owns the
    // hands/body outputs only; freshness of this unmodified head is still part
    // of complete-rig admission. Desktop intentionally rotates its held view.
    if (!desktop && role == DeviceRole::Head) return false;
    const bool generic = GenericTracker(index);
    const bool suspend = desktop && generic && !spin;
    const bool applySpin = spin && BodySpinEligible(index);
    if (!suspend && (capturedInvalidVersions_[index].load(std::memory_order_acquire) !=
                         captureInvalidVersions_[index].load(std::memory_order_acquire) ||
                     !poseValid_[index].load(std::memory_order_acquire) ||
                     capturedContainers_[index].load(std::memory_order_acquire) != container)) return false;
    if (desktop && role != DeviceRole::Other)
    {
        PoseSnapshot anchor{};
        if (!anchor_.Read(anchor) || anchor.epoch != r.epoch ||
            epoch_.load(std::memory_order_acquire) != r.epoch ||
            !Synthetic(role, anchor, anchor, r, out))
            return false;
    }
    else if (desktop && generic)
    {
        PoseSnapshot source{};
        if (suspend)
        {
            if (!Physical(index, source) || source.container != container) return false;
            out = source.pose;
            out.poseIsValid = false;
            out.deviceIsConnected = false;
            out.result = vr::TrackingResult_Uninitialized;
        }
        else
        {
            if (!applySpin || !TrackerAnchorsReady(r.epoch) || !trackerAnchors_[index].Read(source) ||
                source.epoch != r.epoch || source.container != container || !ValidPose(source.pose))
                return false;
            out = source.pose;
            out.poseTimeOffset = 0;
            for (int axis = 0; axis < 3; ++axis)
                out.vecVelocity[axis] = out.vecAcceleration[axis] =
                    out.vecAngularVelocity[axis] = out.vecAngularAcceleration[axis] = 0;
        }
    }
    else
    {
        PoseSnapshot source{};
        if (!applySpin || !Physical(index, source) || !ValidPose(source.pose) || source.container != container) return false;
        auto age = (static_cast<double>(now) - source.timestamp) * 1000 / frequency;
        if (age < -25 || age > WatchdogMilliseconds) return false;
        out = source.pose;
    }
    if (applySpin)
    {
        const Q turn = Normalize({r.bodySpinQuaternion[0], r.bodySpinQuaternion[1],
                                  r.bodySpinQuaternion[2], r.bodySpinQuaternion[3]});
        // Left-compose the outer calibrated WORLD transform. Raw device/head
        // offsets and physical derivatives remain in their original driver frame.
        // Every eligible device sees the same rigid rotation about the same pivot.
        auto translation = Rotate(turn, {out.vecWorldFromDriverTranslation[0] - r.bodySpinPivot[0],
                                         out.vecWorldFromDriverTranslation[1] - r.bodySpinPivot[1],
                                         out.vecWorldFromDriverTranslation[2] - r.bodySpinPivot[2]});
        out.qWorldFromDriverRotation = Normalize(Mul(turn, out.qWorldFromDriverRotation));
        out.vecWorldFromDriverTranslation[0] = r.bodySpinPivot[0] + translation.x;
        out.vecWorldFromDriverTranslation[1] = r.bodySpinPivot[1] + translation.y;
        out.vecWorldFromDriverTranslation[2] = r.bodySpinPivot[2] + translation.z;
    }
    if (Role(index) != role || containers_[index].load(std::memory_order_acquire) != container ||
        (generic && !GenericTracker(index)) ||
        (suspend && (!desktop_.load(std::memory_order_acquire) ||
                     anchorEpoch_.load(std::memory_order_acquire) != r.epoch)) ||
        (applySpin && (!BodySpinEligible(index) || !BodySpinPermitted(r, read, now, frequency))) ||
        epoch_.load(std::memory_order_acquire) != r.epoch)
        return false;
    if (spinApplied) *spinApplied = applySpin;
    if (trackerSuspended) *trackerSuspended = suspend;
    return true;
}
Status Router::GetStatus(const Request &r, bool read, int64_t now, int64_t frequency) noexcept
{
    Status s{};
    s.timestamp = now;
    s.ackEpoch = r.epoch;
    s.routedSamples = routed_.load(std::memory_order_relaxed);
    s.physicalSamples = physical_.load(std::memory_order_relaxed);
    s.bodySpinSamples = bodySpinSamples_.load(std::memory_order_relaxed);
    s.bodySpinAttemptGeneration = read ? r.bodySpinGeneration : 0;
    s.bodySpinGeneration = bodySpinGeneration_.load(std::memory_order_acquire);
    s.headAgeMilliseconds = -1;
    s.actualMode = Evaluate(r, read, now, frequency) ? 1 : 0;
    s.bodySpinActive = BodySpinPermitted(r, read, now, frequency) && s.bodySpinSamples &&
                       s.bodySpinGeneration == r.bodySpinGeneration;
    s.bodySpinBlockReason = uint32_t(SpinBlockReason(r, read, now, frequency));
    s.error = uint32_t(error_.load(std::memory_order_relaxed));
    s.anchorEpoch = anchorValid_.load(std::memory_order_acquire)
                        ? anchorEpoch_.load(std::memory_order_acquire) : 0;
    PoseSnapshot synthetic{};
    if (syntheticHead_.Read(synthetic) && synthetic.timestamp)
    {
        s.syntheticHeadEpoch = synthetic.epoch;
        s.syntheticHeadQpc = synthetic.timestamp;
        auto age = (static_cast<double>(now) - synthetic.timestamp) * 1000 / frequency;
        s.syntheticHeadValid = s.actualMode && synthetic.epoch == r.epoch &&
                               age >= -25 && age <= WatchdogMilliseconds &&
                               ValidPose(synthetic.pose);
        auto position = WorldPosition(synthetic.pose);
        s.syntheticPosition[0] = position.x;
        s.syntheticPosition[1] = position.y;
        s.syntheticPosition[2] = position.z;
        auto quaternion = WorldRotation(synthetic.pose);
        s.syntheticQuaternion[0] = quaternion.w;
        s.syntheticQuaternion[1] = quaternion.x;
        s.syntheticQuaternion[2] = quaternion.y;
        s.syntheticQuaternion[3] = quaternion.z;
    }
    for (uint32_t i = 0; i < roles_.size(); ++i)
    {
        auto role = Role(i);
        PoseSnapshot p{};
        if (GenericTrackerPhysical(i, p))
            ++s.genericTrackerAvailable;
        if (role == DeviceRole::Other || !Physical(i, p) || !ValidPose(p.pose) ||
            capturedInvalidVersions_[i].load(std::memory_order_acquire) !=
                captureInvalidVersions_[i].load(std::memory_order_acquire) ||
            !poseValid_[i].load(std::memory_order_acquire) ||
            p.container != containers_[i].load(std::memory_order_acquire) || Role(i) != role)
            continue;
        auto age = (static_cast<double>(now) - p.timestamp) * 1000 / frequency;
        if (role == DeviceRole::Head)
        {
            s.hasHead = age <= WatchdogMilliseconds && age >= -25;
            s.headAgeMilliseconds = age >= -25 ? std::max(age, 0.0) : age;
            auto v = WorldPosition(p.pose);
            s.position[0] = v.x;
            s.position[1] = v.y;
            s.position[2] = v.z;
            auto q = WorldRotation(p.pose);
            s.quaternion[0] = q.w;
            s.quaternion[1] = q.x;
            s.quaternion[2] = q.y;
            s.quaternion[3] = q.z;
        }
        if (role == DeviceRole::Left)
        {
            s.hasLeft = 1;
            s.leftPhysicalAgeMilliseconds = age >= -25 ? std::max(age, 0.0) : -1;
        }
        if (role == DeviceRole::Right)
        {
            s.hasRight = 1;
            s.rightPhysicalAgeMilliseconds = age >= -25 ? std::max(age, 0.0) : -1;
        }
    }
    // A concurrent mode transaction can finish while this status is assembled.
    // Never label an older request as a newly committed desktop anchor.
    if (s.actualMode && (s.anchorEpoch != r.epoch ||
                         epoch_.load(std::memory_order_acquire) != r.epoch))
    {
        s.actualMode = 0;
        s.syntheticHeadValid = 0;
        s.error = uint32_t(Error::InvalidRequest);
    }
    if (s.bodySpinActive && !BodySpinPermitted(r, read, now, frequency))
    {
        s.bodySpinActive = 0;
        if (!s.actualMode) s.syntheticHeadValid = 0;
    }
    return s;
}
Control ClassifyControl(const char *name) noexcept
{
    if (!name)
        return Control::Unknown;
    auto is = [name](const char *path) { return std::strcmp(name, path) == 0; };
    const auto length = std::strlen(name);
    if (length >= 6 && std::strcmp(name + length - 6, "/touch") == 0)
        return Control::Touch;
    if (is("/input/proximity") || is("/input/proximity/click"))
        return Control::Proximity;
    if (is("/input/trigger/value") || is("/input/trigger/click"))
        return Control::Trigger;
    if (is("/input/grip/value") || is("/input/grip/click") || is("/input/grip/force") ||
        is("/input/squeeze/value") || is("/input/squeeze/click") || is("/input/squeeze/force"))
        return Control::Grip;
    if (is("/input/application_menu/click") || is("/input/y/click") || is("/input/b/click"))
        return Control::Menu;
    if (is("/input/joystick/click") || is("/input/thumbstick/click") ||
        is("/input/trackpad/click"))
        return Control::Run;
    if (is("/input/joystick/x") || is("/input/thumbstick/x") || is("/input/trackpad/x"))
        return Control::StickX;
    if (is("/input/joystick/y") || is("/input/thumbstick/y") || is("/input/trackpad/y"))
        return Control::StickY;
    if (is("/input/a/click"))
        return Control::Jump;
    return Control::Unknown;
}
float DesiredValue(Control control, DeviceRole role, const Request &r) noexcept
{
    if (!r.armed)
        return 0;
    switch (control)
    {
    case Control::Trigger:
        return role == DeviceRole::Right && (r.actions & Trigger) ? 1.f : 0.f;
    case Control::Grip:
        return role == DeviceRole::Right && (r.actions & Grip) ? 1.f : 0.f;
    case Control::Menu:
        return role == DeviceRole::Left && (r.actions & Menu) ? 1.f : 0.f;
    case Control::StickX:
        return role == DeviceRole::Left ? float(r.strafe) : 0.f;
    case Control::StickY:
        return role == DeviceRole::Left ? float(r.forward) : 0.f;
    case Control::Jump:
        return role == DeviceRole::Right && (r.actions & Jump) ? 1.f : 0.f;
    case Control::Run:
        return role == DeviceRole::Left && (r.actions & Run) ? 1.f : 0.f;
    default:
        return 0.f;
    }
}
} // namespace sw
