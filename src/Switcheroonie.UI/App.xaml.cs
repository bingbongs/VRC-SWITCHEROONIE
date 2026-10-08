using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Switcheroonie.UI;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private EventWaitHandle? showRequest;
    private DispatcherTimer? showTimer;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--render-preview")
        {
            var outputDirectory = e.Args.Length > 1 ? Path.GetFullPath(e.Args[1]) : Path.Combine(AppContext.BaseDirectory, "preview");
            Directory.CreateDirectory(outputDirectory);
            PreviewRenderer.Render(Path.Combine(outputDirectory, "ui-preview.png"), 1030, 860, 96);
            PreviewRenderer.Render(Path.Combine(outputDirectory, "ui-preview-compact.png"), 760, 700, 144);
            PreviewRenderer.Render(Path.Combine(outputDirectory, "ui-preview-lower.png"), 760, 700, 144, scrollBottom: true);
            PreviewRenderer.Render(Path.Combine(outputDirectory, "ui-preview-advanced.png"), 1030, 1300, 96, advancedPad: true);
            PreviewRenderer.Render(Path.Combine(outputDirectory, "ui-preview-waiting.png"), 1030, 860, 96, offline: true);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--self-test-lifetime"))
        {
            try { BrokerRestartPolicy.SelfTest(); StartupRegistration.SelfTest(); Shutdown(0); }
            catch { Shutdown(1); }
            return;
        }
        bool background = e.Args.Contains("--background");
        string instanceName = "Local\\VRC-SWITCHEROONIE-Panel-" + Identity.Sid;
        instanceMutex = new Mutex(true, instanceName, out bool ownsInstance);
        if (!ownsInstance)
        {
            if (!background)
            {
                try { using var request = EventWaitHandle.OpenExisting(instanceName + "-Show"); request.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
            }
            instanceMutex.Dispose(); instanceMutex = null;
            Shutdown(); return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "-Show");
        var panel = new MainWindow();
        MainWindow = panel;
        showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        showTimer.Tick += (_, _) => { if (showRequest?.WaitOne(0) == true) panel.ShowPanel(); };
        showTimer.Start();
        _ = panel.InitializeResidentAsync();
        if (!background) panel.ShowPanel();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        showTimer?.Stop(); showRequest?.Dispose();
        if (instanceMutex is not null) { instanceMutex.ReleaseMutex(); instanceMutex.Dispose(); }
        base.OnExit(e);
    }
}
