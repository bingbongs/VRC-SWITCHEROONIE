using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Switcheroonie.UI;

internal enum SetupOperation { None, Install, Enable }
internal sealed record SetupSnapshot(
    bool InventoryKnown = true, bool ProtectedProcessActive = false, bool InstallerRunning = false,
    bool PackageValid = true, bool RuntimeKnown = true, string RuntimeBuild = "25330290",
    bool JournalPresent = false, bool JournalValid = false, bool DriverPresent = false,
    bool Registered = false, bool Conflict = false, bool ConfigPresent = false,
    bool ConfigMatchesJournal = false, bool Enabled = false, bool DriverMatchesPackage = true, string Fingerprint = "fixture");
internal sealed record SetupState(string State, string Detail, SetupOperation Operation = SetupOperation.None)
{
    internal bool Ready => State == "Ready";
    internal bool CanRun => State is "Needed" or "EnableNeeded";
    internal bool ShowAction => State != "Ready" && State != "Checking";
    internal string ActionText => Operation == SetupOperation.Enable ? "Enable SteamVR setup" : "Set up SteamVR";
}
internal sealed record SetupRunResult(int? ExitCode, bool StillRunning = false);
internal sealed record SetupJournalReview(bool Valid, string InstallRoot = "", bool ConfigMatches = false);
internal interface ISetupBoundary
{
    SetupSnapshot Read();
    Task<SetupRunResult> RunAsync(SetupOperation operation, string expectedFingerprint);
}

// Only an explicit click reaches the reviewed installer. Status inspection has
// no writes, process launch, migration, registration, or recovery side effects.
internal sealed class SetupService(ISetupBoundary boundary)
{
    internal const string ApprovedRuntimeBuild = "25330290";
    // Updated only after reviewing the bundled installer source.
    internal const string ExpectedInstallerSha256 = "3A0D4C9067B399E2839C0E8354D69E60C4630C05DD97258ACAEF9072A71C7238";
    readonly SemaphoreSlim gate = new(1, 1);
    internal SetupState Inspect() => Evaluate(boundary.Read());
    internal static SetupState Evaluate(SetupSnapshot s)
    {
        if (!s.InventoryKnown) return new("Unavailable", "Setup check unavailable · try again");
        if (s.InstallerRunning) return new("Busy", "SteamVR setup is still finishing");
        if (!s.PackageValid) return new("Unavailable", "Setup files unavailable · use the complete app folder");
        if (!s.RuntimeKnown) return new("Unavailable", "Install or open SteamVR once, then close it and check again");
        if (s.RuntimeBuild != ApprovedRuntimeBuild) return new("Unsupported", "This SteamVR build is not supported by setup");
        if (s.Conflict || s.JournalPresent && (!s.JournalValid || !s.DriverPresent || !s.Registered || !s.ConfigPresent || !s.ConfigMatchesJournal))
            return new("Recovery", "Existing setup needs review · nothing was changed");
        if (s.JournalPresent && !s.DriverMatchesPackage)
            return new("UpdateNeeded", "Signed update needed · check for updates");
        if (s.JournalPresent && s.Enabled) return new("Ready", "SteamVR setup is ready");
        if (!s.JournalPresent && (s.DriverPresent || s.Registered || s.ConfigPresent))
            return new("Recovery", "Existing setup needs review · nothing was changed");
        var operation = s.JournalPresent ? SetupOperation.Enable : SetupOperation.Install;
        if (s.ProtectedProcessActive)
            return new("Deferred", "Close VRChat and SteamVR to finish setup", operation);
        return new(s.JournalPresent ? "EnableNeeded" : "Needed",
            s.JournalPresent ? "SteamVR setup is installed but disabled" : "Set up SteamVR once to use switching", operation);
    }
    internal async Task<SetupState> RunAsync()
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return new("Busy", "SteamVR setup is still finishing");
        try
        {
            var before = boundary.Read(); var state = Evaluate(before);
            if (!state.CanRun) return state;
            // Re-read immediately before launching. A new journal, registration,
            // changed configuration or newly opened session cannot inherit consent.
            var current = boundary.Read(); var checkedState = Evaluate(current);
            if (!checkedState.CanRun) return checkedState;
            if (current.Fingerprint != before.Fingerprint || state.Operation != checkedState.Operation)
                return new("Recovery", "Setup changed while checking · check again");
            var result = await boundary.RunAsync(state.Operation, current.Fingerprint).ConfigureAwait(false);
            if (result.StillRunning) return new("Busy", "SteamVR setup is still finishing");
            if (result.ExitCode != 0) return new("Recovery", "Setup did not finish · check again; recovery record retained");
            var finished = Inspect();
            return finished.Ready ? finished : new("Recovery", "Setup needs review · recovery record retained");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new("Recovery", "Setup did not finish · check again; recovery record retained"); }
        finally { gate.Release(); }
    }
}

internal sealed class WindowsSetupBoundary(string packageDirectory) : ISetupBoundary
{
    readonly string package = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
    Process? installer;
    FileStream? scriptLease;
    internal string InstallerPath => Path.Combine(package, "tools", "Manage-Driver.ps1");
    internal static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    internal static ProcessStartInfo StartInfo(string script, SetupOperation operation)
    {
        if (operation is not (SetupOperation.Install or SetupOperation.Enable)) throw new ArgumentException("No setup action.");
        var start = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(script)!, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Action", operation.ToString() }) start.ArgumentList.Add(arg);
        if (operation == SetupOperation.Install) { start.ArgumentList.Add("-EnableExperimental"); start.ArgumentList.Add("-FreshSetupOnly"); }
        else start.ArgumentList.Add("-OwnedSetupOnly");
        return start;
    }
    bool InstallerAlive()
    {
        if (installer is null) return false;
        try
        {
            if (!installer.HasExited) return true;
            installer.Dispose(); installer = null; scriptLease?.Dispose(); scriptLease = null; return false;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }
    public SetupSnapshot Read()
    {
        try { return ReadCore(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException or KeyNotFoundException or System.Security.SecurityException)
        { return new(InventoryKnown: false); }
    }
    SetupSnapshot ReadCore()
    {
        bool running = InstallerAlive();
        if (running) return new(InstallerRunning: true);
        bool protectedActive = false;
        foreach (var name in new[] { "VRChat", "vrserver", "vrcompositor", "vrmonitor", "vrstartup", "switcheroonie-test-scene" })
        {
            var processes = Process.GetProcessesByName(name);
            protectedActive |= processes.Length != 0;
            foreach (var process in processes) process.Dispose();
        }
        var statePaths = StatePaths.Current;
        string localData = StatePaths.ResolveProcessKnownFolder(StatePaths.LegacyLocalAppDataFolderId);
        string relocated = Path.Combine(statePaths.DataDirectory, "drivers", "0.1.0", "switcheroonie");
        string legacy = Path.Combine(localData, "VRC-SWITCHEROONIE", "drivers", "0.1.0", "switcheroonie");
        string[] allowedRoots = [relocated, legacy];
        var script = ReadText(InstallerPath, package);
        bool packageValid = script is not null && HashFile(InstallerPath) == SetupService.ExpectedInstallerSha256 && File.Exists(PowerShellPath);
        string packageManifest = Path.Combine(package, "driver", "driver.vrdrivermanifest");
        string packageDll = Path.Combine(package, "driver", "bin", "win64", "driver_switcheroonie.dll");
        packageValid &= ValidManifest(ReadText(packageManifest, package)) && SafeExistingPath(packageDll, package) && File.Exists(packageDll);
        packageValid &= ReadText(Path.Combine(package, "driver", "resources", "settings", "default.vrsettings"), package) is not null;
        var packagedFiles = packageValid ? DriverFiles(Path.Combine(package, "driver")) : null;
        packageValid &= packagedFiles is not null;
        string pathsFile = Path.Combine(localData, "openvr", "openvrpaths.vrpath");
        string? pathsText = ReadText(pathsFile, localData);
        if (pathsText is null) return new(ProtectedProcessActive: protectedActive, InstallerRunning: running, PackageValid: packageValid, RuntimeKnown: false);
        using var paths = JsonDocument.Parse(pathsText);
        var runtimes = paths.RootElement.GetProperty("runtime");
        if (runtimes.ValueKind != JsonValueKind.Array || runtimes.GetArrayLength() == 0) throw new JsonException("Runtime absent");
        string runtime = Canonical(runtimes[0].GetString()!);
        bool runtimeKnown = File.Exists(Path.Combine(runtime, "bin", "win64", "vrpathreg.exe")) && SafeExistingPath(runtime, Path.GetPathRoot(runtime)!);
        string manifestPath = Path.Combine(Directory.GetParent(runtime)?.Parent?.FullName ?? throw new IOException("Runtime path unavailable"), "appmanifest_250820.acf");
        string build = System.Text.RegularExpressions.Regex.Match(ReadText(manifestPath, Path.GetPathRoot(manifestPath)!) ?? "", "\"buildid\"\\s*\"(\\d+)\"").Groups[1].Value;
        var roots = new List<string>();
        if (paths.RootElement.TryGetProperty("external_drivers", out var external) && external.ValueKind != JsonValueKind.Null)
        {
            if (external.ValueKind != JsonValueKind.Array || external.GetArrayLength() > 128) throw new JsonException("Driver inventory invalid");
            foreach (var value in external.EnumerateArray()) roots.Add(Canonical(value.GetString()!));
        }
        bool conflict = allowedRoots.Any(root => roots.Count(x => Same(x, root)) > 1);
        foreach (var root in roots.Where(root => !allowedRoots.Any(allowed => Same(root, allowed))))
        {
            string? otherManifest = ReadText(Path.Combine(root, "driver.vrdrivermanifest"), Path.GetPathRoot(root)!);
            if (otherManifest is not null && HasHarnessName(otherManifest)) conflict = true;
        }
        string? journalText = ReadText(statePaths.InstallationJournal, statePaths.ProfileDirectory);
        string? configText = ReadText(statePaths.DriverConfiguration, statePaths.ProfileDirectory);
        bool journalPresent = journalText is not null, journalValid = false, matches = false, enabled = false;
        string installRoot = relocated;
        if (journalPresent)
        {
            var review = ReviewJournal(journalText!, configText, allowedRoots);
            journalValid = review.Valid; installRoot = review.InstallRoot; matches = review.ConfigMatches;
            if (!journalValid) return new(ProtectedProcessActive: protectedActive, InstallerRunning: running, PackageValid: packageValid,
                RuntimeKnown: runtimeKnown, RuntimeBuild: build, JournalPresent: true, Conflict: true);
            conflict |= roots.Any(x => allowedRoots.Any(a => Same(x, a)) && !Same(x, installRoot));
        }
        else conflict |= allowedRoots.Any(root => Directory.Exists(root) || roots.Any(x => Same(x, root)));
        if (configText is not null)
        {
            using var config = JsonDocument.Parse(configText);
            bool optInValid = config.RootElement.TryGetProperty("experimentalOptIn", out var optIn) && optIn.ValueKind is JsonValueKind.True or JsonValueKind.False;
            bool approvedValid = config.RootElement.TryGetProperty("approvedRuntimeBuild", out var approved) && approved.ValueKind == JsonValueKind.String && approved.GetString() == SetupService.ApprovedRuntimeBuild;
            conflict |= journalPresent && (!optInValid || !approvedValid);
            enabled = optInValid && optIn.ValueKind == JsonValueKind.True && approvedValid;
        }
        string installedDll = Path.Combine(installRoot, "bin", "win64", "driver_switcheroonie.dll");
        bool installed = Directory.Exists(installRoot) && SafeExistingPath(installedDll, Path.GetPathRoot(installRoot)!) &&
            ValidManifest(ReadText(Path.Combine(installRoot, "driver.vrdrivermanifest"), Path.GetPathRoot(installRoot)!)) &&
            File.Exists(installedDll);
        var installedFiles = installed ? DriverFiles(installRoot) : null;
        string fingerprint = HashText(string.Join('\n', package, HashText(script ?? ""), TreeFingerprint(packagedFiles), runtime, build, string.Join('|', roots), journalText ?? "", configText ?? "", installRoot, TreeFingerprint(installedFiles)));
        return new(ProtectedProcessActive: protectedActive, InstallerRunning: running, PackageValid: packageValid,
            RuntimeKnown: runtimeKnown, RuntimeBuild: build, JournalPresent: journalPresent, JournalValid: journalValid,
            DriverPresent: installed, Registered: roots.Any(x => Same(x, installRoot)), Conflict: conflict,
            ConfigPresent: configText is not null, ConfigMatchesJournal: matches, Enabled: enabled,
            DriverMatchesPackage: FilesMatch(packagedFiles, installedFiles), Fingerprint: fingerprint);
    }
    public async Task<SetupRunResult> RunAsync(SetupOperation operation, string expectedFingerprint)
    {
        if (InstallerAlive()) return new(null, true);
        // Final platform check, independently of the service's two snapshots.
        var finalSnapshot = Read(); var finalState = SetupService.Evaluate(finalSnapshot);
        if (finalState.Operation != operation || !finalState.CanRun || finalSnapshot.Fingerprint != expectedFingerprint) return new(-1);
        // Deny writes and replacement of the approved script while PowerShell
        // reads/executes it, including a slow installer retained after timeout.
        scriptLease = File.Open(InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(scriptLease)) != SetupService.ExpectedInstallerSha256)
        { scriptLease.Dispose(); scriptLease = null; return new(-1); }
        try { installer = Process.Start(StartInfo(InstallerPath, operation)) ?? throw new IOException("Setup could not start"); }
        catch { scriptLease.Dispose(); scriptLease = null; throw; }
        var output = DrainAsync(installer.StandardOutput); var error = DrainAsync(installer.StandardError);
        try { await installer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false); }
        catch (TimeoutException) { return new(null, true); } // Never kill an installer mid-journal write.
        await Task.WhenAll(output, error).ConfigureAwait(false);
        int code = installer.ExitCode; installer.Dispose(); installer = null; scriptLease.Dispose(); scriptLease = null; return new(code);
    }
    static async Task DrainAsync(StreamReader reader) { char[] buffer = new char[4096]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
    static bool Same(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);
    static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Contains('\0')) throw new IOException("Ambiguous setup path");
        foreach (string part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) throw new IOException("Ambiguous setup path");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    static bool SafeExistingPath(string path, string authorityRoot)
    {
        string full = Canonical(path), root = Canonical(authorityRoot);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!Same(full, root) && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }
    static string? ReadText(string path, string authorityRoot)
    {
        if (!SafeExistingPath(path, authorityRoot)) throw new IOException("Setup path changed");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 262144) throw new IOException("Setup file too large");
        return File.ReadAllText(path);
    }
    static string? ConfigText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var wrapped) && wrapped.ValueKind == JsonValueKind.String)
        {
            string text = wrapped.GetString()!; using var parsed = JsonDocument.Parse(text);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object) return text;
        }
        throw new JsonException("Invalid ownership record");
    }
    internal static SetupJournalReview ReviewJournal(string text, string? config, IReadOnlyList<string> allowedRoots)
    {
        try
        {
            using var journal = JsonDocument.Parse(text); var j = journal.RootElement;
            string root = Canonical(j.GetProperty("installRoot").GetString()!);
            bool valid = j.GetProperty("version").GetInt32() == 1 && j.GetProperty("phase").GetString() == "installed" &&
                allowedRoots.Any(x => Same(x, root)) && j.GetProperty("originalRegistration").ValueKind == JsonValueKind.False;
            if (j.TryGetProperty("freshSetupRootCreated", out var created)) valid &= created.ValueKind == JsonValueKind.True;
            _ = ConfigText(j.GetProperty("originalConfig"));
            string? last = ConfigText(j.GetProperty("lastConfig"));
            if (last is null) valid = false;
            if (j.TryGetProperty("ownedInstallRoots", out var owned))
            {
                valid &= owned.ValueKind == JsonValueKind.Array && owned.GetArrayLength() is > 0 and <= 2;
                if (owned.ValueKind == JsonValueKind.Array)
                    foreach (var value in owned.EnumerateArray()) valid &= allowedRoots.Any(x => Same(x, Canonical(value.GetString()!)));
            }
            return new(valid, valid ? root : "", last is not null && config == last);
        }
        catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return new(false); }
    }
    static bool HasHarnessName(string text) { using var manifest = JsonDocument.Parse(text); return manifest.RootElement.TryGetProperty("name", out var name) && name.GetString() == "switcheroonie"; }
    static bool ValidManifest(string? text)
    {
        if (text is null) return false;
        using var manifest = JsonDocument.Parse(text); var m = manifest.RootElement;
        return m.TryGetProperty("name", out var name) && name.GetString() == "switcheroonie" &&
            m.TryGetProperty("resourceOnly", out var resource) && resource.ValueKind == JsonValueKind.False &&
            m.TryGetProperty("alwaysActivate", out var activate) && activate.ValueKind == JsonValueKind.True &&
            (!m.TryGetProperty("directory", out var directory) || directory.ValueKind == JsonValueKind.String && directory.GetString() == "");
    }
    static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static bool FilesMatch(IReadOnlyDictionary<string, string>? packaged, IReadOnlyDictionary<string, string>? installed) =>
        packaged is not null && installed is not null && packaged.Count != 0 && packaged.Count == installed.Count &&
        packaged.All(entry => installed.TryGetValue(entry.Key, out var value) && value == entry.Value);
    static string TreeFingerprint(IReadOnlyDictionary<string, string>? files) => files is null ? "invalid" :
        HashText(string.Join('\n', files.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + ":" + x.Value)));
    static Dictionary<string, string>? DriverFiles(string root)
    {
        if (!Directory.Exists(root) || !SafeExistingPath(root, root)) return null;
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(root);
        int directories = 0; long total = 0;
        while (pending.Count != 0)
        {
            if (++directories > 256) return null;
            foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (!SafeExistingPath(path, root)) return null;
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                long length = new FileInfo(path).Length;
                if (length < 0 || length > 64 * 1024 * 1024 - total || files.Count >= 256) return null;
                total += length;
                if (!files.TryAdd(Path.GetRelativePath(root, path), length + ":" + HashFile(path))) return null;
            }
        }
        return files.ContainsKey("driver.vrdrivermanifest") && files.ContainsKey(Path.Combine("bin", "win64", "driver_switcheroonie.dll")) ? files : null;
    }
}
