# Testing

Version 0.2.0 is experimental. Software tests and screenshots do not establish visible headset or VRChat behavior.

| Check | Current result |
|---|---|
| Managed controls, routing, cursor ownership and spin | 395 checks passed, including the packaged Common/Broker binaries |
| Native routing, startup, isolated hooks, driver resources and scene self-test | All 9 suites passed |
| Signed updates, hostile archives and interrupted recovery | 106 fixture checks passed |
| Compact panel, startup ownership and resident lifetime | 81 fixture checks passed |
| Offscreen layout at 100/150/200% DPI and minimum size | 21 fixtures passed |
| Installer journal, relocation and canonical state | 20 + 18 + 9 fixture checks passed |
| Public broker/process integration | P01–P06 passed on fresh Windows CI; local packaged-process run waits for normal shutdown |
| New cursor presentation, posture toggles, M/Esc menus and wrists in VRChat | Live retest pending |
| Whole-avatar spinning, calibration and prediction | Live test pending |
| Published update download and staging | Real GitHub download, signature and all 456 files verified in a private store |
| Windows sign-in and live update activation/rollback | Separate acceptance tests pending |
| Headset-free VRChat startup | Unsupported |

The managed tests use private mappings and temporary configuration. They cover stale or missing source rejection, committed epochs, input ownership, shared menu state, posture continuity, short key presses, chat, focus loss, emergency release, cursor handoff and bounded spin. Native hook tests use an isolated fake host and input implementation. Update tests use temporary stores, fixture signing keys, fake HTTP and private driver/registration files. None of these tests launch VRChat or change the live headset session.

The process harness starts only processes it owns. Public-pipe tests defer if a user broker or SteamVR runtime is active. A packaged-process run also checks that the broker loads its bundled .NET runtime.

The public download check used the exact final updater with its embedded production key and a private simulated older selection. It staged signed release 0.2.0, sequence 1, and refused activation through its fixture safety gate. It changed no live driver, application or user configuration. This proves delivery and staging; it does not claim a live update or prior-version rollback.

Screenshots are rendered offscreen with illustrative connection states. Their fixture branches do not connect to the broker, register hotkeys, capture input, write startup registration or download updates.

The small graphics share a 24 Hz target and cap. The detached animation workload measured about 19.5 updates per second under the Windows scheduler; hidden graphics produced no updates and stopped the shared clock. This measures property updates, not visible GPU rendering or concurrent VRChat performance.

## Hardware history

PICO Swan through Virtual Desktop passed controlled-scene stereo presentation, repeated switching, held-view stability, mouse look and physical return in earlier builds. Actual VRChat tests then exposed menu closure, cursor and wrist problems. Those failures motivated the new controls and remain failures until the replacement passes its own live checks. Version 0.2.0's software results do not overwrite them.

Persistent-headset experiments failed actual Virtual Desktop delivery and were removed. No headset-free VRChat continuity has passed. Other headsets, transports, runtime builds, stream recovery, full-body calibration, face/eye tracking and audio need their own tests.

Use [the hardware checklist](hardware-testing.md) for the connected test. Test spinning first with small desktop taps and confirm the avatar in a mirror or with an observer. OpenVR pose transforms cannot guarantee how VRChat's IK moves the avatar root.

## Repeat the software checks

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build.ps1
```

The build preserves existing package directories. For another candidate, pass a new `-PackageName`. Raw machine reports and private recovery journals are excluded from public source and releases.
