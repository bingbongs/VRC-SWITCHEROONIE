using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Switcheroonie;
using Switcheroonie.Broker;

if (args.Contains("--osc-watchdog")) { await OscWatchdog.RunAsync(args.FirstOrDefault(a => a.StartsWith("--map="))?[6..]); return; }
var cursorArgument = args.FirstOrDefault(a => a.StartsWith("--cursor-watchdog=", StringComparison.Ordinal));
if (cursorArgument is not null)
{
    if (int.TryParse(args.FirstOrDefault(a => a.StartsWith("--parent=", StringComparison.Ordinal))?[9..], out int parentPid))
        await CursorWatchdog.RunAsync(cursorArgument[18..], parentPid);
    return;
}
using var mutex = new Mutex(true, "Local\\VRC-SWITCHEROONIE-Broker-" + Identity.Sid, out bool owns);
if (!owns) return;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var channel = new SharedChannel();
using var engine = new HarnessEngine(channel, new WindowsGameInput());
var executable = Environment.ProcessPath!;
var helper = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) helper.ArgumentList.Add(typeof(HarnessEngine).Assembly.Location);
helper.ArgumentList.Add("--osc-watchdog");
System.Diagnostics.Process? watchdog = null;
var watchdogRestarts = new HelperRestartPolicy(System.Diagnostics.Stopwatch.Frequency);
void EnsureWatchdog()
{
    bool childAlive;
    try { childAlive = watchdog is not null && !watchdog.HasExited; }
    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
    { childAlive = true; } // Uncertain child identity cannot authorize a replacement.
    if (!watchdogRestarts.TryBeginStart(System.Diagnostics.Stopwatch.GetTimestamp(), childAlive, channel.OscWatchdogAlive)) return;
    try
    {
        var previous = watchdog; watchdog = null; previous?.Dispose();
        watchdog = System.Diagnostics.Process.Start(helper);
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException) { }
}
EnsureWatchdog();
using var connections = new SemaphoreSlim(8, 8);
var ticker = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
    while (await timer.WaitForNextTickAsync(cancellation.Token)) { engine.Tick(); EnsureWatchdog(); }
}, cancellation.Token);
Console.WriteLine("VRC-SWITCHEROONIE broker ready. No VR application or runtime is launched.");
try
{
    while (!cancellation.IsCancellationRequested)
    {
        var pipe = new NamedPipeServerStream(Identity.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 16384, 32768);
        try { await pipe.WaitForConnectionAsync(cancellation.Token); }
        catch { pipe.Dispose(); throw; }
        if (connections.Wait(0)) _ = RunClientAsync(pipe);
        else
        {
            using (pipe)
            using (var busyTimeout = new CancellationTokenSource(100))
            {
                try { await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Reply(false, "Broker busy; try again shortly.", engine.Status())) + "\n"), busyTimeout.Token); }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
            }
        }
    }
}
catch (OperationCanceledException) { }
cancellation.Cancel();
try { await ticker; } catch (OperationCanceledException) { }
watchdog?.Dispose();

async Task RunClientAsync(NamedPipeServerStream pipe)
{
    try { await HandleAsync(pipe); }
    finally { connections.Release(); }
}

async Task HandleAsync(NamedPipeServerStream pipe)
{
    using (pipe)
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
    {
        try
        {
            // Read a strictly bounded UTF-8 message, never an unbounded ReadLine.
            var bytes = new byte[16384]; int count = 0;
            while (count < bytes.Length)
            {
                int read = await pipe.ReadAsync(bytes.AsMemory(count, 1), timeout.Token);
                if (read == 0) return;
                if (bytes[count] == 10) break;
                ++count;
            }
            Reply reply;
            if (count == bytes.Length) reply = new(false, "Message exceeds 16 KiB.", engine.Status());
            else
            {
                try
                {
                    var command = JsonSerializer.Deserialize<Command>(Encoding.UTF8.GetString(bytes, 0, count));
                    reply = command is null ? new(false, "Empty command.", engine.Status()) : await engine.ExecuteAsync(command);
                }
                catch (JsonException) { reply = new(false, "Invalid JSON.", engine.Status()); }
            }
            var output = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply) + "\n");
            await pipe.WriteAsync(output, timeout.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
