using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Switcheroonie;
using Switcheroonie.Broker;

var repo = Path.GetFullPath(args.FirstOrDefault() ?? Directory.GetCurrentDirectory());
var brokerPath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(repo, "src", "Switcheroonie.Broker", "bin", "Release", "net10.0-windows", "Switcheroonie.Broker.exe");
if (!File.Exists(brokerPath)) throw new FileNotFoundException("Build the Release broker before running process integration.");
var brokerArtifact = brokerPath.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(repo, brokerPath) : "Explicit external broker artifact";
using var brokerBytes = File.OpenRead(brokerPath);
var brokerSha256 = Convert.ToHexString(SHA256.HashData(brokerBytes)).ToLowerInvariant();
brokerBytes.Close();
var results = new List<TestResult>();
var owned = new List<Process>();
Process? mainBroker = null;
int exit = 0;
void Result(string id, bool passed, string message, double? milliseconds = null) {
    results.Add(new(id, passed ? "Passed" : "Failed", message, milliseconds));
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {id}: {message}");
    if (!passed) exit = 1;
}
void Skip(string id, string message) { results.Add(new(id, "NotRun", message)); Console.WriteLine("NOT RUN " + id + ": " + message); }
Process StartOwned(params string[] arguments) {
    var start = new ProcessStartInfo(brokerPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(brokerPath)! };
    if (args.Length > 1) {
        // A packaged self-contained app must resolve its own runtime, not an installed SDK.
        start.Environment["DOTNET_ROOT"] = Path.Combine(repo, "build", "NoDotnetRuntimeForIntegration");
        start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        start.Environment["DOTNET_ROLL_FORWARD"] = "Disable";
    }
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    var process = Process.Start(start) ?? throw new IOException("Owned process could not start");
    owned.Add(process); return process;
}
async Task StopOwnedAsync(Process process) {
    try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } }
    catch (InvalidOperationException) { }
}
void CaptureMainChildren() {
    if (mainBroker is null || mainBroker.HasExited) return;
    foreach (var pid in NativeChildren.Find(mainBroker.Id)) {
        if (owned.Any(p => p.Id == pid)) continue;
        try { var process = Process.GetProcessById(pid); if (process.ProcessName == "Switcheroonie.Broker") owned.Add(process); else process.Dispose(); }
        catch (ArgumentException) { }
    }
}
async Task<Reply> ExchangeAsync(string message, int timeoutMilliseconds = 3000) {
    using var timeout = new CancellationTokenSource(timeoutMilliseconds);
    using var pipe = new NamedPipeClientStream(".", Identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(timeoutMilliseconds, timeout.Token);
    await pipe.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), timeout.Token);
    using var reader = new StreamReader(pipe);
    var reply = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Missing pipe reply");
    return JsonSerializer.Deserialize<Reply>(reply) ?? throw new IOException("Invalid pipe reply");
}

try {
    // Never interfere with a broker or helper that predated this test process.
    using var existing = new ProcessList(Process.GetProcessesByName("Switcheroonie.Broker"));
    using var existingRuntime = new ProcessList(Process.GetProcessesByName("vrserver"));
    if (existing.Processes.Length != 0 || existingRuntime.Processes.Length != 0) {
        Skip("P01-P06", "An existing broker/helper or active SteamVR runtime was detected; public-pipe tests skipped without touching it.");
    } else {
        mainBroker = StartOwned();
        Reply? ready = null;
        for (var attempt = 0; attempt < 30 && ready is null; ++attempt) {
            try { ready = await ExchangeAsync("{}", 500); }
            catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { await Task.Delay(100); }
        }
        Result("P01", ready?.Accepted == true, "Actual hidden Broker.exe answers the current-user pipe; no VR runtime/application launch.");
        CaptureMainChildren();
        if (ready is null) throw new IOException("Owned broker did not become ready");
        if (args.Length > 1) {
            var runtimeModule = mainBroker.Modules.Cast<ProcessModule>().FirstOrDefault(module => module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
            var expectedDirectory = Path.GetDirectoryName(brokerPath)! + Path.DirectorySeparatorChar;
            Result("P07", runtimeModule?.FileName.StartsWith(expectedDirectory, StringComparison.OrdinalIgnoreCase) == true,
                "Packaged process loads coreclr.dll from its own package with external DOTNET_ROOT disabled.");
        }
        var malformed = await ExchangeAsync("{broken");
        Result("P02", !malformed.Accepted && malformed.Reason == "Invalid JSON.", "Malformed request rejected through real pipe without stopping broker.");
        var oversized = await ExchangeAsync(new string('a', 16385));
        Result("P03", !oversized.Accepted && oversized.Reason.Contains("16 KiB"), "Oversized request rejected through real pipe.");
        var release = await ExchangeAsync("{\"Name\":\"ReleaseInputs\"}");
        Result("P04", release.Accepted && !release.Status.Armed, "Emergency release works while native driver is unavailable.");

        var idleClients = new List<NamedPipeClientStream>();
        try {
            for (var i = 0; i < 8; ++i) {
                var pipe = new NamedPipeClientStream(".", Identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                idleClients.Add(pipe);
                using var timeout = new CancellationTokenSource(1500);
                await pipe.ConnectAsync(1500, timeout.Token);
                // No bytes: exercise bounded slow-client admission, not malformed-request parsing.
            }
            await Task.Delay(50);
            var busy = await ExchangeAsync("{}", 1500);
            Result("P05", !busy.Accepted && busy.Reason.Contains("busy", StringComparison.OrdinalIgnoreCase) && !mainBroker.HasExited, "Eight idle clients and a ninth request return busy rather than crashing the broker.");
        } finally { foreach (var pipe in idleClients) pipe.Dispose(); }
        await Task.Delay(100);
        var recovered = await ExchangeAsync("{}");
        Result("P06", recovered.Accepted && !mainBroker.HasExited, "Broker responds normally after saturated connections close.");
        CaptureMainChildren();
        await StopOwnedAsync(mainBroker); // Stop parent before helper cleanup to prevent a respawn race.
        foreach (var child in owned.Where(p => p != mainBroker).ToArray()) await StopOwnedAsync(child);
    }

    // Private map and ephemeral UDP destination. No values are ever written to the user's driver map or VRChat OSC port.
    var mapName = "Local\\VRC-SWITCHEROONIE-Test-" + Guid.NewGuid().ToString("N");
    using var channel = new SharedChannel(mapName);
    using var fixture = MemoryMappedFile.OpenExisting(mapName);
    using var data = fixture.CreateViewAccessor();
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
    long sequence = 0;
    bool arm = true;
    Task? publisher = null;
    CancellationTokenSource? publishing = null;
    void Publish() {
        data.Write(2048, ++sequence);
        data.Write(2056, (1L << 32) | 0x53575243u);
        data.Write(2064, Stopwatch.GetTimestamp()); data.Write(2072, 1UL);
        data.Write(2080, 1L); data.Write(2088, 0.0); data.Write(2096, 1L); data.Write(2104, 1L);
        data.Write(2048, ++sequence);
        channel.Publish(1, true, Volatile.Read(ref arm), 0, 0, 0, 0, 0, 0, 0, 0, 0, true, port, 1, 0, 8);
    }
    void StartPublisher() {
        publishing = new CancellationTokenSource(); var cancellation = publishing;
        Publish();
        publisher = Task.Run(async () => {
            try { using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10)); while (await timer.WaitForNextTickAsync(cancellation.Token)) Publish(); }
            catch (OperationCanceledException) { }
        });
    }
    async Task StopPublisher() {
        if (publishing is null) return;
        publishing.Cancel(); if (publisher is not null) await publisher;
        publishing.Dispose(); publishing = null; publisher = null;
    }
    void Drain() { while (receiver.Available > 0) _ = receiver.Receive(ref UnsafeEndpoint.Value); }
    async Task<(bool Matched, double Milliseconds)> ObserveAsync(Dictionary<string, int> expectedBits, int timeoutMilliseconds) {
        var remaining = new Dictionary<string, int>(expectedBits);
        var clock = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        try {
            while (remaining.Count > 0) {
                var packet = (await receiver.ReceiveAsync(timeout.Token)).Buffer;
                var nul = Array.IndexOf(packet, (byte)0);
                if (nul < 1 || packet.Length < 8) continue;
                var address = Encoding.ASCII.GetString(packet, 0, nul);
                var bits = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(packet.Length - 4));
                if (remaining.TryGetValue(address, out var expected) && bits == expected) remaining.Remove(address);
            }
        } catch (OperationCanceledException) { }
        return (remaining.Count == 0, clock.Elapsed.TotalMilliseconds);
    }
    var asserted = new Dictionary<string, int> { ["/input/Vertical"] = BitConverter.SingleToInt32Bits(1), ["/input/Jump"] = 1 };
    var neutral = new Dictionary<string, int> { ["/input/Vertical"] = 0, ["/input/Jump"] = 0 };
    StartPublisher();
    var helper = StartOwned("--osc-watchdog", "--map=" + mapName);
    try {
        var active = await ObserveAsync(asserted, 2000);
        Result("W01", active.Matched && channel.OscWatchdogAlive, "Real separate helper sends asserted float/integer OSC to an ephemeral mock receiver using a private simulated driver map.", active.Milliseconds);
        Drain();
        await StopPublisher();
        var stale = await ObserveAsync(neutral, 600);
        Result("W02", stale.Matched && stale.Milliseconds < 450, "Stopping the producer heartbeat causes actual helper UDP neutral packets; no broker process is involved.", stale.Milliseconds);
        Volatile.Write(ref arm, true); StartPublisher();
        var restored = await ObserveAsync(asserted, 1200);
        Result("W03", restored.Matched, "Same forward/jump values are resent after neutralization; sender cache does not suppress rearm.", restored.Milliseconds);
        Drain();
        await StopOwnedAsync(helper);
        Drain(); // Exclude packets queued before the sender actually exited.
        var afterDeath = await ObserveAsync(neutral, 320);
        results.Add(new("W04", "ObservedLimit", afterDeath.Matched ? "Unexpected neutral traffic after all senders exited; investigate source isolation." : "After the only OSC sender is killed, no neutral packets arrive. Total-sender-loss neutralization is not covered; receiver retains its prior value.", afterDeath.Milliseconds));
        Console.WriteLine("OBSERVED LIMIT W04: total-sender loss cannot emit release packets; this is not a release pass.");
        if (afterDeath.Matched) exit = 1;
        Volatile.Write(ref arm, false);
        var replacement = StartOwned("--osc-watchdog", "--map=" + mapName);
        var restart = await ObserveAsync(neutral, 2000);
        Result("W05", restart.Matched, "Fresh helper emits baseline neutral packets for an enabled but disarmed lease, clearing a dead predecessor's latched inputs.", restart.Milliseconds);
        await StopOwnedAsync(replacement);
    } finally { await StopPublisher(); await StopOwnedAsync(helper); }

    // Exercise the live managed broker engine's surviving emergency sender.
    // The disappeared helper heartbeat is simulated in this private map; UDP release is real.
    var privatePreferences = Path.Combine(repo, "build", "integration-private-preferences-" + Guid.NewGuid().ToString("N"));
    using (var emergencyEngine = new HarnessEngine(channel, configurationDirectory: privatePreferences)) {
        Publish(); // Fresh private driver status; producer is disarmed.
        channel.PublishOscWatchdog();
        var configured = await emergencyEngine.ExecuteAsync(new Command { Name = "ConfigureOsc", Osc = true, Destination = "127.0.0.1", Port = port });
        if (!configured.Accepted) throw new IOException("Private emergency sender configuration rejected: " + configured.Reason);
        emergencyEngine.Tick();
        Drain();
        using (var predecessor = new UdpClient()) {
            var target = new IPEndPoint(IPAddress.Loopback, port);
            var vertical = OscSender.Encode("/input/Vertical", 1, false);
            var jump = OscSender.Encode("/input/Jump", 1, true);
            await predecessor.SendAsync(vertical, target);
            await predecessor.SendAsync(jump, target);
        } // Socket disposal intentionally cannot emit release packets.
        var priorInput = await ObserveAsync(asserted, 1000);
        if (!priorInput.Matched) throw new IOException("Private predecessor input did not reach mock receiver");
        Drain();
        data.Write(3080, Stopwatch.GetTimestamp() - Stopwatch.Frequency); // Expired helper heartbeat, not the user's map.
        emergencyEngine.Tick();
        var backup = await ObserveAsync(neutral, 1000);
        Result("W06", backup.Matched && !emergencyEngine.Status().Armed, "Live broker engine emits actual emergency OSC neutral packets after helper heartbeat expires, including an already disarmed input lease (private simulated lifecycle).", backup.Milliseconds);
    }
} catch (Exception e) {
    var userPrefix = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    Result("HARNESS", false, e.GetType().Name + ": " + e.Message.Replace(userPrefix, "%USERPROFILE%"));
} finally {
    CaptureMainChildren();
    if (mainBroker is not null) await StopOwnedAsync(mainBroker);
    foreach (var process in owned) await StopOwnedAsync(process);
    foreach (var process in owned) process.Dispose();
    var output = Path.Combine(repo, "reports", "process-integration.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var summary = new {
        schemaVersion = 1, dateUtc = DateTime.UtcNow, category = "Actual broker/helper processes and managed emergency sender, real named pipes and loopback UDP; simulated private driver/lifecycle map; no hardware tests",
        brokerArtifact, brokerSha256,
        passed = exit == 0, results,
        boundary = "No SteamVR, VRChat, streamer, tracker, or desktop input was started/stopped/changed. Only captured owned broker/helper process handles were terminated. Public driver state was never spoofed. No raw logs, usernames, device serials, account/instance identifiers or network addresses were exported.",
        uncovered = new[] { "Actual VRChat OSC receipt, gameplay, physical tracking and display presentation.", "Simultaneous loss of every OSC sender cannot neutralize receiver state.", "Full UI-focus and native routing path with physically armed OSC remains untested; the public driver map was not spoofed here." }
    };
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Process integration: {results.Count(r => r.Status == "Passed")} passed, {results.Count(r => r.Status == "Failed")} failed, {results.Count(r => r.Status == "NotRun")} not run, {results.Count(r => r.Status == "ObservedLimit")} documented limit. Report: reports/process-integration.json");
}
return exit;

sealed record TestResult(string Id, string Status, string Message, double? ElapsedMilliseconds = null);
sealed class ProcessList(Process[] processes) : IDisposable { public Process[] Processes => processes; public void Dispose() { foreach (var process in processes) process.Dispose(); } }
static class UnsafeEndpoint { public static IPEndPoint Value = new(IPAddress.Any, 0); }
static class NativeChildren {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Entry {
        public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, ParentPid; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string File;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(nint snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(nint snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    public static int[] Find(int parent) {
        var found = new List<int>(); var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) return [];
        try { var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>() }; if (Process32First(snapshot, ref entry)) do { if (entry.ParentPid == parent) found.Add((int)entry.Pid); } while (Process32Next(snapshot, ref entry)); }
        finally { CloseHandle(snapshot); }
        return found.ToArray();
    }
}
