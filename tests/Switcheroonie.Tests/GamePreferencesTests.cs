using System.Text.Json;
using Switcheroonie;
using Switcheroonie.Broker;

// Private configuration and random IPC only. No real input platform or runtime.
internal static class GamePreferencesTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var invalid in new[] { "null", "{}", "{\"Enabled\":false}",
            "{\"Enabled\":false,\"Sensitivity\":6,\"Height\":0.3}",
            "{\"Enabled\":false,\"Sensitivity\":1,\"Height\":100}",
            "{\"Enabled\":false,\"Enabled\":true,\"Sensitivity\":1}",
            "{\"enabled\":false,\"Sensitivity\":1}",
            "{\"Enabled\":false,\"Sensitivity\":\"1\"}", new string(' ', 20000) + "{}" })
        {
            using var fixture = new Fixture(invalid);
            check(!fixture.Engine.Status().GameInputEnabled, "Incomplete or invalid saved controls never default to enabled");
            fixture.Engine.Tick();
            check(!fixture.Engine.Status().GameInputEnabled && fixture.Engine.Status().GameInputDetail.Contains("settings", StringComparison.OrdinalIgnoreCase),
                "Settings-read failure remains explained after the first broker tick");
            check(File.ReadAllText(fixture.GameFile) == invalid, "Invalid preferences remain available for review without silent repair");
            var reply = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureGameInput", Enabled = false, Sensitivity = 1.25 });
            check(reply.Accepted && !reply.Status.GameInputEnabled && reply.Status.GameSensitivity == 1.25,
                "An explicit valid Apply recovers invalid controls preferences");
        }
        using (var legacy = new Fixture("{\"Enabled\":false,\"Sensitivity\":0.8}"))
            check(!legacy.Engine.Status().GameInputEnabled && legacy.Engine.Status().GameSensitivity == .8 && legacy.Engine.Status().Height == 0,
                "Older preferences without a height retain explicit control settings and default only the height");

        foreach (var invalid in new[] { "null", "{}", "{\"automatic\":false}", "{\"ManualMode\":\"Desktop\"}",
            "{\"Automatic\":false,\"Automatic\":true}", "{\"Automatic\":false,\"ManualMode\":0}", "{\"Automatic\":\"true\"}" })
        {
            using var fixture = new Fixture("{\"Enabled\":false,\"Sensitivity\":1}", invalid);
            check(!fixture.Engine.Status().AutomaticEnabled && fixture.Engine.Status().ManualMode is null,
                "Invalid routing preferences cannot default to automatic switching or a manual route");
            fixture.Engine.Tick();
            check(fixture.Engine.Status().AdaptationDetail.Contains("settings"), "Routing settings failure remains visible after broker polling");
            check(File.ReadAllText(fixture.RoutingFile) == invalid, "Invalid routing preferences remain available for review");
            var reply = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureAutomatic", Enabled = false });
            check(reply.Accepted && !reply.Status.AutomaticEnabled, "An explicit routing preference recovers malformed saved settings");
        }
        using (var legacy = new Fixture("{\"Enabled\":false,\"Sensitivity\":1}", "{\"Automatic\":false}"))
            check(!legacy.Engine.Status().AutomaticEnabled && legacy.Engine.Status().ManualMode is null,
                "Older routing preferences without a manual selection retain an explicit Auto pause");

        using (var fixture = new Fixture(JsonSerializer.Serialize(new GameInputSettings(false, .75, .2))))
        {
            var original = File.ReadAllBytes(fixture.GameFile);
            using (var locked = new FileStream(fixture.GameFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var enable = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureGameInput", Enabled = true, Sensitivity = 2 });
                check(!enable.Accepted && !enable.Status.GameInputEnabled && enable.Status.GameSensitivity == .75,
                    "Rejected enable/save keeps prior disabled controls and sensitivity");
                var height = await fixture.Engine.ExecuteAsync(new() { Name = "SetDesktopHeight", Height = .9 });
                check(!height.Accepted && height.Status.Height == .2, "Rejected height save cannot move the live view");
                check(File.ReadAllBytes(fixture.GameFile).SequenceEqual(original), "A locked destination preserves previous preference bytes");
                check(Directory.EnumerateFiles(fixture.Directory).All(x => !x.EndsWith(".tmp", StringComparison.Ordinal)),
                    "Failed atomic replacement cleans only its own temporary file");
            }
            var recovered = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureGameInput", Enabled = true, Sensitivity = 1.2 });
            check(recovered.Accepted && recovered.Status.GameInputEnabled && recovered.Status.GameSensitivity == 1.2,
                "Settings can be applied after the file is released");
            using (var locked = new FileStream(fixture.GameFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var disable = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureGameInput", Enabled = false, Sensitivity = 1.2 });
                check(!disable.Accepted && !disable.Status.GameInputEnabled && disable.Reason.Contains("not saved", StringComparison.OrdinalIgnoreCase),
                    "A failed disable save still disables controls for this run and explains persistence failure");
            }
        }
        using (var fixture = new Fixture(JsonSerializer.Serialize(new GameInputSettings(false, 1)),
            "{\"Automatic\":false,\"ManualMode\":\"Physical\"}", spin: true))
        {
            File.WriteAllText(fixture.RoutingFile + ".tmp", "earlier recovery data");
            File.WriteAllText(fixture.SpinFile + ".tmp", "earlier spin data");
            using (var locked = new FileStream(fixture.RoutingFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var enable = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureAutomatic", Enabled = true });
                check(!enable.Accepted && !enable.Status.AutomaticEnabled && enable.Status.ManualMode == "Physical",
                    "Failed Auto save cannot enable switching or discard the existing manual choice");
            }
            var automatic = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureAutomatic", Enabled = true });
            check(automatic.Accepted && automatic.Status.AutomaticEnabled && automatic.Status.ManualMode is null,
                "Successful Auto save commits both automatic and manual preferences together");
            using (var locked = new FileStream(fixture.RoutingFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var pause = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureAutomatic", Enabled = false });
                check(!pause.Accepted && !pause.Status.AutomaticEnabled && pause.Reason.Contains("not saved"),
                    "A failed pause save still pauses automatic switching for this run");
                var manual = await fixture.Engine.ExecuteAsync(new() { Name = "RequestMode", Mode = "Desktop" });
                check(!manual.Accepted && manual.Status.ManualMode == "Desktop" && manual.Reason.Contains("not saved"),
                    "Offline manual selection cannot claim persistence when its file is locked");
                fixture.Engine.Tick();
                check(fixture.Engine.Status().AdaptationDetail.Contains("not saved"),
                    "Session-only manual routing warning survives status polling");
            }
            using (var locked = new FileStream(fixture.SpinFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var stop = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureSpin", Enabled = false });
                check(!stop.Accepted && !stop.Status.SpinEnabled && !stop.Status.SpinActive && stop.Reason.Contains("not saved"),
                    "A failed spin-disable save still removes spin authority for this run");
            }
            var spin = await fixture.Engine.ExecuteAsync(new() { Name = "ConfigureSpin", Enabled = false });
            check(spin.Accepted && File.ReadAllText(fixture.RoutingFile + ".tmp") == "earlier recovery data" &&
                File.ReadAllText(fixture.SpinFile + ".tmp") == "earlier spin data",
                "Atomic preference writes preserve temporary recovery files from earlier work");
        }
    }

    sealed class Fixture : IDisposable
    {
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "Switcheroonie-gameprefs-" + Guid.NewGuid().ToString("N"));
        internal string GameFile => Path.Combine(Directory, "direct-input.json");
        internal string RoutingFile => Path.Combine(Directory, "routing.json");
        internal string SpinFile => Path.Combine(Directory, "spin.json");
        readonly SharedChannel channel;
        internal readonly HarnessEngine Engine;
        internal Fixture(string text, string routing = "{\"Automatic\":false,\"ManualMode\":null}", bool spin = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(GameFile, text);
            File.WriteAllText(RoutingFile, routing);
            File.WriteAllText(SpinFile, JsonSerializer.Serialize(new SpinPreferences(spin)));
            channel = new SharedChannel("Local\\Switcheroonie-GamePreferences-" + Guid.NewGuid().ToString("N"));
            Engine = new HarnessEngine(channel, new NullGameInputPlatform(), Directory);
        }
        public void Dispose()
        {
            Engine.Dispose(); channel.Dispose();
            string full = Path.GetFullPath(Directory), temporary = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Switcheroonie-gameprefs-", StringComparison.Ordinal))
                throw new IOException("Private fixture scope changed");
            foreach (var file in System.IO.Directory.EnumerateFileSystemEntries(full))
            {
                if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Unknown fixture entry");
                File.Delete(file);
            }
            System.IO.Directory.Delete(full);
        }
    }
}
