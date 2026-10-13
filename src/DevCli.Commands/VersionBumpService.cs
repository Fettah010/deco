using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// Pure port of Scripts/bump-version.ps1: validates the version, computes the
/// MSIX quad, and rewrites csproj / CITATION.cff / CHANGELOG.md / BuildInfo.cs
/// as TEXT edits (never XML DOM — DOM rewrites bytes and mojibakes comments).
/// All methods are string-in/string-out so they unit-test without touching disk.
/// </summary>
public static partial class VersionBumpService
{
    [GeneratedRegex(@"^\d+\.\d+(?:\.\d+){0,2}(-[A-Za-z0-9.]+)?$")]
    private static partial Regex VersionPattern();

    public static void Validate(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || !VersionPattern().IsMatch(version.Trim()))
        {
            throw new Core.DecoException(
                $"Version '{version}' must have 2-4 numeric parts, optionally followed by a prerelease suffix (e.g. 0.6.2 or 0.6.2-beta).",
                "invalid-version",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        string plain = StripSuffix(version);
        foreach (string part in QuadParts(plain))
        {
            if (!int.TryParse(part, out int n) || n < 0 || n > 65534)
            {
                throw new Core.DecoException(
                    $"Version part '{part}' is not valid for MSIX (0-65534 per part).",
                    "version-part-out-of-range");
            }
        }
    }

    public static string StripSuffix(string version) => version.Trim().Split('-', 2)[0];

    public static string ToQuad(string version)
    {
        var parts = StripSuffix(version).Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count < 4)
        {
            parts.Add("0");
        }

        return string.Join(".", parts.Take(4));
    }

    private static List<string> QuadParts(string plainVersion)
    {
        var parts = plainVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count < 4)
        {
            parts.Add("0");
        }

        return parts.Take(4).ToList();
    }

    /// <summary>
    /// True when the csproj XML text declares a WinExe output (bump target).
    /// Parses just enough XML to find OutputType; returns false on any error.
    /// </summary>
    public static bool IsWinExeCsproj(string csprojText)
    {
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(csprojText);
            foreach (System.Xml.XmlNode n in doc.GetElementsByTagName("OutputType"))
            {
                if (string.Equals(n.InnerText?.Trim(), "WinExe", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>Rewrites the four version elements + ReleasePreviousVersion. Returns (text, touchedNames).</summary>
    public static (string Text, List<string> Touched) UpdateCsprojText(string csprojText, string version)
    {
        string plain = StripSuffix(version);
        string quad = ToQuad(version);
        var touched = new List<string>();

        string oldAssembly = MatchFirst(csprojText, "AssemblyVersion");
        if (!string.IsNullOrWhiteSpace(oldAssembly))
        {
            string next = ReplaceFirst(csprojText, "ReleasePreviousVersion", oldAssembly, out bool hit);
            if (hit)
            {
                csprojText = next;
                touched.Add("ReleasePreviousVersion");
            }
        }

        foreach ((string name, string value) in new[]
                 {
                     ("Version", plain),
                     ("AssemblyVersion", quad),
                     ("FileVersion", quad),
                     ("InformationalVersion", version.Trim()),
                 })
        {
            string next = ReplaceFirst(csprojText, name, value, out bool hit);
            if (hit)
            {
                csprojText = next;
                touched.Add(name);
            }
        }

        return (csprojText, touched);
    }

    public static string UpdateCitationText(string citationText, string version, string date)
    {
        citationText = Regex.Replace(citationText, @"(?m)^version:.*$", $"version: \"{version.Trim()}\"");
        citationText = Regex.Replace(citationText, @"(?m)^date-released:.*$", $"date-released: \"{date}\"");
        return citationText;
    }

    /// <summary>Inserts a "## [version] - date" stub before the first "## " section. No-op when present.</summary>
    public static (string Text, bool Inserted) InsertChangelogStub(string changelogText, string version, string date)
    {
        string v = version.Trim();
        if (Regex.IsMatch(changelogText, @"(?m)^## \[" + Regex.Escape(v) + @"\]"))
        {
            return (changelogText, false);
        }

        string stub = $"\n## [{v}] - {date}\n\n### Added\n\n- TBD\n";
        string[] lines = changelogText.Replace("\r\n", "\n").Split('\n');
        int firstSection = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (firstSection < 0)
        {
            return (changelogText.TrimEnd() + "\n" + stub, true);
        }

        var before = lines.Take(firstSection).ToList();
        var after = lines.Skip(firstSection).ToList();
        while (before.Count > 0 && string.IsNullOrWhiteSpace(before[^1]))
        {
            before.RemoveAt(before.Count - 1);
        }

        string head = string.Join("\n", before);
        string tail = string.Join("\n", after);
        string result = (string.IsNullOrWhiteSpace(head) ? string.Empty : head + "\n") + stub + "\n" + tail;
        return (result, true);
    }

    public static string StampBuildInfoCommit(string buildInfoText, string commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            commit = "unknown";
        }

        return Regex.Replace(buildInfoText, @"Commit = ""[^""]*""", $"Commit = \"{commit.Trim()}\"");
    }

    private static string MatchFirst(string text, string element)
    {
        Match m = Regex.Match(text, $@"(?s)<{element}>(.*?)</{element}>");
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static string ReplaceFirst(string text, string element, string value, out bool hit)
    {
        string pattern = $@"(<{element}>).*?(</{element}>)";
        Match m = Regex.Match(text, pattern, RegexOptions.Singleline);
        hit = m.Success;
        if (!hit)
        {
            return text;
        }

        return text[..m.Index] + m.Groups[1].Value + value + m.Groups[2].Value + text[(m.Index + m.Length)..];
    }
}
