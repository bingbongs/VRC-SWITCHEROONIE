# Standalone bundle launcher

This standalone CMake project produces a static-CRT Windows x64 GUI executable. Before starting adjacent `App/VRC-SWITCHEROONIE.exe`, it verifies every file against a compiled immutable payload inventory and the separate required inner-EXE SHA256. It then uses absolute `CreateProcessW` with `--launch`. It accepts only no arguments, exact `--background`, or exact `--verify-only`. Verification mode is silent, returns0/1 and never launches the inner application. Normal errors use one terse MessageBox.

```powershell
$innerHash = (Get-FileHash -LiteralPath '<published-flat-payload>/VRC-SWITCHEROONIE.exe' -Algorithm SHA256).Hash
cmake -S native/launcher -B build/standalone-launcher-release -G 'Visual Studio 17 2022' -A x64 "-DSWITCHEROONIE_INNER_SHA256=$innerHash" "-DSWITCHEROONIE_PAYLOAD_INVENTORY=<inventory-file>"
cmake --build build/standalone-launcher-release --config Release
```

The UTF8 inventory contains `SHA256|canonicalRelativePath` lines for all published native/managed/docs/tools files, created before the wrapper and `package-hashes.json`. Paths use forward slashes; ambiguous, duplicate, reserved or escaping names fail configuration. There must be1–4096 files including the exact inner launcher. Copy the built wrapper to both the ZIP root and the flat payload's `portable/VRC-SWITCHEROONIE.exe`. That mirror is required and hash-compared to the running root wrapper without introducing a circular compiled hash. The only unpinned file permitted is exactly `package-hashes.json`: required ordinary nonexecuted metadata at most2MiB. No other additional file is admitted. The exact existing flat payload belongs inside `App/`, and its original inner launcher/bootstrap/startup schema remain authoritative.

The launcher rejects reparse points on itself, its immediate directory and every traversed App object. Ordinary directory/file handles deny write/delete sharing through verification and the exact inner bootstrap process's lifetime, preventing direct replacement of the held objects or in-place dependency writes before that bootstrap exits. The wrapper waits quietly for its captured child; it never terminates it. The ordinary inner --launch path starts the UI and then exits. Enumeration is bounded to32 levels,8192 directories and the expected files plus2; each hashed file is at most512MiB and total enumerated bytes at most2GiB. It does not alter permissions, elevate, use a shell, stop runtimes, install anything or repair a damaged bundle. The inventory closes the apphost-only gap where changed managed/runtime DLLs could execute before managed verification. It is not a protection against a hostile same-user process changing ancestry or files after launch; normal updates remain the signed updater's responsibility.

No suitable existing native icon was present, so this wrapper has no custom icon.

```powershell
powershell -NoProfile -File tests/StandaloneLauncher/Test-Launcher.ps1 -LaunchFixture
```

Fixtures build exclusively inside ignored workspace `build/` directories. Correct, missing/tampered dependencies, extra DLL, mirror mismatch, metadata bounds, competing writer, empty/wrong-type payload, junction and Unicode root/relative-path cases use silent verification. If Windows permits an unprivileged file symlink, that rejection is also checked; otherwise it is explicitly skipped. Optional `-LaunchFixture` starts only a compiled harmless GUI payload that records its arguments into its own temporary `App/` folder and exits. No real UI, broker, runtime or user program is launched.
