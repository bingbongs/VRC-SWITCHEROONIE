using System.Text.Json;

namespace Switcheroonie.UI;

internal static class SetupServiceTests
{
    internal static void Run(Action<bool, string> check) => Task.Run(() => RunAsync(check)).GetAwaiter().GetResult();
    static async Task RunAsync(Action<bool, string> check)
    {
        var fresh = new SetupSnapshot(DriverPolicy: SteamVrDriverPolicy.Allowed);
        check(SetupService.ExpectedInstallerSha256.Length == 64 && SetupService.ExpectedInstallerSha256.All(Uri.IsHexDigit), "Published setup requires an exact reviewed installer hash pin");
        var owned = fresh with { JournalPresent = true, JournalValid = true, DriverPresent = true, Registered = true, ConfigPresent = true, ConfigMatchesJournal = true, Enabled = true };
        CheckRuntimePolicy(check, fresh, owned);
        check(SetupService.Evaluate(fresh) is { CanRun: true, Operation: SetupOperation.Install }, "Fresh setup offers Install only after valid inventory");
        check(SetupService.Evaluate(owned with { ProtectedProcessActive = true }).Ready, "Already enabled owned installation stays ready during a session, without reinstall");
        check(SetupService.Evaluate(owned with { Enabled = false }) is { CanRun: true, Operation: SetupOperation.Enable }, "Unchanged owned disabled setup offers only explicit Enable");
        check(SetupService.Evaluate(owned with { DriverMatchesPackage = false }) is { State: "UpdateNeeded", CanRun: false }, "Owned older/different driver requires signed update, never false readiness or reinstall");
        check(SetupService.Evaluate(owned with { DriverMatchesPackage = false, Enabled = false }).Operation == SetupOperation.None, "Disabled mismatched owned driver cannot be enabled as current setup");
        var packageFiles = new Dictionary<string, string> { ["manifest"] = "42:abc", ["driver"] = "120:def" };
        check(WindowsSetupBoundary.FilesMatch(packageFiles, new Dictionary<string, string>(packageFiles)), "Full owned driver inventory and length/hash must match before setup readiness");
        check(!WindowsSetupBoundary.FilesMatch(packageFiles, new Dictionary<string, string> { ["manifest"] = "42:abc", ["driver"] = "120:changed" }), "Same file inventory with changed bytes never claims ready");
        check(!WindowsSetupBoundary.FilesMatch(packageFiles, new Dictionary<string, string> { ["manifest"] = "42:abc" }), "Missing driver resource needs review/update rather than enabling");
        check(!WindowsSetupBoundary.FilesMatch(packageFiles, new Dictionary<string, string>(packageFiles) { ["foreign"] = "1:xyz" }), "Extra owned-tree file prevents claiming a matching installation");
        foreach (var snapshot in new[] { fresh, owned with { Enabled = false } })
            check(SetupService.Evaluate(snapshot with { ProtectedProcessActive = true }).State == "Deferred", "Running VRChat or SteamVR defers every install/enable action");
        foreach (var conflict in new[] { fresh with { ConfigPresent = true }, fresh with { DriverPresent = true }, fresh with { Registered = true }, owned with { ConfigMatchesJournal = false }, owned with { JournalValid = false }, owned with { DriverPresent = false }, owned with { Registered = false }, owned with { Conflict = true } })
            check(!SetupService.Evaluate(conflict).CanRun && !SetupService.Evaluate(conflict).Ready, "Unknown or conflicting ownership never enables or overwrites setup");
        foreach (var unavailable in new[] { fresh with { InventoryKnown = false }, fresh with { PackageValid = false }, fresh with { RuntimeKnown = false }, fresh with { RuntimeBuild = "different" }, fresh with { InstallerRunning = true } })
            check(!SetupService.Evaluate(unavailable).CanRun, "Uncertain inventory/package/build or living installer cannot authorize a new process");
        string root = @"C:\fixture-profile\VRC-SWITCHEROONIE\drivers\0.1.0\switcheroonie";
        string config = "{\"experimentalOptIn\":true,\"approvedRuntimeBuild\":\"25330290\"}";
        string Journal(string phase = "installed", string? install = null, bool originalRegistration = false, object? last = null) => JsonSerializer.Serialize(new { version = 1, installRoot = install ?? root, phase, originalRegistration, originalConfig = (string?)null, lastConfig = last ?? config });
        var valid = WindowsSetupBoundary.ReviewJournal(Journal(), config, [root]);
        check(valid.Valid && valid.ConfigMatches && valid.InstallRoot == root, "Actual ownership parser accepts installed exact root and exact configuration text");
        check(WindowsSetupBoundary.ReviewJournal(Journal(), config + "\n", [root]) is { Valid: true, ConfigMatches: false }, "Journal comparison detects byte-text changes, including added newline");
        check(!WindowsSetupBoundary.ReviewJournal(Journal(install: @"C:\foreign\switcheroonie"), config, [root]).Valid, "Foreign same-name install root is rejected before path use");
        check(!WindowsSetupBoundary.ReviewJournal(Journal(originalRegistration: true), config, [root]).Valid, "Pre-existing registration ownership cannot be enabled by first-run setup");
        foreach (var created in new object[] { false, "true" })
        {
            string incomplete = JsonSerializer.Serialize(new { version = 1, installRoot = root, phase = "installed", originalRegistration = false, originalConfig = (string?)null, lastConfig = config, freshSetupRootCreated = created });
            check(!WindowsSetupBoundary.ReviewJournal(incomplete, config, [root]).Valid, "Incomplete or invalid fresh-directory ownership never offers GUI Enable");
        }
        foreach (var phase in new[] { "prepared", "uninstalled", "relocating", "relocation-recovery-required" })
            check(!WindowsSetupBoundary.ReviewJournal(Journal(phase), config, [root]).Valid, "Incomplete and archived journals are preserved without automatic recovery");
        check(WindowsSetupBoundary.ReviewJournal(Journal(last: new { value = config }), config, [root]) is { Valid: true, ConfigMatches: true }, "Reviewed legacy PS text wrapper remains compatible");
        check(!WindowsSetupBoundary.ReviewJournal(Journal(last: new { value = "[]" }), "[]", [root]).Valid, "Invalid legacy journal wrapper is refused");
        check(!WindowsSetupBoundary.ReviewJournal("{}", config, [root]).Valid && !WindowsSetupBoundary.ReviewJournal("{broken", config, [root]).Valid, "Missing and malformed ownership records fail closed");
        string hostilePath = @"C:\bundle with spaces & $(never)\tools\Manage-Driver.ps1";
        var install = WindowsSetupBoundary.StartInfo(hostilePath, SetupOperation.Install);
        check(!install.UseShellExecute && install.CreateNoWindow && install.ArgumentList.Contains(hostilePath) && install.Arguments == "" && install.ArgumentList.Contains("-EnableExperimental") && install.ArgumentList.Contains("-FreshSetupOnly"), "PowerShell uses exact argument list, fresh-only install, hidden system executable, no shell interpolation");
        var enable = WindowsSetupBoundary.StartInfo(hostilePath, SetupOperation.Enable);
        check(enable.ArgumentList.Contains("Enable") && enable.ArgumentList.Contains("-OwnedSetupOnly") && !enable.ArgumentList.Contains("-EnableExperimental") && !enable.ArgumentList.Any(x => x is "Uninstall" or "Relocate"), "Existing setup requires owned-only enable and cannot reinstall, relocate, uninstall or request UAC");
        var boundary = new FakeBoundary(fresh, fresh, owned);
        var service = new SetupService(boundary);
        check((await service.RunAsync()).Ready && boundary.Actions.SequenceEqual([SetupOperation.Install]), "Explicit setup validates again, installs once and requires read-back confirmation");
        boundary = new(owned); service = new(boundary);
        check((await service.RunAsync()).Ready && boundary.Actions.Count == 0, "Clicking an already-setup service launches no installer");
        boundary = new(fresh, fresh with { ProtectedProcessActive = true }); service = new(boundary);
        check((await service.RunAsync()).State == "Deferred" && boundary.Actions.Count == 0, "Session opening after first inspection cancels process launch");
        foreach (var policy in new[] { SteamVrDriverPolicy.Disabled, SteamVrDriverPolicy.BlockedBySafeMode, SteamVrDriverPolicy.Unknown })
        {
            boundary = new(fresh, fresh with { DriverPolicy = policy }); service = new(boundary);
            check(!(await service.RunAsync()).CanRun && boundary.Actions.Count == 0,
                "SteamVR disable/block or unreadable policy appearing before launch cancels setup");
        }
        boundary = new(fresh, fresh with { Fingerprint = "new journal or config" }); service = new(boundary);
        check((await service.RunAsync()).State == "Recovery" && boundary.Actions.Count == 0, "Changed preflight fingerprint never inherits earlier consent");
        boundary = new(owned with { Enabled = false }, owned with { Enabled = false }, owned); service = new(boundary);
        check((await service.RunAsync()).Ready && boundary.Actions.SequenceEqual([SetupOperation.Enable]), "Only valid owned disabled state invokes reviewed Enable");
        boundary = new(fresh) { Result = new(1) }; service = new(boundary);
        check((await service.RunAsync()).State == "Recovery", "Nonzero installer exit never claims setup ready");
        boundary = new(fresh); service = new(boundary);
        check((await service.RunAsync()).State == "Recovery", "Exit zero without installed/registered/enabled read-back is not success");
        boundary = new(fresh) { Result = new(null, true) }; service = new(boundary);
        check((await service.RunAsync()).State == "Busy", "Slow installer is retained, never killed mid-journal or reported ready");
        var pending = new TaskCompletionSource<SetupRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary = new(fresh, fresh, owned) { Pending = pending.Task }; service = new(boundary);
        var first = service.RunAsync();
        check((await service.RunAsync()).State == "Busy" && boundary.Actions.Count == 1, "Two setup clicks cannot launch overlapping installer processes");
        pending.SetResult(new(0)); check((await first).Ready, "Original setup completes after concurrent click was refused");
        check(AnimationClock.SubscriberCount == 0 && !AnimationClock.IsRunning, "Setup fixtures never initialize resident hooks, animation or user settings");
    }
    static void CheckRuntimePolicy(Action<bool, string> check, SetupSnapshot fresh, SetupSnapshot owned)
    {
        foreach (var text in new[] { "{}", "{\"driver_switcheroonie\":{}}", "{\"driver_switcheroonie\":{\"enable\":true,\"blocked_by_safe_mode\":false}}",
            "{\"unrelated\":{\"A\":1,\"a\":2},\"driver_switcheroonie\":{\"enable\":true}}" })
            check(SteamVrDriverSettings.Parse(text) == SteamVrDriverPolicy.Allowed, "No owned disable/block override retains the driver's enabled default");
        check(SteamVrDriverSettings.Parse("{\"driver_switcheroonie\":{\"enable\":false}}") == SteamVrDriverPolicy.Disabled,
            "SteamVR's own disabled add-on overrides the app opt-in");
        check(SteamVrDriverSettings.Parse("{\"driver_switcheroonie\":{\"enable\":false,\"blocked_by_safe_mode\":true}}") == SteamVrDriverPolicy.BlockedBySafeMode,
            "Crash block remains explicit even when the add-on is also disabled");
        foreach (var text in new string?[] { null, "", "[{}]", "{broken", "{\"driver_switcheroonie\":null}",
            "{\"driver_switcheroonie\":{\"enable\":\"false\"}}", "{\"driver_switcheroonie\":{\"blocked_by_safe_mode\":0}}",
            "{\"driver_switcheroonie\":{\"enable\":false,\"enable\":true}}", "{\"driver_switcheroonie\":{},\"driver_switcheroonie\":{}}",
            "{\"Driver_Switcheroonie\":{}}", "{\"driver_switcheroonie\":{\"Enable\":true}}" })
            check(SteamVrDriverSettings.Parse(text) == SteamVrDriverPolicy.Unknown, "Missing, malformed or ambiguous owned policy never grants readiness");
        foreach (var policy in new[] { SteamVrDriverPolicy.Disabled, SteamVrDriverPolicy.BlockedBySafeMode, SteamVrDriverPolicy.Unknown })
            foreach (var snapshot in new[] { fresh, owned, owned with { Enabled = false } })
                check(SetupService.Evaluate(snapshot with { DriverPolicy = policy }) is { CanRun: false, Ready: false, Operation: SetupOperation.None },
                    "Setup cannot enable, install or claim readiness across a runtime disable/block/unknown state");
        var both = SetupService.Evaluate(owned with { DriverPolicy = SteamVrDriverPolicy.Disabled, RuntimeBuild = "26496310" });
        check(both.State == "RuntimeDisabled" && both.Detail.Contains("26496310") && both.Detail.Contains("25330290") && both.Detail.Contains("preserved"),
            "Current Desko combination reports the deliberate disable and both runtime builds together");
        var stopped = new HarnessStatus();
        check(ReadinessPresentation.Describe(stopped, both)?.Title == "Disabled in SteamVR", "Panel exposes disabled add-on instead of waiting forever for tracking");
        foreach (var code in new uint[] { 5, 6, 7, 8, 10 })
            check(ReadinessPresentation.Describe(stopped with { DriverAlive = true, NativeError = code }, new("Ready", "ready")) is not null,
                "Driver heartbeat with a routing fault is not displayed as healthy connection");
        check(ReadinessPresentation.Describe(stopped with { DriverAlive = true, RoutingReady = true, NativeCapabilityFlags = 1 }, both) is null,
            "Next-startup disk disable never hides a confirmed still-live route");
        check(ReadinessPresentation.Describe(stopped, new("Ready", "ready")) is null, "Missing heartbeat alone retains honest waiting state");
        var desktop = new HarnessStatus { GameInputEnabled = true, GameInputActive = false, MenuNavigation = true };
        check(ReadinessPresentation.DesktopCaption(desktop) == "Click VRChat to resume", "Retained menu state after focus loss does not falsely claim active pointer control");
        check(ReadinessPresentation.DesktopCaption(desktop with { GameInputActive = true }) == "Menu pointer", "Actual active menu capture is distinguished from mouse look");
        check(ReadinessPresentation.DesktopCaption(desktop with { GameInputActive = true, MenuNavigation = false }) == "Mouse look", "Active head-look controls have a visible session caption");
        check(ReadinessPresentation.DesktopCaption(desktop with { GameInputEnabled = false }) == "Controls disabled", "A capture exception cannot leave the panel claiming controls are active");
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Switcheroonie-policy-fixture-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            using var paths = JsonDocument.Parse(JsonSerializer.Serialize(new { config = new[] { directory } }));
            check(WindowsSetupBoundary.ReadDriverPolicy(paths.RootElement) == SteamVrDriverPolicy.Unknown, "Missing settings file is not permission to enable");
            var file = System.IO.Path.Combine(directory, "steamvr.vrsettings");
            const string disabled = "{\"driver_switcheroonie\":{\"enable\":false},\"unrelated\":{\"A\":1,\"a\":2}}";
            System.IO.File.WriteAllText(file, disabled);
            check(WindowsSetupBoundary.ReadDriverPolicy(paths.RootElement) == SteamVrDriverPolicy.Disabled && System.IO.File.ReadAllText(file) == disabled,
                "Read-only boundary follows the OpenVR config path and preserves settings byte-text");
            foreach (var directories in new[] { Array.Empty<string>(), new[] { directory, directory } })
            {
                using var ambiguous = JsonDocument.Parse(JsonSerializer.Serialize(new { config = directories }));
                check(WindowsSetupBoundary.ReadDriverPolicy(ambiguous.RootElement) == SteamVrDriverPolicy.Unknown, "Absent or ambiguous configuration authority is refused");
            }
        }
        finally
        {
            // Only this newly-created fixture directory and its one file are removed.
            var file = System.IO.Path.Combine(directory, "steamvr.vrsettings");
            if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
            System.IO.Directory.Delete(directory);
        }
    }
    sealed class FakeBoundary(params SetupSnapshot[] snapshots) : ISetupBoundary
    {
        int reads;
        internal List<SetupOperation> Actions { get; } = [];
        internal SetupRunResult Result = new(0);
        internal Task<SetupRunResult>? Pending;
        public SetupSnapshot Read() => snapshots[Math.Min(reads++, snapshots.Length - 1)];
        public Task<SetupRunResult> RunAsync(SetupOperation operation, string expectedFingerprint) { Actions.Add(operation); return Pending ?? Task.FromResult(Result); }
    }
}
