// Command handlers: Test. Shared helpers, types, Main, and the command tree stay in Program.cs.
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
    private static int RunTestParity(string verbosity, string format, bool noAnsi,
        string appRoot, string templateRoot, string[] excludes, string[] renames, string? project)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        try
        {
            ProjectContext ctx = ProjectDiscovery.Discover(project);
            if (string.IsNullOrWhiteSpace(appRoot))
            {
                appRoot = ctx.RepoRoot;
            }

            if (string.IsNullOrWhiteSpace(templateRoot))
            {
                templateRoot = Path.Combine(ctx.RepoRoot, "Templates", "Project");
            }
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (!Directory.Exists(appRoot))
        {
            Console.Error.WriteLine($"Error: App root not found: {appRoot}");
            return ExitCodes.Error;
        }

        if (!Directory.Exists(templateRoot))
        {
            Console.Error.WriteLine($"Error: Template root not found: {templateRoot}");
            return ExitCodes.Error;
        }

        var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string r in renames)
        {
            string[] kv = r.Split('=', 2);
            if (kv.Length != 2 || string.IsNullOrWhiteSpace(kv[0]) || string.IsNullOrWhiteSpace(kv[1]))
            {
                Console.Error.WriteLine($"Error: --rename must be A=B, got '{r}'.");
                return ExitCodes.UsageOrAuthNeeded;
            }

            renameMap[kv[0].Trim()] = kv[1].Trim();
        }

        ParityChecker.Report report = ParityChecker.Compare(
            appRoot, templateRoot,
            ["bin", "obj", ".vs", "TestResults", "Logs", "nupkgs", "Releases", ".icon-backup"],
            excludes, renameMap);

        bool failed = report.MissingInTemplate.Length > 0
            || report.MissingInApp.Length > 0
            || report.Different.Length > 0;

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "test parity",
                passed = !failed,
                compared = report.Compared,
                missingInTemplate = report.MissingInTemplate,
                missingInApp = report.MissingInApp,
                different = report.Different,
            });
            return failed ? ExitCodes.Error : ExitCodes.Success;
        }

        if (noAnsi || verbosity.Trim().ToLowerInvariant() == "quiet")
        {
            Console.WriteLine(failed
                ? $"PARITY FAIL: +{report.MissingInTemplate.Length} app-only, -{report.MissingInApp.Length} template-only, ~{report.Different.Length} different ({report.Compared} compared)."
                : $"PARITY OK: {report.Compared} file(s) compared, trees match.");
            return failed ? ExitCodes.Error : ExitCodes.Success;
        }

        if (!failed)
        {
            AnsiConsole.MarkupLine($"[green]PARITY OK:[/] {report.Compared} file(s) compared, trees match.");
            return ExitCodes.Success;
        }

        AnsiConsole.MarkupLine($"[red]PARITY FAIL[/] ({report.Compared} compared):");
        foreach (string f in report.MissingInTemplate)
        {
            AnsiConsole.MarkupLine($"  [yellow]+[/] {Markup.Escape(f)} (app-only)");
        }

        foreach (string f in report.MissingInApp)
        {
            AnsiConsole.MarkupLine($"  [yellow]-[/] {Markup.Escape(f)} (template-only)");
        }

        foreach (string f in report.Different)
        {
            AnsiConsole.MarkupLine($"  [yellow]~[/] {Markup.Escape(f)} (content differs)");
        }

        return ExitCodes.Error;
    }

    private static int RunTestPipeline(string? project, string verbosity, string format, bool noAnsi, bool skipTests)
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

        string? target = Directory.GetFiles(ctx.RepoRoot, "*.sln", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? Directory.GetFiles(ctx.RepoRoot, "*.slnx", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? ctx.CsprojPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine($"Error: No solution or project found under {ctx.RepoRoot}.");
            return ExitCodes.Error;
        }

        bool isWinUi = false;
        try
        {
            if (ctx.CsprojPath is not null && File.Exists(ctx.CsprojPath))
            {
                isWinUi = File.ReadAllText(ctx.CsprojPath, Encoding.UTF8)
                    .Contains("<UseWinUI>true</UseWinUI>", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Read failure surfaces as build failure below.
        }

        var steps = new List<(string Name, List<string> Args, string? WorkDir, TimeSpan Timeout)>
        {
            ("dotnet build", new List<string> { "build", target, "-c", "Release" }
                .Concat(isWinUi ? ["-p:Platform=x64"] : []).ToList(), ctx.RepoRoot, TimeSpan.FromMinutes(20)),
        };
        if (!skipTests)
        {
            steps.Add(("dotnet test", new List<string> { "test", target, "-c", "Release", "--no-build" }
                .Concat(isWinUi ? ["-p:Platform=x64"] : []).ToList(), ctx.RepoRoot, TimeSpan.FromMinutes(20)));
        }

        IProcessRunner runner = new ProcessRunner();
        var results = new List<(string Name, bool Ok, string Detail)>();
        foreach ((string name, List<string> args, string? workDir, TimeSpan timeout) in steps)
        {
            DevCli.Commands.CliConsole.Rule(name, noAnsi);
            ProcessResult r;
            try
            {
                r = runner.RunAsync("dotnet", args, workDir, timeout).GetAwaiter().GetResult();
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }

            bool ok = r.ExitCode == 0;
            results.Add((name, ok, ok ? "exit 0" : $"exit {r.ExitCode}"));
            if (!ok)
            {
                string tail = (r.StdErr + "\n" + r.StdOut).Trim();
                if (tail.Length > 3000)
                {
                    tail = tail[^3000..];
                }

                Console.Error.WriteLine(tail);
            }
        }

        bool failed = results.Any(r => !r.Ok);
        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "test pipeline",
                target,
                passed = !failed,
                steps = results.Select(r => new { name = r.Name, ok = r.Ok, detail = r.Detail }).ToArray(),
            });
            return failed ? ExitCodes.Error : ExitCodes.Success;
        }

        if (failed)
        {
            DevCli.Commands.CliConsole.Fail("PIPELINE: FAIL", noAnsi);
        }
        else
        {
            DevCli.Commands.CliConsole.Success("PIPELINE: green (build + tests).", noAnsi);
        }

        return failed ? ExitCodes.Error : ExitCodes.Success;
    }

}
