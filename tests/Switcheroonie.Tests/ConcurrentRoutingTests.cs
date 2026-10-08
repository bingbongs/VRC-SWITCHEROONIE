using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Switcheroonie;
using Switcheroonie.Broker;

// Private IPC maps and a null input platform exercise the actual broker without
// opening the runtime, observing physical controls, or changing user settings.
static class ConcurrentRoutingTests
{
    const ulong InitialEpoch = 7000;
    sealed record Source(bool FollowRequest = false, bool WearKnown = false, bool Worn = false,
        double HeadAge = 2, bool HasHead = true);
    readonly record struct Request(ulong Epoch, bool Desktop, bool Armed, double Height,
        double Yaw, double Pitch, double HandYaw, double HandPitch, uint Actions);

    sealed class Fixture : IAsyncDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), "Switcheroonie-concurrency-" + Guid.NewGuid().ToString("N"));
        readonly MemoryMappedFile map;
        readonly MemoryMappedViewAccessor view;
        readonly CancellationTokenSource stop = new();
        readonly Task pump;
        readonly bool initialDesktop;
        Source source = new();
        long sequence;
        public SharedChannel Channel { get; }
        public HarnessEngine Engine { get; }

        public Fixture(bool desktop = false, PoseIntent? retained = null)
        {
            initialDesktop = desktop;
            string name = "Local\\VRC-SWITCHEROONIE-Concurrency-" + Guid.NewGuid().ToString("N");
            Channel = new SharedChannel(name);
            map = MemoryMappedFile.OpenExisting(name);
            view = map.CreateViewAccessor();
            var pose = retained ?? new PoseIntent(0, 0, 0, 0, 0);
            // Deliberately seed a previously armed producer: startup may keep its
            // pose offsets, but it must never restore its actions or ownership.
            Channel.Publish(InitialEpoch, desktop, retained.HasValue, pose.Height, pose.Yaw,
                pose.Pitch, pose.HandYaw, pose.HandPitch, 2, retained.HasValue ? 7u : 0, 1, -1);
            PublishStatus(new Request(InitialEpoch, desktop, false, 0, 0, 0, 0, 0, 0), source);
            Engine = new HarnessEngine(Channel, configurationDirectory: directory);
            pump = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var current = Volatile.Read(ref source);
                    var request = ReadRequest();
                    var acknowledgement = current.FollowRequest ? request : request with { Epoch = InitialEpoch, Desktop = initialDesktop };
                    PublishStatus(acknowledgement, current);
                    Engine.Tick();
                    await Task.Delay(5);
                }
            });
        }
        public void SetSource(bool followRequest = false, bool wearKnown = false, bool worn = false,
            double headAge = 2, bool hasHead = true) =>
            Volatile.Write(ref source, new(followRequest, wearKnown, worn, headAge, hasHead));
        public Request ReadRequest()
        {
            for (int attempt = 0; attempt < 100; ++attempt)
            {
                long before = view.ReadInt64(0);
                if ((before & 1) != 0) { Thread.Yield(); continue; }
                var result = new Request(view.ReadUInt64(24), view.ReadUInt32(32) == 1, view.ReadUInt32(36) == 1,
                    view.ReadDouble(40), view.ReadDouble(48), view.ReadDouble(56), view.ReadDouble(64),
                    view.ReadDouble(72), view.ReadUInt32(84));
                if (view.ReadInt64(0) == before) return result;
            }
            throw new InvalidOperationException("Fixture could not read a coherent broker request.");
        }
        void PublishStatus(Request request, Source current)
        {
            view.Write(2048, ++sequence);
            view.Write(2056, (1L << 32) | 0x53575243u);
            view.Write(2064, Stopwatch.GetTimestamp()); view.Write(2072, request.Epoch);
            view.Write(2080, request.Desktop ? 1L : 0L); view.Write(2088, current.HeadAge);
            view.Write(2096, (1L << 32) | (current.HasHead ? 1L : 0)); view.Write(2104, (3L << 32) | 1L);
            view.Write(2304, (current.Worn ? 1L << 32 : 0) | (current.WearKnown ? 1L : 0));
            view.Write(2312, 2.0); view.Write(2320, 2.0); view.Write(2328, 0.0);
            view.Write(2048, ++sequence);
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); await pump;
            Engine.Dispose(); Channel.Dispose(); view.Dispose(); map.Dispose(); stop.Dispose();
            // This directory contains only this fixture's top-level preference
            // files. Avoid recursive cleanup and leave unexpected entries alone.
            foreach (var file in new[] { "keyboard.json", "direct-input.json", "routing.json", "routing.json.tmp" })
                File.Delete(Path.Combine(directory, file));
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    static async Task Until(Func<bool> condition, string stage, int milliseconds = 3000)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
        if (!condition()) throw new TimeoutException("Concurrent routing fixture did not reach " + stage);
    }
    static Command Mode(string mode) => new() { Name = "RequestMode", Mode = mode };

    public static async Task RunAsync(Action<bool, string> check)
    {
        await using (var fixture = new Fixture())
        {
            var older = fixture.Engine.ExecuteAsync(Mode("Desktop"));
            await Until(() => fixture.Engine.Status().State == "Preparing" && fixture.ReadRequest().Desktop, "blocked manual switch");
            var newer = fixture.Engine.ExecuteAsync(Mode("Physical"));
            check(fixture.Engine.Status().ManualMode == "Physical",
                "Newer manual intent is recorded while an older native transaction holds the gate");
            fixture.SetSource(followRequest: true);
            var replies = await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(3));
            check(!replies[0].Accepted && replies[1].Accepted && fixture.Engine.Status().Mode == "Physical" && !fixture.ReadRequest().Armed,
                "A blocked older manual switch cannot commit over the newer manual preference");
        }

        await using (var fixture = new Fixture())
        {
            var older = fixture.Engine.ExecuteAsync(Mode("Desktop"));
            await Until(() => fixture.Engine.Status().State == "Preparing" && fixture.ReadRequest().Desktop, "manual switch before Auto resumes");
            var resumed = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureAutomatic", Enabled = true });
            var oldReply = await older.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => !fixture.ReadRequest().Desktop, "superseded manual release");
            check(resumed.Accepted && !oldReply.Accepted && fixture.Engine.Status().AutomaticEnabled && fixture.Engine.Status().ManualMode is null && !fixture.ReadRequest().Armed,
                "Resuming Auto cancels an in-flight manual choice and releases it before unknown wear can choose a route");
        }

        await using (var fixture = new Fixture())
        {
            fixture.SetSource(wearKnown: true, worn: false);
            await Until(() => fixture.Engine.Status().State == "Preparing" && fixture.ReadRequest().Desktop, "in-flight automatic removal switch");
            var paused = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureAutomatic", Enabled = false });
            await Until(() => fixture.Engine.Status().State != "Preparing" && !fixture.ReadRequest().Desktop, "automatic cancellation");
            await Task.Delay(60);
            check(paused.Accepted && !fixture.Engine.Status().AutomaticEnabled && fixture.Engine.Status().ManualMode is null &&
                !fixture.ReadRequest().Desktop && !fixture.ReadRequest().Armed,
                "Pausing Auto invalidates a transaction already waiting for native acknowledgement");
        }

        await using (var fixture = new Fixture())
        {
            var command = Mode("Desktop");
            var first = fixture.Engine.ExecuteAsync(command);
            await Until(() => fixture.Engine.Status().State == "Preparing" && fixture.ReadRequest().Desktop, "in-flight idempotent request");
            ulong transactionEpoch = fixture.ReadRequest().Epoch;
            var duplicate = fixture.Engine.ExecuteAsync(command);
            check(!first.IsCompleted && !duplicate.IsCompleted && fixture.ReadRequest().Epoch == transactionEpoch,
                "A repeated in-flight request ID joins the existing native transaction");
            fixture.SetSource(followRequest: true);
            var replies = await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(3));
            check(replies[0].Accepted && replies[0] == replies[1] && fixture.ReadRequest().Epoch == transactionEpoch && transactionEpoch == InitialEpoch + 1,
                "Coalesced request IDs return one acknowledgement without generating another anchor epoch");
        }

        var retained = new PoseIntent(0.4, 0.7, -0.2, 0.6, -0.1);
        await using (var fixture = new Fixture(desktop: true, retained: retained))
        {
            await Task.Delay(30);
            var request = fixture.ReadRequest(); var status = fixture.Engine.Status();
            check(status.Mode == "Desktop" && status.Epoch == InitialEpoch && request.Desktop && request.Epoch == InitialEpoch && !status.HeadsetWornKnown,
                "Broker startup preserves a healthy committed Desktop epoch when the wear sensor is unknown");
            check(request.Height == retained.Height && request.Yaw == retained.Yaw && request.Pitch == retained.Pitch &&
                request.HandYaw == retained.HandYaw && request.HandPitch == retained.HandPitch && !request.Armed && request.Actions == 0 && !status.GameInputActive,
                "Startup adoption preserves valid pose offsets while discarding the previous producer's armed actions");
        }

        await using (var fixture = new Fixture())
        {
            var pending = fixture.Engine.ExecuteAsync(Mode("Desktop"));
            await Until(() => fixture.Engine.Status().State == "Preparing", "transaction before service disposal");
            fixture.Engine.Dispose();
            var stopped = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            check(!stopped.Accepted && stopped.Status.ServiceState == "Stopped" && !fixture.ReadRequest().Desktop && !fixture.ReadRequest().Armed,
                "Disposal neutralizes the native lease and terminates a pending routing transaction");
            // Dispose the actual IPC view first; rejected commands must not
            // access it, persist preferences, or reacquire platform resources.
            fixture.Channel.Dispose();
            var late = await Task.WhenAll(new[]
            {
                new Command { Name = "ReleaseInputs" }, new Command { Name = "ConfigureAutomatic", Enabled = true },
                new Command { Name = "SetDesktopHeight", Height = 0.2 }, Mode("Physical")
            }.Select(fixture.Engine.ExecuteAsync));
            fixture.Engine.Tick();
            check(late.All(reply => !reply.Accepted && reply.Status.ServiceState == "Stopped") && fixture.Engine.Status().ServiceState == "Stopped",
                "Late manual and ordinary commands safely reject after disposal of the broker's IPC view");
        }

        foreach (bool missingHead in new[] { false, true })
        {
            await using var fixture = new Fixture();
            var pending = fixture.Engine.ExecuteAsync(Mode("Desktop"));
            await Until(() => fixture.Engine.Status().State == "Preparing" && fixture.ReadRequest().Desktop, "stale acknowledgement fixture");
            fixture.SetSource(followRequest: true, headAge: missingHead ? 2 : 300, hasHead: !missingHead);
            var reply = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            check(!reply.Accepted && !reply.Status.Armed,
                missingHead ? "An epoch-matching native acknowledgement without a physical HMD cannot commit routing" :
                    "A fresh native heartbeat with an epoch-matching but stale physical head cannot commit routing");
        }
    }
}
