# Experimental SteamVR server backend

This Windows x64 backend changes control sources inside `vrserver.exe` using its driver interface methods. It registers no HMD, controller, tracker, display component, or virtual display. The connected vendor HMD continues to own rendering and its stereo display. Controlled-scene stereo, switching and repaired held-view checks have live evidence on the selected route. The later VRChat F3 failed, and the replacement controls candidate is unverified live; the allowlist is not a compatibility certification.

## Activation and exact gate

The package does not register the driver or start/restart SteamVR automatically. After explicitly opting in and registering it while SteamVR is stopped, the companion driver can load at the next normal runtime start. Its sole configuration is the process-token Profile known folder followed by `VRC-SWITCHEROONIE\config\driver.json`; AppData is not a fallback:

```json
{"experimentalOptIn":true,"approvedRuntimeBuild":"25330290"}
```

Only runtime buildid `25330290` is currently admitted by the compiled gate. The exact value is read from `appmanifest_250820.acf` above the actual running `vrserver.exe`, never inferred from an SDK version. The two interfaces must exactly be `IVRServerDriverHost_006` and `IVRDriverInput_004`. Any missing build manifest, different build, absent interface, disabled opt-in, existing method detour, or unexpected module implementation prevents hook installation. The driver reports the reason through IPC and SteamVR's driver log.

The default manifest's `alwaysActivate` loads this companion after registration even without its own devices; it is not a fake HMD presence claim. An empty HMD-presence list creates no display path. Scenario C (absent headset launch, attach later) remains unimplemented.

## Provenance and routing

`TrackedDevicePoseUpdated` is intercepted at the actual server-host interface's vtable method target. The original `DriverPose_t` argument is atomically captured before any synthetic output is passed through our trampoline. Client `GetDeviceToAbsoluteTrackingPose` results are never used as independent physical evidence.

Ordinary Desktop routes the active HMD at index 0, controllers with actual `LeftHand`/`RightHand` roles, and devices whose actual class is `GenericTracker`, admitted by current property-container identity. No headset, transport or tracker vendor-name list is used. Competing controller roles refuse that hand's routing. Other controller roles pass through ordinary Desktop; whole-rig spin transforms all identity-approved controller poses, including those other roles. Tracking references, eye tracking and haptics pass through. No serial, role, universe, calibration property, vendor setting or binding is changed. This shared SteamVR path supports vendor-neutral implementation; it does not certify an untested headset or transport.

In ordinary Desktop mode, generic trackers are reported disconnected and invalid so the application can fall back to its non-FBT animation. The original vendor captures continue independently. Returning to VR, a cancelled route or an expired routing lease restores the latest captured physical poses; an old capture is never presented as fresh. Input disarm while Desktop remains committed keeps the trackers suspended, matching the held head view. The `genericTrackerAvailable` and `genericTrackerSuspended` counters distinguish current identities with matching captured sources from actual suspended output. Available is not a freshness claim. VRChat's freeze-on-disconnect option and IK can affect its visible fallback; normal avatar animation and calibration restoration require a live check.

Whole-rig spinning uses fresh head/controller/tracker captures and current identities together. Desktop entry captures an immutable complete tracker rig for that routing epoch. A missing or replaced identity blocks spinning until a new Desktop transaction; a stale or incomplete source blocks it as well. The head, hands and body are never intentionally given different spin transforms. This affects only poses entering SteamVR's server interface. OSC-only trackers sent directly to VRChat bypass it and need sender/relay cooperation.

On desktop entry, an immutable anchor is captured from the physical head. Its world-space position and yaw are retained, while physical pitch/roll are removed. Desktop height modifies global Y. Yaw and pitch fields are accumulated offsets in radians, with positive yaw looking right and positive pitch looking up. Presets 0/1 use resting hands; preset 2 places the right pointer near the view and left hand near the body. Crouch/prone act only while armed, derive height from the anchor, and never accumulate; prone takes precedence. Captured world Y does not establish the standing floor. Synthetic pose derivatives are zeroed; physical return passes the newest vendor pose through unchanged, including its transforms and timing.

Both the broker heartbeat and connected physical head stream must remain fresh within 200 ms. Missing/invalid data returns physical passthrough and neutralizes owned actions; headset sleep or stream termination is not claimed to preserve the scene. The last complete command may be used across a brief odd IPC publication, but only until that command's original timestamp expires. Armed=false retains the desktop head/hand pose while suppressing actions, which supports focus release and other desktop work.

Real-time callbacks allocate nothing, perform no file/network I/O, and never wait on mutexes. State publication uses lock-free 64-bit atomics, bounded seqlock reads, and bounded try-gates. An established desktop session has no shared state lock that could leak a physical pose or button during concurrent input/status callbacks.

## Input mapping and limits

The input hooks capture boolean/scalar/skeleton/pose-component creation and retain the original existing device/container identity. Selected hand input is suppressed during desktop mode. Synthesized controls follow the locally observed VRChat layouts: left stick X/Y for movement, right trigger for click/use or held trigger drag, right grip/squeeze for middle-mouse grab, right A for jump, left stick click for run, and one selected Y/B/application-menu click for Quick Menu. Esc queues that click; Tab is untouched. Right-held pointer emits no Grip by itself. Reserved `system` and menu touch components are neutral. OSC routing in the broker must zero the native locomotion/jump/run fields to prevent duplicate ownership. Action bits 32/64 select crouch/prone pose height rather than a guessed application button binding.

These are component-path mappings, not proof that an application's current binding uses those controls. The path selection is still binding-dependent. Skeleton updates are held at their previous shape during desktop mode, not regenerated as a tested open-hand skeleton. Controller pose-component offsets are held. A hand-pointer's projection, vendor grip/aim offsets, menu behavior, finger animation, and compatibility with custom bindings require the real controlled test scene and per-route application tests.

Creation callbacks that occurred before this companion installed its hooks cannot be recovered through the published input interface. Missing captured components yield status `UnmappedControllers`; driver load order must be validated, and this backend cannot claim an input capability from pose detection alone. On physical return, owned scalar/buttons remain zero until the corresponding physical input is released once, preventing a held physical control from inheriting synthetic action ownership.

Unknown patched targets fail closed instead of constructing a guessed hook chain. One exact approved SpaceCalibrator binary has an independently captured, post-calibration pose path with live resolution evidence; full FBT/calibration behavior remains a separate test. If another add-on changes a method after installation, synthesis is disabled, actions are neutralized, and the module/state is pinned so external trampolines are not invalidated. The foreign hook is never overwritten during cleanup. This conflict needs a clean runtime exit to fully remove the backend; other add-on hashes remain refused.

## Shared memory

`Local\VRC-SWITCHEROONIE-{current user SID}` is 4096 bytes. Creation uses a protected DACL for the current user and SYSTEM. The broker owns the 512-byte request at offset 0; the driver alone owns the 512-byte status at offset 2048. All fields are published/read as aligned atomic 64-bit words under odd/even sequence counters. `protocol.hpp` supplies precise offsets; both processes share QPC frequency.

Request epochs are monotonic mode transactions, independent from input heartbeats; restart seeds a new epoch from current QPC or adopts an existing committed route, and an existing map retains its sequence. Old epochs are rejected. `armed` owns actions only. Status at offset 60 extends the reserved word with capability bits: 1 pose hook, 2 input hooks, 4 left move component, 8 right trigger, 16 menu component, 32 HMD proximity, 64 body spin and 128 generic-tracker suspension/rig diagnostics. The additive fields are guarded by their capability. These bits report implemented or captured paths, not hardware-verified usability. Spin block reason 1 means the physical rig is stale or its Desktop anchor was incomplete; reason 2 means the Desktop rig identity changed; reason 3 means bounded native admission failed and SteamVR must restart. Spin attempt tokens use monotonic QPC, including inactive cancellation after broker restart. Refused or cancelled tokens remain retired; only a fresh greater token can resume. The broker resets rotation and requires a fresh activation rather than accumulating unseen motion.

Error values: 0 none, 1 missing/invalid head, 2 stale physical source, 3 stale broker command, 4 malformed/future command, 5 conflicting hook, 6 runtime/interface gate, 7 opt-in disabled, 8 IPC unavailable, 9 missing captured controller components.

## Build and isolated evidence

```powershell
cmake -S . -B build/native -G "Visual Studio 17 2022" -A x64
cmake --build build/native --config Release -j 8
ctest --test-dir build/native -C Release --output-on-failure
```

The driver is at `build/native/driver/switcheroonie/bin/win64/driver_switcheroonie.dll`; diagnostics and tests are in `build/native/Release`. Runtime registration is intentionally a separate operation.

`switcheroonie-native-tests` checks routing, 100 switches, independent physical storage, malformed/stale/future IPC, disarm/focus semantics, generic-tracker suspension/restoration, immutable rig identities and concurrent capture/input/pose/status calls. `switcheroonie-hook-tests` uses actual MinHook method interposition in its own isolated fake-host process and checks independent physical movement, synthetic head/input, generic-tracker output and raw-source separation, system/proximity isolation, clean physical reacquisition, 100 switches, a broker-loss deadline, hook removal and pre-existing-hook refusal. These tests never load a SteamVR scene or prove real F1-F5.

`switcheroonie-diagnostics [--ipc] [--output file.json]` is read-only. It initializes OpenVR as `VRApplication_Background` only if `vrserver.exe` already exists; otherwise it reports that probing was skipped. Device serials are hashed with a user-specific salt. Diagnostic client poses are explicitly post-routing and cannot prove F1. The controlled scene must be explicitly launched by the user during a hardware test window.

For the actual independent-source experiment, run `switcheroonie-diagnostics --record physical-output.jsonl --seconds 15 --hz 30` while SteamVR, this opted-in driver, and a controlled scene are already active. Bounds are 1–60 seconds and 1–120 Hz. The JSONL records QPC timing, exact SDK revision/runtime build, client head pose in `TrackingUniverseStanding` with zero prediction, and the driver's captured pre-output head pose with validity/heartbeat/hook flags, counters, mode, and transaction epoch. It contains no raw serials or scene identifiers. Driver coordinates may exclude the client's standing/chaperone transform, so compare independent changes before assuming absolute positions match. Move only the physical HMD while synthetic look is held, then hold the HMD while mouse look changes; record both distinct streams and observe stereo output. A generated capture file alone is not a passed F1 test. Recording refuses to start without an existing runtime and stops if that runtime disappears or changes process identity.
