#include "config_loader.hpp"
#include <array>
#include <shlobj.h>
#include <windows.h>
#include <bcrypt.h>
namespace sw
{
namespace
{
struct File
{
    HANDLE handle{INVALID_HANDLE_VALUE};
    ~File() { if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle); }
};
struct Folder
{
    PWSTR value{};
    ~Folder() { if (value) CoTaskMemFree(value); }
};
struct Token
{
    HANDLE value{};
    ~Token() { if (value) CloseHandle(value); }
};
bool TokenSid(HANDLE token, std::array<std::byte, 1024> &storage, PSID &sid)
{
    DWORD bytes{};
    if (!GetTokenInformation(token, TokenUser, storage.data(), DWORD(storage.size()), &bytes))
        return false;
    sid = reinterpret_cast<TOKEN_USER *>(storage.data())->User.Sid;
    return IsValidSid(sid) != FALSE;
}
void ContextFacts(HANDLE process, ConfigLoadResult &result)
{
    std::array<std::byte, 1024> processStorage{}, effectiveStorage{};
    PSID processSid{}, effectiveSid{};
    result.processSidKnown = TokenSid(process, processStorage, processSid);
    Token thread;
    if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &thread.value))
    {
        result.effectiveTokenError = GetLastError();
        if (result.effectiveTokenError == ERROR_CANT_OPEN_ANONYMOUS)
            result.threadImpersonated = true;
        if (result.effectiveTokenError == ERROR_NO_TOKEN)
        {
            result.effectiveTokenError = 0;
            result.effectiveSidKnown = result.processSidKnown;
            result.effectiveMatchesProcess = result.processSidKnown;
        }
        return;
    }
    result.threadImpersonated = true;
    result.effectiveSidKnown = TokenSid(thread.value, effectiveStorage, effectiveSid);
    result.effectiveMatchesProcess = result.processSidKnown && result.effectiveSidKnown &&
                                     EqualSid(processSid, effectiveSid);
}
} // namespace
bool CurrentUserConfigPath(std::filesystem::path &out, uint32_t &systemError)
{
    ConfigLoadResult context;
    return CurrentUserConfigPath(out, systemError, context);
}
bool CurrentUserConfigPath(std::filesystem::path &out, uint32_t &systemError,
                           ConfigLoadResult &context)
{
    Token process;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_IMPERSONATE | TOKEN_DUPLICATE,
                          &process.value))
    {
        systemError = GetLastError();
        ContextFacts(nullptr, context);
        return false;
    }
    ContextFacts(process.value, context);
    Folder folder;
    auto result = SHGetKnownFolderPath(FOLDERID_Profile, KF_FLAG_DEFAULT, process.value,
                                      &folder.value);
    if (FAILED(result) || !folder.value || !*folder.value)
    {
        systemError = FAILED(result) ? uint32_t(result) : ERROR_PATH_NOT_FOUND;
        return false;
    }
    out = std::filesystem::path(folder.value) / L"VRC-SWITCHEROONIE" / L"config" / L"driver.json";
    ConfigPathFingerprint(out, context.pathFingerprint);
    systemError = 0;
    return true;
}
bool ConfigPathFingerprint(const std::filesystem::path &path, std::array<char, 65> &out)
{
    out.fill(0);
    auto required = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
    if (!required || required > 32768) return false;
    std::wstring normalized(required, L'\0');
    auto length = GetFullPathNameW(path.c_str(), required, normalized.data(), nullptr);
    if (!length || length >= required) return false;
    normalized.resize(length);
    while (!normalized.empty() && normalized.back() == L'\\') normalized.pop_back();
    std::wstring upper(normalized.size(), L'\0');
    if (!LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, normalized.data(),
                       int(normalized.size()), upper.data(), int(upper.size()), nullptr, nullptr, 0))
        return false;
    return ConfigRawPathFingerprint(std::filesystem::path(upper), out);
}
bool ConfigRawPathFingerprint(const std::filesystem::path &path, std::array<char, 65> &out)
{
    out.fill(0);
    const auto &native = path.native();
    if (native.size() > 32768) return false;
    BCRYPT_ALG_HANDLE algorithm{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
        return false;
    std::array<unsigned char, 32> hash{};
    auto success = BCryptHash(algorithm, nullptr, 0,
                              reinterpret_cast<PUCHAR>(const_cast<wchar_t *>(native.data())),
                              ULONG(native.size() * sizeof(wchar_t)), hash.data(), ULONG(hash.size())) >= 0;
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (!success) return false;
    constexpr char hex[] = "0123456789ABCDEF";
    for (size_t i = 0; i < hash.size(); ++i)
    {
        out[i * 2] = hex[hash[i] >> 4];
        out[i * 2 + 1] = hex[hash[i] & 15];
    }
    return true;
}
bool CanonicalExtendedConfigPath(const std::filesystem::path &path, std::filesystem::path &out)
{
    const auto &native = path.native();
    if (!path.is_absolute() || native.find(L'\0') != std::wstring::npos ||
        native.starts_with(L"\\\\.\\") || native.starts_with(L"\\\\?\\") ||
        native.size() > 32760 || path.filename() != L"driver.json" ||
        path.parent_path().filename() != L"config" ||
        path.parent_path().parent_path().filename() != L"VRC-SWITCHEROONIE") return false;
    // Extended Win32 syntax disables normal dot/space processing. Reject
    // ambiguous components so this is an equivalent spelling of this file.
    for (const auto &component : path)
    {
        auto value = component.native();
        if (value == L"." || value == L".." ||
            (!value.empty() && (value.back() == L'.' || value.back() == L' '))) return false;
    }
    auto required = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
    if (!required || required > 32760) return false;
    std::wstring normalized(required, L'\0');
    auto length = GetFullPathNameW(path.c_str(), required, normalized.data(), nullptr);
    if (!length || length >= required) return false;
    normalized.resize(length);
    if (normalized.starts_with(L"\\\\"))
        out = L"\\\\?\\UNC\\" + normalized.substr(2);
    else if (normalized.size() >= 3 && normalized[1] == L':' && normalized[2] == L'\\')
        out = L"\\\\?\\" + normalized;
    else return false;
    return true;
}
ConfigLoadResult LoadConfigFile(const std::filesystem::path &path)
{
    ConfigLoadResult result;
    result.stage = ConfigLoadStage::Open;
    File file{CreateFileW(path.c_str(), GENERIC_READ,
                          FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr,
                          OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
                          nullptr)};
    if (file.handle == INVALID_HANDLE_VALUE)
    {
        result.systemError = GetLastError();
        return result;
    }
    result.stage = ConfigLoadStage::Read;
    if (GetFileType(file.handle) != FILE_TYPE_DISK)
    {
        result.systemError = ERROR_INVALID_FUNCTION;
        return result;
    }
    LARGE_INTEGER length{};
    if (!GetFileSizeEx(file.handle, &length))
    {
        result.systemError = GetLastError();
        return result;
    }
    if (length.QuadPart < 0 || length.QuadPart > MaxConfigBytes)
    {
        result.stage = ConfigLoadStage::TooLarge;
        result.systemError = ERROR_FILE_TOO_LARGE;
        return result;
    }
    std::array<char, MaxConfigBytes> content{};
    DWORD read{};
    if (!ReadFile(file.handle, content.data(), DWORD(length.QuadPart), &read, nullptr))
    {
        result.systemError = GetLastError();
        return result;
    }
    result.bytes = read;
    if (read != DWORD(length.QuadPart))
    {
        result.systemError = ERROR_HANDLE_EOF;
        return result;
    }
    char extra{};
    DWORD additional{};
    if (!ReadFile(file.handle, &extra, 1, &additional, nullptr))
    {
        result.systemError = GetLastError();
        return result;
    }
    LARGE_INTEGER finalLength{};
    if (!GetFileSizeEx(file.handle, &finalLength))
    {
        result.systemError = GetLastError();
        return result;
    }
    if (additional || finalLength.QuadPart != length.QuadPart)
    {
        result.systemError = ERROR_RETRY;
        return result;
    }
    result.stage = ConfigLoadStage::Schema;
    if (!ParseConfig(std::string_view(content.data(), read), result.config))
        return result;
    result.stage = ConfigLoadStage::Ready;
    return result;
}
ConfigLoadResult LoadCurrentUserConfig()
{
    std::filesystem::path path;
    uint32_t error{};
    ConfigLoadResult result;
    if (!CurrentUserConfigPath(path, error, result))
    {
        result.systemError = error;
        return result;
    }
    auto loaded = LoadCanonicalConfigFile(path);
    loaded.pathFingerprint = result.pathFingerprint;
    loaded.processSidKnown = result.processSidKnown;
    loaded.effectiveSidKnown = result.effectiveSidKnown;
    loaded.effectiveMatchesProcess = result.effectiveMatchesProcess;
    loaded.threadImpersonated = result.threadImpersonated;
    loaded.effectiveTokenError = result.effectiveTokenError;
    return loaded;
}
ConfigLoadResult LoadCanonicalConfigFile(const std::filesystem::path &path)
{
    ConfigLoadResult diagnostics;
    const auto &native = path.native();
    diagnostics.pathCharacters = uint32_t(std::min<size_t>(native.size(), UINT32_MAX));
    diagnostics.pathAbsolute = path.is_absolute();
    diagnostics.pathEmbeddedNul = native.find(L'\0') != std::wstring::npos;
    ConfigRawPathFingerprint(path, diagnostics.rawPathFingerprint);
    std::filesystem::path extended;
    bool equivalent = CanonicalExtendedConfigPath(path, extended);
    if (!equivalent)
    {
        diagnostics.systemError = ERROR_INVALID_NAME;
        return diagnostics;
    }
    auto normal = LoadConfigFile(path);
    diagnostics.normalStage = normal.stage;
    diagnostics.normalOpenError = normal.stage == ConfigLoadStage::Open ? normal.systemError : 0;
    std::array<std::filesystem::path, 3> normalPaths{path, path.parent_path(), path.parent_path().parent_path()};
    std::array<std::filesystem::path, 3> extendedPaths{};
    if (equivalent) extendedPaths = {extended, extended.parent_path(), extended.parent_path().parent_path()};
    for (size_t i = 0; i < normalPaths.size(); ++i)
    {
        auto attributes = GetFileAttributesW(normalPaths[i].c_str());
        diagnostics.normalAttributes[i] = attributes;
        diagnostics.normalAttributeErrors[i] = attributes == INVALID_FILE_ATTRIBUTES ? GetLastError() : 0;
        if (equivalent)
        {
            attributes = GetFileAttributesW(extendedPaths[i].c_str());
            diagnostics.extendedAttributes[i] = attributes;
            diagnostics.extendedAttributeErrors[i] = attributes == INVALID_FILE_ATTRIBUTES ? GetLastError() : 0;
            if (i > 0)
            {
                File directory{CreateFileW(extendedPaths[i].c_str(), FILE_READ_ATTRIBUTES,
                                           FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                                           FILE_FLAG_BACKUP_SEMANTICS, nullptr)};
                if (directory.handle == INVALID_HANDLE_VALUE) diagnostics.parentCaseErrors[i - 1] = GetLastError();
                else
                {
                    FILE_CASE_SENSITIVE_INFO flags{};
                    diagnostics.parentCaseKnown[i - 1] = GetFileInformationByHandleEx(
                        directory.handle, FileCaseSensitiveInfo, &flags, sizeof(flags)) != FALSE;
                    if (diagnostics.parentCaseKnown[i - 1]) diagnostics.parentCaseFlags[i - 1] = flags.Flags;
                    else diagnostics.parentCaseErrors[i - 1] = GetLastError();
                }
            }
        }
    }
    auto selected = normal;
    if (equivalent)
    {
        // Same canonical file, rights, sharing and bounded reader. This never
        // retries under another identity or changes a permission/namespace.
        selected = LoadConfigFile(extended);
        diagnostics.extendedAttempted = true;
        diagnostics.extendedStage = selected.stage;
        diagnostics.extendedOpenError = selected.stage == ConfigLoadStage::Open ? selected.systemError : 0;
    }
    diagnostics.config = std::move(selected.config);
    diagnostics.stage = selected.stage;
    diagnostics.systemError = selected.systemError;
    diagnostics.bytes = selected.bytes;
    return diagnostics;
}
const char *ConfigStageName(ConfigLoadStage stage) noexcept
{
    switch (stage)
    {
    case ConfigLoadStage::Ready: return "ready";
    case ConfigLoadStage::Resolve: return "resolve";
    case ConfigLoadStage::Open: return "open";
    case ConfigLoadStage::Read: return "read";
    case ConfigLoadStage::TooLarge: return "size-limit";
    case ConfigLoadStage::Schema: return "schema";
    }
    return "unknown";
}
} // namespace sw
