#include "ipc.hpp"
#include <sddl.h>
#include <vector>
namespace sw
{
int64_t QpcNow() noexcept
{
    LARGE_INTEGER n;
    QueryPerformanceCounter(&n);
    return n.QuadPart;
}
int64_t QpcFrequency() noexcept
{
    LARGE_INTEGER n;
    QueryPerformanceFrequency(&n);
    return n.QuadPart;
}
std::wstring UserSid()
{
    HANDLE token{};
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        return {};
    DWORD size{};
    GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::vector<std::byte> buffer(size);
    if (!GetTokenInformation(token, TokenUser, buffer.data(), size, &size))
    {
        CloseHandle(token);
        return {};
    }
    LPWSTR text{};
    auto user = reinterpret_cast<TOKEN_USER *>(buffer.data());
    std::wstring result;
    if (ConvertSidToStringSidW(user->User.Sid, &text))
    {
        result = text;
        LocalFree(text);
    }
    CloseHandle(token);
    return result;
}
std::wstring MappingName()
{
    auto sid = UserSid();
    return sid.empty() ? L"" : L"Local\\VRC-SWITCHEROONIE-" + sid;
}
Mapping::~Mapping()
{
    Close();
}
bool Mapping::Open(bool create)
{
    Close();
    auto name = MappingName();
    if (name.empty())
        return false;
    if (create)
    {
        PSECURITY_DESCRIPTOR descriptor{};
        auto dacl = L"D:P(A;;GA;;;" + UserSid() + L")(A;;GA;;;SY)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(dacl.c_str(), SDDL_REVISION_1,
                                                                  &descriptor, nullptr))
            return false;
        SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
        handle_ = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0,
                                     MappingBytes, name.c_str());
        LocalFree(descriptor);
    }
    else
        handle_ = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name.c_str());
    if (!handle_)
        return false;
    memory_ = MapViewOfFile(handle_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, MappingBytes);
    if (!memory_)
    {
        Close();
        return false;
    }
    return true;
}
void Mapping::Close()
{
    if (memory_)
        UnmapViewOfFile(memory_);
    if (handle_)
        CloseHandle(handle_);
    memory_ = nullptr;
    handle_ = nullptr;
}
} // namespace sw
