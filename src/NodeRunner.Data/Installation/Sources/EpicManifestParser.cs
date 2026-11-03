using System.Text.Json;

namespace NodeRunner.Data.Installation.Sources;

// Extracts the install folder from an Epic Games Launcher manifest, if it is the game we want.
public static class EpicManifestParser
{
    private const string GameDisplayName = "Grand Theft Auto V";

    public static string? TryGetGameInstallLocation(string manifestJson)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("DisplayName", out var name) || name.ValueKind != JsonValueKind.String)
            return null;
        if (!(name.GetString() ?? "").StartsWith(GameDisplayName, StringComparison.OrdinalIgnoreCase))
            return null;

        return root.TryGetProperty("InstallLocation", out var loc) && loc.ValueKind == JsonValueKind.String
            ? loc.GetString()
            : null;
    }
}
