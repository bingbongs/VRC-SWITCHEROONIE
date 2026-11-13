using System.Buffers.Binary;
using Switcheroonie.Broker;

// Pure SDK-layout packet fixtures. No WindowsGameInput construction, device
// queries, cursor operations, process launch, hooks or other OS input calls.
internal static class RawMousePacketTests
{
    internal static void Run(Action<bool, string> check)
    {
        var primary = new RawMouseGeometry(new(0, 0, 1920, 1080), new(-1920, -1080, 3840, 2160), new(100, 100, 1100, 700));
        var identity = new RawMouseGeometry(new(0, 0, 65535, 65535), new(0, 0, 65535, 65535), new(100, 100, 1100, 700));
        var packet = Packet(8, 7, 1, 1, 32768, 32768);
        check(RawMousePacket.TryRead(packet, 8, out var decoded) && decoded.Device == 7 && decoded.Flags == 1 &&
            decoded.Buttons == 1 && decoded.X == 32768 && decoded.Y == 32768,
            "RAWMOUSE x64 packet reads device, absolute coordinates and left-down independently");
        check(RawMousePacket.TryRead(Packet(4, uint.MaxValue, 3, 2, 12345, 54321), 4, out var x86) &&
            x86.Device == -1 && x86.Flags == 3 && x86.Buttons == 2 && x86.X == 12345 && x86.Y == 54321,
            "RAWMOUSE x86 header matches IntPtr device notification identity and virtual-desktop packet fields");
        check(RawMousePacket.TryRead(Packet(8, 0, 1, 0, 100, 200), 8, out var anonymous) && anonymous.Device == 0,
            "Precision-touchpad zero device handle is valid without probing a device name");
        check(!RawMousePacket.TryRead(packet.AsSpan(0, packet.Length - 1), 8, out _), "Truncated raw packet is rejected");
        var malformed = packet.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(malformed, 1);
        check(!RawMousePacket.TryRead(malformed, 8, out _), "Raw keyboard data is not decoded as mouse motion");
        malformed = packet.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(4), 512);
        check(!RawMousePacket.TryRead(malformed, 8, out _), "Header length must match the copied raw packet");
        check(!RawMousePacket.TryRead(packet, 16, out _), "Unsupported raw header pointer layout is rejected");
        check(!RawMousePacket.TryRead(new byte[513], 8, out _), "Raw packet buffer limit remains bounded");

        var motion = new RawMouseMotion(1000);
        check(motion.Apply(decoded, primary, 100) == new RawMouseMotion.Delta() && decoded.Buttons == 1,
            "First absolute position establishes baseline without swallowing its real activation button");
        RawMousePacket.TryRead(Packet(8, 7, 1, 0, 33451, 33982), 8, out var moved);
        var delta = motion.Apply(moved, primary, 101);
        // The former adapter's `if ((flags & 1) == 0)` emits zero for this real
        // packet pair. These literal pixel expectations reproduce that fault.
        check(delta == new RawMouseMotion.Delta(20, 20), "An absolute-only normalized packet stream produces real pixel look deltas");
        var control = new GameInputController();
        control.Step(new(Window: 10, Focused: true, ActivateClick: true), true, 1);
        var look = control.Step(new(Window: 10, Focused: true, DeltaX: delta.X, DeltaY: delta.Y), true, 1);
        check(look.Active && Math.Abs(look.Yaw - .05) < 1e-12 && Math.Abs(look.Pitch + .05) < 1e-12 && look.HandYaw == 0,
            "Decoded absolute mouse moves normal Desktop head look without holding a mouse button");
        control.Step(new(Window: 10, Focused: true, RaiseMenu: true), true, 1);
        var aim = control.Step(new(Window: 10, Focused: true, DeltaX: delta.X, DeltaY: delta.Y), true, 1);
        check(aim.Pointer && aim.Yaw == 0 && aim.Pitch == 0 && Math.Abs(aim.HandYaw - .05) < 1e-12 && Math.Abs(aim.HandPitch + .05) < 1e-12,
            "Decoded absolute mouse moves menu hand aim while keeping the view still");
        check(RawMouseMotion.Pixel(0, 0, 1920) == 0 && RawMouseMotion.Pixel(65535, 0, 1920) == 1919 &&
            RawMouseMotion.Pixel(32768, 0, 1920) == 960,
            "Normalized endpoints stay on physical pixels with documented MulDiv-style midpoint mapping");
        check(RawMouseMotion.Pixel(0, -1920, 5760) == -1920 && RawMouseMotion.Pixel(65535, -1920, 5760) == 3839 &&
            RawMouseMotion.Pixel(32768, -1920, 5760) == 960,
            "Virtual-desktop endpoints include the negative origin and remain inside the exclusive rectangle");
        motion = new(1000);
        motion.Apply(new(1, 3, 0, 32768, 32768), primary, 100);
        check(motion.Apply(new(1, 3, 0, 32996, 33173), primary, 101) == new RawMouseMotion.Delta(20, 20),
            "Absolute virtual-desktop motion uses the combined display extent rather than primary width");
        check(motion.Apply(new(1, 1, 0, 33451, 33982), primary, 102) == new RawMouseMotion.Delta(),
            "Changing absolute coordinate universe rebaselines instead of jumping across monitors");
        check(motion.Apply(new(1, 1, 0, 34133, 35195), primary, 103) == new RawMouseMotion.Delta(20, 20),
            "A fresh primary-display baseline resumes normal absolute deltas");

        motion = new(1000);
        motion.Apply(new(1, 1, 0, 1000, 1000), identity, 100);
        motion.Apply(new(2, 1, 0, 50000, 50000), identity, 101);
        check(motion.Apply(new(1, 1, 0, 1010, 1005), identity, 102) == new RawMouseMotion.Delta(10, 5) &&
            motion.Apply(new(2, 1, 0, 49995, 50002), identity, 103) == new RawMouseMotion.Delta(-5, 2),
            "Interleaved absolute devices each retain their own baseline");
        check(motion.Apply(new(1, 0, 0, 3, -4), identity, 104) == new RawMouseMotion.Delta(3, -4),
            "Relative input keeps its existing signed motion semantics");
        check(motion.Apply(new(1, 1, 0, 60000, 60000), identity, 105) == new RawMouseMotion.Delta() &&
            motion.Apply(new(1, 1, 0, 60010, 60002), identity, 106) == new RawMouseMotion.Delta(10, 2),
            "Relative-to-absolute transition cannot reuse an obsolete absolute origin");
        motion.Apply(new(1, 0, 0x400, 0, 0), identity, 107);
        check(motion.Apply(new(1, 1, 0, 60012, 60003), identity, 108) == new RawMouseMotion.Delta(2, 1),
            "A relative button or wheel-only packet does not manufacture a motion-source transition");
        check(motion.Apply(new(1, 0, 0, int.MaxValue, int.MinValue), identity, 109) == new RawMouseMotion.Delta(20000, -20000),
            "Pathological relative packet deltas remain bounded before controller conversion");
        motion.ForgetDevice(1);
        check(motion.Apply(new(1, 1, 0, 100, 100), identity, 110) == new RawMouseMotion.Delta(),
            "Device removal and same-handle arrival require a new baseline");
        check(motion.Apply(new(2, 1, 0, 49997, 50005), identity, 111) == new RawMouseMotion.Delta(2, 3),
            "Removing one device does not alter another device's baseline");
        motion = new(1000);
        motion.Apply(new(0, 1, 0, 1000, 1000), identity, 100);
        check(motion.Apply(new(0, 1, 0, 1015, 1007), identity, 101) == new RawMouseMotion.Delta(15, 7),
            "Anonymous precision-touchpad source can move after its safe first baseline");

        motion = new(1000);
        motion.Apply(new(1, 1, 0, 1000, 1000), identity, 100);
        check(motion.Apply(new(1, 1, 0, -1, 1000), identity, 101) == new RawMouseMotion.Delta(),
            "Negative absolute coordinates are rejected rather than treated as signed motion");
        check(motion.Apply(new(1, 1, 0, 65536, 1000), identity, 102) == new RawMouseMotion.Delta(),
            "Out-of-range normalized absolute coordinates are rejected");
        check(motion.Apply(new(1, 1, 0, 1005, 1005), identity, 103) == new RawMouseMotion.Delta() &&
            motion.Apply(new(1, 1, 0, 1010, 1010), identity, 104) == new RawMouseMotion.Delta(5, 5),
            "Malformed absolute input clears only its obsolete baseline and valid motion recovers");
        check(motion.Apply(new(1, 5, 0, 50000, 50000), identity, 105) == new RawMouseMotion.Delta() &&
            motion.Apply(new(1, 1, 0, 50003, 50004), identity, 106) == new RawMouseMotion.Delta(3, 4),
            "MOUSE_ATTRIBUTES_CHANGED safely rebaselines a changed device");
        var resizedClient = identity with { Client = new(-100, 200, 1200, 900) };
        check(motion.Apply(new(1, 1, 0, 60000, 60000), resizedClient, 107) == new RawMouseMotion.Delta(),
            "Client resize or screen translation cannot carry deltas from old capture geometry");
        var displayChanged = resizedClient with { Primary = new(0, 0, 32768, 32768) };
        check(motion.Apply(new(1, 1, 0, 30000, 30000), displayChanged, 108) == new RawMouseMotion.Delta(),
            "Primary display geometry change requires a fresh normalized baseline");
        var virtualChanged = displayChanged with { Virtual = new(-10000, -10000, 65535, 65535) };
        check(motion.Apply(new(1, 3, 0, 40000, 40000), virtualChanged, 109) == new RawMouseMotion.Delta(),
            "Virtual display origin or extent change cannot manufacture motion");
        check(motion.Apply(new(1, 1, 0, 10, 10), default, 110) == new RawMouseMotion.Delta() && motion.TrackedDevices == 0,
            "Lost geometry drops motion and all old device baselines");
        check(motion.Apply(new(1, 1, 0, 1000, 1000), identity, 111) == new RawMouseMotion.Delta(),
            "Recovered geometry begins without an initial jump");
        ulong packetCount = motion.AbsolutePackets;
        check(motion.Apply(new(1, 1, 0, 50000, 50000), identity, 112, authorized: false) == new RawMouseMotion.Delta() &&
            motion.AbsolutePackets == packetCount && motion.TrackedDevices == 0,
            "Focus or typing authority loss discards motion without counting an unrelated packet");
        check(motion.Apply(new(1, 1, 0, 60000, 60000), identity, 113) == new RawMouseMotion.Delta(),
            "Fresh focused scope cannot reuse a pre-focus absolute position");
        check(motion.Apply(new(1, 1, 0, 100, 100), identity, 400) == new RawMouseMotion.Delta(),
            "A stale device stream rebaselines after its bounded gap");
        check(motion.Apply(new(1, 1, 0, 60000, 60000), identity, 399) == new RawMouseMotion.Delta(),
            "A backward packet clock cannot turn a previous baseline into a jump");

        motion = new(1000);
        motion.Apply(new(1, 1, 0, 20000, 20000), identity, 99);
        motion.OwnWarp(10000, 10000, 100);
        check(motion.Apply(new(1, 1, 0, 20005, 20005), identity, 101) == new RawMouseMotion.Delta(),
            "An owned cursor mutation invalidates pre-mutation queued motion baselines");
        check(motion.Apply(new(1, 1, 0, 10000, 10000), identity, 102) == new RawMouseMotion.Delta() && motion.WarpSuppressed == 1,
            "Absolute feedback matching the owned center warp emits no look movement");
        check(motion.Apply(new(1, 1, 0, 20010, 20010), identity, 103) == new RawMouseMotion.Delta() &&
            motion.Apply(new(1, 1, 0, 20012, 20013), identity, 104) == new RawMouseMotion.Delta(2, 3),
            "Tablet coordinates returning from a synthetic center establish baseline before resuming motion");
        check(motion.Apply(new(1, 1, 0, 10001, 9999), identity, 105) == new RawMouseMotion.Delta() && motion.WarpSuppressed == 2,
            "Repeated quantized center feedback remains suppressed within the bounded window");
        motion = new(1000); motion.SetGeometry(identity); motion.OwnWarp(10000, 10000, 100);
        motion.Apply(new(1, 1, 0, 12000, 12000), identity, 201);
        check(motion.Apply(new(1, 1, 0, 10000, 10000), identity, 202) == new RawMouseMotion.Delta(-2000, -2000) && motion.WarpSuppressed == 0,
            "Expired warp marker cannot suppress later genuine movement through that point");
        motion.OwnWarp(10000, 10000, 203); motion.CursorChanged();
        motion.Apply(new(1, 1, 0, 12000, 12000), identity, 204);
        check(motion.Apply(new(1, 1, 0, 10000, 10000), identity, 205) == new RawMouseMotion.Delta(-2000, -2000),
            "Pointer or release transition clears a look-only pending center marker");
        motion.Reset();
        check(motion.Apply(new(1, 1, 0, 10000, 10000), identity, 206) == new RawMouseMotion.Delta() && motion.TrackedDevices == 1,
            "Explicit scope reset removes old warp and device state");
        check(motion.AbsolutePackets > 0 && motion.Rebaselines > 0 && motion.RelativePackets == 0,
            "Packet diagnostics count only categories, safe baselines and matching suppressed feedback");

        motion = new(1000);
        for (int i = 0; i <= RawMouseMotion.MaximumDevices; ++i) motion.Apply(new(i, 1, 0, 1000, 1000), identity, 100 + i);
        check(motion.TrackedDevices == 32, "Per-device motion cache cannot grow beyond 32 device slots");
        check(motion.Apply(new(0, 1, 0, 50000, 50000), identity, 140) == new RawMouseMotion.Delta(),
            "An evicted device returns with a fresh baseline instead of another device's position");
        check(motion.Apply(new(32, 1, 0, 1010, 1020), identity, 141) == new RawMouseMotion.Delta(10, 20),
            "Bounded eviction preserves the remaining device's independent baseline");
        var badClock = new RawMouseMotion(0);
        check(badClock.Apply(new(1, 0, 0, 3, 4), identity, 100) == new RawMouseMotion.Delta(),
            "Unavailable timing authority cannot admit mouse movement");
        var oversized = identity with { Virtual = new(int.MinValue, 0, int.MaxValue, 1080) };
        check(motion.Apply(new(1, 3, 0, 65535, 65535), oversized, 142) == new RawMouseMotion.Delta() && motion.TrackedDevices == 0,
            "Overflow-sized display geometry is rejected using long extent arithmetic");
        var reversed = identity with { Primary = new(1920, 0, 0, 1080) };
        check(motion.Apply(new(1, 1, 0, 65535, 65535), reversed, 143) == new RawMouseMotion.Delta(),
            "Reversed physical display geometry cannot admit normalized motion");
        var onePixel = identity with { Primary = new(0, 0, 1, 1) };
        motion.Apply(new(1, 1, 0, 0, 0), onePixel, 144);
        check(motion.Apply(new(1, 1, 0, 65535, 65535), onePixel, 145) == new RawMouseMotion.Delta(),
            "Single-pixel extent maps both normalized endpoints to its only physical pixel");
        long stalePacket = motion.Generation;
        motion.Reset();
        check(!motion.IsCurrent(stalePacket), "A parsed old raw packet cannot publish activation or motion after a scope reset");
        long stablePacket = motion.Generation;
        motion.SetGeometry(identity);
        stablePacket = motion.Generation;
        motion.SetGeometry(identity);
        check(motion.IsCurrent(stablePacket), "Unchanged geometry heartbeats do not invalidate a legitimate pending raw packet");
        motion.CursorChanged();
        check(!motion.IsCurrent(stablePacket), "An owned cursor or capture-mode transition invalidates pre-transition packets");
        stablePacket = motion.Generation;
        motion.ForgetDevice(1);
        check(!motion.IsCurrent(stablePacket), "Device notification invalidates packets parsed before its source boundary");
    }

    static byte[] Packet(int pointerBytes, long device, ushort flags, ushort buttons, int x, int y)
    {
        int header = 8 + pointerBytes * 2;
        byte[] packet = new byte[header + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
        if (pointerBytes == 8) BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(8), device);
        else BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)device);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(header), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(header + 4), buttons);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(header + 12), x);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(header + 16), y);
        return packet;
    }
}
