# Cold-start display ownership: evidence and next experiments

Status: **The isolated persistent-HMD/Virtual Desktop shim trials failed real headset delivery and were rolled back. Scenario C remains unsolved.** Updated 2026-10-07. Always-ready service and automatic preferences are implemented; they do not establish headset-free VR rendering. The next research candidate is a normal-graphics OpenXR probe before an owned display bridge. See `../headset-free-startup-design.md` and `../openxr-display-bridge-design.md`.

## Current machine evidence

Initial discovery by `tools/collect-diagnostics/collect-environment.ps1` found SteamVR build ID 25330290 installed with no `vrserver` process. The then-running VRChat startup log reported `XR Device: None` and SteamVR initialization error 109. That native desktop session could not validate connected-HMD routing or headset-free VR startup; it ended during authorized setup. Later controlled tests use an active Virtual Desktop SteamVR display with a connected headset and therefore provide no absent-headset launch evidence. Protected VRChat module enumeration supplied no additional evidence.

Installed routes include PICO Connect 10.6.6, PICO Connect TMP 20.5.5, PICO Streaming Service 2.5.4.1, Virtual Desktop Streamer 1.34.22, and SteamVR's `vrlink` provider. An ALVR nightly driver is already registered. These observations establish availability, not headset compatibility or a successful render path. Historical SteamVR `LastKnown` data identifies an Oculus Quest2 through `oculus_virtualdesktop`; it is not the current HMD. The detected PICO face-tracking module also does not prove the current headset model or firmware.

## Documented boundaries

Valve documents distinct tracking, display, direct-mode, and virtual-display components. `IVRVirtualDisplay::Present` supplies a final composite shared texture; its implementer owns delivery and timing. `TrackedDeviceAdded` has a condition against registering a second HMD. The lifecycle documentation mentions deactivation when switching HMDs, but does not guarantee application/session survival across all providers. **Inference:** pose routing that preserves the current vendor display owner is the best initial A/B experiment; it does not itself implement C. [Valve driver API](https://github.com/ValveSoftware/openvr/blob/master/docs/Driver_API_Documentation.md)

Manifest `alwaysActivate` concerns driver activation; `hmd_presence` patterns influence HMD presence detection. Neither creates a stereo transport. [Valve DriverManifest](https://github.com/ValveSoftware/openvr/wiki/DriverManifest)

The historical TrackingOverrides implementation documents replaced raw poses, lost access to the original overridden pose, static handoff behavior, and missing automatic universe reconciliation. This must be measured against the installed build; it rules out treating a post-override pose query as an independently acquired physical source. [Valve TrackingOverrides](https://github.com/ValveSoftware/openvr/wiki/TrackingOverrides)

VRChat documents `--no-vr` as forcing desktop startup. That option is excluded from the cold-start experiment because the harness needs a continuing VR render session. [VRChat launch options](https://docs.vrchat.com/docs/launch-options)

## Candidate experiments

The table records remaining candidates alongside measured failures. Research registration was changed only in the explicit controlled window and then rolled back. New experiments belong in a controlled application first and remain outside normal installation.

| Candidate | Concrete implementation/experiment | What would count as success | Current limitation |
| --- | --- | --- | --- |
| Persistent vendor provider / installed VD shim | Keep one logical HMD while the installed vendor provider owns delivery; test absent startup and later connection before integrating controls. | Same process delivers changing stereo to both eyes, with real tracking/controllers, then repeat in VRChat. | Tested owned-HMD/VD absent late attachment and connected startup both failed actual delivery. Registrations were restored. Other vendors are untested. |
| Own persistent display provider plus explicit transport adapter | Qualify the isolated pinned VDXR normal-graphics probe first. Then implement one logical OpenVR HMD plus an owned `IVRVirtualDisplay` sink and bounded texture/timing bridge into a public OpenXR graphics session. | Persistent scene session, correct adapter/eye geometry, frame pacing, both-eye content and independent physical pose/input after attachment. | Probe is offline-validated; no live graphics/source pass or frame bridge exists. Headless OpenXR and invisible LibOVR did not qualify as independent sources. |
| Controlled ALVR backend | Use the already installed ALVR route as an optional research environment: audit/pin its driver and headset client, keep their provider/device lifetime stable before connection, and route synthetic/physical poses inside that owned source. Modify a separately named research build only if needed. | F4 passes with matching ALVR server/client builds and the actual headset; driver/FBT identities remain preserved across attachment and loss. | ALVR is an open-source PC/headset streaming implementation, so source can be inspected and changed. Its existence is not evidence that unmodified ALVR supports this cold-start sequence; it also does not certify PICO Connect/VD/Steam Link. [ALVR source](https://github.com/alvr-org/ALVR) |
| Runtime-managed HMD handoff | Run an isolated dummy-to-real experiment only after instrumenting active-HMD lifecycle and scene process continuity. Record admission failure/activation/deactivation and actual eyes. | Correct live stereo reaches the new physical HMD with the original app/session and bindings intact. | There is no validated public operation in this build that guarantees this transition. Adding a second HMD is not the experiment's completion; a rejected registration or black headset is a reproduced failed candidate, not a claim of universal impossibility. |

Valve's example virtual-display driver is a concrete starting point for the second candidate's compositor-facing component. It demonstrates that interface rather than a commercial headset bridge. [Valve virtual_display example](https://github.com/ValveSoftware/virtual_display)

## Exact F4 evidence to capture

1. Record test app PID/start time, logical HMD/provider, compositor session, driver revision, GPU LUID, transport build, headset model/firmware, and the physical connection's absence at startup.
2. Produce changing stereo frames and operate synthetic controls while absent. Do not substitute a screenshot or `VR_IsHmdPresent` result for a display test.
3. Connect the selected route; record both eye images, projections/eye assignment, live frame counter, physical pose timestamps, and controller actions while keeping the original app PID.
4. Return to desktop controls, reconnect after loss separately, and compare stored tracker/calibration/binding fingerprints. Record any runtime restart, app relaunch, or instance rejoin as a failed continuity test.
5. Repeat the passing experiment in VRChat and per requested route. Preserve A/B as independently tested functionality if a C candidate fails.

UI must distinguish a ready resident service and saved preferences from display attachment. The installed VD shim candidate was actually tested and failed; synthetic presence and accepted scene submissions did not override human failure. The next actionable candidate is the bounded normal-graphics probe, followed only on success by an owned transport bridge. These results do not establish a generalized impossibility; they rule out claiming the measured candidates as working headset-free support.
