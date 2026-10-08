#pragma once
#include <algorithm>
#include <cstdint>
#include <limits>
namespace sw
{
// Provider lifecycle gate. Its caller serializes Init/RunFrame/Cleanup. Never
// retry after beginning any hook install: partial/unknown ownership is terminal.
class StartupRetry
{
  public:
    enum class State { Waiting, Checking, Attempted, Cancelled };
    explicit StartupRetry(int64_t frequency = 1) noexcept : frequency_(std::max<int64_t>(1, frequency)) {}
    bool BeginCheck(int64_t now) noexcept
    {
        if (state_ != State::Waiting) return false;
        if (lastNow_ && now < lastNow_) deadline_ = now;
        lastNow_ = now;
        if (now < deadline_) return false;
        state_ = State::Checking;
        return true;
    }
    void Retry(int64_t now) noexcept
    {
        if (state_ != State::Checking) return;
        const int64_t seconds = retryCount_ == 0 ? 1 : retryCount_ == 1 ? 2 : retryCount_ == 2 ? 4 : 5;
        if (retryCount_ < 3) ++retryCount_;
        const auto maximum = std::numeric_limits<int64_t>::max();
        const auto delay = frequency_ > maximum / seconds ? maximum : frequency_ * seconds;
        deadline_ = now > maximum - delay ? maximum : now + delay;
        lastNow_ = now;
        state_ = State::Waiting;
    }
    bool BeginInstall() noexcept
    {
        if (state_ != State::Checking) return false;
        state_ = State::Attempted;
        return true;
    }
    void Cancel() noexcept { state_ = State::Cancelled; }
    State state() const noexcept { return state_; }
    int64_t deadline() const noexcept { return deadline_; }
  private:
    int64_t frequency_, deadline_{}, lastNow_{};
    unsigned retryCount_{};
    State state_{State::Waiting};
};
} // namespace sw
