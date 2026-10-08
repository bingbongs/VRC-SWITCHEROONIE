using System.Diagnostics;
namespace Switcheroonie.Update;

public interface IUpdateSafety
{
    bool CanActivate();
    bool NeedsInitialDriverRepair(string deliveryDirectory) => false;
    DriverTransitionResult PrepareDriver(string payload, ReleaseManifest manifest) => new("NotInstalled");
}
public sealed class WindowsUpdateSafety : IUpdateSafety
{
    // Read-only enumeration. Never closes, terminates, restarts or inputs into another application.
    static readonly string[] protectedNames = ["VRChat", "vrserver", "vrcompositor", "vrmonitor", "vrstartup",
        "Switcheroonie.UI", "Switcheroonie.Broker", "Switcheroonie.Cli", "switcheroonie-test-scene"];
    public static bool IsStoppedInventory(IEnumerable<string> runningNames) =>
        !runningNames.Any(name => protectedNames.Contains(name, StringComparer.OrdinalIgnoreCase));
    public bool CanActivate()
    {
        try
        {
            foreach (string name in protectedNames)
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) if (!process.HasExited) return false;
            return true;
        }
        catch { return false; } // Unknown process state is never permission to activate.
    }
    public DriverTransitionResult PrepareDriver(string payload, ReleaseManifest manifest) =>
        new DriverTransition(StatePaths.Current, Path.Combine(StatePaths.Current.DataDirectory, "updates"), CanActivate).Apply(payload, manifest);
    public bool NeedsInitialDriverRepair(string deliveryDirectory)
    {
        var paths = StatePaths.Current;
        VersionStore.AssertNoReparse(paths.InstallationJournal);
        if (!File.Exists(paths.InstallationJournal)) return false;
        return DriverInventoryNeedsRepair(Path.Combine(deliveryDirectory, "driver"),
            Path.Combine(paths.DataDirectory, "drivers", "0.1.0", "switcheroonie"));
    }
    public static bool DriverInventoryNeedsRepair(string packaged, string installed)
    {
        var expected = ReadDriverFiles(packaged);
        if (!expected.ContainsKey("driver.vrdrivermanifest") || !expected.ContainsKey("bin/win64/driver_switcheroonie.dll"))
            throw new InvalidDataException("Packaged companion inventory is incomplete.");
        VersionStore.AssertNoReparse(installed);
        if (!Directory.Exists(installed)) return true;
        var actual = ReadDriverFiles(installed);
        if (actual.Keys.Any(path => !expected.ContainsKey(path))) throw new IOException("Foreign driver resources require recovery review.");
        return expected.Count != actual.Count || expected.Any(item => !actual.TryGetValue(item.Key, out var hash) || hash != item.Value);
    }
    static Dictionary<string, string> ReadDriverFiles(string root)
    {
        VersionStore.AssertNoReparse(root);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>(); directories.Push(root);
        int count = 1; long bytes = 0;
        while (directories.TryPop(out var directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                VersionStore.AssertNoReparse(path);
                if (Directory.Exists(path))
                {
                    if (++count > 256) throw new InvalidDataException("Companion inventory exceeds its bound.");
                    directories.Push(path); continue;
                }
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!ManifestVerifier.SafeRelativePath(relative) || files.Count >= 256) throw new InvalidDataException("Companion path is invalid.");
                using var file = File.OpenRead(path);
                bytes = checked(bytes + file.Length);
                if (bytes > 64L * 1024 * 1024 || !files.TryAdd(relative, ManifestVerifier.Sha256(file)))
                    throw new InvalidDataException("Companion inventory exceeds its bound.");
            }
        }
        return files;
    }
}
