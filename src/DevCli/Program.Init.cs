// Command handlers: init. Shared helpers, types, Main, and the command tree stay in Program.cs.
using DevCli.Commands;
using DevCli.Core;

static partial class Program
{
    private static int RunInit(string? project, string verbosity, string format, bool noAnsi,
        string appId, string storeId, string csproj, string localesCsv,
        string vmName, string vmUser, bool force, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        string dir = string.IsNullOrWhiteSpace(project)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(project.Trim());
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Error: Directory not found: {dir}");
            return ExitCodes.UsageOrAuthNeeded;
        }

        string[] locales;
        try
        {
            locales = InitProject.ValidateLocales(localesCsv);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            appId = GuessAppId(dir, csproj);
        }

        string? csprojRel = string.IsNullOrWhiteSpace(csproj) ? GuessCsproj(dir) : csproj.Trim();
        if (!string.IsNullOrWhiteSpace(csprojRel))
        {
            string full = Path.IsPathRooted(csprojRel) ? csprojRel : Path.Combine(dir, csprojRel);
            if (!File.Exists(full))
            {
                Console.Error.WriteLine($"Error: csproj not found: {full}");
                return ExitCodes.Error;
            }

            if (full.StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                csprojRel = Path.GetRelativePath(dir, full);
            }
            else
            {
                csprojRel = full;
            }
        }

        var plan = new InitProject.Plan(dir, appId.Trim(), csprojRel, storeId,
            locales, vmName, vmUser, force, dryRun);
        if (dryRun)
        {
            Console.WriteLine("DRYRUN plan (nothing written):");
            Console.WriteLine($"  dir: {dir}");
            Console.WriteLine($"  appId: {plan.AppId}");
            Console.WriteLine($"  locales: {string.Join(", ", locales)}");
            Console.WriteLine($"  manifest: {Path.Combine(dir, ".devcli.json")}");
            Console.WriteLine($"  shim: {Path.Combine(dir, "Scripts", "dev.ps1")}");
            return ExitCodes.Success;
        }

        InitProject.Result result;
        try
        {
            result = InitProject.Run(plan);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        Console.WriteLine(result.ManifestWritten
            ? $"Wrote {result.ManifestPath}"
            : $"Kept {result.ManifestPath}");
        if (result.ShimPath is not null)
        {
            Console.WriteLine(result.ShimWritten
                ? $"Wrote {result.ShimPath}"
                : $"Kept {result.ShimPath}");
        }

        Console.WriteLine("Next: deco doctor");
        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "init",
                dir,
                appId = plan.AppId,
                locales,
                manifest = result.ManifestPath,
                shim = result.ShimPath,
            });
        }

        return ExitCodes.Success;
    }

    private static string GuessAppId(string dir, string csproj)
    {
        string manifestPath = Path.Combine(dir, ".devcli.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                string existing = ProjectDiscovery.LoadManifest(manifestPath).AppId;
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing.Trim();
                }
            }
            catch (DecoException)
            {
            }
        }

        string? csprojPath = string.IsNullOrWhiteSpace(csproj)
            ? GuessCsproj(dir)
            : (Path.IsPathRooted(csproj) ? csproj : Path.Combine(dir, csproj));
        if (!string.IsNullOrWhiteSpace(csprojPath) && File.Exists(csprojPath))
        {
            return Path.GetFileNameWithoutExtension(csprojPath);
        }

        return new DirectoryInfo(dir).Name;
    }

    private static string? GuessCsproj(string dir)
    {
        string[] found;
        try
        {
            found = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return null;
        }

        Array.Sort(found, StringComparer.OrdinalIgnoreCase);
        foreach (string f in found)
        {
            if (!f.Contains(".Tests", StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }

        return found.Length > 0 ? found[0] : null;
    }
}
