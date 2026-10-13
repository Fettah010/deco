// Command handlers: Version. Shared helpers, types, Main, and the command tree stay in Program.cs.
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
    private static int RunVersionBump(string? project, string verbosity, string format, bool noAnsi,
        string newVersion, string date, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        try
        {
            VersionBumpService.Validate(newVersion);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(date))
        {
            date = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

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

        List<string> csprojs = FindWinExeProjects(ctx.RepoRoot);
        if (csprojs.Count == 0)
        {
            Console.Error.WriteLine($"Error: No WinExe csproj found under {ctx.RepoRoot}.");
            return ExitCodes.Error;
        }

        var touchedFiles = new List<string>();
        try
        {
            var noBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            foreach (string path in csprojs)
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                (string next, List<string> touched) = VersionBumpService.UpdateCsprojText(text, newVersion.Trim());
                if (!dryRun)
                {
                    File.WriteAllText(path, next, noBom);
                }

                touchedFiles.Add($"{path} [{string.Join(",", touched)}]");
            }

            string buildInfo = Path.Combine(ctx.RepoRoot, "Services", "BuildInfo.cs");
            if (File.Exists(buildInfo))
            {
                string commit = TryGitShortSha(ctx.RepoRoot) ?? "unknown";
                string stamped = VersionBumpService.StampBuildInfoCommit(
                    File.ReadAllText(buildInfo, Encoding.UTF8), commit);
                if (!dryRun)
                {
                    File.WriteAllText(buildInfo, stamped, noBom);
                }

                touchedFiles.Add($"{buildInfo} [Commit={commit}]");
            }

            string citation = Path.Combine(ctx.RepoRoot, "CITATION.cff");
            if (File.Exists(citation))
            {
                string next = VersionBumpService.UpdateCitationText(
                    File.ReadAllText(citation, Encoding.UTF8), newVersion.Trim(), date);
                if (!dryRun)
                {
                    File.WriteAllText(citation, next, noBom);
                }

                touchedFiles.Add($"{citation} [version,date-released]");
            }

            string changelog = Path.Combine(ctx.RepoRoot, "CHANGELOG.md");
            if (File.Exists(changelog))
            {
                (string next, bool inserted) = VersionBumpService.InsertChangelogStub(
                    File.ReadAllText(changelog, Encoding.UTF8), newVersion.Trim(), date);
                if (inserted && !dryRun)
                {
                    File.WriteAllText(changelog, next, noBom);
                }

                touchedFiles.Add($"{changelog} [{(inserted ? "stub inserted" : "already present")}]");
            }
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
                command = "version bump",
                dryRun,
                newVersion = newVersion.Trim(),
                files = touchedFiles,
            });
            return ExitCodes.Success;
        }

        if (verbosity.Trim().ToLowerInvariant() == "quiet")
        {
            Console.WriteLine(newVersion.Trim());
            return ExitCodes.Success;
        }

        string prefix = dryRun ? "[dry-run] would touch" : "Touched";
        if (noAnsi)
        {
            Console.WriteLine($"{prefix} {touchedFiles.Count} file(s) -> {newVersion.Trim()}:");
            foreach (string f in touchedFiles)
            {
                Console.WriteLine("  " + f);
            }

            return ExitCodes.Success;
        }

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(prefix)} {touchedFiles.Count} file(s) -> {Markup.Escape(newVersion.Trim())}:[/]");
        foreach (string f in touchedFiles)
        {
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(f)}[/]");
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine("[dim]Review, then re-run without --dry-run. Afterwards: fill the CHANGELOG stub, commit, tag.[/]");
        }

        return ExitCodes.Success;
    }

    private static int RunVersionWeight(string? project, string verbosity, string format, bool noAnsi,
        bool failOnJump, int timeoutSec)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        if (timeoutSec < 0)
        {
            timeoutSec = 0;
        }

        TimeSpan timeout = timeoutSec == 0 ? TimeSpan.FromSeconds(600) : TimeSpan.FromSeconds(timeoutSec);

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

        if (string.IsNullOrWhiteSpace(ctx.CsprojPath))
        {
            Console.Error.WriteLine($"Error: No .csproj found under {ctx.RepoRoot}.");
            return ExitCodes.Error;
        }

        string outDir = Path.Combine(Path.GetTempPath(), "deco-weight-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool isWinUi = File.ReadAllText(ctx.CsprojPath, Encoding.UTF8)
                .Contains("<UseWinUI>true</UseWinUI>", StringComparison.OrdinalIgnoreCase);
            var publishArgs = new List<string>
            {
                "publish", ctx.CsprojPath, "-c", "Release",
            };
            if (isWinUi)
            {
                publishArgs.AddRange(["-p:Platform=x64"]);
            }

            publishArgs.AddRange(["-r", "win-x64", "--self-contained", "-o", outDir]);

            IProcessRunner runner = new ProcessRunner();
            ProcessResult pub;
            try
            {
                pub = runner.RunAsync("dotnet", publishArgs, ctx.RepoRoot, timeout).GetAwaiter().GetResult();
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }

            if (pub.ExitCode != 0)
            {
                Console.Error.WriteLine($"Error: dotnet publish failed (exit {pub.ExitCode}).");
                string tail = (pub.StdErr + "\n" + pub.StdOut).Trim();
                if (tail.Length > 2000)
                {
                    tail = tail[^2000..];
                }

                Console.Error.WriteLine(tail);
                return ExitCodes.Error;
            }

            long bytes = WeightCheck.SumDirectoryBytes(outDir);
            long? baselineBytes = null;
            string baselinePath = Path.Combine(ctx.RepoRoot, "docs", "publish-weight-baseline.json");
            if (File.Exists(baselinePath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(baselinePath, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("bytes", out JsonElement el) && el.TryGetInt64(out long b))
                    {
                        baselineBytes = b;
                    }
                }
                catch
                {
                    // Unparseable baseline: treat as missing (warn path below).
                }
            }

            WeightCheck.Evaluation eval = WeightCheck.Evaluate(bytes, baselineBytes);

            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "version weight",
                    bytes,
                    mb = eval.Mb,
                    baselineBytes = eval.BaselineBytes,
                    baselineMb = eval.BaselineMb,
                    deltaPct = eval.DeltaPct,
                    jumped = eval.Jumped,
                    failed = eval.Jumped && failOnJump,
                });
                return eval.Jumped && failOnJump ? ExitCodes.Error : ExitCodes.Success;
            }

            if (noAnsi || verbosity.Trim().ToLowerInvariant() == "quiet")
            {
                Console.WriteLine($"Publish weight: {eval.Mb} MB ({bytes} bytes).");
                if (eval.BaselineBytes is null)
                {
                    Console.WriteLine("No baseline at docs/publish-weight-baseline.json.");
                }
                else
                {
                    Console.WriteLine($"Baseline: {eval.BaselineMb} MB; delta: {eval.DeltaPct}%.");
                }

                if (eval.Jumped)
                {
                    if (failOnJump)
                    {
                        DevCli.Commands.CliConsole.Fail("weight jumped >10% over baseline.", noAnsi);
                    }
                    else
                    {
                        DevCli.Commands.CliConsole.Warn("weight jumped >10% over baseline.", noAnsi);
                    }
                }

                return eval.Jumped && failOnJump ? ExitCodes.Error : ExitCodes.Success;
            }

            AnsiConsole.MarkupLine($"[green]Publish weight: {eval.Mb} MB ({bytes} bytes).[/]");
            if (eval.BaselineBytes is null)
            {
                AnsiConsole.MarkupLine("[yellow]No baseline at docs/publish-weight-baseline.json; recording current as a hint (not writing):[/]");
                AnsiConsole.MarkupLine($"[dim]{{ \"bytes\": {bytes}, \"mb\": {eval.Mb} }}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"Baseline: {eval.BaselineMb} MB; delta: {eval.DeltaPct}%.");
            }

            if (eval.Jumped)
            {
                string msg = $"Publish weight jumped {eval.DeltaPct}% over baseline (>10% guardrail).";
                if (failOnJump)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(msg)}[/]");
                    return ExitCodes.Error;
                }

                AnsiConsole.MarkupLine($"[yellow]WARNING: {Markup.Escape(msg)}[/]");
            }

            return ExitCodes.Success;
        }
        finally
        {
            try
            {
                Directory.Delete(outDir, recursive: true);
            }
            catch
            {
                // Temp cleanup is best effort.
            }
        }
    }

    private static List<string> FindWinExeProjects(string repoRoot)
    {
        var roots = new List<string> { repoRoot };
        string nested = Path.Combine(repoRoot, "Templates", "Project");
        if (Directory.Exists(nested))
        {
            roots.Add(nested);
        }

        var found = new List<string>();
        foreach (string root in roots)
        {
            string[] csprojs;
            try
            {
                csprojs = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            Array.Sort(csprojs, StringComparer.OrdinalIgnoreCase);
            foreach (string csproj in csprojs)
            {
                try
                {
                    if (VersionBumpService.IsWinExeCsproj(File.ReadAllText(csproj, Encoding.UTF8)))
                    {
                        found.Add(csproj);
                    }
                }
                catch
                {
                    // Unreadable file: skip, like the ps1 does.
                }
            }
        }

        return found;
    }

    private static string? TryGitShortSha(string repoRoot)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

}
