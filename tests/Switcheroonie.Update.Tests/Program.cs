using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Switcheroonie;
using System.Text;
using System.Text.Json;
using Switcheroonie.Update;

if (args.Length == 3 && args[0] == "--hold-lock")
{
    var childStore = new VersionStore(args[1]);
    using var lease = childStore.AcquireLock();
    File.WriteAllText(args[2], "ready");
    await Task.Delay(2500);
    return;
}
int passed = 0; int failed = 0; var failures = new List<string>();
void Check(bool condition, string name)
{ if (condition) passed++; else { failed++; failures.Add(name); Console.WriteLine("FAIL " + name); } }
void Refused(Action action, string name)
{
    try { action(); Check(false, name); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or CryptographicException or JsonException)
    { Check(true, name); }
}
async Task RefusedAsync(Func<Task> action, string name)
{
    try { await action(); Check(false, name); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or CryptographicException or JsonException or OperationCanceledException)
    { Check(true, name); }
}
string fixtureRoot = Path.Combine(Path.GetTempPath(), "Switcheroonie-update-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
try
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var trust = new ReleaseTrust("fixture-owner/fixture-repo", key.ExportSubjectPublicKeyInfo(), "0.2.0");
    var first = Fixture.Make(key, trust, "0.2.1", 2);
    var second = Fixture.Make(key, trust, "0.2.2", 3);
    var baseline = Fixture.Make(key, trust, "0.2.0", 1);
    Check(ManifestVerifier.Verify(first.ManifestBytes, first.Signature, trust).Version == "0.2.1", "valid exact signature and inventory");
    var altered = first.ManifestBytes.ToArray(); altered[^2] ^= 1;
    Refused(() => ManifestVerifier.Verify(altered, first.Signature, trust), "tampered signed bytes");
    using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Refused(() => ManifestVerifier.Verify(first.ManifestBytes, first.Signature, new(trust.Repository, wrongKey.ExportSubjectPublicKeyInfo(), "0.2.0")), "wrong pinned key");
    Refused(() => ManifestVerifier.Verify(first.ManifestBytes, first.Signature[..63], trust), "signature size");
    Refused(() => ManifestVerifier.Verify(first.ManifestBytes, first.Signature, new("other/repository", trust.PublicKey, "0.2.0")), "cross-repository signed release");
    foreach (string version in new[] { "../0.2.1", "0.2.1-beta", "v0.2.1", "01.2.1", "0.2", "0.2.1.0", "9999999.0.0" })
        Refused(() => ReleaseTrust.ParseVersion(version), "version path/format " + version);
    foreach (string path in new[] { "../escape", "/absolute", @"dir\file", "file:stream", "file.", "dir /x", "CON.txt", "NUL", "com1.log", "x//y", "x/../y" })
        Check(!ManifestVerifier.SafeRelativePath(path), "path policy " + path);
    Check(ManifestVerifier.SafeRelativePath("docs/controls.md"), "normal portable path");
    foreach (string running in new[] { "vrstartup", "VRSTARTUP", "vrserver", "vrcompositor", "vrmonitor", "VRChat", "Switcheroonie.Broker", "Switcheroonie.UI" })
        Check(!WindowsUpdateSafety.IsStoppedInventory([running]), "stopped guard covers real startup/runtime/harness inventory " + running);
    Check(WindowsUpdateSafety.IsStoppedInventory(["VirtualDesktop.Streamer", "Explorer"]), "unrelated continuing transport and desktop processes do not block update");
    foreach (string url in new[] { "http://github.com/x", "https://github.com.evil.example/x", "https://github.com:444/x", "https://user@github.com/x", "https://github.com/x#fragment" })
        Check(!UpdateService.AllowedDownloadUri(new(url)), "redirect policy " + url);
    Check(UpdateService.AllowedDownloadUri(new("https://release-assets.githubusercontent.com/x")), "official release redirect host");
    var duplicateJson = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(first.ManifestBytes).Replace("\"schema\":1", "\"schema\":1,\"Schema\":1"));
    Refused(() => ManifestVerifier.Verify(duplicateJson, key.SignData(duplicateJson, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), trust), "signed duplicate property");
    var extraFiles = first.Manifest.Files.Concat([new ReleaseFile("switcheroonie.ui.EXE", 1, new string('a', 64))]).ToArray();
    var duplicate = first.Manifest with { Files = extraFiles, UnpackedBytes = first.Manifest.UnpackedBytes + 1 };
    Refused(() => Fixture.VerifySigned(duplicate, key, trust), "case alias inventory");
    var wrongCase = first.Manifest with { Files = first.Manifest.Files.Select(x => x.Path == "driver/bin/win64/driver_switcheroonie.dll" ? x with { Path = "Driver/bin/win64/driver_switcheroonie.dll" } : x).ToArray() };
    Refused(() => Fixture.VerifySigned(wrongCase, key, trust), "single noncanonical required driver path refused before staging/transition");
    Refused(() => Fixture.VerifySigned(first.Manifest with { UnpackedBytes = first.Manifest.UnpackedBytes + 1 }, key, trust), "unpacked budget mismatch");
    Refused(() => Fixture.VerifySigned(first.Manifest with { ArchiveBytes = ManifestVerifier.MaxArchiveBytes + 1 }, key, trust), "archive budget");
    Refused(() => Fixture.VerifySigned(first.Manifest with { Files = first.Manifest.Files.Skip(1).ToArray(), UnpackedBytes = first.Manifest.Files.Skip(1).Sum(x => x.Bytes) }, key, trust), "required executable omitted");
    var store = new VersionStore(Path.Combine(fixtureRoot, "store"));
    using (store.AcquireLock()) await store.StageBootstrapAsync(new MemoryStream(baseline.Archive), baseline.Manifest, baseline.ManifestBytes, baseline.Signature, trust);
    Check(store.Current is null && store.PendingVersion is null && store.HighestSequence == 0, "signed bootstrap snapshot never activates or advances feed");
    using (store.AcquireLock()) await store.StageAsync(new MemoryStream(first.Archive), first.Manifest, first.ManifestBytes, first.Signature, trust);
    Check(store.PendingVersion == "0.2.1" && store.Current is null, "stage never activates");
    Check(store.VerifyVersion("0.2.1", trust).Sequence == 2, "staged receipt and full inventory verify");
    using (store.AcquireLock())
    {
        Check(store.Activate(trust, new Safety(false)).State == "Deferred" && store.Current is null, "live runtime defers selection");
        Check(store.Activate(trust, new Safety(true, false)).State == "Deferred" && store.Current is null, "runtime appearing before commit defers");
        Check(store.Activate(trust, new Safety(true)).State == "Activated" && store.Current?.Version == "0.2.1", "atomic version selection");
        Check(store.Current?.PreviousVersion == "0.2.0", "first update retains signed baseline rollback target");
    }
    using (store.AcquireLock()) await RefusedAsync(() => store.StageAsync(new MemoryStream(first.Archive), first.Manifest, first.ManifestBytes, first.Signature, trust), "same release replay");
    using (store.AcquireLock()) await store.StageAsync(new MemoryStream(second.Archive), second.Manifest, second.ManifestBytes, second.Signature, trust);
    using (store.AcquireLock()) Check(store.Activate(trust, new Safety(true)).State == "Activated", "second signed version activation");
    Check(store.Current?.PreviousVersion == "0.2.1" && store.HighestSequence == 3, "previous version retained and monotonic high-water");
    using (store.AcquireLock()) Check(store.Rollback(trust, new Safety(false)).State == "Deferred", "live rollback deferred");
    using (store.AcquireLock()) Check(store.Rollback(trust, new Safety(true)).State == "RolledBack" && store.Current?.Version == "0.2.1", "signed previous version rollback");
    Check(store.HighestSequence == 3, "rollback preserves anti-replay sequence");
    string payload = store.PayloadDirectory("0.2.1");
    File.AppendAllText(Path.Combine(payload, "Switcheroonie.UI.dll"), "tamper");
    Refused(() => store.VerifyVersion("0.2.1", trust), "changed staged executable refused before launch");
    foreach (string attack in new[] { "traversal", "duplicate", "symlink", "missing", "extra", "root", "wrong-content" })
    {
        var malicious = Fixture.Make(key, trust, "0.2.1", 1, attack);
        var attackStore = new VersionStore(Path.Combine(fixtureRoot, attack));
        using (attackStore.AcquireLock())
            await RefusedAsync(() => attackStore.StageAsync(new MemoryStream(malicious.Archive), malicious.Manifest, malicious.ManifestBytes, malicious.Signature, trust), "signed malicious zip " + attack);
        Check(attackStore.Current is null && attackStore.PendingVersion is null && !Directory.Exists(attackStore.VersionDirectory("0.2.1")), "no selection/partial version after " + attack);
    }
    var truncatedStore = new VersionStore(Path.Combine(fixtureRoot, "truncated"));
    using (truncatedStore.AcquireLock())
        await RefusedAsync(() => truncatedStore.StageAsync(new MemoryStream(first.Archive[..^1]), first.Manifest, first.ManifestBytes, first.Signature, trust), "truncated archive");
    using (truncatedStore.AcquireLock())
        await RefusedAsync(() => truncatedStore.StageAsync(new MemoryStream(first.Archive.Concat(new byte[] { 1 }).ToArray()), first.Manifest, first.ManifestBytes, first.Signature, trust), "extra archive bytes");
    using (var handler = new FakeHttp(first, trust, baseline))
    using (var service = new UpdateService(Path.Combine(fixtureRoot, "http"), trust, handler, new Safety(false)))
    {
        Check((await service.CheckAndStageAsync()).State == "Staged", "real HTTP orchestration stages signed fixture while live");
        Check(service.TryActivate().State == "Deferred", "download never forces runtime stop");
        Check((await service.CheckAndStageAsync()).State == "Staged" && handler.ZipDownloads == 2, "baseline plus update download, pending avoids duplicates");
    }
    using (var handler = new FakeHttp(first, trust, baseline) { BadSignature = true })
    using (var service = new UpdateService(Path.Combine(fixtureRoot, "bad-http"), trust, handler, new Safety(true)))
        Check((await service.CheckAndStageAsync()).State == "Unavailable" && handler.ZipDownloads == 0, "signature failure stops before archive download");
    using (var handler = new FakeHttp(first, trust, baseline) { EvilRedirect = true })
    using (var service = new UpdateService(Path.Combine(fixtureRoot, "redirect-http"), trust, handler, new Safety(true)))
        Check((await service.CheckAndStageAsync()).State == "Unavailable", "foreign redirect refused");
    var interrupted = new VersionStore(Path.Combine(fixtureRoot, "interrupted"));
    var originStore = new VersionStore(Path.Combine(fixtureRoot, "first-21-origin"));
    string original21 = Path.Combine(fixtureRoot, "original-21"), later22 = Path.Combine(fixtureRoot, "later-22");
    Directory.CreateDirectory(original21); Directory.CreateDirectory(later22);
    using (originStore.AcquireLock())
    {
        originStore.RememberBootstrap(original21, "0.2.1");
        await originStore.StageBootstrapAsync(new MemoryStream(baseline.Archive), baseline.Manifest, baseline.ManifestBytes, baseline.Signature, trust);
        await originStore.StageOriginAsync(new MemoryStream(first.Archive), first.Manifest, first.ManifestBytes, first.Signature, trust);
        Check(originStore.Current is null && originStore.PendingVersion is null && originStore.HighestSequence == 0,
            "original2.1 signed receipt retention never selects or advances feed state");
        foreach (var item in first.Manifest.Files)
        {
            string destination = Path.Combine(original21, item.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(originStore.PayloadDirectory(first.Manifest.Version), item.Path.Replace('/', Path.DirectorySeparatorChar)), destination);
        }
        originStore.RememberBootstrap(later22, "0.2.2");
        Check(originStore.RememberedBootstrapVersion(trust) == "0.2.1", "later launcher preserves first recorded release origin");
        await originStore.StageAsync(new MemoryStream(second.Archive), second.Manifest, second.ManifestBytes, second.Signature, trust);
        Check(originStore.Activate(trust, new Safety(true)).State == "Activated" && originStore.Current?.PreviousVersion == "0.2.1",
            "fresh2.1 user updates2.2 with exact signed original rollback target");
    }
    Check(originStore.ResolveStableLauncher(originStore.PayloadDirectory("0.2.2"), trust) == Path.Combine(original21, "VRC-SWITCHEROONIE.exe"),
        "fresh2.1 origin resolves after2.2 against original signed2.1 inventory");
    Check(originStore.ResolveStableLauncher(later22, trust) is null, "unselected UI path cannot resolve authority");
    File.AppendAllText(Path.Combine(original21, "Switcheroonie.Common.dll"), "changed");
    Refused(() => originStore.ResolveStableLauncher(originStore.PayloadDirectory("0.2.2"), trust), "changed origin dependency fails complete signed verification");
    File.Copy(Path.Combine(originStore.PayloadDirectory("0.2.1"), "Switcheroonie.Common.dll"), Path.Combine(original21, "Switcheroonie.Common.dll"), true);
    string savedOriginMetadata = File.ReadAllText(Path.Combine(originStore.Root, "bootstrap.json"));
    File.WriteAllText(Path.Combine(originStore.Root, "bootstrap.json"), JsonSerializer.Serialize(new { directory = original21, version = "0.2.0" }));
    Refused(() => originStore.ResolveStableLauncher(originStore.PayloadDirectory("0.2.2"), trust), "altered remembered version fails original bytes verification");
    File.WriteAllText(Path.Combine(originStore.Root, "bootstrap.json"), JsonSerializer.Serialize(new { directory = original21, version = "99.0.0" }));
    Refused(() => originStore.ResolveStableLauncher(originStore.PayloadDirectory("0.2.2"), trust), "unknown numeric origin version fails closed");
    File.WriteAllText(Path.Combine(originStore.Root, "bootstrap.json"), JsonSerializer.Serialize(new { directory = original21, version = "../0.2.1" }));
    Refused(() => originStore.ResolveStableLauncher(originStore.PayloadDirectory("0.2.2"), trust), "nonnumeric origin version fails closed");
    File.WriteAllText(Path.Combine(originStore.Root, "bootstrap.json"), savedOriginMetadata);
    var legacyOriginStore = new VersionStore(Path.Combine(fixtureRoot, "legacy-origin"));
    string original20 = Path.Combine(fixtureRoot, "original-20"); Directory.CreateDirectory(original20);
    using (legacyOriginStore.AcquireLock())
    {
        File.WriteAllText(Path.Combine(legacyOriginStore.Root, "bootstrap.json"), JsonSerializer.Serialize(new { directory = original20 }));
        legacyOriginStore.RememberBootstrap(later22, "0.2.2");
        Check(legacyOriginStore.RememberedBootstrapVersion(trust) == "0.2.0", "legacy unversioned origin retains baseline interpretation");
        await legacyOriginStore.StageBootstrapAsync(new MemoryStream(baseline.Archive), baseline.Manifest, baseline.ManifestBytes, baseline.Signature, trust);
        foreach (var item in baseline.Manifest.Files)
        {
            string destination = Path.Combine(original20, item.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(legacyOriginStore.PayloadDirectory("0.2.0"), item.Path.Replace('/', Path.DirectorySeparatorChar)), destination);
        }
        await legacyOriginStore.StageAsync(new MemoryStream(first.Archive), first.Manifest, first.ManifestBytes, first.Signature, trust);
        legacyOriginStore.Activate(trust, new Safety(true));
    }
    Check(legacyOriginStore.ResolveStableLauncher(legacyOriginStore.PayloadDirectory("0.2.1"), trust) == Path.Combine(original20, "VRC-SWITCHEROONIE.exe"),
        "legacy2.0 origin resolves after versioned update");
    var missingOriginStore = new VersionStore(Path.Combine(fixtureRoot, "missing-origin"));
    using (missingOriginStore.AcquireLock())
    {
        string missing = Path.Combine(fixtureRoot, "missing-original-directory");
        File.WriteAllText(Path.Combine(missingOriginStore.Root, "bootstrap.json"), JsonSerializer.Serialize(new { directory = missing, version = "0.2.1" }));
        missingOriginStore.RememberBootstrap(later22, "0.2.2");
        Check(File.ReadAllText(Path.Combine(missingOriginStore.Root, "bootstrap.json")).Contains(missing.Replace("\\", "\\\\")),
            "missing original path is preserved rather than silently replaced");
    }
    string originHttpRoot = Path.Combine(fixtureRoot, "origin-http");
    var originHttpStore = new VersionStore(originHttpRoot);
    using (originHttpStore.AcquireLock()) originHttpStore.RememberBootstrap(original21, "0.2.1");
    using (var handler = new FakeHttp(second, trust, baseline, first))
    using (var service = new UpdateService(originHttpRoot, trust, handler, new Safety(true)))
    {
        Check((await service.CheckAndStageAsync()).State == "Staged" && handler.ZipDownloads == 3,
            "first2.1-to2.2 HTTP flow retains signed floor and original receipts before candidate");
        Check(originHttpStore.VerifyVersion("0.2.0", trust).Version == "0.2.0" &&
            originHttpStore.VerifyVersion("0.2.1", trust).Version == "0.2.1" && originHttpStore.HighestSequence == 3,
            "origin retention preserves delivery floor and candidate high-water");
        Check(service.TryActivate().State == "Activated" && originHttpStore.Current?.PreviousVersion == "0.2.1",
            "HTTP staged first update retains actual2.1 rollback");
        Check(service.ResolveStableLauncher(originHttpStore.PayloadDirectory("0.2.2")) == Path.Combine(original21, "VRC-SWITCHEROONIE.exe"),
            "service resolves independently signed actual origin after first update");
    }
    string compiledOriginRoot = Path.Combine(fixtureRoot, "compiled-origin");
    using (var service = new UpdateService(compiledOriginRoot, trust, new FakeHttp(first, trust, baseline), new Safety(false)))
        service.RememberBootstrap(original21);
    var compiledVersion = typeof(UpdateService).Assembly.GetName().Version!;
    Check(new VersionStore(compiledOriginRoot).RememberedBootstrapVersion(trust) ==
        $"{compiledVersion.Major}.{compiledVersion.Minor}.{compiledVersion.Build}",
        "production service records its actual compiled release version");
    using (interrupted.AcquireLock())
    {
        await interrupted.StageBootstrapAsync(new MemoryStream(baseline.Archive), baseline.Manifest, baseline.ManifestBytes, baseline.Signature, trust);
        await interrupted.StageAsync(new MemoryStream(first.Archive), first.Manifest, first.ManifestBytes, first.Signature, trust);
        Check(interrupted.Activate(trust, new Safety(true, true, true, false)).State == "Deferred" && interrupted.TransitionPending && interrupted.Current is null,
            "persistent transition survives runtime appearing after driver preparation");
    }
    using (var service = new UpdateService(interrupted.Root, trust, new FakeHttp(first, trust, baseline), new Safety(false)))
    {
        Refused(() => service.ResolveUi(fixtureRoot), "incomplete pairing never launches bootstrap or new UI");
        Check(service.Status().DriverPending && service.Status().State == "Deferred", "pending driver transition visible after new service instance");
    }
    using (interrupted.AcquireLock())
    {
        Check(interrupted.Activate(trust, new Safety(true)).State == "Activated" && !interrupted.TransitionPending, "interrupted transition resumes with signed revalidation");
        Check(interrupted.Rollback(trust, new Safety(true, true, true, false)).State == "Deferred" && interrupted.TransitionPending,
            "first-update baseline rollback persists when runtime appears after driver rollback");
        Check(interrupted.Activate(trust, new Safety(true)).State == "RolledBack" && interrupted.Current?.Version == "0.2.0", "launcher resumes exact baseline rollback after restart");
    }
    var orphan = new VersionStore(Path.Combine(fixtureRoot, "orphaned-commit"));
    using (orphan.AcquireLock())
        await orphan.StageAsync(new MemoryStream(first.Archive), first.Manifest, first.ManifestBytes, first.Signature, trust);
    File.Delete(Path.Combine(orphan.Root, "pending.json"));
    File.Delete(Path.Combine(orphan.Root, "highest.json"));
    using (orphan.AcquireLock())
        await orphan.StageAsync(Stream.Null, first.Manifest, first.ManifestBytes, first.Signature, trust);
    Check(orphan.PendingVersion == first.Manifest.Version && orphan.HighestSequence == first.Manifest.Sequence,
        "crash after immutable folder commit reconciles verified pending/high-water without download or overwrite");
    File.Delete(Path.Combine(orphan.Root, "pending.json"));
    File.Delete(Path.Combine(orphan.Root, "highest.json"));
    File.AppendAllText(Path.Combine(orphan.PayloadDirectory(first.Manifest.Version), "Switcheroonie.Common.dll"), "foreign change");
    using (orphan.AcquireLock())
        await RefusedAsync(() => orphan.StageAsync(Stream.Null, first.Manifest, first.ManifestBytes, first.Signature, trust),
            "orphan reconciliation refuses changed immutable payload and never overwrites it");
    var driverPaths = StatePaths.FromProfileDirectory(Path.Combine(fixtureRoot, "driver-profile"));
    Directory.CreateDirectory(driverPaths.ConfigurationDirectory);
    string ownedDriver = Path.Combine(driverPaths.DataDirectory, "drivers", "0.1.0", "switcheroonie");
    Directory.CreateDirectory(Path.Combine(ownedDriver, "bin", "win64"));
    string ownedDll = Path.Combine(ownedDriver, "bin", "win64", "driver_switcheroonie.dll");
    File.WriteAllText(ownedDll, "original owned fixture driver");
    File.WriteAllText(Path.Combine(ownedDriver, "driver.vrdrivermanifest"), "{\"name\":\"switcheroonie\",\"hmd_presence\":[]}");
    const string ownedConfig = "{\"enabled\":true,\"fixture\":true}";
    File.WriteAllText(driverPaths.DriverConfiguration, ownedConfig);
    File.WriteAllText(driverPaths.InstallationJournal, JsonSerializer.Serialize(new { installRoot = ownedDriver, phase = "installed", lastConfig = ownedConfig }));
    string fixtureRegistration = Path.Combine(fixtureRoot, "registration.json");
    File.WriteAllText(fixtureRegistration, JsonSerializer.Serialize(new { external_drivers = new[] { ownedDriver } }));
    string registrationBefore = File.ReadAllText(fixtureRegistration), journalBefore = File.ReadAllText(driverPaths.InstallationJournal);
    var driverTransition = new DriverTransition(driverPaths, Path.Combine(driverPaths.DataDirectory, "updates"), () => true);
    var driverResult = driverTransition.ApplyFixture(store.PayloadDirectory(second.Manifest.Version), second.Manifest, fixtureRegistration);
    Check(driverResult.State == "Updated" && driverResult.BackupId is not null, "owned stopped driver files replaced with immutable recovery backup");
    using (var dllStream = File.OpenRead(ownedDll))
        Check(Convert.ToHexString(SHA256.HashData(dllStream)) == second.Manifest.Files.Single(x => x.Path.EndsWith("driver_switcheroonie.dll")).Sha256, "new native exact signed hash");
    Check(File.ReadAllText(fixtureRegistration) == registrationBefore && File.ReadAllText(driverPaths.InstallationJournal) == journalBefore &&
        File.ReadAllText(driverPaths.DriverConfiguration) == ownedConfig, "driver update preserves opt-in config journal and registration byte-for-byte");
    Check(driverTransition.ApplyFixture(store.PayloadDirectory(second.Manifest.Version), second.Manifest, fixtureRegistration).State == "Matched", "matched driver transition is idempotent");
    File.WriteAllText(driverPaths.DriverConfiguration, ownedConfig + " ");
    Refused(() => driverTransition.ApplyFixture(interrupted.PayloadDirectory("0.2.0"), baseline.Manifest, fixtureRegistration), "foreign config conflict prevents native replacement");
    File.WriteAllText(driverPaths.DriverConfiguration, ownedConfig);
    string currentDll = File.ReadAllText(ownedDll);
    var fluctuatingSafety = new Safety(true, true, true, false);
    var interruptedDriver = new DriverTransition(driverPaths, Path.Combine(driverPaths.DataDirectory, "updates"), fluctuatingSafety.CanActivate);
    Refused(() => interruptedDriver.ApplyFixture(interrupted.PayloadDirectory("0.2.0"), baseline.Manifest, fixtureRegistration), "runtime appearing mid-driver transition refuses commit");
    Check(File.ReadAllText(ownedDll) != currentDll, "runtime becoming active prevents rollback writes and retains exact partial new driver");
    Check(Directory.EnumerateFiles(Path.Combine(driverPaths.DataDirectory, "updates", "driver-backups"), "receipt.json", SearchOption.AllDirectories).Count() >= 2,
        "interrupted driver transition retains immutable ownership recovery receipts");
    Check(driverTransition.ApplyFixture(interrupted.PayloadDirectory("0.2.0"), baseline.Manifest, fixtureRegistration).State == "Matched",
        "partial native transition resumes idempotently after stopped window");
    var absentPaths = StatePaths.FromProfileDirectory(Path.Combine(fixtureRoot, "no-driver-profile"));
    Check(new DriverTransition(absentPaths, Path.Combine(fixtureRoot, "absent-updates"), () => true).Apply(payload, first.Manifest).State == "NotInstalled" &&
        !Directory.Exists(absentPaths.DataDirectory), "automatic update never opts in or registers missing driver");
    string protectedKey = Path.Combine(fixtureRoot, "fixture-signing.dpapi");
    byte[] fixturePublic = SigningKeyStore.Initialize(protectedKey);
    using (var restoredKey = SigningKeyStore.Open(protectedKey))
        Check(restoredKey.ExportSubjectPublicKeyInfo().SequenceEqual(fixturePublic), "actual current-user DPAPI key roundtrip inside private fixture");
    Check(!File.ReadAllBytes(protectedKey).SequenceEqual(key.ExportPkcs8PrivateKey()), "protected signing file is not raw private key material");
    string releaseOutput = Path.Combine(fixtureRoot, "signed-release");
    var preparedRelease = ReleasePublisher.Prepare(interrupted.PayloadDirectory("0.2.0"), releaseOutput, "0.2.3", 4, protectedKey, fixturePublic);
    Check((await ReleasePublisher.VerifyArtifactsAsync(releaseOutput, fixturePublic)).Version == "0.2.3", "real release signer and complete publication verifier agree");
    using (var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        await RefusedAsync(() => ReleasePublisher.VerifyArtifactsAsync(releaseOutput, foreignKey.ExportSubjectPublicKeyInfo()), "publication refuses a foreign signing key");
    File.AppendAllText(Path.Combine(releaseOutput, preparedRelease.ArchiveName), "altered");
    await RefusedAsync(() => ReleasePublisher.VerifyArtifactsAsync(releaseOutput, fixturePublic), "publication refuses changed prepared archive");
    string lockRoot = Path.Combine(fixtureRoot, "cross-process"); string ready = Path.Combine(fixtureRoot, "lock-ready");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--hold-lock"); start.ArgumentList.Add(lockRoot); start.ArgumentList.Add(ready);
    using var helper = Process.Start(start) ?? throw new IOException("Fixture helper unavailable.");
    try
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(ready) && deadline.ElapsedMilliseconds < 2000 && !helper.HasExited) await Task.Delay(20);
        Check(File.Exists(ready), "private lock helper ready");
        Refused(() => new VersionStore(lockRoot).AcquireLock().Dispose(), "actual cross-process lock excludes competing writer");
        using var waitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await helper.WaitForExitAsync(waitDeadline.Token);
        using var recovered = new VersionStore(lockRoot).AcquireLock(); Check(true, "lock recovers after exact helper exits");
    }
    finally { if (!helper.HasExited) { helper.Kill(); await helper.WaitForExitAsync(); } }
}
finally
{
    var owned = Path.GetFullPath(fixtureRoot);
    if (!owned.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(owned).StartsWith("Switcheroonie-update-fixture-", StringComparison.Ordinal)) throw new IOException("Fixture cleanup escaped ownership.");
    Directory.Delete(owned, true);
}
Console.WriteLine($"{passed} updater checks passed; {failed} failed. Fixtures only; no network, game, cursor, registry, driver or production state actions.");
if (failed != 0) Environment.ExitCode = 1;

sealed class Safety(params bool[] answers) : IUpdateSafety
{
    int calls;
    public bool CanActivate() => answers[Math.Min(calls++, answers.Length - 1)];
}
sealed record Fixture(ReleaseManifest Manifest, byte[] ManifestBytes, byte[] Signature, byte[] Archive)
{
    static readonly string[] required = ["Switcheroonie.UI.exe","Switcheroonie.UI.dll","Switcheroonie.UI.deps.json","Switcheroonie.UI.runtimeconfig.json",
        "Switcheroonie.Broker.exe","Switcheroonie.Broker.dll","Switcheroonie.Broker.deps.json","Switcheroonie.Broker.runtimeconfig.json",
        "Switcheroonie.Common.dll","Switcheroonie.Cli.exe","hostfxr.dll","hostpolicy.dll","coreclr.dll",
        "driver/bin/win64/driver_switcheroonie.dll","driver/driver.vrdrivermanifest","tools/Manage-Driver.ps1",
        "VRC-SWITCHEROONIE.exe","Switcheroonie.Updater.exe","Switcheroonie.Updater.dll","Switcheroonie.Updater.deps.json","Switcheroonie.Updater.runtimeconfig.json"];
    public static Fixture Make(ECDsa key, ReleaseTrust trust, string version, long sequence, string? attack = null)
    {
        var contents = required.ToDictionary(x => x, x => Encoding.UTF8.GetBytes("fixture-only:" + version + ":" + x));
        contents["driver/driver.vrdrivermanifest"] = Encoding.UTF8.GetBytes("{\"name\":\"switcheroonie\",\"hmd_presence\":[]}");
        var inventory = contents.Select(x => new ReleaseFile(x.Key, x.Value.Length, Convert.ToHexString(SHA256.HashData(x.Value)))).ToArray();
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in contents)
            {
                if (attack == "missing" && name == required[0]) continue;
                string prefix = attack == "root" ? "wrong-root/" : $"VRC-SWITCHEROONIE-{version}/";
                var entry = archive.CreateEntry(prefix + name, CompressionLevel.Optimal);
                if (attack == "symlink" && name == required[0]) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var stream = entry.Open();
                stream.Write(attack == "wrong-content" && name == required[0] ? new byte[content.Length] : content);
            }
            if (attack is "traversal" or "duplicate" or "extra")
            {
                string name = attack == "traversal" ? "../escape" : attack == "duplicate" ? required[0] : "unlisted.txt";
                var entry = archive.CreateEntry($"VRC-SWITCHEROONIE-{version}/" + name); using var stream = entry.Open(); stream.WriteByte(1);
            }
        }
        byte[] bytes = output.ToArray();
        var manifest = new ReleaseManifest(1, trust.Repository, version, sequence, DateTimeOffset.UtcNow.ToString("O"),
            $"VRC-SWITCHEROONIE-{version}-win-x64.zip", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), inventory.Sum(x => x.Bytes), inventory);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return new(manifest, manifestBytes, key.SignData(manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), bytes);
    }
    public static void VerifySigned(ReleaseManifest manifest, ECDsa key, ReleaseTrust trust)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        ManifestVerifier.Verify(bytes, key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), trust);
    }
}
sealed class FakeHttp(Fixture fixture, ReleaseTrust trust, Fixture baseline, Fixture? origin = null) : HttpMessageHandler
{
    public bool BadSignature { get; init; } public bool EvilRedirect { get; init; } public int ZipDownloads { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (EvilRedirect) { var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new("https://evil.example/payload"); return Task.FromResult(redirect); }
        string path = request.RequestUri!.AbsolutePath;
        var selected = path.Contains("/v" + baseline.Manifest.Version + "/", StringComparison.Ordinal) || path.EndsWith("/tags/v" + baseline.Manifest.Version) ? baseline : fixture;
        if (origin is not null && (path.Contains("/v" + origin.Manifest.Version + "/", StringComparison.Ordinal) || path.EndsWith("/tags/v" + origin.Manifest.Version))) selected = origin;
        byte[] bytes;
        if (path.EndsWith("/latest") || path.Contains("/tags/"))
        {
            string prefix = $"https://github.com/{trust.Repository}/releases/download/v{selected.Manifest.Version}/";
            bytes = JsonSerializer.SerializeToUtf8Bytes(new { draft = false, prerelease = false, tag_name = "v" + selected.Manifest.Version,
                assets = new[] { new { name = "release-manifest.json", state = "uploaded", browser_download_url = prefix + "release-manifest.json", size = (long)selected.ManifestBytes.Length },
                    new { name = "release-signature.bin", state = "uploaded", browser_download_url = prefix + "release-signature.bin", size = 64L },
                    new { name = selected.Manifest.ArchiveName, state = "uploaded", browser_download_url = prefix + selected.Manifest.ArchiveName, size = selected.Manifest.ArchiveBytes } } });
        }
        else if (path.EndsWith("/release-manifest.json")) bytes = selected.ManifestBytes;
        else if (path.EndsWith("/release-signature.bin")) bytes = BadSignature ? new byte[64] : selected.Signature;
        else { ZipDownloads++; bytes = selected.Archive; }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
