using System.Security.Cryptography;
using System.Text.Json;

namespace Switcheroonie;

public sealed record ConfigurationFileSnapshot(string Name, long Bytes, string Sha256);
public sealed record ConfigurationMigrationResult(int Copied, int AlreadyIdentical, string BackupDirectory);

/// <summary>
/// Explicit offline migration only. No production startup calls this helper or reads legacy files.
/// Captures bytes without rewriting preferences or the installer's original/last configuration strings.
/// </summary>
public sealed class ConfigurationMigration
{
    public static IReadOnlyList<string> ActiveFileNames { get; } = Array.AsReadOnly(new[]
    { "driver.json", "installation-journal.json", "keyboard.json", "direct-input.json", "routing.json" });
    readonly string source, destination;
    readonly Dictionary<string, byte[]> bytes = new(StringComparer.Ordinal);
    public IReadOnlyList<ConfigurationFileSnapshot> Files { get; }

    public ConfigurationMigration(string sourceDirectory, string destinationDirectory)
    {
        source = Normalize(sourceDirectory); destination = Normalize(destinationDirectory);
        if (Overlaps(source, destination)) throw new IOException("Source and destination directories must be separate.");
        AssertRegularDirectoryChain(source); AssertRegularDirectoryChain(destination);
        if (!Directory.Exists(source)) throw new IOException("Migration source directory is unavailable.");
        foreach (var name in ActiveFileNames)
        {
            var path = Path.Combine(source, name);
            if (Directory.Exists(path)) throw new IOException("An active configuration file is a directory.");
            if (File.Exists(path)) bytes.Add(name, ReadRegularFile(path));
        }
        if (bytes.Count == 0) throw new IOException("No active configuration files were found to migrate.");
        Files = Array.AsReadOnly(bytes.Select(p => new ConfigurationFileSnapshot(p.Key, p.Value.LongLength, Hash(p.Value))).ToArray());
        ValidateCurrent();
    }

    static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.IndexOf('\0') >= 0)
            throw new IOException("An explicit absolute migration directory is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    static bool Overlaps(string first, string second) => first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
        first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static void AssertRegularDirectoryChain(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (File.Exists(current.FullName)) throw new IOException("A migration directory is a file.");
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Migration refuses reparse-point directories.");
        }
    }
    static byte[] ReadRegularFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Migration refuses a nonregular configuration file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Allows old decorated-string journals to be backed up without parsing or normalizing them.
        if (stream.Length > 64 * 1024 * 1024) throw new IOException("Migration file exceeds the explicit 64 MiB backup limit.");
        var value = new byte[checked((int)stream.Length)];
        stream.ReadExactly(value);
        if (stream.ReadByte() != -1) throw new IOException("A configuration file changed during capture.");
        return value;
    }
    static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    void ValidateCurrent()
    {
        AssertRegularDirectoryChain(source); AssertRegularDirectoryChain(destination);
        foreach (var name in ActiveFileNames)
        {
            var path = Path.Combine(source, name);
            if (bytes.TryGetValue(name, out var saved))
            {
                if (!File.Exists(path) || !saved.AsSpan().SequenceEqual(ReadRegularFile(path)))
                    throw new IOException("Source configuration changed; migration must be planned again.");
            }
            else if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("An active source file appeared after planning; migration must be planned again.");
            var target = Path.Combine(destination, name);
            if (Directory.Exists(target)) throw new IOException("An active destination file is a directory.");
            if (File.Exists(target) && (!bytes.TryGetValue(name, out saved) || !saved.AsSpan().SequenceEqual(ReadRegularFile(target))))
                throw new IOException("Destination configuration conflicts; no files were replaced.");
        }
    }

    /// <summary>Requires the caller's controlled stopped-service window. Source files remain untouched.</summary>
    public ConfigurationMigrationResult CopyWithBackup(string backupDirectory)
    {
        var backup = Normalize(backupDirectory);
        if (Overlaps(backup, source) || Overlaps(backup, destination))
            throw new IOException("Backup directory must be separate from source and destination.");
        AssertRegularDirectoryChain(backup);
        if (Directory.Exists(backup) || File.Exists(backup)) throw new IOException("Backup directory already exists; evidence was retained.");
        ValidateCurrent(); // Refuse all known source/destination conflicts before any writes.
        Directory.CreateDirectory(backup);
        foreach (var pair in bytes) WriteNew(Path.Combine(backup, pair.Key), pair.Value);
        WriteNew(Path.Combine(backup, "migration-files.json"), JsonSerializer.SerializeToUtf8Bytes(Files, new JsonSerializerOptions { WriteIndented = true }));
        ValidateCurrent();
        Directory.CreateDirectory(destination);
        var created = new List<string>(); int identical = 0;
        try
        {
            foreach (var pair in bytes)
            {
                var path = Path.Combine(destination, pair.Key);
                if (File.Exists(path))
                {
                    if (!pair.Value.AsSpan().SequenceEqual(ReadRegularFile(path))) throw new IOException("Destination changed during migration.");
                    ++identical; continue;
                }
                // CreateNew, never overwrite, including a target that appears after validation.
                // Stage in the destination directory so rename commits a complete file on its volume.
                var temporary = Path.Combine(destination, ".migration-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    WriteNew(temporary, pair.Value);
                    File.Move(temporary, path, overwrite: false); created.Add(pair.Key);
                }
                finally
                {
                    if (File.Exists(temporary) && pair.Value.AsSpan().SequenceEqual(ReadRegularFile(temporary))) File.Delete(temporary);
                }
            }
            ValidateCurrent();
            return new(created.Count, identical, backup);
        }
        catch
        {
            foreach (var name in created)
            {
                var path = Path.Combine(destination, name);
                // Preserve a concurrent foreign edit instead of deleting it during rollback.
                if (File.Exists(path) && bytes[name].AsSpan().SequenceEqual(ReadRegularFile(path))) File.Delete(path);
            }
            throw;
        }
    }
    static void WriteNew(string path, byte[] value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(value); stream.Flush(flushToDisk: true);
    }
}
