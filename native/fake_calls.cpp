#include "fake_calls.hpp"
__declspec(noinline) void CallPose(vr::IVRServerDriverHost *h, uint32_t index,
                                   const vr::DriverPose_t &p)
{
    h->TrackedDevicePoseUpdated(index, p, sizeof(p));
}
__declspec(noinline) vr::EVRInputError CallCreateBool(vr::IVRDriverInput *i, uint64_t c,
                                                      const char *n, uint64_t *h)
{
    return i->CreateBooleanComponent(c, n, h);
}
__declspec(noinline) vr::EVRInputError CallUpdateBool(vr::IVRDriverInput *i, uint64_t h, bool v)
{
    return i->UpdateBooleanComponent(h, v, 0);
}
__declspec(noinline) vr::EVRInputError CallCreateScalar(vr::IVRDriverInput *i, uint64_t c,
                                                        const char *n, uint64_t *h)
{
    return i->CreateScalarComponent(c, n, h, vr::VRScalarType_Absolute,
                                    vr::VRScalarUnits_NormalizedTwoSided);
}
__declspec(noinline) vr::EVRInputError CallUpdateScalar(vr::IVRDriverInput *i, uint64_t h, float v)
{
    return i->UpdateScalarComponent(h, v, 0);
}
