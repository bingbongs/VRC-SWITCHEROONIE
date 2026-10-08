using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Switcheroonie.Update;

public sealed class UpdateService : IDisposable
{
    readonly ReleaseTrust? trust;
    readonly VersionStore store;
    readonly HttpClient http;
    readonly IUpdateSafety safety;
    public UpdateService(string? storeRoot = null, ReleaseTrust? trust = null,
        HttpMessageHandler? handler = null, IUpdateSafety? safety = null)
    {
        this.trust = trust;
        store = new(storeRoot ?? Path.Combine(StatePaths.Current.DataDirectory, "updates"));
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VRC-SWITCHEROONIE-Updater/0.2.1");
        http.DefaultRequestHeaders.Accept.Add(new("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        this.safety = safety ?? new WindowsUpdateSafety();
    }
    ReleaseTrust Trust => trust ?? ReleaseTrust.Production();
    public UpdateStatus Status()
    {
        try
        {
            var policy = Trust;
            if (store.TransitionPending) return new("Deferred", "Recorded driver/version transition needs normal shutdown before launch.", null, true, true);
            string? pending = store.PendingVersion;
            if (pending is not null) store.VerifyVersion(pending, policy);
            return pending is not null
                ? new("Staged", "Verified update downloaded; activation waits for normal shutdown.", pending, true, true)
                : new("Current", "Automatic signed update checks are available.", store.Current?.Version, false, store.Current?.DriverPending ?? false);
        }
        catch { return new("Unavailable", "Update state cannot be verified; the running version is unchanged."); }
    }
    public async Task<UpdateStatus> CheckAndStageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var policy = Trust;
            using var updateLock = store.AcquireLock();
            if (store.TransitionPending) return new("Deferred", "A recorded driver/version transition must finish after normal shutdown.", null, true, true);
            if (store.PendingVersion is string pending)
            { store.VerifyVersion(pending, policy); return new("Staged", "A verified update is already awaiting normal shutdown.", pending, true, true); }
            Uri api = new($"https://api.github.com/repos/{policy.Repository}/releases/latest");
            using var response = await GetAsync(api, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return new("Current", "No public signed release is available yet.");
            response.EnsureSuccessStatusCode();
            byte[] releaseBytes = await ReadBoundedAsync(response.Content, 2 * 1024 * 1024, cancellationToken);
            using var release = JsonDocument.Parse(releaseBytes);
            var root = release.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("Unpublished or prerelease feed entry.");
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith('v')) throw new InvalidDataException("Release tag is invalid.");
            string version = tag[1..]; ReleaseTrust.ParseVersion(version);
            string originVersion = store.RememberedBootstrapVersion(policy) ?? policy.BaselineVersion;
            if (ReleaseTrust.ParseVersion(version) <= ReleaseTrust.ParseVersion(store.Current?.Version ?? originVersion))
                return new("Current", "This portable version is current.");
            // Keep a real signed first-release rollback, even when the initial portable package has no selection pointer.
            if (store.Current is null && !Directory.Exists(store.VersionDirectory(policy.BaselineVersion)))
            {
                using var baselineResponse = await GetAsync(new($"https://api.github.com/repos/{policy.Repository}/releases/tags/v{policy.BaselineVersion}"), cancellationToken);
                baselineResponse.EnsureSuccessStatusCode();
                using var baseline = JsonDocument.Parse(await ReadBoundedAsync(baselineResponse.Content, 2 * 1024 * 1024, cancellationToken));
                await DownloadReleaseAsync(baseline.RootElement, policy.BaselineVersion, policy, true, cancellationToken);
            }
            if (store.Current is null) store.VerifyVersion(policy.BaselineVersion, policy);
            if (ReleaseTrust.ParseVersion(originVersion) > ReleaseTrust.ParseVersion(version))
                throw new InvalidDataException("Original launcher version exceeds the candidate.");
            if (store.Current is null && originVersion != policy.BaselineVersion &&
                !Directory.Exists(store.VersionDirectory(originVersion)))
            {
                using var originResponse = await GetAsync(new($"https://api.github.com/repos/{policy.Repository}/releases/tags/v{originVersion}"), cancellationToken);
                originResponse.EnsureSuccessStatusCode();
                using var origin = JsonDocument.Parse(await ReadBoundedAsync(originResponse.Content, 2 * 1024 * 1024, cancellationToken));
                await DownloadReleaseAsync(origin.RootElement, originVersion, policy, true, cancellationToken, true);
            }
            if (store.Current is null && originVersion != policy.BaselineVersion) store.VerifyVersion(originVersion, policy);
            await DownloadReleaseAsync(root, version, policy, false, cancellationToken);
            return new("Staged", "Signed update downloaded. Your current session continues; activation waits for normal shutdown.", version, true, true);
        }
        catch (OperationCanceledException) { return new("Deferred", "Update check was canceled; the running version is unchanged."); }
        catch (IOException) { return new("Unavailable", "Update storage is busy or verification failed; the running version is unchanged."); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or
            System.Security.Cryptography.CryptographicException or ArgumentException or InvalidOperationException)
        { return new("Unavailable", "No trusted update could be staged; the running version is unchanged."); }
    }
    async Task DownloadReleaseAsync(JsonElement root, string version, ReleaseTrust policy, bool bootstrap, CancellationToken cancellationToken,
        bool originReceipt = false)
    {
            string tag = "v" + version;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() ||
                root.GetProperty("tag_name").GetString() != tag) throw new InvalidDataException("Unexpected signed release identity.");
            string prefix = $"https://github.com/{policy.Repository}/releases/download/{tag}/";
            var assets = root.GetProperty("assets").EnumerateArray().ToDictionary(
                x => x.GetProperty("name").GetString() ?? "", x => x.Clone(), StringComparer.Ordinal);
            Uri Asset(string name)
            {
                if (!assets.TryGetValue(name, out var asset) || asset.GetProperty("state").GetString() != "uploaded" ||
                    asset.GetProperty("browser_download_url").GetString() != prefix + name)
                    throw new InvalidDataException("Unexpected release asset location.");
                return new Uri(prefix + name);
            }
            using var manifestResponse = await GetAsync(Asset("release-manifest.json"), cancellationToken);
            manifestResponse.EnsureSuccessStatusCode();
            byte[] bytes = await ReadBoundedAsync(manifestResponse.Content, ManifestVerifier.MaxManifestBytes, cancellationToken);
            using var signatureResponse = await GetAsync(Asset("release-signature.bin"), cancellationToken);
            signatureResponse.EnsureSuccessStatusCode();
            byte[] signature = await ReadBoundedAsync(signatureResponse.Content, 64, cancellationToken);
            var manifest = ManifestVerifier.Verify(bytes, signature, policy);
            if (manifest.Version != version || (!bootstrap && manifest.Sequence <= store.HighestSequence))
                throw new InvalidDataException("Release version/sequence replay refused.");
            Uri archiveUri = Asset(manifest.ArchiveName);
            if (assets[manifest.ArchiveName].GetProperty("size").GetInt64() != manifest.ArchiveBytes)
                throw new InvalidDataException("Release asset size disagrees with signed manifest.");
            using var archiveResponse = await GetAsync(archiveUri, cancellationToken);
            archiveResponse.EnsureSuccessStatusCode();
            if (archiveResponse.Content.Headers.ContentLength is long length && length != manifest.ArchiveBytes)
                throw new InvalidDataException("Archive response size disagrees with signed manifest.");
            await using var archive = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken);
            if (originReceipt) await store.StageOriginAsync(archive, manifest, bytes, signature, policy, cancellationToken);
            else if (bootstrap) await store.StageBootstrapAsync(archive, manifest, bytes, signature, policy, cancellationToken);
            else await store.StageAsync(archive, manifest, bytes, signature, policy, cancellationToken);
    }
    async Task<HttpResponseMessage> GetAsync(Uri initial, CancellationToken token)
    {
        Uri current = initial;
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            if (!AllowedDownloadUri(current)) throw new InvalidDataException("Untrusted update redirect.");
            var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null || redirect == 5) throw new InvalidDataException("Too many or missing update redirects.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location); continue;
            }
            return response;
        }
        throw new InvalidDataException("Update redirect limit.");
    }
    public static bool AllowedDownloadUri(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.IdnHost is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength is long length && length > maximum) throw new InvalidDataException("Response exceeds bound.");
        await using var input = await content.ReadAsStreamAsync(token); using var output = new MemoryStream();
        byte[] buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Response exceeds bound.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public UpdateStatus TryActivate()
    {
        try { using var updateLock = store.AcquireLock(); return store.Activate(Trust, safety); }
        catch { return new("Unavailable", "Update activation failed safely; select the previous verified version or retry after normal shutdown."); }
    }
    public UpdateStatus Rollback()
    {
        try { using var updateLock = store.AcquireLock(); return store.Rollback(Trust, safety); }
        catch { return new("Unavailable", "No verified rollback could be selected."); }
    }
    public string ResolveUi(string bootstrapDirectory)
    {
        if (store.TransitionPending) throw new IOException("Driver/version transition is incomplete; launch is deferred.");
        var current = store.Current;
        if (current is null) return Path.Combine(Path.GetFullPath(bootstrapDirectory), "Switcheroonie.UI.exe");
        store.VerifyVersion(current.Version, Trust);
        return Path.Combine(store.PayloadDirectory(current.Version), "Switcheroonie.UI.exe");
    }
    public void RememberBootstrap(string directory)
    {
        using var updateLock = store.AcquireLock();
        var version = typeof(UpdateService).Assembly.GetName().Version ?? throw new InvalidDataException("Compiled release version is unavailable.");
        store.RememberBootstrap(directory, $"{version.Major}.{version.Minor}.{version.Build}");
    }
    public string? ResolveStableLauncher(string currentUiDirectory)
    { try { return store.ResolveStableLauncher(currentUiDirectory, Trust); } catch { return null; } }
    public void Dispose() => http.Dispose();
}
