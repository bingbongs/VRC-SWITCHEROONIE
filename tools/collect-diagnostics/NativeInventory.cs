using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Switcheroonie.Diagnostics
{
    // Read-only DXGI/display enumeration. Does not set DPI awareness, alter displays, or create graphics devices.
    public static class NativeInventory
    {
        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct AdapterDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public Luid AdapterLuid; public uint Flags;
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc1(IntPtr self, out AdapterDesc1 desc);
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        static T Method<T>(IntPtr obj, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
        public sealed class Adapter
        {
            public string Name { get; set; } public string Luid { get; set; }
            public uint VendorId { get; set; } public uint DeviceId { get; set; }
            public ulong DedicatedVideoBytes { get; set; } public bool Software { get; set; }
        }
        public static Adapter[] Adapters()
        {
            var list = new List<Adapter>(); var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); IntPtr factory;
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out factory));
            try
            {
                var enumerate = Method<EnumAdapters1>(factory, 12);
                for (uint index = 0; index < 64; index++)
                {
                    IntPtr adapter; int hr = enumerate(factory, index, out adapter);
                    if (hr == unchecked((int)0x887a0002)) break;
                    Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                        AdapterDesc1 desc; Marshal.ThrowExceptionForHR(Method<GetDesc1>(adapter, 10)(adapter, out desc));
                        list.Add(new Adapter { Name = desc.Description, Luid = ((uint)desc.AdapterLuid.High).ToString("x8") + ":" + desc.AdapterLuid.Low.ToString("x8"), VendorId = desc.VendorId, DeviceId = desc.DeviceId, DedicatedVideoBytes = desc.DedicatedVideoMemory.ToUInt64(), Software = (desc.Flags & 2) != 0 });
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            return list.ToArray();
        }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInfo
        {
            public int Size; public Rect Monitor, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }
        delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        public sealed class Display
        {
            public string Device { get; set; } public int X { get; set; } public int Y { get; set; }
            public int Width { get; set; } public int Height { get; set; } public bool Primary { get; set; }
            public uint DpiX { get; set; } public uint DpiY { get; set; } public string DpiBasis { get; set; }
        }
        public static Display[] Displays()
        {
            var list = new List<Display>();
            MonitorCallback callback = (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                uint x = 0, y = 0; int hr = GetDpiForMonitor(monitor, 0, out x, out y);
                list.Add(new Display { Device = info.Device, X = info.Monitor.Left, Y = info.Monitor.Top, Width = info.Monitor.Right - info.Monitor.Left, Height = info.Monitor.Bottom - info.Monitor.Top, Primary = (info.Flags & 1) != 0, DpiX = x, DpiY = y, DpiBasis = hr == 0 ? "effective DPI under collecting process awareness" : "unavailable" });
                return true;
            };
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            return list.ToArray();
        }
    }
}
