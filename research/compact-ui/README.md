# Compact panel

The 540 × 400 DIP panel uses square charcoal boxes, gold VR/Desktop artwork and a centered small mascot. It scales down to 470 × 360 and keeps one Settings window for controls, startup, help, diagnostics and credits. The selected VR/Desktop button reflects current driver routing; a stored preference does not claim a completed switch. Closing hides the panel to its resident tray.

Click VRChat to activate mouse look and WASD. Shift runs and Space jumps. Esc opens or closes the menu; Tab remains unchanged. C / Z toggle crouch / prone. M raises the left menu hand; M or Esc closes it. Hold right mouse for pointer control. Y opens chat, Enter sends, Esc cancels. Alt-tab releases controls. Ctrl + Alt + F10 switches mode; Ctrl + Alt + F12 releases inputs. The optional panel offers Rest hands and Pointer; the duplicate Extended preset stays hidden.

**speeeeeeen** is saved by the broker and defaults off. Numpad 5 starts or stops rotation; 4 / 6 roll the entire body sideways, and 8 / 2 flip it forward or back. The starfish icon illustrates the option. The status only calls real rotation active after native acknowledgement. The small motion-sickness note remains beside this option.

Only acknowledged mode changes trigger the switch wipe and a shuffled mascot celebration: run/bonk, flip/thumbs-up, self-munch or rocket/spin. The mascot uses a 64-DIP image with frozen cached 64/96/128-pixel atlas crops at 100/150/200% scaling. Gold lettering is cached vector geometry; the starfish is one frozen small decoded image. All graphics share one elapsed-time clock, with a 24 Hz target and cap, precomputed poses and no per-frame bitmap allocation. Hidden/minimized/unloaded graphics unsubscribe, and the clock stops with no subscribers. No video, GIF, WebView, GPU effect, timer-resolution or power-setting change is used.

Settings reads startup registration again when opened. It accepts the exact adjacent stable launcher or an updater-verified original bootstrap launcher; uncertainty disables sign-in controls. It never overwrites a foreign startup entry. The legacy helper's standalone UI fallback remains tested, but the new compact release does not offer an unverified fallback. There are no automatic registry changes.

Update checks run off the Dispatcher at resident startup, every six hours, or on explicit Check. The panel displays a cached result; broker polling and settings rendering perform no update verification or downloads. Updates only stage verified releases. The separate stable launcher activates a staged version after the current session normally ends. Height edits and hiding the panel preserve update lifetime; explicit panel exit cancels it.

State paths use Common's canonical `%USERPROFILE%\VRC-SWITCHEROONIE\config` and `reports` directories. The service remains ready while tracking is unavailable. Existing vendor display connectivity is still required; headset-free VRChat continuity is not certified by this UI.

The whole app was vibe coded with GPT-6.1 Sol, bingbongs and Codex / OpenAI. Settings credits .NET / WPF, OpenVR / SteamVR, MinHook and the user-supplied mascot artwork. Third-party licenses ship with the package.

## Review images

These are labelled offscreen fixtures, with illustrative status and no device connection:

![VR panel](../../docs/screenshots/main-vr.png)
![Desktop panel](../../docs/screenshots/main-desktop.png)
![Settings](../../docs/screenshots/settings.png)
![Help](../../docs/screenshots/help.png)
![Credits and spin](../../docs/screenshots/spin.png)

## Isolated validation

Fixture branches run before resident initialization. They perform no broker connection, startup reads/writes, hotkey registration, cursor capture or update network request, and never show a window:

```powershell
Switcheroonie.UI.exe --self-test-compact "C:\absolute\checks.txt"
Switcheroonie.UI.exe --render-preview "C:\absolute\previews"
Switcheroonie.UI.exe --measure-animation "C:\absolute\performance.json"
```

The height lifetime regression invokes a connected offscreen slider event and stops its debounce synchronously before any command can be issued. Preview renders cover 100/150/200% scaling, minimum size, waiting, each celebration, switch graphics, help, credits/spin and the optional pad. The three session rows reflect source evidence; screenshots prove layout only.

The detached animation fixture measures all three graphics' property updates together after warmup. In the current run it applied 117 updates per graphic over 6.001 seconds (about 19.50 Hz), using 156.25 ms aggregate process CPU and 118,808 managed allocated bytes. The hidden phase applied zero graphic updates, used zero measured process CPU, and had zero subscribers with the animation clock stopped. The Windows scheduler delivered fewer updates than the target. This does not measure real visible compositor/GPU presentation or concurrent VRChat performance.

Publish into a separate folder with the normal package options:

```powershell
dotnet publish research/compact-ui/CompactUi.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false --artifacts-path "C:\absolute\isolated-build" -o "C:\absolute\isolated-publish"
```

Production packaging owns the final matching Common / Updater binaries, signed release inventory and activation. This project does not replace a running panel or deploy runtime changes.

