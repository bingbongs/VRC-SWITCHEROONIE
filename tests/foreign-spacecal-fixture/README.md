# Isolated foreign calibration hook fixture

This executable uses fake OpenVR host/input interfaces in its own process. A separate test DLL links its own static MinHook copy, records incoming physical poses, adds a world-space X translation of 10 metres, then forwards through its global original-pose trampoline. The program does not load, register, start, restart, or contact SteamVR or VRChat.

The fixture models the relevant call order in the installed Space Calibrator 1.5 family. It is original test code, not a rebuild or compatibility certification of that driver. Reviewed primary source:

- [InterfaceHookInjector.cpp](https://raw.githubusercontent.com/hyblocker/OpenVR-SpaceCalibrator/v1.5.1/src/driver/InterfaceHookInjector.cpp): pose copy, calibration handler, global original-function call.
- [ServerTrackedDeviceProvider.cpp](https://raw.githubusercontent.com/hyblocker/OpenVR-SpaceCalibrator/v1.5.1/src/driver/ServerTrackedDeviceProvider.cpp): shared-memory pose capture precedes calibration; cleanup removes foreign hooks.
- [Hooking.h](https://raw.githubusercontent.com/hyblocker/OpenVR-SpaceCalibrator/v1.5.1/src/driver/Hooking.h): original function storage and MinHook trampoline removal.

The test bypasses production DLL path/hash approval only through the explicit isolated fixture parameter. It still uses the actual bounded x64 instruction decoder to resolve the original trampoline, compared against an independent fixture export. It checks that the runtime pose entry retains its foreign patch, physical raw capture remains independent during desktop routing, calibrated physical capture receives the transform once, synthetic output skips the calibrator's raw feed, generic trackers retain calibration, and owned removal preserves the foreign driver.

The last case holds a foreign callback after raw capture/calibration but before its original-function call, then concurrently invokes provider cleanup. Cleanup must wait for that full foreign callback, reject new foreign entries while stopping, and free the foreign trampoline only after the pending callback completes. The stopping guard is intentionally retained until this test process exits.

Three additional invocations pause public `Tick` inside actual pose output, public
`Submit` inside actual pose output, and public `Tick` inside actual controller
input output. Each concurrently invokes foreign cleanup, checks that cleanup
waits, and verifies that new `Tick`/`Submit` calls produce no output during or
after cleanup. Separate processes keep each terminal retained guard isolated.

Build with the repository's native CMake project, then run:

```powershell
ctest --test-dir build/native -C Release -R isolated-foreign --output-on-failure
```

Passing this test demonstrates the isolated software chain and lifetime behaviour. It does not demonstrate actual headset/controller behaviour, continuous calibration on the user's installed driver, physical/desktop switching in VRChat, or display-independent startup.
