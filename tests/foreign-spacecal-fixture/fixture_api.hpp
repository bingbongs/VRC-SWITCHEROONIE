#pragma once
#include <windows.h>
#include "openvr_driver.h"

// This isolated process-only fixture is never loaded by SteamVR or installed as a driver.
using FixtureInstallFn = BOOL (*)(void *poseTarget);
using FixtureReadPoseFn = BOOL (*)(uint32_t index, vr::DriverPose_t *pose);
using FixtureSetBarrierFn = void (*)(HANDLE reached, HANDLE resume);
using FixtureOriginalFn = void *(*)();
using FixtureCountFn = uint32_t (*)();
using FixtureFactoryFn = void *(*)(const char *interfaceVersion, int *error);

