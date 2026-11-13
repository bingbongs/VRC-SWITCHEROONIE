namespace Switcheroonie.UI;

internal sealed record ReadinessMessage(string Title, string Summary, string RuntimeLabel);

internal static class ReadinessPresentation
{
    internal static string DesktopCaption(HarnessStatus status) => !status.GameInputEnabled ? "Controls disabled" :
        !status.GameInputActive ? "Click VRChat to resume" : status.MenuNavigation ? "Menu pointer" : "Mouse look";
    internal static ReadinessMessage? Describe(HarnessStatus status, SetupState setup)
    {
        // Disk settings describe the next startup; never hide a still-live route.
        if (status.DriverAlive && status.RoutingReady && (status.NativeCapabilityFlags & 1) != 0) return null;
        if (status.DriverAlive)
        {
            var fault = status.NativeError switch
            {
                5 => new ReadinessMessage("SteamVR hook conflict", "Routing stopped; see Help & diagnostics", "Hook conflict"),
                6 => new ReadinessMessage("SteamVR needs review", "Runtime compatibility check failed", "Unsupported runtime"),
                7 => new ReadinessMessage("Driver opt-in is off", "Review SteamVR setup in Settings", "Opt-in disabled"),
                8 => new ReadinessMessage("Driver connection failed", "See Help & diagnostics", "Connection unavailable"),
                10 => new ReadinessMessage("Driver settings unavailable", "See Help & diagnostics", "Settings unavailable"),
                _ => null
            };
            if (fault is not null) return fault;
        }
        return setup.State switch
        {
            "RuntimeDisabled" => new("Disabled in SteamVR", "Your add-on setting is preserved", "Add-on disabled"),
            "RuntimeBlocked" => new("Blocked by SteamVR", "Review the add-on problem in Settings", "Add-on blocked"),
            "Unsupported" => new("SteamVR needs review", "This runtime build is not supported", "Unsupported runtime"),
            "Recovery" => new("SteamVR setup needs review", "Open Settings for the setup details", "Setup needs review"),
            "UpdateNeeded" => new("Driver update needed", "Check for a signed update in Settings", "Update needed"),
            "Unavailable" => new("Setup check unavailable", "Check SteamVR setup in Settings", "Setup check unavailable"),
            _ => null
        };
    }
}
