# Current limits

Version 0.2.3 is an experimental Windows/SteamVR portable candidate. Software checks and rendered previews are separate from in-headset and VRChat behavior. Version 0.2.0 had a Settings-opening crash; use 0.2.1 or newer.

- **Controls still need repair/live verification:** 0.2.2 cursor hiding, mouse-look responsiveness, menu-pointer aiming and resting hand rotation failed the human check. C/Z posture and FBT fallback/return passed with freeze disabled. VRChat locomotion animation can remain latched after switching or spinning even though its persisted FBT locomotion preference remains off.
- **speeeeeeen is experimental:** Desktop rotates the view and tracked rig; the 0.2.3 VR change preserves natural headset movement while rotating hands/body. That view correction needs its own live check. Avatar root behavior, VRChat IK, calibration and prediction remain experimental. It may cause motion sickness.
- **Headset-free VRChat startup is not implemented.** The resident app can run without a headset; the VR session still needs the connected vendor display and fresh independent tracking. A native desktop VRChat session cannot be converted into VR by this backend.
- **Compatibility needs route testing:** SteamVR build25330290 is admitted. Head/controller roles and the `GenericTracker` device class are handled without vendor-name filters. PICO Swan / Virtual Desktop is the first test route; Steam Link, PICO Connect, other headsets and runtime builds need their own tests. Ambiguous roles or unsupported hooks refuse routing.
- **Full-body fallback depends on VRChat's freeze setting:** 0.2.2 suspends all ten VD generic trackers, independently verified. The user confirmed Desktop body fallback and physical return after turning Freeze Tracking on Disconnect off. Other tracker routes/calibration are unverified. OSC-only body tracking bypasses SteamVR and is not covered by this driver.
- **Auto depends on vendor wear events:** unknown wear state retains the current route. Headset sleep, stream loss and transport changes are not certified.
- **Tracker/audio coexistence needs testing:** existing roles, calibration and audio settings are retained. Face/eye tracking, continuous calibration, haptics, stations and avatar changes have no complete device-level validation.
- **OSC is optional:** individual producer/helper failures are covered; losing every sender can leave the receiver's previous state latched.
- **Updates wait for a safe session boundary:** they download automatically, but loaded drivers are not replaced and games are never forced closed. Rollback selects a previously verified version.

An unperformed hardware test is recorded as not run. A UI screenshot, pose heartbeat or accepted eye submission alone does not establish visible headset/game behavior.
