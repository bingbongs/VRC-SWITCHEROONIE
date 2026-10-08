#include "known_hook.hpp"
#include "openvr_driver.h"
#include <array>
#include <bcrypt.h>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>
#include "hde64.h"
namespace sw
{
namespace
{
bool Readable(void *address, size_t count) noexcept
{
    MEMORY_BASIC_INFORMATION m{};
    return VirtualQuery(address, &m, sizeof(m)) && m.State == MEM_COMMIT &&
           !(m.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
           reinterpret_cast<uintptr_t>(address) + count <=
               reinterpret_cast<uintptr_t>(m.BaseAddress) + m.RegionSize;
}
bool IsOriginalTrampoline(void *candidate, void *entry) noexcept
{
    MEMORY_BASIC_INFORMATION m{};
    if (!VirtualQuery(candidate, &m, sizeof(m)) || m.Type != MEM_PRIVATE ||
        m.State != MEM_COMMIT ||
        !(m.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE)) ||
        !Readable(candidate, 64))
        return false;
    auto code = static_cast<unsigned char *>(candidate);
    for (size_t at = 0; at < 48;)
    {
        hde64s instruction{};
        if (!hde64_disasm(code + at, &instruction) || instruction.flags & F_ERROR)
            return false;
        if (instruction.opcode == 0xFF && instruction.modrm == 0x25 &&
            instruction.disp.disp32 == 0 && instruction.len == 6)
        {
            void *back{};
            std::memcpy(&back, code + at + 6, 8);
            auto delta = reinterpret_cast<intptr_t>(back) - reinterpret_cast<intptr_t>(entry);
            return at >= 5 && delta >= 5 && delta <= 32;
        }
        if (instruction.opcode == 0xE9 || instruction.opcode == 0xEB ||
            instruction.opcode == 0xC3 || instruction.opcode == 0xC2)
            return false;
        at += instruction.len;
    }
    return false;
}
std::string Hash(const std::filesystem::path &path)
{
    std::ifstream file(path, std::ios::binary);
    if (!file)
        return {};
    BCRYPT_ALG_HANDLE algorithm{};
    BCRYPT_HASH_HANDLE hash{};
    DWORD objectSize{}, received{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
        return {};
    std::string result;
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
                          reinterpret_cast<PUCHAR>(&objectSize), sizeof(objectSize), &received,
                          0) >= 0)
    {
        std::vector<unsigned char> object(objectSize);
        if (BCryptCreateHash(algorithm, &hash, object.data(), objectSize, nullptr, 0, 0) >= 0)
        {
            std::array<char, 65536> bytes{};
            bool valid = true;
            while (file)
            {
                file.read(bytes.data(), bytes.size());
                auto size = file.gcount();
                if (size && BCryptHashData(hash, reinterpret_cast<PUCHAR>(bytes.data()),
                                           static_cast<ULONG>(size), 0) < 0)
                {
                    valid = false;
                    break;
                }
            }
            std::array<unsigned char, 32> digest{};
            if (valid && file.eof() && BCryptFinishHash(hash, digest.data(),
                                                       static_cast<ULONG>(digest.size()), 0) >= 0)
            {
                constexpr char hex[] = "0123456789abcdef";
                for (auto b : digest)
                {
                    result += hex[b >> 4];
                    result += hex[b & 15];
                }
            }
            BCryptDestroyHash(hash);
        }
    }
    BCryptCloseAlgorithmProvider(algorithm, 0);
    return result;
}
} // namespace
bool ResolveMinHookDestination(void *entry, void *&destination) noexcept
{
    destination = nullptr;
    if (!Readable(entry, 5) || *static_cast<unsigned char *>(entry) != 0xE9)
        return false;
    int32_t relative{};
    std::memcpy(&relative, static_cast<unsigned char *>(entry) + 1, 4);
    auto relay = static_cast<unsigned char *>(entry) + 5 + relative;
    // Exact x64 MinHook JMP_ABS relay only; no arbitrary branch traversal.
    if (!Readable(relay, 14) || relay[0] != 0xFF || relay[1] != 0x25 || relay[2] || relay[3] ||
        relay[4] || relay[5])
        return false;
    std::memcpy(&destination, relay + 6, 8);
    return Readable(destination, 16);
}
bool ResolveOriginalPoseTrampoline(void *entry, HMODULE owner, void *&trampoline) noexcept
{
    trampoline = nullptr;
    void *destination{};
    if (!ResolveMinHookDestination(entry, destination))
        return false;
    DWORD64 base{};
    auto function = RtlLookupFunctionEntry(reinterpret_cast<DWORD64>(destination), &base, nullptr);
    if (!function || reinterpret_cast<HMODULE>(base) != owner)
        return false;
    auto start = reinterpret_cast<unsigned char *>(base + function->BeginAddress);
    size_t size = function->EndAddress - function->BeginAddress;
    if (start != destination || size == 0 || size > 4096 || !Readable(start, size))
        return false;
    for (size_t at = 0; at < size;)
    {
        hde64s instruction{};
        if (!Readable(start + at, 16) || !hde64_disasm(start + at, &instruction) ||
            instruction.flags & F_ERROR || at + instruction.len > size)
            return false;
        if ((instruction.opcode == 0xFF || instruction.opcode == 0x8B) &&
            (instruction.modrm & 0xC7) == 0x05 && instruction.flags & F_DISP32)
        {
            auto storage = start + at + instruction.len +
                           static_cast<int32_t>(instruction.disp.disp32);
            MEMORY_BASIC_INFORMATION global{};
            void *candidate{};
            if (Readable(storage, 8) && VirtualQuery(storage, &global, sizeof(global)) &&
                global.AllocationBase == owner)
            {
                std::memcpy(&candidate, storage, 8);
                if (IsOriginalTrampoline(candidate, entry))
                {
                    if (trampoline && trampoline != candidate)
                        return false; // ambiguous globals are never guessed
                    trampoline = candidate;
                }
            }
        }
        at += instruction.len;
    }
    return trampoline != nullptr;
}
bool ApproveSpaceCalibrator(void *entry, PoseChainApproval &approval)
{
    void *destination{};
    if (!ResolveMinHookDestination(entry, destination))
        return false;
    HMODULE module{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(destination), &module))
        return false;
    wchar_t actual[MAX_PATH]{}, executable[MAX_PATH]{};
    if (!GetModuleFileNameW(module, actual, MAX_PATH) ||
        !GetModuleFileNameW(nullptr, executable, MAX_PATH))
        return false;
    auto runtime = std::filesystem::path(executable).parent_path().parent_path().parent_path();
    auto expected = runtime.parent_path() / L"01spacecalibrator" / L"bin" / L"win64" /
                    L"driver_01spacecalibrator.dll";
    if (_wcsicmp(expected.lexically_normal().c_str(),
                 std::filesystem::path(actual).lexically_normal().c_str()) ||
        Hash(actual) != ApprovedSpaceCalibratorSha256)
        return false;
    using Factory = void *(*)(const char *, int *);
    auto factory = reinterpret_cast<Factory>(GetProcAddress(module, "HmdDriverFactory"));
    if (!factory)
        return false;
    int error{};
    auto provider = factory(vr::IServerTrackedDeviceProvider_Version, &error);
    if (!provider || error)
        return false;
    auto cleanup = (*reinterpret_cast<void ***>(provider))[1];
    HMODULE cleanupModule{};
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(cleanup), &cleanupModule) ||
        cleanupModule != module)
        return false;
    void *inner{};
    if (!ResolveOriginalPoseTrampoline(entry, module, inner))
        return false;
    // Code remains resident until process exit. Our Cleanup guard drains our pose callbacks
    // before this provider frees its own trampoline; pinning alone would not suffice.
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                           reinterpret_cast<LPCWSTR>(destination), &module))
        return false;
    approval = {module, cleanup, inner, destination};
    return true;
}
} // namespace sw
