using System.Diagnostics;
namespace Switcheroonie.Update;

public interface IUpdateSafety
{
    bool CanActivate();
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
}
