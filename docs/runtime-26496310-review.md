# SteamVR build 26496310: static compatibility review

Reviewed on 2026-11-12. **The runtime remains unadmitted and the installed driver remains disabled.** This work advances compatibility evidence without loading a SteamVR library, starting a runtime/client, attaching to a process, installing a driver, changing settings, or disturbing the active desktop/game session.

## Direct observations

The installed Steam app manifest reports build `26496310` and branch `beta`. The production admission build is still `25330290`. The inspected server, client and API files have x64 PE headers. Their Windows version resources report the generic value `1.1.1`, so that value is not a useful substitute for the Steam build identifier and exact file hashes.

| Inspected file | SHA-256 |
|---|---|
| `bin/win64/vrserver.exe` | `3e5362b7588aef1bf903ad7ccbae8f3c26cb71a8bc9b5ceec37a996b152c6b1a` |
| `bin/vrclient_x64.dll` | `bc7992bc9f408513c615d5e1dd52e0921e9533b52402966f9df6a021168de30a` |
| `bin/win64/openvr_api.dll` | `407672065db4a6a3a50badac25af852ea3cef18917e460f952ab0ecbe884985b` |

The server file contains exact, NUL-terminated strings for all six public interface versions checked:

| Interface | Pinned/current public version | Occurrences in server file |
|---|---|---|
| Server provider | `IServerTrackedDeviceProvider_004` | 1 |
| Server host | `IVRServerDriverHost_006` | 1 |
| Driver input | `IVRDriverInput_004` | 1 |
| Properties | `IVRProperties_001` | 1 |
| Driver log | `IVRDriverLog_001` | 1 |
| Settings | `IVRSettings_003` | 1 |

The project-pinned driver header and [Valve's current public driver header](https://raw.githubusercontent.com/ValveSoftware/openvr/master/headers/openvr_driver.h), retrieved on the review date, match in full after CRLF/LF normalization. The downloaded raw file SHA-256 is `1036efe998d63e82d1d3db2b32a2f58df4a8eeaf5280f50aaf28220ff60a40ab`; the checkout's CRLF file SHA-256 is `914a17b6dbcc73d1cac5281d6253d5563879ec5029b5db43b9dfc6863b435528`. Separate comparison of the six interface declarations and `DriverPose_t`, `HmdQuaternion_t`, `HmdMatrix34_t`, and `VRBoneTransform_t` also matches.

**Inference:** no public SDK declaration change was found that calls for replacing the pinned header. These observations do not prove that the installed server implements an interface successfully, preserves a specific internal prologue, admits MinHook safely, or cooperates with the exact vendor/calibration drivers. Strings can exist without a usable interface implementation. Published headers are not the runtime binary's source code.

## Concrete changes supporting further evaluation

`tools/review_runtime.py` provides a repeatable read-only inventory. It bounds file reads, validates basic PE header bounds, hashes the exact files, extracts only named interface markers, compares the selected public declarations and records limitations. It refuses report output within the runtime installation or over an existing file. It never loads a DLL, opens a process, installs anything or grants runtime admission.

Five automated cases cover malformed/truncated PE data, architecture identification, ambiguous manifest fields, source declaration comparison, and a fabricated binary with every expected marker. Even matching build/marker/header evidence leaves `compatibilityEstablished` false. These are parser and evidence-boundary tests, not runtime execution tests.

`native/runtime_admission.hpp` now contains the exact production build predicate, shared with startup fixtures instead of duplicating the predicate in tests. Its behavior is unchanged. The 45-check `isolated-runtime-admission` suite verifies that the current beta remains rejected, including when `approvedRuntimeBuild` is manually set to `26496310`. Similar, empty and suffixed build identifiers also remain rejected. This test branch runs before any current-account configuration or child-process checks and uses only private temporary files.

The candidate driver compiles against the existing pinned SDK. All ten selected isolated native suites pass. The live build gate, SteamVR add-on disable, app opt-in, installer, security settings and installed files are preserved.

## Evidence locations and reproduction

The ignored local report `reports/steamvr-26496310-static-review-v2.json` contains hashes, marker counts, source matches and explicit coverage limits. The raw public header is retained as `reports/openvr-driver-reference.h`. `reports/tests-runtime-review.log` and `reports/tests-native-continuation.log` contain the passing results. No proprietary runtime binaries were copied into the repository or report.

Use explicit paths and a new output filename; the reviewer intentionally refuses overwriting an existing report:

```powershell
python tools\review_runtime.py --steamvr 'C:\Program Files (x86)\Steam\steamapps\common\SteamVR' --manifest 'C:\Program Files (x86)\Steam\steamapps\appmanifest_250820.acf' --reference-header reports\openvr-driver-reference.h --output reports\next-runtime-static-review.json
ctest --test-dir build\reliability-native -C Release --output-on-failure -R '^isolated-runtime-admission$'
```

## What still blocks admission

Before approving this exact runtime, obtain evidence for its actual host/input interface targets, method ownership and hook preparation, including the installed companion calibration chain. Then validate startup, failed startup, normal teardown and stalled/concurrent teardown in a controlled session where failure cannot interrupt the owner's active work. The fake-host tests cover our lifecycle behavior but cannot establish the behavior of external runtime calls or MinHook against the new binary.

After compatibility review and any required approval, the exact candidate still needs live checks on the intended headset/Virtual Desktop route: visible cursor behavior, latency, menu/wrist aiming, input release after failures and focus changes, held-view continuity, physical return, and avatar locomotion after switching/spin. Earlier inconclusive aiming traces do not justify a new pose correction. Native pose routing still affects the SteamVR device stream; this candidate does not add an application-identity gate.

No claim of support for build `26496310` is made. The missing live evidence was not replaced by a relaxed allowlist, speculative pose changes, driver activation or a runtime restart.
