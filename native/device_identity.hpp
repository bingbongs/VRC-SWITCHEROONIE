#pragma once
#include "openvr_driver.h"
#include <array>
#include <string_view>
namespace sw
{
inline bool EligibleBodyTrackedClass(vr::ETrackedDeviceClass deviceClass) noexcept
{
    return deviceClass == vr::TrackedDeviceClass_HMD ||
           deviceClass == vr::TrackedDeviceClass_Controller ||
           deviceClass == vr::TrackedDeviceClass_GenericTracker;
}
inline bool EligibleGenericTrackerClass(vr::ETrackedDeviceClass deviceClass) noexcept
{
    return deviceClass == vr::TrackedDeviceClass_GenericTracker;
}
inline bool IdentityPrefix(std::string_view value, std::string_view prefix) noexcept
{
    if (value.size() < prefix.size()) return false;
    for (size_t i = 0; i < prefix.size(); ++i)
    {
        auto lower = [](char c) { return c >= 'A' && c <= 'Z' ? char(c + ('a' - 'A')) : c; };
        if (lower(value[i]) != lower(prefix[i])) return false;
    }
    return true;
}
// The owned research display is synthetic. Its presence/pose/proximity cannot
// establish an independently observed physical source for the production route.
inline bool IsOwnedSyntheticIdentity(std::string_view trackingSystem, std::string_view serial,
                                    std::string_view model) noexcept
{
    constexpr std::string_view system = "switcheroonie_persistent_synthetic";
    return (trackingSystem.size() == system.size() && IdentityPrefix(trackingSystem, system)) ||
           IdentityPrefix(serial, "switcheroonie-persistent-") ||
           IdentityPrefix(model, "Switcheroonie Persistent ");
}
inline bool EligiblePhysicalRoleIdentity(bool identitiesComplete, std::string_view trackingSystem,
                                         std::string_view serial, std::string_view model) noexcept
{
    return identitiesComplete && !IsOwnedSyntheticIdentity(trackingSystem, serial, model);
}
inline bool EligiblePhysicalRoleIdentity(vr::CVRPropertyHelpers &properties,
                                         vr::PropertyContainerHandle_t container) noexcept
{
    std::array<char, 512> system{}, serial{}, model{};
    auto read = [&](vr::ETrackedDeviceProperty property, auto &value) {
        vr::ETrackedPropertyError error{};
        auto required = properties.GetStringProperty(container, property, value.data(),
                                                       uint32_t(value.size()), &error);
        return error == vr::TrackedProp_Success && required > 1 &&
               required <= value.size() && value[required - 1] == '\0';
    };
    bool systemComplete = read(vr::Prop_TrackingSystemName_String, system);
    bool serialComplete = read(vr::Prop_SerialNumber_String, serial);
    bool modelComplete = read(vr::Prop_ModelNumber_String, model);
    return EligiblePhysicalRoleIdentity(systemComplete && serialComplete && modelComplete,
                                        system.data(), serial.data(), model.data());
}
} // namespace sw
