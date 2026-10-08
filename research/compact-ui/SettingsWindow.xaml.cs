using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Switcheroonie.Update;

namespace Switcheroonie.UI;

public partial class SettingsWindow : Window
{
    internal event Action<HarnessStatus>? StatusChanged;
    internal event Action<string>? ConnectionLost;
    internal event Action<string>? MessageChanged;
    internal Action? OpenCompactPanel;
    internal Action? SessionExiting;
    private bool _compactVisible;
    private readonly bool _preview;
    private readonly SpinStarfishView _spinIcon;
    private bool _updatingSpin, _spinCommandPending;
    private bool _spinAnimationAllowed;
    internal bool Connected => _connected;
    internal HarnessStatus CurrentStatus => _status;
    internal string HotkeySummary => HotkeyText.Text;
    internal Task RequestModeFromFrontAsync(string mode) => RequestModeAsync(mode);
    internal Task ReleaseFromFrontAsync() => ReleaseAsync();
    internal Task RefreshFromFrontAsync() => PollAsync();
    internal void SetCompactVisible(bool visible)
    {
        _compactVisible = visible;
        _statusTimer.Interval = visible || IsVisible ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);
    }
    internal void ShowSettings(Window owner)
    {
        if (_closing) return;
        if (_residentInitialized) LoadStartupRegistration(); // Read fresh state; never configure it on open.
        if (_residentInitialized) _ = RefreshStartupLauncherAsync();
        Owner = owner;
        ShowInTaskbar = false;
        Show();
        Activate();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(250);
        if (_residentInitialized) _inputTimer.Start();
    }
    private readonly BrokerClient _broker = new();
    private readonly SemaphoreSlim _brokerGate = new(1, 1);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _inputTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _heightTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly CancellationTokenSource _updateCancellation = new();
    private Task<UpdateStatus>? _updateTask;
    private UpdateStatus? _cachedUpdate;
    private HarnessStatus _status = new();
    private bool _connected, _armed, _arming, _dragging, _closing, _allowClose, _updatingHeight, _residentInitialized;
    private bool _polling, _inputSending, _oscDraftDirty, _updatingOsc, _gameDraftDirty, _updatingGame;
    private bool _automaticDraftDirty, _updatingAutomatic;
    private readonly BrokerRestartPolicy _restartPolicy = new();
    private Process? _startedBrokerProcess;
    private ResidentTray? _tray;
    private uint _taskbarCreatedMessage;
    private StartupRegistration? _startupRegistration;
    private Task<string?>? _startupLookupTask;
    private string? _verifiedStartupLauncher;
    private bool _startupAuthorityResolved;
    private Point _lastPoint;
    private double _yawDelta, _pitchDelta, _handYaw, _handPitch;
    private double _mouseSensitivity = 1;
    private uint _heldActions;
    private long _inputGeneration;
    private HwndSource? _windowSource;
    private nint _window;
    private bool _toggleHotkey, _releaseHotkey;
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(248, 195, 80));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(170, 171, 178));
    private const int ToggleHotkeyId = 0x5310, ReleaseHotkeyId = 0x5312;

    public SettingsWindow() : this(false) { }
    internal SettingsWindow(bool preview)
    {
        _preview = preview;
        InitializeComponent();
        _spinIcon = new SpinStarfishView(preview); SpinIconHost.Children.Add(_spinIcon);
        _statusTimer.Tick += async (_, _) => await PollAsync();
        _inputTimer.Tick += async (_, _) => await SendInputAsync();
        _heightTimer.Tick += async (_, _) =>
        {
            _heightTimer.Stop();
            await SendCommandAsync(new Command { Name = "SetDesktopHeight", Height = HeightSlider.Value });
        };
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
        OscCheck.Checked += (_, _) => { if (!_updatingOsc) _oscDraftDirty = true; };
        OscCheck.Unchecked += (_, _) => { if (!_updatingOsc) _oscDraftDirty = true; };
        GameInputCheck.Checked += (_, _) => { if (!_updatingGame && _connected) _gameDraftDirty = true; };
        GameInputCheck.Unchecked += (_, _) => { if (!_updatingGame && _connected) _gameDraftDirty = true; };
        AutomaticCheck.Checked += (_, _) => { if (!_updatingAutomatic && _connected) _automaticDraftDirty = true; };
        AutomaticCheck.Unchecked += (_, _) => { if (!_updatingAutomatic && _connected) _automaticDraftDirty = true; };
        IsVisibleChanged += (_, _) => UpdateSpinVisibility();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // IsVisibleChanged can precede template attachment. Re-evaluate once
        // the shown settings visual tree has completed its first layout.
        UpdateSpinVisibility();
        if (!_preview) await InitializeResidentAsync();
    }

    internal async Task InitializeResidentAsync()
    {
        if (_preview || _residentInitialized) return;
        _residentInitialized = true;
        _window = new WindowInteropHelper(this).EnsureHandle();
        _windowSource = HwndSource.FromHwnd(_window);
        _windowSource?.AddHook(WindowMessage);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _tray = new ResidentTray(_window, ShowPanel, () => _ = ReleaseAsync(),
            () => _ = RetryServiceAsync(), () => _ = ExitPanelAsync());
        LoadStartupRegistration();
        _ = RefreshStartupLauncherAsync();
        const uint modifiers = 0x0001 | 0x0002 | 0x4000; // ALT | CONTROL | NOREPEAT
        _toggleHotkey = RegisterHotKey(_window, ToggleHotkeyId, modifiers, 0x79); // F10
        _releaseHotkey = RegisterHotKey(_window, ReleaseHotkeyId, modifiers, 0x7B); // F12
        HotkeyText.Text = _toggleHotkey && _releaseHotkey
            ? "Emergency release · Ctrl + Alt + F12"
            : "Shortcut conflict: " + (!_toggleHotkey ? "Ctrl + Alt + F10 unavailable. " : "") + (!_releaseHotkey ? "Ctrl + Alt + F12 unavailable. " : "") + "Use the buttons here.";
        _statusTimer.Interval = _compactVisible || IsVisible ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);
        await PollAsync();
        _statusTimer.Start();
        _updateTimer.Start();
        _ = CheckUpdatesAsync();
    }

    internal void ShowPanel()
    {
        if (_closing) return;
        if (OpenCompactPanel is not null) { OpenCompactPanel(); return; }
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(250);
        if (_residentInitialized) _inputTimer.Start();
    }

    private async Task HidePanelAsync()
    {
        bool hadPad = _armed || _arming;
        ClearLocalInputs();
        Hide();
        ShowInTaskbar = false;
        _statusTimer.Interval = _compactVisible ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);
        _inputTimer.Stop();
        if (hadPad) await ReleaseAsync(false, padOnly: true);
    }

    private async void HidePanel_Click(object sender, RoutedEventArgs e) => await HidePanelAsync();
    private async void RetryService_Click(object sender, RoutedEventArgs e) => await RetryServiceAsync();
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();
    private async Task CheckUpdatesAsync()
    {
        if (_preview || _closing || !_residentInitialized || _updateTask is { IsCompleted: false }) return;
        CheckUpdateButton.IsEnabled = false; UpdateText.Text = "Checking for updates…";
        // Construction, state verification and all staging run away from the
        // Dispatcher. Broker status polling only renders its own status.
        _updateTask = Task.Run(async () =>
        {
            using var updates = new UpdateService();
            return await updates.CheckAndStageAsync(_updateCancellation.Token).ConfigureAwait(false);
        });
        try { _cachedUpdate = await _updateTask; }
        catch (Exception)
        { _cachedUpdate = new UpdateStatus("Unavailable", "Update check unavailable; try again later."); }
        finally
        {
            if (!_closing) { UpdateText.Text = UpdatePresentation.Text(_cachedUpdate); CheckUpdateButton.IsEnabled = true; }
        }
    }
    private async Task RetryServiceAsync()
    {
        // Check first: a running service must retain its mode and configuration.
        await PollAsync();
        if (!_connected) StartBrokerIfNeeded(force: true);
    }
    private async void Readiness_Click(object sender, RoutedEventArgs e) => await PollAsync();
    private void SetupHelp_Click(object sender, RoutedEventArgs e)
    {
        HelpExpander.IsExpanded = true;
        HelpExpander.BringIntoView();
    }

    private void LoadStartupRegistration()
    {
        try
        {
            string adjacentLauncher = Path.Combine(AppContext.BaseDirectory, "VRC-SWITCHEROONIE.exe");
            string? executable = File.Exists(adjacentLauncher) ? adjacentLauncher : _verifiedStartupLauncher;
            if (executable is null)
            {
                _startupRegistration = null;
                StartWithWindowsCheck.IsEnabled = ApplyStartupButton.IsEnabled = false;
                StartupRegistrationText.Text = !_startupAuthorityResolved ? "Checking startup entry…" : "Startup unavailable · verified launcher not found";
                return;
            }
            var registration = new StartupRegistration(new RegistryStartupStore(StartupRegistration.CommandFor(executable)), executable);
            _startupRegistration = registration;
            RenderStartupRegistration(_startupRegistration.Read());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            StartWithWindowsCheck.IsEnabled = ApplyStartupButton.IsEnabled = false;
            StartupRegistrationText.Text = "Startup settings could not be read: " + ex.Message;
        }
    }
    private async Task RefreshStartupLauncherAsync()
    {
        if (_preview || _closing || !_residentInitialized || _startupLookupTask is { IsCompleted: false }) return;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "VRC-SWITCHEROONIE.exe"))) return;
        string currentDirectory = AppContext.BaseDirectory;
        _startupLookupTask = Task.Run(() =>
        {
            using var updates = new UpdateService();
            return updates.ResolveStableLauncher(currentDirectory);
        });
        string? launcher;
        try { launcher = await _startupLookupTask; }
        catch (Exception) { launcher = null; }
        if (_closing) return;
        _verifiedStartupLauncher = launcher; _startupAuthorityResolved = true;
        LoadStartupRegistration();
    }
    private void RenderStartupRegistration(StartupState state)
    {
        StartWithWindowsCheck.IsChecked = state.Enabled;
        StartWithWindowsCheck.IsEnabled = ApplyStartupButton.IsEnabled = true;
        StartupRegistrationText.Text = state.Detail;
    }
    private void StartupApply_Click(object sender, RoutedEventArgs e)
    {
        if (_startupRegistration is null) return;
        try
        {
            bool enabled = StartWithWindowsCheck.IsChecked == true;
            if (enabled && (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Switcheroonie.UI.exe")) ||
                !File.Exists(Path.Combine(AppContext.BaseDirectory, "Switcheroonie.Broker.exe"))))
            {
                StartupRegistrationText.Text = "Keep the panel and broker together in the portable folder before enabling startup.";
                return;
            }
            RenderStartupRegistration(_startupRegistration.Configure(enabled));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            StartupRegistrationText.Text = "Startup setting was not applied: " + ex.Message;
        }
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0) _tray?.Recreate();
        if (_tray?.Handle(message, lParam) == true) { handled = true; return 0; }
        if (message == 0x0312)
        {
            if ((int)wParam == ReleaseHotkeyId) { _ = ReleaseAsync(); handled = true; }
            if ((int)wParam == ToggleHotkeyId)
            {
                _ = RequestModeAsync(_status.Mode == "Desktop" ? "Physical" : "Desktop");
                handled = true;
            }
        }
        return 0;
    }

    private async Task PollAsync()
    {
        if (_polling || _closing || !await _brokerGate.WaitAsync(0)) return;
        _polling = true;
        var generation = _inputGeneration;
        var needStart = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var reply = await _broker.SendAsync(new Command(), timeout.Token);
            if (reply.Status is null) throw new IOException("Broker reply omitted current status");
            if (generation == _inputGeneration) RenderStatus(reply.Status);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            RenderUnavailable(ex.Message);
            needStart = true;
        }
        finally { _polling = false; _brokerGate.Release(); }
        if (needStart) StartBrokerIfNeeded();
    }

    private void StartBrokerIfNeeded(bool force = false)
    {
        if (_closing) return;
        var child = _startedBrokerProcess;
        bool childRunning = BrokerRestartPolicy.ChildMayBeRunning(child is null ? null : () => child.HasExited);
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (!_restartPolicy.CanAttempt(now, childRunning, force)) return;
        _restartPolicy.RecordAttempt(now);
        var brokerPath = Path.Combine(AppContext.BaseDirectory, "Switcheroonie.Broker.exe");
        if (!File.Exists(brokerPath))
        {
            ServiceStateText.Text = "Waiting for the portable service executable";
            MessageText.Text = "Keep Switcheroonie.Broker.exe beside this panel. Readiness retries automatically; Retry service checks again now.";
            return;
        }
        try
        {
            _startedBrokerProcess?.Dispose();
            _startedBrokerProcess = Process.Start(new ProcessStartInfo(brokerPath)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            ServiceStateText.Text = "Starting the background service…";
            MessageText.Text = "Starting the local service. Your current session and selected mode stay unchanged.";
        }
        catch (Exception ex) { MessageText.Text = "Could not start the local broker: " + ex.Message; }
    }

    private async Task<Reply?> SendCommandAsync(Command command, bool showReason = true, long? requiredInputGeneration = null)
    {
        await _brokerGate.WaitAsync();
        var generation = _inputGeneration;
        try
        {
            if (requiredInputGeneration.HasValue && requiredInputGeneration.Value != _inputGeneration) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var reply = await _broker.SendAsync(command, timeout.Token);
            if (reply.Status is null) throw new IOException("Broker reply omitted current status");
            if (generation == _inputGeneration)
            {
                RenderStatus(reply.Status);
                if (showReason) ShowReplyMessage(reply.Reason);
            }
            return reply;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            RenderUnavailable(ex.Message);
            return null;
        }
        finally { _brokerGate.Release(); }
    }

    private void RenderStatus(HarnessStatus status)
    {
        if (!_connected)
            MessageText.Text = "Service ready.";
        _status = status;
        _connected = true;
        _restartPolicy.RecordReady(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        ServiceStateText.Text = "Background service · " + status.ServiceState;
        ServiceDetailText.Text = status.AdaptationDetail;
        ManualModeText.Text = status.ManualMode is "Physical" or "Desktop"
            ? "Manual choice: " + (status.ManualMode == "Desktop" ? "Desktop controls" : "Physical VR") +
                (status.Mode != status.ManualMode || !status.RoutingReady ? " · saved; waiting for routing" : " · active") +
                ". Resume automatic switching to follow headset use again."
            : status.AutomaticEnabled ? "Automatic switching follows fresh, reported headset use." : "Automatic switching is paused.";
        if (!_automaticDraftDirty)
        {
            _updatingAutomatic = true;
            AutomaticCheck.IsChecked = status.AutomaticEnabled;
            _updatingAutomatic = false;
        }
        AutomaticCheck.IsEnabled = ApplyAutomaticButton.IsEnabled = true;
        _tray?.SetStatus(status.DriverAlive ? status.Mode + " · service ready" : "service ready · waiting for VR route");
        ModeText.Text = status.Mode switch { "Desktop" => "Desktop controls", "Physical" => "Physical VR controls", _ => "Waiting for VR routing" };
        ModeText.Foreground = status.DriverAlive && status.HasHead && (status.NativeCapabilityFlags & 1) != 0 ? AccentBrush : MutedBrush;
        StateText.Text = status.State;
        DetailText.Text = status.Detail;
        RuntimeText.Text = status.Runtime;
        TrackingText.Text = status.Tracking;
        DisplayText.Text = status.Display;
        InputText.Text = status.Input;
        GameCaptureText.Text = status.GameInputDetail;
        GameCaptureText.Foreground = status.GameInputActive ? AccentBrush : MutedBrush;
        GameGuideText.Text = status.MenuNavigation
            ? "Menu navigation · mouse aims the hand pointer · left click or drag to use"
            : "Move the mouse to look · WASD to move · Shift to run · Space to jump";
        GameMenuGuideText.Text = status.MenuNavigation
            ? "Esc: close menu and return to head look"
            : "Esc: menu · C / Z: toggle posture · M: menu hand · hold right mouse: pointer";
        ColdStartText.Text = status.ColdStart;
        StartupNextText.Text = !status.DriverAlive
            ? "The service is ready before SteamVR. Start your usual streaming / SteamVR setup when ready; this panel will update automatically."
            : !status.HasHead || !double.IsFinite(status.HeadAgeMilliseconds) || status.HeadAgeMilliseconds is < 0 or >= 200
                ? "Monitoring the current runtime. Waiting for fresh tracking before a route can commit; check your headset / streaming connection."
                : "The current VR route is reporting tracking. Manual selections stay in force; automatic switching can follow reported headset use.";
        EvidenceText.Text = status.Evidence;
        var menuPath = status.NativeMenuPath switch { 1 => "left Y", 2 => "left B", 3 => "right B", 4 => "left application-menu", _ => "none reported" };
        DiagnosticText.Text = $"Broker: responding · service {status.ServiceState}\nAutomatic: {status.AutomaticEnabled} · manual selection: {status.ManualMode ?? "none"}\nReported headset use: {(status.HeadsetWornKnown ? status.HeadsetWorn ? "worn" : "removed" : "unknown")}\nDriver heartbeat: {(status.DriverAlive ? "present" : "absent")}\nGame input: enabled {status.GameInputEnabled}, active {status.GameInputActive}, menu navigation {status.MenuNavigation}\nInput owner: {status.InputOwner}\nNative input coverage: 0x{status.NativeInputCoverage:X2} · menu source: {menuPath}\nNative menu presses accepted: {status.NativeMenuRisingEdges:N0} · pressed {status.NativeMenuPressed}\nNative input armed: {status.NativeInputArmed} · actions 0x{status.NativeEffectiveActions:X2} · reported error {status.NativeLastInputError}\nOSC watchdog: {(status.OscWatchdogAlive ? "responding" : "unavailable")}\nPhysical sources: head {status.HasHead}, left {status.HasLeft}, right {status.HasRight}\nHead sample age: {status.HeadAgeMilliseconds:F1} ms\nPhysical samples: {status.PhysicalSamples:N0} · routed samples: {status.RoutedSamples:N0}\nCommitted epoch: {status.Epoch}\nNative component reports are binding diagnostics; hardware validation is recorded separately.";
        if (!_gameDraftDirty && !SensitivitySlider.IsMouseCaptureWithin)
        {
            _updatingGame = true;
            GameInputCheck.IsChecked = status.GameInputEnabled;
            SensitivitySlider.Value = Math.Clamp(status.GameSensitivity, 0.1, 5);
            _updatingGame = false;
        }
        OscStatusText.Text = status.OscEnabled
            ? (status.OscWatchdogAlive ? "OSC movement enabled · input watchdog responding. Enable OSC in VRChat to receive input." : "OSC watchdog unavailable · inputs disabled")
            : "OSC movement disabled.";
        if (!_oscDraftDirty)
        {
            _updatingOsc = true;
            OscCheck.IsChecked = status.OscEnabled;
            _updatingOsc = false;
        }
        // These also save a waiting manual preference when the native route is
        // offline. Committed Mode still comes exclusively from driver status.
        PhysicalButton.IsEnabled = DesktopButton.IsEnabled = true;
        var desktopAvailable = status.RoutingReady && status.Mode == "Desktop" && (!status.OscEnabled || status.OscWatchdogAlive);
        InteractionPad.IsEnabled = NeutralButton.IsEnabled = ExtendedButton.IsEnabled = MenuPoseButton.IsEnabled = MenuButton.IsEnabled = desktopAvailable;
        HeadRadio.IsEnabled = HandRadio.IsEnabled = desktopAvailable;
        HeightSlider.IsEnabled = ResetHeightButton.IsEnabled = desktopAvailable;
        OscCheck.IsEnabled = ApplyOscButton.IsEnabled = status.DriverAlive;
        GameInputCheck.IsEnabled = ApplyGameInputButton.IsEnabled = SensitivitySlider.IsEnabled = true;
        if (!_spinCommandPending)
        {
            _updatingSpin = true; SpinCheck.IsChecked = status.SpinEnabled; _updatingSpin = false;
        }
        SpinCheck.IsEnabled = !_spinCommandPending;
        SpinStatusText.Text = !status.SpinEnabled ? "Off" : status.SpinActive
            ? status.NativeSpinActive ? "Spinning · Numpad 5 stops" : "Starting · Numpad 5 stops"
            : "Enabled · Numpad 5 starts / stops";
        _spinIcon.SetPlaying(status.SpinEnabled); UpdateSpinVisibility();
        if (!_heightTimer.IsEnabled && !HeightSlider.IsMouseCaptureWithin)
        {
            _updatingHeight = true;
            HeightSlider.Value = status.Height;
            _updatingHeight = false;
        }
        var localPadOwned = status.Armed && status.InputOwner == "Pad" && status.OwnerWindow == _window.ToInt64();
        if (_armed && (!localPadOwned || !desktopAvailable)) ClearLocalInputs();
        RenderPad();
        StatusChanged?.Invoke(status);
    }
    private void ShowReplyMessage(string reason)
    {
        if (reason == "https://freefbt.com")
        {
            MessageText.Inlines.Clear();
            var link = new Hyperlink(new Run("freefbt.com")) { NavigateUri = new Uri("https://freefbt.com"), Foreground = AccentBrush };
            link.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                if (_preview || e.Uri.AbsoluteUri != "https://freefbt.com/") return;
                try { Process.Start(new ProcessStartInfo("https://freefbt.com") { UseShellExecute = true }); }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { MessageText.Text = "Could not open the link."; }
            };
            MessageText.Inlines.Add(link);
            MessageChanged?.Invoke("Routing committed.");
        }
        else { MessageText.Text = reason; MessageChanged?.Invoke(reason); }
    }

    private void RenderUnavailable(string reason)
    {
        _connected = false;
        _restartPolicy.RecordUnavailable();
        ClearLocalInputs();
        ServiceStateText.Text = "Reconnecting background service…";
        ServiceDetailText.Text = "This panel stays resident and retries the local service. Runtime and control state have not been confirmed.";
        ManualModeText.Text = "Stored mode selection will be read when the service responds.";
        AutomaticCheck.IsEnabled = ApplyAutomaticButton.IsEnabled = false;
        _tray?.SetStatus("reconnecting service");
        StartupNextText.Text = "Keep the portable broker beside this panel. Use Retry service to check immediately; background retries continue.";
        ModeText.Text = "Waiting for service status";
        ModeText.Foreground = MutedBrush;
        StateText.Text = "Broker not responding";
        DetailText.Text = "No current control state can be confirmed. Reconnect the local broker before switching.";
        RuntimeText.Text = TrackingText.Text = DisplayText.Text = InputText.Text = "No current broker evidence";
        GameCaptureText.Text = "Game capture state unavailable · broker not responding";
        OscStatusText.Text = "OSC state unavailable · broker not responding";
        DiagnosticText.Text = "Broker communication failed: " + reason;
        MessageText.Text = "Broker unavailable. Emergency release remains available; the runtime watchdog also expires stale input.";
        PhysicalButton.IsEnabled = DesktopButton.IsEnabled = InteractionPad.IsEnabled = NeutralButton.IsEnabled = ExtendedButton.IsEnabled = MenuPoseButton.IsEnabled = MenuButton.IsEnabled = false;
        HeightSlider.IsEnabled = ResetHeightButton.IsEnabled = HeadRadio.IsEnabled = HandRadio.IsEnabled = OscCheck.IsEnabled = ApplyOscButton.IsEnabled = false;
        GameInputCheck.IsEnabled = ApplyGameInputButton.IsEnabled = SensitivitySlider.IsEnabled = false;
        SpinCheck.IsEnabled = false; SpinStatusText.Text = "Waiting for service"; _spinIcon.SetPlaying(false);
        ConnectionLost?.Invoke(reason);
    }

    internal void SetPreviewStatus(bool offline = false, bool physical = false, bool spin = false)
    {
        RenderStatus(new HarnessStatus
        {
            State = "UI preview fixture · illustrative values",
            ServiceState = "Ready · UI preview", AutomaticEnabled = true,
            AdaptationDetail = offline ? "UI preview · Desktop preference saved; waiting for a usable VR route" : "UI preview · monitoring only; no runtime or service is connected",
            ManualMode = offline ? "Desktop" : null, RoutingReady = !offline,
            Mode = offline ? "Unmanaged" : physical ? "Physical" : "Desktop", DriverAlive = !offline, HasHead = !offline, HasLeft = !offline, HasRight = !offline,
            Runtime = offline ? "UI preview · no harness driver heartbeat" : "UI preview · sample driver status",
            Tracking = offline ? "UI preview · waiting for a tracking route" : "UI preview · independent source evidence pending",
            Display = "Vendor display path; headset presentation unverified",
            Input = offline ? "UI preview · released while waiting for a VR route" : "UI preview · waiting for a click inside VRChat",
            GameInputEnabled = true, GameSensitivity = 1, InputOwner = "Released",
            SpinEnabled = spin, SpinActive = spin, NativeSpinActive = spin,
            NativeCapabilityFlags = offline ? 0U : 63U, NativeInputCoverage = offline ? 0U : 0x1FU,
            GameInputDetail = offline ? "UI preview · controls wait for committed VR routing" : "UI preview · click VRChat to take control",
            Detail = "LAYOUT PREVIEW ONLY. No broker or SteamVR connection is made in this fixture.",
            Evidence = "Hardware validation pending · this image proves layout only",
            ColdStart = "Headset-free VR display is still a development target; no runtime continuity is proved by this preview.",
            HeadAgeMilliseconds = offline ? 0 : 8.6, PhysicalSamples = offline ? 0UL : 5821UL, RoutedSamples = offline ? 0UL : 5821UL
        });
        HotkeyText.Text = "Preview only · shortcuts have not been registered";
        StartupRegistrationText.Text = "UI preview only · startup registration has not been inspected or changed.";
        UpdateText.Text = "UI preview · update service not started";
        MessageText.Text = "UI PREVIEW · illustrative, no connection or input capture";
    }

    internal void ScrollPreviewToBottom() => MainScroll.ScrollToEnd();

    internal void ExpandPreviewPad() => AdvancedPadExpander.IsExpanded = true;
    internal void SetPreviewSpinFrame(int frame) => _spinIcon.SetFixtureFrame(frame);
    internal void SetPreviewSpinDpi(double dpi) => _spinIcon.SetDpi(dpi);

    private void Settings_StateChanged(object? sender, EventArgs e) => UpdateSpinVisibility();
    private void Settings_DpiChanged(object sender, DpiChangedEventArgs e) => _spinIcon?.SetDpi(e.NewDpi.PixelsPerInchX);
    private void Settings_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateSpinVisibility();
    private void UpdateSpinVisibility()
        => UpdateSpinVisibility(IsVisible && WindowState != WindowState.Minimized);
    private void UpdateSpinVisibility(bool ownerVisible, bool fixture = false)
    {
        if (_spinIcon is null || SpinIconHost is null || MainScroll is null) { _spinAnimationAllowed = false; return; }
        _spinAnimationAllowed = SpinViewportVisibility.Allows(ownerVisible, SpinIconHost, MainScroll,
            fixture && _preview ? SpinIconHost.Visibility == Visibility.Visible : null);
        _spinIcon.SetAnimationActive(_spinAnimationAllowed);
    }
    internal bool SpinVisibilityFixture(bool ownerVisible, bool minimized = false)
    {
        if (!_preview) throw new InvalidOperationException("Offscreen fixture only");
        // Detached visuals have no PresentationSource, so only presentation
        // flags are simulated. Ancestry, layout and scrolling remain real WPF.
        UpdateSpinVisibility(ownerVisible && !minimized, fixture: true);
        return _spinAnimationAllowed;
    }

    private async void Spin_Click(object sender, RoutedEventArgs e)
    {
        if (_preview || _updatingSpin || _spinCommandPending || !_connected) return;
        _spinCommandPending = true; SpinCheck.IsEnabled = false;
        try
        {
            await ReleaseAsync(false, padOnly: true);
            await SendCommandAsync(new Command { Name = "ConfigureSpin", Enabled = SpinCheck.IsChecked == true });
        }
        finally { _spinCommandPending = false; if (_connected) RenderStatus(_status); }
    }

    private void RenderPad()
    {
        if (InteractionPad is null) return;
        PadStatusText.Text = _armed ? "Optional pad armed · this panel owns its input" : "Released · optional fallback only";
        PadTitleText.Text = _armed ? (HandRadio.IsChecked == true ? "HAND AIM ACTIVE" : "HEAD LOOK ACTIVE") : "CLICK TO ARM DESKTOP CONTROLS";
        PadInstructionText.Text = HandRadio.IsChecked == true
            ? "Right-drag to aim your hand · left click to use · middle click to grab"
            : "Right-drag to look · WASD to move · Shift to run · Space to jump";
        InteractionPad.BorderBrush = _armed ? AccentBrush : new SolidColorBrush(Color.FromRgb(60, 92, 85));
    }

    private async Task RequestModeAsync(string mode)
    {
        await ReleaseAsync(false, padOnly: true);
        await SendCommandAsync(new Command { Name = "RequestMode", Mode = mode });
    }

    private async void Physical_Click(object sender, RoutedEventArgs e) => await RequestModeAsync("Physical");
    private async void Desktop_Click(object sender, RoutedEventArgs e) => await RequestModeAsync("Desktop");
    private async void Release_Click(object sender, RoutedEventArgs e) => await ReleaseAsync();

    private void ClearLocalInputs()
    {
        _inputGeneration++;
        _armed = false;
        _dragging = false;
        _heldActions = 0;
        _yawDelta = _pitchDelta = 0;
        if (Mouse.Captured == InteractionPad) Mouse.Capture(null);
        RenderPad();
    }

    private async Task ReleaseAsync(bool showReason = true, bool padOnly = false)
    {
        ClearLocalInputs();
        var generation = _inputGeneration;
        // Safety releases use their own pipe; a pending status poll or ordinary
        // mode transaction cannot hold this request behind the UI command gate.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var reply = await _broker.SendAsync(new Command { Name = padOnly ? "DisarmViewer" : "ReleaseInputs", Window = _window.ToInt64() }, timeout.Token);
            if (reply.Status is null) throw new IOException("Broker reply omitted current status");
            if (generation == _inputGeneration && !_armed)
            {
                RenderStatus(reply.Status);
                if (showReason) ShowReplyMessage(reply.Reason);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            if (generation == _inputGeneration && !_armed) RenderUnavailable(ex.Message);
        }
    }

    private async void Pad_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_arming || _closing || !_connected || !_status.DriverAlive || _status.Mode != "Desktop") return;
        var firstClick = !_armed;
        InteractionPad.Focus();
        if (firstClick)
        {
            var generation = _inputGeneration;
            _arming = true;
            var reply = await SendCommandAsync(new Command { Name = "ArmViewer", Window = _window.ToInt64(), Armed = true }, requiredInputGeneration: generation);
            _arming = false;
            if (reply?.Accepted != true) return;
            if (generation != _inputGeneration || !IsActive || _closing || !InteractionPad.IsKeyboardFocused)
            {
                // A release may have reached the broker before this late arm.
                // Neutralize the accepted arm again rather than only ignoring it.
                await ReleaseAsync(false, padOnly: true);
                return;
            }
            _armed = reply.Status.Armed && reply.Status.InputOwner == "Pad" && reply.Status.OwnerWindow == _window.ToInt64();
            RenderPad();
        }
        if (!_armed) return;
        if (e.ChangedButton == MouseButton.Right && Mouse.RightButton == MouseButtonState.Pressed)
        {
            _lastPoint = e.GetPosition(InteractionPad);
            _dragging = true;
            InteractionPad.CaptureMouse();
        }
        else if (!firstClick && e.ChangedButton == MouseButton.Left && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _heldActions |= 1;
            InteractionPad.CaptureMouse();
        }
        else if (!firstClick && e.ChangedButton == MouseButton.Middle && Mouse.MiddleButton == MouseButtonState.Pressed)
        {
            _heldActions |= 2;
            InteractionPad.CaptureMouse();
        }
    }

    private void Pad_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) _heldActions &= ~1u;
        if (e.ChangedButton == MouseButton.Middle) _heldActions &= ~2u;
        if (e.ChangedButton == MouseButton.Right) _dragging = false;
        if (!_dragging && (_heldActions & 3) == 0 && Mouse.Captured == InteractionPad) Mouse.Capture(null);
        e.Handled = true;
    }

    private void Pad_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_armed || !_dragging || e.RightButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(InteractionPad);
        var dx = point.X - _lastPoint.X;
        var dy = point.Y - _lastPoint.Y;
        _lastPoint = point;
        var sensitivity = 0.003 * _mouseSensitivity;
        if (HandRadio.IsChecked == true)
        {
            _handYaw = Math.Clamp(_handYaw + dx * sensitivity, -Math.PI, Math.PI);
            _handPitch = Math.Clamp(_handPitch - dy * sensitivity, -1.4, 1.4);
        }
        else
        {
            _yawDelta += dx * sensitivity;
            _pitchDelta -= dy * sensitivity;
        }
    }

    private void Pad_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragging = false;
        _heldActions &= ~3u;
    }

    private async Task SendInputAsync()
    {
        if (!_armed || _closing || _inputSending) return;
        if (!IsActive || !InteractionPad.IsKeyboardFocused)
        {
            await ReleaseAsync(false, padOnly: true);
            return;
        }
        if (!await _brokerGate.WaitAsync(0)) return;
        _inputSending = true;
        var generation = _inputGeneration;
        var yaw = _yawDelta;
        var pitch = _pitchDelta;
        _yawDelta = _pitchDelta = 0;
        var actions = _heldActions;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var reply = await _broker.SendAsync(new Command
            {
                Name = "UpdateInput", Window = _window.ToInt64(), Armed = true,
                Yaw = yaw, Pitch = pitch, HandYaw = _handYaw, HandPitch = _handPitch, Actions = actions
            }, timeout.Token);
            if (reply.Status is null) throw new IOException("Broker reply omitted current status");
            if (generation == _inputGeneration)
            {
                RenderStatus(reply.Status);
                if (!reply.Accepted) { ClearLocalInputs(); MessageText.Text = reply.Reason; }
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException) { RenderUnavailable(ex.Message); }
        finally { _inputSending = false; _brokerGate.Release(); }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // Direct game input is managed by the broker's foreground check. Merely
        // leaving this settings panel must not race acquisition in the game.
        if (!_closing && (_armed || _arming)) _ = ReleaseAsync(false, padOnly: true);
    }

    private async void AdvancedPad_Collapsed(object sender, RoutedEventArgs e)
    {
        if (_armed || _arming) await ReleaseAsync(false, padOnly: true);
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _ = ReleaseAsync(); e.Handled = true; }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_armed && !IsWithinPad(e.OriginalSource as DependencyObject) && !MenuButton.IsMouseOver) _ = ReleaseAsync(false, padOnly: true);
    }

    private void Window_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_armed && !IsWithinPad(e.NewFocus as DependencyObject)) _ = ReleaseAsync(false, padOnly: true);
    }

    private bool IsWithinPad(DependencyObject? item)
    {
        while (item != null)
        {
            if (item == InteractionPad) return true;
            item = item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return false;
    }

    private async void ControlMode_Changed(object sender, RoutedEventArgs e)
    {
        if (InteractionPad == null) return;
        if (_armed) await ReleaseAsync(false, padOnly: true);
        RenderPad();
    }

    private async Task SetPresetAsync(int preset)
    {
        await ReleaseAsync(false, padOnly: true);
        _handYaw = _handPitch = 0;
        await SendCommandAsync(new Command { Name = "SetHandPreset", Preset = preset });
    }
    private async void Neutral_Click(object sender, RoutedEventArgs e) => await SetPresetAsync(0);
    private async void Extended_Click(object sender, RoutedEventArgs e) => await SetPresetAsync(1);
    private async void MenuPose_Click(object sender, RoutedEventArgs e)
    {
        await SetPresetAsync(2);
        HandRadio.IsChecked = true;
    }

    private async void Menu_Click(object sender, RoutedEventArgs e)
    {
        await SendCommandAsync(new Command { Name = "ToggleMenu" });
    }

    private void Height_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HeightText == null) return;
        HeightText.Text = $"{e.NewValue:+0.00;-0.00;0.00} m";
        if (_updatingHeight || !_connected || !_status.DriverAlive || _status.Mode != "Desktop") return;
        _heightTimer.Stop();
        _heightTimer.Start();
    }

    private async void HeightReset_Click(object sender, RoutedEventArgs e)
    {
        _heightTimer.Stop();
        _updatingHeight = true;
        HeightSlider.Value = 0;
        _updatingHeight = false;
        await SendCommandAsync(new Command { Name = "SetDesktopHeight", Height = 0 });
    }

    private void Sensitivity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _mouseSensitivity = e.NewValue;
        if (!_updatingGame && _connected) _gameDraftDirty = true;
        if (SensitivityText != null) SensitivityText.Text = $"{e.NewValue:0.00}×";
    }

    private async void GameInputApply_Click(object sender, RoutedEventArgs e)
    {
        await ReleaseAsync(false, padOnly: true);
        var reply = await SendCommandAsync(new Command
        {
            Name = "ConfigureGameInput", Enabled = GameInputCheck.IsChecked == true,
            Sensitivity = SensitivitySlider.Value
        });
        if (reply?.Accepted == true)
        {
            _gameDraftDirty = false;
            RenderStatus(reply.Status);
        }
    }

    private async void AutomaticApply_Click(object sender, RoutedEventArgs e)
    {
        await ReleaseAsync(false, padOnly: true);
        var reply = await SendCommandAsync(new Command { Name = "ConfigureAutomatic", Enabled = AutomaticCheck.IsChecked == true });
        if (reply?.Accepted == true)
        {
            _automaticDraftDirty = false;
            RenderStatus(reply.Status);
        }
    }

    private async void OscApply_Click(object sender, RoutedEventArgs e)
    {
        await ReleaseAsync(false, padOnly: true);
        var enabled = OscCheck.IsChecked == true;
        if (enabled && OscHost.Text.Trim() != "127.0.0.1")
        {
            MessageText.Text = "This OSC adapter uses the numeric loopback destination 127.0.0.1.";
            return;
        }
        var port = 9000;
        if (enabled && (!int.TryParse(OscPort.Text, out port) || port < 1 || port > 65535))
        {
            MessageText.Text = "Enter an OSC port between 1 and 65535.";
            return;
        }
        var reply = await SendCommandAsync(new Command { Name = "ConfigureOsc", Osc = enabled, Destination = "127.0.0.1", Port = port });
        if (reply?.Accepted == true) { _oscDraftDirty = false; RenderStatus(reply.Status); }
    }

    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        await ReleaseAsync(false, padOnly: true);
        try
        {
            var directory = Identity.ReportsDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"panel-status-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json");
            var report = new
            {
                timestampUtc = DateTime.UtcNow,
                brokerResponding = _connected,
                status = _connected ? _status : null,
                uiVersion = "0.2.1",
                hardwareTests = "Not performed by this panel; consult the evidence reports.",
                shortcuts = new { toggleRegistered = _toggleHotkey, releaseRegistered = _releaseHotkey }
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            MessageText.Text = "Saved a diagnostic report. It contains no typed input or session identifiers.";
        }
        catch (Exception ex) { MessageText.Text = "Could not save diagnostic report: " + ex.Message; }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Preview windows are never resident and must never contact the broker
        // when Application.Shutdown closes the offscreen fixture collection.
        if (_allowClose || !_residentInitialized) return;
        e.Cancel = true;
        if (_closing) return;
        await HidePanelAsync();
    }

    private async Task ExitPanelAsync()
    {
        if (_closing) return;
        _closing = true;
        _statusTimer.Stop();
        _inputTimer.Stop();
        _heightTimer.Stop();
        StopBackgroundUpdates();
        await ReleaseAsync(false, padOnly: true);
        if (_toggleHotkey) UnregisterHotKey(_window, ToggleHotkeyId);
        if (_releaseHotkey) UnregisterHotKey(_window, ReleaseHotkeyId);
        _windowSource?.RemoveHook(WindowMessage);
        _tray?.Dispose(); _tray = null;
        _startedBrokerProcess?.Dispose(); _startedBrokerProcess = null;
        _allowClose = true;
        SessionExiting?.Invoke();
        Application.Current.Shutdown();
    }
    private void StopBackgroundUpdates() { _updateTimer.Stop(); _updateCancellation.Cancel(); }
    internal bool HeightUpdateLifetimeFixture()
    {
        if (!_preview) throw new InvalidOperationException("Offscreen fixture only");
        // Exercise the actual connected height event, then stop its debounce
        // before the Dispatcher can issue a command. No broker is initialized.
        _connected = true;
        HeightSlider.Value = .15;
        bool unaffected = !_updateCancellation.IsCancellationRequested;
        _heightTimer.Stop();
        StopBackgroundUpdates();
        return unaffected && _updateCancellation.IsCancellationRequested;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
