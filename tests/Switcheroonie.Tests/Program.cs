using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Buffers.Binary;
using Switcheroonie;
using Switcheroonie.Broker;

int assertions = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); ++assertions; Console.WriteLine("PASS " + name); }
if (args.Contains("--self-test-state-paths"))
{
    StatePathsTests.Run(Check);
    Console.WriteLine(JsonSerializer.Serialize(new { category = "Private state migration fixtures and read-only known-folder API", assertions, passed = true, dateUtc = DateTime.UtcNow }));
    return;
}
string name = "Local\\VRC-SWITCHEROONIE-Test-" + Guid.NewGuid().ToString("N");
using var channel = new SharedChannel(name);
var configurationDirectory = Path.Combine(Path.GetTempPath(), "Switcheroonie-tests-" + Guid.NewGuid().ToString("N"));
var gamePlatform = new FixtureGameInput();
using var engine = new HarnessEngine(channel, gamePlatform, configurationDirectory);
using var fixture = MemoryMappedFile.OpenExisting(name);
using var data = fixture.CreateViewAccessor();
long fixtureSequence = 0;
void SetDriver(ulong epoch = 1, bool desktop = false, double age = 0, uint error = 0)
{
    data.Write(2048, ++fixtureSequence);
    data.Write(2056, ((long)1 << 32) | 0x53575243u); data.Write(2064, Stopwatch.GetTimestamp()); data.Write(2072, epoch);
    data.Write(2080, (desktop ? 1L : 0) | ((long)error << 32)); data.Write(2088, age);
    data.Write(2096, (1L << 32) | 1); data.Write(2104, (95L << 32) | 1); data.Write(2168, 42L); data.Write(2176, 99L);
    data.Write(2112, 2d); data.Write(2120, 1.7d); data.Write(2128, -3d); data.Write(2136, 1d);
    data.Write(2048, ++fixtureSequence);
}
Check(!channel.Read().Alive, "Empty map is not a connected driver");
Check(!(await engine.ExecuteAsync(new() { Name = "RequestMode", Mode = "Desktop" })).Accepted, "No-driver switch rejected");
Check(engine.Status().Mode == "Unmanaged", "No-driver status is unmanaged");
Check(!(await engine.ExecuteAsync(new() { Version = 2 })).Accepted, "Protocol version rejected");
Check(!(await engine.ExecuteAsync(new() { Id = null! })).Accepted, "Null request ID rejected");
Check(!(await engine.ExecuteAsync(new() { Name = "SetDesktopHeight", Height = double.NaN })).Accepted, "Nonfinite height rejected");
Check(!(await engine.ExecuteAsync(new() { Name = "SetDesktopHeight", Height = 2 })).Accepted, "Out-of-bounds height rejected");
Check((await engine.ExecuteAsync(new() { Name = "SetDesktopHeight", Height = -0.73 })).Accepted, "Desktop height accepted within bounds");
Check(!(await engine.ExecuteAsync(new() { Name = "SetHandPreset", Preset = 9 })).Accepted, "Unknown hand preset rejected");
Check(!(await engine.ExecuteAsync(new() { Name = "ConfigureGameInput", Enabled = true, Sensitivity = double.NaN })).Accepted, "Nonfinite game sensitivity rejected");
Check(!(await engine.ExecuteAsync(new() { Name = "ConfigureGameInput", Enabled = true, Sensitivity = 9 })).Accepted, "Out-of-range game sensitivity rejected");
Check(!(await engine.ExecuteAsync(new() { Name = "ConfigureOsc", Osc = true, Destination = "8.8.8.8" })).Accepted, "Remote OSC destination rejected");
Check(!(await engine.ExecuteAsync(new() { Name = "ConfigureOsc", Osc = true, Destination = "127.0.0.1", Port = 19000 })).Accepted, "Missing OSC watchdog rejects activation");
channel.PublishOscWatchdog();
Check((await engine.ExecuteAsync(new() { Name = "ConfigureOsc", Osc = true, Destination = "127.0.0.1", Port = 19000 })).Accepted, "Send-only loopback OSC config accepted");
SetDriver(age: 300);
Check(!(await engine.ExecuteAsync(new() { Name = "RequestMode", Mode = "Desktop" })).Accepted, "Stale physical tracking rejected");
SetDriver();
Check(channel.Read().Alive && channel.Read().PhysicalSamples == 99, "Driver status layout decoded");
data.Write(2048, ++fixtureSequence);
data.Write(2272, (1L << 32) | 255); data.Write(2280, 3L); data.Write(2288, (20L << 32) | 1); data.Write(2296, (1L << 32) | 4);
data.Write(2048, ++fixtureSequence);
var inputDiagnostics = channel.Read();
Check(inputDiagnostics.InputCoverage == 255 && inputDiagnostics.SelectedMenuPath == 1 && inputDiagnostics.MenuRisingEdges == 3 &&
    inputDiagnostics.MenuPressed && inputDiagnostics.LastInputError == 20 && inputDiagnostics.EffectiveNativeActions == 4 && inputDiagnostics.InputArmed,
    "Additive native menu delivery diagnostics are decoded under the status seqlock");
data.Write(2048, ++fixtureSequence);
Check(!channel.Read().Alive, "Torn status publication rejected without waiting");
data.Write(2048, ++fixtureSequence);

bool acknowledge = true;
using var cancellation = new CancellationTokenSource();
var runtime = Task.Run(async () =>
{
    try
    {
        while (!cancellation.IsCancellationRequested)
        {
            // Refresh both simulated producers before the broker reads them.
            if (Volatile.Read(ref acknowledge)) SetDriver(data.ReadUInt64(24), data.ReadUInt32(32) == 1);
            channel.PublishOscWatchdog();
            engine.Tick();
            gamePlatform.CompleteTick();
            await Task.Delay(5, cancellation.Token);
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    catch (Exception e) { gamePlatform.FailPending(e); throw; }
    finally { gamePlatform.CancelPending(); }
});
try
{
var request = new Command { Name = "RequestMode", Mode = "Desktop" };
var switched = await engine.ExecuteAsync(request);
Check(switched.Accepted && switched.Status.Mode == "Desktop", "Switch waits for fixture driver acknowledgement (simulated)");
var repeated = await engine.ExecuteAsync(request);
Check(repeated == switched, "Same request ID returns prior transaction result");
Check((await engine.ExecuteAsync(new() { Name = "ConfigureOsc", Osc = false })).Accepted, "Native game input test uses no OSC sender");
Check((await engine.ExecuteAsync(new() { Name = "ConfigureGameInput", Enabled = true, Sensitivity = 1.4 })).Accepted &&
    JsonSerializer.Deserialize<GameInputSettings>(File.ReadAllText(Path.Combine(configurationDirectory, "direct-input.json")))?.Sensitivity == 1.4,
    "Game input preferences are persisted in the isolated configuration directory");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, ActivateClick: true), cancellation.Token);
Check(engine.Status().InputOwner == "Game" && engine.Status().GameInputActive, "Fixture game foreground click acquires input without UI lease");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, DeltaY: -100), cancellation.Token);
Check(data.ReadDouble(56) > 0.3 && Math.Abs(data.ReadDouble(56) - data.ReadDouble(72)) < 1e-9 && data.ReadDouble(64) == 0,
    "Gameplay hand ray follows mouse head pitch for ordinary use/pickup aiming");
Check(data.ReadUInt32(80) == 0, "Head look publishes the resting hand preset");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, Crouch: true), cancellation.Token);
Check(data.ReadUInt32(84) == 32 && data.ReadDouble(40) == -0.73,
    "Crouch is a native pose modifier without changing the persisted height offset");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, Crouch: true, Prone: true), cancellation.Token);
Check(data.ReadUInt32(84) == 64, "Broker publishes exclusive prone when both posture keys are held");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true), cancellation.Token);
Check(data.ReadUInt32(84) == 64, "Broker retains toggled prone on key release");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, Prone: true), cancellation.Token);
Check(data.ReadUInt32(84) == 0, "Broker clears prone on its next press");
channel.PublishOscWatchdog();
Check((await engine.ExecuteAsync(new() { Name = "ConfigureOsc", Osc = true, Destination = "127.0.0.1", Port = 19000 })).Accepted,
    "Private fixture enables OSC to verify separate native posture transport");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, ActivateClick: true), cancellation.Token);
channel.PublishOscWatchdog();
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, Prone: true), cancellation.Token);
var postureDeadline = Stopwatch.StartNew();
DriverSnapshot postureSource = default;
OscLease postureLease = default;
bool postureWatchdog = false;
uint nativePosture = 0;
while (postureDeadline.ElapsedMilliseconds < 3000)
{
    // Accept and retain the same coherent reads that satisfied the wait. A
    // second opportunistic read can hit either producer's next odd sequence.
    var source = channel.Read();
    var osc = channel.ReadOscLease();
    bool watchdog = channel.OscWatchdogAlive;
    uint actions = data.ReadUInt32(84);
    if (watchdog && source.Alive && actions == 64 && osc is { Fresh: true, Enabled: true, Armed: true })
    {
        postureSource = source; postureLease = osc;
        postureWatchdog = watchdog; nativePosture = actions;
        break;
    }
    await Task.Delay(5, cancellation.Token);
}
Check(postureWatchdog && postureSource.Alive && nativePosture == 64 &&
    postureLease is { Fresh: true, Enabled: true, Armed: true, Actions: 0 },
    "OSC movement leaves prone on the native pose path and out of OSC button actions");
Check((await engine.ExecuteAsync(new() { Name = "ConfigureOsc", Osc = false })).Accepted,
    "Private posture test restores native movement transport");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, ActivateClick: true), cancellation.Token);
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true), cancellation.Token);
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, RightDown: true, DeltaX: 40), cancellation.Token);
Check(data.ReadUInt32(80) == 2 && data.ReadUInt32(84) == 0 && data.ReadDouble(64) > .1,
    "Right-held pointer publishes the explicit aim preset without a grab press");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true), cancellation.Token);
Check(data.ReadUInt32(80) == 0, "Pointer release publishes resting hands again");
Check((await engine.ExecuteAsync(new() { Name = "DisarmViewer", Window = 99 })).Accepted && engine.Status().GameInputActive,
    "Late panel disarm cannot cancel game ownership");
Check(!(await engine.ExecuteAsync(new() { Name = "UpdateInput", Window = 99, Armed = true, Actions = 1 })).Accepted && engine.Status().GameInputActive,
    "Rejected stale pad packet cannot cancel game ownership");
await gamePlatform.SetAndWaitAsync(new(), cancellation.Token);
Check(!engine.Status().Armed && !gamePlatform.Capture && data.ReadUInt32(36) == 0 && data.ReadUInt32(84) == 0,
    "Fixture game focus loss releases movement, posture, native buttons and cursor ownership");
Check((await engine.ExecuteAsync(new() { Name = "ToggleMenu" })).Accepted, "Broker accepts menu pulse without a pad lease");
await Task.Delay(30);
Check((data.ReadUInt32(84) & 4) != 0 && data.ReadUInt32(36) == 1 && engine.Status().InputOwner == "MenuPulse",
    "Broker publishes actual armed menu action independently of UI cadence");
await Task.Delay(220);
Check((data.ReadUInt32(84) & 4) == 0 && data.ReadUInt32(36) == 0, "Broker sends neutral after timed menu pulse without UI callback");
Check(!(await engine.ExecuteAsync(new() { Name = "ArmViewer", Window = 0 })).Accepted, "Invalid foreground window cannot own gameplay input");
Check(!(await engine.ExecuteAsync(new() { Name = "UpdateInput", Armed = true, Actions = 1 })).Accepted, "Unarmed producer cannot assert trigger");
Check((await engine.ExecuteAsync(new() { Name = "ReleaseInputs" })).Accepted && !engine.Status().Armed, "Emergency input release works independently of UI");
Check((await engine.ExecuteAsync(new() { Name = "RequestMode", Mode = "Physical" })).Accepted, "Physical transaction acknowledged (simulated)");
Check((await engine.ExecuteAsync(new() { Name = "ConfigureSpin", Enabled = true })).Accepted &&
    JsonSerializer.Deserialize<SpinPreferences>(File.ReadAllText(Path.Combine(configurationDirectory, "spin.json")))?.Enabled == true,
    "Spin option persists independently without activating motion");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, SpinToggle: true), cancellation.Token);
Check(engine.Status().SpinActive && data.ReadUInt32(136) == 1 && data.ReadUInt32(36) == 0 && data.ReadUInt32(140) == 0 &&
    data.ReadInt64(144) > 0 && data.ReadDouble(152) == 1 && Math.Abs(data.ReadDouble(192) - .95) < 1e-12,
    "Physical spin publishes a separate coherent quaternion lease without arming desktop buttons");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: true, SpinLeft: true), cancellation.Token); await Task.Delay(40);
Check(engine.Status().SpinRollSpeed > 0 && data.ReadDouble(176) != 0,
    "Physical spin controller reaches the shared native quaternion fields");
await gamePlatform.SetAndWaitAsync(new(Window: 17, Focused: false), cancellation.Token);
Check(!engine.Status().SpinActive && data.ReadUInt32(136) == 0, "Focus loss neutralizes the separate spin lease");
using (var persistedChannel = new SharedChannel("Local\\VRC-SWITCHEROONIE-SpinPrefs-Test-" + Guid.NewGuid().ToString("N")))
using (var persistedEngine = new HarnessEngine(persistedChannel, configurationDirectory: configurationDirectory))
    Check(persistedEngine.Status().SpinEnabled && !persistedEngine.Status().SpinActive,
        "Restart remembers the option but never resumes spinning");
Check((await engine.ExecuteAsync(new() { Name = "ConfigureSpin", Enabled = false })).Accepted && !engine.Status().SpinEnabled,
    "Disabling the preference also revokes motion");
acknowledge = false;
var failed = await engine.ExecuteAsync(new() { Name = "RequestMode", Mode = "Desktop" });
Check(!failed.Accepted && !failed.Status.Armed, "Unacknowledged mode never reports success");
}
finally { cancellation.Cancel(); await runtime; }

channel.Publish(4, true, true, 0, 0.1, -0.2, 0, 0, 2, 1, 0, 0, true, 19000, 1, -1, 8);
var lease = channel.ReadOscLease();
Check(lease.Fresh && lease.Enabled && lease.Armed && lease.Forward == 1 && lease.Strafe == -1 && lease.Actions == 8, "OSC lease extension round trip");
await Task.Delay(230);
Check(!channel.ReadOscLease().Fresh, "OSC lease expires independently of broker");

using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
using var sender = new OscSender();
sender.Configure(true, "127.0.0.1", port); sender.Update(1, 0, 8);
var packets = new List<byte[]>();
for (int i = 0; i < 4; i++) packets.Add((await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1))).Buffer);
Check(packets.Any(p => System.Text.Encoding.ASCII.GetString(p).StartsWith("/input/Vertical") && BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(p.Length - 4)) == BitConverter.SingleToInt32Bits(1)), "Real loopback UDP float OSC packet");
Check(packets.Any(p => System.Text.Encoding.ASCII.GetString(p).StartsWith("/input/Jump") && BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(p.Length - 4)) == 1), "Real loopback UDP integer OSC packet");
sender.Release(repeat: true);
for (int i = 0; i < 4; ++i) await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1));
sender.Update(1, 0, 8);
var rearmed1 = (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1))).Buffer;
var rearmed2 = (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1))).Buffer;
Check(BinaryPrimitives.ReadInt32BigEndian(rearmed1.AsSpan(rearmed1.Length - 4)) != 0 && BinaryPrimitives.ReadInt32BigEndian(rearmed2.AsSpan(rearmed2.Length - 4)) != 0, "Rearm resends held actions after repeated neutral packets");
sender.Release();
var neutral1 = (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1))).Buffer;
var neutral2 = (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1))).Buffer;
Check(BinaryPrimitives.ReadInt32BigEndian(neutral1.AsSpan(neutral1.Length - 4)) == 0 && BinaryPrimitives.ReadInt32BigEndian(neutral2.AsSpan(neutral2.Length - 4)) == 0, "OSC release sends actual neutral packets");
GameInputTests.Run(Check);
MenuRecoveryTests.Run(Check);
RawMousePacketTests.Run(Check);
SpinTests.Run(Check);
GameCursorCaptureTests.Run(Check);
GameWindowAuthorityTests.Run(Check);
OwnedCursorVisibilityTests.Run(Check);
StatePathsTests.Run(Check);
CursorTests.Run(Check);
AutomaticRoutingTests.Run(Check);
HelperRestartPolicyTests.Run(Check);
BrokerLifetimeTests.Run(Check);
HarnessDisposalTests.Run(Check);
await HarnessMenuRecoveryTests.RunAsync(Check);
await AutomaticHarnessTests.RunAsync(Check);
await ConcurrentRoutingTests.RunAsync(Check);
await SpinHarnessTests.RunAsync(Check);
var summary = new { category = "Automated + simulated runtime; no hardware claim", assertions, passed = true, dateUtc = DateTime.UtcNow };
Console.WriteLine(JsonSerializer.Serialize(summary));

sealed class FixtureGameInput : IGameInputPlatform
{
    readonly object sync = new();
    GameInputSample sample;
    long generation, readGeneration;
    TaskCompletionSource? pending;
    public bool Capture { get; private set; }
    public async Task SetAndWaitAsync(GameInputSample value, CancellationToken cancellation)
    {
        Task completion;
        lock (sync)
        {
            if (pending is not null && !pending.Task.IsCompleted)
                throw new InvalidOperationException("An unconsumed fixture sample cannot be replaced.");
            ++generation; sample = value;
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = pending.Task;
        }
        // Consumption alone is insufficient: the enclosing Tick must publish
        // the resulting actions before the test asserts or sends another edge.
        await completion.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
    }
    public void CompleteTick()
    {
        lock (sync) if (readGeneration == generation) pending?.TrySetResult();
    }
    public void FailPending(Exception error) { lock (sync) pending?.TrySetException(error); }
    public void CancelPending() { lock (sync) pending?.TrySetCanceled(); }
    public GameInputSample Read(bool eligible)
    {
        lock (sync)
        {
            var value = sample;
            readGeneration = generation;
            sample = sample with { ActivateClick = false, ToggleMenu = false, EscapeMenu = false, RaiseMenu = false, SpinToggle = false,
                CrouchPress = false, PronePress = false, ChatToggle = false, ChatCancel = false, Emergency = false, DeltaX = 0, DeltaY = 0 };
            return value;
        }
    }
    public void SetCapture(bool capture) => Capture = capture;
    public void Dispose() { CancelPending(); }
}

