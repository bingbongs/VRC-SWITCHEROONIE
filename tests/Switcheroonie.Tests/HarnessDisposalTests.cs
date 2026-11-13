using System.IO.MemoryMappedFiles;
using Switcheroonie.Broker;

static class HarnessDisposalTests
{
    public static void Run(Action<bool, string> check)
    {
        string map = "Local\\VRC-SWITCHEROONIE-Disposal-" + Guid.NewGuid().ToString("N");
        string configuration = Path.Combine(Path.GetTempPath(), "Switcheroonie-disposal-" + Guid.NewGuid().ToString("N"));
        using var channel = new SharedChannel(map);
        var platform = new FailingCapturePlatform();
        using var engine = new HarnessEngine(channel, platform, configuration);
        using var memory = MemoryMappedFile.OpenExisting(map);
        using var view = memory.CreateViewAccessor();
        platform.FailRelease = true;
        bool failedRelease = false;
        try { engine.Dispose(); }
        catch (InvalidOperationException) { failedRelease = true; }
        check(failedRelease, "Disposal fixture exercises an actual input-release failure");
        check(view.ReadUInt32(32) == 0 && view.ReadUInt32(36) == 0 && view.ReadUInt32(84) == 0 &&
              view.ReadDouble(88) == 0 && view.ReadDouble(96) == 0 && view.ReadUInt32(136) == 0,
            "Even a failed cursor release publishes Physical with neutral movement, buttons and spin");
        check(platform.Disposals == 1,
            "Input platform teardown still runs when an earlier release operation fails");
        ulong epoch = view.ReadUInt64(24);
        engine.Dispose();
        check(platform.Disposals == 1 && view.ReadUInt64(24) == epoch,
            "Repeated disposal neither tears down input twice nor republishes another routing epoch");
    }

    sealed class FailingCapturePlatform : IGameInputPlatform
    {
        public bool FailRelease;
        public int Disposals;
        public GameInputSample Read(bool eligible) => new();
        public void SetCapture(bool capture)
        {
            if (FailRelease && !capture) throw new InvalidOperationException("Injected cursor release failure.");
        }
        public void Dispose() => ++Disposals;
    }
}
