# Current limits

Version 0.2.1 is an experimental Windows/SteamVR app. Software checks and rendered previews are separate from in-headset and VRChat behavior. Version 0.2.0 had a Settings-opening crash; use 0.2.1 or newer.

- **New controls need a live check:** hidden look cursor, toggle crouch/prone, M/Esc menu closure, left-hand menu angle and natural resting wrists.
- **speeeeeeen is experimental:** the transform includes the headset, controllers and generic trackers. Avatar root behavior, VRChat IK, full-body calibration and changing-transform prediction require hardware testing. It may cause motion sickness.
- **Headset-free VRChat startup is not implemented.** The resident app can run without a headset; the VR session still needs the connected vendor display and fresh independent tracking. A native desktop VRChat session cannot be converted into VR by this backend.
- **Compatibility is narrow:** SteamVR build25330290, with PICO Swan / Virtual Desktop as the first test route. Other headsets, transports and runtime builds need their own tests. Ambiguous roles or unsupported hooks refuse routing.
- **Auto depends on vendor wear events:** unknown wear state retains the current route. Headset sleep, stream loss and transport changes are not certified.
- **Tracker/audio coexistence needs testing:** existing roles, calibration and audio settings are retained. Face/eye tracking, continuous calibration, haptics, stations and avatar changes have no complete device-level validation.
- **OSC is optional:** individual producer/helper failures are covered; losing every sender can leave the receiver's previous state latched.
- **Updates wait for a safe session boundary:** they download automatically, but loaded drivers are not replaced and games are never forced closed. Rollback selects a previously verified version.

An unperformed hardware test is recorded as not run. A UI screenshot, pose heartbeat or accepted eye submission alone does not establish visible headset/game behavior.
