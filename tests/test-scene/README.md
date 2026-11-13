# Controlled rendering test

`switcheroonie-test-scene.exe` is an original, small SteamVR scene for testing the
harness before VRChat. It renders a standing-space floor grid, colored cubes,
one rotating cube as a frame-continuity marker, and tracked-controller axes and
pointer rays. The monitor shows the left and right eye images side by side with
their aspect ratio preserved. Actual headset presentation is a separate human
observation.

It requires the pinned OpenVR import library/DLL in `third_party/openvr` and the
Windows SDK D3D11, DXGI, DirectXMath, and D3DCompiler libraries. It selects the
runtime GPU by the OpenVR DXGI adapter LUID, falling back to OpenVR's documented
adapter index only if no LUID is returned. It refuses a guessed adapter.
The DXGI factory uses `CreateDXGIFactory1` before device creation, as required by
the pinned Valve SDK's compositor `Submit` documentation. Legacy
`CreateDXGIFactory` can produce error 106 (`SharedTexturesNotSupported`). Eye
submissions use ordinary `ID3D11Texture2D` resources in `R8G8B8A8_UNORM`, one mip,
one sample, and no miscellaneous sharing flags. Startup logs record this setup;
the mirror title also shows both compositor submission result codes.

The application requests poses with `WaitGetPoses`, uses each eye's runtime
projection and inverse eye-to-head transform, converts OpenVR's column-vector
matrices into DirectXMath row-vector matrices, and submits both real D3D11 eye
textures. It changes no SteamVR settings, drivers, bindings, tracking origins,
streaming providers, or other applications. Escape closes this scene only.

## Non-disruptive automated checks

```powershell
cmake -S . -B build/native -G "Visual Studio 17 2022" -A x64
cmake --build build/native --config Release --target switcheroonie-test-scene
& .\build\native\Release\switcheroonie-test-scene.exe --self-test
```

Standalone build also works with `cmake -S tests/test-scene -B build/test-scene`.
The self-test actually compiles both vertex/pixel shader pairs and verifies pose
translation/inversion, projection representation, and stereo transform
composition. It initializes no OpenVR client, GPU device, or visible window.
It also checks that synthetic output diagnostics are unavailable when their
heartbeat/sample is stale, their epoch differs, routing is physical, or the pose
hook capability is absent.
This is automated math/shader evidence, not rendered or hardware evidence.

Real compositor texture acceptance is demonstrated by `leftSubmitError:0` and
`rightSubmitError:0` in an explicitly launched scene. Shader self-tests alone
do not establish texture sharing or headset presentation.

## Explicit controlled hardware test

Use one safe test window. Close other SteamVR scene applications first, including
VRChat; the controlled test is its own scene application and will take scene
focus only when explicitly launched. Start the selected SteamVR/headset route
yourself, using its normal controls. This executable checks that `vrserver.exe`
already exists before OpenVR scene initialization and has no runtime launch
command.

```powershell
& .\build\native\Release\switcheroonie-test-scene.exe --run
# Or choose a report path explicitly:
& .\build\native\Release\switcheroonie-test-scene.exe --run --log .\reports\controlled-scene.jsonl
```

Launching without `--run` displays help and performs no VR initialization.
The default report directory is
`%LOCALAPPDATA%\VRC-SWITCHEROONIE\test-scene`. Reports contain the same process ID
and monotonic frame/time counters throughout a session, head poses, controller
indices/roles/positions/legacy buttons/axes, compositor submission errors, and
driver status when the harness mapping is available. They exclude physical
serials, local usernames, network addresses, VRChat/user/world/instance IDs,
typed text, microphone data, and conversations. Reports remain local.

For F1, hold the desktop head output still while moving the actual headset, then
reverse the experiment. Compare `routedHeadStandingMatrix` with the separate
`capturedPhysicalHeadWorldPosition` and `capturedPhysicalHeadWorldQuaternionWxyz`
driver status channels. The latter are captured from the pre-output hook; the
real provider/callback order still requires hardware validation. Driver world
and client standing space may differ by a chaperone/origin transform. Compare
independent changes, not absolute pose equality, until origins are correlated.
Check `statusAlive`, `capturedPhysicalHeadAvailable`, head age, callback counts,
and validity before interpreting samples. `statusAlive` requires a driver
heartbeat newer than 500 ms. Physical head data is emitted only when the pose
hook capability bit is present and the captured head satisfies the native
200 ms freshness deadline; otherwise those position/quaternion fields are null.

The separate `routedSyntheticWorldPosition` and
`routedSyntheticWorldQuaternionWxyz` fields record the head pose actually
submitted by the harness, accompanied by `anchorEpoch`, `syntheticHeadEpoch`,
`syntheticHeadQpc`, and sample age. They are available only with a fresh driver
heartbeat, the pose-hook capability, desktop mode, a valid synthetic sample, a
sample epoch matching the acknowledged epoch, and sample age at most 200 ms.
The committed anchor epoch must also match the acknowledged epoch.
Otherwise they are null. Compare this output against physical capture and the
client `routedHeadStandingMatrix` to identify anchor changes or changes between
the driver submission and client standing space. Absolute coordinates still
require origin correlation.
Hook presence establishes availability, not completion of the real-provider F1
experiment.

For F2, observe correct left/right stereo and head motion in the physical
headset, switch to Desktop controls and exercise mouse head/hand movement, then
return to fresh physical tracking. Repeat at least 25 times in this one process.
The same process ID, increasing frame counters, an uninterrupted rotating cube,
and successful submissions help document continuity. Headset image geometry,
tracking comfort, controller interaction, and actual visual continuity must be
observed and recorded by a person; logs alone do not pass those checks.

Legacy controller button/axis queries are reported as available/unavailable.
This scene has no VRChat menu, locomotion system, OSC receiver, application
bindings, or full-body avatar. It does not prove VRChat menus, locomotion, FBT
calibration, multiplayer continuity, no-headset launch, later stereo display
attachment, stream-loss recovery, or transport changes. Those remain separate
application/route tests.

## Primary API references

- [Pinned OpenVR API](../../third_party/openvr/headers/openvr.h)
- [Valve's matrix usage example](https://github.com/ValveSoftware/openvr/wiki/Matrix-Usage-Example)
- [Valve's DirectX example](https://github.com/ValveSoftware/openvr/blob/master/samples/hellovr_dx12/hellovr_dx12_main.cpp)
- [Microsoft's DirectXMath matrix representation](https://learn.microsoft.com/en-us/windows/win32/api/directxmath/ns-directxmath-xmmatrix)
