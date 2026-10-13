// Command handlers: Wack. Shared helpers, types, Main, and the command tree stay in Program.cs.
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
    private static int RelaunchElevatedWack(string package, string report, int timeout, string? project)
    {
        string self = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(self))
        {
            Console.Error.WriteLine("Error: Cannot determine own executable for elevation.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        static string Q(string s) => "\"" + s.Replace("\"", "", StringComparison.Ordinal) + "\"";
        string wackArgs = $"wack --package {Q(package)} --report {Q(report)} --timeout-sec {timeout}";
        if (!string.IsNullOrWhiteSpace(project))
        {
            wackArgs += $" --project {Q(project)}";
        }

        string fileName = self;
        string arguments = wackArgs;
        if (self.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "dotnet";
            arguments = $"{Q(self)} {wackArgs}";
        }

        Console.WriteLine("Re-launching elevated for the WACK run (UAC prompt)...");
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    Verb = "runas",
                    UseShellExecute = true,
                },
            };
            proc.Start();
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Elevation was declined or failed: {ex.Message}");
            return ExitCodes.UsageOrAuthNeeded;
        }
    }

    private static int RunWack(string? project, string verbosity, string format, bool noAnsi,
        string package, string report, bool elevate, bool parseOnly, int timeoutSec, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        if (timeoutSec <= 0)
        {
            timeoutSec = 1500;
        }

        try
        {
            WackRunner.ValidatePackage(package);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(report))
        {
            report = WackRunner.DefaultReportPath(package);
        }

        string? appcert = WackRunner.FindAppCert();
        if (appcert is null)
        {
            if (dryRun)
            {
                Console.WriteLine("[dry-run] inputs valid; WACK kit not found on this machine (exit 2 at runtime).");
                return ExitCodes.Success;
            }

            Console.Error.WriteLine("Error: WACK kit (appcert.exe) not found. Install the Windows App Certification Kit.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (dryRun)
        {
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "wack",
                    dryRun = true,
                    package,
                    report,
                    appcert,
                });
                return ExitCodes.Success;
            }

            Console.WriteLine($"[dry-run] would run appcert on {package} -> {report} (kit: {appcert}).");
            return ExitCodes.Success;
        }

        if (!IsCurrentUserAdmin() && !parseOnly)
        {
            if (!elevate)
            {
                Console.Error.WriteLine("Error: WACK needs an elevated shell. Re-run with --elevate (one UAC click) or from an admin shell.");
                return ExitCodes.UsageOrAuthNeeded;
            }

            return RelaunchElevatedWack(package, report, timeoutSec, project);
        }

        IProcessRunner runner = new ProcessRunner();
        if (parseOnly && File.Exists(report))
        {
            Console.WriteLine("Parse-only: skipping the appcert run, gating on the existing report.");
        }
        else
        {
            Console.WriteLine("Running WACK (can take 10-20 minutes)...");
            try
            {
                RunTool(runner, appcert, WackRunner.AppCertArgs(package, report),
                    null, TimeSpan.FromSeconds(timeoutSec), "appcert");
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }
        }

        if (!File.Exists(report))
        {
            Console.Error.WriteLine($"Error: WACK produced no report at {report}.");
            return ExitCodes.Error;
        }

        Console.WriteLine($"Report: {report}");
        WackRunner.Verdict verdict = WackRunner.ParseReport(File.ReadAllText(report, Encoding.UTF8));
        Console.WriteLine($"OVERALL_RESULT: {verdict.Overall}");
        foreach (WackRunner.Failure f in verdict.Failures)
        {
            Console.WriteLine("FAIL: " + f.Test);
            foreach (string m in f.Messages)
            {
                Console.WriteLine("  " + m);
            }
        }

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "wack",
                dryRun = false,
                overall = verdict.Overall,
                failures = verdict.Failures.Select(f => new { test = f.Test, messages = f.Messages }).ToArray(),
                report,
            });
        }

        if (string.Equals(verdict.Overall, "PASS", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"WACK PASSED overall ({verdict.Failures.Count} failing sub-test(s), review above).");
            return ExitCodes.Success;
        }

        Console.Error.WriteLine($"WACK did not pass overall: {verdict.Overall}.");
        return ExitCodes.Error;
    }

}
