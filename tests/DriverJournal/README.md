# Installer recovery regressions

Run with Windows PowerShell 5.1 from the repository root:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\tests\DriverJournal\Test-PowerShell51.ps1
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\tests\DriverJournal\Test-Relocation.ps1
```

Both tests parse the owned installer and compile only its named function definitions. They never execute the installer's top-level runtime discovery or registration actions. Every file operation uses a freshly created, absolute sandbox beneath this test directory; cleanup checks that boundary before deletion.

The first test verifies plain JSON-string backups under Windows PowerShell 5.1, compact journals without `Get-Content` provider metadata, valid legacy `value` wrapper recovery, rejection of invalid wrappers/missing fields, exact Unicode/escape/line-ending restoration, and case-sensitive conflict preservation.

The relocation test replaces registration with a private fake list. It verifies both exact allowed roots, refusal of unknown targets/configuration changes, preservation of unrelated registrations, latest-package resource precedence, empty manifest-directory removal, retention of the legacy tree, and rollback after registration failures including a failure that already mutated the fake registration list. This is software safety evidence; it does not prove that SteamVR discovers the proposed location.

Reports: `reports/driver-journal-regression.json` and `reports/driver-relocation-regression.json`. No live installation, SteamVR process, driver registration, ACL, application, or configuration is changed by these tests.
