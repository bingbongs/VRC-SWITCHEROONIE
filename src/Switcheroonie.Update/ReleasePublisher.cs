using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace Switcheroonie.Update;

public static class ReleasePublisher
{
    public static async Task<ReleaseManifest> VerifyArtifactsAsync(string artifacts, byte[] pinnedKey, CancellationToken token = default)
    {
        artifacts = Path.GetFullPath(artifacts); VersionStore.AssertNoReparse(artifacts);
        byte[] Read(string name, int bound)
        {
            string path = Path.Combine(artifacts, name); VersionStore.AssertNoReparse(path);
            using var file = File.OpenRead(path);
            if (file.Length > bound) throw new InvalidDataException("Release receipt exceeds bounds.");
            byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
        }
        byte[] bytes = Read("release-manifest.json", ManifestVerifier.MaxManifestBytes), signature = Read("release-signature.bin", 64);
        var initialTrust = new ReleaseTrust(ReleaseTrust.ProductionRepository, pinnedKey, "0.0.0");
        var manifest = ManifestVerifier.Verify(bytes, signature, initialTrust);
        string fixtureRoot = Path.Combine(Path.GetTempPath(), "Switcheroonie-release-verification-" + Guid.NewGuid().ToString("N"));
        var store = new VersionStore(fixtureRoot);
        try
        {
            using var updateLock = store.AcquireLock();
            using var archive = File.OpenRead(Path.Combine(artifacts, manifest.ArchiveName));
            // The same bounded archive/inventory verifier used by automatic updates.
            await store.StageBootstrapAsync(archive, manifest, bytes, signature,
                new(ReleaseTrust.ProductionRepository, pinnedKey, manifest.Version), token);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                VersionStore.AssertNoReparse(fixtureRoot);
                var pending = new Stack<string>(); pending.Push(fixtureRoot);
                while (pending.TryPop(out var directory))
                    foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                    { VersionStore.AssertNoReparse(path); if (Directory.Exists(path)) pending.Push(path); }
                Directory.Delete(fixtureRoot, true);
            }
        }
    }
    // Produces local release artifacts only. Publishing is a separate explicit script.
    public static ReleaseManifest Prepare(string package, string output, string version, long sequence, string keyPath, byte[] pinnedKey)
    {
        ReleaseTrust.ParseVersion(version); package = Path.GetFullPath(package); output = Path.GetFullPath(output);
        if (sequence < 1 || output.StartsWith(package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Release output/sequence is invalid.");
        VersionStore.AssertNoReparse(package); VersionStore.AssertNoReparse(output);
        if (Directory.Exists(output)) throw new IOException("Release output must be new.");
        using var key = SigningKeyStore.Open(keyPath);
        if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(pinnedKey))
            throw new CryptographicException("Signing key does not match the embedded public key.");
        var files = new List<ReleaseFile>();
        var paths = new List<(string Relative, string Absolute)>();
        var directories = new Stack<string>(); directories.Push(package);
        while (directories.TryPop(out var directory))
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                VersionStore.AssertNoReparse(path);
                string relative = Path.GetRelativePath(package, path).Replace('\\', '/');
                // Raw machine reports/research are never release content.
                if (relative is "reports" or "research" || relative.StartsWith("reports/") || relative.StartsWith("research/")) continue;
                if (Directory.Exists(path)) { directories.Push(path); continue; }
                if (!ManifestVerifier.SafeRelativePath(relative)) throw new InvalidDataException("Unsafe release file.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                files.Add(new(relative, stream.Length, ManifestVerifier.Sha256(stream))); paths.Add((relative, path));
            }
        if (files.Count > ManifestVerifier.MaxFiles) throw new InvalidDataException("Too many release files.");
        Directory.CreateDirectory(output);
        string archiveName = $"VRC-SWITCHEROONIE-{version}-win-x64.zip";
        string archivePath = Path.Combine(output, archiveName);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            foreach (var item in paths.OrderBy(x => x.Relative, StringComparer.Ordinal))
                archive.CreateEntryFromFile(item.Absolute, $"VRC-SWITCHEROONIE-{version}/" + item.Relative, CompressionLevel.Optimal);
        long archiveBytes; string hash;
        using (var archive = File.OpenRead(archivePath)) { archiveBytes = archive.Length; hash = ManifestVerifier.Sha256(archive); }
        var manifest = new ReleaseManifest(1, ReleaseTrust.ProductionRepository, version, sequence,
            DateTimeOffset.UtcNow.ToString("O"), archiveName, archiveBytes, hash, files.Sum(x => x.Bytes),
            files.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestVerifier.Json);
        byte[] signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        ManifestVerifier.Verify(bytes, signature, new(ReleaseTrust.ProductionRepository, pinnedKey, "0.0.0"));
        // Re-open the original files after archiving: changes during preparation refuse a release.
        foreach (var item in files)
        {
            using var file = File.OpenRead(Path.Combine(package, item.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (file.Length != item.Bytes || !ManifestVerifier.Sha256(file).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Package changed during release preparation.");
        }
        File.WriteAllBytes(Path.Combine(output, "release-manifest.json"), bytes);
        File.WriteAllBytes(Path.Combine(output, "release-signature.bin"), signature);
        return manifest;
    }
}
