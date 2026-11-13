using System.Text.Json;
using Switcheroonie;

var verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
var command = verb switch
{
    "status" => new Command(),
    "release" => new Command { Name = "ReleaseInputs" },
    "physical" => new Command { Name = "RequestMode", Mode = "Physical" },
    "desktop" => new Command { Name = "RequestMode", Mode = "Desktop" },
    "auto" => new Command { Name = "ConfigureAutomatic", Enabled = true },
    "pause-auto" => new Command { Name = "ConfigureAutomatic", Enabled = false },
    "diagnostics" => new Command { Name = "ExportDiagnostics" },
    _ => null
};
if (command is null) { Console.Error.WriteLine("Commands: status, release, physical, desktop, auto, pause-auto, diagnostics. Driver disable/unregister: package tools\\Manage-Driver.ps1."); return 2; }
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var reply = await new BrokerClient().SendAsync(command, timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(reply, new JsonSerializerOptions { WriteIndented = true }));
    return reply.Accepted ? 0 : 1;
}
catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
{ Console.Error.WriteLine("Broker unavailable. Driver falls back to physical passthrough after its 200 ms lease. " + e.Message); return 3; }
