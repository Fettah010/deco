using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// `deco init` core: locale validation, manifest generation, and file
/// writing (temp-dir testable). The dev.ps1 shim text is embedded so the
/// installed single-file exe needs no templates folder at runtime; a test
/// pins it byte-identical to templates/dev.ps1 (no drift, ever).
/// </summary>
public static class InitProject
{
    public sealed record Plan(
        string Dir,
        string AppId,
        string? Csproj,
        string? StoreAppId,
        string[] Locales,
        string? VmName,
        string? VmUser,
        bool Force,
        bool DryRun);

    public sealed record Result(
        string ManifestPath,
        string? ShimPath,
        bool ManifestWritten,
        bool ShimWritten);

    private static readonly Regex LocaleRe = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})+$");

    /// <summary>Parses "en-US, fr-FR" strictly; empty = ["en-US"]. Throws DecoException naming bad entries.</summary>
    public static string[] ValidateLocales(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return ["en-US"];
        }

        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bad = parts.Where(p => !LocaleRe.IsMatch(p)).ToList();
        if (bad.Count > 0)
        {
            throw new Core.DecoException(
                $"Invalid locale(s): {string.Join(", ", bad)} (want BCP-47 like en-US, fr-FR).",
                "init-locales",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        var clean = Core.ProjectManifest.NormalizeLocales(parts);
        return clean ?? ["en-US"];
    }

    public static string BuildManifest(Plan plan)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$schema"] = "https://decoschema.local/devcli.json",
            ["appId"] = plan.AppId,
            ["locales"] = plan.Locales,
        };
        if (!string.IsNullOrWhiteSpace(plan.Csproj))
        {
            root["csproj"] = plan.Csproj;
        }

        if (!string.IsNullOrWhiteSpace(plan.StoreAppId))
        {
            root["storeAppId"] = plan.StoreAppId!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(plan.VmName) || !string.IsNullOrWhiteSpace(plan.VmUser))
        {
            var capture = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(plan.VmName))
            {
                capture["vmName"] = plan.VmName!.Trim();
            }

            if (!string.IsNullOrWhiteSpace(plan.VmUser))
            {
                capture["vmUser"] = plan.VmUser!.Trim();
            }

            root["capture"] = capture;
        }

        return JsonSerializer.Serialize(root,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    /// <summary>The dev.ps1 shim, pinned to templates/dev.ps1 by test (line-ending insensitive).</summary>
    public static readonly string DevShim = """
#Requires -Version 5.1
<#
.SYNOPSIS
  Thin per-repo shim: every old script becomes `deco <command>`.
  Created by `deco init`. Anything not yet ported falls through to docs/archive/ with a pointer.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DecoArgs
)

$ErrorActionPreference = 'Stop'

function Find-Deco {
    $c = Get-Command deco -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $local = Join-Path $PSScriptRoot '..' '..' 'DevCli' 'src' 'DevCli' 'bin' 'Release' 'net10.0' 'deco.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    throw "deco not found: install it (see DevCli/packaging/install.ps1) or build DevCli."
}

$repo = Split-Path -Parent $PSScriptRoot
$json = Join-Path $repo '.devcli.json'
$extra = @()
if (Test-Path -LiteralPath $json) { $extra += @('--project', $repo) }

& (Find-Deco) @extra @DecoArgs
exit $LASTEXITCODE
""" + "\n";

    public static Result Run(Plan plan)
    {
        Directory.CreateDirectory(plan.Dir);
        string manifestPath = Path.Combine(plan.Dir, ".devcli.json");
        string shimPath = Path.Combine(plan.Dir, "Scripts", "dev.ps1");
        bool manifestExists = File.Exists(manifestPath);
        bool shimExists = File.Exists(shimPath);
        if ((manifestExists || shimExists) && !plan.Force && !plan.DryRun)
        {
            string what = manifestExists ? manifestPath : shimPath;
            throw new Core.DecoException(
                $"Already initialized ({what} exists). Re-run with --force to overwrite.",
                "init-exists",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        if (plan.DryRun)
        {
            return new Result(manifestPath, shimPath, false, false);
        }

        bool manifestWritten = false;
        bool shimWritten = false;
        if (!manifestExists || plan.Force)
        {
            File.WriteAllText(manifestPath, BuildManifest(plan), System.Text.Encoding.UTF8);
            manifestWritten = true;
        }

        if (!shimExists || plan.Force)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shimPath)!);
            File.WriteAllText(shimPath, DevShim, System.Text.Encoding.UTF8);
            shimWritten = true;
        }

        return new Result(manifestPath, shimExists && !plan.Force ? null : shimPath, manifestWritten, shimWritten);
    }
}
