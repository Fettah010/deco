using System.Text.Json;
using System.Xml;

namespace DevCli.Core;

/// <summary>
/// Locates the target repo and builds a <see cref="ProjectContext"/>.
/// Precedence: --project flag &gt; .devcli.json walk-up &gt; .git root &gt; *.csproj heuristic.
/// Never throws raw IO exceptions; always <see cref="DecoException"/> with actionable text.
/// </summary>
public static class ProjectDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ProjectContext Discover(string? startDirOrFile)
    {
        string startDir = ResolveStartDir(startDirOrFile);
        string? manifestDir = WalkUpForFile(startDir, CliConstants.ManifestFileName);
        string repoRoot = FindRepoRoot(startDir, manifestDir);

        ProjectManifest? manifest = null;
        bool manifestFound = false;
        if (manifestDir is not null)
        {
            string manifestPath = Path.Combine(manifestDir, CliConstants.ManifestFileName);
            manifest = LoadManifest(manifestPath);
            manifestFound = true;
        }

        string? csprojPath = ResolveCsproj(repoRoot, manifest);
        string? version = null;
        string? informationalVersion = null;
        if (csprojPath is not null)
        {
            (version, informationalVersion) = ReadVersions(csprojPath);
        }

        string appId = manifest?.AppId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(appId))
        {
            appId = csprojPath is not null
                ? Path.GetFileNameWithoutExtension(csprojPath)
                : new DirectoryInfo(repoRoot).Name;
        }

        return new ProjectContext(
            repoRoot,
            appId,
            csprojPath,
            version,
            informationalVersion,
            manifest?.StoreAppId,
            manifestFound,
            ProjectManifest.NormalizeLocales(manifest?.Locales) ?? CliConstants.DefaultLocales,
            ResolveDir(repoRoot, manifest?.Paths?.MsixDir, "Releases", "Msix"),
            ResolveDir(repoRoot, manifest?.Paths?.StoreDir, "Releases", "Store"),
            ResolveDir(repoRoot, manifest?.Paths?.ListingSourceDir, "Store", "ListingSource"),
            BlankToNull(manifest?.Capture?.VmName),
            BlankToNull(manifest?.Capture?.VmUser));
    }

    private static string ResolveDir(string repoRoot, string? configured, params string[] legacyParts)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string trimmed = configured.Trim();
            string rooted = Path.IsPathRooted(trimmed)
                ? trimmed : Path.Combine(repoRoot, trimmed);
            return Path.GetFullPath(rooted);
        }

        return Path.Combine(new[] { repoRoot }.Concat(legacyParts).ToArray());
    }

    private static string? BlankToNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static ProjectManifest LoadManifest(string manifestPath)
    {
        try
        {
            string json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions);
            if (manifest is null)
            {
                throw new DecoException(
                    $"Manifest '{manifestPath}' is empty or invalid JSON.",
                    "invalid-manifest");
            }

            return manifest;
        }
        catch (DecoException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DecoException(
                $"Cannot read manifest '{manifestPath}': {ex.Message}",
                "manifest-unreadable");
        }
    }

    private static string ResolveStartDir(string? startDirOrFile)
    {
        string candidate = string.IsNullOrWhiteSpace(startDirOrFile)
            ? Directory.GetCurrentDirectory()
            : startDirOrFile;

        if (File.Exists(candidate))
        {
            return Path.GetDirectoryName(Path.GetFullPath(candidate)) ?? Directory.GetCurrentDirectory();
        }

        if (Directory.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        throw new DecoException(
            $"--project path '{candidate}' does not exist. Pass a repo folder, a .csproj file, or omit it to use the current directory.",
            "project-not-found",
            ExitCodes.UsageOrAuthNeeded);
    }

    private static string? WalkUpForFile(string startDir, string fileName)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, fileName)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string FindRepoRoot(string startDir, string? manifestDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return manifestDir ?? startDir;
    }

    private static string? ResolveCsproj(string repoRoot, ProjectManifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest?.Csproj))
        {
            string declared = Path.IsPathRooted(manifest.Csproj)
                ? manifest.Csproj
                : Path.Combine(repoRoot, manifest.Csproj);
            if (File.Exists(declared))
            {
                return Path.GetFullPath(declared);
            }
        }

        string[] found = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.TopDirectoryOnly);
        if (found.Length == 0)
        {
            return null;
        }

        Array.Sort(found, StringComparer.OrdinalIgnoreCase);
        foreach (string f in found)
        {
            if (!f.Contains(".Tests", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(f);
            }
        }

        return Path.GetFullPath(found[0]);
    }

    private static (string? Version, string? InformationalVersion) ReadVersions(string csprojPath)
    {
        try
        {
            var doc = new XmlDocument();
            doc.Load(csprojPath);
            string? version = SelectFirst(doc, "Version");
            string? informational = SelectFirst(doc, "InformationalVersion");
            return (version?.Trim(), informational?.Trim());
        }
        catch (DecoException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DecoException(
                $"Cannot read versions from '{csprojPath}': {ex.Message}",
                "csproj-unreadable");
        }
    }

    private static string? SelectFirst(XmlDocument doc, string tag)
    {
        XmlNodeList nodes = doc.GetElementsByTagName(tag);
        foreach (XmlNode n in nodes)
        {
            if (!string.IsNullOrWhiteSpace(n.InnerText))
            {
                return n.InnerText;
            }
        }

        return null;
    }
}
