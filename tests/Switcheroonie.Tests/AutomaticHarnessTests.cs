using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using Switcheroonie;
using Switcheroonie.Broker;

static class AutomaticHarnessTests
{
    sealed record Source(bool Connected = false, bool Worn = false, uint Error = 0);
    public static async Task RunAsync(Action<bool, string> check)
    {
        string name = "Local\\VRC-SWITCHEROONIE-AutoTest-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "Switcheroonie-auto-tests-" + Guid.NewGuid().ToString("N"));
        using var channel = new SharedChannel(name);
        using var map = MemoryMappedFile.OpenExisting(name);
        using var view = map.CreateViewAccessor();
        using var engine = new HarnessEngine(channel, configurationDirectory: directory);
        var queued = await engine.ExecuteAsync(new() { Name = "RequestMode", Mode = "Desktop" });
        check(!queued.Accepted && queued.Status.Mode == "Unmanaged" && queued.Status.ManualMode == "Desktop",
            "Offline manual choice is saved without claiming a committed routing change");
        engine.Tick();
        check(view.ReadUInt32(32) == 0 && view.ReadUInt32(36) == 0 && engine.Status().ServiceState == "Ready",
            "Headset-free broker stays ready while its native lease remains released and physical");
        var persisted = JsonSerializer.Deserialize<RoutingPreferences>(File.ReadAllText(Path.Combine(directory, "routing.json")));
        check(persisted?.ManualMode == "Desktop", "Offline route preference is persisted for later device readiness");

        Source state = new();
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            long sequence = 0;
            while (!stop.IsCancellationRequested)
            {
                engine.Tick();
                var source = Volatile.Read(ref state);
                if (source.Connected)
                {
                    view.Write(2048, ++sequence);
                    view.Write(2056, (1L << 32) | 0x53575243u);
                    view.Write(2064, Stopwatch.GetTimestamp()); view.Write(2072, view.ReadUInt64(24));
                    view.Write(2080, ((long)source.Error << 32) | (source.Error is 0 or 9 ? view.ReadUInt32(32) : 0));
                    view.Write(2088, 2.0); view.Write(2096, (1L << 32) | 1L); view.Write(2104, 1L);
                    view.Write(2304, (source.Worn ? 1L << 32 : 0) | 1L);
                    view.Write(2312, 3.0); view.Write(2320, 4.0); view.Write(2328, 10000.0);
                    view.Write(2048, ++sequence);
                }
                await Task.Delay(5);
            }
        });
        async Task<bool> Until(Func<bool> condition, int milliseconds = 2000)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < milliseconds)
            { if (condition()) return true; await Task.Delay(10); }
            return condition();
        }
        try
        {
            Volatile.Write(ref state, new Source(Connected: true, Worn: true));
            check(await Until(() => engine.Status().Mode == "Desktop" && !engine.Status().Armed),
                "Queued manual desktop preference commits through a simulated driver after late headset attachment");
            check((await engine.ExecuteAsync(new() { Name = "ConfigureAutomatic", Enabled = true })).Accepted && engine.Status().ManualMode is null,
                "Resuming Auto clears the manual override");
            check(await Until(() => engine.Status().Mode == "Physical"), "Stable vendor wear event commits physical return through the real broker transaction path (simulated driver)");
            check(!engine.Status().Armed && !engine.Status().GameInputActive, "Automatic physical return never arms gameplay input");
            Volatile.Write(ref state, new Source(Connected: true, Worn: false));
            check(await Until(() => engine.Status().Mode == "Desktop"), "Stable vendor removal commits desktop through the broker without a UI producer (simulated driver)");
            check(!engine.Status().Armed, "Automatic desktop entry still requires a fresh user activation click");
            check((await engine.ExecuteAsync(new() { Name = "RequestMode", Mode = "Physical" })).Accepted, "Manual physical selection waits for an actual simulated native acknowledgement");
            await Task.Delay(1150);
            check(engine.Status().Mode == "Physical" && engine.Status().ManualMode == "Physical", "Headset removal cannot override the user's committed manual physical preference");
            var telemetry = channel.Read();
            check(telemetry.ProximityKnown && !telemetry.ProximityActive && telemetry.LeftAge == 3 && telemetry.RightAge == 4 && telemetry.ProximityAge == 10000,
                "Additive independent wear and controller ages decode coherently without equating event age with a heartbeat");
            check((await engine.ExecuteAsync(new() { Name = "SetDesktopHeight", Height = 0.05 })).Accepted,
                "Desktop height can be saved while physical tracking is selected");
        }
        finally { stop.Cancel(); await pump; }
        using var restartChannel = new SharedChannel("Local\\VRC-SWITCHEROONIE-AutoRestart-" + Guid.NewGuid().ToString("N"));
        using var restarted = new HarnessEngine(restartChannel, configurationDirectory: directory);
        check(restarted.Status().ManualMode == "Physical" && restarted.Status().Mode == "Unmanaged",
            "Service restart restores user preference while reporting the absent runtime honestly");
        check(restarted.Status().Height == 0.05, "Headset-free service restart retains the user's desktop height preference");
    }
}
