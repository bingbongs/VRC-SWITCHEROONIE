using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using Switcheroonie;
using Switcheroonie.Broker;

// Actual HarnessEngine plus private shared-map/configuration fixtures. No real
// input platform, game process, cursor, runtime, OSC feed or production map.
internal static class HarnessMenuRecoveryTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (bool emergency in new[] { false, true })
        {
            using var fixture = new Fixture();
            var game = fixture.Game;
            fixture.Tick(game with { ActivateClick = true, LeftDown = true });
            fixture.Tick(game);
            fixture.Tick(game with { RaiseMenu = true });
            // Accepted non-game pulses would continue even with input released
            // unless the full-release path really clears the pending queue.
            // Two more toggles preserve the initially open local menu state.
            for (int i = 0; i < 2; ++i)
                check((await fixture.Command(new() { Name = "ToggleMenu" })).Accepted,
                    "Private menu recovery precondition queues a real broker-owned menu pulse");
            var held = game with { LeftDown = true, RightDown = true, MiddleDown = true, Forward = true, Right = true,
                Jump = true, Run = true, Crouch = true, Prone = true };
            fixture.Tick(held with { DeltaX = 40, DeltaY = -20 });
            string label = emergency ? "Emergency sample" : "Full ReleaseInputs command";
            check(fixture.Engine.Status().MenuNavigation && fixture.Preset == 3 && fixture.Input.Capture && fixture.Input.Pointer &&
                fixture.HandYaw != 0 && fixture.HandPitch != 0 && (fixture.Actions & 4) != 0,
                label + " begins with a real left-menu preset, pointer offsets and active/queued native menu delivery");
            byte[] preferences = File.ReadAllBytes(fixture.InputPreferences);
            if (emergency) fixture.Tick(held with { Emergency = true, ActivateClick = true, EscapeMenu = true, DeltaX = 100 });
            else check((await fixture.Command(new() { Name = "ReleaseInputs" })).Accepted,
                "Full ReleaseInputs command is accepted through the actual broker command handler");
            check(fixture.Neutral && fixture.Preset == 0 && fixture.HandYaw == 0 && fixture.HandPitch == 0 &&
                !fixture.Engine.Status().MenuNavigation && !fixture.Input.Capture && !fixture.Input.Active,
                label + " immediately publishes rest hands, zero pointer offsets and neutral uncaptured gameplay without a menu toggle");
            check(fixture.Engine.Status().Height == .15 && File.ReadAllBytes(fixture.InputPreferences).AsSpan().SequenceEqual(preferences),
                label + " preserves the saved desktop height and exact input preferences");

            var deadline = Stopwatch.StartNew();
            bool neutralThroughout = true, freshThroughout = true;
            while (deadline.ElapsedMilliseconds < 850)
            {
                fixture.Tick(held);
                var status = fixture.Engine.Status();
                neutralThroughout &= fixture.Neutral && fixture.Preset == 0 && fixture.HandYaw == 0 && fixture.HandPitch == 0 &&
                    !status.MenuNavigation && !fixture.Input.Capture;
                freshThroughout &= status.DriverAlive && status.RoutingReady && status.Mode == "Desktop";
                await Task.Delay(20);
            }
            check(freshThroughout && neutralThroughout,
                label + " stays neutral across the entire queued-pulse lifetime while simulated routing remains coherently fresh");
            fixture.Tick(held with { ActivateClick = true, EscapeMenu = true });
            check(fixture.Neutral && !fixture.Engine.Status().MenuNavigation && !fixture.Input.Capture,
                label + " rejects premature reacquisition and a menu edge while the pre-release left button is held");
            fixture.Tick(held with { LeftDown = false });
            fixture.Tick(held with { LeftDown = false });
            check(fixture.Neutral && !fixture.Input.Capture,
                label + " cannot reacquire from held keyboard/right/middle controls after releasing the old left button");
            fixture.Tick(held with { ActivateClick = true });
            check(fixture.Input.Capture && fixture.Input.Active && !fixture.Input.Pointer && !fixture.Engine.Status().MenuNavigation &&
                fixture.Preset == 0 && fixture.Actions == 0 && fixture.Forward == 0 && fixture.Strafe == 0,
                label + " accepts only a fresh click and selects ordinary look with every already-held action gated");
            fixture.Tick(held);
            check(fixture.Actions == 0 && fixture.Forward == 0 && fixture.Strafe == 0 && !fixture.Input.Pointer && fixture.Preset == 0,
                label + " keeps held movement, interaction and posture neutral after reacquisition");
            fixture.Tick(game);
            double yaw = fixture.HeadYaw;
            fixture.Tick(game with { Forward = true, Right = true, Jump = true, Run = true, LeftDown = true, MiddleDown = true,
                Crouch = true, DeltaX = 40 });
            check(fixture.Forward == 1 && fixture.Strafe == 1 && fixture.Actions == 59 && fixture.HeadYaw != yaw &&
                !fixture.Input.Pointer && fixture.Preset == 0 && !fixture.Engine.Status().MenuNavigation,
                label + " restores fresh gameplay movement, look, interactions and posture after physical key/button release");
        }
    }

    sealed class Platform : IGameInputPlatform
    {
        GameInputSample sample;
        public bool Capture, Pointer, Active;
        public void Set(GameInputSample value) => sample = value;
        public GameInputSample Read(bool eligible) => eligible ? sample : new();
        public void SetCapture(bool value) => SetCapture(value, false);
        public void SetCapture(bool value, bool pointer) { Capture = value; Pointer = value && pointer; }
        public void SetActive(bool value) => Active = value;
        public void Dispose() { }
    }
    sealed class Fixture : IDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), "Switcheroonie-menu-recovery-" + Guid.NewGuid().ToString("N"));
        readonly MemoryMappedFile memory;
        readonly MemoryMappedViewAccessor view;
        long sequence;
        public readonly SharedChannel Channel;
        public readonly HarnessEngine Engine;
        public readonly Platform Input = new();
        public GameInputSample Game => new(Window: 73, Focused: true);
        public string InputPreferences => Path.Combine(directory, "direct-input.json");
        public uint Preset => view.ReadUInt32(80);
        public uint Actions => view.ReadUInt32(84);
        public double HeadYaw => view.ReadDouble(48);
        public double HandYaw => view.ReadDouble(64);
        public double HandPitch => view.ReadDouble(72);
        public double Forward => view.ReadDouble(88);
        public double Strafe => view.ReadDouble(96);
        public bool Neutral => view.ReadUInt32(36) == 0 && Actions == 0 && Forward == 0 && Strafe == 0;
        public Fixture()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "routing.json"), JsonSerializer.Serialize(new RoutingPreferences(false, "Desktop")));
            File.WriteAllText(InputPreferences, JsonSerializer.Serialize(new GameInputSettings(true, 1, .15)));
            string name = "Local\\VRC-SWITCHEROONIE-MenuRecovery-" + Guid.NewGuid().ToString("N");
            Channel = new SharedChannel(name); memory = MemoryMappedFile.OpenExisting(name); view = memory.CreateViewAccessor();
            Publish(44); Engine = new HarnessEngine(Channel, Input, directory);
        }
        void Publish(ulong? epoch = null)
        {
            view.Write(2048, ++sequence); view.Write(2056, (1L << 32) | 0x53575243u);
            view.Write(2064, Stopwatch.GetTimestamp()); view.Write(2072, epoch ?? view.ReadUInt64(24));
            view.Write(2080, 1L); view.Write(2088, 2d);
            view.Write(2096, (1L << 32) | 1); view.Write(2104, (95L << 32) | 1);
            view.Write(2112, 2d); view.Write(2120, 1.7d); view.Write(2128, -3d); view.Write(2136, 1d);
            view.Write(2048, ++sequence);
        }
        public void Tick(GameInputSample sample) { Publish(); Input.Set(sample); Engine.Tick(); }
        public Task<Reply> Command(Command command) { Publish(); return Engine.ExecuteAsync(command); }
        public void Dispose()
        {
            Engine.Dispose(); Channel.Dispose(); view.Dispose(); memory.Dispose();
            string full = Path.GetFullPath(directory), temporary = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Switcheroonie-menu-recovery-", StringComparison.Ordinal))
                throw new IOException("Private fixture cleanup scope changed.");
            foreach (string path in Directory.EnumerateFileSystemEntries(full))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || Directory.Exists(path))
                    throw new IOException("Unknown private fixture cleanup entry.");
            }
            Directory.Delete(full, true);
        }
    }
}
