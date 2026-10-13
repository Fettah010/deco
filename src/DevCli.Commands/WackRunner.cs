using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// Pure port of Scripts/invoke-wack.ps1 gating logic: appcert discovery
/// (PATH + App Certification Kit roots), wack.xml verdict parsing
/// (OVERALL_RESULT + per-TEST CDATA FAIL blocks), and the ps1→deco exit map
/// (ps1 0→0, 2 missing kit→2, 3 not elevated→2, 4 non-PASS→1).
/// Elevation relaunch + the appcert run stay in the handler.
/// </summary>
public static partial class WackRunner
{
    [GeneratedRegex(@"OVERALL_RESULT=""([^""]+)""")]
    private static partial Regex OverallRegex();

    [GeneratedRegex(@"<TEST\b.*?</TEST>", RegexOptions.Singleline)]
    private static partial Regex TestBlockRegex();

    [GeneratedRegex(@"NAME=""([^""]+)""")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"TEXT=""((?:[^""]|&quot;)+)""")]
    private static partial Regex TextRegex();

    public sealed record Failure(string Test, string[] Messages);

    public sealed record Verdict(string Overall, List<Failure> Failures);

    public static string? FindAppCert() =>
        Infrastructure.SdkTools.Find("appcert") ?? FindInCertKit();

    internal static string? FindInCertKit()
    {
        foreach (string pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            if (string.IsNullOrWhiteSpace(pf))
            {
                continue;
            }

            string candidate = Path.Combine(pf, "Windows Kits", "10", "App Certification Kit", "appcert.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static void ValidatePackage(string package)
    {
        if (string.IsNullOrWhiteSpace(package) || !File.Exists(package))
        {
            throw new Core.DecoException(
                $"Package not found: {package}.",
                "wack-package-missing",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        string ext = Path.GetExtension(package);
        if (string.Equals(ext, ".msixupload", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new Core.DecoException(
                $"WACK needs a package or bundle (.msix/.msixbundle), not an upload archive: {package}.",
                "wack-bad-extension",
                Core.ExitCodes.UsageOrAuthNeeded);
        }
    }

    public static string DefaultReportPath(string package) =>
        Path.Combine(Path.GetTempPath(), "wack-" + Path.GetFileNameWithoutExtension(package) + ".xml");

    public static List<string> AppCertArgs(string package, string reportPath) =>
        ["test", "-appxpackagepath", package, "-reportoutputpath", reportPath];

    public static Verdict ParseReport(string reportText)
    {
        Match overall = OverallRegex().Match(reportText);
        string verdict = overall.Success ? overall.Groups[1].Value : "UNKNOWN";

        var failures = new List<Failure>();
        foreach (Match block in TestBlockRegex().Matches(reportText))
        {
            if (!block.Value.Contains("<![CDATA[FAIL]]>", StringComparison.Ordinal))
            {
                continue;
            }

            Match name = NameRegex().Match(block.Value);
            var messages = TextRegex().Matches(block.Value)
                .Select(m => m.Groups[1].Value.Replace("&quot;", "'", StringComparison.Ordinal))
                .Select(m => m.Length > 170 ? m[..170] : m)
                .Take(5)
                .ToArray();
            failures.Add(new Failure(name.Success ? name.Groups[1].Value : "?", messages));
        }

        return new Verdict(verdict, failures);
    }

    /// <summary>ps1 exit → deco exit: 0→0 (PASS), 2→2 (kit missing), 3→2 (elevation), 4→1 (non-PASS).</summary>
    public static int MapVerdictToExit(string overall) =>
        string.Equals(overall, "PASS", StringComparison.OrdinalIgnoreCase)
            ? Core.ExitCodes.Success
            : Core.ExitCodes.Error;
}
