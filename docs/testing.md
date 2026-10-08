# Testing

Version 0.2.5 adds absolute mouse-motion decoding and explicit recovery from a stale local menu state. Version 0.2.4 added a cooperative service lifetime: explicit tray Quit retires the service that panel created, allowing staged updates after normal shutdown. Version 0.2.3 added bundled dependencies, in-app first setup, bounded window validation, an owned invisible cursor shape and VR spin that keeps natural headset movement. Software tests and screenshots do not establish visible headset or VRChat behavior. The live 0.2.2 findings below remain the hardware authority until a replacement is checked.

| Check | Current result |
|---|---|
| Managed controls, routing, cursor ownership and spin | 0.2.5 isolated source suite: 631 checks passed, including 57 absolute/relative packet checks, 34 menu-controller recovery checks and 23 full broker recovery checks |
| Native routing, startup, isolated hooks, driver resources and scene self-test | 0.2.3 isolated source build: all 9 suites passed, including 1,772 counted assertions |
| Signed updates, hostile archives and interrupted recovery | 0.2.3 source: 194 fixture checks passed, including same-origin repair, complete driver resources, bounded launch preparation and cloud-directory tag checks |
| Compact panel, startup ownership and resident lifetime | 0.2.4: 153 fixture checks passed, including exact sibling broker launch, injected one-click setup and retry behavior; 8 harmless real process checks passed for retained parent identity and cooperative exit |
| Offscreen layout at 100/150/200% DPI and minimum size | 30 fixtures passed |
| Installer journal, relocation, canonical state and runtime guards | 115 fixture checks passed under Windows PowerShell 5.1; no live installation |
| Standalone launcher and archive | 32 launcher fixtures passed; file-symlink creation skipped by Windows error1314. Complete payload pins, dependency/runtime tampering and harmless child launch covered; archive suite: 10 checks passed |
| Packaged broker/helper processes | Exact signed 0.2.2: 12 passed, 0 failed, 0 not run; one documented OSC limitation. 0.2.3 source: 5 private OSC checks passed; public-pipe cases deferred because the user's game/runtime/broker are active |
| Cursor, menus and wrists in VRChat | 0.2.2 cursor hiding, lag and hand aiming/rotation failed. New cursor/cache code needs a live check |
| Spinning, calibration and prediction | User confirms spin works; requested VR-view preservation is new in 0.2.3 and needs a live check. Locomotion animation can remain latched in VRChat |
| Published update download and activation | Public signed 0.2.4 flat and standalone downloads verified, including staging by the exact old 0.2.2 updater in a private store. Production selection and installed driver remain 0.2.2; 0.2.3 is staged while the session runs. Configuration and earlier signed history preserved |
| SteamVR full-body suspension and restoration | All 10 VD generic trackers independently observed disconnected/invalid in Desktop. Human body movement/posture and VR return passed after disabling VRChat freeze |
| Windows sign-in and live update activation/rollback | Separate acceptance tests pending |
| Headset-free VRChat startup | Unsupported |

The managed tests use private mappings and temporary configuration. They cover stale or missing source rejection, committed epochs, input ownership, shared menu state, posture continuity, short key presses, chat, focus loss, emergency release, cursor handoff and bounded spin. Native hook tests use an isolated fake host and input implementation. Update tests use temporary stores, fixture signing keys, fake HTTP and private driver/registration files. None of these tests launch VRChat or change the live headset session.

The 0.2.4 lifetime checks validate the launching panel's creator PID, creation time, SID, Windows session and exact sibling image, then retain its process handle. Hidden panels stay resident; parent exit cancels the service, drains accepted requests and disposes input helpers even when a producer fails. The eight real process checks use harmless private executable siblings, without a production broker or VR input. Actual tray Quit, installed update activation and the new driver's visible behavior remain separate live acceptance checks.

The absolute-motion fixtures reproduce the former dropped-motion path using actual RAWMOUSE packet layouts, then check head-look and menu-pointer output, per-device baselines, desktop geometry, scope loss and feedback from the app's own cursor centering. Packet counters contain counts only. They do not establish which packet type the user's mouse produces. Menu recovery fixtures reproduce the old stuck navigation state and verify that explicit Release inputs and the emergency key publish neutral controls and resting hands without a menu-toggle pulse. Ordinary focus loss retains the current local menu state; chat safety and fresh-click/held-key gates still apply.

The process harness starts only processes it owns. Public-pipe tests defer if a user broker or SteamVR runtime is active. After normal shutdown, the exact final local package passed real pipe validation, saturation/recovery, emergency release, separate OSC helper and broker-backup neutralization checks. It loaded its bundled .NET runtime with external runtime roots disabled. OSC used loopback mock receivers and private simulated driver/lifecycle mappings; this does not establish VRChat receipt, physical tracking, cursor behavior or headset presentation.

The total-OSC-sender-loss check recorded a limitation: killing every sender leaves no process able to send neutral values, so the receiver can retain its last input. The 12 passing checks do not remove that limitation.

The matched final driver and resident panel were installed in a stopped-runtime window while five existing configuration files were preserved byte-for-byte. The exact owned sign-in command was migrated. This is deployment evidence; Windows sign-in, visible controls and a future-version update activation/rollback still need their own acceptance tests.

After the user launched SteamVR through Virtual Desktop, the loaded driver matched the signed final package. Read-only checks showed Physical mode, current headset and both controller captures, acknowledged routing and all expected capabilities. VRChat was already running, so no controlled scene or automated switching was started alongside it. The visible control test remains pending.

Opening Settings in 0.2.0 crashed the panel before the control test. The replacement checks reproduce that original exception using the actual Settings controls before template attachment, then verify the repaired attach, scroll, hide, minimize and reattach paths. The patched 0.2.1 panel was reopened against the continuing broker and native driver; opening Settings and visible controls still need human confirmation.

The public download check used the exact final updater with its embedded production key and a private simulated older selection. It staged signed release 0.2.0, sequence 1, and refused activation through its fixture safety gate. It changed no live driver, application or user configuration. This proves delivery and staging; it does not claim a live update or prior-version rollback.

The exact 0.2.1 updater also downloaded and verified the real signed 0.2.1 release into the production update store. It independently retained the original signed 0.2.0 delivery, preserved the original stable launcher, left the installed driver unchanged and deferred activation while the game/runtime/harness were running. No process was stopped for this check. Live version activation and rollback remain separate acceptance tests.

Screenshots are rendered offscreen with illustrative connection states. Their fixture branches do not connect to the broker, register hotkeys, capture input, write startup registration or download updates.

The small graphics share a 24 Hz target and cap. The detached animation workload measured about 19.5 updates per second under the Windows scheduler; hidden graphics produced no updates and stopped the shared clock. This measures property updates, not visible GPU rendering or concurrent VRChat performance.

## Hardware history

On 2026-10-08, an independent background OpenVR client observed all ten VD generic trackers disconnected and invalid while Desktop kept the headset and handed controllers valid. VRChat's Freeze Tracking on Disconnect preference was enabled. The user disabled it in VRChat and confirmed both Desktop body behavior and return to physical full-body tracking. This is a PICO Swan / Virtual Desktop route result. It does not certify other providers or calibration changes.

The held Desktop capture recorded 535 active/captured samples with no positive hidden-cursor observation. The user reported visible cursor and mouse-look lag, then both menu pointer and resting hand rotation failures. The persisted FBT locomotion preference was still off despite reported animation latching after switching/spin. Those findings remain open; the app does not rewrite VRChat preferences or calibration.

PICO Swan through Virtual Desktop passed controlled-scene stereo presentation, repeated switching, held-view stability, mouse look and physical return in earlier builds. Actual VRChat tests then exposed menu closure, cursor and wrist problems. Those failures motivated the new controls and remain failures until the replacement passes its own live checks. Version 0.2.0's software results do not overwrite them.

Persistent-headset experiments failed actual Virtual Desktop delivery and were removed. No headset-free VRChat continuity has passed. Other headsets, transports, runtime builds, stream recovery, full-body calibration, face/eye tracking and audio need their own tests.

Use [the hardware checklist](hardware-testing.md) for the connected test. Test spinning first with small desktop taps and confirm the avatar in a mirror or with an observer. OpenVR pose transforms cannot guarantee how VRChat's IK moves the avatar root.

## Repeat the software checks

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build.ps1
```

The build preserves existing package directories. For another candidate, pass a new `-PackageName`. Raw machine reports and private recovery journals are excluded from public source and releases.
