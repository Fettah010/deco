using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// Generic port of the test-mirror-parity.ps1 comparison engine: walks two
/// trees (app vs template), skips hygiene dirs, applies glob excludes and
/// A=B renames, then reports files missing on either side plus SHA256 content
/// diffs. Repo-specific allowlists travel as --exclude/--rename flags, never
/// as hardcoded paths. Guard-manifest checking (template #if conditions)
/// stays a repo-side concern for Phase 3.
/// </summary>
public static partial class ParityChecker
{
    public sealed record Report(
        string[] MissingInTemplate,
        string[] MissingInApp,
        string[] Different,
        int Compared);

    public static Report Compare(
        string appRoot,
        string templateRoot,
        string[] skipDirs,
        string[] excludes,
        Dictionary<string, string> renames)
    {
        var appFiles = RelativeFiles(appRoot, skipDirs);
        var templateFiles = RelativeFiles(templateRoot, skipDirs);

        var renameBack = renames.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
        var missingInTemplate = new List<string>();
        var missingInApp = new List<string>();
        var different = new List<string>();
        int compared = 0;

        var templateSet = new HashSet<string>(templateFiles.Keys, StringComparer.OrdinalIgnoreCase);
        var matchedTemplate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string rel, string full) in appFiles.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (IsExcluded(rel, excludes))
            {
                continue;
            }

            string templateRel = renames.TryGetValue(rel, out string? renamed) ? renamed : rel;
            if (!templateSet.Contains(templateRel))
            {
                missingInTemplate.Add(rel);
                continue;
            }

            matchedTemplate.Add(templateRel);
            compared++;
            if (Sha256(full) != Sha256(templateFiles[templateRel]))
            {
                different.Add(rel);
            }
        }

        foreach (string rel in templateSet.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            if (matchedTemplate.Contains(rel) || IsExcluded(rel, excludes))
            {
                continue;
            }

            string appRel = renameBack.TryGetValue(rel, out string? back) ? back : rel;
            if (!appFiles.ContainsKey(appRel) || IsExcluded(appRel, excludes))
            {
                missingInApp.Add(rel);
            }
        }

        return new Report([.. missingInTemplate], [.. missingInApp], [.. different], compared);
    }

    public static Dictionary<string, string> RelativeFiles(string root, string[] skipDirs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skip = new HashSet<string>(skipDirs, StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch
            {
                continue;
            }

            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
            foreach (string e in entries)
            {
                string name = Path.GetFileName(e);
                if (Directory.Exists(e))
                {
                    if (name.StartsWith('.') || skip.Contains(name))
                    {
                        continue;
                    }

                    stack.Push(e);
                }
                else
                {
                    string rel = Path.GetRelativePath(root, e);
                    result[rel] = e;
                }
            }
        }

        return result;
    }

    public static bool IsExcluded(string relPath, string[] patterns)
    {
        string normalized = relPath.Replace('\\', '/');
        foreach (string p in patterns)
        {
            string pattern = p.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(pattern))
            {
                continue;
            }

            if (GlobToRegex(pattern).IsMatch(normalized))
            {
                return true;
            }
        }

        return false;
    }

    internal static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    sb.Append("(.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i++;
                }
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }

    internal static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
