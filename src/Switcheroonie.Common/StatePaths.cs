using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Switcheroonie;

/// <summary>One process-user profile authority; resolving paths never creates directories.</summary>
public sealed class StatePaths
{
    public static readonly Guid ProfileFolderId = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
    public static readonly Guid LegacyLocalAppDataFolderId = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    static readonly Lazy<StatePaths> current = new(() => FromProfileDirectory(ResolveProcessKnownFolder(ProfileFolderId)));
    public static StatePaths Current => current.Value;
    public string ProfileDirectory { get; }
    public string DataDirectory { get; }
    public string ConfigurationDirectory { get; }
    public string ReportsDirectory { get; }
    public string DriverConfiguration => Path.Combine(ConfigurationDirectory, "driver.json");
    public string InstallationJournal => Path.Combine(ConfigurationDirectory, "installation-journal.json");

    StatePaths(string profile)
    {
        ProfileDirectory = profile;
        DataDirectory = Path.Combine(profile, "VRC-SWITCHEROONIE");
        ConfigurationDirectory = Path.Combine(DataDirectory, "config");
        ReportsDirectory = Path.Combine(DataDirectory, "reports");
    }

    // Explicit fixture entry point. Production uses Current and never environment-variable overrides.
    public static StatePaths FromProfileDirectory(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.IndexOf('\0') >= 0 ||
            !Path.IsPathFullyQualified(profile) || profile.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            profile.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("A canonical profile directory is required.");
        foreach (var component in profile.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ') ||
                component.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0 ||
                (component.Contains(':') && component != Path.GetPathRoot(profile)?.TrimEnd('\\', '/')))
                throw new ArgumentException("The profile directory contains an ambiguous component.");
        }
        return new StatePaths(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)));
    }

    /// <summary>For explicit migration/historical driver ownership only, never active configuration.</summary>
    public static string ResolveLegacyLocalDataDirectory() =>
        Path.Combine(ResolveProcessKnownFolder(LegacyLocalAppDataFolderId), "VRC-SWITCHEROONIE");

    // Same token and known-folder API as the native loader. The explicit process token avoids
    // resolving an impersonated thread's profile. No environment, current-directory or AppData fallback.
    public static string ResolveProcessKnownFolder(Guid folder)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows profile resolution is required.");
        if (!OpenProcessToken(GetCurrentProcess(), 0x000E, out var token))
            throw new IOException("Process profile token unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
        using (token)
        {
            IntPtr value = IntPtr.Zero;
            try
            {
                int result = SHGetKnownFolderPath(ref folder, 0, token, out value);
                if (result < 0 || value == IntPtr.Zero)
                    throw new IOException($"Process profile resolution failed (0x{result:X8}).");
                string? path = Marshal.PtrToStringUni(value);
                if (string.IsNullOrWhiteSpace(path)) throw new IOException("Process profile resolution returned an empty directory.");
                return path;
            }
            finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
        }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("shell32.dll", PreserveSig = true)]
    static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, SafeAccessTokenHandle token, out IntPtr path);
}
