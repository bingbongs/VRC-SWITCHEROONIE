# Recovery

Ctrl+Alt+F12 or the panel release button neutralizes owned controls. Escape in the panel also releases them; in the game it toggles menu navigation or cancels observed chat entry. From the package, `Switcheroonie.Cli.exe release` works independently of the panel. `physical` requests fresh physical tracking with driver acknowledgement. Commands have bounded timeouts.

Panel failure: the broker stays alive. The optional pad's200ms lease expires; primary game-window controls continue under their own foreground checks. Desktop pose remains selected while the broker's driver lease remains fresh. Reopen the panel or use CLI.

Broker failure: the native deadline selects physical passthrough; its own input-release gate waits for real physical button release before forwarding held controls. The separate OSC helper emits neutral movement/jump/run packets when the broker lease expires. An independent cursor helper restores our recorded previous clip boundary only if the current clip still matches the owned rectangle.

OSC helper failure: the broker disarms and sends emergency neutrals; helper liveness is reported. Helper startup sends baseline zero packets to clear a predecessor's latched actions. If every sender is gone, restart the broker/helper or explicitly send neutral OSC values. Do not claim hardware release from a panel message alone.

To disable for the next startup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Manage-Driver.ps1 -Action Disable
```

This does not unload interception from a live runtime or stop it. Complete your session normally. When SteamVR is stopped, remove only the owned installation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Manage-Driver.ps1 -Action Uninstall
```

The removal target is checked against the exact owned version directory. Registration is removed only if the harness added it. `driver.json` is restored only when it still matches the last journaled write. Conflicts preserve the changed configuration and recovery journal for review. Existing vendor/Valve drivers, tracker calibration, bindings and user preferences remain untouched. A completed uninstall journal is archived on a later reinstall.

Fresh installations use `%USERPROFILE%\VRC-SWITCHEROONIE\drivers\0.1.0\switcheroonie`. Configuration and the recovery journal use `%USERPROFILE%\VRC-SWITCHEROONIE\config`, resolved from the process user's Windows Profile known folder. Diagnostics use the sibling `reports` folder. AppData is not a production configuration fallback. Preserve the original journal and configuration bytes before an explicit state migration; the migration helper is not invoked during ordinary startup. See `profile-state-authority.md`. Older journals retain their recorded `%LOCALAPPDATA%\VRC-SWITCHEROONIE\drivers\0.1.0\switcheroonie` installation until explicitly relocated or uninstalled. `Status` reports the journal-selected root.

To migrate an older owned installation, finish the VR session and stop SteamVR normally, then run this command from the latest built package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Manage-Driver.ps1 -Action Relocate
```

Relocation copies the owned tree, overlays the current packaged driver resources, changes only its registration, and retains the legacy tree as a journaled rollback copy. It preserves `driver.json` exactly. An existing target directory, unknown registration, changed configuration, or reparse point is refused. A failed registration transition attempts to restore the previous registration and journal; an incomplete rollback retains the recorded trees for inspection. A later uninstall checks and removes both exact journaled owned roots. No runtime is launched or restarted.

Live discovery loaded the actual driver DLL and produced a heartbeat at the new location. The location, omission of the manifest's empty `directory` field, and driver priority changed in the same experiment, so the cause of the earlier discovery failure is not isolated. No ACL permissions were widened. Discovery evidence alone does not establish switching behavior or compatibility with every installed driver.

When SteamVR updates, the driver refuses a mismatching build. Disable the owned backend and use normal physical VR until the new build has been evaluated. Never overwrite a Valve/vendor DLL or bypass VRChat's normal launch protection to recover this utility.
