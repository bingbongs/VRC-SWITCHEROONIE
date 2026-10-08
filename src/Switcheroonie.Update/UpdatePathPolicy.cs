using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Switcheroonie.Update;

public static class UpdatePathPolicy
{
    // Microsoft CLOUD_0..F directories are not name surrogates. Their fixed
    // SDK values differ only in bits12..15; other reparse tags remain refused.
    // https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-tags
    public static bool IsCloudDirectory(uint attributes, uint tag) =>
        (attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) ==
            ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint) &&
        (tag & 0xFFFF0FFFu) == 0x9000001Au;

    internal static bool IsCloudDirectory(string path)
    {
        // Inspect the entry itself without following a junction or symbolic link.
        // Metadata access only; no content reads, tag setters or permission changes.
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8))
            throw new IOException("Reparse entry metadata is unavailable; path refused.");
        return IsCloudDirectory(info.Attributes, info.Tag);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AttributeTagInfo { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out AttributeTagInfo info, uint bytes);
}
