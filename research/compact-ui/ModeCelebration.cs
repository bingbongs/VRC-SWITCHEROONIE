namespace Switcheroonie.UI;

// Broker status remains the sole authority. A pending preference, unchanged
// acknowledgement or reconnection is not a successful mode transition.
internal sealed class ModeCommitGate
{
    private string? _lastCommitted;
    internal bool Observe(HarnessStatus status)
    {
        if (!status.DriverAlive || !status.RoutingReady || (status.NativeCapabilityFlags & 1) == 0 ||
            status.State == "Preparing" || status.Mode is not ("Physical" or "Desktop")) return false;
        if (_lastCommitted is null) { _lastCommitted = status.Mode; return false; }
        if (_lastCommitted == status.Mode) return false;
        _lastCommitted = status.Mode;
        return true;
    }
}

internal enum Celebration { RunBonk, FlipThumb, SelfMunch, RocketSpin }
internal sealed class CelebrationBag
{
    private readonly Random _random;
    private readonly Queue<Celebration> _bag = new();
    private Celebration? _previous;
    internal CelebrationBag(Random? random = null) => _random = random ?? Random.Shared;
    internal Celebration Next()
    {
        if (_bag.Count == 0)
        {
            var values = Enum.GetValues<Celebration>();
            _random.Shuffle(values);
            if (values[0] == _previous) (values[0], values[1]) = (values[1], values[0]);
            foreach (var value in values) _bag.Enqueue(value);
        }
        return (_previous = _bag.Dequeue()).Value;
    }
}

internal static class CompactSelfTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string name) { count++; if (!value) throw new InvalidOperationException(name); }
        HarnessStatus Status(string mode = "Physical", bool ready = true, string state = "Physical", uint capabilities = 1, ulong epoch = 0) =>
            new() { Mode = mode, DriverAlive = ready, RoutingReady = ready, NativeCapabilityFlags = capabilities, State = state, Epoch = epoch };
        var gate = new ModeCommitGate();
        Check(!gate.Observe(Status()), "Initial current mode must not celebrate");
        Check(!gate.Observe(Status()), "Repeated current status must not celebrate");
        Check(!gate.Observe(Status("Desktop", state: "Preparing")), "Pending desktop must not celebrate");
        Check(!gate.Observe(Status("Desktop", ready: false)), "Unavailable route must not celebrate");
        var noHook = Status("Desktop", capabilities: 0);
        Check(!gate.Observe(noHook), "Missing source hook must not celebrate");
        Check(gate.Observe(Status("Desktop", state: "Desktop")), "Confirmed mode transition celebrates");
        Check(!gate.Observe(Status("Desktop", state: "Desktop")), "Confirmed status repetition is silent");
        var newEpoch = Status("Desktop", state: "Desktop", epoch: 80);
        Check(!gate.Observe(newEpoch), "Same mode new epoch is silent");
        Check(!gate.Observe(Status("Unmanaged")), "Unknown mode is silent");
        Check(gate.Observe(Status()), "Reverse confirmed transition celebrates");
        var bag = new CelebrationBag(new Random(5312));
        var values = Enumerable.Range(0, 80).Select(_ => bag.Next()).ToArray();
        Check(values.Zip(values.Skip(1)).All(x => x.First != x.Second), "Adjacent celebrations never repeat");
        Check(Enumerable.Range(0, 20).All(i => values.Skip(i * 4).Take(4).Distinct().Count() == 4), "Every bag covers all celebrations");
        Check(MascotTracks.FramesPerSecond == 24 && MascotTracks.Celebrations.Values.All(x => x.Length == 42), "All celebrations use precomputed 24 fps tracks");
        Check(MascotTracks.Idle.Select(x => (x.Y, x.ScaleY)).Distinct().Count() > 10, "Idle has body bounce and squash motion");
        Check(MascotTracks.Celebrations.Values.All(x => x.Select(f => (f.X, f.Y, f.ScaleX, f.ScaleY, f.Angle)).Distinct().Count() > 12), "Every celebration changes body pose across frames");
        Check(MascotTracks.Celebrations.Values.SelectMany(x => x).All(x => x.Sprite is >= 0 and < 6 && Math.Abs(x.X) <= 24 && Math.Abs(x.Y) <= 20 && x.ScaleX is > 0 and <= 1.2 && x.ScaleY is > 0 and <= 1.2), "Tracks have bounded valid poses");
        Check(SceneTracks.Switch[0].Progress == 0 && SceneTracks.Switch[^1].Progress == 1 &&
            SceneTracks.Switch.Zip(SceneTracks.Switch.Skip(1)).All(x => x.First.Progress <= x.Second.Progress), "Mode graphics crossfade monotonically between exact endpoints");
        Check(SceneTracks.Switch.All(x => x.Progress is >= 0 and <= 1 && x.Scale is >= 1 and < 1.06 && x.Sweep is >= 0 and <= 1), "Mode graphic transforms stay bounded for compact layout");
        Check(SpinStarfishView.Angles.Length == 36 && SpinStarfishView.Angles[0] == 0 && SpinStarfishView.Angles[^1] == 350, "Spin sprite completes a precomputed continuous turn without duplicated endpoint");
        Check(AnimationClock.SubscriberCount == 0 && !AnimationClock.IsRunning, "Detached fixtures have no animation timer subscribers");
        Check(UpdatePresentation.Text(new("Staged", "fixture", "9.9.9", true, true)).Contains("normal shutdown"), "Staged update never claims immediate activation");
        Check(UpdatePresentation.Text(new("Current", "No public signed release is available yet.")) == "No signed update available", "Unavailable release feed does not claim version freshness");
        Check(UpdatePresentation.Text(new("Current", "This portable version is current.")) == "Up to date", "Confirmed update result is concise");
        Check(UpdatePresentation.Text(null) == "Not checked yet" && UpdatePresentation.Text(new("Unavailable", "fixture")).Contains("unavailable"), "Missing and failed update state stay explicit");
        Check(new SettingsWindow(preview: true).HeightUpdateLifetimeFixture(), "Height edits keep update lifetime alive; explicit exit cancels it");
        SpinViewportLifecycleTests.Run(Check);
        SetupServiceTests.Run(Check);
        return count;
    }
}
