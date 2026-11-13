using System.Text;
using System.Text.Json;

namespace Switcheroonie.Broker;

internal static class PreferenceFiles
{
    internal static GameInputSettings ReadGame(string path)
    {
        using var document = Read(path);
        var fields = Fields(document, "Enabled", "Sensitivity", "Height");
        if (!fields.TryGetValue("Enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !fields.TryGetValue("Sensitivity", out var sensitivity) || sensitivity.ValueKind != JsonValueKind.Number ||
            !sensitivity.TryGetDouble(out double value) || !double.IsFinite(value) || value is < .1 or > 5)
            throw new JsonException("Control settings are missing or invalid");
        double height = 0; // Earlier versions omitted only this field.
        if (fields.TryGetValue("Height", out var offset) && (offset.ValueKind != JsonValueKind.Number || !offset.TryGetDouble(out height) ||
            !double.IsFinite(height) || height is < -1.5 or > 1.5)) throw new JsonException("Invalid saved height");
        return new(enabled.GetBoolean(), value, height);
    }

    internal static RoutingPreferences ReadRouting(string path)
    {
        using var document = Read(path);
        var fields = Fields(document, "Automatic", "ManualMode");
        if (!fields.TryGetValue("Automatic", out var automatic) || automatic.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Automatic preference is missing or invalid");
        string? manual = null;
        if (fields.TryGetValue("ManualMode", out var mode) && mode.ValueKind != JsonValueKind.Null)
        {
            if (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("Physical" or "Desktop"))
                throw new JsonException("Invalid manual routing preference");
            manual = mode.GetString();
        }
        return new(automatic.GetBoolean(), manual);
    }

    static JsonDocument Read(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var text = new char[8193];
        int count;
        try { count = reader.ReadBlock(text, 0, text.Length); }
        catch (DecoderFallbackException error) { throw new JsonException("Invalid preferences encoding", error); }
        if (count == text.Length) throw new JsonException("Preferences exceed the size limit");
        return JsonDocument.Parse(new string(text, 0, count));
    }

    static Dictionary<string, JsonElement> Fields(JsonDocument document, params string[] knownFields)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Settings must be an object");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            string? known = knownFields.FirstOrDefault(x => x.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
            if (known is null) continue;
            if (property.Name != known || !fields.TryAdd(known, property.Value)) throw new JsonException("Ambiguous setting");
        }
        return fields;
    }

    internal static void Write<T>(string path, T settings)
    {
        // Keep the last complete file until a unique sibling is fully written.
        // Never reuse or remove a previous run's recovery/temp file.
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (created)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
