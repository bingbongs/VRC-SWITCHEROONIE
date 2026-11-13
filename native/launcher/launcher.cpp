#include "launcher_config.hpp"
#include <windows.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <array>
#include <iterator>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace
{
struct FileHandle
{
    HANDLE value{INVALID_HANDLE_VALUE};
    ~FileHandle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    FileHandle() = default;
    FileHandle(const FileHandle &) = delete;
    FileHandle &operator=(const FileHandle &) = delete;
    FileHandle(FileHandle &&other) noexcept : value(std::exchange(other.value, INVALID_HANDLE_VALUE)) {}
    FileHandle &operator=(FileHandle &&other) noexcept
    {
        if (this != &other)
        {
            if (value != INVALID_HANDLE_VALUE) CloseHandle(value);
            value = std::exchange(other.value, INVALID_HANDLE_VALUE);
        }
        return *this;
    }
};
struct FindHandle
{
    HANDLE value{INVALID_HANDLE_VALUE};
    ~FindHandle() { if(value != INVALID_HANDLE_VALUE) FindClose(value); }
};
struct HashAlgorithm
{
    BCRYPT_ALG_HANDLE value{};
    ~HashAlgorithm() { if (value) BCryptCloseAlgorithmProvider(value, 0); }
};
struct HashHandle
{
    BCRYPT_HASH_HANDLE value{};
    ~HashHandle() { if (value) BCryptDestroyHash(value); }
};
enum class Failure { Missing, Link, Changed, Arguments, Launch, Unexpected };

int Fail(Failure reason, bool silent) noexcept
{
    if (!silent)
    {
        const wchar_t *message = L"The bundled app could not be verified. Extract a fresh copy of the complete ZIP.";
        switch (reason)
        {
        case Failure::Missing:
            message = L"The bundled App folder or app is missing. Extract the complete ZIP and try again."; break;
        case Failure::Link:
            message = L"This launcher and its App folder must be regular extracted files, not links."; break;
        case Failure::Changed:
            message = L"The bundled app does not match this launcher. Download and extract a fresh copy."; break;
        case Failure::Arguments:
            message = L"Open this app normally, or use --background."; break;
        case Failure::Launch:
            message = L"Windows could not start the bundled app. Try extracting the complete ZIP again."; break;
        default: break;
        }
        MessageBoxW(nullptr, message, L"VRC-SWITCHEROONIE", MB_OK | MB_ICONERROR);
    }
    return 1;
}

bool ModulePath(std::wstring &path)
{
    for (DWORD capacity = 512; capacity <= 32768; capacity *= 2)
    {
        std::vector<wchar_t> buffer(capacity);
        const auto length = GetModuleFileNameW(nullptr, buffer.data(), capacity);
        if (!length) return false;
        if (length < capacity)
        {
            path.assign(buffer.data(), length);
            return true;
        }
    }
    return false;
}

bool OpenOrdinary(const std::wstring &path, bool directory, FileHandle &handle, Failure &reason)
{
    // Deny write/delete sharing while verifying and creating the child. This
    // prevents replacing the verified file or its adjacent owned directories.
    handle.value = CreateFileW(path.c_str(), directory ? FILE_READ_ATTRIBUTES : GENERIC_READ,
                               FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                               FILE_FLAG_OPEN_REPARSE_POINT |
                                   (directory ? FILE_FLAG_BACKUP_SEMANTICS : FILE_FLAG_SEQUENTIAL_SCAN), nullptr);
    if (handle.value == INVALID_HANDLE_VALUE) { reason = Failure::Missing; return false; }
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(handle.value, &info)) { reason = Failure::Unexpected; return false; }
    if (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) { reason = Failure::Link; return false; }
    if (bool(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != directory)
    { reason = Failure::Missing; return false; }
    return true;
}

bool FileHash(HANDLE file, std::array<UCHAR, 32> &digest)
{
    LARGE_INTEGER length{};
    constexpr LONGLONG maximumBytes = 512LL * 1024 * 1024;
    if (!GetFileSizeEx(file, &length) || length.QuadPart < 0 || length.QuadPart > maximumBytes) return false;
    LARGE_INTEGER beginning{};
    if (!SetFilePointerEx(file, beginning, nullptr, FILE_BEGIN)) return false;
    HashAlgorithm algorithm;
    if (BCryptOpenAlgorithmProvider(&algorithm.value, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return false;
    DWORD objectBytes{}, resultBytes{};
    if (BCryptGetProperty(algorithm.value, BCRYPT_OBJECT_LENGTH,
                          reinterpret_cast<PUCHAR>(&objectBytes), sizeof(objectBytes), &resultBytes, 0) < 0 ||
        resultBytes != sizeof(objectBytes) || !objectBytes || objectBytes > 65536) return false;
    std::vector<UCHAR> object(objectBytes);
    HashHandle hash;
    if (BCryptCreateHash(algorithm.value, &hash.value, object.data(), objectBytes, nullptr, 0, 0) < 0) return false;
    std::array<UCHAR, 65536> bytes{};
    LONGLONG total{};
    for (;;)
    {
        DWORD read{};
        if (!ReadFile(file, bytes.data(), DWORD(bytes.size()), &read, nullptr)) return false;
        if (!read) break;
        total += read;
        if (total > length.QuadPart || BCryptHashData(hash.value, bytes.data(), read, 0) < 0) return false;
    }
    if (total != length.QuadPart || BCryptFinishHash(hash.value, digest.data(), DWORD(digest.size()), 0) < 0)
        return false;
    return true;
}
bool Matches(const std::array<UCHAR, 32> &digest, const char *expected) noexcept
{
    constexpr char hex[] = "0123456789ABCDEF";
    unsigned difference{};
    for (size_t index = 0; index < digest.size(); ++index)
    {
        difference |= unsigned(expected[index * 2] ^ hex[digest[index] >> 4]);
        difference |= unsigned(expected[index * 2 + 1] ^ hex[digest[index] & 15]);
    }
    return !difference;
}

bool VerifyPayload(const std::wstring &app, HANDLE outerFile, std::vector<FileHandle> &held,
                   Failure &reason)
{
    std::array<UCHAR, 32> wrapperHash{};
    if (!FileHash(outerFile, wrapperHash)) { reason=Failure::Changed; return false; }
    std::unordered_map<std::wstring, const char *> expected;
    for (const auto &pin : ExpectedPayload) expected.emplace(pin.path, pin.sha256);
    std::unordered_set<std::wstring> observed;
    struct Directory { std::wstring path, relative; unsigned depth; };
    std::vector<Directory> pending{{app,L"",0}};
    size_t directories=1, files=0;
    uint64_t totalBytes{};
    bool mirrorFound=false, metadataFound=false;
    while(!pending.empty())
    {
        auto current=std::move(pending.back());pending.pop_back();
        WIN32_FIND_DATAW entry{};
        FindHandle enumeration;
        enumeration.value=FindFirstFileExW((current.path+L"\\*").c_str(), FindExInfoBasic,
                                           &entry,FindExSearchNameMatch,nullptr,0);
        if(enumeration.value==INVALID_HANDLE_VALUE) { reason=Failure::Missing;return false; }
        for(;;)
        {
            std::wstring_view name(entry.cFileName);
            if(name!=L"."&&name!=L"..")
            {
                if(entry.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT) { reason=Failure::Link;return false; }
                const auto relative=current.relative.empty()?std::wstring(name):current.relative+L"/"+std::wstring(name);
                const auto absolute=current.path+L"\\"+std::wstring(name);
                if(relative.size()>1024) { reason=Failure::Changed;return false; }
                const bool directory=(entry.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)!=0;
                const auto pin=expected.find(relative);
                if(!directory&&pin==expected.end()&&relative!=L"portable/VRC-SWITCHEROONIE.exe"&&
                   relative!=L"package-hashes.json") { reason=Failure::Changed;return false; }
                FileHandle handle;
                if(!OpenOrdinary(absolute,directory,handle,reason)) return false;
                if(directory)
                {
                    if(++directories>8192||current.depth>=32) { reason=Failure::Changed;return false; }
                    pending.push_back({absolute,relative,current.depth+1});
                }
                else
                {
                    if(++files>std::size(ExpectedPayload)+2||!observed.emplace(relative).second)
                    { reason=Failure::Changed;return false; }
                    LARGE_INTEGER fileBytes{};
                    if(!GetFileSizeEx(handle.value,&fileBytes)||fileBytes.QuadPart<0)
                    { reason=Failure::Changed;return false; }
                    totalBytes+=uint64_t(fileBytes.QuadPart);
                    if(totalBytes>2ULL*1024*1024*1024) { reason=Failure::Changed;return false; }
                    if(pin!=expected.end())
                    {
                        std::array<UCHAR,32> digest{};
                        if(!FileHash(handle.value,digest)||!Matches(digest,pin->second))
                        { reason=Failure::Changed;return false; }
                        if(relative==L"VRC-SWITCHEROONIE.exe"&&!Matches(digest,ExpectedInnerSha256))
                        { reason=Failure::Changed;return false; }
                    }
                    else if(relative==L"portable/VRC-SWITCHEROONIE.exe")
                    {
                        std::array<UCHAR,32> digest{};
                        if(!FileHash(handle.value,digest)||digest!=wrapperHash)
                        { reason=Failure::Changed;return false; }
                        mirrorFound=true;
                    }
                    else if(relative==L"package-hashes.json")
                    {
                        LARGE_INTEGER length{};
                        if(!GetFileSizeEx(handle.value,&length)||length.QuadPart<=0||length.QuadPart>2*1024*1024)
                        { reason=Failure::Changed;return false; }
                        metadataFound=true;
                    }
                    else { reason=Failure::Changed;return false; }
                }
                held.push_back(std::move(handle));
            }
            if(!FindNextFileW(enumeration.value,&entry))
            {
                if(GetLastError()!=ERROR_NO_MORE_FILES) { reason=Failure::Unexpected;return false; }
                break;
            }
        }
    }
    if(!mirrorFound||!metadataFound||files!=expected.size()+2) { reason=Failure::Missing;return false; }
    for(const auto &pin:ExpectedPayload)
        if(!observed.contains(pin.path)) { reason=Failure::Missing;return false; }
    return true;
}

int Run(bool verifyOnly, bool background)
{
    std::wstring module;
    if (!ModulePath(module)) return Fail(Failure::Unexpected, verifyOnly);
    const auto separator = module.find_last_of(L"\\/");
    if (separator == std::wstring::npos) return Fail(Failure::Unexpected, verifyOnly);
    auto root = module.substr(0, separator);
    if(root.empty()) return Fail(Failure::Unexpected,verifyOnly);
    if(root.back()==L':') root+=L"\\";
    const auto app = root + (root.back()==L'\\'?L"App":L"\\App");
    const auto inner = app + L"\\VRC-SWITCHEROONIE.exe";
    Failure reason{};
    FileHandle rootDirectory, appDirectory, outerFile;
    if (!OpenOrdinary(root, true, rootDirectory, reason) ||
        !OpenOrdinary(module, false, outerFile, reason) ||
        !OpenOrdinary(app, true, appDirectory, reason)) return Fail(reason, verifyOnly);
    std::vector<FileHandle> payloadHandles;
    if (!VerifyPayload(app, outerFile.value, payloadHandles, reason)) return Fail(reason, verifyOnly);
    if (verifyOnly) return 0;
    std::wstring command = L"\"" + inner + L"\" --launch";
    if (background) command += L" --background";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(inner.c_str(), command.data(), nullptr, nullptr, FALSE, 0,
                        nullptr, app.c_str(), &startup, &process)) return Fail(Failure::Launch, false);
    CloseHandle(process.hThread);
    // CreateProcess returns before the apphost loads its managed/runtime
    // dependencies. Keep the verified read leases through this exact bootstrap
    // process's lifetime. Its ordinary --launch path starts the UI then exits.
    // Waiting owns no window/console, and never terminates the child or runtime.
    const auto wait=WaitForSingleObject(process.hProcess,INFINITE);
    CloseHandle(process.hProcess);
    return wait==WAIT_OBJECT_0?0:Fail(Failure::Launch,false);
}
} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int count{};
    auto **arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    bool silent{}, verifyOnly{}, background{}, valid{};
    if (arguments)
    {
        for (int index = 1; index < count; ++index)
            if (std::wstring_view(arguments[index]) == L"--verify-only") silent = true;
        valid = count == 1;
        if (count == 2)
        {
            verifyOnly = std::wstring_view(arguments[1]) == L"--verify-only";
            background = std::wstring_view(arguments[1]) == L"--background";
            valid = verifyOnly || background;
        }
        LocalFree(arguments);
    }
    if (!valid) return Fail(Failure::Arguments, silent);
    try { return Run(verifyOnly, background); }
    catch (...) { return Fail(Failure::Unexpected, verifyOnly); }
}
