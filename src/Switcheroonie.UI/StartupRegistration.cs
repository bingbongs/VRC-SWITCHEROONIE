using Microsoft.Win32;
using System.IO;
using System.Runtime.Versioning;

namespace Switcheroonie.UI;

internal interface IStartupStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

internal readonly record struct StartupState(bool Enabled, bool Conflict, string Detail);

// The configuration core is pure when supplied with a fake store. Changes are
// made only for this exact executable and command; relocated/foreign entries
// with the same value name are preserved for their original owner to remove.
internal sealed class StartupRegistration
{
    internal const string ValueName = "VRC-SWITCHEROONIE";
    private readonly IStartupStore store;
    internal string Command { get; }
    internal string? ExactLegacyUiCommand { get; }

    internal StartupRegistration(IStartupStore store, string executable, Func<string, bool>? fileExists = null)
    {
        this.store = store;
        Command = CommandFor(executable, fileExists);
        var path = Path.GetFullPath(executable);
        string uiPath = Path.GetFileName(path).Equals("Switcheroonie.UI.exe", StringComparison.OrdinalIgnoreCase)
            ? path : Path.Combine(Path.GetDirectoryName(path)!, "Switcheroonie.UI.exe");
        string legacy = Quote(uiPath) + " --background";
        ExactLegacyUiCommand = legacy == Command ? null : legacy;
    }
    private static string Quote(string path)
    {
        if (path.Contains('"') || path.Contains('\r') || path.Contains('\n') || path.Contains('\0'))
            throw new ArgumentException("Invalid startup executable path");
        return "\"" + path + "\"";
    }
    internal static string CommandFor(string executable, Func<string, bool>? fileExists = null)
    {
        var path = Path.GetFullPath(executable);
        _ = Quote(path);
        if (Path.GetFileName(path).Equals("VRC-SWITCHEROONIE.exe", StringComparison.OrdinalIgnoreCase))
            return Quote(path) + " --launch --background";
        if (!Path.GetFileName(path).Equals("Switcheroonie.UI.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown startup executable", nameof(executable));
        string launcher = Path.Combine(Path.GetDirectoryName(path)!, "VRC-SWITCHEROONIE.exe");
        return (fileExists ?? File.Exists)(launcher)
            ? Quote(launcher) + " --launch --background" : Quote(path) + " --background";
    }
    internal bool IsExactLegacyUiValue(string? value) => value is not null && value == ExactLegacyUiCommand;
    internal StartupState Read()
    {
        var value = store.Read();
        return value is null ? new(false, false, "Off · launch the portable panel whenever you need it.") :
            value == Command ? new(true, false, "On · starts quietly for this Windows account at sign-in.") :
            IsExactLegacyUiValue(value) ? new(false, true, "The exact old panel startup entry is present. It was preserved for deliberate migration.") :
            new(false, true, "A startup entry with this name uses another path or command. It was preserved.");
    }
    internal StartupState Configure(bool enabled)
    {
        var value = store.Read();
        if (value is not null && value != Command) return Read();
        if (enabled && value is null) store.Write(Command);
        else if (!enabled && value == Command) store.Delete();
        return Read();
    }

    internal static int SelfTest()
    {
        int checks = 0;
        var store = new FakeStore();
        var registration = new StartupRegistration(store, @"C:\Portable tools\Switcheroonie.UI.exe", _ => false);
        void Require(bool condition) { ++checks; if (!condition) throw new InvalidOperationException("Startup ownership regression"); }
        Require(registration.Command == "\"C:\\Portable tools\\Switcheroonie.UI.exe\" --background");
        Require(!registration.Read().Enabled && !registration.Read().Conflict);
        Require(registration.Configure(true).Enabled && store.Value == registration.Command && store.Writes == 1);
        Require(registration.Configure(true).Enabled && store.Writes == 1);
        Require(!registration.Configure(false).Enabled && store.Value is null && store.Deletes == 1);
        registration.Configure(false); Require(store.Deletes == 1);
        foreach (string foreign in new[] { "\"C:\\Other\\Switcheroonie.UI.exe\" --background",
            registration.Command + " --other", registration.Command.Replace("--background", "--BACKGROUND"), "\0non-string registry value" })
        {
            store.Value = foreign;
            Require(registration.Read().Conflict);
            Require(registration.Configure(true).Conflict && store.Value == foreign && store.Writes == 1);
            Require(registration.Configure(false).Conflict && store.Value == foreign && store.Deletes == 1);
        }
        var stableStore = new FakeStore();
        string ui = @"C:\Portable tools\Switcheroonie.UI.exe", launcher = @"C:\Portable tools\VRC-SWITCHEROONIE.exe";
        var stable = new StartupRegistration(stableStore, ui, candidate => candidate == launcher);
        Require(stable.Command == "\"" + launcher + "\" --launch --background");
        Require(CommandFor(launcher, _ => throw new InvalidOperationException("Explicit launcher performs no file probe")) == stable.Command);
        Require(stable.Configure(true).Enabled && stableStore.Writes == 1);
        Require(stable.Configure(true).Enabled && stableStore.Writes == 1);
        Require(!stable.Configure(false).Enabled && stableStore.Deletes == 1);
        stableStore.Value = "\"" + ui + "\" --background";
        Require(stable.IsExactLegacyUiValue(stableStore.Value) && stable.Read().Conflict);
        Require(stable.Configure(true).Conflict && stableStore.Writes == 1 && stableStore.Value == stable.ExactLegacyUiCommand);
        Require(stable.Configure(false).Conflict && stableStore.Deletes == 1 && stableStore.Value == stable.ExactLegacyUiCommand);
        foreach (string foreign in new[] { "\"C:\\Other\\VRC-SWITCHEROONIE.exe\" --launch --background",
            stable.Command + " --other", "\"" + ui + "\" --BACKGROUND", "\"C:\\Other\\Switcheroonie.UI.exe\" --background" })
        {
            stableStore.Value = foreign;
            Require(!stable.IsExactLegacyUiValue(foreign) && stable.Configure(true).Conflict && stableStore.Value == foreign && stableStore.Writes == 1);
            Require(stable.Configure(false).Conflict && stableStore.Value == foreign && stableStore.Deletes == 1);
        }
        return checks;
    }
    private sealed class FakeStore : IStartupStore
    {
        internal string? Value;
        internal int Writes, Deletes;
        public string? Read() => Value;
        public void Write(string command) { Value = command; ++Writes; }
        public void Delete() { Value = null; ++Deletes; }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class RegistryStartupStore : IStartupStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string ownedCommand;
    internal RegistryStartupStore(string ownedCommand) => this.ownedCommand = ownedCommand;
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return AsCommand(key?.GetValue(StartupRegistration.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }
    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        var current = AsCommand(key.GetValue(StartupRegistration.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        if (current is not null && current != ownedCommand) return;
        if (command != ownedCommand) throw new ArgumentException("Unexpected startup command", nameof(command));
        key.SetValue(StartupRegistration.ValueName, command, RegistryValueKind.String);
    }
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is not null && AsCommand(key.GetValue(StartupRegistration.ValueName, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames)) == ownedCommand)
            key.DeleteValue(StartupRegistration.ValueName, throwOnMissingValue: false);
    }
    private static string? AsCommand(object? value) => value is null ? null : value as string ?? "\0non-string registry value";
}
