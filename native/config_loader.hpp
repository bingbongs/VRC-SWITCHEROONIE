#pragma once
#include "config.hpp"
#include <cstdint>
#include <array>
#include <filesystem>
namespace sw
{
enum class ConfigLoadStage : uint32_t
{
    Ready,
    Resolve,
    Open,
    Read,
    TooLarge,
    Schema
};
struct ConfigLoadResult
{
    DriverConfig config;
    ConfigLoadStage stage{ConfigLoadStage::Resolve};
    uint32_t systemError{}, bytes{};
    std::array<char, 65> pathFingerprint{};
    bool processSidKnown{}, effectiveSidKnown{}, effectiveMatchesProcess{}, threadImpersonated{};
    uint32_t effectiveTokenError{};
    std::array<char, 65> rawPathFingerprint{};
    uint32_t pathCharacters{}, normalOpenError{}, extendedOpenError{};
    bool pathAbsolute{}, pathEmbeddedNul{}, extendedAttempted{};
    ConfigLoadStage normalStage{ConfigLoadStage::Resolve}, extendedStage{ConfigLoadStage::Resolve};
    // Order: canonical file, config parent, owned profile-root parent. Values are
    // public file attributes/errors only; no path or identity text is exposed.
    std::array<uint32_t, 3> normalAttributes{}, normalAttributeErrors{};
    std::array<uint32_t, 3> extendedAttributes{}, extendedAttributeErrors{};
    std::array<uint32_t, 2> parentCaseFlags{}, parentCaseErrors{};
    std::array<bool, 2> parentCaseKnown{};
    bool ready() const noexcept { return stage == ConfigLoadStage::Ready; }
};
inline constexpr uint32_t MaxConfigBytes = 4096;
// One canonical opt-in: explicit process-token FOLDERID_Profile followed by
// VRC-SWITCHEROONIE/config/driver.json. No old AppData/other-location fallback.
bool CurrentUserConfigPath(std::filesystem::path &, uint32_t &systemError);
bool CurrentUserConfigPath(std::filesystem::path &, uint32_t &systemError, ConfigLoadResult &context);
bool ConfigPathFingerprint(const std::filesystem::path &, std::array<char, 65> &);
bool ConfigRawPathFingerprint(const std::filesystem::path &, std::array<char, 65> &);
bool CanonicalExtendedConfigPath(const std::filesystem::path &, std::filesystem::path &);
ConfigLoadResult LoadCanonicalConfigFile(const std::filesystem::path &);
ConfigLoadResult LoadConfigFile(const std::filesystem::path &);
ConfigLoadResult LoadCurrentUserConfig();
const char *ConfigStageName(ConfigLoadStage) noexcept;
} // namespace sw
