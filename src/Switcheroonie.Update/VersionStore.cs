using System.IO.Compression;
using System.Text.Json;
namespace Switcheroonie.Update;

public sealed class VersionStore
{
    public string Root { get; }
    public string VersionsDirectory => Path.Combine(Root, "versions");
    public string? PendingVersion => ReadJson<Pending>("pending.json")?.Version;
    public VersionSelection? Current => ReadJson<VersionSelection>("current.json");
    public bool TransitionPending => ReadJson<Transition>("transition.json") is not null;
    public long HighestSequence => Math.Max(ReadJson<HighWater>("highest.json")?.Sequence ?? 0, Current?.HighestSequence ?? 0);
    sealed record Pending(string Version);
    sealed record HighWater(long Sequence);
    sealed record Transition(string Version, string? PreviousVersion, string Kind);
    sealed record Bootstrap(string Directory, string? Version = null);
    public VersionStore(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Update store must be absolute.");
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); AssertNoReparse(Root);
    }
    public FileStream AcquireLock()
    {
        AssertNoReparse(Root); Directory.CreateDirectory(Root); AssertNoReparse(Root);
        return new FileStream(Path.Combine(Root, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal static void AssertNoReparse(string path)
    {
        for (string? cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths may not contain reparse points.");
    }
    T? ReadJson<T>(string name)
    {
        string path = Path.Combine(Root, name); AssertNoReparse(path);
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Update state exceeds its bound.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), ManifestVerifier.Json)
            ?? throw new InvalidDataException("Update state is invalid.");
    }
    void WriteJson<T>(string name, T value)
    {
        string target = Path.Combine(Root, name); AssertNoReparse(target);
        string temporary = Path.Combine(Root, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, ManifestVerifier.Json); stream.Flush(true); }
            AssertNoReparse(target); File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public string VersionDirectory(string version)
    { ReleaseTrust.ParseVersion(version); return Path.Combine(VersionsDirectory, version); }
    public string PayloadDirectory(string version) => Path.Combine(VersionDirectory(version), "payload");
    public ReleaseManifest VerifyVersion(string version, ReleaseTrust trust)
    {
        var folder = VersionDirectory(version); AssertNoReparse(folder);
        var bytes = ReadBounded(Path.Combine(folder, "release-manifest.json"), ManifestVerifier.MaxManifestBytes);
        var signature = ReadBounded(Path.Combine(folder, "release-signature.bin"), 64);
        var manifest = ManifestVerifier.Verify(bytes, signature, trust);
        if (manifest.Version != version) throw new InvalidDataException("Version directory does not match its signed receipt.");
        VerifyPayload(PayloadDirectory(version), manifest); return manifest;
    }
    static byte[] ReadBounded(string path, long limit)
    {
        AssertNoReparse(path); using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException("Receipt exceeds its bound.");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    static void VerifyPayload(string payload, ReleaseManifest manifest)
    {
        AssertNoReparse(payload);
        var inventory = manifest.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        // Reject reparse points before descending: never recurse into a foreign link.
        var directories = new Stack<string>(); directories.Push(payload);
        while (directories.TryPop(out var directory))
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                AssertNoReparse(path);
                if (Directory.Exists(path)) { directories.Push(path); continue; }
                var relative = Path.GetRelativePath(payload, path).Replace('\\', '/');
                if (!inventory.ContainsKey(relative)) throw new InvalidDataException("Version contains an unsigned file.");
            }
        foreach (var item in manifest.Files)
        {
            string path = Path.Combine(payload, item.Path.Replace('/', Path.DirectorySeparatorChar)); AssertNoReparse(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != item.Bytes || !ManifestVerifier.Sha256(file).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged version no longer matches the signed inventory.");
        }
    }
    // Caller holds AcquireLock. Never overwrites an existing version or running binary.
    public async Task StageAsync(Stream archiveStream, ReleaseManifest manifest, byte[] manifestBytes, byte[] signature,
        ReleaseTrust trust, CancellationToken cancellationToken = default) =>
        await StageCoreAsync(archiveStream, manifest, manifestBytes, signature, trust, false, cancellationToken);
    public async Task StageBootstrapAsync(Stream archiveStream, ReleaseManifest manifest, byte[] manifestBytes, byte[] signature,
        ReleaseTrust trust, CancellationToken cancellationToken = default) =>
        await StageCoreAsync(archiveStream, manifest, manifestBytes, signature, trust, true, cancellationToken);
    public async Task StageOriginAsync(Stream archiveStream, ReleaseManifest manifest, byte[] manifestBytes, byte[] signature,
        ReleaseTrust trust, CancellationToken cancellationToken = default) =>
        await StageCoreAsync(archiveStream, manifest, manifestBytes, signature, trust, true, cancellationToken,
            RememberedBootstrapVersion(trust) ?? throw new InvalidDataException("No original launcher is recorded."));
    async Task StageCoreAsync(Stream archiveStream, ReleaseManifest manifest, byte[] manifestBytes, byte[] signature,
        ReleaseTrust trust, bool bootstrap, CancellationToken cancellationToken, string? originReceiptVersion = null)
    {
        var verified = ManifestVerifier.Verify(manifestBytes, signature, trust);
        if (verified.Version != manifest.Version || verified.ArchiveSha256 != manifest.ArchiveSha256)
            throw new InvalidDataException("Staging must use the verified manifest.");
        manifest = verified;
        long baselineSequence = !bootstrap && Current is null && Directory.Exists(VersionDirectory(trust.BaselineVersion))
            ? VerifyVersion(trust.BaselineVersion, trust).Sequence : 0;
        string? originVersion = !bootstrap && Current is null ? RememberedBootstrapVersion(trust) : null;
        long originSequence = originVersion is not null && Directory.Exists(VersionDirectory(originVersion))
            ? VerifyVersion(originVersion, trust).Sequence : 0;
        if (bootstrap ? manifest.Version != (originReceiptVersion ?? trust.BaselineVersion) ||
                (originReceiptVersion is null && Current is not null) :
            ReleaseTrust.ParseVersion(manifest.Version) <= ReleaseTrust.ParseVersion(Current?.Version ?? originVersion ?? trust.BaselineVersion) ||
            manifest.Sequence <= Math.Max(HighestSequence, Math.Max(baselineSequence, originSequence)))
            throw new InvalidDataException("Older, repeated or downgrade releases are refused.");
        string cachedVersion = VersionDirectory(manifest.Version);
        if (Directory.Exists(cachedVersion))
        {
            // A crash can leave the immutable payload committed before its selection metadata.
            VerifyVersion(manifest.Version, trust);
            if (!ReadBounded(Path.Combine(cachedVersion, "release-manifest.json"), ManifestVerifier.MaxManifestBytes).AsSpan().SequenceEqual(manifestBytes))
                throw new IOException("Existing signed version differs from the release feed; never overwritten.");
            if (!bootstrap)
            {
                WriteJson("pending.json", new Pending(manifest.Version));
                WriteJson("highest.json", new HighWater(manifest.Sequence));
            }
            return;
        }
        string stagingRoot = Path.Combine(Root, "staging"); AssertNoReparse(stagingRoot); Directory.CreateDirectory(stagingRoot);
        string stage = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            string archivePath = Path.Combine(stage, "download.zip");
            await using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await CopyBounded(archiveStream, file, manifest.ArchiveBytes, cancellationToken);
                await file.FlushAsync(cancellationToken); file.Flush(true);
            }
            using (var file = File.OpenRead(archivePath))
                if (file.Length != manifest.ArchiveBytes || !ManifestVerifier.Sha256(file).Equals(manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded archive hash or size failed.");
            string candidate = Path.Combine(stage, "candidate"), payload = Path.Combine(candidate, "payload");
            Directory.CreateDirectory(payload);
            var expected = manifest.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = $"VRC-SWITCHEROONIE-{manifest.Version}/";
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.Entries.Count > ManifestVerifier.MaxFiles * 2) throw new InvalidDataException("Too many archive entries.");
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) ||
                        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Unsafe archive entry or root.");
                    string relative = entry.FullName[prefix.Length..];
                    if (entry.FullName.EndsWith('/'))
                    {
                        if (relative.Length != 0 && (!ManifestVerifier.SafeRelativePath(relative.TrimEnd('/')) ||
                            !expected.Keys.Any(x => x.StartsWith(relative, StringComparison.OrdinalIgnoreCase))))
                            throw new InvalidDataException("Unexpected archive directory.");
                        continue;
                    }
                    if (!ManifestVerifier.SafeRelativePath(relative) || !seen.Add(relative) ||
                        !expected.TryGetValue(relative, out var item) || entry.Length != item.Bytes ||
                        (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 400))
                        throw new InvalidDataException("Archive entry does not match signed inventory.");
                    string target = Path.Combine(payload, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!Path.GetFullPath(target).StartsWith(payload + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Archive path escaped staging.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var source = entry.Open();
                    await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    await CopyBounded(source, destination, item.Bytes, cancellationToken);
                }
            }
            if (seen.Count != expected.Count) throw new InvalidDataException("Archive inventory is incomplete.");
            VerifyPayload(payload, manifest);
            File.WriteAllBytes(Path.Combine(candidate, "release-manifest.json"), manifestBytes);
            File.WriteAllBytes(Path.Combine(candidate, "release-signature.bin"), signature);
            AssertNoReparse(VersionsDirectory); Directory.CreateDirectory(VersionsDirectory);
            string targetVersion = VersionDirectory(manifest.Version); AssertNoReparse(targetVersion);
            if (Directory.Exists(targetVersion)) throw new IOException("Immutable version directory already exists.");
            Directory.Move(candidate, targetVersion);
            if (!bootstrap)
            {
                WriteJson("pending.json", new Pending(manifest.Version));
                WriteJson("highest.json", new HighWater(manifest.Sequence));
            }
        }
        finally { DeleteOwnedStaging(stage, stagingRoot); }
    }
    internal static async Task CopyBounded(Stream source, Stream destination, long exactLength, CancellationToken token)
    {
        long total = 0; byte[] buffer = new byte[65536]; int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            total = checked(total + read);
            if (total > exactLength) throw new InvalidDataException("Download or decompression exceeded declared size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (total != exactLength) throw new InvalidDataException("Download or decompression was truncated.");
    }
    static void DeleteOwnedStaging(string stage, string stagingRoot)
    {
        if (!Path.GetFullPath(stage).StartsWith(Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing cleanup outside owned staging.");
        if (Directory.Exists(stage))
        {
            // If a foreign reparse appeared, preserve evidence rather than recurse.
            var directories = new Stack<string>(); directories.Push(stage);
            while (directories.TryPop(out var directory))
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                { AssertNoReparse(path); if (Directory.Exists(path)) directories.Push(path); }
            AssertNoReparse(stage); Directory.Delete(stage, true);
        }
    }
    public UpdateStatus Activate(ReleaseTrust trust, IUpdateSafety safety)
    {
        var interrupted = ReadJson<Transition>("transition.json");
        if (interrupted is not null) return CompleteTransition(interrupted, trust, safety);
        string? pending = PendingVersion;
        if (pending is null) return new("Current", "No verified update is pending.");
        if (!safety.CanActivate()) return new("Deferred", "Downloaded; activation waits for normal app and SteamVR shutdown.", pending, true, true);
        var manifest = VerifyVersion(pending, trust); var previous = Current;
        if (ReleaseTrust.ParseVersion(pending) <= ReleaseTrust.ParseVersion(previous?.Version ?? RememberedBootstrapVersion(trust) ?? trust.BaselineVersion))
            throw new InvalidDataException("Pending activation is a downgrade.");
        if (!safety.CanActivate()) return new("Deferred", "Application activity changed; update remains staged.", pending, true, true);
        string rollbackVersion = previous?.Version ?? RememberedBootstrapVersion(trust) ?? trust.BaselineVersion;
        VerifyVersion(rollbackVersion, trust);
        var transition = new Transition(pending, rollbackVersion, "Activate");
        WriteJson("transition.json", transition);
        return CompleteTransition(transition, trust, safety);
    }
    public UpdateStatus Rollback(ReleaseTrust trust, IUpdateSafety safety)
    {
        var interrupted = ReadJson<Transition>("transition.json");
        if (interrupted is not null) return CompleteTransition(interrupted, trust, safety);
        var current = Current;
        if (current?.PreviousVersion is null) return new("Current", "No previous signed version is available.");
        if (!safety.CanActivate()) return new("Deferred", "Rollback waits for normal app and SteamVR shutdown.", current.PreviousVersion);
        var previous = VerifyVersion(current.PreviousVersion, trust);
        if (!safety.CanActivate()) return new("Deferred", "Application activity changed; rollback deferred.");
        var transition = new Transition(previous.Version, null, "Rollback");
        WriteJson("transition.json", transition);
        return CompleteTransition(transition, trust, safety);
    }
    UpdateStatus CompleteTransition(Transition transition, ReleaseTrust trust, IUpdateSafety safety)
    {
        if (transition.Kind is not "Activate" and not "Rollback") throw new InvalidDataException("Invalid update transition.");
        var manifest = VerifyVersion(transition.Version, trust);
        if (transition.PreviousVersion is string previous) VerifyVersion(previous, trust);
        if (!safety.CanActivate()) return new("Deferred", "Interrupted update waits for normal app and SteamVR shutdown.", transition.Version, true, true);
        var driver = safety.PrepareDriver(PayloadDirectory(transition.Version), manifest);
        if (driver.State is not "Updated" and not "Matched" and not "NotInstalled") throw new IOException("Driver transition did not complete.");
        if (!safety.CanActivate()) return new("Deferred", "Driver preparation is recorded; portable launch waits for normal shutdown.", transition.Version, true, true);
        string hash = manifest.Files.Single(x => x.Path == "driver/bin/win64/driver_switcheroonie.dll").Sha256;
        WriteJson("current.json", new VersionSelection(transition.Version, transition.PreviousVersion, Math.Max(HighestSequence, manifest.Sequence), hash, false));
        // Crash between commit and deletion is safe: the next launch verifies and repeats this exact transition.
        File.Delete(Path.Combine(Root, "pending.json"));
        File.Delete(Path.Combine(Root, "transition.json"));
        return new(transition.Kind == "Activate" ? "Activated" : "RolledBack", "Matched signed portable version and owned driver selected.", transition.Version);
    }
    public void RememberBootstrap(string directory, string version = ReleaseTrust.BootstrapVersion)
    {
        ReleaseTrust.ParseVersion(version);
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); AssertNoReparse(directory);
        // The path alone never grants authority. ResolveStableLauncher requires this origin's signed inventory.
        if (Current is not null || TransitionPending) return;
        var existing = ReadJson<Bootstrap>("bootstrap.json");
        if (existing is not null)
        {
            ValidateBootstrap(existing, ReleaseTrust.BootstrapVersion);
            return; // Never replace a remembered origin on a later launcher start.
        }
        if (!Directory.Exists(directory)) throw new IOException("Original portable directory is missing.");
        WriteJson("bootstrap.json", new Bootstrap(directory, version));
    }
    static string ValidateBootstrap(Bootstrap bootstrap, string legacyVersion)
    {
        string version = bootstrap.Version ?? legacyVersion;
        ReleaseTrust.ParseVersion(version);
        if (!Path.IsPathFullyQualified(bootstrap.Directory)) throw new InvalidDataException("Original portable directory is not canonical.");
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bootstrap.Directory));
        if (!canonical.Equals(bootstrap.Directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Original portable directory is not canonical.");
        AssertNoReparse(canonical);
        return version;
    }
    public string? RememberedBootstrapVersion(ReleaseTrust trust)
    {
        var bootstrap = ReadJson<Bootstrap>("bootstrap.json");
        if (bootstrap is null) return null;
        string version = ValidateBootstrap(bootstrap, trust.BaselineVersion);
        if (ReleaseTrust.ParseVersion(version) < ReleaseTrust.ParseVersion(trust.BaselineVersion))
            throw new InvalidDataException("Original portable version is below the delivery floor.");
        return version;
    }
    public string? ResolveStableLauncher(string uiDirectory, ReleaseTrust trust)
    {
        var selected = Current;
        if (selected is null || TransitionPending ||
            !Path.GetFullPath(uiDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(PayloadDirectory(selected.Version), StringComparison.OrdinalIgnoreCase)) return null;
        VerifyVersion(selected.Version, trust);
        var location = ReadJson<Bootstrap>("bootstrap.json");
        if (location is null) return null;
        string originVersion = ValidateBootstrap(location, trust.BaselineVersion);
        if (ReleaseTrust.ParseVersion(originVersion) < ReleaseTrust.ParseVersion(trust.BaselineVersion) ||
            ReleaseTrust.ParseVersion(originVersion) > ReleaseTrust.ParseVersion(selected.Version))
            throw new InvalidDataException("Original portable version is outside the selected release history.");
        var origin = VerifyVersion(originVersion, trust);
        VerifyPayload(location.Directory, origin);
        string launcher = Path.Combine(location.Directory, "VRC-SWITCHEROONIE.exe"); AssertNoReparse(launcher);
        var item = origin.Files.Single(x => x.Path == "VRC-SWITCHEROONIE.exe");
        using var stream = File.OpenRead(launcher);
        return stream.Length == item.Bytes && ManifestVerifier.Sha256(stream).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase) ? launcher : null;
    }
}
