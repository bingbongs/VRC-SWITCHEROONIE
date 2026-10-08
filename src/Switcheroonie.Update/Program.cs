using System.Diagnostics;
using System.Text.Json;
using Switcheroonie.Update;

try
{
    if (args.Length == 0 || args[0] == "--launch")
    {
        using var updater = new UpdateService();
        updater.RememberBootstrap(AppContext.BaseDirectory);
        updater.TryActivate();
        string ui = updater.ResolveUi(AppContext.BaseDirectory);
        if (!File.Exists(ui)) throw new IOException("Selected portable UI is missing.");
        Process.Start(new ProcessStartInfo(ui)
        {
            UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ui)!,
            Arguments = args.Contains("--background") ? "--background" : ""
        });
        return 0;
    }
    if (args[0] == "--create-signing-key" && args.Length == 2)
    {
        string publicFile = Path.GetFullPath(args[1]);
        if (File.Exists(publicFile) && File.ReadAllText(publicFile).Trim() != "UNINITIALIZED")
            throw new IOException("Public signing key is already initialized; explicit key rotation is required.");
        byte[] publicKey = SigningKeyStore.Initialize(SigningKeyStore.DefaultPath);
        File.WriteAllText(publicFile, Convert.ToBase64String(publicKey) + Environment.NewLine);
        Console.WriteLine("Public key initialized. The private key remains protected by current-user DPAPI outside the repository.");
        return 0;
    }
    if (args[0] == "--prepare-release" && args.Length == 5)
    {
        var manifest = ReleasePublisher.Prepare(args[1], args[2], args[3], long.Parse(args[4]),
            SigningKeyStore.DefaultPath, ReleaseTrust.Production().PublicKey);
        Console.WriteLine($"Prepared signed release {manifest.Version}, {manifest.Files.Count} files. Nothing published.");
        return 0;
    }
    if (args[0] == "--verify-release" && args.Length == 2)
    {
        var manifest = await ReleasePublisher.VerifyArtifactsAsync(args[1], ReleaseTrust.Production().PublicKey);
        Console.WriteLine($"Verified signed release {manifest.Version}: archive and complete inventory.");
        return 0;
    }
    using var service = new UpdateService();
    UpdateStatus status = args[0] switch
    {
        "--check" => await service.CheckAndStageAsync(),
        "--status" => service.Status(),
        "--activate" => service.TryActivate(),
        "--rollback" => service.Rollback(),
        "--help" => new("Help", "Default/--launch [--background]; --check; --status; --activate; --rollback; --create-signing-key <public-file>; --prepare-release <package> <new-output> <version> <sequence>; --verify-release <artifacts>."),
        _ => throw new ArgumentException("Unknown updater command.")
    };
    Console.WriteLine(JsonSerializer.Serialize(status));
    return status.State == "Unavailable" ? 1 : 0;
}
catch (Exception)
{
    if (args.Length == 0 || args[0] == "--launch")
        LauncherError.Show();
    Console.Error.WriteLine("Updater could not verify or complete this action. Existing application/runtime files are retained.");
    return 1;
}
static class LauncherError
{
    internal static void Show() => MessageBox(IntPtr.Zero,
        "VRC-SWITCHEROONIE could not open its selected portable version. Your VRChat and SteamVR session was not stopped. Use the original package or updater status/recovery instructions.",
        "VRC-SWITCHEROONIE", 0x10);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int MessageBox(IntPtr owner, string message, string title, uint flags);
}
