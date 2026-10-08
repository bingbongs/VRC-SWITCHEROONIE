using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace Switcheroonie;

public static class Identity
{
    public static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    public static string PipeName => "VRC-SWITCHEROONIE-" + Sid;
    public static string MapName => "Local\\VRC-SWITCHEROONIE-" + Sid;
    public static string DataDirectory => StatePaths.Current.DataDirectory;
    public static string ConfigurationDirectory => StatePaths.Current.ConfigurationDirectory;
    public static string ReportsDirectory => StatePaths.Current.ReportsDirectory;
}

public sealed record Command
{
    public int Version { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "GetStatus";
    public string? Mode { get; init; }
    public long Window { get; init; }
    public double Height { get; init; }
    public double Yaw { get; init; }
    public double Pitch { get; init; }
    public double HandYaw { get; init; }
    public double HandPitch { get; init; }
    public int Preset { get; init; }
    public uint Actions { get; init; }
    public bool Armed { get; init; }
    public bool Osc { get; init; }
    public bool Enabled { get; init; }
    public double Sensitivity { get; init; } = 1;
    public string? Destination { get; init; }
    public int Port { get; init; } = 9000;
}

public sealed record HarnessStatus
{
    public string ServiceState { get; init; } = "Ready";
    public bool RoutingReady { get; init; }
    public bool AutomaticEnabled { get; init; } = true;
    public string? ManualMode { get; init; }
    public string AdaptationDetail { get; init; } = "Waiting for the selected headset route.";
    public bool HeadsetWornKnown { get; init; }
    public bool HeadsetWorn { get; init; }
    public string State { get; init; } = "Idle";
    public string Mode { get; init; } = "Physical";
    public string Runtime { get; init; } = "No driver heartbeat";
    public string Tracking { get; init; } = "Unknown";
    public string Display { get; init; } = "Vendor display path; headset presentation unverified";
    public string Input { get; init; } = "Released";
    public bool DriverAlive { get; init; }
    public bool HasHead { get; init; }
    public bool HasLeft { get; init; }
    public bool HasRight { get; init; }
    public bool Armed { get; init; }
    public bool GameInputEnabled { get; init; }
    public bool GameInputActive { get; init; }
    public bool GameCursorCaptured { get; init; }
    public bool GameCursorHidden { get; init; }
    public bool GameCursorWatchdogAlive { get; init; }
    public bool MenuNavigation { get; init; }
    public bool SpinEnabled { get; init; }
    public bool SpinActive { get; init; }
    public double SpinRollSpeed { get; init; }
    public double SpinPitchSpeed { get; init; }
    public bool NativeSpinActive { get; init; }
    public double GameSensitivity { get; init; } = 1;
    public string GameInputDetail { get; init; } = "Click into VRChat to use desktop controls.";
    public string InputOwner { get; init; } = "Released";
    public long OwnerWindow { get; init; }
    public bool OscEnabled { get; init; }
    public bool OscWatchdogAlive { get; init; }
    public ulong Epoch { get; init; }
    public double Height { get; init; }
    public double HeadAgeMilliseconds { get; init; }
    public ulong PhysicalSamples { get; init; }
    public ulong RoutedSamples { get; init; }
    public uint NativeCapabilityFlags { get; init; }
    public uint NativeInputCoverage { get; init; }
    public uint NativeMenuPath { get; init; }
    public ulong NativeMenuRisingEdges { get; init; }
    public bool NativeMenuPressed { get; init; }
    public uint NativeLastInputError { get; init; }
    public uint NativeEffectiveActions { get; init; }
    public bool NativeInputArmed { get; init; }
    public string Detail { get; init; } = "Install and validate the experimental SteamVR component before switching.";
    public string ColdStart { get; init; } = "Switcheroonie can stay ready without a headset. Keeping a headset-free VRChat session through headset attachment is still under development.";
    public string Evidence { get; init; } = "Hardware validation pending";
}

public sealed record Reply(bool Accepted, string Reason, HarnessStatus Status);

public sealed class BrokerClient
{
    public async Task<Reply> SendAsync(Command command, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", Identity.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(1000, cancellationToken);
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(command));
        var buffer = new char[32768]; int count = 0;
        while (count < buffer.Length)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(count, 1), cancellationToken);
            if (read == 0) throw new IOException("Broker closed the connection");
            if (buffer[count] == '\n')
            {
                try { var reply = JsonSerializer.Deserialize<Reply>(new string(buffer, 0, count)); return reply?.Status is null ? throw new IOException("Invalid broker response") : reply; }
                catch (JsonException e) { throw new IOException("Invalid broker response", e); }
            }
            ++count;
        }
        throw new IOException("Oversized broker response");
    }
}
