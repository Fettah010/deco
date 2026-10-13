// Command handlers: Msstore. Shared helpers, types, Main, and the command tree stay in Program.cs.
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
    private static int RunMsstoreConfigure(string verbosity, string format, bool noAnsi,
        string tenantId, string sellerId, string clientId, string clientSecret,
        string certThumbprint, string certFilePath, string certPassword, bool verify, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        MsstoreConfig.Credentials creds = MsstoreConfig.Resolve(
            tenantId.Trim(), sellerId.Trim(), clientId.Trim(), clientSecret,
            certThumbprint.Trim(), certFilePath.Trim(), certPassword);

        IProcessRunner runner = new ProcessRunner();
        string msstore = MsstoreConfig.RequireCli(runner);
        if (MsstoreConfig.IsEmpty(creds))
        {
            if (dryRun)
            {
                Console.WriteLine("[dry-run] no credentials supplied; would launch interactive `msstore reconfigure`.");
                return ExitCodes.Success;
            }

            Console.WriteLine("No credentials supplied - launching interactive msstore configuration...");
            try
            {
                RunTool(runner, msstore, ["reconfigure"], null, TimeSpan.FromMinutes(10), "msstore reconfigure");
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }

            return ExitCodes.Success;
        }

        List<string> missing = MsstoreConfig.MissingIdentity(creds);
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("Error: Missing: " + string.Join(", ", missing) + ". Pass as flags or env vars (values never printed).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        List<string> args;
        try
        {
            args = MsstoreConfig.ReconfigureArgs(creds);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        // Lengths only — values (incl. secret) never touch output.
        Console.WriteLine("Configuring msstore (TenantId/SellerId/ClientId echoed as lengths only)...");
        Console.WriteLine($"  TenantId length: {creds.TenantId.Length} ({creds.TenantSource}), SellerId length: {creds.SellerId.Length} ({creds.SellerSource}), ClientId length: {creds.ClientId.Length} ({creds.ClientSource})");

        if (dryRun)
        {
            var display = MsstoreConfig.RedactForDisplay(args, creds);
            Console.WriteLine("[dry-run] msstore " + string.Join(" ", display));
            return ExitCodes.Success;
        }

        try
        {
            RunTool(runner, msstore, args, null, TimeSpan.FromMinutes(5), "msstore reconfigure");
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        Console.WriteLine("msstore configured.");
        if (verify)
        {
            Console.WriteLine("Verifying: msstore apps list ...");
            try
            {
                ProcessResult r = runner.RunAsync(msstore, ["apps", "list"], timeout: TimeSpan.FromMinutes(2))
                    .GetAwaiter().GetResult();
                foreach (string line in (r.StdOut + "\n" + r.StdErr).Replace("\r", "").Split('\n').Take(20))
                {
                    Console.WriteLine(line);
                }

                if (r.ExitCode != 0)
                {
                    Console.Error.WriteLine("Error: Verify failed. The app may lack the Manager role in Partner Center.");
                    return ExitCodes.Error;
                }
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }

            Console.WriteLine("Verify OK.");
        }
        else
        {
            Console.WriteLine("Verify with: msstore apps list");
        }

        return ExitCodes.Success;
    }

    private static int RunMsstorePublish(string? project, string verbosity, string format, bool noAnsi,
        string msixUpload, string appId, string flightId, int rollout, string priceId,
        bool allowMigration, int uploadTimeoutSec, bool noCommit, bool wait,
        bool statusOnly, bool deleteDraft, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            Console.Error.WriteLine("Error: Missing --app-id (Store Product ID, e.g. 9ABCDEF12345).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (rollout is < -1 or > 100)
        {
            Console.Error.WriteLine("Error: --rollout must be 0-100.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (uploadTimeoutSec <= 0)
        {
            uploadTimeoutSec = 1200;
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

        IProcessRunner runner = new ProcessRunner();
        string msstore = MsstoreConfig.RequireCli(runner);
        try
        {
            if (statusOnly)
            {
                Console.WriteLine($"== submission status for {appId} ==");
                if (dryRun)
                {
                    Console.WriteLine("[dry-run] msstore submission status <appId>");
                    return ExitCodes.Success;
                }

                RunTool(runner, msstore, ["submission", "status", appId], null, TimeSpan.FromMinutes(5), "msstore status");
                return ExitCodes.Success;
            }

            if (deleteDraft)
            {
                if (dryRun)
                {
                    Console.WriteLine("[dry-run] msstore submission delete <appId> --no-confirm");
                    return ExitCodes.Success;
                }

                RunTool(runner, msstore, ["submission", "delete", appId, "--no-confirm"], null, TimeSpan.FromMinutes(5), "msstore delete");
                Console.WriteLine("Pending draft deleted.");
                return ExitCodes.Success;
            }

            if (string.IsNullOrWhiteSpace(msixUpload))
            {
                string msixDir = ctx.MsixDir;
                if (Directory.Exists(msixDir))
                {
                    msixUpload = Directory.GetFiles(msixDir, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".msixupload", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                        .FirstOrDefault() ?? string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(msixUpload) || !File.Exists(msixUpload))
            {
                Console.Error.WriteLine("Error: No package found. Build one (deco build msix --store-upload) or pass --msixupload.");
                return ExitCodes.Error;
            }

            DevCli.Commands.CliConsole.Info($"Package: {msixUpload}", noAnsi);
            DevCli.Commands.CliConsole.Info($"AppId:   {appId}", noAnsi);

            // Pricing pre-check (fail fast before upload + draft churn).
            try
            {
                ProcessResult get = runner.RunAsync(msstore, ["submission", "get", appId, "--output-stream", "stderr"], null, TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
                string raw = get.StdOut + "\n" + get.StdErr;
                int openAt = raw.IndexOf('{');
                int closeAt = raw.LastIndexOf('}');
                if (get.ExitCode == 0 && openAt >= 0 && closeAt > openAt)
                {
                    using var sub = System.Text.Json.JsonDocument.Parse(raw.Substring(openAt, closeAt - openAt + 1));
                    string storePriceId = sub.RootElement.TryGetProperty("Pricing", out var pricing)
                        && pricing.TryGetProperty("PriceId", out var pid) ? pid.GetString() ?? string.Empty : string.Empty;
                    bool advanced = sub.RootElement.TryGetProperty("Pricing", out var pricing2)
                        && pricing2.TryGetProperty("IsAdvancedPricingModel", out var adv)
                        && adv.ValueKind == System.Text.Json.JsonValueKind.True;
                    if (advanced)
                    {
                        if (allowMigration && System.Text.RegularExpressions.Regex.IsMatch(priceId, "^Tier\\d+$"))
                        {
                            DevCli.Commands.CliConsole.Warn($"product uses advanced (MSRP) pricing - proceeding with --price-id {priceId} MIGRATES it to legacy tier pricing.", noAnsi);
                        }
                        else
                        {
                            Console.Error.WriteLine("Error: Product uses advanced (MSRP) pricing (PriceId 'Base'). No API write can preserve it - use Partner Center. See docs/RELEASE.md.");
                            Console.Error.WriteLine("NEVER guess a tier and NEVER assume Free: confirm the exact tier with the owner first (per-market detail needs Partner Center).");
                            return ExitCodes.Error;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(storePriceId) && !System.Text.RegularExpressions.Regex.IsMatch(storePriceId, "^(Free|NotAvailable|Tier\\d+)$"))
                    {
                        if (!string.IsNullOrWhiteSpace(priceId) && System.Text.RegularExpressions.Regex.IsMatch(priceId, "^Tier\\d+$"))
                        {
                            DevCli.Commands.CliConsole.Warn($"Store base price is '{storePriceId}' (per-market). Proceeding with explicit --price-id {priceId}.", noAnsi);
                        }
                        else
                        {
                            Console.Error.WriteLine($"Error: Store base price is '{storePriceId}' (per-market pricing). msstore cannot write submissions for this product - publish from Partner Center instead.");
                            Console.Error.WriteLine("NEVER guess a tier and NEVER assume Free: confirm the exact tier with the owner first.");
                            return ExitCodes.Error;
                        }
                    }
                }
            }
            catch (DecoException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DevCli.Commands.CliConsole.Warn($"could not verify pricing upfront ({ex.Message}); continuing - the CLI guards pricing itself.", noAnsi);
            }

            var pubArgs = new List<string> { "publish", msixUpload, "--appId", appId, "--uploadTimeout", uploadTimeoutSec.ToString() };
            if (noCommit)
            {
                pubArgs.Add("--noCommit");
            }

            if (!string.IsNullOrWhiteSpace(flightId))
            {
                pubArgs.AddRange(["--flightId", flightId]);
            }

            if (rollout >= 0)
            {
                pubArgs.AddRange(["--packageRolloutPercentage", rollout.ToString()]);
            }

            if (!string.IsNullOrWhiteSpace(priceId))
            {
                pubArgs.AddRange(["--priceId", priceId]);
            }

            if (dryRun)
            {
                Console.WriteLine("[dry-run] msstore " + string.Join(" ", pubArgs));
                return ExitCodes.Success;
            }

            RunTool(runner, msstore, pubArgs, null, TimeSpan.FromMinutes(uploadTimeoutSec + 10), "msstore publish");
            if (noCommit)
            {
                DevCli.Commands.CliConsole.Info("Publish call done (draft kept uncommitted -- inspect in Partner Center).", noAnsi);
            }
            else
            {
                DevCli.Commands.CliConsole.Success("Publish call done (submitted for certification).", noAnsi);
            }

            if (wait && !noCommit)
            {
                DevCli.Commands.CliConsole.Info("Polling certification status (Ctrl+C to stop, submission keeps running)...", noAnsi);
                RunTool(runner, msstore, ["submission", "poll", appId], null, Timeout.InfiniteTimeSpan, "msstore poll");
            }
            else if (wait)
            {
                Console.WriteLine("NOTE: --wait has nothing to poll on a --no-commit draft. Commit with: msstore submission publish <appId>");
            }
            else
            {
                DevCli.Commands.CliConsole.Info("Track it: msstore submission status <appId>  |  commit a draft: msstore submission publish <appId>", noAnsi);
            }

            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
    }

}
