# Reliability candidate, 2026-11-12

This development record describes the reliability improvements included in the initial public baseline. At the time of these checks, the candidate had not been signed, deployed, enabled in SteamVR, or accepted on a live headset. The original checkout and installed application were left alone. The black, heather gray and gold interface, pixel superhero mascot, and existing mode artwork are preserved.

## Evidence and changes

### Readiness that explains the actual blocker

The read-only installation inspection found the app's experimental opt-in enabled, but SteamVR's `driver_switcheroonie.enable` explicitly false. SteamVR build `26496310` also differs from the admitted build `25330290`. This is configuration and source evidence, not a live driver activation test. The existing setup logic only considered the app's opt-in and could call a supported-but-disabled installation ready.

`SteamVrDriverSettings` now parses SteamVR's own `enable` and `blocked_by_safe_mode` settings through the registered OpenVR configuration path. Missing, unreadable, malformed or ambiguous authority does not authorize setup. Disabled and crash-blocked states have no install/enable action. The existing final preflight rechecks these settings before any otherwise eligible setup action. The reader never writes them.

The panel exposes the blocker, Settings shows both actual and admitted build numbers, and the action opens setup review. Native heartbeat errors now reach the UI as an additive `NativeError` status property. A heartbeat alone no longer masks hook, compatibility, opt-in, IPC or configuration faults. A confirmed live route still takes precedence over disk settings that apply on the next startup.

The runtime allowlist, experimental opt-in, installer, registration, signed update policy and security settings are unchanged.

The follow-up [installed-build compatibility review](runtime-26496310-review.md) records exact runtime hashes, public interface evidence, and the admission tests. The full pinned SDK header matches Valve's current public header after newline normalization; this does not establish compatibility of the installed runtime's internal hook targets. Build `26496310` remains unadmitted.

### Native lifecycle waits and retained code

The previous hook implementation had four callback-drain loops with no deadline and an unbounded cleanup serialization lock. A callback that never returns could therefore indefinitely hold one of these cleanup paths. No retained trace proves this caused the owner's historical SteamVR disruption.

The candidate gives its own serialization and drain waits a shared 500 ms budget per cleanup attempt. If they cannot finish, synthesis stops and referenced state, trampolines and modules are retained until process exit. Foreign cleanup is withheld while a callback could still use its resources. Concurrent foreign cleanup is counted; its entry is recorded before waiting for the lock. Duplicate foreign cleanup has a single forwarding owner. Failure diagnostics distinguish retained/withheld cleanup from completed cleanup.

This deliberately trades a bounded resource leak until exit for avoiding freed callback code. It does **not** impose a hard deadline on external host calls, MinHook internals, the provider's outer lifecycle lock, or foreign cleanup code itself. There is no claim that every SteamVR shutdown hang is fixed.

The third pass found two further ownership problems in the provider. Another `Init` could reopen a mapping that retained callbacks still reference, and another `Cleanup` could close that mapping after the first cleanup released its hook pointer for retention. The provider now permits only its first non-null initialization attempt per process. Failed initialization stops frame work and clears an unused OpenVR context; recovery requires a fresh runtime process. Configuration-read retries within a valid first initialization remain intact. A shared cleanup owner remembers retention across initialization failure, partial installation and normal cleanup, so subsequent cleanup cannot free the retained mapping or context.

Three new tests link the actual driver provider to missing/throwing fake contexts and reproduce the former repeated/concurrent initialization behavior before the fix. They stop before configuration reads, shared mappings or hook installation. Additional cleanup-owner fixtures verify that retained callback state and dependent resources stay retained across repeated calls. These complement the real isolated hook timeout tests; they do not simulate a full live SteamVR provider lifecycle.

### Capture failure cannot republish movement

The broker previously computed an active movement frame, attempted cursor capture, and on an exception released input but then republished that already-computed frame later in the same tick. The new regression reproduces this against the unchanged baseline at its second assertion.

The candidate publishes neutral controls before platform teardown, discards a failed tick's movement/look frame, and detects capture that silently remains unavailable. A missing capture disarms input and requires a fresh click after recovery. A platform exception leaves controls disabled for explicit recovery. The selected Desktop view and saved height are preserved.

Visible captions distinguish active mouse look, menu pointing, released controls and disabled controls. The Release inputs tooltip and Help describe recovery after closing a visible VRChat menu. Hand pose math, automatic routing policy and avatar animation behavior are unchanged.

The follow-up separates cursor readiness from ownership: a failed look-to-pointer transition can retain the old clip solely for recovery while reporting controls unready. A full-client clip already owned by the authenticated game can support pointer input without being claimed, changed or restored by Switcheroonie. Both paths are exercised through the real broker and capture policy with fake cursor operations. Settings now shows the existing capture/recovery message beside the controls instead of leaving it in a collapsed legacy container.

### Preferences cannot silently enable controls or apply rejected values

The third pass reproduced `direct-input.json` containing `null` leaving controls enabled. Missing fields could also inherit enabled record defaults; routing preferences had the same automatic-switching default. Bounded readers now require explicit, valid control and automatic-switching fields, reject ambiguous known keys, and retain compatibility with older files that omit optional height or manual mode. Invalid files remain unchanged for review. Controls or Auto stay off and their settings diagnostic survives status polling until an explicit successful save.

Control, routing and spin writes now flush a unique sibling file before replacing the previous complete file. Failed saves preserve the existing bytes and earlier recovery files. Enabling controls/Auto, changing sensitivity, or changing height commits to live state only after its save succeeds. Disabling controls, pausing Auto or disabling spin still takes effect for the current run when persistence fails, with an explicit warning. Manual mode remains a session preference on save failure and no longer claims it was saved while waiting for tracking.

If a stop/pause cannot be saved, restarting can restore the older on-disk preference; resolve the displayed save failure before relying on it across restarts. The change does not alter the intended defaults for genuinely new installations, the routing policy, global SteamVR stream scope, or headset-free support.

## Verification

| Check | Result | Boundary |
|---|---|---|
| Native driver and hook fixtures build | Passed | Separate build tree; driver was not installed |
| Native routing, admission, provider, fake host and foreign DLL lifecycle | Third pass: 13 suites passed, including 45 admission checks and three provider scenarios | Fake hosts/contexts, cleanup-owner fixtures and private temporary files; no OpenVR client or SteamVR launch |
| Competing cleanup timeout and duplicate cleanup | 10 consecutive runs of each scenario passed | Isolated processes; retained-code fault injection |
| Managed broker/control suite | Third pass: 744 checks passed, including 79 preference checks | Private random mappings, temporary configuration, fake input and reserved ephemeral loopback receiver |
| Pure managed subset | Third pass: 420 checks passed | No OS input, IPC, network or process integration; subset of the full suite |
| Invalid controls and repeated provider initialization regressions | Reproduced before the third-pass fixes | Private files and actual provider with missing fake contexts; no production resources |
| Capture regression on original source | Reproduced | Same new test with the unmodified source snapshot |
| Compact state, startup ownership, resident retry fixtures | Previous pass: 143 + 34 + 22 checks passed | UI unchanged in third pass; no broker connection, hotkeys or live settings changes |
| Offscreen layouts | Previous pass: 51 fixtures passed | UI unchanged in third pass; 100/150/200% DPI, minimum panel size, readiness blockers and capture recovery |
| Managed builds | No warnings or errors | Installed SDK; no package feeds |
| Static runtime-review tool | 5 test cases passed | Bounded byte reads; malformed metadata and matching-but-not-certifying evidence covered |

The full managed suite now reserves an ephemeral loopback receiver instead of sending fixture OSC traffic to a fixed port that might belong to another application. `--self-test-offline` selects the pure subset before IPC or socket creation. Restricted-token access to private mappings failed; the full suite then passed under the normal user token without using production mappings.

Logs are in the isolated checkout's ignored `reports/` directory: `build-native.log`, `tests-native.log`, `tests-native-concurrent.log`, `build-managed.log`, `build-ui.log`, `tests-managed.log`, `tests-offline.log`, `tests-compact.txt`, and `capture-baseline-regression.log`. Offscreen images and layout checks are in `reports/reliability-previews/`. Test summaries are local evidence, not published release certification.

Follow-up results use the `-continuation` suffix (`tests-managed-continuation.log`, `tests-native-continuation.log`, `tests-offline-continuation.log`, `tests-compact-continuation.txt` and corresponding build logs), plus `tests-runtime-review.log` and `continuation-previews/`. The earlier logs are retained. The Python fixture's first attempt encountered restricted-token temporary-directory access; its private fixture tree was moved under the writable checkout's ignored reports directory and all five cases passed.

Third-pass final evidence is in `tests-managed-pass3-final.log`, `tests-preferences-pass3-final.log`, `tests-offline-pass3-final.log`, `tests-native-pass3-final.log` and corresponding managed/native build logs. The preference-only and pure checks are subsets, not additional counts. `tests-preferences-baseline.log` and `tests-provider-baseline.log` retain the expected failures before those fixes. Final read-only verification still found the original checkout clean at the stated base, installed driver SHA256 `5596EAB5D07087BFE4590397DF5CDDE18B6D93485CF68AFF7EEA312B00AB9F86`, SteamVR driver disabled, actual build `26496310` and admitted build `25330290` unchanged.

## Reproducing the safe checks

Build the projects first using the installed .NET/Windows SDKs and bundled native dependencies. The commands below run fixture branches only; do not invoke the resident UI or broker entrypoints without their fixture arguments.

```powershell
dotnet tests\Switcheroonie.Tests\bin\Release\net10.0-windows\Switcheroonie.Tests.dll --self-test-offline
dotnet tests\Switcheroonie.Tests\bin\Release\net10.0-windows\Switcheroonie.Tests.dll --self-test-preferences
dotnet tests\Switcheroonie.Tests\bin\Release\net10.0-windows\Switcheroonie.Tests.dll
dotnet research\compact-ui\bin\Release\net10.0-windows\Switcheroonie.UI.dll --self-test-compact "$PWD\reports\tests-compact.txt"
dotnet research\compact-ui\bin\Release\net10.0-windows\Switcheroonie.UI.dll --render-preview "$PWD\reports\reliability-previews"
ctest --test-dir build\reliability-native -C Release --output-on-failure -R '^(native-routing|isolated-runtime-admission|isolated-provider-.*|isolated-host-hooks|isolated-foreign-.*)$'
python -m unittest discover -s tools -p test_review_runtime.py -v
```

## Remaining priorities

1. Review this candidate before any installation. Keep the deliberate SteamVR disable. The installed runtime build remains unsupported; changing the allowlist is a separate compatibility decision requiring evidence, not a setup workaround.
2. At an owner-selected idle session boundary, validate the exact candidate on the intended headset/Virtual Desktop/calibration route, with a known rollback package. Check clean startup and exit, runtime faults, companion cleanup and return to physical tracking before normal use. A fake host cannot establish vrserver compatibility or live teardown timing.
3. Perform explicit human checks for hidden cursor, mouse latency, menu pointer/wrist rotation, fresh-click recovery, held-key release and locomotion animation after switching or spin. The earlier wrist trace contains no applicable Desktop aim samples; speculative pose correction is not justified.
4. Review application/session scoping separately. Native pose routing affects the SteamVR device stream; preserving a Desktop view through ordinary focus loss is intentional. The candidate does not add a current-scene application identity gate or certify coexistence with other VR applications.

Headset-free startup remains unsupported. No driver re-enable, runtime restart, application focus change, live device test, installed update, signing or publishing occurred in this work.
