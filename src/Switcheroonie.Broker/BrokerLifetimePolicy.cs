using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Switcheroonie.Broker;

internal enum BrokerParentState { Alive, Exited, Unavailable }
internal sealed record BrokerParentIdentity(int Pid, int Session, long Started, string Image, string User);

internal static class BrokerLifetimePolicy
{
    internal static bool TryParse(string[] arguments, int ownPid, out int? parentPid)
    {
        parentPid = null;
        var candidates = arguments.Where(a => a.StartsWith("--ui-parent", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0) return true; // Direct brokers retain independent lifetime.
        if (candidates.Length != 1 || arguments.Length != 1 || !candidates[0].StartsWith("--ui-parent=", StringComparison.Ordinal) ||
            arguments.Any(a => a.StartsWith("--osc-watchdog", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--cursor-watchdog", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--parent", StringComparison.OrdinalIgnoreCase))) return false;
        string value = candidates[0]["--ui-parent=".Length..];
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[1-9][0-9]{0,9}\z") ||
            !int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) ||
            pid == ownPid) return false;
        parentPid = pid; return true;
    }

    internal static bool Admitted(BrokerParentIdentity parent, int requestedPid, int actualCreatorPid, int ownPid,
        int ownSession, long ownStarted, string ownUser, string brokerImage,
        Func<string, bool> ordinary, Func<string, string, bool> sameFile)
    {
        try
        {
            if (requestedPid <= 0 || requestedPid != actualCreatorPid || parent.Pid != requestedPid ||
                parent.Pid == ownPid || parent.Session != ownSession || ownStarted <= 0 ||
                parent.Started <= 0 || parent.Started > ownStarted || parent.User != ownUser || !Canonical(brokerImage) || !Canonical(parent.Image) ||
                !Path.GetFileName(brokerImage).Equals("Switcheroonie.Broker.exe", StringComparison.OrdinalIgnoreCase)) return false;
            string root = Path.GetDirectoryName(brokerImage)!;
            string expected = Path.Combine(root, "Switcheroonie.UI.exe");
            return parent.Image.Equals(expected, StringComparison.OrdinalIgnoreCase) &&
                ordinary(root) && ordinary(brokerImage) && ordinary(expected) && ordinary(parent.Image) &&
                sameFile(expected, parent.Image);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
    static bool Canonical(string path) => !string.IsNullOrEmpty(path) && path.Length <= 4096 &&
        Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
        !path.StartsWith(@"\\.\", StringComparison.Ordinal) && path.IndexOf('\0') < 0 &&
        Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldCancel(BrokerParentIdentity retained, BrokerParentIdentity? observed, BrokerParentState state) =>
        state != BrokerParentState.Alive || observed is null || observed != retained;

    internal static void Poll(BrokerParentIdentity retained, BrokerParentIdentity? observed,
        BrokerParentState state, CancellationTokenSource ownCancellation)
    { if (ShouldCancel(retained, observed, state)) ownCancellation.Cancel(); }
}

// A retained kernel process object, never a recurring PID lookup. Its exit
// cannot be confused with a replacement process that later obtains the PID.
internal sealed class BrokerUiParent : IDisposable
{
    readonly SafeProcessHandle handle;
    readonly BrokerParentIdentity identity;
    BrokerUiParent(SafeProcessHandle handle, BrokerParentIdentity identity) { this.handle = handle; this.identity = identity; }

    internal static BrokerUiParent? Acquire(int requestedPid)
    {
        SafeProcessHandle? handle = null;
        try
        {
            handle = OpenProcess(0x00101000, false, requestedPid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
            if (handle.IsInvalid || GetProcessId(handle) != (uint)requestedPid || WaitForSingleObject(handle, 0) != 0x102 ||
                !ProcessIdToSessionId((uint)requestedPid, out uint parentSession) ||
                !ProcessIdToSessionId((uint)Environment.ProcessId, out uint ownSession) ||
                !GetProcessTimes(handle, out var created, out _, out _, out _) ||
                !OpenProcessToken(handle.DangerousGetHandle(), 8, out var parentToken)) return null;
            using (parentToken)
            {
                if (!OpenProcessToken(GetCurrentProcess(), 8, out var ownToken)) return null;
                using (ownToken)
                using (var parentUser = new WindowsIdentity(parentToken.DangerousGetHandle()))
                using (var ownUser = new WindowsIdentity(ownToken.DangerousGetHandle()))
                {
                    string? parentSid = parentUser.User?.Value, ownSid = ownUser.User?.Value;
                    if (parentSid is null || ownSid is null) return null;
                    string parentImage = Image(handle);
                    using var ownProcess = OpenProcess(0x1000, false, Environment.ProcessId);
                    if (ownProcess.IsInvalid || !GetProcessTimes(ownProcess, out var ownCreated, out _, out _, out _)) return null;
                    string brokerImage = Image(ownProcess);
                    long started = ((long)created.High << 32) | created.Low;
                    long ownStarted = ((long)ownCreated.High << 32) | ownCreated.Low;
                    var identity = new BrokerParentIdentity(requestedPid, checked((int)parentSession), started, parentImage, parentSid);
                    if (!BrokerLifetimePolicy.Admitted(identity, requestedPid, CreatorPid(), Environment.ProcessId, checked((int)ownSession), ownStarted,
                            ownSid, brokerImage, OrdinaryEntry, SameFile) || WaitForSingleObject(handle, 0) != 0x102) return null;
                    var lease = new BrokerUiParent(handle, identity); handle = null; return lease;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or
            System.ComponentModel.Win32Exception or OverflowException) { return null; }
        finally { handle?.Dispose(); }
    }

    internal void Poll(CancellationTokenSource ownCancellation)
    {
        BrokerParentState state;
        try
        {
            uint result = WaitForSingleObject(handle, 0);
            state = result == 0x102 ? BrokerParentState.Alive : result == 0 ? BrokerParentState.Exited : BrokerParentState.Unavailable;
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { state = BrokerParentState.Unavailable; }
        BrokerLifetimePolicy.Poll(identity, state == BrokerParentState.Alive ? identity : null, state, ownCancellation);
    }
    static string Image(SafeProcessHandle process)
    {
        var text = new System.Text.StringBuilder(4096); uint count = 4096;
        if (!QueryFullProcessImageNameW(process, 0, text, ref count) || count is 0 or >= 4096)
            throw new IOException("Parent image identity is unavailable.");
        return text.ToString();
    }
    static bool OrdinaryEntry(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    static int CreatorPid()
    {
        // The documented process snapshot exposes the creator PID. Combined
        // with creation times and the retained object this rejects pre-acquire
        // PID reuse without private NT APIs or any foreign process mutation.
        // https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32w
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new IOException("Process creator identity is unavailable.");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
        if (!Process32FirstW(snapshot, ref entry)) throw new IOException("Process creator snapshot is unavailable.");
        int count = 0;
        do
        {
            if (++count > 65536) throw new IOException("Process creator snapshot exceeds its bound.");
            if (entry.Pid == (uint)Environment.ProcessId) return checked((int)entry.ParentPid);
        } while (Process32NextW(snapshot, ref entry));
        throw new IOException("Own process creator is absent.");
    }
    static bool SameFile(string expected, string actual)
    {
        using var a = File.OpenHandle(expected, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var b = File.OpenHandle(actual, FileMode.Open, FileAccess.Read, FileShare.Read);
        return GetFileInformationByHandle(a, out var x) && GetFileInformationByHandle(b, out var y) &&
            x.Volume == y.Volume && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] struct FileInformation
    { public uint Attributes; public FileTime Created, Accessed, Written; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessEntry
    {
        public uint Size, Usage, Pid; public UIntPtr Heap;
        public uint Module, Threads, ParentPid; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, System.Text.StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}

internal sealed class BrokerClientLifetime
{
    readonly object sync = new();
    readonly HashSet<Task> clients = [];
    bool closing;
    internal bool TryRun(Func<Task> start)
    {
        lock (sync)
        {
            if (closing) return false;
            var task = start(); clients.Add(task);
            _ = task.ContinueWith(completed =>
            {
                _ = completed.Exception; // Observe faults even before a shutdown snapshot.
                lock (sync) clients.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return true;
        }
    }
    internal Task DrainAsync()
    {
        lock (sync) { closing = true; return Task.WhenAll(clients.ToArray()); }
    }
}

internal static class BrokerServiceLifetime
{
    internal static async Task RunProducerAsync(Func<Task> work, CancellationTokenSource ownCancellation)
    {
        try { await work(); }
        finally { ownCancellation.Cancel(); }
    }
    internal static async Task StopAsync(Task producer, BrokerClientLifetime clients,
        CancellationTokenSource ownCancellation, Action helperCleanup)
    {
        ownCancellation.Cancel();
        try { try { await producer; } catch (OperationCanceledException) when (ownCancellation.IsCancellationRequested) { } }
        finally
        {
            try { await clients.DrainAsync(); }
            finally { helperCleanup(); }
        }
    }
}
