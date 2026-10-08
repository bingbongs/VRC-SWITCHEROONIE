# Portable updates

VRC-SWITCHEROONIE.exe opens the small resident panel without a console or elevation. The panel checks bingbongs/VRC-SWITCHEROONIE public GitHub releases in the background at startup, every six hours, and when Check is clicked.

Downloads do not stop VRChat, SteamVR, or the running harness. Releases stage under the process user's profile in VRC-SWITCHEROONIE/updates/versions. The next ordinary launcher start selects a release only when VRChat, SteamVR, the controlled scene, and the existing harness UI/broker are closed. Otherwise activation stays pending and the current session continues.

The registered companion driver must already have its canonical ownership journal and unchanged opt-in configuration. Updating replaces only signed companion files after stopped-runtime checks, with exact-file recovery backups. It does not register a missing driver, alter settings or other drivers, or install the research HMD.

A persistent transition is written before driver replacement. The updater commits current.json only after the signed driver and portable version are matched. Interrupted transitions remain visible and prevent mismatched UI/broker launches. If a runtime appears, recovery writes also wait for shutdown. Rollback selects the previous signed version and driver, retaining the highest observed release sequence. The first update independently retains both the original installed release and the signed v0.2.0 delivery floor. Starting from a later first-install package therefore rolls back to that original release. Legacy unversioned bootstrap records identify v0.2.0; unknown or changed origins fail closed.

## Verification

Every release provides a versioned win-x64 ZIP, release-manifest.json, and release-signature.bin. An embedded P-256 public key verifies the exact manifest bytes using SHA-256 and a 64-byte IEEE P1363 signature. The manifest pins the repository, three-part version, increasing sequence, archive hash/size, and every payload file's hash/size. Download hosts are restricted to GitHub release hosts.

Bounds are 512 KiB for the manifest, 512 MiB ZIP, 1.5 GiB unpacked, and 4,096 files. Extraction refuses traversal, absolute paths, ADS, reserved names, duplicate/case-alias entries, reparse points, symlinks, unexpected/missing files, and excessive compression. Versions are immutable. A cross-process file lock excludes competing update writers. A signed orphan version left by a crash is reverified before its pending selection is recovered.

This protects download provenance and integrity. It does not protect against another program already running as the same Windows user and modifying the application, trust key, or state. Repeated process checks bound activation races; SteamVR exposes no public atomic lock against another user launching it at the exact commit instant. An observed race defers selection and retains its transition/backups.

## Release preparation

The private signing key stays outside the repository in VRC-SWITCHEROONIE/updates/keys/release-signing.dpapi, protected by Windows current-user DPAPI. It is never a GitHub secret, workflow input, source file, or release asset. The public key is tracked in release/release-public-key.txt; an uninitialized key disables verification.

Initialize once with the updater's --create-signing-key command, then rebuild so the package embeds the public key. Existing launchers pin that key; key rotation requires an explicit migration.

Build with tools/Build.ps1. Existing portable package directories are preserved; use another staging name for a rebuild. Fetch-Dependencies.ps1 verifies the official pinned OpenVR DLL/import library. CI builds/tests an unsigned portable artifact and holds no release private key.

Prepare-Release.ps1 accepts -Package, a new -Output, -Version, and increasing -Sequence. It creates local signed assets and publishes nothing. After a reviewed source commit and matching tag are pushed, Publish-Release.ps1 accepts -Artifacts, the reviewed -Package, and -NotesFile. It verifies signature, archive, and full inventory before creating a draft in the pinned GitHub repository. Publishing the reviewed draft is a separate release action.

The updater's --status, --check, --activate, and --rollback commands return status/exit codes with redirected output. The WinExe launcher shows an owned error dialog if its selected version cannot open. It never force-closes a game or runtime.

## Integration and tests

The compact UI owns one UpdateService. CheckAndStageAsync runs outside rendering, its status is cached, and resident exit cancels/disposes it. ResolveStableLauncher returns the original launcher only after the selected payload and original bootstrap package match signed inventories. Uncertain managed startup is disabled; no registry value is silently replaced.

Tests use in-memory signing keys, fake HTTP, temporary stores, and private driver/config/registration fixtures. They also test real DPAPI and a captured owned helper for lock exclusion. They do not run VRChat, load VR runtimes, modify live drivers or capture the cursor. After publication, the exact final updater downloaded signed release 0.2.0 from the real GitHub feed and verified all 456 files in a private store with a simulated older selection. The exact 0.2.1 updater also verified/staged the real signed 0.2.1 production update, retained signed 0.2.0 and the stable launcher, and deferred activation while the runtime and harness were active. Windows sign-in and actual subsequent-version activation/rollback remain separate acceptance tests.

## Primary references

- [GitHub latest release API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release) and [asset API](https://docs.github.com/en/rest/releases/assets)
- [.NET ECDSA verification](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdsa.verifydata?view=net-10.0) and [SPKI import](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.asymmetricalgorithm.importsubjectpublickeyinfo?view=net-10.0)
- [Windows CryptProtectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)
- [Pinned official OpenVR source](https://github.com/ValveSoftware/openvr/tree/0924064316de3effbcd1acf1e309182a2deb1c05)
