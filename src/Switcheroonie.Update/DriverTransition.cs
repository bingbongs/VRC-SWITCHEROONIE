using System.Text.Json;
namespace Switcheroonie.Update;

public sealed record DriverTransitionResult(string State, string? BackupId = null);
public sealed class DriverTransition
{
    readonly StatePaths paths;
    readonly string updatesRoot;
    readonly Func<bool> stopped;
    public DriverTransition(StatePaths paths, string updatesRoot, Func<bool> stopped)
    { this.paths = paths; this.updatesRoot = Path.GetFullPath(updatesRoot); this.stopped = stopped; }
    sealed record BackupFile(string Relative, bool Existed, string? PreviousSha256, string NewSha256);
    public DriverTransitionResult Apply(string payload, ReleaseManifest manifest)
    {
        string installed = Path.Combine(paths.DataDirectory, "drivers", "0.1.0", "switcheroonie");
        if (!File.Exists(paths.InstallationJournal) && !Directory.Exists(installed)) return new("NotInstalled");
        if (!stopped()) throw new IOException("Driver replacement waits for normal runtime shutdown.");
        VersionStore.AssertNoReparse(installed); VersionStore.AssertNoReparse(paths.InstallationJournal);
        if (!File.Exists(paths.InstallationJournal) || new FileInfo(paths.InstallationJournal).Length > 65536)
            throw new IOException("Owned driver journal is unavailable.");
        using var journal = JsonDocument.Parse(File.ReadAllBytes(paths.InstallationJournal));
        var root = journal.RootElement;
        string? recordedRoot = root.GetProperty("installRoot").GetString();
        if (recordedRoot is null || !Path.IsPathFullyQualified(recordedRoot) ||
            !Path.GetFullPath(recordedRoot).Equals(installed, StringComparison.OrdinalIgnoreCase) ||
            root.GetProperty("phase").GetString() != "installed" || root.GetProperty("lastConfig").ValueKind != JsonValueKind.String)
            throw new IOException("Only the canonical journal-owned driver may be updated.");
        string expectedConfig = root.GetProperty("lastConfig").GetString()!;
        VersionStore.AssertNoReparse(paths.DriverConfiguration);
        if (!File.Exists(paths.DriverConfiguration) || File.ReadAllText(paths.DriverConfiguration) != expectedConfig)
            throw new IOException("Driver configuration conflict; retained for review.");
        string registration = Path.Combine(StatePaths.ResolveProcessKnownFolder(StatePaths.LegacyLocalAppDataFolderId), "openvr", "openvrpaths.vrpath");
        // Tests can exercise the transition without host registration discovery.
        return ApplyOwned(payload, manifest, installed, expectedConfig, registration);
    }
    // Explicit fixture boundary. Production passes a known-folder registration file.
    public DriverTransitionResult ApplyFixture(string payload, ReleaseManifest manifest, string registrationFile)
    {
        string installed = Path.Combine(paths.DataDirectory, "drivers", "0.1.0", "switcheroonie");
        VersionStore.AssertNoReparse(installed);
        using var journal = JsonDocument.Parse(File.ReadAllBytes(paths.InstallationJournal));
        var root = journal.RootElement;
        if (root.GetProperty("installRoot").GetString() != installed || root.GetProperty("phase").GetString() != "installed")
            throw new IOException("Fixture journal ownership failed.");
        return ApplyOwned(payload, manifest, installed, root.GetProperty("lastConfig").GetString()!, registrationFile);
    }
    DriverTransitionResult ApplyOwned(string payload, ReleaseManifest manifest, string installed, string expectedConfig, string registration)
    {
        if (!stopped() || !File.Exists(paths.DriverConfiguration) || new FileInfo(paths.DriverConfiguration).Length > 65536 ||
            File.ReadAllText(paths.DriverConfiguration) != expectedConfig) throw new IOException("Runtime or configuration conflict; driver retained.");
        VersionStore.AssertNoReparse(registration);
        if (new FileInfo(registration).Length > 256 * 1024) throw new InvalidDataException("Registration file exceeds bounds.");
        byte[] registrationBytes = File.ReadAllBytes(registration), journalBytes = File.ReadAllBytes(paths.InstallationJournal);
        bool AuthorityStillMatches()
        {
            if (!stopped()) return false;
            VersionStore.AssertNoReparse(registration); VersionStore.AssertNoReparse(paths.InstallationJournal);
            VersionStore.AssertNoReparse(paths.DriverConfiguration);
            return new FileInfo(registration).Length == registrationBytes.Length &&
                new FileInfo(paths.InstallationJournal).Length == journalBytes.Length &&
                File.ReadAllBytes(registration).AsSpan().SequenceEqual(registrationBytes) &&
                File.ReadAllBytes(paths.InstallationJournal).AsSpan().SequenceEqual(journalBytes) &&
                new FileInfo(paths.DriverConfiguration).Length <= 65536 &&
                File.ReadAllText(paths.DriverConfiguration) == expectedConfig;
        }
        bool ExactTarget(string target, bool existed, string? hash)
        {
            VersionStore.AssertNoReparse(target);
            if (!existed) return !File.Exists(target) && !Directory.Exists(target);
            if (!File.Exists(target)) return false;
            using var file = File.OpenRead(target);
            return ManifestVerifier.Sha256(file).Equals(hash, StringComparison.OrdinalIgnoreCase);
        }
        using (var registered = JsonDocument.Parse(registrationBytes))
            if (!registered.RootElement.GetProperty("external_drivers").EnumerateArray().Any(x =>
                string.Equals(x.GetString(), installed, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Owned driver registration is missing.");
        AssertCompanion(Path.Combine(installed, "driver.vrdrivermanifest"));
        AssertCompanion(Path.Combine(payload, "driver", "driver.vrdrivermanifest"));
        var files = manifest.Files.Where(x => x.Path.StartsWith("driver/", StringComparison.Ordinal)).ToArray();
        if (files.Length == 0) throw new InvalidDataException("Signed release contains no companion driver.");
        WindowsUpdateSafety.DriverInventoryNeedsRepair(Path.Combine(payload, "driver"), installed);
        var backup = new List<BackupFile>();
        foreach (var item in files)
        {
            string relative = item.Path["driver/".Length..];
            if (!ManifestVerifier.SafeRelativePath(relative)) throw new InvalidDataException("Unsafe driver relative path.");
            string source = Path.Combine(payload, item.Path.Replace('/', Path.DirectorySeparatorChar));
            string target = Path.Combine(installed, relative.Replace('/', Path.DirectorySeparatorChar));
            VersionStore.AssertNoReparse(source); VersionStore.AssertNoReparse(target);
            using var sourceStream = File.OpenRead(source);
            if (sourceStream.Length != item.Bytes || !ManifestVerifier.Sha256(sourceStream).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Driver source changed after verification.");
            bool exists = File.Exists(target);
            string? oldHash = null;
            if (exists) { using var current = File.OpenRead(target); oldHash = ManifestVerifier.Sha256(current); }
            if (oldHash?.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase) != true)
                backup.Add(new(relative, exists, oldHash, item.Sha256));
        }
        if (backup.Count == 0)
        {
            if (!AuthorityStillMatches()) throw new IOException("Runtime or ownership changed before driver match confirmation.");
            return new("Matched");
        }
        string id = Guid.NewGuid().ToString("N"); string backupRoot = Path.Combine(updatesRoot, "driver-backups", id);
        VersionStore.AssertNoReparse(backupRoot); Directory.CreateDirectory(backupRoot);
        foreach (var item in backup.Where(x => x.Existed))
        {
            string target = Path.Combine(backupRoot, item.Relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(installed, item.Relative.Replace('/', Path.DirectorySeparatorChar)), target, false);
            using var file = File.OpenRead(target);
            if (ManifestVerifier.Sha256(file) != item.PreviousSha256) throw new IOException("Driver changed before backup.");
        }
        File.WriteAllText(Path.Combine(backupRoot, "receipt.json"), JsonSerializer.Serialize(backup));
        var committed = new List<BackupFile>();
        try
        {
            foreach (var item in backup)
            {
                if (!stopped() || File.ReadAllText(paths.DriverConfiguration) != expectedConfig)
                    throw new IOException("Runtime or configuration changed; driver update deferred.");
                string target = Path.Combine(installed, item.Relative.Replace('/', Path.DirectorySeparatorChar));
                VersionStore.AssertNoReparse(target);
                if (item.Existed)
                {
                    using var file = File.OpenRead(target);
                    if (ManifestVerifier.Sha256(file) != item.PreviousSha256) throw new IOException("Installed driver file conflict.");
                }
                else if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Unexpected driver destination appeared.");
                AtomicCopy(Path.Combine(payload, "driver", item.Relative.Replace('/', Path.DirectorySeparatorChar)), target, item.NewSha256,
                    () => AuthorityStillMatches() && ExactTarget(target, item.Existed, item.PreviousSha256));
                committed.Add(item);
            }
            if (!AuthorityStillMatches()) throw new IOException("Runtime or ownership changed; version activation is deferred.");
            return new("Updated", id);
        }
        catch
        {
            // Restore only our own last exact write. A foreign change is never overwritten.
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                // A runtime may now hold the new DLL. Leave the marker/backups for the next stopped window.
                if (!stopped()) break;
                string target = Path.Combine(installed, item.Relative.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (File.ReadAllText(paths.DriverConfiguration) != expectedConfig) break;
                    VersionStore.AssertNoReparse(target);
                    using (var file = File.OpenRead(target))
                        if (ManifestVerifier.Sha256(file) != item.NewSha256) continue;
                    if (!stopped()) break;
                    if (item.Existed) AtomicCopy(Path.Combine(backupRoot, item.Relative.Replace('/', Path.DirectorySeparatorChar)), target, item.PreviousSha256,
                        () => AuthorityStillMatches() && ExactTarget(target, true, item.NewSha256));
                    else { if (!AuthorityStillMatches() || !ExactTarget(target, true, item.NewSha256)) break; File.Delete(target); }
                }
                catch (IOException) { /* Preserve immutable backup and receipt for recovery. */ }
            }
            throw;
        }
    }
    static void AssertCompanion(string path)
    {
        VersionStore.AssertNoReparse(path);
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Driver definition exceeds bounds.");
        using var definition = JsonDocument.Parse(File.ReadAllBytes(path));
        if (definition.RootElement.GetProperty("name").GetString() != "switcheroonie" ||
            definition.RootElement.GetProperty("hmd_presence").GetArrayLength() != 0)
            throw new IOException("Only the owned non-HMD companion may be updated.");
    }
    static void AtomicCopy(string source, string target, string? expectedHash = null, Func<bool>? mutationAllowed = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); VersionStore.AssertNoReparse(target);
        string temporary = target + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            if (expectedHash is not null)
            {
                using var verification = File.OpenRead(temporary);
                if (!ManifestVerifier.Sha256(verification).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Source changed while preparing the atomic driver write.");
            }
            VersionStore.AssertNoReparse(target);
            if (mutationAllowed is not null && !mutationAllowed()) throw new IOException("Runtime became active before driver restoration.");
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
