using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Switcheroonie.UI;

public partial class MainWindow : Window
{
    private readonly SettingsWindow _controller;
    private readonly MascotView _mascot;
    private readonly ModeSceneView _scene;
    private readonly ModeCommitGate _commits = new();
    private readonly bool _preview;
    private bool _allowClose, _commandPending;
    private static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(248, 195, 80));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(170, 171, 178));
    private static readonly Brush Dark = new SolidColorBrush(Color.FromRgb(29, 30, 34));
    internal SettingsWindow Controller => _controller;

    public MainWindow() : this(false) { }
    internal MainWindow(bool preview)
    {
        _preview = preview;
        InitializeComponent();
        _mascot = new MascotView(preview);
        MascotHost.Children.Add(_mascot);
        _scene = new ModeSceneView(preview); ModeSceneHost.Children.Add(_scene);
        _controller = new SettingsWindow(preview);
        _controller.OpenCompactPanel = ShowPanel;
        _controller.SessionExiting = () => { _allowClose = true; };
        _controller.StatusChanged += RenderStatus;
        _controller.ConnectionLost += RenderUnavailable;
        _controller.MessageChanged += text => { PreferenceText.ToolTip = text; };
    }
    internal Task InitializeResidentAsync() => _controller.InitializeResidentAsync();
    internal void ShowPanel()
    {
        if (_preview) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate(); _controller.SetCompactVisible(true); _mascot.SetAnimationActive(true); _scene.SetAnimationActive(true);
    }
    private void RenderStatus(HarnessStatus status)
    {
        bool current = status.DriverAlive && status.RoutingReady && (status.NativeCapabilityFlags & 1) != 0;
        bool physical = current && status.Mode == "Physical", desktop = current && status.Mode == "Desktop";
        bool celebrate = _commits.Observe(status);
        _scene.SetMode(physical ? "Physical" : desktop ? "Desktop" : null, celebrate);
        VrButton.Background = physical ? Gold : Brushes.Transparent;
        VrButton.Foreground = physical ? Dark : Muted;
        DesktopButton.Background = desktop ? Gold : Brushes.Transparent;
        DesktopButton.Foreground = desktop ? Dark : Muted;
        VrButton.IsEnabled = DesktopButton.IsEnabled = !_commandPending;
        ModeTitle.Text = status.State == "Preparing" ? "Switching controls…" : desktop ? "Desktop mode active" : physical ? "VR mode active" : "Waiting for VR route";
        ModeSubtitle.Text = status.State == "Preparing" ? "Waiting for the driver" : desktop
            ? status.MenuNavigation ? "Menu pointer · click or drag to use" : status.GameInputActive ? "Mouse look · WASD to move" : "Click VRChat to play"
            : physical ? "Headset and controller input" : status.ManualMode is "Desktop" or "Physical" ? "Your mode choice is saved" : "Service running · tracking not ready";
        ModeSubtitle.ToolTip = status.Detail;
        AutomationProperties.SetItemStatus(VrButton, physical ? "Active" : "Inactive");
        AutomationProperties.SetItemStatus(DesktopButton, desktop ? "Active" : "Inactive");
        ModeSceneHost.ToolTip = current ? (physical ? "VR controls" : "Desktop controls") : "Waiting for a VR route";
        SessionCaption.Text = status.State == "Preparing" ? "Applying selection" : current ? "Connected" : "Service running";
        RuntimeValue.Text = status.DriverAlive ? "Connected" : "Waiting for driver";
        bool freshHead = status.DriverAlive && status.HasHead && double.IsFinite(status.HeadAgeMilliseconds) && status.HeadAgeMilliseconds is >= 0 and < 200;
        HeadsetValue.Text = freshHead ? status.HeadsetWornKnown ? status.HeadsetWorn ? "Tracking · worn" : "Tracking · removed" : "Tracking" : "Waiting for tracking";
        bool controllers = status.DriverAlive && status.HasLeft && status.HasRight;
        bool controlsMapped = controllers && (status.NativeInputCoverage & 0x17) == 0x17;
        ControllerValue.Text = controlsMapped ? "Input ready" : controllers ? "Mapping incomplete" : "Waiting for controllers";
        RuntimeDot.Fill = status.DriverAlive ? Gold : Muted;
        HeadsetDot.Fill = freshHead ? Gold : Muted;
        ControllerDot.Fill = controlsMapped ? Gold : Muted;
        PreferenceText.Text = status.ManualMode is "Physical" or "Desktop"
            ? "Manual " + (status.ManualMode == "Physical" ? "VR" : "Desktop") + (status.Mode != status.ManualMode || !current ? " · waiting" : "")
            : status.AutomaticEnabled ? "Automatic switching" : "Automatic paused";
        PreferenceText.ToolTip = status.AdaptationDetail;
        ShortcutText.Text = _controller.HotkeySummary.StartsWith("Shortcut conflict", StringComparison.Ordinal)
            ? "Shortcut conflict · Settings" : "Ctrl + Alt + F10 switch  ·  F12 release";
        ShortcutText.ToolTip = _controller.HotkeySummary;
        if (_preview)
        {
            SessionCaption.Text = "Illustrative status";
            ShortcutText.Text = "UI PREVIEW · illustrative";
        }
        if (celebrate) _mascot.Celebrate();
    }
    private void RenderUnavailable(string reason)
    {
        VrButton.IsEnabled = DesktopButton.IsEnabled = false;
        VrButton.Background = DesktopButton.Background = Brushes.Transparent;
        VrButton.Foreground = DesktopButton.Foreground = Muted;
        ModeTitle.Text = "Reconnecting…"; ModeSubtitle.Text = "Background service is being checked";
        SessionCaption.Text = "Status unavailable";
        RuntimeValue.Text = HeadsetValue.Text = ControllerValue.Text = "Waiting for service";
        RuntimeDot.Fill = HeadsetDot.Fill = ControllerDot.Fill = Muted;
        PreferenceText.Text = "Retrying automatically"; PreferenceText.ToolTip = reason;
        _scene.SetMode(null, false);
    }
    private async Task SwitchAsync(string mode)
    {
        if (_preview || _commandPending || !_controller.Connected) return;
        _commandPending = true; VrButton.IsEnabled = DesktopButton.IsEnabled = false;
        try { await _controller.RequestModeFromFrontAsync(mode); }
        finally
        {
            _commandPending = false;
            if (_controller.Connected) RenderStatus(_controller.CurrentStatus);
        }
    }
    private async void Vr_Click(object sender, RoutedEventArgs e) => await SwitchAsync("Physical");
    private async void Desktop_Click(object sender, RoutedEventArgs e) => await SwitchAsync("Desktop");
    private async void Release_Click(object sender, RoutedEventArgs e) { if (!_preview) await _controller.ReleaseFromFrontAsync(); }
    private void Settings_Click(object sender, RoutedEventArgs e) { if (!_preview) _controller.ShowSettings(this); }
    private void FreeFbt_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (_preview || e.Uri.AbsoluteUri != "https://freefbt.com/") return;
        try { Process.Start(new ProcessStartInfo("https://freefbt.com") { UseShellExecute = true }); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        { PreferenceText.ToolTip = "Could not open the link."; }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Title_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_preview || e.OriginalSource is DependencyObject source && IsButton(source)) return;
        if (e.ClickCount == 2) return;
        DragMove();
    }
    private static bool IsButton(DependencyObject item)
    {
        while (item is not null)
        {
            if (item is System.Windows.Controls.Button) return true;
            item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return false;
    }
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool active = IsVisible && WindowState != WindowState.Minimized;
        _controller?.SetCompactVisible(active); _mascot?.SetAnimationActive(active); _scene?.SetAnimationActive(active);
    }
    private void Window_Loaded(object sender, RoutedEventArgs e) => _mascot.SetDpi(VisualTreeHelper.GetDpi(this).PixelsPerInchX);
    private void Window_DpiChanged(object sender, DpiChangedEventArgs e) => _mascot.SetDpi(e.NewDpi.PixelsPerInchX);
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_preview && e.Key == Key.Escape) { _ = _controller.ReleaseFromFrontAsync(); e.Handled = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_preview || _allowClose) return;
        e.Cancel = true;
        if (_controller.IsVisible) _controller.Close();
        Hide(); _controller.SetCompactVisible(false); _mascot.SetAnimationActive(false); _scene.SetAnimationActive(false);
    }
    internal void SetPreviewStatus(bool waiting = false, bool physical = false) => _controller.SetPreviewStatus(waiting, physical);
    internal void SetPreviewDpi(double dpi) => _mascot.SetDpi(dpi);
    internal void SetPreviewFrame(Celebration celebration, int frame) => _mascot.SetFixtureFrame(celebration, frame);
    internal void SetPreviewTransition(bool toDesktop, int frame) => _scene.SetFixtureTransition(toDesktop, frame);
}
