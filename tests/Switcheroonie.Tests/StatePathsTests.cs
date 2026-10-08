using System.Text;
using System.Text.Json;
using Switcheroonie;
using Switcheroonie.Broker;

static class StatePathsTests
{
    public static void Run(Action<bool, string> check)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "Switcheroonie-state-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var paths = StatePaths.FromProfileDirectory(Path.Combine(sandbox, "Profile with spaces"));
            check(paths.ConfigurationDirectory == Path.Combine(paths.ProfileDirectory, "VRC-SWITCHEROONIE", "config") &&
                paths.DriverConfiguration == Path.Combine(paths.ConfigurationDirectory, "driver.json") &&
                paths.InstallationJournal == Path.Combine(paths.ConfigurationDirectory, "installation-journal.json"),
                "Profile state places driver, journal and preferences under the same config authority");
            check(paths.ReportsDirectory == Path.Combine(paths.DataDirectory, "reports") && !Directory.Exists(paths.DataDirectory),
                "Directory resolution is read-only and separates reports from configuration");
            foreach (var value in new[] { "", "relative", @"C:relative", @"\\?\C:\profile", @"\\.\C:\profile", @"C:\profile\..\other", @"C:\profile.\child", @"C:\profile \child", "C:\\profile\0", @"C:\profile:stream", @"C:\pro*file" })
            {
                bool rejected = false; try { StatePaths.FromProfileDirectory(value); } catch (ArgumentException) { rejected = true; }
                check(rejected, "Ambiguous profile directory rejected without a fallback");
            }
            check(StatePaths.FromProfileDirectory(@"C:\").DataDirectory == @"C:\VRC-SWITCHEROONIE",
                "Drive-root profile remains absolute after trailing-separator normalization");
            var expected = StatePaths.ResolveProcessKnownFolder(StatePaths.ProfileFolderId);
            var priorProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            var priorLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            try
            {
                Environment.SetEnvironmentVariable("USERPROFILE", Path.Combine(sandbox, "wrong-profile"));
                Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(sandbox, "wrong-local"));
                var actual = StatePaths.ResolveProcessKnownFolder(StatePaths.ProfileFolderId);
                check(actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
                    "Explicit process-token known-folder resolution ignores profile and AppData environment overrides");
            }
            finally
            {
                Environment.SetEnvironmentVariable("USERPROFILE", priorProfile);
                Environment.SetEnvironmentVariable("LOCALAPPDATA", priorLocal);
            }
            var source = Path.Combine(sandbox, "legacy"); Directory.CreateDirectory(source);
            var driver = "{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\"}\r\n";
            var original = "{\"experimentalOptIn\":false}\r\n";
            var journal = JsonSerializer.Serialize(new { version = 1, installRoot = @"C:\fixture\VRC-SWITCHEROONIE\drivers\0.1.0\switcheroonie", originalRegistration = false, originalConfig = original, lastConfig = driver, phase = "installed" });
            var contents = new Dictionary<string, byte[]>
            {
                ["driver.json"] = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(driver)).ToArray(),
                ["installation-journal.json"] = Encoding.UTF8.GetBytes(journal),
                ["keyboard.json"] = Encoding.UTF8.GetBytes("{\"Forward\":87,\"Back\":83,\"Left\":65,\"Right\":68,\"Jump\":32,\"Run\":16}"),
                ["direct-input.json"] = Encoding.UTF8.GetBytes("{\"Enabled\":true,\"Sensitivity\":1.4,\"Height\":0.15}\r\n"),
                ["routing.json"] = Encoding.UTF8.GetBytes("{\"Automatic\":false,\"ManualMode\":\"Desktop\"}")
            };
            foreach (var pair in contents) File.WriteAllBytes(Path.Combine(source, pair.Key), pair.Value);
            File.WriteAllText(Path.Combine(source, "ui-settings.json"), "retained-unknown-preferences");
            var migration = new ConfigurationMigration(source, paths.ConfigurationDirectory);
            check(migration.Files.Count == 5 && !Directory.Exists(paths.ConfigurationDirectory),
                "Explicit migration plans exactly five active files without destination writes");
            var backup = Path.Combine(sandbox, "backup"); var copied = migration.CopyWithBackup(backup);
            check(copied.Copied == 5 && copied.AlreadyIdentical == 0 && contents.All(p =>
                File.ReadAllBytes(Path.Combine(source, p.Key)).SequenceEqual(p.Value) &&
                File.ReadAllBytes(Path.Combine(backup, p.Key)).SequenceEqual(p.Value) &&
                File.ReadAllBytes(Path.Combine(paths.ConfigurationDirectory, p.Key)).SequenceEqual(p.Value)),
                "Migration preserves source, backup and destination bytes including BOM and CRLF");
            var restoredJournal = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(paths.ConfigurationDirectory, "installation-journal.json")));
            check(restoredJournal.GetProperty("originalConfig").GetString() == original && restoredJournal.GetProperty("lastConfig").GetString() == driver,
                "Migration preserves exact installer original/last configuration strings and ownership journal");
            check(File.ReadAllText(Path.Combine(source, "ui-settings.json")) == "retained-unknown-preferences" && !File.Exists(Path.Combine(paths.ConfigurationDirectory, "ui-settings.json")),
                "Unused and unknown legacy files remain at the source without being activated");
            using (var channel = new SharedChannel("Local\\VRC-SWITCHEROONIE-StateFixture-" + Guid.NewGuid().ToString("N")))
            using (var engine = new HarnessEngine(channel, configurationDirectory: paths.ConfigurationDirectory))
            {
                check(engine.Status().Height == 0.15 && engine.Status().GameSensitivity == 1.4 && !engine.Status().AutomaticEnabled,
                    "Broker loads migrated +0.15 height, sensitivity and manual routing from only the canonical fixture directory");
            }
            var unchanged = File.GetLastWriteTimeUtc(Path.Combine(paths.ConfigurationDirectory, "direct-input.json"));
            var repeated = new ConfigurationMigration(source, paths.ConfigurationDirectory).CopyWithBackup(Path.Combine(sandbox, "second-backup"));
            check(repeated.Copied == 0 && repeated.AlreadyIdentical == 5 && unchanged == File.GetLastWriteTimeUtc(Path.Combine(paths.ConfigurationDirectory, "direct-input.json")),
                "Identical destination files are retained without rewriting settings");
            bool conflict = false;
            var freshTarget = Path.Combine(sandbox, "fresh-target"); Directory.CreateDirectory(freshTarget);
            File.WriteAllText(Path.Combine(freshTarget, "direct-input.json"), "{\"Height\":-0.1}");
            try { new ConfigurationMigration(source, freshTarget); } catch (IOException) { conflict = true; }
            check(conflict && File.ReadAllText(Path.Combine(freshTarget, "direct-input.json")) == "{\"Height\":-0.1}" && Directory.GetFiles(freshTarget).Length == 1,
                "Conflicting destination settings abort before copying or overwriting");
            var changed = new ConfigurationMigration(source, Path.Combine(sandbox, "changed-target"));
            File.AppendAllText(Path.Combine(source, "routing.json"), " ");
            bool stale = false; try { changed.CopyWithBackup(Path.Combine(sandbox, "stale-backup")); } catch (IOException) { stale = true; }
            check(stale && !Directory.Exists(Path.Combine(sandbox, "changed-target")) && !Directory.Exists(Path.Combine(sandbox, "stale-backup")),
                "Source edits after planning abort before backup and destination writes");
            bool overlap = false; try { new ConfigurationMigration(source, Path.Combine(source, "config")); } catch (IOException) { overlap = true; }
            check(overlap, "Migration rejects overlapping source and destination directories");
        }
        finally
        {
            var resolved = Path.GetFullPath(sandbox);
            if (!resolved.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Switcheroonie-state-fixtures-", StringComparison.Ordinal))
                throw new IOException("Unsafe fixture cleanup target.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
