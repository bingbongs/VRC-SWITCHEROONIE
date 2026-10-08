using Switcheroonie;

namespace Switcheroonie.Broker;

public static class OscWatchdog
{
    // A separate process owns the send-only UDP adapter. Broker/UI death expires
    // the memory lease and causes real OSC neutral packets while this process lives.
    public static async Task RunAsync(string? mapName = null)
    {
        var suffix = mapName is null ? "" : "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(mapName)));
        using var mutex = new Mutex(true, "Local\\VRC-SWITCHEROONIE-OSC-" + Identity.Sid + suffix, out bool owns);
        if (!owns) return;
        using var channel = new SharedChannel(mapName);
        using var sender = new OscSender();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        int port = 0, staleTicks = 0, neutralTicks = 0;
        while (await timer.WaitForNextTickAsync())
        {
            channel.PublishOscWatchdog();
            var lease = channel.ReadOscLease();
            if (lease.Fresh) staleTicks = 0;
            else if (++staleTicks > 300) return;
            try
            {
                // New helper must release values that a killed predecessor may
                // have left latched, even when the broker has already disarmed.
                if (lease.Enabled && lease.Port is > 0 and <= 65535 && (!sender.Enabled || port != lease.Port))
                { sender.Configure(true, "127.0.0.1", lease.Port); port = lease.Port; sender.ForceNeutral(); }
                if (!lease.Fresh || !lease.Enabled || !lease.Armed || !channel.Read().Alive)
                { sender.Release(repeat: ++neutralTicks <= 25); continue; }
                neutralTicks = 0;
                sender.Update(lease.Forward, lease.Strafe, lease.Actions);
            }
            catch (Exception e) when (e is ArgumentException or System.Net.Sockets.SocketException)
            { sender.Release(); }
        }
    }
}
