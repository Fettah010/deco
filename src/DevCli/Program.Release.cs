// Command handlers: Release. Shared helpers, types, Main, and the command tree stay in Program.cs.
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
    private static int RunReleaseStore(string? project, string verbosity, string format, bool noAnsi,
        string publisher, string appId, string tenantId, string clientId, string clientSecret,
        string publishMode, string submitter, string descriptionFile, string shotsDir, string releaseNotes,
        string certNotes, string packageDir, bool skipTests, bool skipBuild, bool requireShots,
        bool skipShots, bool skipWack, bool certifiedConfirmed, bool rehearse, bool listingOnly,
        bool skipSubmit, bool dryRun, bool resume, bool allowDirty, bool cleanupOnFail,
        int buildTimeoutSec, int pollTimeoutSec, int wackTimeoutSec, string[] stageFilter, bool listStages)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (listStages)
        {
            Console.WriteLine("Release stages:");
            foreach (string s in ReleaseStageNames)
            {
                Console.WriteLine("  " + s);
            }

            return ExitCodes.Success;
        }

        List<int> stages;
        try
        {
            stages = ReleaseStages.Select(stageFilter);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (publishMode != "Immediate" && publishMode != "Manual")
        {
            Console.Error.WriteLine($"Error: --publish-mode must be Immediate|Manual, got '{publishMode}'.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (submitter != "Api" && submitter != "Portal")
        {
            Console.Error.WriteLine($"Error: --submitter must be Api|Portal, got '{submitter}'.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (buildTimeoutSec <= 0)
        {
            buildTimeoutSec = 1800;
        }

        if (pollTimeoutSec <= 0)
        {
            pollTimeoutSec = 1800;
        }

        if (wackTimeoutSec <= 0)
        {
            wackTimeoutSec = 1200;
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

        if (string.IsNullOrWhiteSpace(ctx.CsprojPath))
        {
            Console.Error.WriteLine($"Error: No .csproj found under {ctx.RepoRoot}.");
            return ExitCodes.Error;
        }

        string version = ctx.Version ?? string.Empty;
        string listingVersion = ctx.InformationalVersion ?? version;
        if (string.IsNullOrWhiteSpace(version))
        {
            Console.Error.WriteLine("Error: No <Version> in the csproj.");
            return ExitCodes.Error;
        }

        string versionQuad;
        try
        {
            versionQuad = VersionQuad.ToMsixQuad(version);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        appId = StoreCred(appId, "DEVCLOUD_STORE_APP_ID", "DECO_STORE_APP_ID", "DEVTEM_STORE_APP_ID");
        if (string.IsNullOrWhiteSpace(appId))
        {
            appId = ctx.StoreAppId ?? string.Empty;
        }

        string msixDir = ctx.MsixDir;
        string stateFile = Path.Combine(msixDir, $"release-state-{version}.json");
        var resumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (resume && !dryRun && File.Exists(stateFile))
        {
            try
            {
                using var state = StoreJson.Load(stateFile, "Release state");
                if (state.RootElement.TryGetProperty("stagesDone", out var done))
                {
                    foreach (var s in done.EnumerateArray())
                    {
                        if (s.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            resumed.Add(s.GetString() ?? string.Empty);
                        }
                    }
                }
            }
            catch
            {
                // Unreadable state: run everything.
            }
        }

        var timings = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        System.Diagnostics.Stopwatch? stageSw = null;
        string stageCurrent = string.Empty;
        void Stage(string name)
        {
            if (stageSw is not null && stageCurrent != string.Empty)
            {
                timings[stageCurrent] = Math.Round(stageSw.Elapsed.TotalSeconds, 1);
            }

            Console.WriteLine(string.Empty);
            DevCli.Commands.CliConsole.Rule(name, noAnsi);
            stageCurrent = name;
            stageSw = System.Diagnostics.Stopwatch.StartNew();
        }

        void Gate(string name, bool ok, string detail)
        {
            if (ok)
            {
                DevCli.Commands.CliConsole.Success($"{name} -- {detail}", noAnsi);
            }
            else
            {
                throw new DecoException($"GATE FAILED: {name} -- {detail}", "release-gate");
            }
        }

        int RunChild(string[] args, TimeSpan timeout, string? logPath)
        {
            if (dryRun)
            {
                Console.WriteLine("DRYRUN would run: deco " + string.Join(" ", args));
                return 0;
            }

            return RunDecoChild(args, ctx.RepoRoot, timeout, logPath);
        }

        void SaveCheckpoint(string stage)
        {
            if (dryRun)
            {
                return;
            }

            try
            {
                var done = resumed.ToList();
                if (!done.Contains(stage, StringComparer.OrdinalIgnoreCase))
                {
                    done.Add(stage);
                }

                Directory.CreateDirectory(msixDir);
                File.WriteAllText(stateFile,
                    StoreJson.CanonicalWriter.Write(w =>
                    {
                        w.BeginObject();
                        w.PropertyName("version"); w.Value(version);
                        w.PropertyName("stagesDone");
                        w.BeginArray();
                        foreach (string s in done)
                        {
                            w.Value(s);
                        }

                        w.EndArray();
                        w.PropertyName("status"); w.Value("in-progress");
                        w.EndObject();
                    }), Encoding.UTF8);
            }
            catch
            {
                // Checkpoint failure never fails the release.
            }
        }

        string bundleName = $"{ctx.AppId}_{versionQuad}_multi.msixbundle";
        string uploadName = $"{ctx.AppId}_{versionQuad}_multi.msixupload";
        string multiBundle = Path.Combine(msixDir, bundleName);
        string multiUpload = Path.Combine(msixDir, uploadName);

        try
        {
            // --- Stage 0: Preflight ---
            if (stages.Contains(0))
            {
                Stage("0. Preflight");
                string manifestSrc = Path.Combine(ctx.RepoRoot, "Packaging", "Msix", "Package.appxmanifest");
                Gate("Manifest exists", File.Exists(manifestSrc), manifestSrc);
                (string? idName, _) = MsixPackaging.ReadIdentity(File.ReadAllText(manifestSrc, Encoding.UTF8));
                Gate("Manifest identity", !string.IsNullOrWhiteSpace(idName), idName ?? "(empty)");
                Gate("Publisher", !string.IsNullOrWhiteSpace(publisher) && publisher.Trim() != "CN=YourPublisher",
                    string.IsNullOrWhiteSpace(publisher) ? "missing (pass --publisher)" : "set");
                Gate("Version", true, $"csproj Version {version}");
                Gate("Version quad", System.Text.RegularExpressions.Regex.IsMatch(versionQuad, @"^\d+\.\d+\.\d+\.\d+$"), versionQuad);

                string prev = ReadCsprojProp(ctx.CsprojPath, "ReleasePreviousVersion");
                Gate("Version progression declared", !string.IsNullOrWhiteSpace(prev),
                    string.IsNullOrWhiteSpace(prev) ? "csproj must declare ReleasePreviousVersion" : $"{prev} -> {versionQuad}");
                if (!string.IsNullOrWhiteSpace(prev))
                {
                    Gate("Version progression", new Version(versionQuad) > new Version(VersionQuad.ToMsixQuad(prev)), $"{prev} -> {versionQuad}");
                }

                foreach (string loc in ctx.Locales)
                {
                    string np = Path.Combine(ctx.ListingSourceDir, "release-notes", listingVersion, loc + ".json");
                    Gate($"Release notes {loc}", File.Exists(np), np);
                }

                if (string.IsNullOrWhiteSpace(descriptionFile))
                {
                    descriptionFile = Path.Combine(ctx.RepoRoot, "Store", "Listing", "description.txt");
                }

                Gate("Description file", File.Exists(descriptionFile), descriptionFile);
                string descriptionText = File.ReadAllText(descriptionFile, Encoding.UTF8).Trim();
                Gate("Description length", descriptionText.Length <= 10000, $"{descriptionText.Length} chars");
                if (descriptionText.Contains("certified", StringComparison.OrdinalIgnoreCase) && !certifiedConfirmed)
                {
                    throw new DecoException("GATE FAILED: Certified wording -- description mentions 'certified' but --certified-confirmed not passed.", "release-gate");
                }

                foreach (string loc in ctx.Locales)
                {
                    string lp = Path.Combine(ctx.ListingSourceDir, loc, "listing.json");
                    if (File.Exists(lp) && File.ReadAllText(lp, Encoding.UTF8).Contains("certified", StringComparison.OrdinalIgnoreCase) && !certifiedConfirmed)
                    {
                        throw new DecoException($"GATE FAILED: Certified wording -- {loc} listing mentions 'certified' but --certified-confirmed not passed.", "release-gate");
                    }
                }

                DevCli.Commands.CliConsole.Success($"Certified wording gate -- flag state: {certifiedConfirmed}.", noAnsi);

                try
                {
                    IProcessRunner grunner = new ProcessRunner();
                    ProcessResult status = grunner.RunAsync("git", ["status", "--porcelain=v1"], ctx.RepoRoot, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    var trackedDirty = status.StdOut.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => !l.StartsWith("??", StringComparison.Ordinal)).ToList();
                    if (trackedDirty.Count > 0 && !allowDirty && !dryRun)
                    {
                        throw new DecoException($"GATE FAILED: Tree -- {trackedDirty.Count} tracked file(s) modified. Commit first or re-run with --allow-dirty.", "release-gate");
                    }

                    if (trackedDirty.Count > 0)
                    {
                        DevCli.Commands.CliConsole.Warn($"tree has {trackedDirty.Count} modified tracked file(s).", noAnsi);
                    }
                }
                catch (DecoException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    DevCli.Commands.CliConsole.Warn($"git status check skipped ({ex.Message}).", noAnsi);
                }
            }

            // --- Stage 1: Unit tests ---
            string unitStatus = "passed";
            string unitReason = string.Empty;
            if (stages.Contains(1))
            {
                Stage("1. Unit tests");
                if ((resume && resumed.Contains("tests")) || skipTests)
                {
                    unitStatus = "skipped";
                    unitReason = skipTests ? "Explicitly skipped with --skip-tests." : "Resumed from checkpoint.";
                    Console.WriteLine(resume && resumed.Contains("tests") && !skipTests
                        ? "Resumed: skipping unit tests (done before)." : $"Skipped: {unitReason}");
                }
                else
                {
                    int code = RunChild(["test", "pipeline"], TimeSpan.FromMinutes(30), null);
                    if (code != 0)
                    {
                        throw new DecoException($"Unit tests failed (exit {code}).", "release-tests");
                    }

                    SaveCheckpoint("tests");
                }
            }

            // --- Stage 2: Packages ---
            string packageStatus = "passed";
            string packageReason = string.Empty;
            if (stages.Contains(2))
            {
                Stage("2. Packages (x64 + ARM64 + multi-arch bundle)");
                bool haveBoth = File.Exists(multiBundle) && File.Exists(multiUpload);
                if ((resume && resumed.Contains("packages")) || skipBuild)
                {
                    if (!haveBoth)
                    {
                        throw new DecoException($"GATE FAILED: Packages -- {bundleName} or {uploadName} missing (rebuild without --skip-build).", "release-gate");
                    }

                    packageStatus = "reused";
                    packageReason = skipBuild ? "Existing package artifacts were reused with --skip-build." : "Resumed from checkpoint.";
                    Console.WriteLine($"Skipped: {packageReason}");
                }
                else
                {
                    foreach (string d in new[] { Path.Combine(ctx.RepoRoot, "bin"), Path.Combine(ctx.RepoRoot, "obj") })
                    {
                        if (!dryRun && Directory.Exists(d))
                        {
                            Directory.Delete(d, recursive: true);
                        }
                    }

                    foreach (string stale in Directory.Exists(msixDir)
                        ? Directory.GetFiles(msixDir, $"{ctx.AppId}_{version}*")
                        : [])
                    {
                        if (!dryRun)
                        {
                            File.Delete(stale);
                        }
                    }

                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    foreach (string plat in new[] { "x64", "arm64" })
                    {
                        Console.WriteLine($"-- build msix --store-upload {plat} (sequential, {buildTimeoutSec}s timeout)");
                        int code = RunChild(
                            ["build", "msix", "--store-upload", "--publisher", publisher, "--platform", plat, "--version", version],
                            TimeSpan.FromSeconds(buildTimeoutSec),
                            Path.Combine(msixDir, $"build-{plat}-{stamp}.log"));
                        if (code != 0)
                        {
                            throw new DecoException($"{plat} StoreUpload build failed (exit {code}).", "release-build");
                        }
                    }

                    if (!dryRun)
                    {
                        string? makeappx = SdkTools.Find("makeappx");
                        if (makeappx is null)
                        {
                            throw new DecoException("makeappx.exe not found (Windows SDK required).", "release-build");
                        }

                        string tmp = Path.Combine(Path.GetTempPath(), "msix-multi-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tmp);
                        try
                        {
                            foreach (string arch in new[] { "x64", "arm64" })
                            {
                                string? single = Directory.GetFiles(msixDir, $"{ctx.AppId}_{versionQuad}_{arch}.msix")
                                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
                                if (single is null)
                                {
                                    throw new DecoException($"Missing {arch} package for {versionQuad}.", "release-build");
                                }

                                File.Copy(single, Path.Combine(tmp, Path.GetFileName(single)), overwrite: true);
                            }

                            IProcessRunner runner = new ProcessRunner();
                            RunTool(runner, makeappx, MsixPackaging.MakeAppxBundleArgs(tmp, multiBundle),
                                null, TimeSpan.FromMinutes(10), "makeappx bundle");
                        }
                        finally
                        {
                            try
                            {
                                Directory.Delete(tmp, recursive: true);
                            }
                            catch
                            {
                            }
                        }

                        DevCli.Commands.CliConsole.Success($"Bundled: {multiBundle}", noAnsi);
                        if (File.Exists(multiUpload))
                        {
                            File.Delete(multiUpload);
                        }

                        string zipStage = Path.Combine(Path.GetTempPath(), "msix-upload-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(zipStage);
                        try
                        {
                            File.Copy(multiBundle, Path.Combine(zipStage, bundleName), overwrite: true);
                            string zipTemp = Path.Combine(Path.GetTempPath(), "msix-upload-" + Guid.NewGuid().ToString("N") + ".zip");
                            if (File.Exists(zipTemp))
                            {
                                File.Delete(zipTemp);
                            }

                            System.IO.Compression.ZipFile.CreateFromDirectory(zipStage, zipTemp);
                            File.Move(zipTemp, multiUpload);
                        }
                        finally
                        {
                            try
                            {
                                Directory.Delete(zipStage, recursive: true);
                            }
                            catch
                            {
                            }
                        }

                        DevCli.Commands.CliConsole.Success($"Wrapped: {multiUpload}", noAnsi);
                    }
                    else
                    {
                        Console.WriteLine($"DRYRUN would run: makeappx bundle {bundleName} + wrap {uploadName}");
                    }

                    SaveCheckpoint("packages");
                }
            }

            // --- Stage 3: Screenshots ---
            if (stages.Contains(3))
            {
                Stage("3. Screenshots");
                if (string.IsNullOrWhiteSpace(shotsDir))
                {
                    shotsDir = Path.Combine(ctx.RepoRoot, "Releases", "Shots", version);
                }
                else if (!Path.IsPathRooted(shotsDir))
                {
                    shotsDir = Path.Combine(ctx.RepoRoot, shotsDir);
                }

                int shotCount = Directory.Exists(shotsDir)
                    ? Directory.GetFiles(shotsDir, "*.png").Length : 0;
                Gate("Screenshots", !requireShots || shotCount > 0, $"{shotCount} shot(s) in {shotsDir}.");
                if (shotCount == 0)
                {
                    DevCli.Commands.CliConsole.Warn("no screenshots -- listing images will be cloned from the last submission.", noAnsi);
                }
            }

            // --- Stage 4: WACK ---
            string wackStatus = "not-run";
            string wackReason = string.Empty;
            if (stages.Contains(4))
            {
                Stage("4. Windows App Certification Kit");
                if ((resume && resumed.Contains("wack")) || skipWack)
                {
                    if (resume && resumed.Contains("wack") && !skipWack)
                    {
                        wackStatus = "passed";
                        wackReason = "Passed in the checkpointed earlier stage.";
                        Console.WriteLine("Resumed (-Resume): skipping WACK (passed before).");
                    }
                    else
                    {
                        wackStatus = "skipped";
                        wackReason = "Explicitly skipped with --skip-wack.";
                        Console.WriteLine("Skipped (-SkipWack) -- WACK stays an owner-side gate.");
                    }
                }
                else if (WackRunner.FindAppCert() is null)
                {
                    throw new DecoException("GATE FAILED: WACK -- appcert.exe not found and --skip-wack not passed.", "release-gate");
                }
                else
                {
                    int code = RunChild(
                        ["wack", "--package", multiBundle, "--elevate", "--timeout-sec", wackTimeoutSec.ToString()],
                        TimeSpan.FromSeconds(wackTimeoutSec + 300), null);
                    if (code == 2)
                    {
                        throw new DecoException("WACK needs elevation and --elevate was declined at the UAC prompt.", "release-wack");
                    }

                    if (code != 0)
                    {
                        throw new DecoException($"WACK gate failed (exit {code}).", "release-wack");
                    }

                    wackStatus = "passed";
                    SaveCheckpoint("wack");
                }
            }

            // --- Stage 5: Submission ---
            string submissionState = "not-submitted";
            string? submissionId = null;
            string? submissionStatus = null;
            if (stages.Contains(5))
            {
                Stage($"5. Partner Center submission ({submitter})");
                if (skipSubmit)
                {
                    submissionState = "skipped";
                    Console.WriteLine($"Skipped (--skip-submit). Upload ready: {multiUpload}");
                }
                else if (submitter == "Portal")
                {
                    var portalArgs = new List<string>
                    {
                        "store", "portal", "--upload", multiUpload,
                        "--description", descriptionFile,
                        "--store-id", appId, "--publish-mode", publishMode,
                        "--auto-confirm", "--version", listingVersion,
                    };
                    if (!string.IsNullOrWhiteSpace(shotsDir) && Directory.Exists(shotsDir))
                    {
                        portalArgs.AddRange(["--shots", shotsDir]);
                    }

                    if (!string.IsNullOrWhiteSpace(releaseNotes))
                    {
                        portalArgs.AddRange(["--release-notes", releaseNotes]);
                    }

                    if (certifiedConfirmed)
                    {
                        portalArgs.Add("--certified-confirmed");
                    }

                    if (skipShots)
                    {
                        portalArgs.Add("--skip-shots");
                    }

                    portalArgs.AddRange(["--progress-json",
                        Path.Combine(msixDir, $"portal-progress-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl")]);
                    DevCli.Commands.CliConsole.Info("-- portal submitter (checkpointed browser automation)", noAnsi);
                    int portalCode = RunChild(portalArgs.ToArray(),
                        TimeSpan.FromMinutes(pollTimeoutSec + 30),
                        Path.Combine(msixDir, $"portal-run-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
                    if (portalCode == 2)
                    {
                        throw new DecoException(
                            "Portal submitter needs a login it could not complete (exit 2: non-interactive run, or the assist window timed out). Re-run interactively and sign in when the login window appears.",
                            "release-portal");
                    }

                    if (portalCode != 0)
                    {
                        throw new DecoException($"Portal submitter failed (exit {portalCode}).", "release-portal");
                    }

                    submissionState = "portal-submitted";
                }
                else
                {
                    tenantId = StoreCred(tenantId, "DEVCLOUD_STORE_TENANT_ID", "DECO_STORE_TENANT_ID", "DEVTEM_STORE_TENANT_ID");
                    clientId = StoreCred(clientId, "DEVCLOUD_STORE_CLIENT_ID", "DECO_STORE_CLIENT_ID", "DEVTEM_STORE_CLIENT_ID");
                    clientSecret = StoreCred(clientSecret, "DEVCLOUD_STORE_CLIENT_SECRET", "DECO_STORE_CLIENT_SECRET", "DEVTEM_STORE_CLIENT_SECRET");
                    if (dryRun)
                    {
                        Console.WriteLine("DRYRUN would: auth -> create -> PUT listings+packages -> upload ZIP -> "
                            + ((rehearse || listingOnly) ? "DELETE (rehearse)" : "commit + poll"));
                        submissionState = "dryrun";
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                        {
                            throw new DecoException("Missing TenantId/ClientId/ClientSecret for Api submission.", "release-creds", ExitCodes.UsageOrAuthNeeded);
                        }

                        if (string.IsNullOrWhiteSpace(appId))
                        {
                            throw new DecoException("Missing --app-id for Api submission.", "release-creds", ExitCodes.UsageOrAuthNeeded);
                        }
                    }

                    if (!dryRun)
                    {
                        using var http = new HttpClient();
                        var api = new StoreApiClient(http);
                        Console.WriteLine("-- authenticate tenant");
                        string token = api.GetTokenAsync(tenantId, clientId, clientSecret).GetAwaiter().GetResult();
                        Console.WriteLine("-- create submission (clones last published)");
                        var created = api.CreateSubmissionAsync("1.0", appId, token).GetAwaiter().GetResult();
                        submissionId = StoreApiClient.Str(created, "id");
                        Console.WriteLine($"Submission: {submissionId}");
                        try
                        {
                            Console.WriteLine("-- GET submission, patch listings+packages (full object, never partial)");
                            var full = api.GetSubmissionAsync("1.0", appId, submissionId, token).GetAwaiter().GetResult();
                            string patched = StoreApiSubmission.BuildSubmissionListings(
                                System.Text.Json.JsonSerializer.Serialize(full), descriptionFile,
                                string.IsNullOrWhiteSpace(releaseNotes)
                                    ? "See what's new in the app (Home > About) and the changelog."
                                    : releaseNotes,
                                skipShots ? null : shotsDir, Path.GetFileName(multiBundle));
                            if (!string.IsNullOrWhiteSpace(certNotes))
                            {
                                patched = patched.TrimEnd('}');
                                patched += $",\"notesForCertification\":{System.Text.Json.JsonSerializer.Serialize(certNotes)}}}";
                            }

                            Console.WriteLine("-- PUT full submission + upload ZIP (bundle + shots)");
                            string zipPath = Path.Combine(Path.GetTempPath(), "submission-" + Guid.NewGuid().ToString("N") + ".zip");
                            try
                            {
                                StoreApiSubmission.BuildSubmissionZip(multiBundle, skipShots ? null : shotsDir, zipPath);
                                (_, string uploadUrl) = api.UpdateSubmissionAsync("1.0", appId, submissionId, token, patched).GetAwaiter().GetResult();
                                api.UploadBlobAsync(uploadUrl, zipPath).GetAwaiter().GetResult();
                            }
                            finally
                            {
                                try
                                {
                                    File.Delete(zipPath);
                                }
                                catch
                                {
                                }
                            }

                            if (rehearse || listingOnly)
                            {
                                Console.WriteLine($"-- rehearsal/listing-only: deleting uncommitted submission {submissionId}");
                                api.DeleteSubmissionAsync("1.0", appId, submissionId, token).GetAwaiter().GetResult();
                                submissionState = "rehearsed-then-deleted";
                            }
                            else
                            {
                                Console.WriteLine($"-- commit (publish mode: {publishMode})");
                                api.CommitAsync("1.0", appId, submissionId, token, publishMode).GetAwaiter().GetResult();
                                submissionState = "committed";
                                submissionStatus = api.WaitForPublishedAsync("1.0", appId, submissionId, token,
                                    pollTimeoutSec, Console.WriteLine).GetAwaiter().GetResult();
                            }
                        }
                        catch
                        {
                            Console.WriteLine($"Submission {submissionId} left uncommitted after error.");
                            if (cleanupOnFail && submissionId is not null)
                            {
                                Console.WriteLine($"-- cleanup-on-fail: deleting uncommitted submission {submissionId}");
                                try
                                {
                                    using var http2 = new HttpClient();
                                    var api2 = new StoreApiClient(http2);
                                    string token2 = api2.GetTokenAsync(tenantId, clientId, clientSecret).GetAwaiter().GetResult();
                                    api2.DeleteSubmissionAsync("1.0", appId, submissionId, token2).GetAwaiter().GetResult();
                                    submissionState = "cleaned-up";
                                }
                                catch (Exception ex2)
                                {
                                    Console.WriteLine($"Cleanup delete failed (delete it in Partner Center): {ex2.Message}");
                                }
                            }

                            throw;
                        }
                    }
                }
            }

            // --- Stage 6: Manifest ---
            string manifestPath = Path.Combine(msixDir, $"release-manifest-{version}.json");
            if (stages.Contains(6))
            {
                Stage("6. Release manifest");
                string gitSha = "unknown";
                string branch = string.Empty;
                bool gitClean = false;
                try
                {
                    IProcessRunner grunner = new ProcessRunner();
                    ProcessResult head = grunner.RunAsync("git", ["rev-parse", "--short", "HEAD"], ctx.RepoRoot, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    if (head.ExitCode == 0)
                    {
                        gitSha = head.StdOut.Trim();
                    }

                    ProcessResult br = grunner.RunAsync("git", ["branch", "--show-current"], ctx.RepoRoot, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    if (br.ExitCode == 0)
                    {
                        branch = br.StdOut.Trim();
                    }

                    ProcessResult st = grunner.RunAsync("git", ["status", "--porcelain=v1"], ctx.RepoRoot, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    gitClean = st.ExitCode == 0 && !st.StdOut.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Any(l => !l.StartsWith("??", StringComparison.Ordinal));
                }
                catch
                {
                    // Best effort git facts.
                }

                if (stageSw is not null && stageCurrent != string.Empty)
                {
                    timings[stageCurrent] = Math.Round(stageSw.Elapsed.TotalSeconds, 1);
                    stageSw = null;
                    stageCurrent = string.Empty;
                }

                var artifactRows = new List<string>();
                foreach (string artifactFile in new[] { multiBundle, multiUpload })
                {
                    if (File.Exists(artifactFile))
                    {
                        var info = new FileInfo(artifactFile);
                        artifactRows.Add(StoreJson.CanonicalWriter.Write(w =>
                        {
                            w.BeginObject();
                            w.PropertyName("name"); w.Value(info.Name);
                            w.PropertyName("bytes"); w.Value(info.Length);
                            w.PropertyName("sha256"); w.Value(StoreJson.Sha256File(artifactFile));
                            w.EndObject();
                        }));
                    }
                }

                string manifestJson = StoreJson.CanonicalWriter.Write(w =>
                {
                    w.BeginObject();
                    w.PropertyName("schemaVersion"); w.Value((long)2);
                    w.PropertyName("version"); w.Value(version);
                    w.PropertyName("versionQuad"); w.Value(versionQuad);
                    w.PropertyName("publisher"); w.Value("withheld (passed as parameter, never logged)");
                    w.PropertyName("appId"); w.Value(appId);
                    w.PropertyName("publishMode"); w.Value(publishMode);
                    w.PropertyName("submitter"); w.Value(submitter);
                    w.PropertyName("startedUtc"); w.Value(DateTime.UtcNow.ToString("o"));
                    w.PropertyName("checks");
                    w.BeginObject();
                    w.PropertyName("unitTests");
                    w.BeginObject();
                    w.PropertyName("status"); w.Value(skipTests ? "skipped" : unitStatus);
                    w.PropertyName("reason"); w.Value(skipTests ? "Explicitly skipped with --skip-tests." : unitReason);
                    w.EndObject();
                    w.PropertyName("packageBuild");
                    w.BeginObject();
                    w.PropertyName("status"); w.Value(skipBuild ? "reused" : packageStatus);
                    w.PropertyName("reason"); w.Value(skipBuild ? "Existing package artifacts were reused with --skip-build." : packageReason);
                    w.EndObject();
                    w.PropertyName("wack");
                    w.BeginObject();
                    w.PropertyName("status"); w.Value(wackStatus);
                    w.PropertyName("reason"); w.Value(wackReason);
                    w.EndObject();
                    w.PropertyName("packagedUiSmoke");
                    w.BeginObject();
                    w.PropertyName("status"); w.Value("not-run");
                    w.PropertyName("reason"); w.Value("Packaged navigation smoke is an owner-side installed-package check.");
                    w.EndObject();
                    w.EndObject();
                    w.PropertyName("gitSha"); w.Value(gitSha);
                    w.PropertyName("branch"); w.Value(branch);
                    w.PropertyName("gitClean"); w.Value(gitClean);
                    w.PropertyName("submission"); w.Value(submissionState);
                    if (submissionId is not null)
                    {
                        w.PropertyName("submissionId"); w.Value(submissionId);
                    }

                    if (submissionStatus is not null)
                    {
                        w.PropertyName("status"); w.Value(submissionStatus);
                    }

                    w.PropertyName("finishedUtc"); w.Value(DateTime.UtcNow.ToString("o"));
                    w.PropertyName("timingsSec");
                    w.BeginObject();
                    foreach ((string k, double v) in timings)
                    {
                        w.PropertyName(k); w.Raw(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    w.EndObject();
                    w.PropertyName("artifacts");
                    w.BeginArray();
                    foreach (string row in artifactRows)
                    {
                        w.Raw(row);
                    }

                    w.EndArray();
                    w.EndObject();
                });

                if (dryRun)
                {
                    Console.WriteLine("DRYRUN manifest would be written to: " + manifestPath);
                }
                else
                {
                    File.WriteAllText(manifestPath, PrettyPrint(manifestJson), Encoding.UTF8);
                    Console.WriteLine($"Manifest: {manifestPath}");
                    try
                    {
                        File.WriteAllText(stateFile, StoreJson.CanonicalWriter.Write(w =>
                        {
                            w.BeginObject();
                            w.PropertyName("version"); w.Value(version);
                            w.PropertyName("stagesDone");
                            w.BeginArray();
                            w.Value("packages");
                            if (unitStatus == "passed")
                            {
                                w.Value("tests");
                            }

                            if (wackStatus == "passed")
                            {
                                w.Value("wack");
                            }

                            w.EndArray();
                            w.PropertyName("status"); w.Value("complete");
                            w.EndObject();
                        }), Encoding.UTF8);
                    }
                    catch
                    {
                    }

                    if (!string.IsNullOrWhiteSpace(packageDir))
                    {
                        Directory.CreateDirectory(packageDir);
                        foreach (string f in Directory.GetFiles(msixDir, $"{ctx.AppId}_{version}*"))
                        {
                            File.Copy(f, Path.Combine(packageDir, Path.GetFileName(f)), overwrite: true);
                        }

                        File.Copy(manifestPath, Path.Combine(packageDir, $"release-manifest-{version}.json"), overwrite: true);
                        Console.WriteLine($"Copied submission set to {packageDir}");
                    }
                }
            }

            // --- Stage 7: Review index ---
            if (stages.Contains(7))
            {
                Stage("7. Review index");
                string reviewVersion = listingVersion;
                string reviewManifest = Path.Combine(ctx.StoreDir, reviewVersion, "screenshot-manifest.json");
                if (!File.Exists(reviewManifest) && !dryRun)
                {
                    Console.WriteLine($"Skipped (no screenshot manifest for {reviewVersion}).");
                }
                else
                {
                    int code = RunChild(
                        ["store", "review", "--version", reviewVersion, "--project", ctx.RepoRoot],
                        TimeSpan.FromMinutes(10), null);
                    if (code != 0)
                    {
                        throw new DecoException($"Review index failed (exit {code}).", "release-review");
                    }
                }
            }

            Console.WriteLine(string.Empty);
            DevCli.Commands.CliConsole.Success("Pipeline complete.", noAnsi);
            if (stageSw is not null && stageCurrent != string.Empty)
            {
                timings[stageCurrent] = Math.Round(stageSw.Elapsed.TotalSeconds, 1);
            }

        DevCli.Commands.CliConsole.Rule("Stage timings (s)", noAnsi);
        foreach ((string k, double v) in timings)
        {
            DevCli.Commands.CliConsole.Info($"{k}: {v}", noAnsi);
        }

            if (format.Trim().ToLowerInvariant() == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "release store",
                    dryRun,
                    submission = submissionState,
                    submissionId,
                    manifest = Path.Combine(msixDir, $"release-manifest-{version}.json"),
                    timings,
                });
            }

            return ExitCodes.Success;
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

        static string ReadCsprojProp(string csprojPath, string name)
        {
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(csprojPath);
                foreach (System.Xml.XmlNode n in doc.GetElementsByTagName(name))
                {
                    if (!string.IsNullOrWhiteSpace(n.InnerText))
                    {
                        return n.InnerText.Trim();
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        static string PrettyPrint(string compact)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(compact);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
    }

}
