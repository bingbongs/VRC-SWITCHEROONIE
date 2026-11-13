#pragma once
#include "openvr_driver.h"
#include <array>
#include <cmath>
#include <string_view>

namespace sw
{
// Normalized from the observed public GetComponentStateForDevicePath("tip")
// rigid rotation, SteamVR build25330290 / exact Quest2_right render model.
// Component choice inside VRChat itself was not queried or established.
inline constexpr vr::HmdQuaternion_t Quest2RightTipRotation{
    .9472102882779216, -.3206129594705375, 0, 0};
inline bool ValidPointerTipRotation(vr::HmdQuaternion_t q) noexcept
{
    return std::isfinite(q.w) && std::isfinite(q.x) && std::isfinite(q.y) &&
           std::isfinite(q.z) &&
           std::abs(q.w * q.w + q.x * q.x + q.y * q.y + q.z * q.z - 1) <= 1e-6;
}
inline bool ApprovedQuest2RightPointerModel(std::string_view model, bool complete) noexcept
{
    return complete && model == "oculus_quest2_controller_right" &&
           ValidPointerTipRotation(Quest2RightTipRotation);
}
struct DetectedControllerModel
{
    std::array<char, 64> name{};
    uint32_t length{};
    bool complete{};
    std::string_view value() const noexcept { return {name.data(), length}; }
};
inline DetectedControllerModel ReadControllerRenderModel(vr::CVRPropertyHelpers &properties,
                                                          uint64_t container) noexcept
{
    DetectedControllerModel model;
    vr::ETrackedPropertyError error{};
    auto required = properties.GetStringProperty(container, vr::Prop_RenderModelName_String,
        model.name.data(), uint32_t(model.name.size()), &error);
    if (error == vr::TrackedProp_Success && required > 1 && required <= model.name.size() &&
        model.name[required - 1] == '\0')
    {
        model.length = required - 1;
        model.complete = std::string_view(model.name.data(), model.length).find('\0') ==
                         std::string_view::npos;
    }
    if (!model.complete) model.length = 0;
    return model;
}
} // namespace sw
