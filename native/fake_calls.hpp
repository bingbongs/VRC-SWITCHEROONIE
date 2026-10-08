#pragma once
#include "openvr_driver.h"
void CallPose(vr::IVRServerDriverHost *, uint32_t, const vr::DriverPose_t &);
vr::EVRInputError CallCreateBool(vr::IVRDriverInput *, uint64_t, const char *, uint64_t *);
vr::EVRInputError CallUpdateBool(vr::IVRDriverInput *, uint64_t, bool);
vr::EVRInputError CallCreateScalar(vr::IVRDriverInput *, uint64_t, const char *, uint64_t *);
vr::EVRInputError CallUpdateScalar(vr::IVRDriverInput *, uint64_t, float);
