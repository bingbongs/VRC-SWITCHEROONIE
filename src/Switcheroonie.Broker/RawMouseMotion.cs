using System.Buffers.Binary;

namespace Switcheroonie.Broker;

// Pure packet/motion decoding. The Windows adapter supplies authenticated scope
// and physical display geometry; this class never reads a device or OS state.
internal readonly record struct RawMousePacket(long Device, ushort Flags, ushort Buttons, int X, int Y)
{
    internal const ushort Absolute = 1, VirtualDesktop = 2, AttributesChanged = 4;

    internal static bool TryRead(ReadOnlySpan<byte> bytes, int pointerBytes, out RawMousePacket packet)
    {
        packet = default;
        if (pointerBytes is not (4 or 8)) return false;
        int headerBytes = 8 + 2 * pointerBytes;
        if (bytes.Length < headerBytes + 24 || bytes.Length > 512 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length) return false;
        long device = pointerBytes == 8 ? BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]) : BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var mouse = bytes[headerBytes..];
        packet = new(device, BinaryPrimitives.ReadUInt16LittleEndian(mouse), BinaryPrimitives.ReadUInt16LittleEndian(mouse[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(mouse[12..]), BinaryPrimitives.ReadInt32LittleEndian(mouse[16..]));
        return true;
    }
}

internal readonly record struct RawMouseGeometry(CursorNative.Rect Primary, CursorNative.Rect Virtual, CursorNative.Rect Client)
{
    internal bool Valid => CursorNative.ValidRect(Primary) && CursorNative.ValidRect(Virtual) && CursorNative.ValidRect(Client);
}

internal sealed class RawMouseMotion(long frequency)
{
    internal const int MaximumDevices = 32;
    internal readonly record struct Delta(int X = 0, int Y = 0);
    struct DeviceState
    {
        internal bool Used, Baseline;
        internal long Device, Stamp;
        internal int X, Y;
        internal bool Virtual;
    }
    readonly DeviceState[] devices = new DeviceState[MaximumDevices];
    int replacement;
    long generation;
    RawMouseGeometry geometry;
    bool hasGeometry;
    long warpStamp;
    int warpX, warpY;
    internal ulong RelativePackets { get; private set; }
    internal ulong AbsolutePackets { get; private set; }
    internal ulong Rebaselines { get; private set; }
    internal ulong WarpSuppressed { get; private set; }
    internal long Generation => Volatile.Read(ref generation);
    internal bool IsCurrent(long capturedGeneration) => capturedGeneration == Generation;
    internal int TrackedDevices { get { int count = 0; foreach (var d in devices) if (d.Used) ++count; return count; } }

    internal void Reset()
    {
        ClearState(); Interlocked.Increment(ref generation);
    }
    void ClearState()
    {
        Array.Clear(devices); replacement = 0; hasGeometry = false; geometry = default; warpStamp = 0;
    }
    internal void CursorChanged()
    {
        for (int i = 0; i < devices.Length; ++i) devices[i].Baseline = false;
        warpStamp = 0;
        Interlocked.Increment(ref generation);
    }
    internal void ForgetDevice(long device)
    {
        for (int i = 0; i < devices.Length; ++i) if (devices[i].Used && devices[i].Device == device) devices[i] = default;
        Interlocked.Increment(ref generation);
    }
    internal bool SetGeometry(RawMouseGeometry next)
    {
        if (!next.Valid) { Reset(); return false; }
        bool changed = !hasGeometry || !geometry.Equals(next);
        if (changed) { ClearState(); geometry = next; hasGeometry = true; Interlocked.Increment(ref generation); }
        return changed;
    }
    internal void OwnWarp(int x, int y, long now)
    {
        CursorChanged();
        if (!hasGeometry || now <= 0 || frequency <= 0 || x < geometry.Virtual.Left || x >= geometry.Virtual.Right ||
            y < geometry.Virtual.Top || y >= geometry.Virtual.Bottom) return;
        warpX = x; warpY = y; warpStamp = now;
    }
    ref DeviceState Device(long key)
    {
        int empty = -1;
        for (int i = 0; i < devices.Length; ++i)
        {
            if (devices[i].Used && devices[i].Device == key) return ref devices[i];
            if (!devices[i].Used && empty < 0) empty = i;
        }
        int index = empty >= 0 ? empty : replacement;
        if (empty < 0) replacement = (replacement + 1) % devices.Length;
        devices[index] = new() { Used = true, Device = key };
        return ref devices[index];
    }
    static ulong Increment(ulong value) => value == ulong.MaxValue ? value : value + 1;
    internal static int Pixel(int value, int origin, long extent)
    {
        if (value is < 0 or > 65535 || extent is <= 0 or > 100000) throw new ArgumentOutOfRangeException(nameof(value));
        // Win32 documents normalized MulDiv mapping. Clamp its upper endpoint
        // to the final physical pixel, since display rectangles are exclusive.
        long offset = Math.Min(extent - 1, ((long)value * extent + 32767) / 65535);
        return checked((int)(origin + offset));
    }

    internal Delta Apply(RawMousePacket packet, RawMouseGeometry next, long now, bool authorized = true)
    {
        if (!authorized || frequency <= 0 || now <= 0 || !next.Valid) { Reset(); return new(); }
        SetGeometry(next);
        ref var d = ref Device(packet.Device);
        if ((packet.Flags & RawMousePacket.AttributesChanged) != 0) d.Baseline = false;
        if ((packet.Flags & RawMousePacket.Absolute) == 0)
        {
            RelativePackets = Increment(RelativePackets);
            // A button/wheel-only packet does not change the motion source.
            if (packet.X != 0 || packet.Y != 0) d.Baseline = false;
            return new(Math.Clamp(packet.X, -20000, 20000), Math.Clamp(packet.Y, -20000, 20000));
        }
        AbsolutePackets = Increment(AbsolutePackets);
        if (packet.X is < 0 or > 65535 || packet.Y is < 0 or > 65535) { d.Baseline = false; return new(); }
        bool virtualDesktop = (packet.Flags & RawMousePacket.VirtualDesktop) != 0;
        var rectangle = virtualDesktop ? geometry.Virtual : geometry.Primary;
        long width = (long)rectangle.Right - rectangle.Left, height = (long)rectangle.Bottom - rectangle.Top;
        int x = Pixel(packet.X, rectangle.Left, width), y = Pixel(packet.Y, rectangle.Top, height);
        // Matching our bounded center mutation cannot be interpreted as user
        // look. Rebaseline AFTER it, so a tablet's independent coordinates also
        // cannot jump from the synthetic center back to its physical position.
        if (warpStamp > 0 && now >= warpStamp && now - warpStamp <= Math.Max(1, frequency / 10) &&
            Math.Abs((long)x - warpX) <= Math.Max(1, (width + 65534) / 65535) &&
            Math.Abs((long)y - warpY) <= Math.Max(1, (height + 65534) / 65535))
        {
            WarpSuppressed = Increment(WarpSuppressed); d.Baseline = false; d.Stamp = now; return new();
        }
        bool baseline = !d.Baseline || d.Virtual != virtualDesktop || now < d.Stamp ||
            now - d.Stamp > Math.Max(1, frequency / 4);
        var delta = baseline ? new Delta() : new(Math.Clamp(x - d.X, -20000, 20000), Math.Clamp(y - d.Y, -20000, 20000));
        if (baseline) Rebaselines = Increment(Rebaselines);
        d.Baseline = true; d.X = x; d.Y = y; d.Virtual = virtualDesktop; d.Stamp = now;
        return delta;
    }
}
