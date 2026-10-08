using System.Diagnostics;
using System.Runtime.InteropServices;
using Switcheroonie;

namespace Switcheroonie.Broker;

// Each aligned payload word is atomic, with a bounded seqlock snapshot.
// This layout is shared verbatim with native/backend-api/protocol.h.
public sealed unsafe class SharedChannel : IDisposable
{
    const uint Magic = 0x53575243;
    IntPtr handle;
    byte* view;
    public SharedChannel(string? name = null)
    {
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;" + Identity.Sid + ")", 1, out descriptor, out _)) throw new System.ComponentModel.Win32Exception();
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            handle = CreateFileMapping(new IntPtr(-1), ref security, 4, 0, 4096, name ?? Identity.MapName);
            if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            view = (byte*)MapViewOfFile(handle, 0xF001F, 0, 0, 4096);
            if (view == null) throw new System.ComponentModel.Win32Exception();
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }
    static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
    void Put(int offset, long value) => Interlocked.Exchange(ref *(long*)(view + offset), value);
    long Get(int offset) => Interlocked.Read(ref *(long*)(view + offset));
    public bool OscWatchdogAlive => Get(3080) > 0 && Stopwatch.GetElapsedTime(Get(3080)).TotalMilliseconds is >= 0 and < 200;
    public void PublishOscWatchdog() => Put(3080, Stopwatch.GetTimestamp());
    public void Publish(ulong epoch, bool desktop, bool armed, double height, double yaw, double pitch, double handYaw, double handPitch, int preset, uint actions, double forward, double strafe,
        bool osc = false, int oscPort = 9000, double oscForward = 0, double oscStrafe = 0, uint oscActions = 0,
        SpinController? spin = null)
    {
        long previous = Get(0) & ~1L;
        Put(0, previous + 1);
        Put(8, ((long)1 << 32) | Magic);
        Put(16, Stopwatch.GetTimestamp()); Put(24, unchecked((long)epoch));
        Put(32, (armed ? 1L << 32 : 0) | (desktop ? 1L : 0));
        Put(40, Bits(height)); Put(48, Bits(yaw)); Put(56, Bits(pitch));
        Put(64, Bits(handYaw)); Put(72, Bits(handPitch));
        Put(80, ((long)actions << 32) | (uint)preset); Put(88, Bits(forward)); Put(96, Bits(strafe));
        Put(104, (osc ? 1L : 0) | ((long)oscPort << 32)); Put(112, Bits(oscForward)); Put(120, Bits(oscStrafe)); Put(128, oscActions);
        bool spinning = spin?.Active == true && spin.Rotation.Valid;
        Put(136, spinning ? 1 : 0); Put(144, spinning ? Stopwatch.GetTimestamp() : 0);
        var rotation = spinning ? spin!.Rotation : SpinQuaternion.Identity;
        Put(152, Bits(rotation.W)); Put(160, Bits(rotation.X)); Put(168, Bits(rotation.Y)); Put(176, Bits(rotation.Z));
        Put(184, Bits(spinning ? spin!.PivotX : 0)); Put(192, Bits(spinning ? spin!.PivotY : 0)); Put(200, Bits(spinning ? spin!.PivotZ : 0));
        Put(208, spinning ? unchecked((long)spin!.Generation) : 0);
        Put(0, previous + 2);
    }
    public OscLease ReadOscLease()
    {
        Span<long> words = stackalloc long[17];
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            long sequence = Get(0);
            if ((sequence & 1) != 0) continue;
            for (int i = 1; i < words.Length; ++i) words[i] = Get(i * 8);
            if (Get(0) != sequence) continue;
            long elapsed = Stopwatch.GetTimestamp() - words[2];
            bool valid = (uint)words[1] == Magic && (uint)(words[1] >> 32) == 1 && elapsed >= 0 && elapsed < Stopwatch.Frequency / 5;
            double forward = BitConverter.Int64BitsToDouble(words[14]), strafe = BitConverter.Int64BitsToDouble(words[15]);
            valid &= double.IsFinite(forward) && double.IsFinite(strafe) && Math.Abs(forward) <= 1 && Math.Abs(strafe) <= 1 && (words[16] & ~24L) == 0;
            return new(valid, (uint)words[13] == 1, (uint)(words[4] >> 32) == 1 && (uint)words[4] == 1,
                (int)(words[13] >> 32), forward, strafe, (uint)words[16]);
        }
        return new();
    }
    public PoseIntent? ReadPoseIntent(ulong expectedEpoch, bool expectedDesktop)
    {
        Span<long> words = stackalloc long[11];
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            long sequence = Get(0);
            if ((sequence & 1) != 0) continue;
            for (int i = 1; i < words.Length; ++i) words[i] = Get(i * 8);
            if (Get(0) != sequence) continue;
            if ((uint)words[1] != Magic || (uint)(words[1] >> 32) != 1 || unchecked((ulong)words[3]) != expectedEpoch ||
                (uint)words[4] != (expectedDesktop ? 1u : 0u)) return null;
            var pose = new PoseIntent(BitConverter.Int64BitsToDouble(words[5]), BitConverter.Int64BitsToDouble(words[6]),
                BitConverter.Int64BitsToDouble(words[7]), BitConverter.Int64BitsToDouble(words[8]), BitConverter.Int64BitsToDouble(words[9]));
            return pose.Valid ? pose : null;
        }
        return null;
    }
    public DriverSnapshot Read()
    {
        Span<long> words = stackalloc long[39];
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            long sequence = Get(2048);
            if ((sequence & 1) != 0) continue;
            for (int i = 1; i < words.Length; ++i) words[i] = Get(2048 + i * 8);
            if (Get(2048) != sequence) continue;
            if ((uint)words[1] != Magic || (uint)(words[1] >> 32) != 1) return new();
            long elapsed = Stopwatch.GetTimestamp() - words[2];
            bool alive = elapsed >= 0 && elapsed < Stopwatch.Frequency / 2;
            return new(alive, unchecked((ulong)words[3]), (uint)words[4] == 1, (uint)(words[4] >> 32),
                BitConverter.Int64BitsToDouble(words[5]), (uint)words[6] != 0, (uint)(words[6] >> 32) != 0,
                (uint)words[7] != 0, unchecked((ulong)words[15]), unchecked((ulong)words[16]), (uint)(words[7] >> 32),
                (uint)words[28], (uint)(words[28] >> 32), unchecked((ulong)words[29]), (uint)words[30] == 1,
                (uint)(words[30] >> 32), (uint)words[31], (uint)(words[31] >> 32) == 1,
                (uint)words[32] == 1, (uint)(words[32] >> 32) == 1,
                BitConverter.Int64BitsToDouble(words[33]), BitConverter.Int64BitsToDouble(words[34]), BitConverter.Int64BitsToDouble(words[35]),
                BitConverter.Int64BitsToDouble(words[8]), BitConverter.Int64BitsToDouble(words[9]), BitConverter.Int64BitsToDouble(words[10]),
                BitConverter.Int64BitsToDouble(words[11]), BitConverter.Int64BitsToDouble(words[12]), BitConverter.Int64BitsToDouble(words[13]), BitConverter.Int64BitsToDouble(words[14]),
                (uint)words[36] == 1, unchecked((ulong)words[37]), unchecked((ulong)words[38]));
        }
        return new();
    }
    public void Dispose() { if (view != null) { UnmapViewOfFile((IntPtr)view); view = null; } if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr result, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileMapping(IntPtr file, ref SecurityAttributes attributes, uint protection, uint high, uint low, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, nuint size);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr address);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
}

public readonly record struct DriverSnapshot(bool Alive = false, ulong Epoch = 0, bool Desktop = false, uint Error = 0,
    double HeadAge = double.PositiveInfinity, bool HasHead = false, bool HasLeft = false, bool HasRight = false,
    ulong RoutedSamples = 0, ulong PhysicalSamples = 0, uint CapabilityFlags = 0,
    uint InputCoverage = 0, uint SelectedMenuPath = 0, ulong MenuRisingEdges = 0, bool MenuPressed = false,
    uint LastInputError = 0, uint EffectiveNativeActions = 0, bool InputArmed = false,
    bool ProximityKnown = false, bool ProximityActive = false, double LeftAge = -1, double RightAge = -1, double ProximityAge = -1,
    double HeadX = 0, double HeadY = 0, double HeadZ = 0, double HeadW = 0, double HeadQx = 0, double HeadQy = 0, double HeadQz = 0,
    bool SpinActive = false, ulong SpinGeneration = 0, ulong SpinSamples = 0)
{
    public bool PhysicalPoseValid => Alive && HasHead && HeadAge is >= 0 and < 200 &&
        double.IsFinite(HeadX) && double.IsFinite(HeadY) && double.IsFinite(HeadZ) &&
        Math.Abs(HeadX) <= 10000 && Math.Abs(HeadY) <= 10000 && Math.Abs(HeadZ) <= 10000 &&
        new SpinQuaternion(HeadW, HeadQx, HeadQy, HeadQz).Valid;
}
public readonly record struct OscLease(bool Fresh = false, bool Enabled = false, bool Armed = false, int Port = 9000, double Forward = 0, double Strafe = 0, uint Actions = 0);
public readonly record struct PoseIntent(double Height, double Yaw, double Pitch, double HandYaw, double HandPitch)
{
    public bool Valid => double.IsFinite(Height) && Math.Abs(Height) <= 1.5 && double.IsFinite(Yaw) && Math.Abs(Yaw) <= Math.PI &&
        double.IsFinite(Pitch) && Math.Abs(Pitch) <= Math.PI / 2 && double.IsFinite(HandYaw) && Math.Abs(HandYaw) <= Math.PI * 4 &&
        double.IsFinite(HandPitch) && Math.Abs(HandPitch) <= Math.PI / 2;
}
