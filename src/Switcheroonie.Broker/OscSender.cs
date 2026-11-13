using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Switcheroonie.Broker;

public sealed class OscSender : IDisposable
{
    readonly UdpClient socket = new();
    IPEndPoint? endpoint;
    readonly Dictionary<string, float> values = new();
    public bool Enabled => endpoint is not null;
    public void Configure(bool enabled, string? destination, int port)
    {
        if (enabled && (!IPAddress.TryParse(destination, out var ip) || !IPAddress.IsLoopback(ip) || port is < 1 or > 65535)) throw new ArgumentException("OSC requires a numeric loopback address and port 1–65535.");
        Release();
        endpoint = enabled ? new IPEndPoint(IPAddress.Parse(destination!), port) : null;
    }
    public static byte[] Encode(string address, float value, bool integer)
    {
        var name = Encoding.ASCII.GetBytes(address);
        int padded = (name.Length + 4) & ~3;
        byte[] packet = new byte[padded + 8];
        name.CopyTo(packet, 0); packet[padded] = (byte)','; packet[padded + 1] = (byte)(integer ? 'i' : 'f');
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(padded + 4), integer ? (int)value : BitConverter.SingleToInt32Bits(value));
        return packet;
    }
    void Set(string name, float value, bool integer)
    {
        if (endpoint is null) return;
        if (values.TryGetValue(name, out var previous) && previous == value) return;
        var packet = Encode(name, value, integer);
        socket.Send(packet, packet.Length, endpoint); values[name] = value;
    }
    public void Update(double forward, double strafe, uint actions)
    {
        Set("/input/Vertical", (float)forward, false); Set("/input/Horizontal", (float)strafe, false);
        Set("/input/Jump", (actions & 8) != 0 ? 1 : 0, true); Set("/input/Run", (actions & 16) != 0 ? 1 : 0, true);
        // Pointer/grab/menu always use native controller routing. Never duplicate them in OSC.
    }
    public void ForceNeutral()
    {
        if (endpoint is null) return;
        foreach (var name in new[] { "/input/Vertical", "/input/Horizontal", "/input/Jump", "/input/Run" })
        {
            var packet = Encode(name, 0, name is not "/input/Vertical" and not "/input/Horizontal");
            socket.Send(packet, packet.Length, endpoint); values[name] = 0;
        }
    }
    public void Release(bool repeat = false)
    {
        if (endpoint is null) { values.Clear(); return; }
        foreach (var name in values.Keys.ToArray())
        {
            if (repeat) { var packet = Encode(name, 0, name is not "/input/Vertical" and not "/input/Horizontal"); socket.Send(packet, packet.Length, endpoint); values[name] = 0; }
            else Set(name, 0, name is not "/input/Vertical" and not "/input/Horizontal");
        }
        if (!repeat) values.Clear();
    }
    public void Dispose() { try { Release(); } finally { socket.Dispose(); } }
}
