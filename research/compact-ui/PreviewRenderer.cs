using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Switcheroonie.UI;

internal static class PreviewRenderer
{
    // No Show/Loaded, broker, updater, process launch, startup reads, hotkeys or
    // input capture. Fixture values are labelled in the pixels.
    internal static void RenderAll(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            RenderPanel(Path.Combine(directory, $"compact-{dpi}dpi.png"), 540, 400, dpi, false, false, checks);
            RenderPanel(Path.Combine(directory, $"compact-min-{dpi}dpi.png"), 470, 360, dpi, false, false, checks);
            RenderSettings(Path.Combine(directory, $"settings-{dpi}dpi.png"), 500, 580, dpi, "settings", checks);
            RenderPanel(Path.Combine(directory, $"setup-{dpi}dpi.png"), 470, 360, dpi, true, false, checks, setup: true);
            RenderSettings(Path.Combine(directory, $"setup-settings-{dpi}dpi.png"), 500, 580, dpi, "setup", checks);
            RenderSettings(Path.Combine(directory, $"setup-update-{dpi}dpi.png"), 500, 580, dpi, "setup-update", checks);
        }
        RenderPanel(Path.Combine(directory, "main-vr.png"), 540, 400, 96, false, true, checks);
        RenderPanel(Path.Combine(directory, "main-desktop.png"), 540, 400, 96, false, false, checks);
        RenderPanel(Path.Combine(directory, "compact-waiting.png"), 540, 400, 96, true, false, checks);
        foreach (var celebration in Enum.GetValues<Celebration>())
            RenderPanel(Path.Combine(directory, $"compact-{celebration}.png"), 540, 400, 96, false, false, checks, celebration);
        RenderPanel(Path.Combine(directory, "mode-transition.png"), 540, 400, 96, false, false, checks, transition: true);
        RenderSettings(Path.Combine(directory, "settings.png"), 500, 580, 96, "settings", checks);
        RenderSettings(Path.Combine(directory, "help.png"), 500, 580, 96, "help", checks);
        RenderSettings(Path.Combine(directory, "spin.png"), 500, 580, 96, "spin", checks);
        RenderSettings(Path.Combine(directory, "settings-controls.png"), 500, 580, 192, "controls", checks);
        if (AnimationClock.SubscriberCount != 0 || AnimationClock.IsRunning)
            throw new InvalidOperationException("Offscreen previews must never start graphics timers");
        File.WriteAllLines(Path.Combine(directory, "layout-checks.txt"), checks);
    }
    private static void RenderPanel(string path, double width, double height, double dpi, bool waiting, bool physical,
        List<string> checks, Celebration? frame = null, bool transition = false, bool setup = false)
    {
        var window = new MainWindow(preview: true);
        window.SetPreviewStatus(waiting, physical); window.SetPreviewDpi(dpi);
        window.SetPreviewSetup(setup);
        if (frame is not null) window.SetPreviewFrame(frame.Value, frame == Celebration.SelfMunch ? 18 : 14);
        if (transition) window.SetPreviewTransition(true, 19);
        var content = (FrameworkElement)window.Content; window.Content = null;
        Layout(content, width, height);
        foreach (string name in new[] { "VrButton", "DesktopButton", "ModeSceneHost", "MascotHost", "RuntimeValue", "HeadsetValue", "ControllerValue", "ReleaseButton", "SettingsButton", "FooterText", "ShortcutText" })
            RequireContained(window, name, content, width, height, path);
        if (setup) RequireContained(window, "SetupButton", content, width, height, path);
        if (((FrameworkElement)window.FindName("SetupButton")).Visibility != (setup ? Visibility.Visible : Visibility.Collapsed) ||
            ((FrameworkElement)window.FindName("PreferenceText")).Visibility != (setup ? Visibility.Collapsed : Visibility.Visible))
            throw new InvalidOperationException("Setup action must appear only when setup is needed and reserve its own layout space");
        var graphics = Bounds(window, "ModeSceneHost", content);
        var mascot = Bounds(window, "MascotHost", content);
        var controllers = Bounds(window, "ControllerValue", content);
        var release = Bounds(window, "ReleaseButton", content);
        if (Overlaps(graphics, mascot) || Overlaps(controllers, release))
            throw new InvalidOperationException($"Critical compact content overlaps: graphics {graphics}; mascot {mascot}; controllers {controllers}; release {release}");
        if (Math.Abs(mascot.Left + mascot.Width / 2 - width / 2) > 1)
            throw new InvalidOperationException("Mascot must be centered in the main scene");
        if (((FrameworkElement)window.FindName("ModeTitle")).IsVisible || ((FrameworkElement)window.FindName("ModeSubtitle")).IsVisible)
            throw new InvalidOperationException("Legacy plain mode headline and subtitle must remain collapsed");
        string active = AutomationProperties.GetItemStatus((Button)window.FindName(physical ? "VrButton" : "DesktopButton"));
        if (active != (waiting ? "Inactive" : "Active")) throw new InvalidOperationException("Mode selection must reflect only fixture committed status");
        if (!((TextBlock)window.FindName("FooterText")).Inlines.OfType<Hyperlink>().Any(x => x.NavigateUri.AbsoluteUri == "https://freefbt.com/"))
            throw new InvalidOperationException("The footer must retain its explicit freefbt link");
        Save(content, path, width, height, dpi);
        checks.Add($"PASS {Path.GetFileName(path)}: centered mascot, graphics and controls contained; committed selector; collapsed legacy copy; labelled fixture.");
    }
    private static Rect Bounds(Window window, string name, Visual content)
    {
        var element = (FrameworkElement)window.FindName(name);
        return element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
    }
    private static bool Overlaps(Rect a, Rect b) => Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > .1 &&
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > .1;
    private static void RequireContained(Window window, string name, Visual content, double width, double height, string path)
    {
        var bounds = Bounds(window, name, content);
        if (bounds.Left < -.1 || bounds.Top < -.1 || bounds.Right > width + .1 || bounds.Bottom > height + .1 || bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException($"{Path.GetFileName(path)}: {name} outside panel: {bounds}");
    }
    private static void RenderSettings(string path, double width, double height, double dpi, string page, List<string> checks)
    {
        var window = new SettingsWindow(preview: true); window.SetPreviewStatus(spin: page == "spin"); window.SetPreviewSpinDpi(dpi);
        window.SetPreviewSetup(page == "setup", page == "setup-update");
        var help = (Expander)window.FindName("HelpExpander");
        var credits = (Expander)window.FindName("CreditsExpander");
        var optionalPad = (Expander)window.FindName("AdvancedPadExpander");
        help.IsExpanded = page == "help"; credits.IsExpanded = page == "spin"; optionalPad.IsExpanded = page == "controls";
        window.SetPreviewSpinFrame(page == "spin" ? 5 : 0);
        var content = (FrameworkElement)window.Content; window.Content = null;
        var canvas = new Border { Background = new SolidColorBrush(Color.FromRgb(23, 24, 27)), Child = content };
        Layout(canvas, width, height);
        var scroll = (ScrollViewer)window.FindName("MainScroll");
        if (page is "help" or "controls")
        {
            var anchor = page == "help" ? help : optionalPad;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + anchor.TranslatePoint(new Point(0, 0), scroll).Y - 16); canvas.UpdateLayout();
        }
        if (page == "spin") { window.ScrollPreviewToBottom(); canvas.UpdateLayout(); }
        if (page == "controls")
        {
            var legacyPreset = (Button)window.FindName("ExtendedButton");
            if (legacyPreset.Visibility != Visibility.Collapsed || legacyPreset.RenderSize.Width > 0 || legacyPreset.RenderSize.Height > 0)
                throw new InvalidOperationException("Legacy duplicate hand preset must take no layout space");
            foreach (string name in new[] { "InteractionPad", "NeutralButton", "MenuPoseButton", "MenuButton" })
                RequireContained(window, name, canvas, width, height, path);
        }
        if (page == "spin")
            foreach (string name in new[] { "CreditsCard", "SpinCard", "SpinCheck", "SpinIconHost" }) RequireContained(window, name, canvas, width, height, path);
        if (page == "help") RequireContained(window, "HelpExpander", canvas, width, height, path);
        if (page == "setup")
            foreach (string name in new[] { "SetupCard", "RunSetupButton", "CheckSetupButton", "SetupStatusText" }) RequireContained(window, name, canvas, width, height, path);
        if (page == "setup-update")
        {
            foreach (string name in new[] { "SetupCard", "SetupUpdateButton", "CheckSetupButton", "SetupStatusText" }) RequireContained(window, name, canvas, width, height, path);
            if (((Button)window.FindName("RunSetupButton")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Owned driver update state must not expose install or enable");
        }
        Save(canvas, path, width, height, dpi);
        checks.Add($"PASS {Path.GetFileName(path)}: single settings window; {page} fixture, no resident, update or input side effects.");
    }
    private static void Layout(FrameworkElement content, double width, double height)
    {
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
    }
    private static void Save(Visual content, string path, double width, double height, double dpi)
    {
        var scale = dpi / 96;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
