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
        int monitorManual = 0, manualIntentLost = 0, desktopRequested = 0, desktopObserved = 0, pausePublication = 0;
        var publicationPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveManual()
        {
            if (Volatile.Read(ref monitorManual) == 0) return;
            var status = engine.Status();
            if (status.ManualMode != "Physical") Interlocked.Exchange(ref manualIntentLost, 1);
            if (view.ReadUInt32(32) != 0) Interlocked.Exchange(ref desktopRequested, 1);
            if (status.DriverAlive && status.Mode == "Desktop") Interlocked.Exchange(ref desktopObserved, 1);
        }
        var pump = Task.Run(async () =>
        {
            long sequence = 0;
            while (!stop.IsCancellationRequested)
            {
                var source = Volatile.Read(ref state);
                if (source.Connected)
                {
                    view.Write(2048, ++sequence);
                    if (Volatile.Read(ref pausePublication) != 0 && !resumePublication.Task.IsCompleted)
                    {
                        publicationPaused.TrySetResult();
                        await resumePublication.Task;
                    }
                    view.Write(2056, (1L << 32) | 0x53575243u);
                    view.Write(2064, Stopwatch.GetTimestamp()); view.Write(2072, view.ReadUInt64(24));
                    view.Write(2080, ((long)source.Error << 32) | (source.Error is 0 or 9 ? view.ReadUInt32(32) : 0));
                    view.Write(2088, 2.0); view.Write(2096, (1L << 32) | 1L); view.Write(2104, 1L);
                    view.Write(2304, (source.Worn ? 1L << 32 : 0) | 1L);
                    view.Write(2312, 3.0); view.Write(2320, 4.0); view.Write(2328, 10000.0);
                    view.Write(2048, ++sequence);
                }
                // Refresh this simulated producer before Tick. A scheduler pause
                // must not fabricate an expired source on the following tick.
                engine.Tick();
                ObserveManual();
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
            ulong manualEpoch = view.ReadUInt64(24);
            Volatile.Write(ref monitorManual, 1);
            await Task.Delay(1150);
            // The real reader deliberately rejects an in-progress publication.
            // Force that window instead of treating one opportunistic status
            // read as proof that the user's route preference was overwritten.
            Volatile.Write(ref pausePublication, 1);
            await publicationPaused.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var unreadable = engine.Status();
            check(!unreadable.DriverAlive && unreadable.Mode == "Unmanaged" && unreadable.ManualMode == "Physical",
                "Odd native status publication reports unavailable routing while retaining committed manual intent");
            engine.Tick();
            ObserveManual();
            check(engine.Status().ManualMode == "Physical" && view.ReadUInt32(32) == 0 && view.ReadUInt32(36) == 0,
                "A tick during unreadable native status preserves manual physical intent and a neutral physical lease");
            resumePublication.TrySetResult();
            check(await Until(() =>
            {
                ObserveManual();
                var status = engine.Status();
                return status.DriverAlive && status.RoutingReady && status.Mode == "Physical" &&
                    status.ManualMode == "Physical" && status.Epoch == manualEpoch;
            }), "Headset removal cannot override the user's committed manual physical preference; fresh same-epoch physical acknowledgement recovers");
            DriverSnapshot telemetry = new();
            bool telemetryReady = await Until(() =>
            {
                ObserveManual();
                var sample = channel.Read();
                if (sample.Alive && sample.Desktop) Interlocked.Exchange(ref desktopObserved, 1);
                if (!sample.Alive || !sample.HasHead || sample.HeadAge is < 0 or >= 200 ||
                    !double.IsFinite(sample.HeadAge) || sample.Error != 0 || sample.Epoch != manualEpoch || sample.Desktop) return false;
                telemetry = sample;
                return true;
            });
            check(telemetryReady && telemetry.ProximityKnown && !telemetry.ProximityActive && telemetry.LeftAge == 3 && telemetry.RightAge == 4 && telemetry.ProximityAge == 10000,
                "Additive independent wear and controller ages decode coherently without equating event age with a heartbeat");
            check(Volatile.Read(ref manualIntentLost) == 0 && Volatile.Read(ref desktopRequested) == 0 && Volatile.Read(ref desktopObserved) == 0,
                "Every monitored tick and telemetry wait preserves manual intent and never requests or coherently observes desktop routing after manual physical commitment");
            check((await engine.ExecuteAsync(new() { Name = "SetDesktopHeight", Height = 0.05 })).Accepted,
                "Desktop height can be saved while physical tracking is selected");
        }
        finally { stop.Cancel(); resumePublication.TrySetResult(); await pump; }
        using var restartChannel = new SharedChannel("Local\\VRC-SWITCHEROONIE-AutoRestart-" + Guid.NewGuid().ToString("N"));
        using var restarted = new HarnessEngine(restartChannel, configurationDirectory: directory);
        check(restarted.Status().ManualMode == "Physical" && restarted.Status().Mode == "Unmanaged",
            "Service restart restores user preference while reporting the absent runtime honestly");
        check(restarted.Status().Height == 0.05, "Headset-free service restart retains the user's desktop height preference");
    }
}
