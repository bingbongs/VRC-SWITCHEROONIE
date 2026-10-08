using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Switcheroonie;

namespace Switcheroonie.Broker;

public sealed class HarnessEngine : IDisposable
{
    readonly object sync = new();
    readonly SemaphoreSlim transition = new(1, 1);
    readonly SharedChannel channel;
    readonly IGameInputPlatform gamePlatform;
    readonly GameInputController gameInput = new();
    readonly SpinController spin = new();
    readonly string gameConfigPath;
    readonly string routingConfigPath;
    readonly string spinConfigPath;
    bool spinEnabled;
    uint stoppedSpinReason;
    readonly AutomaticRouting automaticRouting = new();
    bool automaticEnabled = true, automaticPending, disposed, routeAdopted;
    string? manualMode;
    long routingGeneration;
    string adaptationDetail = "Service ready · waiting for the selected headset route.";
    bool gameEnabled = true, gameActive;
    double gameSensitivity = 1;
    string inputOwner = "Released", gameDetail = "Choose Desktop controls, then click into VRChat.";
    string spinDetail = "Off";
    long publishedWindow, menuPulseUntil, nextMenuPulseAt;
    readonly Queue<bool> pendingMenuPulses = new();
    bool menuPulseRequiresGame;
    bool publishedArmed;
    bool oscEnabled;
    int oscPort = 9000;
    readonly OscSender emergencyOsc = new();
    bool helperWasAlive;
    int neutralRounds;
    readonly Dictionary<string, Reply> requests = new();
    readonly Dictionary<string, Task<Reply>> manualRequests = new();
    readonly Dictionary<string, int> keys;
    ulong epoch = unchecked((ulong)Stopwatch.GetTimestamp());
    bool desktop, armed, preparing;
    long ownerWindow, inputTime;
    double height, yaw, pitch, handYaw, handPitch;
    int preset;
    uint actions;
    string detail = "Experimental driver required; hardware verification pending.";
    public HarnessEngine(SharedChannel channel, IGameInputPlatform? gamePlatform = null, string? configurationDirectory = null)
    {
        this.channel = channel;
        this.gamePlatform = gamePlatform ?? new NullGameInputPlatform();
        var directory = configurationDirectory ?? Identity.ConfigurationDirectory;
        try { Directory.CreateDirectory(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { detail = "Preferences directory unavailable; service will stay ready with defaults."; }
        var config = Path.Combine(directory, "keyboard.json");
        gameConfigPath = Path.Combine(directory, "direct-input.json");
        routingConfigPath = Path.Combine(directory, "routing.json");
        spinConfigPath = Path.Combine(directory, "spin.json");
        try
        {
            if (File.Exists(spinConfigPath)) spinEnabled = JsonSerializer.Deserialize<SpinPreferences>(File.ReadAllText(spinConfigPath))?.Enabled == true;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { spinEnabled = false; }
        spinDetail = spinEnabled ? "Checking spin readiness" : "Off";
        this.gamePlatform.SetSpinEnabled(spinEnabled);
        try
        {
            if (File.Exists(routingConfigPath))
            {
                var preferences = JsonSerializer.Deserialize<RoutingPreferences>(File.ReadAllText(routingConfigPath));
                if (preferences is null || preferences.ManualMode is not null and not "Physical" and not "Desktop")
                    throw new JsonException("Invalid routing preference.");
                automaticEnabled = preferences.Automatic; manualMode = preferences.ManualMode;
            }
            else SaveRoutingSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { automaticEnabled = false; adaptationDetail = "Automatic preferences could not be read; resume Auto in the panel."; }
        try
        {
            if (File.Exists(gameConfigPath))
            {
                var settings = JsonSerializer.Deserialize<GameInputSettings>(File.ReadAllText(gameConfigPath));
                if (settings is not null && double.IsFinite(settings.Sensitivity) && settings.Sensitivity is >= 0.1 and <= 5)
                {
                    gameEnabled = settings.Enabled; gameSensitivity = settings.Sensitivity;
                    if (double.IsFinite(settings.Height) && settings.Height is >= -1.5 and <= 1.5) height = settings.Height;
                }
            }
            else SaveGameSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { gameEnabled = false; gameDetail = "Game input settings could not be read; apply settings in the panel."; }
        var defaults = new Dictionary<string, int> { ["Forward"] = 0x57, ["Back"] = 0x53, ["Left"] = 0x41, ["Right"] = 0x44, ["Jump"] = 0x20, ["Run"] = 0x10 };
        try { keys = File.Exists(config) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(config)) ?? defaults : defaults; }
        catch { keys = defaults; detail = "Keyboard configuration invalid; defaults loaded."; }
        if (defaults.Keys.Any(key => !keys.TryGetValue(key, out var code) || code is < 1 or > 254)) keys = defaults;
        this.gamePlatform.ConfigureKeys(keys);
        try
        {
            if (!File.Exists(config)) File.WriteAllText(config, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { detail = "Default keyboard controls loaded; preferences could not be saved."; }
        AdoptRouteLocked(channel.Read());
    }
    static bool Fresh(DriverSnapshot source) => source.Alive && source.HasHead && double.IsFinite(source.HeadAge) && source.HeadAge is >= 0 and < 200;
    void ReleasePadLocked()
    {
        armed = false; actions = 0; ownerWindow = 0;
        if (inputOwner == "Pad") { publishedArmed = false; publishedWindow = 0; inputOwner = "Released"; }
    }
    void ReleaseLocked()
    {
        spin.Reset();
        stoppedSpinReason = 0;
        ReleasePadLocked(); gameInput.Release(); gameActive = publishedArmed = false;
        pendingMenuPulses.Clear(); menuPulseUntil = nextMenuPulseAt = 0;
        inputOwner = "Released"; publishedWindow = 0;
        gamePlatform.SetActive(false); gamePlatform.SetTyping(gameInput.Typing); gamePlatform.SetCapture(false);
        spinDetail = spinEnabled ? "Checking spin readiness" : "Off";
        channel.Publish(epoch, desktop, false, height, yaw, pitch, handYaw, handPitch, preset, 0, 0, 0, oscEnabled, oscPort, spin: spin);
    }
    void SaveGameSettings() => File.WriteAllText(gameConfigPath,
        JsonSerializer.Serialize(new GameInputSettings(gameEnabled, gameSensitivity, height), new JsonSerializerOptions { WriteIndented = true }));
    void SaveRoutingSettings()
    {
        var temporary = routingConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new RoutingPreferences(automaticEnabled, manualMode), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, routingConfigPath, true);
    }
    void SaveSpinSettings(bool enabled)
    {
        var temporary = spinConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new SpinPreferences(enabled)));
        File.Move(temporary, spinConfigPath, true);
    }
    static long NowMilliseconds => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
    async Task ApplyAutomaticAsync(string target, long generation, string? expectedManual)
    {
        bool accepted = false;
        try { accepted = (await SwitchAsync(new Command { Name = "RequestMode", Mode = target }, automatic: true, generation: generation, expectedManual: expectedManual)).Accepted; }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
        finally
        {
            lock (sync)
            {
                automaticPending = false;
                if (!disposed && generation == routingGeneration) automaticRouting.Complete(accepted, NowMilliseconds);
            }
        }
    }
    void AdoptRouteLocked(DriverSnapshot source)
    {
        if (routeAdopted || preparing || !Fresh(source) || source.Error is not 0 and not 9) return;
        // A service reconnect must not recapture the current desktop anchor.
        routeAdopted = true; desktop = source.Desktop; epoch = source.Epoch;
        if (channel.ReadPoseIntent(source.Epoch, source.Desktop) is { } retained)
        {
            if (source.Desktop) height = retained.Height;
            yaw = retained.Yaw; pitch = retained.Pitch; handYaw = retained.HandYaw; handPitch = retained.HandPitch;
        }
        ReleaseLocked(); detail = "Existing committed routing adopted; gameplay inputs released.";
    }
    bool QueueMenuPulse(bool requiresGame = false)
    {
        if (pendingMenuPulses.Count >= 4) return false;
        pendingMenuPulses.Enqueue(requiresGame); return true;
    }
    public void Tick()
    {
        lock (sync)
        {
            if (disposed) return;
            var source = channel.Read();
            AdoptRouteLocked(source);
            var automatic = automaticRouting.Evaluate(source, NowMilliseconds, automaticEnabled, manualMode, preparing || automaticPending);
            adaptationDetail = automatic.Detail;
            if (automatic.Target is { } destination && !automaticPending)
            {
                automaticPending = true;
                long generation = routingGeneration;
                string? expectedManual = manualMode;
                _ = Task.Run(() => ApplyAutomaticAsync(destination, generation, expectedManual));
            }
            bool helperAlive = channel.OscWatchdogAlive;
            if (oscEnabled && !helperAlive)
            {
                if (helperWasAlive) { neutralRounds = 25; ReleaseLocked(); detail = "OSC watchdog unavailable; controls released."; }
                if (neutralRounds > 0)
                {
                    --neutralRounds;
                    try { emergencyOsc.Configure(true, "127.0.0.1", oscPort); emergencyOsc.ForceNeutral(); }
                    catch (System.Net.Sockets.SocketException) { detail = "OSC emergency neutral send failed; check local routing."; }
                }
            }
            helperWasAlive = helperAlive;
            if (!preparing && source.Alive && source.Epoch == epoch && source.Desktop != desktop)
            { desktop = source.Desktop; ++epoch; ReleaseLocked(); detail = "Driver fallback adopted; controls released."; }
            bool routeReady = Fresh(source) && source.Desktop && source.Epoch == epoch && !preparing && (!oscEnabled || helperAlive);
            bool focused = armed && ownerWindow != 0 && GetForegroundWindow() == new IntPtr(ownerWindow) &&
                Stopwatch.GetElapsedTime(inputTime).TotalMilliseconds < 200 && routeReady;
            if (!focused && armed) ReleasePadLocked();
            double forward = 0, strafe = 0;
            uint owned = focused ? actions : 0;
            GameInputFrame game = new();
            bool spinEligible = spinEnabled && Fresh(source) && source.Error is 0 or 9 && (source.CapabilityFlags & 64) != 0 && source.SpinBlockReason != 3 &&
                source.Epoch == epoch && !preparing && !focused;
            try
            {
                if (gamePlatform is WindowsGameInput windows && windows.InitializationError is { } failure)
                    throw new InvalidOperationException(failure);
                var sample = gamePlatform.Read((gameEnabled && routeReady || spinEligible) && !focused);
                if (sample.Emergency) { ReleaseLocked(); detail = "Emergency release; click VRChat again to resume."; }
                game = gameInput.Step(sample, gameEnabled && routeReady && !focused, gameSensitivity, observeChat: spinEligible);
                // Native refusal ends this motion attempt. Never accumulate a
                // hidden quaternion which could jump when tracker data returns.
                bool spinRefused = spin.Active && source.SpinBlockReason != 0 && source.SpinAttemptGeneration == spin.Generation;
                if (spinRefused) { stoppedSpinReason = source.SpinBlockReason; spin.Reset(); }
                else if (sample.SpinToggle) stoppedSpinReason = 0;
                // Both routes share one game-window chat state, including when
                // Desktop movement is disabled or the driver falls back to VR.
                spin.Step(sample, spinEligible && !spinRefused, sharedChatState: true, observedTyping: gameInput.Typing,
                    head: source, seconds: Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
                gamePlatform.SetActive(game.Active);
                gamePlatform.SetTyping(gameInput.Typing);
                gamePlatform.SetCapture(game.Capture, game.Pointer);
                bool nativeSpinCurrent = source.SpinActive && source.SpinGeneration == spin.Generation &&
                    ((source.CapabilityFlags & 128) == 0 || source.SpinAttemptGeneration == spin.Generation);
                spinDetail = !spinEnabled ? "Off" : !Fresh(source) ? "Waiting for tracking" :
                    source.Error is not 0 and not 9 ? "Spin unavailable · check diagnostics" :
                    (source.CapabilityFlags & 64) == 0 ? "Spin unavailable · driver update needed" :
                    source.SpinBlockReason == 3 ? "Spin blocked · restart SteamVR" :
                    preparing || source.Epoch != epoch ? "Waiting for mode switch" :
                    focused ? "Release panel controls to spin" :
                    !sample.Focused || sample.Window == 0 ? "Click VRChat to spin" :
                    gameInput.Typing ? "Close chat to spin" :
                    !source.PhysicalPoseValid ? "Waiting for headset pose" :
                    stoppedSpinReason != 0 ? stoppedSpinReason switch
                    {
                        1 => desktop ? "When tracking is ready: VR → Desktop, then Numpad 5" : "Tracking stopped spin · Numpad 5 to retry",
                        2 => "Rig changed · VR → Desktop, then Numpad 5",
                        3 => "Spin blocked · restart SteamVR",
                        _ => "Spin blocked · check diagnostics"
                    } :
                    spin.Active ? nativeSpinCurrent ? "Spinning · Numpad 5 stops" : "Starting · Numpad 5 stops" :
                    "Ready · Numpad 5 starts";
                gameDetail = !gameEnabled ? "Game-window controls are disabled." : !routeReady ? "Choose Desktop controls with fresh headset tracking." :
                    gameInput.Typing ? "Chat entry: desktop input released. Enter sends; Esc cancels." :
                    game.Active ? gameInput.MenuNavigation ? "VRChat active · mouse aims the menu pointer." : "VRChat active · mouse look and keyboard movement." :
                    sample.Focused ? "Click once inside VRChat to resume controls." : "Controls released · click into the VRChat window.";
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ObjectDisposedException)
            { ReleaseLocked(); gameEnabled = false; gameDetail = "Game input unavailable: " + e.Message; spinDetail = "Spin input unavailable · check diagnostics"; }
            gameActive = game.Active;
            if (game.Active)
            {
                yaw = Math.IEEERemainder(yaw + game.Yaw, Math.Tau);
                pitch = Math.Clamp(pitch + game.Pitch, -Math.PI * 85 / 180, Math.PI * 85 / 180);
                if (game.Pointer)
                {
                    handYaw = Math.Clamp(handYaw + game.HandYaw, -Math.PI, Math.PI);
                    handPitch = Math.Clamp(handPitch + game.HandPitch, -Math.PI * 80 / 180, Math.PI * 80 / 180);
                }
                else { handYaw = 0; handPitch = pitch; }
                preset = gameInput.MenuNavigation ? 3 : game.Pointer ? 2 : 0;
                forward = game.Forward; strafe = game.Strafe; owned = game.Actions;
                if (game.MenuEdge)
                {
                    handYaw = handPitch = 0;
                    if (!QueueMenuPulse(true)) { gameInput.ToggleMenu(); preset = gameInput.MenuNavigation ? 3 : 0; }
                }
            }
            if (focused)
            {
                bool Down(string key) => (GetAsyncKeyState(keys[key]) & 0x8000) != 0;
                forward = (Down("Forward") ? 1 : 0) - (Down("Back") ? 1 : 0);
                strafe = (Down("Right") ? 1 : 0) - (Down("Left") ? 1 : 0);
                if (Down("Jump")) owned |= 8;
                if (Down("Run")) owned |= 16;
            }
            long now = Stopwatch.GetTimestamp();
            if (!routeReady) { pendingMenuPulses.Clear(); menuPulseUntil = nextMenuPulseAt = 0; }
            if (menuPulseRequiresGame && !game.Active) menuPulseUntil = 0;
            while (pendingMenuPulses.TryPeek(out bool requiresGame) && requiresGame && !game.Active) pendingMenuPulses.Dequeue();
            if (routeReady && pendingMenuPulses.Count > 0 && now >= nextMenuPulseAt)
            {
                menuPulseRequiresGame = pendingMenuPulses.Dequeue();
                menuPulseUntil = now + (long)(Stopwatch.Frequency * 0.180);
                nextMenuPulseAt = menuPulseUntil + (long)(Stopwatch.Frequency * 0.060);
            }
            bool menuPulse = routeReady && now < menuPulseUntil;
            if (menuPulse) owned |= 4;
            publishedArmed = focused || game.Active || menuPulse;
            inputOwner = focused ? "Pad" : game.Active ? "Game" : menuPulse ? "MenuPulse" : "Released";
            publishedWindow = focused ? ownerWindow : game.Active ? game.Window : 0;
            channel.Publish(epoch, desktop, publishedArmed, height, yaw, pitch, handYaw, handPitch, preset,
                oscEnabled ? owned & 103u : owned, oscEnabled ? 0 : forward, oscEnabled ? 0 : strafe,
                oscEnabled, oscPort, forward, strafe, owned & 24, spin);
        }
    }
    public HarnessStatus Status()
    {
        lock (sync)
        {
            if (disposed) return new() { ServiceState = "Stopped", AutomaticEnabled = false };
            var source = channel.Read();
            string state = preparing ? "Preparing" : !source.Alive ? "Idle" : source.Desktop ? (Fresh(source) ? "Desktop" : "WaitingForHeadset") : Fresh(source) ? "PhysicalVR" : "Degraded";
            return new()
            {
                ServiceState = "Ready", RoutingReady = Fresh(source) && source.Error is 0 or 9,
                AutomaticEnabled = automaticEnabled, ManualMode = manualMode, AdaptationDetail = adaptationDetail,
                HeadsetWornKnown = Fresh(source) && source.ProximityKnown,
                HeadsetWorn = Fresh(source) && source.ProximityKnown && source.ProximityActive,
                State = state, Mode = !source.Alive ? "Unmanaged" : source.Desktop ? "Desktop" : "Physical", Epoch = source.Epoch,
                Runtime = source.Alive ? "Experimental SteamVR driver responding" : "No harness driver heartbeat",
                Tracking = !source.Alive ? "Harness tracking capture unavailable" : Fresh(source) ? "Fresh physical source captured before routing" : "Harness physical capture unavailable or stale",
                Input = publishedArmed ? inputOwner + (oscEnabled ? " · native pointer + OSC movement" : " · native controller actions") : "Released",
                DriverAlive = source.Alive, HasHead = source.HasHead, HasLeft = source.HasLeft, HasRight = source.HasRight,
                Armed = publishedArmed, InputOwner = inputOwner, OwnerWindow = publishedWindow,
                GameInputEnabled = gameEnabled, GameInputActive = gameActive, GameSensitivity = gameSensitivity,
                GameCursorCaptured = gamePlatform is WindowsGameInput cursor && cursor.CaptureActive,
                GameCursorHidden = gamePlatform is WindowsGameInput visibility && visibility.CursorHidden,
                GameCursorSurfaceVisible = gamePlatform is WindowsGameInput surface && surface.GameCursorSurfaceVisible,
                GameCursorSurfaceOwnsPoint = gamePlatform is WindowsGameInput surfaceOwner && surfaceOwner.GameCursorSurfaceOwnsPoint,
                GameCursorInfoAvailable = gamePlatform is WindowsGameInput cursorInfo && cursorInfo.GameCursorInfoAvailable,
                GameCursorHideAttempts = gamePlatform is WindowsGameInput attempts ? attempts.GameCursorHideAttempts : 0,
                GameCursorHideObserved = gamePlatform is WindowsGameInput observed ? observed.GameCursorHideObserved : 0,
                GameCursorWatchdogAlive = gamePlatform is WindowsGameInput watchdog && watchdog.CursorWatchdogAlive,
                MenuNavigation = gameInput.MenuNavigation, GameInputDetail = gameDetail,
                SpinEnabled = spinEnabled, SpinActive = spin.Active,
                SpinDetail = spinDetail,
                SpinRollSpeed = spin.RollSpeed, SpinPitchSpeed = spin.PitchSpeed, NativeSpinActive = source.SpinActive,
                NativeGenericTrackerAvailable = source.GenericTrackerAvailable, NativeGenericTrackerSuspended = source.GenericTrackerSuspended,
                NativeSpinBlockReason = source.SpinBlockReason,
                NativeSpinAttemptGeneration = source.SpinAttemptGeneration,
                OscEnabled = oscEnabled, OscWatchdogAlive = channel.OscWatchdogAlive, Height = height,
                HeadAgeMilliseconds = double.IsFinite(source.HeadAge) ? source.HeadAge : -1,
                PhysicalSamples = source.PhysicalSamples, RoutedSamples = source.RoutedSamples,
                NativeCapabilityFlags = source.CapabilityFlags,
                NativeInputCoverage = source.InputCoverage, NativeMenuPath = source.SelectedMenuPath,
                NativeMenuRisingEdges = source.MenuRisingEdges, NativeMenuPressed = source.MenuPressed,
                NativeLastInputError = source.LastInputError, NativeEffectiveActions = source.EffectiveNativeActions,
                NativeInputArmed = source.InputArmed,
                Detail = source.Error != 0 ? Error(source.Error) : detail
            };
        }
    }
    public static string Error(uint code) => code switch
    {
        1 => "No physical HMD captured. Connect the selected headset route.",
        2 => "Physical tracking is stale. Physical return was not committed.",
        3 => "Driver input lease expired; physical passthrough is the fallback.",
        4 => "Driver rejected a request or startup configuration schema.",
        5 => "Runtime interception conflict; experimental routing refused.",
        6 => "SteamVR build or interface mismatch; routing refused.",
        7 => "Experimental driver has not been enabled.",
        8 => "Driver IPC unavailable.",
        9 => "Controller input mapping incomplete; menu/locomotion needs validation.",
        10 => "Driver could not resolve or read its startup configuration; check the masked SteamVR startup diagnostic.",
        _ => "Driver fault " + code
    };
    public async Task<Reply> ExecuteAsync(Command command)
    {
        if (command.Version != 1 || command.Id is null || command.Id.Length is < 1 or > 128) return new(false, "Unsupported protocol or request ID.", Status());
        if (command.Name == "RequestMode") return await RequestManualAsync(command);
        lock (sync)
        {
            if (disposed) return new(false, "Service stopped.", Status());
            try
            {
                switch (command.Name)
                {
                    case "GetStatus": break;
                    case "ConfigureAutomatic":
                        ++routingGeneration; automaticRouting.Reset(); ReleaseLocked();
                        automaticEnabled = command.Enabled;
                        if (automaticEnabled) manualMode = null;
                        SaveRoutingSettings();
                        adaptationDetail = automaticEnabled ? "Auto resumed · waiting for stable headset wear information." : "Automatic switching is paused.";
                        break;
                    case "ReleaseInputs": ReleaseLocked(); break;
                    case "ConfigureSpin":
                        SaveSpinSettings(command.Enabled); spinEnabled = command.Enabled;
                        spin.Reset(); gamePlatform.SetSpinEnabled(spinEnabled);
                        stoppedSpinReason = 0;
                        spinDetail = spinEnabled ? "Checking spin readiness" : "Off"; break;
                    case "DisarmViewer":
                        if (armed && command.Window == ownerWindow) ReleasePadLocked();
                        break;
                    case "ConfigureGameInput":
                        if (!double.IsFinite(command.Sensitivity) || command.Sensitivity is < 0.1 or > 5)
                            return new(false, "Mouse sensitivity must be between 0.1 and 5.", Status());
                        ReleaseLocked(); gameEnabled = command.Enabled; gameSensitivity = command.Sensitivity; SaveGameSettings(); break;
                    case "ToggleMenu":
                        var menuSource = channel.Read();
                        if (!Fresh(menuSource) || !menuSource.Desktop || menuSource.Epoch != epoch || preparing ||
                            (oscEnabled && !channel.OscWatchdogAlive))
                            return new(false, "Desktop routing must be committed before using the menu.", Status());
                        if (!QueueMenuPulse()) return new(false, "Menu input busy; wait for the current presses to finish.", Status());
                        ReleasePadLocked(); gameInput.ToggleMenu(); handYaw = handPitch = 0;
                        preset = gameInput.MenuNavigation ? 3 : 0; break;
                    case "ArmViewer":
                        var source = channel.Read();
                        GetWindowThreadProcessId(new IntPtr(command.Window), out var process);
                        using (var candidate = Process.GetProcessById((int)process))
                        {
                            if (candidate.SessionId != Process.GetCurrentProcess().SessionId || GetForegroundWindow() != new IntPtr(command.Window) ||
                                !source.Alive || !source.Desktop || source.Epoch != epoch || preparing)
                                return new(false, "Arm the interaction pad while desktop routing is active and this window is foreground.", Status());
                        }
                        gameInput.Release(); gamePlatform.SetCapture(false);
                        ownerWindow = publishedWindow = command.Window; armed = publishedArmed = true;
                        inputOwner = "Pad"; inputTime = Stopwatch.GetTimestamp(); break;
                    case "UpdateInput":
                        if (!armed || command.Window != ownerWindow || !command.Armed) return new(false, "Input lease is not armed.", Status());
                        if (!Finite(command.Yaw, command.Pitch, command.HandYaw, command.HandPitch) || Math.Abs(command.Yaw) > Math.PI || Math.Abs(command.Pitch) > Math.PI || Math.Abs(command.HandYaw) > Math.PI * 4 || Math.Abs(command.HandPitch) > Math.PI / 2 || (command.Actions & ~7u) != 0)
                            return new(false, "Input values out of range.", Status());
                        yaw = Math.IEEERemainder(yaw + command.Yaw, Math.Tau); pitch = Math.Clamp(pitch + command.Pitch, -Math.PI * 85 / 180, Math.PI * 85 / 180);
                        handYaw = command.HandYaw; handPitch = command.HandPitch; actions = command.Actions; inputTime = Stopwatch.GetTimestamp(); break;
                    case "SetDesktopHeight":
                        if (!double.IsFinite(command.Height) || command.Height is < -1.5 or > 1.5) return new(false, "Height must be between -1.5 and 1.5 metres.", Status());
                        height = command.Height; SaveGameSettings(); break;
                    case "SetHandPreset":
                        if (command.Preset is < 0 or > 3) return new(false, "Unknown hand preset.", Status());
                        preset = command.Preset; break;
                    case "Recenter": yaw = pitch = handYaw = handPitch = 0; break;
                    case "ConfigureOsc":
                        if (command.Osc && ((command.Destination ?? "127.0.0.1") != "127.0.0.1" || command.Port is < 1 or > 65535)) return new(false, "OSC requires 127.0.0.1 and port 1–65535.", Status());
                        if (command.Osc && !channel.OscWatchdogAlive) return new(false, "OSC watchdog is not responding; OSC was not enabled.", Status());
                        ReleaseLocked(); oscEnabled = command.Osc; oscPort = command.Port; helperWasAlive = channel.OscWatchdogAlive; break;
                    case "ExportDiagnostics":
                        Directory.CreateDirectory(Identity.ReportsDirectory);
                        var report = Path.Combine(Identity.ReportsDirectory, "broker-diagnostics.json");
                        File.WriteAllText(report, JsonSerializer.Serialize(Status(), new JsonSerializerOptions { WriteIndented = true }));
                        return new(true, "Saved broker-diagnostics.json in the application data directory.", Status());
                    default: return new(false, "Unknown command.", Status());
                }
                return new(true, "Accepted", Status());
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            { ReleaseLocked(); return new(false, "Command rejected: " + e.Message, Status()); }
        }
    }
    static bool Finite(params double[] values) => values.All(double.IsFinite);
    Task<Reply> RequestManualAsync(Command command)
    {
        lock (sync)
        {
            if (disposed) return Task.FromResult(new Reply(false, "Service stopped.", Status()));
            if (requests.TryGetValue(command.Id, out var prior)) return Task.FromResult(prior);
            if (manualRequests.TryGetValue(command.Id, out var pending)) return pending;
            if (command.Mode is not "Desktop" and not "Physical") return Task.FromResult(new Reply(false, "Choose Desktop or Physical.", Status()));
            // User intent supersedes queued and in-flight automatic work before
            // waiting for the native transaction gate. Repeated IDs share work.
            manualMode = command.Mode; long generation = ++routingGeneration; automaticRouting.Reset();
            try { SaveRoutingSettings(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { detail = "Manual selection retained for this run; routing preferences could not be saved."; }
            ReleaseLocked();
            var completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
            manualRequests.Add(command.Id, completion.Task);
            _ = CompleteManualAsync(command, generation, completion);
            return completion.Task;
        }
    }
    async Task CompleteManualAsync(Command command, long generation, TaskCompletionSource<Reply> completion)
    {
        try { completion.TrySetResult(await SwitchAsync(command, generation: generation)); }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            lock (sync)
            {
                if (!disposed) ReleaseLocked();
                completion.TrySetResult(new(false, "Mode change could not complete; preference retained.", Status()));
            }
        }
        finally { lock (sync) manualRequests.Remove(command.Id); }
    }
    bool IntentCurrent(long generation, bool automatic, string? expectedManual) =>
        generation == routingGeneration && (!automatic || (expectedManual is null ? automaticEnabled && manualMode is null : manualMode == expectedManual));
    Reply SupersededLocked(Command command)
    {
        var observed = channel.Read(); desktop = observed.Alive && observed.Desktop; ++epoch; preparing = false;
        ReleaseLocked(); detail = "Routing change superseded by the user's latest preference; controls released.";
        return Remember(command.Id, new(false, detail, Status()));
    }
    async Task<Reply> SwitchAsync(Command command, bool automatic = false, long generation = 0, string? expectedManual = null)
    {
        await transition.WaitAsync();
        try
        {
            bool target;
            lock (sync)
            {
                if (disposed) return new(false, "Service stopped.", new() { ServiceState = "Stopped" });
                if (!IntentCurrent(generation, automatic, expectedManual))
                    return Remember(command.Id, new(false, "Routing change superseded by the user's preference.", Status()));
                if (requests.TryGetValue(command.Id, out var prior)) return prior;
                if (command.Mode is not "Desktop" and not "Physical") return new(false, "Choose Desktop or Physical.", Status());
                target = command.Mode == "Desktop";
                var source = channel.Read();
                AdoptRouteLocked(source); ReleaseLocked();
                if (!Fresh(source)) return Remember(command.Id, new(false, "Mode preference saved; waiting for fresh independent headset tracking. No routing change has been committed. Headset-free VRChat continuity is still under development.", Status()));
                if (source.Desktop == target && source.Epoch == epoch) return Remember(command.Id, new(true, "Already committed.", Status()));
                preparing = true; desktop = target; gameInput.Reset(); ++epoch; yaw = pitch = handYaw = handPitch = 0;
                detail = "Waiting for the driver to acknowledge the new routing epoch.";
            }
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 750)
            {
                await Task.Delay(10);
                lock (sync)
                {
                    if (disposed) return new(false, "Service stopped.", new() { ServiceState = "Stopped" });
                    if (!IntentCurrent(generation, automatic, expectedManual)) return SupersededLocked(command);
                    var source = channel.Read();
                    if (Fresh(source) && source.Epoch == epoch && source.Desktop == target && source.Error is 0 or 9)
                    {
                        preparing = false; detail = "Routing committed by driver. Headset display and menu usability still need hardware validation.";
                        return Remember(command.Id, new(true, "https://freefbt.com", Status()));
                    }
                }
            }
            lock (sync)
            {
                if (disposed) return new(false, "Service stopped.", new() { ServiceState = "Stopped" });
                if (!IntentCurrent(generation, automatic, expectedManual)) return SupersededLocked(command);
                var source = channel.Read(); desktop = source.Alive && source.Desktop; ++epoch; preparing = false;
                detail = "Switch timed out; inputs released and last observed route retained.";
                return Remember(command.Id, new(false, detail, Status()));
            }
        }
        finally { transition.Release(); }
    }
    Reply Remember(string id, Reply reply) { if (requests.Count >= 128) requests.Remove(requests.Keys.First()); requests[id] = reply; return reply; }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; ++routingGeneration;
            try
            {
                try { ReleaseLocked(); }
                finally
                {
                    desktop = false; ++epoch;
                    channel.Publish(epoch, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, spin: spin);
                }
            }
            finally
            {
                try { emergencyOsc.Dispose(); }
                finally { gamePlatform.Dispose(); }
            }
        }
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}

public sealed record GameInputSettings(bool Enabled = true, double Sensitivity = 1, double Height = 0);
public sealed record SpinPreferences(bool Enabled = false);
