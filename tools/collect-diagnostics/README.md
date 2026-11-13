# Read-only environment inventory

Run from the repository with PowerShell 7:

```powershell
pwsh -NoLogo -NoProfile -File .\tools\collect-diagnostics\collect-environment.ps1
```

Optional destination:

```powershell
pwsh -NoLogo -NoProfile -File .\tools\collect-diagnostics\collect-environment.ps1 -OutputDirectory .\reports\before-install
```

The collector writes `environment-report.json` and `environment-report.md`. It enumerates the Windows build, DXGI adapter LUIDs, display layout/effective DPI, installed VR software, relevant processes, local OpenVR/OpenXR registration, driver manifests, stored tracker roles, binding references, face-tracking descriptors, audio endpoints, and UDP port ownership. It extracts only whitelisted XR/version/microphone facts from the latest VRChat startup log. Protected process access and absent configuration are reported as limitations.

It does not call OpenVR initialization, start or stop software, register drivers, edit runtime settings, capture user input, enumerate network addresses, switch audio devices, or change DPI awareness. The only writes are its two output reports and the normal temporary compilation artifacts used by `Add-Type` for the read-only native helper.

Raw command lines, account identifiers, conversations, world/instance/avatar identifiers, raw serials, and unredacted application logs are excluded. User profile prefixes become `%USERPROFILE%`; stored device identities and local configuration names become SHA-256 hashes. These hashes allow preservation comparisons, not proof of live calibration.

`steamVrRunning`, XR module visibility, and the startup log facts have different evidentiary strength. A transport process or registry default alone never establishes the target application's active runtime. `LastKnown` HMD data is explicitly historical. Reports should be refreshed before installation and after the controlled hardware test.

Known limitations: some SteamVR controller bindings are URLs rather than local files; only their fingerprints are collected. Calibration locations vary between versions and zero discovered files does not imply an uncalibrated system. Effective monitor DPI depends on the collecting process's awareness; per-monitor registry values are included when available. GPU adapters can include multiple logical/virtual entries for one physical GPU.
