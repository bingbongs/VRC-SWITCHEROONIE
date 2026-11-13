#pragma once
#include "protocol.hpp"
#include <string>
#include <windows.h>
namespace sw
{
int64_t QpcNow() noexcept;
int64_t QpcFrequency() noexcept;
std::wstring UserSid();
std::wstring MappingName();
class Mapping
{
  public:
    ~Mapping();
    bool Open(bool create);
    void Close();
    void *request() const noexcept
    {
        return memory_;
    }
    void *status() const noexcept
    {
        return memory_ ? static_cast<std::byte *>(memory_) + StatusOffset : nullptr;
    }

  private:
    HANDLE handle_{};
    void *memory_{};
};
} // namespace sw
