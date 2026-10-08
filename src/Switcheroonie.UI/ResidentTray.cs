using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Switcheroonie.UI;

internal sealed class ResidentTray : IDisposable
{
    internal const int CallbackMessage = 0x8530;
    private const uint IconId = 0x5311;
    private readonly nint window;
    private readonly Action showPanel;
    private readonly ContextMenu menu;
    private NotifyIconData data;
    internal bool Available { get; private set; }

    internal ResidentTray(nint window, Action showPanel, Action release, Action retry, Action exitPanel)
    {
        this.window = window;
        this.showPanel = showPanel;
        menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 35, 42)),
            Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 243)),
            Placement = PlacementMode.MousePoint
        };
        AddItem("Open VRC-SWITCHEROONIE", showPanel);
        AddItem("Release all inputs", release);
        AddItem("Retry background service", retry);
        menu.Items.Add(new Separator());
        AddItem("Exit panel (service stays running)", exitPanel);
        data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = IconId,
            Flags = 1 | 2 | 4, Callback = CallbackMessage,
            Icon = LoadIcon(0, new nint(32512)), Tip = "VRC-SWITCHEROONIE · connecting to service",
            Info = "", InfoTitle = ""
        };
        Recreate();
    }

    private void AddItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    internal void Recreate() => Available = ShellNotifyIcon(0, ref data);
    internal void SetStatus(string status)
    {
        var tip = "VRC-SWITCHEROONIE · " + status;
        data.Tip = tip[..Math.Min(127, tip.Length)];
        if (Available) ShellNotifyIcon(1, ref data);
    }
    internal bool Handle(int message, nint lParam)
    {
        if (message != CallbackMessage) return false;
        switch ((int)lParam & 0xFFFF)
        {
            case 0x0202: case 0x0203: showPanel(); break; // left up/double-click
            case 0x0205: case 0x007B:
                // A deliberate tray interaction owns this popup's foreground.
                SetForegroundWindow(window);
                menu.IsOpen = true;
                break;
        }
        return true;
    }
    public void Dispose()
    {
        menu.IsOpen = false;
        if (Available) ShellNotifyIcon(2, ref data);
        Available = false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}
