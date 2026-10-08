# VRC-SWITCHEROONIE

A small Windows app for switching an existing SteamVR VRChat session between your headset and mouse/keyboard controls.

**Experimental.** The first test route is PICO Swan through Virtual Desktop with physical controllers. Tracking uses SteamVR device classes and roles rather than vendor names; Steam Link, PICO Connect and other routes need their own live tests. Start VRChat in VR and keep the PCVR connection alive. Headset-free VRChat startup and converting an existing native desktop session into VR are still under development.

![VR panel](docs/screenshots/main-vr.png)
![Desktop panel](docs/screenshots/main-desktop.png)

## Download and start

1. Download the Windows x64 ZIP from [Releases](https://github.com/bingbongs/VRC-SWITCHEROONIE/releases).
2. Extract the whole ZIP into a folder you can write to. Run **VRC-SWITCHEROONIE.exe**.
3. For first setup, close VRChat and SteamVR normally. Register the included experimental driver:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Manage-Driver.ps1 -Action Install -EnableExperimental
   ```

4. Connect Virtual Desktop, use **Launch SteamVR**, wake the controllers, then launch VRChat in VR.
5. Choose **Desktop** in SWITCHEROONIE and click into VRChat. Choose **VR** to return to physical tracking.

The portable package includes its .NET runtime. Closing the panel keeps it in the tray. **Start with Windows** opens it quietly at sign-in. Updates download in the background and wait for a safe stopped session before replacing the driver; they do not require an installer or restart your game.

The driver currently accepts SteamVR build **25330290**. Other builds refuse activation until reviewed. See [compatibility](docs/compatibility.md) and [recovery](docs/recovery.md).

## Controls

| Control | Action |
|---|---|
| Mouse | Look around after clicking into VRChat |
| WASD / Shift / Space | Move / run / jump |
| C / Z | Toggle crouch / prone; press again to stand |
| M | Raise the left hand and open the Quick Menu; press again to close and lower it |
| Esc | Open or close the same menu; cancel chat while typing |
| Left mouse | Select, use, or drag |
| Hold right mouse | Aim the interaction pointer |
| Hold middle mouse | Grab |
| Y / Enter | Open chat / send |
| Ctrl + Alt + F10 | Switch VR / Desktop while the panel is running |
| Ctrl + Alt + F12 | Release inputs and stop speeeeeeen |
| Alt-Tab | Release controls; click back into VRChat to resume |

**Tab keeps its normal game behavior.** Menu navigation keeps head look steady while the mouse aims the right hand. The left menu hand stays raised until the menu closes. Mouse look hides the Windows cursor; menu and pointer controls show it.

![Settings](docs/screenshots/settings.png)

## speeeeeeen

Enable **speeeeeeen** below Credits in Settings. The choice is remembered; motion starts inactive each time.

| Number pad | Action |
|---|---|
| 5 | Turn spinning on or off |
| 4 / 6 | Roll sideways |
| 8 / 2 | Flip forward / backward |

Rotation builds speed while held and slows down after release. The opposite direction brakes faster. Num Lock can be on or off. Controls work in VR and Desktop while VRChat is foreground; typing, focus loss and emergency release stop motion.

**May cause motion sickness.** Start slowly. The feature rotates the tracked head, controllers and generic trackers together around a body pivot. Avatar behavior depends on VRChat's IK and is experimental; it does not change avatar files or calibration.

Desktop suspends SteamVR generic body trackers so VRChat can use its non-FBT animation; VR restores their physical poses. Full-body fallback and restoration are still awaiting the repaired build's live check. OSC-only body trackers sent directly to VRChat need a separate sender/relay path.

![Help and credits](docs/screenshots/help.png)
![speeeeeeen](docs/screenshots/spin.png)

## Updates

[GitHub Releases](https://github.com/bingbongs/VRC-SWITCHEROONIE/releases) hosts the update files. The updater verifies a signed manifest and every packaged file, stages each version separately, and retains the previous version for recovery. It never replaces a loaded driver or closes VRChat, SteamVR, or Virtual Desktop. See [update details](docs/updates.md).

## Build

Windows x64, .NET SDK 10, CMake 3.25+, and Visual Studio Build Tools 2022 with C++ and the Windows SDK are required.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build.ps1
```

OpenVR and MinHook revisions are pinned in `third_party`. [Testing](docs/testing.md) distinguishes automated checks from headset and game testing. Screenshots show the app's own rendered UI with illustrative connection states.

## Credits

**Entirely vibe coded using 6.1 sol.** Built with OpenAI Codex and collaborating coding agents, directed and tested by **bingbongs**.

- Microsoft .NET / WPF and Windows APIs — desktop app and controls.
- Valve OpenVR — SteamVR interfaces.
- TsudaKageyu MinHook — native hook library.
- GitHub Releases — source hosting and portable update delivery.
- OpenAI image generation and user-supplied artwork — mascot development; the included mascot atlas is the user's supplied reference.
- [freefbt.com](https://freefbt.com)

MIT licensed; bundled dependencies retain their own licenses. See [third-party notices](THIRD_PARTY_NOTICES.md). This project is independent of VRChat, Valve, PICO and Virtual Desktop.
