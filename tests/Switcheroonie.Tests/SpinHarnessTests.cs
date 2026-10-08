using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Switcheroonie;
using Switcheroonie.Broker;

static class SpinHarnessTests
{
    sealed class Platform : IGameInputPlatform
    {
        GameInputSample sample;
        public bool Typing { get; private set; }
        public bool Capture { get; private set; }
        public void Set(GameInputSample value) => sample = value;
        public GameInputSample Read(bool eligible)
        {
            var result = eligible ? sample : new();
            // Model the real hook's typing gate, rather than delivering a spin
            // edge which Windows would have refused while chat is known open.
            if (Typing) result = result with { SpinToggle = false, SpinLeft = false, SpinRight = false, SpinForward = false, SpinBack = false };
            sample = sample with { ChatToggle = false, ChatCancel = false, Emergency = false, SpinToggle = false,
                ActivateClick = false, ToggleMenu = false, EscapeMenu = false, RaiseMenu = false, DeltaX = 0, DeltaY = 0 };
            return result;
        }
        public void SetTyping(bool value) => Typing = value;
        public void SetCapture(bool value) => Capture = value;
        public void Dispose() { }
    }

    sealed class Fixture : IDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), "Switcheroonie-spin-chat-" + Guid.NewGuid().ToString("N"));
        readonly MemoryMappedFile map;
        readonly MemoryMappedViewAccessor view;
        long sequence;
        public readonly SharedChannel Channel;
        public readonly HarnessEngine Engine;
        public readonly Platform Input = new();
        public GameInputSample Game = new(Window: 73, Focused: true);
        public Fixture(bool desktop = true)
        {
            string name = "Local\\VRC-SWITCHEROONIE-SpinChat-" + Guid.NewGuid().ToString("N");
            Channel = new SharedChannel(name); map = MemoryMappedFile.OpenExisting(name); view = map.CreateViewAccessor();
            Publish(desktop, 44); Engine = new(Channel, Input, directory);
        }
        public ulong Epoch => view.ReadUInt64(24);
        public ulong SpinGeneration => view.ReadUInt64(208);
        public string ConfigurationDirectory => directory;
        public bool DesktopRequest => view.ReadUInt32(32) == 1;
        public bool SpinLease => view.ReadUInt32(136) == 1;
        public uint NativeActions => view.ReadUInt32(84);
        public bool IdentitySpin => view.ReadDouble(152) == 1 && view.ReadDouble(160) == 0 && view.ReadDouble(168) == 0 && view.ReadDouble(176) == 0;
        public bool NeutralGameplay => view.ReadUInt32(36) == 0 && view.ReadUInt32(84) == 0 && view.ReadDouble(88) == 0 && view.ReadDouble(96) == 0;
        public void Publish(bool desktop, ulong epoch, double age = 2, uint capabilities = 67, uint error = 0,
            uint available = 0, uint suspended = 0, uint spinBlockReason = 0, ulong? spinAttemptGeneration = null,
            bool nativeSpinActive = false, ulong? submittedSpinGeneration = null)
        {
            view.Write(2048, ++sequence); view.Write(2056, (1L << 32) | 0x53575243u);
            view.Write(2064, Stopwatch.GetTimestamp()); view.Write(2072, epoch);
            view.Write(2080, ((long)error << 32) | (desktop ? 1L : 0)); view.Write(2088, age);
            view.Write(2096, (1L << 32) | 1L); view.Write(2104, ((long)capabilities << 32) | 1L);
            view.Write(2112, 2d); view.Write(2120, 1.7d); view.Write(2128, -3d); view.Write(2136, 1d);
            view.Write(2336, ((long)spinBlockReason << 32) | (nativeSpinActive ? 1L : 0));
            view.Write(2344, unchecked((long)(submittedSpinGeneration ?? (nativeSpinActive ? SpinGeneration : 0))));
            view.Write(2360, ((long)suspended << 32) | available);
            view.Write(2368, unchecked((long)(spinAttemptGeneration ?? SpinGeneration)));
            view.Write(2048, ++sequence);
        }
        public void Tick(GameInputSample? sample = null, bool? desktop = null, double age = 2, uint capabilities = 67, uint error = 0,
            uint spinBlockReason = 0, ulong? spinAttemptGeneration = null, bool nativeSpinActive = false, ulong? submittedSpinGeneration = null)
        {
            Publish(desktop ?? DesktopRequest, Epoch, age, capabilities, error, spinBlockReason: spinBlockReason,
                spinAttemptGeneration: spinAttemptGeneration, nativeSpinActive: nativeSpinActive, submittedSpinGeneration: submittedSpinGeneration);
            Input.Set(sample ?? Game); Engine.Tick();
        }
        public async Task EnableSpin() => await Engine.ExecuteAsync(new() { Name = "ConfigureSpin", Enabled = true });
        public void Dispose()
        {
            Engine.Dispose(); Channel.Dispose(); view.Dispose(); map.Dispose();
            foreach (string name in new[] { "keyboard.json", "direct-input.json", "routing.json", "routing.json.tmp", "spin.json", "spin.json.tmp" })
                File.Delete(Path.Combine(directory, name));
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        using (var fixture = new Fixture())
        {
            await fixture.EnableSpin();
            fixture.Tick(fixture.Game with { ChatToggle = true });
            check(fixture.Input.Typing && !fixture.Engine.Status().SpinActive && fixture.Engine.Status().SpinDetail == "Close chat to spin",
                "Desktop chat produces a truthful spin gate without activating motion");
            fixture.Tick(desktop: false); // Simulated native fallback changes the committed route and epoch.
            fixture.Tick(); // Acknowledge the broker's released Physical epoch.
            check(fixture.Input.Typing && fixture.Engine.Status().Mode == "Physical" && fixture.NeutralGameplay,
                "Physical fallback retains real chat knowledge while releasing all Desktop gameplay");
            fixture.Tick(fixture.Game with { ChatCancel = true });
            check(!fixture.Input.Typing && fixture.Engine.Status().SpinDetail == "Ready · Numpad 5 starts",
                "Esc cancels chat after Physical fallback without a stale Desktop typing mask");
            fixture.Tick(fixture.Game with { SpinToggle = true, Forward = true, LeftDown = true });
            check(fixture.Engine.Status().SpinActive && fixture.SpinLease && fixture.NeutralGameplay && !fixture.Input.Capture,
                "A fresh Physical Numpad 5 activates only spin after chat cancellation");
            fixture.Tick(fixture.Game with { ChatToggle = true, Forward = true });
            check(fixture.Input.Typing && !fixture.SpinLease && fixture.NeutralGameplay,
                "Y chat in Physical mode stops spin without acquiring Desktop actions");
            fixture.Tick(fixture.Game with { ChatToggle = true }); // Authenticated Enter edge.
            fixture.Tick(fixture.Game with { SpinToggle = true });
            check(!fixture.Input.Typing && fixture.SpinLease,
                "Physical chat submission permits a later explicit spin toggle");
            fixture.Tick(fixture.Game with { ChatToggle = true });
            fixture.Tick(fixture.Game with { Focused = false });
            fixture.Tick(fixture.Game with { SpinToggle = true });
            check(fixture.Input.Typing && !fixture.SpinLease,
                "Same-window focus loss preserves open chat and rejects spin reactivation");
            fixture.Tick(fixture.Game with { ChatCancel = true });
            fixture.Tick(fixture.Game with { SpinToggle = true });
            fixture.Tick(fixture.Game with { Emergency = true });
            check(!fixture.SpinLease && fixture.NeutralGameplay && !fixture.Input.Capture,
                "Emergency release clears spin and never acquires gameplay or cursor authority");
            fixture.Tick(fixture.Game with { ChatToggle = true });
            fixture.Tick(fixture.Game with { Window = 74 });
            fixture.Tick(fixture.Game with { Window = 74, SpinToggle = true });
            check(!fixture.Input.Typing && fixture.SpinLease,
                "A different authenticated game window cannot inherit the old chat gate");
        }
        using (var fixture = new Fixture())
        {
            await fixture.EnableSpin(); fixture.Tick(fixture.Game with { ChatToggle = true });
            await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureGameInput", Enabled = false });
            fixture.Tick(fixture.Game with { ChatCancel = true, Forward = true, LeftDown = true, ActivateClick = true, DeltaX = 80 });
            check(!fixture.Input.Typing && fixture.NeutralGameplay && !fixture.Input.Capture,
                "Disabled Desktop movement still observes chat cancellation for independent spin without acquiring input");
            fixture.Tick(fixture.Game with { SpinToggle = true });
            check(fixture.Engine.Status().SpinActive && !fixture.Engine.Status().GameInputActive && fixture.SpinLease && fixture.NeutralGameplay,
                "Desktop spin works independently when mouse and keyboard gameplay is disabled");
            await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureGameInput", Enabled = true });
            fixture.Tick(fixture.Game with { Forward = true, LeftDown = true });
            check(!fixture.Engine.Status().GameInputActive && fixture.NeutralGameplay,
                "Re-enabling gameplay does not convert held keys or mouse into acquisition");
        }
        using (var fixture = new Fixture(desktop: false))
        {
            fixture.Tick(); check(fixture.Engine.Status().SpinDetail == "Off", "Disabled spin status says Off");
            await fixture.EnableSpin(); fixture.Tick(fixture.Game with { Focused = false });
            check(fixture.Engine.Status().SpinDetail == "Click VRChat to spin", "Unfocused spin reports the actual foreground requirement");
            fixture.Tick(fixture.Game with { SpinToggle = true }, age: 300);
            check(!fixture.SpinLease && fixture.Engine.Status().SpinDetail == "Waiting for tracking", "Stale head tracking blocks spin and explains why");
            fixture.Tick(fixture.Game with { SpinToggle = true }, capabilities: 3);
            check(!fixture.SpinLease && fixture.Engine.Status().SpinDetail.Contains("driver update"), "Missing native spin capability cannot appear ready");
            fixture.Tick(fixture.Game with { SpinToggle = true }, error: 5);
            check(!fixture.SpinLease && fixture.Engine.Status().SpinDetail.Contains("diagnostics"), "A fatal native conflict rejects spin despite a retained capability flag");
            fixture.Tick(fixture.Game with { SpinToggle = true });
            check(fixture.SpinLease && fixture.Engine.Status().SpinDetail == "Starting · Numpad 5 stops",
                "Accepted spin request distinguishes pending native acknowledgement from actual spinning");
        }
        using (var fixture = new Fixture())
        {
            fixture.Publish(true, fixture.Epoch, capabilities: 3, available: 10, suspended: 10, spinBlockReason: 2, spinAttemptGeneration: 999);
            check(fixture.Channel.Read().GenericTrackerAvailable == 0 && fixture.Channel.Read().GenericTrackerSuspended == 0 && fixture.Channel.Read().SpinBlockReason == 0 && fixture.Channel.Read().SpinAttemptGeneration == 0,
                "Reserved tracker counts, spin-block reason and attempted generation are ignored without capability 128");
            fixture.Publish(true, fixture.Epoch, capabilities: 131, available: 10, suspended: 10);
            var status = fixture.Engine.Status();
            check(status.NativeGenericTrackerAvailable == 10 && status.NativeGenericTrackerSuspended == 10,
                "Capability 128 exposes exact captured-identity available and actually suspended generic tracker counts");
            fixture.Publish(true, fixture.Epoch, capabilities: 131, available: 65);
            check(fixture.Channel.Read().GenericTrackerAvailable == 0 && fixture.Channel.Read().GenericTrackerSuspended == 0,
                "Generic tracker count beyond the device bound is rejected");
            fixture.Publish(true, fixture.Epoch, capabilities: 131, available: 10, suspended: 11);
            check(fixture.Channel.Read().GenericTrackerAvailable == 0 && fixture.Channel.Read().GenericTrackerSuspended == 0,
                "Suspended count cannot exceed the available generic tracker count");
            await fixture.EnableSpin();
            fixture.Tick(fixture.Game with { SpinToggle = true }, capabilities: 195);
            await Task.Delay(20); fixture.Tick(fixture.Game with { SpinForward = true }, capabilities: 195);
            check(fixture.Engine.Status().SpinPitchSpeed > 0 && !fixture.IdentitySpin,
                "Controlled spin attempt has real accumulated rotation before its native refusal");
            ulong blockedAttempt = fixture.SpinGeneration;
            fixture.Tick(fixture.Game with { SpinForward = true }, capabilities: 195, spinBlockReason: 1);
            check(!fixture.SpinLease && fixture.IdentitySpin && !fixture.Engine.Status().SpinActive && fixture.Engine.Status().SpinPitchSpeed == 0 &&
                fixture.Engine.Status().NativeSpinBlockReason == 1 && fixture.Engine.Status().NativeSpinAttemptGeneration == blockedAttempt && fixture.Engine.Status().SpinDetail.Contains("VR → Desktop, then Numpad 5"),
                "Native stale-rig refusal stops accumulated motion and explains Desktop re-anchoring");
            fixture.Tick(fixture.Game with { SpinForward = true }, capabilities: 195);
            check(!fixture.SpinLease && fixture.IdentitySpin,
                "Fresh tracker recovery and held direction cannot silently resume refused spin");
            fixture.Tick(fixture.Game with { SpinToggle = true }, capabilities: 195);
            check(fixture.SpinLease && fixture.IdentitySpin && fixture.Engine.Status().SpinPitchSpeed == 0,
                "A fresh spin toggle after refusal starts from identity and zero speed");
            fixture.Tick(capabilities: 195, spinBlockReason: 1, spinAttemptGeneration: blockedAttempt);
            check(fixture.SpinLease && fixture.Engine.Status().SpinActive && fixture.Engine.Status().SpinDetail == "Starting · Numpad 5 stops" &&
                fixture.Engine.Status().NativeSpinAttemptGeneration == blockedAttempt,
                "A previous attempt's delayed refusal cannot cancel a new explicit retry generation");
            fixture.Tick(capabilities: 195, spinAttemptGeneration: blockedAttempt, nativeSpinActive: true, submittedSpinGeneration: blockedAttempt);
            check(fixture.SpinLease && fixture.Engine.Status().SpinDetail == "Starting · Numpad 5 stops",
                "Previously submitted native output cannot be labelled as the new spin attempt succeeding");
            fixture.Tick(capabilities: 195, nativeSpinActive: true);
            check(fixture.SpinLease && fixture.Engine.Status().SpinDetail == "Spinning · Numpad 5 stops",
                "Matching native attempt and submitted output confirm the fresh retry without cancellation");
            fixture.Tick(capabilities: 195, spinBlockReason: 2);
            check(!fixture.SpinLease && fixture.IdentitySpin && fixture.Engine.Status().NativeSpinBlockReason == 2 && fixture.Engine.Status().SpinDetail.Contains("VR → Desktop, then Numpad 5"),
                "Native tracker identity replacement reports the need for a new Desktop anchor");
            fixture.Tick(fixture.Game with { SpinToggle = true }, capabilities: 195);
            fixture.Tick(capabilities: 195, spinBlockReason: 3);
            check(!fixture.SpinLease && fixture.IdentitySpin && fixture.Engine.Status().NativeSpinBlockReason == 3 &&
                fixture.Engine.Status().SpinDetail == "Spin blocked · restart SteamVR",
                "Native lifetime admission fault stops spin and reports the required runtime recovery");
        }
        using (var fixture = new Fixture(desktop: false))
        {
            await fixture.EnableSpin(); fixture.Tick(fixture.Game with { SpinToggle = true });
            ulong oldEpoch = fixture.Epoch, oldAttempt = fixture.SpinGeneration;
            fixture.Engine.Dispose();
            check(!fixture.SpinLease && fixture.SpinGeneration == oldAttempt,
                "Broker disposal publishes the exact positive attempt token on its inactive lease");
            fixture.Publish(false, oldEpoch);
            var restartedInput = new Platform();
            using var restarted = new HarnessEngine(fixture.Channel, restartedInput, fixture.ConfigurationDirectory);
            ulong cancellationToken = fixture.SpinGeneration;
            check(restarted.Status().Epoch == oldEpoch && !fixture.SpinLease && cancellationToken > oldAttempt && fixture.NeutralGameplay,
                "Broker restart adopting the same native epoch first retires the old attempt with a newer inactive QPC token");
            restartedInput.Set(fixture.Game with { SpinToggle = true }); restarted.Tick();
            check(restarted.Status().SpinActive && fixture.SpinLease && fixture.SpinGeneration > cancellationToken && fixture.IdentitySpin,
                "Restarted broker activates a distinct increasing spin token on its retained epoch from identity");
        }
        using (var fixture = new Fixture())
        using (var receiver = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
        {
            int privatePort = ((System.Net.IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            fixture.Channel.PublishOscWatchdog();
            await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureOsc", Osc = true, Port = privatePort });
            fixture.Tick(fixture.Game with { ActivateClick = true });
            fixture.Tick(fixture.Game with { Prone = true });
            check(fixture.NativeActions == 64 && fixture.Channel.ReadOscLease() is { Fresh: true, Armed: true, Actions: 0 },
                "Private OSC posture fixture keeps pose modifiers out of its button lease");
            await Task.Delay(250); // Deliberately expire the real 200ms private-helper contract.
            fixture.Tick(fixture.Game with { Prone = true, Forward = true, LeftDown = true });
            check(!fixture.Channel.OscWatchdogAlive && fixture.NeutralGameplay && fixture.Channel.ReadOscLease() is { Fresh: true, Armed: false, Actions: 0 },
                "A truly expired OSC helper still releases posture and held inputs despite fresh HMD tracking");
            fixture.Channel.PublishOscWatchdog(); fixture.Tick(fixture.Game with { Forward = true, LeftDown = true });
            check(!fixture.Engine.Status().GameInputActive && fixture.NeutralGameplay,
                "Fresh helper recovery cannot reacquire a mouse or movement key held across expiry");
        }
    }
}
