#include "config_loader.hpp"
#include "startup_retry.hpp"
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <cstring>
#include <iostream>
#include <limits>
#include <string>
#include <thread>
#include <vector>
namespace
{
unsigned checks{}, failures{};
void Check(bool value, const char *name)
{
    ++checks;
    if (!value) { ++failures; std::cerr << "FAIL " << name << '\n'; }
}
struct FileFixture
{
    std::filesystem::path path;
    FileFixture()
    {
        wchar_t temp[MAX_PATH]{}, file[MAX_PATH]{};
        if (!GetTempPathW(MAX_PATH, temp) || !GetTempFileNameW(temp, L"swc", 0, file))
            throw std::runtime_error("Owned startup fixture creation failed.");
        path = file;
    }
    ~FileFixture() { DeleteFileW(path.c_str()); }
    void Write(std::string_view bytes)
    {
        HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("Fixture write open failed.");
        DWORD written{};
        bool okay = WriteFile(file, bytes.data(), DWORD(bytes.size()), &written, nullptr) &&
                    written == bytes.size();
        CloseHandle(file);
        if (!okay) throw std::runtime_error("Fixture write failed.");
    }
};
constexpr std::string_view enabled = "{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\"}";
struct OwnedCanonicalConfig
{
    FileFixture location;
    std::filesystem::path file;
    std::vector<std::filesystem::path> directories;
    explicit OwnedCanonicalConfig(bool longPath = false)
    {
        DeleteFileW(location.path.c_str());
        auto path = location.path;
        auto directory = [&](const std::filesystem::path &value) {
            const auto extended = L"\\\\?\\" + value.native();
            if (!CreateDirectoryW(extended.c_str(), nullptr)) throw std::runtime_error("Owned canonical directory creation failed.");
            directories.push_back(value);
        };
        directory(path);
        if (longPath)
            for (int i = 0; i < 7; ++i) { path /= L"owned-startup-long-name-abcdefghijklmnopqrstuvwxyz"; directory(path); }
        path /= L"VRC-SWITCHEROONIE";
        directory(path);
        path /= L"config";
        directory(path);
        file = path / L"driver.json";
    }
    ~OwnedCanonicalConfig()
    {
        DeleteFileW((L"\\\\?\\" + file.native()).c_str());
        for (auto it = directories.rbegin(); it != directories.rend(); ++it)
            RemoveDirectoryW((L"\\\\?\\" + it->native()).c_str());
    }
    void Write(std::string_view text)
    {
        HANDLE handle = CreateFileW((L"\\\\?\\" + file.native()).c_str(), GENERIC_WRITE, 0,
                                     nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (handle == INVALID_HANDLE_VALUE) throw std::runtime_error("Owned canonical write open failed.");
        DWORD written{};
        bool okay = WriteFile(handle, text.data(), DWORD(text.size()), &written, nullptr) && written == text.size();
        CloseHandle(handle);
        if (!okay) throw std::runtime_error("Owned canonical write failed.");
    }
};
void FilesystemChecks()
{
    OwnedCanonicalConfig shortPath;
    shortPath.Write(enabled);
    auto loaded = sw::LoadCanonicalConfigFile(shortPath.file);
    Check(loaded.ready() && loaded.extendedAttempted && !loaded.normalOpenError && !loaded.extendedOpenError &&
              loaded.config.experimentalOptIn && loaded.config.approvedRuntimeBuild == "25330290",
          "canonical and extended readers validate same owned opt-in with identical rights");
    Check(loaded.pathAbsolute && !loaded.pathEmbeddedNul && loaded.pathCharacters == shortPath.file.native().size() &&
              loaded.rawPathFingerprint[0], "masked diagnostics capture exact path shape and case-sensitive hash");
    Check(loaded.normalAttributeErrors == std::array<uint32_t, 3>{} &&
              loaded.extendedAttributeErrors == std::array<uint32_t, 3>{} &&
              (loaded.normalAttributes[0] & FILE_ATTRIBUTE_DIRECTORY) == 0 &&
              (loaded.normalAttributes[1] & FILE_ATTRIBUTE_DIRECTORY) &&
              (loaded.normalAttributes[2] & FILE_ATTRIBUTE_DIRECTORY),
          "file plus both parent attributes are captured through public APIs");
    auto missing = shortPath.file;
    DeleteFileW(shortPath.file.c_str());
    loaded = sw::LoadCanonicalConfigFile(missing);
    Check(!loaded.ready() && loaded.normalOpenError == ERROR_FILE_NOT_FOUND &&
              loaded.extendedOpenError == ERROR_FILE_NOT_FOUND && loaded.normalAttributeErrors[0] == ERROR_FILE_NOT_FOUND &&
              !loaded.normalAttributeErrors[1] && !loaded.normalAttributeErrors[2],
          "missing file diagnostic isolates failure from existing parent directories");
    RemoveDirectoryW(shortPath.file.parent_path().c_str());
    loaded = sw::LoadCanonicalConfigFile(missing);
    Check(!loaded.ready() && loaded.normalOpenError == ERROR_PATH_NOT_FOUND &&
              loaded.extendedOpenError == ERROR_PATH_NOT_FOUND && loaded.normalAttributeErrors[1] &&
              !loaded.normalAttributeErrors[2], "missing owned parent is distinguished from existing account parent");
    CreateDirectoryW(shortPath.file.parent_path().c_str(), nullptr);
    shortPath.Write(enabled);
    HANDLE lock = CreateFileW(shortPath.file.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    loaded = sw::LoadCanonicalConfigFile(shortPath.file);
    Check(lock != INVALID_HANDLE_VALUE && !loaded.ready() &&
              loaded.normalOpenError == ERROR_SHARING_VIOLATION && loaded.extendedOpenError == ERROR_SHARING_VIOLATION,
          "extended spelling cannot bypass a same-file sharing restriction");
    if (lock != INVALID_HANDLE_VALUE) CloseHandle(lock);
    shortPath.Write("{\"experimentalOptIn\":false,\"approvedRuntimeBuild\":\"25330290\"}");
    loaded = sw::LoadCanonicalConfigFile(shortPath.file);
    Check(loaded.ready() && !loaded.config.experimentalOptIn, "extended reader preserves explicit disabled opt-in");
    shortPath.Write("{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\",\"unknown\":1}");
    loaded = sw::LoadCanonicalConfigFile(shortPath.file);
    Check(loaded.stage == sw::ConfigLoadStage::Schema && loaded.extendedStage == sw::ConfigLoadStage::Schema &&
              !loaded.ready(), "extended reader never bypasses strict schema");
    shortPath.Write(std::string(sw::MaxConfigBytes + 1, ' '));
    loaded = sw::LoadCanonicalConfigFile(shortPath.file);
    Check(loaded.stage == sw::ConfigLoadStage::TooLarge && loaded.systemError == ERROR_FILE_TOO_LARGE,
          "extended reader retains4096byte bound");
    OwnedCanonicalConfig longPath(true);
    longPath.Write(enabled);
    loaded = sw::LoadCanonicalConfigFile(longPath.file);
    const auto longPathCharacters = loaded.pathCharacters;
    Check(loaded.pathCharacters > MAX_PATH && loaded.ready() && loaded.extendedAttempted && !loaded.extendedOpenError,
          "actual overMAX_PATH owned file validates through documented extended Win32 spelling");
    Check(loaded.extendedAttributeErrors == std::array<uint32_t, 3>{},
          "extended attributes cover long canonical file and both parents");
    std::filesystem::path extended;
    Check(!sw::CanonicalExtendedConfigPath(L"relative\\VRC-SWITCHEROONIE\\config\\driver.json", extended),
          "relative source cannot become alternate config location");
    Check(!sw::CanonicalExtendedConfigPath(L"C:\\owned.\\VRC-SWITCHEROONIE\\config\\driver.json", extended) &&
              !sw::CanonicalExtendedConfigPath(L"C:\\owned \\VRC-SWITCHEROONIE\\config\\driver.json", extended),
          "extended spelling rejects dot or space ambiguity");
    Check(!sw::CanonicalExtendedConfigPath(L"\\\\.\\NUL", extended) &&
              !sw::CanonicalExtendedConfigPath(L"\\\\?\\C:\\VRC-SWITCHEROONIE\\config\\driver.json", extended),
          "device and alreadyextended namespaces cannot bypass canonical source rule");
    Check(!sw::CanonicalExtendedConfigPath(L"C:\\Users\\owned\\AppData\\Local\\VRC-SWITCHEROONIE\\driver.json", extended),
          "legacy AppData layout is never an activation fallback");
    auto embedded = shortPath.file.native();
    embedded.push_back(0); embedded += L"suffix";
    loaded = sw::LoadCanonicalConfigFile(std::filesystem::path(embedded));
    Check(loaded.pathEmbeddedNul && loaded.stage == sw::ConfigLoadStage::Resolve &&
              loaded.systemError == ERROR_INVALID_NAME && !loaded.extendedAttempted,
          "embeddedNUL source is rejected before read, never truncated to a valid opt-in");
    std::array<char, 65> rawLower{}, rawUpper{}, foldedLower{}, foldedUpper{};
    Check(sw::ConfigRawPathFingerprint(L"C:\\Owned\\driver.json", rawLower) &&
              sw::ConfigRawPathFingerprint(L"C:\\OWNED\\DRIVER.JSON", rawUpper) && rawLower != rawUpper &&
              sw::ConfigPathFingerprint(L"C:\\Owned\\driver.json", foldedLower) &&
              sw::ConfigPathFingerprint(L"C:\\OWNED\\DRIVER.JSON", foldedUpper) && foldedLower == foldedUpper,
          "raw exactUTF16 fingerprint distinguishes case that folded fingerprint deliberately merges");
    std::cout << "Filesystem fixture: longPathChars=" << longPathCharacters << "; case flags only queried, never modified.\n";
}
struct FixtureStartup
{
    sw::StartupRetry retry{1000};
    unsigned reads{}, installs{};
    bool runtimeSupported{true}, interfacesPresent{true};
    bool CheckFile(int64_t now, const std::filesystem::path &path)
    {
        if (!retry.BeginCheck(now)) return false;
        ++reads;
        auto result = sw::LoadConfigFile(path);
        if (result.ready() && result.config.experimentalOptIn &&
            result.config.approvedRuntimeBuild == "25330290" && runtimeSupported && interfacesPresent)
        {
            if (retry.BeginInstall()) ++installs;
        }
        else retry.Retry(now);
        return true;
    }
};
void RetryChecks()
{
    FileFixture file;
    DeleteFileW(file.path.c_str());
    FixtureStartup initialMissing;
    Check(initialMissing.CheckFile(1000, file.path) && initialMissing.reads == 1 && !initialMissing.installs,
          "initial absent file remains closed and schedules retry");
    Check(!initialMissing.CheckFile(1999, file.path), "retry does not read every RunFrame");
    file.Write(enabled);
    Check(initialMissing.CheckFile(2000, file.path) && initialMissing.installs == 1,
          "owned file creation permits one strictly validated later attempt");
    Check(!initialMissing.CheckFile(999999, file.path) && initialMissing.installs == 1,
          "successful hook attempt permanently closes retry");
    FixtureStartup initiallyLocked;
    HANDLE lock = CreateFileW(file.path.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    Check(lock != INVALID_HANDLE_VALUE && initiallyLocked.CheckFile(1000, file.path) && !initiallyLocked.installs,
          "locked file never starts installation");
    if (lock != INVALID_HANDLE_VALUE) CloseHandle(lock);
    Check(initiallyLocked.CheckFile(2000, file.path) && initiallyLocked.installs == 1,
          "sharing lock release permits one later strict read");
    FixtureStartup disabled;
    file.Write("{\"experimentalOptIn\":false,\"approvedRuntimeBuild\":\"25330290\"}");
    Check(disabled.CheckFile(1000, file.path) && !disabled.installs, "disabled schema cannot activate");
    Check(disabled.CheckFile(2000, file.path) && !disabled.installs, "retry never bypasses disabled opt-in");
    file.Write(enabled);
    Check(disabled.CheckFile(4000, file.path) && disabled.installs == 1,
          "only explicit later opt-in permits installation");
    FixtureStartup malformed;
    file.Write("{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\",\"extra\":true}");
    Check(malformed.CheckFile(1000, file.path) && !malformed.installs,
          "schema refusal stays fail closed during retry");
    FixtureStartup approvedMismatch;
    file.Write("{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"1\"}");
    Check(approvedMismatch.CheckFile(1000, file.path) && !approvedMismatch.installs,
          "configuration build mismatch cannot activate");
    FixtureStartup installedMismatch;
    file.Write(enabled);
    installedMismatch.runtimeSupported = false;
    Check(installedMismatch.CheckFile(1000, file.path) && !installedMismatch.installs,
          "actual installed build mismatch cannot activate");
    FixtureStartup absentInterface;
    absentInterface.interfacesPresent = false;
    Check(absentInterface.CheckFile(1000, file.path) && !absentInterface.installs,
          "interface gate cannot be bypassed by retry");
    FixtureStartup cancelled;
    cancelled.retry.Cancel();
    Check(!cancelled.CheckFile(1000, file.path) && !cancelled.reads && !cancelled.installs,
          "cleanup cancellation prevents future configuration and install work");
    sw::StartupRetry checking{1000};
    Check(checking.BeginCheck(1000), "check enters serialized provider gate");
    checking.Cancel();
    checking.Retry(1000);
    Check(!checking.BeginInstall() && !checking.BeginCheck(100000),
          "cleanup during configuration check prevents installation and rescheduling");
    sw::StartupRetry attempted{1000};
    Check(attempted.BeginCheck(1000) && attempted.BeginInstall(), "single attempt marks terminal before hooking");
    attempted.Retry(1000);
    Check(!attempted.BeginCheck(100000) && !attempted.BeginInstall(),
          "failed or partially installed hook transaction cannot be retried");
    sw::StartupRetry bounded{1000};
    const int64_t delays[] = {1000, 2000, 4000, 5000, 5000, 5000};
    int64_t now = 1000;
    for (auto delay : delays)
    {
        Check(bounded.BeginCheck(now), "scheduled startup check admitted");
        bounded.Retry(now);
        Check(bounded.deadline() - now == delay, "startup read backoff is modest and capped at five seconds");
        Check(!bounded.BeginCheck(now + delay - 1), "startup read never runs before deadline");
        now += delay;
    }
    Check(bounded.BeginCheck(100), "clock rollback cannot freeze startup indefinitely");
    sw::StartupRetry saturation{std::numeric_limits<int64_t>::max()};
    Check(saturation.BeginCheck(1), "saturation fixture starts");
    saturation.Retry(1);
    Check(saturation.deadline() == std::numeric_limits<int64_t>::max(), "retry deadline saturates without overflow");
}
bool ColdChild(const char *fingerprint)
{
    wchar_t executable[32768]{};
    auto length = GetModuleFileNameW(nullptr, executable, DWORD(std::size(executable)));
    if (!length || length >= std::size(executable)) return false;
    std::vector<std::wstring> entries;
    auto original = GetEnvironmentStringsW();
    if (!original) return false;
    for (auto at = original; *at; at += std::wcslen(at) + 1)
    {
        if (_wcsnicmp(at, L"LOCALAPPDATA=", 13) && _wcsnicmp(at, L"USERPROFILE=", 12))
            entries.emplace_back(at);
    }
    FreeEnvironmentStringsW(original);
    entries.emplace_back(L"LOCALAPPDATA=Z:\\owned-startup-test-no-account\\AppData\\Local");
    entries.emplace_back(L"USERPROFILE=Z:\\owned-startup-test-no-account");
    std::sort(entries.begin(), entries.end(), [](const auto &a, const auto &b) { return _wcsicmp(a.c_str(), b.c_str()) < 0; });
    std::vector<wchar_t> environment;
    for (const auto &entry : entries) { environment.insert(environment.end(), entry.begin(), entry.end()); environment.push_back(0); }
    environment.push_back(0);
    std::wstring command = L"\"" + std::wstring(executable) + L"\" --canonical-child ";
    for (const char *at = fingerprint; *at; ++at) command += wchar_t(*at);
    STARTUPINFOW startup{sizeof(startup)};
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(executable, command.data(), nullptr, nullptr, FALSE,
                         CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, environment.data(), nullptr,
                         &startup, &child)) return false;
    CloseHandle(child.hThread);
    auto wait = WaitForSingleObject(child.hProcess, 10000);
    if (wait != WAIT_OBJECT_0) { TerminateProcess(child.hProcess, 99); WaitForSingleObject(child.hProcess, 1000); }
    DWORD result{99};
    GetExitCodeProcess(child.hProcess, &result);
    CloseHandle(child.hProcess);
    return wait == WAIT_OBJECT_0 && result == 0;
}
void ContextChecks()
{
    sw::ConfigLoadResult context;
    std::filesystem::path canonical;
    uint32_t error{};
    Check(sw::CurrentUserConfigPath(canonical, error, context) && !error && context.pathFingerprint[0],
          "explicit process token resolves canonical known folder and masked fingerprint");
    Check(context.processSidKnown && context.effectiveSidKnown && context.effectiveMatchesProcess &&
              !context.threadImpersonated && !context.effectiveTokenError,
          "ordinary process identity facts are known and matched without exposing identifiers");
    Check(ColdChild(context.pathFingerprint.data()),
          "cold child with wrong inherited USERPROFILE and LOCALAPPDATA resolves same process-account folder");
    std::array<char, 65> same{};
    Check(sw::ConfigPathFingerprint(canonical.parent_path() / L"." / canonical.filename(), same) &&
              same == context.pathFingerprint,
          "fingerprint normalization removes dot path variation");
    auto read = sw::LoadCurrentUserConfig();
    std::cout << "Current-account loader: stage=" << sw::ConfigStageName(read.stage)
              << " systemError=" << read.systemError << " bytes=" << read.bytes
              << " schema=" << (read.ready() ? "valid" : "not-valid")
              << " pathFingerprint=" << read.pathFingerprint.data() << '\n';
    std::cout << "Current-account filesystem: rawPathFingerprint=" << read.rawPathFingerprint.data()
              << " pathChars=" << read.pathCharacters << " absolute=" << read.pathAbsolute
              << " embeddedNul=" << read.pathEmbeddedNul << " normalOpenError=" << read.normalOpenError
              << " extendedOpenError=" << read.extendedOpenError << " parentCaseKnown="
              << read.parentCaseKnown[0] << ',' << read.parentCaseKnown[1] << " parentCaseFlags="
              << read.parentCaseFlags[0] << ',' << read.parentCaseFlags[1] << '\n';
    Check(read.pathFingerprint == context.pathFingerprint && read.bytes <= sw::MaxConfigBytes &&
              read.processSidKnown && read.effectiveSidKnown && read.effectiveMatchesProcess,
          "current-account read propagates canonical context with bounded success or fail-closed result");
    std::atomic<bool> impersonationCreated{}, resolverStayedCanonical{}, identityDifferent{}, restored{};
    std::thread isolated([&] {
        if (!ImpersonateAnonymousToken(GetCurrentThread())) return;
        impersonationCreated = true;
        sw::ConfigLoadResult anonymous;
        std::filesystem::path resolved;
        uint32_t failure{};
        const bool okay = sw::CurrentUserConfigPath(resolved, failure, anonymous);
        std::cout << "Anonymous fixture: resolved=" << okay << " systemError=" << failure
                  << " processSidKnown=" << anonymous.processSidKnown
                  << " effectiveSidKnown=" << anonymous.effectiveSidKnown
                  << " effectiveMatchesProcess=" << anonymous.effectiveMatchesProcess
                  << " threadImpersonated=" << anonymous.threadImpersonated
                  << " effectiveTokenError=" << anonymous.effectiveTokenError << '\n';
        // Anonymous access can itself fail closed. It must never redirect to
        // another identity's folder or reuse an environment-derived fallback.
        resolverStayedCanonical = !okay ? failure != 0 : resolved == canonical && anonymous.pathFingerprint == context.pathFingerprint;
        identityDifferent = anonymous.threadImpersonated && !anonymous.processSidKnown &&
                            !anonymous.effectiveMatchesProcess &&
                            !okay && failure == ERROR_ACCESS_DENIED;
        restored = RevertToSelf() != FALSE;
    });
    isolated.join();
    Check(impersonationCreated && restored, "owned isolated anonymous impersonation is restored before thread exit");
    Check(resolverStayedCanonical, "wrong effective identity cannot redirect process-token canonical resolution");
    Check(identityDifferent, "inaccessible process token under anonymous context never claims an identity match");
    std::atomic<bool> ownedTokenFacts{}, ownedTokenRestored{};
    std::thread ownedToken([&] {
        HANDLE process{}, duplicate{};
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE,
                              &process)) return;
        if (DuplicateTokenEx(process, TOKEN_QUERY | TOKEN_IMPERSONATE, nullptr,
                              SecurityImpersonation, TokenImpersonation, &duplicate) &&
            SetThreadToken(nullptr, duplicate))
        {
            sw::ConfigLoadResult impersonated;
            std::filesystem::path resolved;
            uint32_t failure{};
            ownedTokenFacts = sw::CurrentUserConfigPath(resolved, failure, impersonated) && !failure &&
                              resolved == canonical && impersonated.processSidKnown &&
                              impersonated.effectiveSidKnown && impersonated.effectiveMatchesProcess &&
                              impersonated.threadImpersonated && !impersonated.effectiveTokenError;
            ownedTokenRestored = RevertToSelf() != FALSE;
        }
        if (duplicate) CloseHandle(duplicate);
        CloseHandle(process);
    });
    ownedToken.join();
    Check(ownedTokenFacts && ownedTokenRestored,
          "readable owned impersonation reports known matched SID facts and stable process folder");
}
} // namespace
int main(int argc, char **argv)
{
    if (argc == 3 && !std::strcmp(argv[1], "--canonical-child"))
    {
        std::filesystem::path path;
        uint32_t error{};
        sw::ConfigLoadResult context;
        return sw::CurrentUserConfigPath(path, error, context) && !error &&
               !std::strcmp(context.pathFingerprint.data(), argv[2]) && context.processSidKnown &&
               context.effectiveMatchesProcess ? 0 : 1;
    }
    try { ContextChecks(); RetryChecks(); FilesystemChecks(); }
    catch (...) { Check(false, "startup context fixture unexpected exception"); }
    std::cout << "Startup context: " << checks << " checks, " << failures << " failures; no runtime or hook launch.\n";
    return failures ? 1 : 0;
}
