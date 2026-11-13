#pragma once
#include <memory>

namespace sw
{
// Caller holds the provider lifecycle lock. Once callback state is retained,
// every later cleanup must also retain its mapping/router/OpenVR context.
class ProviderCleanup
{
  public:
    template<class Hook> bool Remove(std::unique_ptr<Hook> &hooks)
    {
        if (retained_) return false;
        if (!hooks) return true;
        if (hooks->Remove())
        {
            hooks.reset();
            return true;
        }
        retained_ = true;
        failure_ = hooks->failure();
        hooks.release(); // Callback lifetime is now process lifetime.
        return false;
    }
    const char *failure() const noexcept { return failure_; }
  private:
    bool retained_{};
    const char *failure_{"Stopped callback resources retained until process exit"};
};
} // namespace sw
