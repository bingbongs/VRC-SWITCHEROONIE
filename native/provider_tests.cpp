#include "openvr_driver.h"
#include "provider_cleanup.hpp"
#include <atomic>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <windows.h>

extern "C" void *HmdDriverFactory(const char *, int *) noexcept;
namespace
{
unsigned checks{}, failures{};
void Check(bool good, const char *message)
{
    ++checks;
    if (!good) { ++failures; std::cerr << "FAIL " << message << '\n'; }
}
struct MissingContext final : vr::IVRDriverContext
{
    std::atomic<unsigned> queries{};
    bool throws{}, blocked{};
    std::atomic<bool> entered{}, resume{};
    void *GetGenericInterface(const char *, vr::EVRInitError *error) override
    {
        ++queries; entered = true;
        if (blocked)
        {
            const auto start = GetTickCount64();
            while (!resume && GetTickCount64() - start < 2000) Sleep(1);
        }
        if (throws) throw std::runtime_error("Private missing-interface fixture");
        if (error) *error = vr::VRInitError_Init_InterfaceNotFound;
        return nullptr; // InitServerDriverContext fails before IPC/config/host access.
    }
    vr::DriverHandle_t GetDriverHandle() override { return vr::k_ulInvalidDriverHandle; }
};
struct CallbackFixture
{
    bool removable;
    unsigned &removals, &destructions;
    bool Remove() { ++removals; return removable; }
    const char *failure() const { return "private outstanding callback"; }
    ~CallbackFixture() { ++destructions; }
};
void RetainedResources()
{
    unsigned removals{}, destructions{}, closes{};
    sw::ProviderCleanup cleanup;
    auto hooks = std::make_unique<CallbackFixture>(false, removals, destructions);
    auto retained = hooks.get(); // Only this fixture can retire its fake callback.
    if (cleanup.Remove(hooks)) ++closes;
    Check(!hooks && removals == 1 && destructions == 0 && closes == 0,
          "failed hook removal retains callback and dependent resources");
    retained->removable = true;
    for (unsigned i = 0; i != 3; ++i)
        if (cleanup.Remove(hooks)) ++closes;
    Check(removals == 1 && destructions == 0 && closes == 0,
          "repeated cleanup cannot free a previously retained mapping or retry released state");
    Check(std::strcmp(cleanup.failure(), "private outstanding callback") == 0,
          "retained cleanup keeps its original diagnostic");
    delete retained;

    sw::ProviderCleanup normal;
    hooks = std::make_unique<CallbackFixture>(true, removals, destructions);
    Check(normal.Remove(hooks) && !hooks && removals == 2 && destructions == 2,
          "successful cleanup destroys only a drained hook");
    Check(normal.Remove(hooks) && removals == 2 && destructions == 2,
          "repeated successful cleanup is harmless");
}
}
int main(int argc, char **argv)
{
    RetainedResources();
    int error{};
    Check(HmdDriverFactory("not-an-interface", &error) == nullptr && error == vr::VRInitError_Init_InterfaceNotFound,
          "unrecognized factory interface is refused");
    auto provider = static_cast<vr::IServerTrackedDeviceProvider *>(HmdDriverFactory(vr::IServerTrackedDeviceProvider_Version, &error));
    Check(provider && error == vr::VRInitError_None, "actual driver factory returns its provider");
    if (!provider) return 1;
    Check(provider->Init(nullptr) == vr::VRInitError_Driver_Failed, "null provider context is refused without dereferencing it");
    MissingContext first, second;
    const std::string scenario = argc > 1 ? argv[1] : "failure";
    first.throws = scenario == "exception";
    if (scenario == "concurrent")
    {
        first.blocked = true;
        vr::EVRInitError a{}, b{};
        std::thread initial([&] { a = provider->Init(&first); });
        const auto start = GetTickCount64();
        while (!first.entered && GetTickCount64() - start < 1000) Sleep(1);
        std::thread duplicate([&] { b = provider->Init(&second); });
        first.resume = true;
        initial.join(); duplicate.join();
        Check(a != vr::VRInitError_None && b != vr::VRInitError_None && first.queries > 0 && second.queries == 0,
              "competing Init calls cannot replace the first provider context");
    }
    else
    {
        Check(provider->Init(&first) != vr::VRInitError_None && first.queries > 0,
              "initial missing/throwing interface is rejected before configuration or mapping access");
        Check(provider->Init(&second) != vr::VRInitError_None && second.queries == 0,
              "a second Init is refused before touching a replacement context or reopening resources");
    }
    provider->Cleanup(); provider->Cleanup();
    Check(!provider->ShouldBlockStandbyMode(), "repeated cleanup leaves the provider stopped");
    Check(provider->Init(&second) != vr::VRInitError_None && second.queries == 0,
          "cleanup cannot reset the process-lifetime initialization boundary");
    const auto beforeFrame = second.queries.load();
    provider->RunFrame();
    Check(second.queries == beforeFrame, "stopped provider performs no late interface work");
    std::cout << "Provider lifecycle (" << scenario << "): " << checks << " checks, " << failures
              << " failures; actual provider with missing fake interfaces, no IPC, config, runtime or hook launch.\n";
    return failures ? 1 : 0;
}
