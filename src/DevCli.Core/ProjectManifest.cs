using System.Text.Json.Serialization;

namespace DevCli.Core;

/// <summary>
/// Repo manifest (<c>.devcli.json</c>). One schema serves every app
/// (Formixa, DevDesk, template-scaffolded apps). All fields optional
/// except <see cref="AppId"/> so discovery never crashes on partial files.
/// </summary>
public sealed record ProjectManifest
{
    [JsonPropertyName("appId")]
    public string AppId { get; init; } = string.Empty;

    [JsonPropertyName("csproj")]
    public string? Csproj { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("storeAppId")]
    public string? StoreAppId { get; init; }

    [JsonPropertyName("defaultPublisher")]
    public string? DefaultPublisher { get; init; }

    [JsonPropertyName("channels")]
    public string[] Channels { get; init; } = ["stable", "beta", "dev"];

    [JsonPropertyName("features")]
    public Dictionary<string, string> Features { get; init; } = new();

    /// <summary>Schema v2: Store listing locales (e.g. ["en-US"]). Empty/absent = legacy default.</summary>
    [JsonPropertyName("locales")]
    public string[]? Locales { get; init; }

    /// <summary>Schema v2: repo layout overrides (all relative to the repo root unless rooted).</summary>
    [JsonPropertyName("paths")]
    public ManifestPaths? Paths { get; init; }

    /// <summary>Schema v2: capture lab defaults (flag always wins over these).</summary>
    [JsonPropertyName("capture")]
    public ManifestCapture? Capture { get; init; }

    /// <summary>Trims, drops empties, dedupes (order kept). Empty result = null (caller falls back).</summary>
    public static string[]? NormalizeLocales(string[]? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var clean = raw
            .Select(s => (s ?? string.Empty).Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return clean.Length == 0 ? null : clean;
    }
}

/// <summary>Schema v2 repo layout overrides. Every field optional.</summary>
public sealed record ManifestPaths
{
    [JsonPropertyName("msixDir")]
    public string? MsixDir { get; init; }

    [JsonPropertyName("storeDir")]
    public string? StoreDir { get; init; }

    [JsonPropertyName("listingSourceDir")]
    public string? ListingSourceDir { get; init; }
}

/// <summary>Schema v2 capture lab defaults. Every field optional.</summary>
public sealed record ManifestCapture
{
    [JsonPropertyName("vmName")]
    public string? VmName { get; init; }

    [JsonPropertyName("vmUser")]
    public string? VmUser { get; init; }
}
