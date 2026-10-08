using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Switcheroonie.Broker;

// A one-pixel, non-activating cursor owner at the existing look lock. Windows
// lets this process handle WM_SETCURSOR for its own surface. We never change
// another process's cursor count, system cursor theme, code or window procedure.
// Pointer mode hides the surface; process exit destroys it automatically.
internal sealed class LookCursorSurface : IDisposable
{
    readonly Func<(bool Allowed, int X, int Y)> authority;
    readonly Procedure procedure;
    readonly string className = "SwitcheroonieLookCursor-" + Guid.NewGuid().ToString("N");
    readonly IntPtr instance;
    readonly OwnedCursorVisibilityPolicy visibility;
    IntPtr window;
    bool visible;
    int shownX, shownY;
    public LookCursorSurface(Func<(bool Allowed, int X, int Y)> authority)
    {
        this.authority = authority; procedure = WindowProc; instance = GetModuleHandle(null);
        visibility = new(new VisibilityOperations(this));
        var wc = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = instance, ClassName = className,
            Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Background = GetStockObject(4)
        };
        if (RegisterClassEx(ref wc) == 0) throw new Win32Exception();
        try
        {
            // WS_EX_NOACTIVATE | TOOLWINDOW | LAYERED. Alpha 1 retains hit
            // testing while changing at most one pixel by 1/255 opacity.
            window = CreateWindowEx(0x08080080, className, "", 0x80000000, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero || !SetLayeredWindowAttributes(window, 0, 1, 2))
                throw new Win32Exception();
        }
        catch { Dispose(); throw; }
    }
    public void Refresh() { if (window != IntPtr.Zero) PostMessage(window, 0x8001, IntPtr.Zero, IntPtr.Zero); }
    void Update()
    {
        var state = authority();
        if (!state.Allowed) { Suspend(); return; }
        if (!visible && SetTimer(window, 1, 50, IntPtr.Zero) == 0) return;
        if (!visible || state.X != shownX || state.Y != shownY)
        {
            // Do not repeatedly raise our window over a later foreign overlay.
            // A moved existing surface keeps its current place in the z-order.
            visible = SetWindowPos(window, new IntPtr(-1), state.X, state.Y, 1, 1,
                0x0010 | 0x0040 | (visible ? 0x0004u : 0));
            if (visible) { shownX = state.X; shownY = state.Y; }
        }
        if (!visible || !authority().Allowed) Suspend();
        else visibility.Hide();
    }
    void Suspend() { KillTimer(window, 1); visibility.Suspend(); visible = false; }
    IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp)
    {
        if (message is 0x0113 or 0x8001) { Update(); return IntPtr.Zero; }
        if (message == 0x0021) return new IntPtr(3); // MA_NOACTIVATE
        if (message == 0x0020)
        {
            bool allowed = authority().Allowed;
            if (hwnd == window && wp == window && (lp.ToInt64() & 0xffff) == 1 && allowed) visibility.Hide();
            else if (!allowed) Suspend();
            // A delayed message can arrive after the cursor left our surface.
            // DefWindowProc would choose an arrow without our ownership check.
            return new IntPtr(1);
        }
        return DefWindowProc(hwnd, message, wp, lp);
    }
    public void Dispose()
    {
        if (window != IntPtr.Zero) { KillTimer(window, 1); visibility.Dispose(); window = IntPtr.Zero; }
        UnregisterClass(className, instance);
    }
    sealed class VisibilityOperations(LookCursorSurface owner) : OwnedCursorVisibilityPolicy.IOperations
    {
        bool OwnsPoint() => owner.window != IntPtr.Zero && GetCursorPos(out var point) && WindowFromPoint(point) == owner.window;
        public bool Read(out OwnedCursorVisibilityPolicy.Observation observation)
        {
            observation = default;
            var info = new CursorNative.CursorInfo { Size = (uint)Marshal.SizeOf<CursorNative.CursorInfo>() };
            if (!GetCursorInfo(ref info)) return false;
            observation = new(OwnsPoint(), info.Cursor == IntPtr.Zero && (info.Flags & 1) == 0);
            return true;
        }
        public bool Hide()
        {
            // Recheck immediately before mutation: clip ownership alone does
            // not establish which window actually owns the cursor position.
            if (!owner.authority().Allowed || !Read(out var state) || !state.OwnsPoint || !OwnsPoint()) return false;
            // Keep no foreign HCURSOR across a callback or message pump. A null
            // return means this call did not replace a non-null cursor shape.
            return SetCursor(IntPtr.Zero) != IntPtr.Zero;
        }
        public bool RestoreArrow()
        {
            if (!Read(out var state) || !state.OwnsPoint || !state.Hidden) return false;
            IntPtr arrow = LoadCursor(IntPtr.Zero, new IntPtr(32512)); // shared IDC_ARROW
            if (arrow == IntPtr.Zero || !Read(out state) || !state.OwnsPoint || !state.Hidden || !OwnsPoint()) return false;
            SetCursor(arrow);
            return true;
        }
        public void HideSurface() { if (owner.window != IntPtr.Zero) ShowWindow(owner.window, 0); }
        public void DestroySurface() { if (owner.window != IntPtr.Zero) DestroyWindow(owner.window); }
    }
    delegate IntPtr Procedure(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
    { public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public IntPtr SmallIcon; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern nuint SetTimer(IntPtr hwnd, nuint id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hwnd, nuint id);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorNative.Point point);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(CursorNative.Point point);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorNative.CursorInfo info);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW", ExactSpelling = true)] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)] static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
}

// Pure cursor-shape ownership policy. The adapter rechecks hit testing before
// every mutation. Pre-existing hidden shapes create no restoration claim;
// handoff never overwrites a currently replaced shape, and disposal is terminal.
internal sealed class OwnedCursorVisibilityPolicy(OwnedCursorVisibilityPolicy.IOperations operations) : IDisposable
{
    internal readonly record struct Observation(bool OwnsPoint, bool Hidden);
    internal interface IOperations
    {
        bool Read(out Observation observation);
        bool Hide();
        bool RestoreArrow();
        void HideSurface();
        void DestroySurface();
    }
    bool hiddenByUs, disposed;
    internal bool Hide()
    {
        if (disposed || !operations.Read(out var state)) return false;
        if (!state.OwnsPoint) { hiddenByUs = false; return false; }
        if (state.Hidden) return true;
        hiddenByUs = operations.Hide();
        return hiddenByUs;
    }
    void Handoff()
    {
        if (hiddenByUs && operations.Read(out var state) && state.OwnsPoint && state.Hidden)
            operations.RestoreArrow();
        hiddenByUs = false;
    }
    internal void Suspend()
    {
        if (disposed) return;
        Handoff(); operations.HideSurface();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Handoff(); operations.HideSurface(); operations.DestroySurface();
    }
}
