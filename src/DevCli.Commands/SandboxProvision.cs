using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevCli.Commands;

/// <summary>
/// Fetches + lays out the Sandbox guest dependencies (FlaUI net48 stack, no
/// SDK needed in the guest): flaui.core + flaui.uia3 + interop.uiautomationclient
/// nupkgs from nuget.org, net48/net45 DLLs extracted to a versioned dir.
/// Offline after the first run; `--force` re-fetches. HttpClient is injected
/// so tests serve crafted zips without touching the network.
/// </summary>
public static class SandboxProvision
{
    public const string FlaUIVersion = "5.0.0";
    public const string InteropVersion = "10.19041.0";

    public sealed record WantedPackage(string Id, string Version, string LibPath, string FileName);

    public sealed record FileEntry(string Name, string Sha256, long Bytes);

    public sealed record DepsManifest(string FlaUIVersion, string InteropVersion, List<FileEntry> Files, string ProvisionedUtc);

    public static IReadOnlyList<WantedPackage> Packages(string flaUI = FlaUIVersion, string interop = InteropVersion) =>
    [
        new("flaui.core", flaUI, "lib/net48/FlaUI.Core.dll", "FlaUI.Core.dll"),
        new("flaui.uia3", flaUI, "lib/net48/FlaUI.UIA3.dll", "FlaUI.UIA3.dll"),
        new("interop.uiautomationclient", interop, "lib/net45/Interop.UIAutomationClient.dll", "Interop.UIAutomationClient.dll"),
    ];

    public static string NupkgUrl(WantedPackage p) =>
        $"https://api.nuget.org/v3-flatcontainer/{p.Id}/{p.Version}/{p.Id}.{p.Version}.nupkg";

    public static string ManifestName => "deps-manifest.json";

    public static string DepsDir(string localAppData, string version = FlaUIVersion) =>
        Path.Combine(localAppData, "deco", "sandbox-guest", $"flaui-{version}");

    public static bool IsProvisioned(string depsDir, string version = FlaUIVersion)
    {
        string manifestPath = Path.Combine(depsDir, ManifestName);
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            var root = doc.RootElement;
            if (!root.TryGetProperty("flaUIVersion", out var v) || v.GetString() != version)
            {
                return false;
            }

            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var f in files.EnumerateArray())
            {
                string name = f.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name) || !File.Exists(Path.Combine(depsDir, name)))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<DepsManifest> ProvisionAsync(
        string depsDir, string version, HttpClient http, bool force,
        Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(depsDir);
        if (!force && IsProvisioned(depsDir, version))
        {
            log?.Invoke($"  guest deps already provisioned: {depsDir}");
            string existing = File.ReadAllText(Path.Combine(depsDir, ManifestName), Encoding.UTF8);
            using var doc = JsonDocument.Parse(existing);
            return ReadManifest(doc.RootElement);
        }

        var entries = new List<FileEntry>();
        foreach (WantedPackage p in Packages(version))
        {
            log?.Invoke($"  fetching {p.Id} {p.Version}...");
            byte[] nupkg;
            try
            {
                nupkg = await http.GetByteArrayAsync(NupkgUrl(p), ct);
            }
            catch (Exception ex)
            {
                throw new Core.DecoException(
                    $"Cannot download {p.Id} {p.Version} from nuget.org: {OneLine(ex.Message)}",
                    "sandbox-provision-download");
            }

            byte[] dll = ExtractEntry(nupkg, p);
            string dest = Path.Combine(depsDir, p.FileName);
            await File.WriteAllBytesAsync(dest, dll, ct);
            entries.Add(new FileEntry(p.FileName, Convert.ToHexString(SHA256.HashData(dll)).ToLowerInvariant(), dll.Length));
            log?.Invoke($"  ok: {p.FileName} ({dll.Length} bytes)");
        }

        var manifest = new DepsManifest(version, InteropVersion, entries, DateTime.UtcNow.ToString("o"));
        await File.WriteAllTextAsync(Path.Combine(depsDir, ManifestName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }), ct);
        return manifest;
    }

    internal static byte[] ExtractEntry(byte[] nupkg, WantedPackage p)
    {
        using var ms = new MemoryStream(nupkg, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), p.LibPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new Core.DecoException(
                $"Package {p.Id} {p.Version} has no {p.LibPath} (layout changed upstream).",
                "sandbox-provision-layout");
        }

        using var src = entry.Open();
        using var outMs = new MemoryStream();
        src.CopyTo(outMs);
        return outMs.ToArray();
    }

    internal static DepsManifest ReadManifest(JsonElement root)
    {
        var files = new List<FileEntry>();
        if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                files.Add(new FileEntry(
                    f.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    f.TryGetProperty("sha256", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                    f.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : 0));
            }
        }

        return new DepsManifest(
            root.TryGetProperty("flaUIVersion", out var v) ? v.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("interopVersion", out var iv) ? iv.GetString() ?? string.Empty : string.Empty,
            files,
            root.TryGetProperty("provisionedUtc", out var u) ? u.GetString() ?? string.Empty : string.Empty);
    }

    internal static string OneLine(string s)
    {
        string line = (s ?? string.Empty).Split('\n')[0].Trim();
        return line.Length <= 160 ? line : line[..160];
    }
}
