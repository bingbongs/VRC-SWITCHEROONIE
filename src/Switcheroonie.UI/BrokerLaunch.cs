using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Switcheroonie.UI;

internal static class BrokerLaunch
{
    internal static ProcessStartInfo Create() => ForParent(AppContext.BaseDirectory, Environment.ProcessId);

    // The broker authenticates and retains this process identity. Hiding the
    // resident UI keeps it alive; only an actual UI exit retires its child.
    internal static ProcessStartInfo ForParent(string uiDirectory, int uiProcessId)
    {
        if (!Path.IsPathFullyQualified(uiDirectory)) throw new ArgumentException("An absolute UI directory is required.", nameof(uiDirectory));
        if (uiProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(uiProcessId));
        string directory = Path.GetFullPath(uiDirectory);
        var start = new ProcessStartInfo(Path.Combine(directory, "Switcheroonie.Broker.exe"))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--ui-parent=" + uiProcessId.ToString(CultureInfo.InvariantCulture));
        return start;
    }

    internal static void SelfTest(Action<bool, string> check)
    {
        var actual = Create();
        check(actual.FileName == Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "Switcheroonie.Broker.exe"), "Broker launch uses the exact adjacent executable");
        check(actual.WorkingDirectory == Path.GetFullPath(AppContext.BaseDirectory), "Broker launch stays in the UI package directory");
        check(!actual.UseShellExecute && actual.CreateNoWindow, "Broker child launch stays hidden without a shell");
        check(actual.Arguments.Length == 0 && actual.ArgumentList.SequenceEqual(new[] { "--ui-parent=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }), "Broker parent argument is the current UI PID in invariant decimal");
        string fixtureDirectory = Path.Combine(Path.GetTempPath(), "UI parent \u03a9 \u96ea");
        var unicode = ForParent(fixtureDirectory, 5312);
        check(unicode.FileName == Path.Combine(fixtureDirectory, "Switcheroonie.Broker.exe") && unicode.ArgumentList.Single() == "--ui-parent=5312", "Unicode and spaced paths remain separate from the parent argument");
        bool Invalid(string directory, int pid)
        {
            try { ForParent(directory, pid); return false; }
            catch (ArgumentException) { return true; }
        }
        check(Invalid(fixtureDirectory, 0) && Invalid(fixtureDirectory, -1), "Broker launch refuses invalid parent identities");
        check(Invalid("relative-package", 5312), "Broker launch refuses relative executable authority");
        check(ResidentTray.QuitLabel == "Quit VRC-SWITCHEROONIE", "Tray exit truthfully offers a full application quit");
    }
}
