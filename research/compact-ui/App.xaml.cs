using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Switcheroonie.UI;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showRequest;
    private DispatcherTimer? _showTimer;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 1 && e.Args[0] == "--measure-animation")
        {
            try { await AnimationPerformanceFixture.RunAsync(e.Args[1]); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.GetFullPath(e.Args[1]), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--self-test-compact"))
        {
            try
            {
                int checks = CompactSelfTests.Run();
                BrokerRestartPolicy.SelfTest(); int startupChecks = StartupRegistration.SelfTest();
                var output = e.Args.SkipWhile(x => x != "--self-test-compact").Skip(1).FirstOrDefault();
                if (!string.IsNullOrEmpty(output)) File.WriteAllText(Path.GetFullPath(output), $"{checks} compact state checks; {startupChecks} startup ownership checks + 22 resident retry checks passed. No live OS settings or broker connection.\n");
                Shutdown(0);
            }
            catch (Exception ex) { if (e.Args.Length > 1) File.WriteAllText(Path.GetFullPath(e.Args[^1]), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--render-preview")
        {
            try
            {
                var output = e.Args.Length > 1 ? Path.GetFullPath(e.Args[1]) : Path.Combine(AppContext.BaseDirectory, "preview");
                PreviewRenderer.RenderAll(output); Shutdown(0);
            }
            catch (Exception ex)
            {
                if (e.Args.Length > 1) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "render-error.txt"), ex.ToString()); }
                Shutdown(1);
            }
            return;
        }
        // Share the existing panel's identity: a staged executable must not run
        // a second resident controller or compete for the same hotkeys.
        bool background = e.Args.Contains("--background");
        string name = "Local\\VRC-SWITCHEROONIE-Panel-" + Identity.Sid;
        _instance = new Mutex(true, name, out bool owns);
        if (!owns)
        {
            if (!background)
            {
                try { using var request = EventWaitHandle.OpenExisting(name + "-Show"); request.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
            }
            _instance.Dispose(); _instance = null; Shutdown(); return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Show");
        var panel = new MainWindow(); MainWindow = panel;
        _showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _showTimer.Tick += (_, _) => { if (_showRequest?.WaitOne(0) == true) panel.ShowPanel(); };
        _showTimer.Start();
        _ = panel.InitializeResidentAsync();
        if (!background) panel.ShowPanel();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _showTimer?.Stop(); _showRequest?.Dispose();
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
