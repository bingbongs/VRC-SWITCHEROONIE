# Canonical profile state

The production native loader, managed broker and installer now select **the process user's Windows `FOLDERID_Profile` followed by `VRC-SWITCHEROONIE\config`**. They obtain the known folder with an explicit current-process token. Environment variables, the working directory, and the old AppData location are not configuration fallbacks. Resolution itself creates no directories. Microsoft documents the token rights and allocation lifetime for this API. [SHGetKnownFolderPath](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath)

| File/location | Authority and preservation |
| --- | --- |
| `config\driver.json` | Native strict opt-in and approved SteamVR build configuration; installer writes this same file. |
| `config\installation-journal.json` | Installer ownership and exact `originalConfig`/`lastConfig` recovery text. Copy its bytes without normalizing or recreating its journal. |
| `config\direct-input.json` | Broker game-input enabled state, sensitivity and desktop height. Preserve the current `Height: 0.15`. |
| `config\keyboard.json` | Editable broker input bindings. |
| `config\routing.json` | Automatic/manual routing preference. |
| `reports` under `VRC-SWITCHEROONIE` | Managed diagnostic output, outside active configuration. |
| `drivers\0.1.0\switcheroonie` under `VRC-SWITCHEROONIE` | Existing Profile driver registration; the state move does not relocate or re-register this driver tree. |

Previously `Identity.DataDirectory` selected LocalAppData for the three broker preference files. `Manage-Driver.ps1` separately selected that same logical directory for the native opt-in and installer journal. The installer already used the Profile driver tree after the earlier reversible relocation. `tools/Build.ps1` copies the installer and managed binaries; it does not provide a second configuration location. The UI uses the common helper for report output and sends preference updates through the broker. Its unused historical `ui-settings.json` is not an active preference authority.

Masked file-handle evidence found that the old logical owned AppData directory resolves to a different physical `Packages/...` backing in the inspecting context. The selected Profile root and registered driver root resolved exactly to their requested physical paths. This is evidence for the new location choice, rather than proof of the mechanism that caused the earlier runtime open failures. See `reports/profile-state-canonical-folder-proof.json` and `reports/native-profile-state-candidate-validation.json`.

## Explicit migration and recovery

`ConfigurationMigration` is a separate explicit offline helper; no application startup calls it or searches legacy state. It snapshots exactly the five active files above as bytes, records sizes and SHA-256 hashes, refuses conflicting destination files and source edits after planning, and copies a new complete backup before committing files. Existing identical destination files are retained. Source files and unrelated legacy files remain untouched. It rejects overlapping or reparse-point directory chains and refuses to overwrite an existing backup. Destination files are staged in that destination directory and committed without replacement. If a later copy fails, rollback deletes only newly committed files whose bytes still match the snapshot, while retaining backup evidence and concurrent foreign edits.

The migration operator owns the stopped broker/UI write window and deployment sequencing. Before deploying either new reader, retain the old source files, a byte-for-byte original installer journal, source hashes, and the currently registered driver-root list. Explicitly select the observed old source; do not recreate a fresh install journal. Confirm the destination `driver.json` still equals journal `lastConfig`, then reload the broker's `direct-input.json` and verify `+0.15` height, sensitivity, keyboard and routing preferences. Preserve all original sources and the separate unused UI settings copy. A runtime startup/configuration test remains separate from the private migration fixtures.

Historical journal-owned driver roots remain an exact two-root allowlist: the old LocalAppData driver tree and the current Profile driver tree. The legacy resolver is used only for explicit migration/historical ownership, never native opt-in, broker preferences or active journal selection. A missing canonical journal therefore cannot authorize removing an unknown registration.

## Validation boundary

On 2026-10-07, 24 managed path/migration checks passed in private directories, including exact UTF-8 BOM/CRLF bytes, original/last journal strings, conflict refusal, stale-source refusal, idempotent retention, and a broker reload of the migrated `+0.15` preference. The complete managed suite passed 258 checks. Installer path fixtures passed nine checks in Windows PowerShell 5.1 and PowerShell 7; the existing PS5.1 journal and relocation fixtures passed 20 and 18 checks. Known-folder API checks were read-only; there were no live configuration, registration, runtime or application changes by these fixtures. These results do not claim a successful live migration or headset test.
