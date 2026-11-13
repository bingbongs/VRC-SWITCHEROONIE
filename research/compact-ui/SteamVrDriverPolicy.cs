using System.Text.Json;

namespace Switcheroonie.UI;

internal enum SteamVrDriverPolicy { Unknown, Allowed, Disabled, BlockedBySafeMode }

// Read only. These are Valve's per-driver enable and blocked_by_safe_mode keys.
// Never clear a deliberate disable or a runtime crash block during setup/update.
internal static class SteamVrDriverSettings
{
    internal static SteamVrDriverPolicy Parse(string? text)
    {
        if (text is null) return SteamVrDriverPolicy.Unknown;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return SteamVrDriverPolicy.Unknown;
            JsonElement? section = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("driver_switcheroonie", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Name != "driver_switcheroonie" || section.HasValue) return SteamVrDriverPolicy.Unknown;
                section = property.Value;
            }
            // An absent override retains the packaged default (enabled).
            if (section is null) return SteamVrDriverPolicy.Allowed;
            if (section.Value.ValueKind != JsonValueKind.Object) return SteamVrDriverPolicy.Unknown;
            bool enabled = true, blocked = false, sawEnable = false, sawBlocked = false;
            foreach (var property in section.Value.EnumerateObject())
            {
                bool isEnable = property.Name.Equals("enable", StringComparison.OrdinalIgnoreCase);
                bool isBlocked = property.Name.Equals("blocked_by_safe_mode", StringComparison.OrdinalIgnoreCase);
                if (!isEnable && !isBlocked) continue;
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return SteamVrDriverPolicy.Unknown;
                if (isEnable)
                {
                    if (sawEnable || property.Name != "enable") return SteamVrDriverPolicy.Unknown;
                    sawEnable = true; enabled = property.Value.GetBoolean();
                }
                else
                {
                    if (sawBlocked || property.Name != "blocked_by_safe_mode") return SteamVrDriverPolicy.Unknown;
                    sawBlocked = true; blocked = property.Value.GetBoolean();
                }
            }
            return blocked ? SteamVrDriverPolicy.BlockedBySafeMode : enabled ? SteamVrDriverPolicy.Allowed : SteamVrDriverPolicy.Disabled;
        }
        catch (JsonException) { return SteamVrDriverPolicy.Unknown; }
    }
}
