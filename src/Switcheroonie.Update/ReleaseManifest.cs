using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Switcheroonie.Update;

public sealed record UpdateStatus(string State, string Message, string? AvailableVersion = null,
    bool PendingActivation = false, bool DriverPending = false);
public sealed record ReleaseFile(string Path, long Bytes, string Sha256);
public sealed record ReleaseManifest(int Schema, string Repository, string Version, long Sequence,
    string PublishedUtc, string ArchiveName, long ArchiveBytes, string ArchiveSha256,
    long UnpackedBytes, IReadOnlyList<ReleaseFile> Files);
public sealed record VersionSelection(string Version, string? PreviousVersion, long HighestSequence,
    string DriverSha256, bool DriverPending);

public sealed class ReleaseTrust
{
    public const string ProductionRepository = "bingbongs/VRC-SWITCHEROONIE";
    public const string BootstrapVersion = "0.2.0";
    public string Repository { get; }
    public byte[] PublicKey { get; }
    public string BaselineVersion { get; }
    public ReleaseTrust(string repository, byte[] publicKey, string baselineVersion)
    {
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z")) throw new ArgumentException("Invalid release repository.");
        ParseVersion(baselineVersion);
        Repository = repository; PublicKey = publicKey.ToArray(); BaselineVersion = baselineVersion;
    }
    public static ReleaseTrust Production()
    {
        using var stream = typeof(ReleaseTrust).Assembly.GetManifestResourceStream("Switcheroonie.ReleasePublicKey")
            ?? throw new InvalidDataException("Release trust anchor is missing.");
        using var reader = new StreamReader(stream);
        byte[] key;
        try { key = Convert.FromBase64String(reader.ReadToEnd().Trim()); }
        catch (FormatException) { throw new InvalidDataException("Release signing has not been initialized; updates are disabled."); }
        using var algorithm = ECDsa.Create(); algorithm.ImportSubjectPublicKeyInfo(key, out int read);
        if (read != key.Length || algorithm.KeySize != 256) throw new InvalidDataException("Invalid release trust anchor.");
        return new(ProductionRepository, key, BootstrapVersion);
    }
    public static Version ParseVersion(string? value)
    {
        if (value is null || !Regex.IsMatch(value, @"\A(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\z"))
            throw new InvalidDataException("A three-part release version is required.");
        return Version.Parse(value);
    }
}

public static class ManifestVerifier
{
    public const int MaxManifestBytes = 512 * 1024;
    public const long MaxArchiveBytes = 512L * 1024 * 1024;
    public const long MaxUnpackedBytes = 1536L * 1024 * 1024;
    public const int MaxFiles = 4096;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    public static ReleaseManifest Verify(byte[] exactBytes, byte[] signature, ReleaseTrust trust)
    {
        if (exactBytes.Length is 0 or > MaxManifestBytes || signature.Length != 64)
            throw new InvalidDataException("Release manifest/signature length is invalid.");
        using var algorithm = ECDsa.Create();
        algorithm.ImportSubjectPublicKeyInfo(trust.PublicKey, out int imported);
        if (imported != trust.PublicKey.Length || algorithm.KeySize != 256 ||
            !algorithm.VerifyData(exactBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Release signature does not match the pinned key.");
        using (var document = JsonDocument.Parse(exactBytes, new() { MaxDepth = 16 })) RejectDuplicateKeys(document.RootElement);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(exactBytes, Json)
            ?? throw new InvalidDataException("Missing release manifest.");
        var version = ReleaseTrust.ParseVersion(manifest.Version);
        if (manifest.Schema != 1 || manifest.Repository != trust.Repository || manifest.Sequence < 1 ||
            manifest.ArchiveName != $"VRC-SWITCHEROONIE-{manifest.Version}-win-x64.zip" ||
            manifest.ArchiveBytes is <= 0 or > MaxArchiveBytes || manifest.UnpackedBytes is <= 0 or > MaxUnpackedBytes ||
            manifest.Files is null || manifest.Files.Count is 0 or > MaxFiles || !HashValid(manifest.ArchiveSha256) ||
            !DateTimeOffset.TryParse(manifest.PublishedUtc, out var published) || published > DateTimeOffset.UtcNow.AddDays(1))
            throw new InvalidDataException("Release manifest policy failed.");
        long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || !SafeRelativePath(file.Path) || !names.Add(file.Path) ||
                file.Bytes < 0 || file.Bytes > MaxUnpackedBytes || !HashValid(file.Sha256))
                throw new InvalidDataException("Release inventory contains an unsafe or duplicate entry.");
            total = checked(total + file.Bytes);
        }
        if (total != manifest.UnpackedBytes) throw new InvalidDataException("Release unpacked size does not match its inventory.");
        foreach (var required in new[] { "Switcheroonie.UI.exe", "Switcheroonie.UI.dll", "Switcheroonie.UI.deps.json",
            "Switcheroonie.UI.runtimeconfig.json", "Switcheroonie.Broker.exe", "Switcheroonie.Broker.dll", "Switcheroonie.Broker.deps.json",
            "Switcheroonie.Broker.runtimeconfig.json", "Switcheroonie.Common.dll", "Switcheroonie.Cli.exe", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll",
            "driver/bin/win64/driver_switcheroonie.dll", "driver/driver.vrdrivermanifest", "tools/Manage-Driver.ps1",
            "VRC-SWITCHEROONIE.exe", "Switcheroonie.Updater.exe", "Switcheroonie.Updater.dll",
            "Switcheroonie.Updater.deps.json", "Switcheroonie.Updater.runtimeconfig.json" })
            if (!names.Any(name => name.Equals(required, StringComparison.Ordinal))) throw new InvalidDataException("Required portable release file is missing or has noncanonical casing.");
        return manifest;
    }
    static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate manifest property."); RejectDuplicateKeys(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) RejectDuplicateKeys(child);
    }
    public static bool HashValid(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z");
    public static bool SafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 200 || path.Contains('\\') || path.StartsWith('/') ||
            path.Any(c => c < 32 || ":*?\"<>|".Contains(c))) return false;
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) return false;
            var stem = part.Split('.')[0];
            if (Regex.IsMatch(stem, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase)) return false;
        }
        return true;
    }
    internal static string Sha256(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
}
