using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Switcheroonie;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Switcheroonie.Tests")]

namespace Switcheroonie.Broker;

// Separate current-user-only mapping; no native driver or OSC protocol offsets are reused.
public static class CursorWatchdog
{
    public static async Task RunAsync(string name, int parentPid)
    {
        if (!CursorLeaseMap.ValidName(name) || parentPid <= 0 || parentPid == Environment.ProcessId) return;
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            using var current = Process.GetCurrentProcess();
            string? ownImage = Environment.ProcessPath;
            if (ownImage is null || !Path.GetFileNameWithoutExtension(ownImage).Equals("Switcheroonie.Broker", StringComparison.OrdinalIgnoreCase) ||
                parent.SessionId != current.SessionId || !string.Equals(parent.MainModule?.FileName, ownImage, StringComparison.OrdinalIgnoreCase)) return;
            using var map = new CursorLeaseMap(name, false, parentPid);
            if (map.WriterPid != parentPid) return;
            CursorLease lastCommitted = new();
            while (true)
            {
                map.PublishHelperHeartbeat();
                var lease = map.Read();
                if (lease.Valid) lastCommitted = lease;
                else lease = lastCommitted;
                bool exited = parent.HasExited;
                bool expired = !lease.Valid || !CursorLeaseMap.Fresh(lease.Timestamp, 200);
                bool focusLost = lease.Armed && (CursorNative.GetForegroundWindow().ToInt64() != lease.Window ||
                    CursorNative.GetWindowThreadProcessId(new IntPtr(lease.Window), out uint pid) == 0 || pid != lease.GamePid);
                if (lease.Armed && (exited || expired || focusLost || lease.Shutdown))
                {
                    var latest = map.Read();
                    if (latest.Valid && latest.Generation != lease.Generation) { await Task.Delay(10); continue; }
                    if (latest.Valid) lease = latest;
                    if (!CursorRecovery.ShouldRecover(lease, lastCommitted, exited, focusLost, Stopwatch.GetTimestamp())) { await Task.Delay(10); continue; }
                    // ClipCursor has no owner API. Match our recorded rectangle exactly and
                    // restore only the recorded previous rectangle; never clear a different clip.
                    if (CursorNative.RestoreOwnedResult(lease.Owned, lease.Previous) == CursorNative.RestoreResult.RetryFailure)
                    {
                        // A transient desktop/native failure must not strand
                        // an owned center lock when the parent exits. Keep the
                        // exact private record and retry without touching a
                        // different clip or publishing false recovery success.
                        await Task.Delay(100); continue;
                    }
                    map.PublishRecovery(lease.Generation);
                }
                if (exited || lease.Shutdown) return;
                await Task.Delay(10);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}

internal readonly record struct CursorLease(bool Valid, bool Armed, bool Shutdown, long Timestamp,
    long Window, uint GamePid, CursorNative.Rect Owned, CursorNative.Rect Previous, long Generation);

internal static class CursorRecovery
{
    internal static bool ShouldRecover(CursorLease current, CursorLease observed, bool parentExited, bool focusLost, long now)
    {
        if (!current.Valid || !current.Armed || current.Generation <= 0 || current.Generation != observed.Generation ||
            current.Window != observed.Window || current.GamePid != observed.GamePid || !current.Owned.Equals(observed.Owned) || !current.Previous.Equals(observed.Previous)) return false;
        double age = (now - current.Timestamp) * 1000.0 / Stopwatch.Frequency;
        return parentExited || focusLost || current.Shutdown || current.Timestamp <= 0 || age < 0 || age >= 200;
    }
}

internal sealed unsafe class CursorLeaseMap : IDisposable
{
    const long Header = (1L << 32) | 0x43535256;
    const int Size = 4096;
    IntPtr handle;
    byte* view;
    public string Name { get; }
    public int WriterPid => (int)Get(16);
    public bool HelperAlive => Fresh(Get(128), 200);
    public long RecoveryTimestamp => Get(136);
    public long RecoveryGeneration => Get(152);
    public static bool Fresh(long stamp, int milliseconds)
    {
        if (stamp <= 0) return false;
        double age = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
        return age >= 0 && age < milliseconds;
    }
    public static bool ValidName(string name)
    {
        string prefix = "Local\\VRC-SWITCHEROONIE-Cursor-" + Identity.Sid + "-";
        return name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }
    public CursorLeaseMap(string name, bool create, int writerPid)
    {
        if (!ValidName(name)) throw new ArgumentException("Invalid cursor lease map.");
        Name = name;
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (create)
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;" + Identity.Sid + ")", 1, out descriptor, out _)) throw new Win32Exception();
                var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
                handle = CreateFileMapping(new IntPtr(-1), ref security, 4, 0, Size, name);
                if (handle == IntPtr.Zero) throw new Win32Exception();
                if (Marshal.GetLastWin32Error() == 183) throw new IOException("Cursor map already exists.");
            }
            else handle = OpenFileMapping(0xF001F, false, name);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            view = (byte*)MapViewOfFile(handle, 0xF001F, 0, 0, Size);
            if (view == null) throw new Win32Exception();
            if (create) { Put(8, Header); Put(16, writerPid); }
            else if (Get(8) != Header || WriterPid != writerPid) throw new IOException("Cursor map identity mismatch.");
        }
        catch { Dispose(); throw; }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }
    long Get(int offset) => Interlocked.Read(ref *(long*)(view + offset));
    void Put(int offset, long value) => Interlocked.Exchange(ref *(long*)(view + offset), value);
    public void Publish(bool armed, long window, uint gamePid, CursorNative.Rect owned, CursorNative.Rect previous, bool shutdown = false, long generation = 0)
    {
        // Commit a complete inactive slot, then atomically select it. A process
        // killed mid-write leaves the previous armed recovery record readable.
        long publication = Get(0), slot = 256 + ((publication + 1) & 1) * 256;
        int start = (int)slot;
        long sequence = Get(start) & ~1L;
        // -2 retains armed recovery authority during shutdown after a failed
        // local restoration; -1 means shutdown with no remaining owned clip.
        Put(start, sequence + 1); Put(start + 8, Stopwatch.GetTimestamp()); Put(start + 16, shutdown ? armed ? -2 : -1 : armed ? 1 : 0);
        Put(start + 24, window); Put(start + 32, gamePid);
        Put(start + 40, owned.Left); Put(start + 48, owned.Top); Put(start + 56, owned.Right); Put(start + 64, owned.Bottom);
        Put(start + 72, previous.Left); Put(start + 80, previous.Top); Put(start + 88, previous.Right); Put(start + 96, previous.Bottom);
        Put(start + 104, generation);
        Put(start, sequence + 2); Put(0, publication + 1);
    }
    public CursorLease Read()
    {
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            long publication = Get(0);
            int start = 256 + (int)(publication & 1) * 256;
            long seq = Get(start); if ((seq & 1) != 0) continue;
            long stamp = Get(start + 8), armed = Get(start + 16), window = Get(start + 24), pid = Get(start + 32);
            var owned = new CursorNative.Rect((int)Get(start + 40), (int)Get(start + 48), (int)Get(start + 56), (int)Get(start + 64));
            var previous = new CursorNative.Rect((int)Get(start + 72), (int)Get(start + 80), (int)Get(start + 88), (int)Get(start + 96));
            if (Get(0) != publication || Get(start) != seq) continue;
            bool valid = Get(8) == Header && WriterPid > 0 && armed is -2 or -1 or 0 or 1;
            long generation = Get(start + 104);
            if (Get(0) != publication || Get(start) != seq) continue;
            if (armed is 1 or -2) valid &= window != 0 && pid > 0 && pid <= uint.MaxValue && generation > 0 && CursorNative.ValidRect(owned) && CursorNative.ValidRect(previous);
            return new(valid, valid && armed is 1 or -2, valid && armed is -1 or -2, stamp, window, (uint)pid, owned, previous, generation);
        }
        return new();
    }
    public void PublishHelperHeartbeat() { Put(128, Stopwatch.GetTimestamp()); Put(144, Environment.ProcessId); }
    public void PublishRecovery(long generation) { Put(152, generation); Put(136, Stopwatch.GetTimestamp()); }
    public void Dispose()
    {
        if (view != null) { UnmapViewOfFile((IntPtr)view); view = null; }
        if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out IntPtr result, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileMapping(IntPtr file, ref SecurityAttributes attributes, uint protection, uint high, uint low, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, nuint size);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr address);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr value);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
}

internal static class CursorNative
{
    internal enum RestoreResult { Restored, NotOwned, RetryFailure }
    internal interface IClipOperations { bool Read(out Rect rectangle); bool Restore(Rect rectangle); }
    sealed class NativeClipOperations : IClipOperations
    {
        public bool Read(out Rect rectangle) => GetClipCursor(out rectangle);
        public bool Restore(Rect rectangle) => ClipCursor(ref rectangle);
    }
    static readonly IClipOperations operations = new NativeClipOperations();
    [StructLayout(LayoutKind.Sequential)] internal struct Rect(int left, int top, int right, int bottom) : IEquatable<Rect>
    {
        public int Left = left, Top = top, Right = right, Bottom = bottom;
        public readonly bool Equals(Rect other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct CursorInfo { public uint Size, Flags; public IntPtr Cursor; public Point Position; }
    internal static bool ValidRect(Rect value) => value.Right > value.Left && value.Bottom > value.Top &&
        (long)value.Right - value.Left <= 100000 && (long)value.Bottom - value.Top <= 100000;
    internal static Rect DesktopRect() => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(76) + GetSystemMetrics(78), GetSystemMetrics(77) + GetSystemMetrics(79));
    internal static bool RestoreIfStillOwned(Rect owned, Rect previous) => RestoreIfStillOwned(owned, previous, operations);
    internal static bool RestoreIfStillOwned(Rect owned, Rect previous, IClipOperations clip) => RestoreOwnedResult(owned, previous, clip) == RestoreResult.Restored;
    internal static RestoreResult RestoreOwnedResult(Rect owned, Rect previous) => RestoreOwnedResult(owned, previous, operations);
    internal static RestoreResult RestoreOwnedResult(Rect owned, Rect previous, IClipOperations clip)
    {
        if (!ValidRect(owned) || !ValidRect(previous)) return RestoreResult.NotOwned;
        if (!clip.Read(out var current)) return RestoreResult.RetryFailure;
        if (!current.Equals(owned)) return RestoreResult.NotOwned;
        return clip.Restore(previous) ? RestoreResult.Restored : RestoreResult.RetryFailure;
    }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetClipCursor(out Rect rectangle);
    [DllImport("user32.dll")] internal static extern bool ClipCursor(ref Rect rectangle);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
}
