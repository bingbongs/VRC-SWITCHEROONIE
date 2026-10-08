using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Switcheroonie;

namespace Switcheroonie.Broker;

public sealed class WindowsGameInput : IGameInputPlatform
{
    readonly object gate = new();
    readonly Thread thread;
    readonly ManualResetEventSlim ready = new(false);
    readonly WindowProcedure windowProcedure;
    readonly HookProcedure hookProcedure;
    readonly KeyboardPressTracker keyPresses = new();
    readonly int session = Process.GetCurrentProcess().SessionId;
    volatile bool disposed, initialized, eligibleScope, active, typing, spinEnabled, lookRequested;
    LookCursorSurface? lookSurface;
    readonly bool[] numpad = new bool[10];
    sealed record KeyMapping(int Forward = 0x57, int Back = 0x53, int Left = 0x41, int Right = 0x44, int Jump = 0x20, int Run = 0x10, int Crouch = 0x43, int Prone = 0x5A);
    KeyMapping mapping = new();
    IntPtr messageWindow, hook;
    long scopeWindow, scopeStamp;
    long cursorOwnerWindow, cursorOwnerGeneration;
    uint cursorOwnerPid;
    int scopePid;
    int deltaX, deltaY, activationClick, toggleMenu, escapeMenu, raiseMenu, spinToggle, crouchPress, pronePress, chatToggle, chatCancel, emergency;
    bool crouchHeld, proneHeld;
    long activationWindow, activationStamp;
    int activationPid;
    string? initializationError;
    CursorNative.Rect clientRect;
    readonly GameCursorCapture cursorCapture;
    CursorLeaseMap? cursorMap;
    Process? helper;
    readonly HelperRestartPolicy helperRestarts = new(Stopwatch.Frequency);
    public string? CursorMapName => cursorMap?.Name;
    public bool IsInitialized => initialized;
    public string? InitializationError => Volatile.Read(ref initializationError);
    public bool CursorWatchdogAlive { get { lock (gate) return cursorMap?.HelperAlive == true; } }
    public bool CaptureActive { get { lock (gate) return cursorCapture.Owned; } }
    public bool CursorHidden
    {
        get
        {
            lock (gate)
            {
                if (!lookRequested || !cursorCapture.Owned || !ScopeFresh()) return false;
                var info = new CursorNative.CursorInfo { Size = (uint)Marshal.SizeOf<CursorNative.CursorInfo>() };
                return CursorNative.GetCursorInfo(ref info) && (info.Flags & 1) == 0;
            }
        }
    }

    public WindowsGameInput()
    {
        cursorCapture = new(new CaptureOperations(this), PublishCaptureLease);
        windowProcedure = WindowProc; hookProcedure = KeyboardProc;
        thread = new Thread(MessageLoop) { IsBackground = true, Name = "Switcheroonie raw input" };
        thread.Start(); if (!ready.Wait(TimeSpan.FromSeconds(1))) Volatile.Write(ref initializationError, "Windows input thread startup timed out.");
    }
    public void SetActive(bool value) => active = value;
    public void SetTyping(bool value) => typing = value;
    public void SetSpinEnabled(bool value) { lock (gate) { spinEnabled = value; Array.Clear(numpad); Interlocked.Exchange(ref spinToggle, 0); } }
    public void ConfigureKeys(IReadOnlyDictionary<string, int> keys)
    {
        static int Key(IReadOnlyDictionary<string, int> source, string name, int fallback) => source.TryGetValue(name, out int value) && value is > 0 and < 255 ? value : fallback;
        lock (gate)
        {
            Volatile.Write(ref mapping, new(Key(keys, "Forward", 0x57), Key(keys, "Back", 0x53), Key(keys, "Left", 0x41), Key(keys, "Right", 0x44), Key(keys, "Jump", 0x20), Key(keys, "Run", 0x10), Key(keys, "Crouch", 0x43), Key(keys, "Prone", 0x5A)));
            crouchHeld = proneHeld = false; Interlocked.Exchange(ref crouchPress, 0); Interlocked.Exchange(ref pronePress, 0);
        }
    }

    public GameInputSample Read(bool eligible)
    {
        lock (gate)
        {
            eligibleScope = eligible;
            if (disposed || !initialized || !eligible || !ValidateGameWindow(out var window, out uint pid, out var rectangle))
            {
                InvalidateScope(); ReleaseCapture(); PublishLease(false); return new();
            }
            bool changed = Volatile.Read(ref scopeWindow) != window.ToInt64() || Volatile.Read(ref scopePid) != (int)pid;
            if (changed) { InvalidateScope(); ReleaseCapture(); }
            clientRect = rectangle;
            Volatile.Write(ref scopePid, (int)pid); Volatile.Write(ref scopeWindow, window.ToInt64());
            Volatile.Write(ref scopeStamp, Stopwatch.GetTimestamp());
            if (cursorMap is not null)
            {
                if (cursorCapture.Owned && (!cursorMap.HelperAlive || cursorMap.RecoveryGeneration == cursorCapture.Generation))
                {
                    ReleaseCapture(); PublishLease(false); InvalidateScope(); return new();
                }
                PublishLease(cursorCapture.Owned);
            }
            int dx = Interlocked.Exchange(ref deltaX, 0), dy = Interlocked.Exchange(ref deltaY, 0);
            long clickStamp = Volatile.Read(ref activationStamp);
            bool acquire = Interlocked.Exchange(ref activationClick, 0) != 0 && Volatile.Read(ref activationWindow) == window.ToInt64() &&
                Volatile.Read(ref activationPid) == (int)pid && clickStamp > 0 && Stopwatch.GetElapsedTime(clickStamp).TotalMilliseconds is >= 0 and <= 250;
            bool toggle = Interlocked.Exchange(ref toggleMenu, 0) != 0;
            bool escape = Interlocked.Exchange(ref escapeMenu, 0) != 0;
            bool raise = Interlocked.Exchange(ref raiseMenu, 0) != 0;
            bool spin = Interlocked.Exchange(ref spinToggle, 0) != 0;
            bool crouch = Interlocked.Exchange(ref crouchPress, 0) != 0, prone = Interlocked.Exchange(ref pronePress, 0) != 0;
            bool chat = Interlocked.Exchange(ref chatToggle, 0) != 0;
            bool cancel = Interlocked.Exchange(ref chatCancel, 0) != 0;
            bool release = Interlocked.Exchange(ref emergency, 0) != 0;
            var keys = Volatile.Read(ref mapping);
            // These polls happen only after exact foreground process/window/session validation.
            return new(Window: window.ToInt64(), Focused: true, LeftDown: Down(1), RightDown: Down(2), MiddleDown: Down(4),
                Forward: Down(keys.Forward), Back: Down(keys.Back), Left: Down(keys.Left), Right: Down(keys.Right), Jump: Down(keys.Jump), Run: Down(keys.Run),
                DeltaX: dx, DeltaY: dy, ToggleMenu: toggle, ChatToggle: chat, ChatCancel: cancel, Emergency: release, ActivateClick: acquire,
                Crouch: crouchHeld || Down(keys.Crouch), Prone: proneHeld || Down(keys.Prone), EscapeMenu: escape, RaiseMenu: raise,
                SpinToggle: spin, SpinLeft: numpad[4], SpinRight: numpad[6], SpinForward: numpad[8], SpinBack: numpad[2], CrouchPress: crouch, PronePress: prone);
        }
    }

    public void SetCapture(bool capture) => SetCapture(capture, false);
    public void SetCapture(bool capture, bool pointer)
    {
        lock (gate)
        {
            if (disposed) return;
            if (!capture || !active || typing || !ScopeFresh() || !ValidateGameWindow(out var window, out uint pid, out var rectangle) ||
                window.ToInt64() != Volatile.Read(ref scopeWindow) || pid != (uint)Volatile.Read(ref scopePid))
            {
                ReleaseCapture(); PublishLease(false); return;
            }
            EnsureHelper();
            if (cursorMap?.HelperAlive != true) { ReleaseCapture(); PublishLease(false); return; }
            clientRect = rectangle;
            cursorCapture.Update(true, pointer, rectangle, CursorNative.DesktopRect());
            lookRequested = cursorCapture.Owned && !pointer; lookSurface?.Refresh();
        }
    }

    void EnsureHelper()
    {
        bool childAlive;
        try { childAlive = helper is not null && !helper.HasExited; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        { childAlive = true; }
        if (!helperRestarts.TryBeginStart(Stopwatch.GetTimestamp(), childAlive, cursorMap?.HelperAlive == true)) return;
        string? executable = Environment.ProcessPath;
        if (executable is null || !Path.GetFileNameWithoutExtension(executable).Equals("Switcheroonie.Broker", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            cursorMap ??= new CursorLeaseMap("Local\\VRC-SWITCHEROONIE-Cursor-" + Identity.Sid + "-" + Guid.NewGuid().ToString("N"), true, Environment.ProcessId);
            PublishLease(false);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--cursor-watchdog=" + cursorMap.Name);
            start.ArgumentList.Add("--parent=" + Environment.ProcessId);
            var previous = helper; helper = null; previous?.Dispose(); helper = Process.Start(start);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }
    void PublishLease(bool armed) => cursorCapture.Publish(armed || cursorCapture.Owned);
    void PublishCaptureLease(bool armed, CursorNative.Rect owned, CursorNative.Rect previous, long generation)
    {
        long scopedWindow = Volatile.Read(ref scopeWindow);
        uint scopedPid = (uint)Volatile.Read(ref scopePid);
        if (armed && generation != cursorOwnerGeneration && scopedWindow != 0 && scopedPid != 0)
        { cursorOwnerWindow = scopedWindow; cursorOwnerPid = scopedPid; cursorOwnerGeneration = generation; }
        // Failed restoration must retain a valid recovery identity even after
        // input scope is invalidated, until the helper can restore the owned clip.
        cursorMap?.Publish(armed, armed ? cursorOwnerWindow : scopedWindow, armed ? cursorOwnerPid : scopedPid,
            owned, previous, generation: generation);
    }
    void ReleaseCapture() { lookRequested = false; lookSurface?.Refresh(); cursorCapture.Release(); }
    sealed class CaptureOperations(WindowsGameInput owner) : GameCursorCapture.IOperations
    {
        public bool AuthorityValid => !owner.disposed && owner.eligibleScope && owner.active && !owner.typing && owner.ScopeFresh() && owner.cursorMap?.HelperAlive == true;
        public bool Read(out CursorNative.Rect rectangle) => CursorNative.GetClipCursor(out rectangle);
        public bool Clip(CursorNative.Rect rectangle) => CursorNative.ClipCursor(ref rectangle);
        public bool Center(int x, int y) => CursorNative.SetCursorPos(x, y);
    }
    void InvalidateScope()
    {
        Volatile.Write(ref scopeStamp, 0); Volatile.Write(ref scopeWindow, 0); Volatile.Write(ref scopePid, 0);
        Interlocked.Exchange(ref deltaX, 0); Interlocked.Exchange(ref deltaY, 0); Interlocked.Exchange(ref activationClick, 0);
        Volatile.Write(ref activationWindow, 0); Volatile.Write(ref activationPid, 0); Volatile.Write(ref activationStamp, 0);
        Interlocked.Exchange(ref toggleMenu, 0); Interlocked.Exchange(ref escapeMenu, 0); Interlocked.Exchange(ref chatToggle, 0); Interlocked.Exchange(ref chatCancel, 0); Interlocked.Exchange(ref emergency, 0);
        Interlocked.Exchange(ref raiseMenu, 0); Interlocked.Exchange(ref spinToggle, 0); Array.Clear(numpad);
        Interlocked.Exchange(ref crouchPress, 0); Interlocked.Exchange(ref pronePress, 0); crouchHeld = proneHeld = false;
    }
    bool ScopeFresh()
    {
        long stamp = Volatile.Read(ref scopeStamp), window = Volatile.Read(ref scopeWindow);
        return !disposed && stamp > 0 && Stopwatch.GetElapsedTime(stamp).TotalMilliseconds is >= 0 and <= 100 && window != 0 &&
            CursorNative.GetForegroundWindow().ToInt64() == window && CursorNative.GetWindowThreadProcessId(new IntPtr(window), out uint pid) != 0 && pid == (uint)Volatile.Read(ref scopePid);
    }
    bool ValidateGameWindow(out IntPtr window, out uint pid, out CursorNative.Rect rectangle)
    {
        window = CursorNative.GetForegroundWindow(); pid = 0; rectangle = default;
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window) || CursorNative.GetWindowThreadProcessId(window, out pid) == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (!process.ProcessName.Equals("VRChat", StringComparison.OrdinalIgnoreCase) || process.SessionId != session || process.MainWindowHandle != window) return false;
            var className = new StringBuilder(64);
            if (GetClassName(window, className, className.Capacity) == 0 || !className.ToString().Equals("UnityWndClass", StringComparison.Ordinal)) return false;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
        if (!GetClientRect(window, out var client) || !CursorNative.ValidRect(client) || client.Right - client.Left < 64 || client.Bottom - client.Top < 64) return false;
        var first = new CursorNative.Point { X = client.Left, Y = client.Top };
        var last = new CursorNative.Point { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(window, ref first) || !ClientToScreen(window, ref last)) return false;
        var desktop = CursorNative.DesktopRect();
        rectangle = new(Math.Max(first.X, desktop.Left), Math.Max(first.Y, desktop.Top), Math.Min(last.X, desktop.Right), Math.Min(last.Y, desktop.Bottom));
        return CursorNative.ValidRect(rectangle) && CursorNative.GetForegroundWindow() == window;
    }
    static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    static void AddDelta(ref int destination, int value)
    {
        int previous, next;
        do { previous = Volatile.Read(ref destination); next = (int)Math.Clamp((long)previous + value, -20000, 20000); }
        while (Interlocked.CompareExchange(ref destination, next, previous) != previous);
    }

    unsafe void RawMouse(IntPtr raw)
    {
        if (!eligibleScope || disposed) return;
        uint size = 0, headerSize = (uint)Marshal.SizeOf<RawHeader>();
        if (GetRawInputData(raw, 0x10000003, IntPtr.Zero, ref size, headerSize) != 0 || size < headerSize + 24 || size > 512) return;
        byte* buffer = stackalloc byte[512];
        if (GetRawInputData(raw, 0x10000003, (IntPtr)buffer, ref size, headerSize) != size || *(uint*)buffer != 0) return;
        byte* mouse = buffer + headerSize;
        ushort flags = *(ushort*)mouse, buttons = *(ushort*)(mouse + 4);
        lock (gate)
        {
            if (!eligibleScope || disposed) return;
            if (!ScopeFresh())
            {
                // A real left-down may focus the game before the next broker
                // poll. Scope validation and publication share Read's gate so
                // an old callback cannot tag deltas/clicks onto a newer scope.
                if ((buttons & 1) == 0 || !ValidateGameWindow(out var game, out uint pid, out var rectangle) || CursorNative.GetForegroundWindow() != game) return;
                InvalidateScope(); ReleaseCapture(); clientRect = rectangle;
                Volatile.Write(ref scopePid, (int)pid); Volatile.Write(ref scopeWindow, game.ToInt64());
                Volatile.Write(ref scopeStamp, Stopwatch.GetTimestamp());
            }
            if ((buttons & 1) != 0)
            {
                Volatile.Write(ref activationWindow, Volatile.Read(ref scopeWindow)); Volatile.Write(ref activationPid, Volatile.Read(ref scopePid));
                Volatile.Write(ref activationStamp, Stopwatch.GetTimestamp()); Interlocked.Exchange(ref activationClick, 1);
            }
            if ((flags & 1) == 0) { AddDelta(ref deltaX, *(int*)(mouse + 12)); AddDelta(ref deltaY, *(int*)(mouse + 16)); }
        }
    }
    IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || disposed) return CallNextHookEx(hook, code, wParam, lParam);
        var key = Marshal.PtrToStructure<KeyboardData>(lParam);
        bool up = wParam.ToInt64() is 0x101 or 0x105;
        int vk = (int)key.Key;
        if ((key.Flags & 0x12) != 0) return CallNextHookEx(hook, code, wParam, lParam);
        int pad = NumpadKey(key.Scan, key.Flags);
        int pressIdentity = PressIdentity(vk, key.Scan, key.Flags);
        var keys = Volatile.Read(ref mapping);
        if (vk is not (27 or 13 or 89 or 123 or 77) && vk != keys.Crouch && vk != keys.Prone && pad < 0 && !keyPresses.IsOwned(pressIdentity))
            return CallNextHookEx(hook, code, wParam, lParam);
        var press = keyPresses.Observe(pressIdentity, up);
        if (pad >= 0 && up) { lock (gate) numpad[pad] = false; }
        if (up) { lock (gate) { if (vk == keys.Crouch) crouchHeld = false; if (vk == keys.Prone) proneHeld = false; } }
        // Keep one owned key press consistent until its matching release, even
        // when modifiers, typing state, or focus change between its repeats.
        if (press.Swallow) return new IntPtr(1);
        if (up) return CallNextHookEx(hook, code, wParam, lParam);
        bool first = press.First;
        bool claim = false;
        lock (gate)
        {
            if (eligibleScope && ScopeFresh())
            {
                bool control = Down(17), alt = Down(18), win = Down(91) || Down(92), shift = Down(16);
                if (vk == 123 && control && alt && !win)
                {
                    if (first) Interlocked.Exchange(ref emergency, 1);
                    // Pass to the UI's existing emergency registration too.
                }
                else if (!control && !alt && !win)
                {
                    if (spinEnabled && !typing && pad is 2 or 4 or 5 or 6 or 8)
                    {
                        numpad[pad] = true;
                        if (pad == 5 && first) Interlocked.Exchange(ref spinToggle, 1);
                        keyPresses.Claim(pressIdentity); claim = true;
                    }
                    if (first && ((vk == 13 && typing && !shift) || (vk == 89 && !typing))) Interlocked.Exchange(ref chatToggle, 1);
                    if (vk == 27 && typing)
                    {
                        if (first) Interlocked.Exchange(ref chatCancel, 1);
                    }
                    else if (!shift && active && !typing && vk == 27)
                    {
                        if (first) Interlocked.Exchange(ref escapeMenu, 1);
                        keyPresses.Claim(pressIdentity); claim = true;
                    }
                    else if (!shift && active && !typing && vk == 77)
                    {
                        if (first) Interlocked.Exchange(ref raiseMenu, 1);
                        keyPresses.Claim(pressIdentity); claim = true;
                    }
                    else if (active && !typing && (vk == keys.Crouch || vk == keys.Prone))
                    {
                        if (vk == keys.Crouch) { crouchHeld = true; if (first) Interlocked.Exchange(ref crouchPress, 1); }
                        if (vk == keys.Prone) { proneHeld = true; if (first) Interlocked.Exchange(ref pronePress, 1); }
                        keyPresses.Claim(pressIdentity); claim = true;
                    }
                }
            }
        }
        if (claim) return new IntPtr(1);
        // Do not hold our publication gate while invoking another hook.
        return CallNextHookEx(hook, code, wParam, lParam);
    }
    internal static int NumpadKey(uint scan, uint flags) => (flags & 1) != 0 ? -1 : scan switch
    { 0x50 => 2, 0x4B => 4, 0x4C => 5, 0x4D => 6, 0x48 => 8, _ => -1 };
    internal static int PressIdentity(int virtualKey, uint scan, uint flags)
    { int pad = NumpadKey(scan, flags); return pad >= 0 ? 256 + pad : virtualKey; }

    void MessageLoop()
    {
        string className = "SwitcheroonieInput-" + Guid.NewGuid().ToString("N");
        IntPtr instance = GetModuleHandle(null);
        try
        {
            var wc = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(windowProcedure), Instance = instance, ClassName = className };
            if (RegisterClassEx(ref wc) == 0) throw new Win32Exception();
            messageWindow = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
            if (messageWindow == IntPtr.Zero) throw new Win32Exception();
            var device = new RawDevice { UsagePage = 1, Usage = 2, Flags = 0x100, Target = messageWindow };
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception();
            hook = SetWindowsHookEx(13, hookProcedure, instance, 0);
            if (hook == IntPtr.Zero) throw new Win32Exception();
            lookSurface = new LookCursorSurface(() =>
            {
                lock (gate)
                {
                    bool allowed = lookRequested && active && !typing && eligibleScope && ScopeFresh() && cursorMap?.HelperAlive == true &&
                        cursorCapture.Owned && CursorNative.GetClipCursor(out var actual) && actual.Equals(cursorCapture.OwnedRectangle);
                    return (allowed, cursorCapture.OwnedRectangle.Left, cursorCapture.OwnedRectangle.Top);
                }
            });
            Volatile.Write(ref initializationError, null); initialized = true; ready.Set();
            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
            if (result < 0) throw new Win32Exception();
        }
        catch (Win32Exception e) { Volatile.Write(ref initializationError, "Windows input initialization failed: " + e.Message); }
        finally
        {
            initialized = false; ready.Set();
            lookSurface?.Dispose(); lookSurface = null;
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            var remove = new RawDevice { UsagePage = 1, Usage = 2, Flags = 1, Target = IntPtr.Zero };
            RegisterRawInputDevices([remove], 1, (uint)Marshal.SizeOf<RawDevice>());
            if (messageWindow != IntPtr.Zero) { DestroyWindow(messageWindow); messageWindow = IntPtr.Zero; }
            UnregisterClass(className, instance);
        }
    }
    IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == 0xFF) RawMouse(lParam);
        else if (message == 0x10) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(window, message, wParam, lParam);
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; eligibleScope = false; InvalidateScope(); ReleaseCapture();
            cursorMap?.Publish(cursorCapture.Owned, cursorCapture.Owned ? cursorOwnerWindow : 0, cursorCapture.Owned ? cursorOwnerPid : 0,
                cursorCapture.OwnedRectangle, cursorCapture.PreviousRectangle, shutdown: true, generation: cursorCapture.Generation);
            cursorMap?.Dispose(); cursorMap = null; helper?.Dispose(); helper = null;
        }
        if (messageWindow != IntPtr.Zero) PostMessage(messageWindow, 0x10, IntPtr.Zero, IntPtr.Zero);
        if (thread.Join(TimeSpan.FromSeconds(1))) ready.Dispose();
    }

    delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct RawHeader { public uint Type, Size; public IntPtr Device, Parameter; }
    [StructLayout(LayoutKind.Sequential)] struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass { public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public IntPtr SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public CursorNative.Point Point; public uint Private; }
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out CursorNative.Rect rectangle);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref CursorNative.Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder value, int size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)] static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", ExactSpelling = true, SetLastError = true)] static extern int GetMessage(out Message message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW", ExactSpelling = true)] static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int type, HookProcedure callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
}

// Used only by the input thread. No typed text or general keyboard history is stored.
internal sealed class KeyboardPressTracker
{
    readonly bool[] down = new bool[266], owned = new bool[266];
    internal readonly record struct Press(bool First, bool Swallow);
    internal bool IsOwned(int key) => (uint)key < owned.Length && owned[key];
    internal Press Observe(int key, bool up)
    {
        if ((uint)key >= down.Length) return new(false, false);
        if (up)
        {
            bool swallow = owned[key]; down[key] = owned[key] = false;
            return new(false, swallow);
        }
        bool first = !down[key]; down[key] = true;
        return new(first, owned[key]);
    }
    internal void Claim(int key) { if ((uint)key < owned.Length) owned[key] = true; }
}
