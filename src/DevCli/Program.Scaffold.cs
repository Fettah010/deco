// Command handlers: Scaffold. Shared helpers, types, Main, and the command tree stay in Program.cs.
using System.CommandLine;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using DevCli.Commands;
using DevCli.Core;
using DevCli.Infrastructure;
using Spectre.Console;

static partial class Program
{
    private static int RunScaffoldListing(string? project, string verbosity, string format, bool noAnsi,
        string output, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        string readmePath = Path.Combine(ctx.RepoRoot, "README.md");
        if (!File.Exists(readmePath))
        {
            Console.Error.WriteLine($"Error: README.md not found at {readmePath}.");
            return ExitCodes.Error;
        }

        string outputDir = string.IsNullOrWhiteSpace(output)
            ? Path.Combine(ctx.RepoRoot, "Store", "Listing")
            : output;

        try
        {
            var facts = ListingGenerator.ParseReadme(File.ReadAllText(readmePath, Encoding.UTF8));
            var files = new Dictionary<string, string>
            {
                ["description.txt"] = ListingGenerator.BuildDescription(facts),
                ["screenshots-checklist.md"] = ListingGenerator.ScreenshotsChecklist,
                ["submission-checklist.md"] = ListingGenerator.SubmissionChecklist,
            };

            if (!dryRun)
            {
                Directory.CreateDirectory(outputDir);
                var noBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                foreach ((string name, string content) in files)
                {
                    File.WriteAllText(Path.Combine(outputDir, name), content, noBom);
                }
            }

            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "scaffold listing",
                    dryRun,
                    outputDir,
                    files = files.Keys.ToArray(),
                    title = facts.Title,
                });
                return ExitCodes.Success;
            }

            string prefix = dryRun ? "[dry-run] would write" : "Wrote";
            if (noAnsi || verbosity.Trim().ToLowerInvariant() == "quiet")
            {
                Console.WriteLine($"{prefix} {files.Count} file(s) to {outputDir} (title: {facts.Title}).");
                return ExitCodes.Success;
            }

            AnsiConsole.MarkupLine($"[green]{Markup.Escape(prefix)} {files.Count} file(s) to {Markup.Escape(outputDir)}[/] (title: {Markup.Escape(facts.Title)})");
            AnsiConsole.MarkupLine("[dim]Edit description.txt, then paste into Partner Center.[/]");
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    private static int RunScaffoldProtocol(string? project, string verbosity, string format, bool noAnsi,
        string scheme, string exe, bool unregister, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        string? manifestProtocol = null;
        try
        {
            string manifestPath = Path.Combine(ctx.RepoRoot, CliConstants.ManifestFileName);
            if (File.Exists(manifestPath))
            {
                manifestProtocol = ProjectDiscovery.LoadManifest(manifestPath).Protocol;
            }
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(scheme))
        {
            scheme = manifestProtocol ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(scheme))
        {
            Console.Error.WriteLine("Error: No scheme resolved. Pass --scheme (e.g. \"formixa://\") or set \"protocol\" in .devcli.json.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (string.IsNullOrWhiteSpace(exe) && !unregister)
        {
            exe = FindBuiltExe(ctx.RepoRoot, ctx.AppId) ?? string.Empty;
        }

        List<ProtocolPlan.RegistryAction> actions;
        try
        {
            string exeForPlan = string.IsNullOrWhiteSpace(exe) && dryRun ? "<exe-not-resolved>" : exe;
            actions = ProtocolPlan.Plan(scheme, exeForPlan, unregister);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        string normalized = ProtocolPlan.NormalizeScheme(scheme);
        if (dryRun)
        {
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "scaffold protocol",
                    dryRun = true,
                    unregister,
                    scheme = normalized,
                    exe,
                    actions = actions.Select(a => new { key = a.Key, valueName = a.ValueName, value = a.Value }).ToArray(),
                });
                return ExitCodes.Success;
            }

            if (noAnsi)
            {
                Console.WriteLine($"[dry-run] {(unregister ? "remove" : "register")} {normalized}://");
                foreach (var a in actions)
                {
                    Console.WriteLine($"  HKCU {a.Key} [{a.ValueName ?? "<key>"}] = {a.Value}");
                }

                return ExitCodes.Success;
            }

            AnsiConsole.MarkupLine($"[yellow][dry-run][/] {(unregister ? "remove" : "register")} {Markup.Escape(normalized)}://");
            foreach (var a in actions)
            {
                AnsiConsole.MarkupLine($"  [dim]HKCU {Markup.Escape(a.Key)} [[{Markup.Escape(a.ValueName ?? "<key>")}]] = {Markup.Escape(a.Value)}[/]");
            }

            return ExitCodes.Success;
        }

        bool existedBefore = unregister && OperatingSystem.IsWindows() && ProtocolPlan.IsRegistered(scheme);
        try
        {
            ProtocolPlan.Apply(scheme, exe, unregister);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Error;
        }

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "scaffold protocol",
                dryRun = false,
                unregister,
                scheme = normalized,
                exe,
                wasRegistered = existedBefore,
            });
            return ExitCodes.Success;
        }

        if (noAnsi || verbosity.Trim().ToLowerInvariant() == "quiet")
        {
            if (unregister && !existedBefore)
            {
                Console.WriteLine($"{normalized}:// was not registered.");
                return ExitCodes.Success;
            }

            Console.WriteLine(unregister ? $"Unregistered {normalized}://" : $"Registered {normalized}:// -> {exe}");
            return ExitCodes.Success;
        }

        if (unregister && !existedBefore)
        {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(normalized)}:// was not registered.[/]");
            return ExitCodes.Success;
        }

        AnsiConsole.MarkupLine(unregister
            ? $"[green]Unregistered {Markup.Escape(normalized)}://[/]"
            : $"[green]Registered {Markup.Escape(normalized)}://[/] -> {Markup.Escape(exe)}");
        return ExitCodes.Success;
    }

    private static int RunScaffoldIcon(string? project, string verbosity, string format, bool noAnsi,
        string source, string assetsDir, int padding, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("Error: --source is required (square PNG, >= 512px per side).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (string.IsNullOrWhiteSpace(assetsDir))
        {
            assetsDir = Path.Combine(ctx.RepoRoot, "Assets");
        }

        IconService.Plan plan;
        try
        {
            plan = IconService.Run(source, assetsDir, padding, dryRun);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Error;
        }

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "scaffold icon",
                dryRun,
                sourceWidth = plan.SourceWidth,
                cropSize = plan.CropSize,
                contentWidth = plan.ContentWidth,
                contentHeight = plan.ContentHeight,
                writes = plan.Writes,
                backupDir = plan.BackupDir,
            });
            return ExitCodes.Success;
        }

        if (noAnsi || verbosity.Trim().ToLowerInvariant() == "quiet")
        {
            Console.WriteLine($"{(dryRun ? "[dry-run] would write" : "Wrote")} {plan.Writes.Length} asset(s) from {plan.SourceWidth}px source (crop {plan.CropSize}px).");
            return ExitCodes.Success;
        }

        AnsiConsole.MarkupLine($"[green]{(dryRun ? "[dry-run] would write" : "Wrote")} {plan.Writes.Length} asset(s)[/] from {plan.SourceWidth}px source (visible {plan.ContentWidth}x{plan.ContentHeight}, crop {plan.CropSize}px).");
        foreach (string w in plan.Writes)
        {
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(w)}[/]");
        }

        if (!dryRun)
        {
            AnsiConsole.MarkupLine($"[dim]Backup: {Markup.Escape(plan.BackupDir)}[/]");
            AnsiConsole.MarkupLine("[dim]MSIX tiles render from Logo.png at pack time; nothing else to update.[/]");
        }

        return ExitCodes.Success;
    }

    private static int RunScaffoldShortcut(string? project, string verbosity, string format, bool noAnsi,
        string name, string exe, string devScript, string outputDir, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = ctx.AppId;
        }

        if (string.IsNullOrWhiteSpace(exe))
        {
            exe = FindBuiltExe(ctx.RepoRoot, ctx.AppId) ?? string.Empty;
        }

        string? resolvedDev = ShortcutService.ResolveDevScript(ctx.RepoRoot, devScript);
        if (resolvedDev is null)
        {
            Console.Error.WriteLine($"Error: Dev launcher script not found (expected Scripts/run-app.ps1 or Scripts/launch-dev.ps1; or pass --dev-script).");
            return ExitCodes.Error;
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        string iconPath = Path.Combine(ctx.RepoRoot, "Assets", "app.ico");
        if (!File.Exists(iconPath))
        {
            iconPath = string.Empty;
        }

        ShortcutService.FullPlan plan;
        try
        {
            plan = ShortcutService.Plan(name, exe, resolvedDev, Path.Combine(ctx.RepoRoot, "Scripts"), outputDir,
                string.IsNullOrWhiteSpace(iconPath) ? null : iconPath);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (dryRun)
        {
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "scaffold shortcut",
                    dryRun = true,
                    outputDir,
                    shortcuts = new[]
                    {
                        new { name = plan.Launch.Name, target = plan.Launch.Target, args = plan.Launch.Arguments },
                        new { name = plan.Dev.Name, target = plan.Dev.Target, args = plan.Dev.Arguments },
                    },
                });
                return ExitCodes.Success;
            }

            if (noAnsi)
            {
                Console.WriteLine($"[dry-run] would write 2 shortcut(s) to {outputDir}:");
                Console.WriteLine($"  {plan.Launch.Name} -> {plan.Launch.Target}");
                Console.WriteLine($"  {plan.Dev.Name} -> {plan.Dev.Target} {plan.Dev.Arguments}");
                return ExitCodes.Success;
            }

            AnsiConsole.MarkupLine($"[yellow][dry-run][/] would write 2 shortcut(s) to {Markup.Escape(outputDir)}:");
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(plan.Launch.Name)} -> {Markup.Escape(plan.Launch.Target)}[/]");
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(plan.Dev.Name)} -> {Markup.Escape(plan.Dev.Target)} {Markup.Escape(plan.Dev.Arguments)}[/]");
            return ExitCodes.Success;
        }

        try
        {
            ShortcutService.Apply(plan);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Error;
        }

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "scaffold shortcut",
                dryRun = false,
                outputDir,
                shortcuts = new[] { plan.Launch.Name, plan.Dev.Name },
            });
            return ExitCodes.Success;
        }

        if (noAnsi || verbosity.Trim().ToLowerInvariant() == "quiet")
        {
            Console.WriteLine($"Wrote {plan.Launch.Name} + {plan.Dev.Name} to {outputDir}.");
            return ExitCodes.Success;
        }

        AnsiConsole.MarkupLine($"[green]Wrote {Markup.Escape(plan.Launch.Name)} + {Markup.Escape(plan.Dev.Name)}[/] to {Markup.Escape(outputDir)}.");
        return ExitCodes.Success;
    }

    private static int RunScaffoldSplash(string? project, string verbosity, string format, bool noAnsi,
        string version, string channel, double sizeMB, string output, string logo, bool statik, string theme, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            output = Path.Combine(ctx.RepoRoot, "Releases", "setup-splash.png");
        }

        var req = new SplashService.Request(
            ctx.AppId, version.Trim(), channel.Trim(), sizeMB,
            string.IsNullOrWhiteSpace(logo) ? Path.Combine(ctx.RepoRoot, "Assets", "Logo.png") : logo,
            output, statik, theme.Trim());

        if (dryRun)
        {
            try
            {
                SplashService.Validate(req);
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }

            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "scaffold splash",
                    dryRun = true,
                    output,
                    meta = SplashService.MetaLine(req.Channel, req.SizeMB, req.Static),
                });
                return ExitCodes.Success;
            }

            Console.WriteLine($"[dry-run] would render {SplashService.Width}x{SplashService.Height} splash to {output} ({SplashService.MetaLine(req.Channel, req.SizeMB, req.Static)}).");
            return ExitCodes.Success;
        }

        try
        {
            SplashService.Render(req);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Error;
        }

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "scaffold splash",
                dryRun = false,
                output,
            });
            return ExitCodes.Success;
        }

        if (noAnsi)
        {
            Console.WriteLine($"Rendered: {output}");
            return ExitCodes.Success;
        }

        AnsiConsole.MarkupLine($"[green]Setup splash rendered:[/] {Markup.Escape(output)}");
        return ExitCodes.Success;
    }

    private static string? FindBuiltExe(string repoRoot, string appId)
    {
        try
        {
            string bin = Path.Combine(repoRoot, "bin");
            if (!Directory.Exists(bin) || string.IsNullOrWhiteSpace(appId))
            {
                return null;
            }

            return Directory.GetFiles(bin, appId + ".exe", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

}
