using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace Switcheroonie.Update;

public static class SigningKeyStore
{
    public static string DefaultPath => Path.Combine(StatePaths.Current.DataDirectory, "updates", "keys", "release-signing.dpapi");
    static readonly byte[] entropy = Encoding.UTF8.GetBytes("VRC-SWITCHEROONIE release-signing v1");
    public static byte[] Initialize(string protectedKeyPath)
    {
        VersionStore.AssertNoReparse(protectedKeyPath);
        if (File.Exists(protectedKeyPath)) { using var existing = Open(protectedKeyPath); return existing.ExportSubjectPublicKeyInfo(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(protectedKeyPath))!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] privateBytes = key.ExportPkcs8PrivateKey();
        try
        {
            byte[] encrypted = Protect(privateBytes, false);
            using var output = new FileStream(protectedKeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(encrypted); output.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
        return key.ExportSubjectPublicKeyInfo();
    }
    public static ECDsa Open(string protectedKeyPath)
    {
        VersionStore.AssertNoReparse(protectedKeyPath);
        if (new FileInfo(protectedKeyPath).Length is <= 0 or > 8192) throw new InvalidDataException("Signing key storage is invalid.");
        byte[] privateBytes = Protect(File.ReadAllBytes(protectedKeyPath), true);
        try
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(privateBytes, out int read);
                if (read != privateBytes.Length || key.KeySize != 256) throw new CryptographicException("Wrong release key.");
                return key;
            }
            catch { key.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
    }
    static byte[] Protect(byte[] data, bool decrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows user DPAPI is required.");
        var input = Allocate(data); var additional = Allocate(entropy); Blob output = default; IntPtr description = IntPtr.Zero;
        try
        {
            bool success = decrypt
                ? CryptUnprotectData(ref input, out description, ref additional, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, "VRC-SWITCHEROONIE release key", ref additional, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Current-user key protection failed.");
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Clear(input); Clear(additional);
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length); LocalFree(output.Data); }
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }
    static Blob Allocate(byte[] data)
    { var value = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) }; Marshal.Copy(data, 0, value.Data, data.Length); return value; }
    static void Clear(Blob data)
    { if (data.Data != IntPtr.Zero) { Marshal.Copy(new byte[data.Length], 0, data.Data, data.Length); Marshal.FreeHGlobal(data.Data); } }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptUnprotectData(ref Blob input, out IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
}
