#pragma once
#include <string_view>

namespace sw
{
inline constexpr std::string_view SupportedRuntimeBuild = "25330290";
// Static inventory, matching interface names, or user configuration alone must
// never admit a new runtime. Change this only with reviewed compatibility evidence.
inline constexpr bool RuntimeBuildAdmitted(std::string_view configured, std::string_view observed) noexcept
{
    return configured == SupportedRuntimeBuild && observed == SupportedRuntimeBuild;
}
} // namespace sw
