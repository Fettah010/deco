// Command handlers: Store. Shared helpers, types, Main, and the command tree stay in Program.cs.
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
    private static string[] DefaultStoreLocales(string[] flag, ProjectContext ctx) =>
        flag.Length > 0 ? flag : ctx.Locales;

    private static string ResolveStoreVersion(ProjectContext ctx, string flag, bool informational)
    {
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return flag.Trim();
        }

        string? v = informational ? ctx.InformationalVersion : ctx.Version;
        if (string.IsNullOrWhiteSpace(v))
        {
            throw new DecoException(
                "Could not resolve a version from the csproj; pass --version explicitly.",
                "store-no-version",
                ExitCodes.UsageOrAuthNeeded);
        }

        return v.Trim();
    }

    private static string ResolveCandidateDir(ProjectContext ctx, string candidate, string version)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Path.Combine(ctx.StoreDir, version);
        }

        return Path.IsPathRooted(candidate) ? candidate : Path.Combine(ctx.RepoRoot, candidate);
    }

    private static StoreListingBundle.Bundle LoadStoreBundle(ProjectContext ctx, string source, string version, string[] locales, bool allowDraft)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            source = ctx.ListingSourceDir;
        }

        return StoreListingBundle.Load(source, version, ctx.RepoRoot, locales, allowDraft);
    }

    private static (StoreShots.ShotPlan Plan, string PlanSha) LoadShotPlan(ProjectContext ctx, string[] locales)
    {
        string planPath = Path.Combine(ctx.ListingSourceDir, "ScreenshotPlan.json");
        return (StoreShots.LoadPlan(planPath, locales), StoreJson.Sha256File(planPath));
    }

    private static ShotProvenance GatherShotProvenance(
        IProcessRunner runner, ProjectContext ctx, string version, string configuration, string[] locales)
    {
        (StoreShots.ShotPlan plan, string planSha) = LoadShotPlan(ctx, locales);

        if (!string.IsNullOrWhiteSpace(ctx.InformationalVersion)
            && ctx.InformationalVersion.Trim() != version)
        {
            throw new DecoException(
                $"Screenshot version '{version}' does not match csproj InformationalVersion '{ctx.InformationalVersion}'.",
                "shots-version");
        }

        string GitOut(string exe, List<string> args)
        {
            ProcessResult r = runner.RunAsync(exe, args, ctx.RepoRoot, TimeSpan.FromSeconds(60))
                .GetAwaiter().GetResult();
            if (r.ExitCode != 0)
            {
                throw new DecoException($"Could not resolve git provenance ({exe} {string.Join(" ", args)}).", "shots-git");
            }

            return r.StdOut;
        }

        string commit = GitOut("git", ["rev-parse", "HEAD"]).Trim();
        if (string.IsNullOrWhiteSpace(commit))
        {
            throw new DecoException("Could not resolve the current Git commit for screenshot provenance.", "shots-git");
        }

        string[] status = GitOut("git", ["status", "--porcelain", "--untracked-files=all"])
            .Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string trackedDiff = GitOut("git", ["-c", "core.safecrlf=false", "diff", "--binary", "HEAD", "--"]);
        string[] untracked = GitOut("git", ["ls-files", "--others", "--exclude-standard"])
            .Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var untrackedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string rel in untracked)
        {
            string full = Path.Combine(ctx.RepoRoot, rel);
            if (File.Exists(full))
            {
                untrackedHashes[rel] = StoreJson.Sha256File(full);
            }
        }

        string trackedSha = StoreJson.Sha256Text(trackedDiff);
        string statusSha = StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            w.PropertyName("status");
            w.BeginArray();
            foreach (string s in status)
            {
                w.Value(s);
            }

            w.EndArray();
            w.PropertyName("trackedDiffSha256"); w.Value(trackedSha);
            w.PropertyName("untrackedFileHashes");
            w.BeginObject();
            foreach ((string k, string v) in untrackedHashes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                w.PropertyName(k); w.Value(v);
            }

            w.EndObject();
            w.EndObject();
        });
        statusSha = StoreJson.Sha256Text(statusSha);

        string binDir = Path.Combine(ctx.RepoRoot, "bin");
        string? binaryPath = Directory.Exists(binDir)
            ? Directory.GetFiles(binDir, ctx.AppId + ".exe", SearchOption.AllDirectories)
                .Where(p => p.Contains(configuration, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        string? binarySha = null;
        string? productVersion = null;
        if (binaryPath is not null)
        {
            binarySha = StoreJson.Sha256File(binaryPath);
            productVersion = FileVersionInfo.GetVersionInfo(binaryPath).ProductVersion;
            if (string.IsNullOrWhiteSpace(productVersion)
                || !productVersion.StartsWith(version + "+", StringComparison.OrdinalIgnoreCase)
                || !productVersion.Contains(commit, StringComparison.OrdinalIgnoreCase))
            {
                throw new DecoException(
                    $"The {configuration} app binary does not identify candidate version '{version}' and commit '{commit}'.",
                    "shots-binary");
            }
        }

        string harnessPath = Path.Combine(ctx.RepoRoot, "Tests", "UI", "StoreShotsTests.cs");
        string? harnessSha = File.Exists(harnessPath) ? StoreJson.Sha256File(harnessPath) : null;

        var listingHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string locale in locales)
        {
            string listingPath = Path.Combine(ctx.ListingSourceDir, locale, "listing.json");
            if (!File.Exists(listingPath))
            {
                throw new DecoException($"Localized listing source is missing for screenshot review: {listingPath}", "shots-listing");
            }

            listingHashes[locale] = StoreJson.Sha256File(listingPath);
        }

        string? binaryRel = binaryPath is null ? null
            : Path.GetRelativePath(ctx.RepoRoot, binaryPath).Replace('\\', '/');
        var provenance = new StoreShots.Provenance(
            commit, status.Length > 0, statusSha, binaryRel, binarySha, productVersion, harnessSha, listingHashes);
        return new ShotProvenance(provenance, planSha, plan);
    }

    private static int RunStoreListings(string? project, string verbosity, string format, bool noAnsi,
        string source, string version, string[] locales, bool preview, bool requireApproval)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        ProjectContext ctx;
        StoreListingBundle.Bundle bundle;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
            locales = DefaultStoreLocales(locales, ctx);
            version = ResolveStoreVersion(ctx, version, informational: true);
            bundle = LoadStoreBundle(ctx, source, version, locales, allowDraft: preview);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        foreach (string loc in StoreListingBundle.FindEmbeddedReleaseNotes(bundle.Locales))
        {
            if (format != "json")
            {
                DevCli.Commands.CliConsole.Warn($"{loc} description embeds a versioned 'New in X' note; release notes belong in whatsNew, not the description.", noAnsi);
            }
        }

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "store listings",
                requestedVersion = version,
                approved = bundle.IsApproved,
                issues = bundle.ReviewIssues,
                locales = bundle.Locales.Select(l => new
                {
                    locale = l.Locale,
                    product = l.Listing.TryGetProperty("productName", out var pn) ? pn.GetString() : null,
                    reviewStatus = l.Listing.TryGetProperty("reviewStatus", out var rs) ? rs.GetString() : null,
                }).ToArray(),
            });
            if (requireApproval && !bundle.IsApproved)
            {
                return ExitCodes.Error;
            }

            return ExitCodes.Success;
        }

        foreach (StoreListingBundle.LocaleEntry entry in bundle.Locales)
        {
            string listingStatus = StatusOf(entry.Listing) == "approved" && StatusOf(entry.ReleaseNotes) == "approved"
                ? "APPROVED" : "DRAFT - REVIEW REQUIRED";
            DevCli.Commands.CliConsole.Rule($"{entry.Locale} | {version} | {listingStatus}", noAnsi);
            Console.WriteLine($"Product: {Str(entry.Listing, "productName")}");
            Console.WriteLine($"Short description: {Str(entry.Listing, "shortDescription")}");
            Console.WriteLine("Description:");
            Console.WriteLine(Str(entry.Listing, "description"));
            Console.WriteLine("Product features:");
            if (entry.Listing.TryGetProperty("productFeatures", out var features))
            {
                foreach (var f in features.EnumerateArray())
                {
                    Console.WriteLine($"- [{Str(f, "id")}] {Str(f, "text")}");
                }
            }

            string whatsNew = Str(entry.ReleaseNotes, "whatsNew");
            Console.WriteLine(string.IsNullOrWhiteSpace(whatsNew) ? "What's new: (not provided)" : $"What's new: {whatsNew}");
            Console.WriteLine(string.Empty);
        }

        if (!bundle.IsApproved)
        {
            if (requireApproval)
            {
                Console.Error.WriteLine($"Error: Listing source is not approved for submission. {string.Join(" ", bundle.ReviewIssues)}");
                return ExitCodes.Error;
            }

            DevCli.Commands.CliConsole.Warn($"Draft preview only; not approved for submission. {string.Join(" ", bundle.ReviewIssues)}", noAnsi);
        }
        else if (preview)
        {
            DevCli.Commands.CliConsole.Warn("Preview requested; content is approved, but this command never submits to Partner Center.", noAnsi);
        }
        else
        {
            Console.WriteLine($"Listing source validated for {version}. This command does not submit to Partner Center.");
        }

        return ExitCodes.Success;

        static string Str(System.Text.Json.JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        static string StatusOf(System.Text.Json.JsonElement e) => Str(e, "reviewStatus");
    }

    private static int RunStoreShots(string? project, string verbosity, string format, bool noAnsi,
        string candidate, string version, string configuration, string[] locales,
        bool requireApproval, bool newManifest)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        if (configuration != "Debug" && configuration != "Release")
        {
            Console.Error.WriteLine($"Error: Configuration must be Debug|Release, got '{configuration}'.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        try
        {
            ProjectContext ctx = ProjectDiscovery.Discover(project);
            locales = DefaultStoreLocales(locales, ctx);
            version = ResolveStoreVersion(ctx, version, informational: true);
            candidate = ResolveCandidateDir(ctx, candidate, version);
            IProcessRunner runner = new ProcessRunner();
            ShotProvenance prov = GatherShotProvenance(runner, ctx, version, configuration, locales);

            if (newManifest)
            {
                (_, string sha) = StoreShots.NewManifest(candidate, version, configuration, prov.Plan, prov.PlanSha, prov.Provenance);
                using var manifestDoc = StoreJson.Load(Path.Combine(candidate, "screenshot-manifest.json"), "Screenshot manifest");
                StoreShots.WriteReviewAssets(candidate, manifestDoc.RootElement, prov.Plan.Locales);
                Console.WriteLine($"Screenshot manifest created: {Path.Combine(candidate, "screenshot-manifest.json")} ({sha[..12]})");
            }

            StoreShots.TestedManifest tested = StoreShots.TestManifest(
                candidate, version, configuration, prov.Plan, prov.PlanSha, prov.Provenance, requireApproval);
            int images = tested.Manifest.GetProperty("images").GetArrayLength();
            int localeCount = tested.Manifest.GetProperty("locales").GetArrayLength();
            Console.WriteLine($"Valid screenshot set: {images} images, {localeCount} locales.");
            Console.WriteLine($"Manifest SHA-256: {tested.ManifestSha}");
            Console.WriteLine($"Approval: {(File.Exists(Path.Combine(candidate, "review", "approval.json")) ? "valid" : "pending human review")}");
            foreach (var dup in StoreShots.FindDuplicateImages(tested.Manifest))
            {
                if (format != "json")
                {
                    DevCli.Commands.CliConsole.Warn($"identical screenshots (each page should photograph differently): {string.Join(", ", dup)}", noAnsi);
                }
            }
            Console.WriteLine("This command validates local evidence only; it does not submit to Partner Center.");

            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "store shots",
                    candidate,
                    images,
                    locales = localeCount,
                    manifestSha256 = tested.ManifestSha,
                    approvalRequired = requireApproval,
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
    }

    private static int RunStoreApprove(string? project, string verbosity, string format, bool noAnsi,
        string reviewer, string candidate, string version, string configuration, string[] locales)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            Console.Error.WriteLine("Error: --reviewer is required.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        try
        {
            ProjectContext ctx = ProjectDiscovery.Discover(project);
            locales = DefaultStoreLocales(locales, ctx);
            version = ResolveStoreVersion(ctx, version, informational: true);
            candidate = ResolveCandidateDir(ctx, candidate, version);

            string candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            string storeRoot = Path.GetFullPath(ctx.StoreDir).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!candidateFull.StartsWith(storeRoot, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(candidateFull) != version)
            {
                throw new DecoException(
                    $"Screenshot approval must target '{Path.Combine(ctx.StoreDir, version)}'.",
                    "shots-approve-path",
                    ExitCodes.UsageOrAuthNeeded);
            }

            IProcessRunner runner = new ProcessRunner();
            ShotProvenance prov = GatherShotProvenance(runner, ctx, version, configuration, locales);
            StoreShots.TestedManifest tested = StoreShots.TestManifest(
                candidateFull, version, configuration, prov.Plan, prov.PlanSha, prov.Provenance, requireApproval: false);
            StoreShots.Approval approval = StoreShots.NewApproval(
                candidateFull, tested, prov.Plan, prov.PlanSha, prov.Provenance.ListingHashes, reviewer);

            Console.WriteLine($"Approval recorded for {approval.ScreenshotCount} screenshots by {approval.ApprovedBy}.");
            Console.WriteLine($"Approval file: {Path.Combine(candidateFull, "review", "approval.json")}");
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "store approve",
                    candidate = candidateFull,
                    approvedBy = approval.ApprovedBy,
                    approvedUtc = approval.ApprovedUtc,
                    screenshotCount = approval.ScreenshotCount,
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
    }

    private static int RunStoreEvidence(string? project, string verbosity, string format, bool noAnsi,
        string releaseManifest, string output, string candidate, string listingVersion,
        string wackStatus, string wackReason, string uiStatus, string uiReason, bool clean, bool dryRun)
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
            string packageVersion;
            string infoVersion;
            if (string.IsNullOrWhiteSpace(releaseManifest))
            {
                packageVersion = ResolveStoreVersion(ctx, "", informational: false);
                infoVersion = ResolveStoreVersion(ctx, "", informational: true);
                releaseManifest = Path.Combine(ctx.MsixDir, $"release-manifest-{packageVersion}.json");
            }
            else
            {
                using var probe = StoreJson.Load(releaseManifest, "Release manifest");
                packageVersion = probe.RootElement.TryGetProperty("version", out var pv) && pv.ValueKind == System.Text.Json.JsonValueKind.String
                    ? pv.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(packageVersion))
                {
                    throw new DecoException("Release manifest has no version.", "store-release-manifest");
                }

                infoVersion = ctx.InformationalVersion ?? packageVersion;
            }

            if (!File.Exists(releaseManifest))
            {
                throw new DecoException($"Release manifest not found: {releaseManifest}", "store-release-manifest");
            }

            using var relDoc = StoreJson.Load(releaseManifest, "Release manifest");
            string relVersion = relDoc.RootElement.TryGetProperty("version", out var rv) && rv.ValueKind == System.Text.Json.JsonValueKind.String
                ? rv.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(listingVersion))
            {
                listingVersion = relVersion == packageVersion ? infoVersion : relVersion;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                output = Path.Combine(ctx.RepoRoot, "Releases", "StoreEvidence", relVersion);
            }
            else if (!Path.IsPathRooted(output))
            {
                output = Path.Combine(ctx.RepoRoot, output);
            }

            var bundle = LoadStoreBundle(ctx, string.Empty, listingVersion,
                ctx.Locales, allowDraft: true);

            StoreShots.TestedManifest? tested = null;
            string candidateDir = ResolveCandidateDir(ctx, candidate, relVersion);
            if (File.Exists(Path.Combine(candidateDir, "screenshot-manifest.json")))
            {
                IProcessRunner runner = new ProcessRunner();
                ShotProvenance prov = GatherShotProvenance(runner, ctx, relVersion, "Release", ctx.Locales);
                tested = StoreShots.TestManifest(candidateDir, relVersion, "Release", prov.Plan, prov.PlanSha, prov.Provenance, requireApproval: true);
            }

            if (dryRun)
            {
                string guarded = StoreEvidence.GuardOutputDir(ctx.RepoRoot, output);
                Console.WriteLine($"[dry-run] would assemble evidence for {relVersion} -> {guarded}");
                Console.WriteLine($"  listing: {(bundle.IsApproved ? "approved" : "draft")}, screenshots: {(tested is null ? "not-provided" : "approved")}");
                return ExitCodes.Success;
            }

            string gitSha = "unknown";
            bool cleanTree = false;
            try
            {
                IProcessRunner grunner = new ProcessRunner();
                ProcessResult head = grunner.RunAsync("git", ["rev-parse", "HEAD"], ctx.RepoRoot, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                ProcessResult status = grunner.RunAsync("git", ["status", "--porcelain=v1", "--untracked-files=all"], ctx.RepoRoot, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                if (head.ExitCode == 0 && !string.IsNullOrWhiteSpace(head.StdOut))
                {
                    gitSha = head.StdOut.Trim();
                }

                if (status.ExitCode == 0)
                {
                    cleanTree = string.IsNullOrWhiteSpace(status.StdOut);
                }
            }
            catch
            {
                // Best effort; evidence records unknown/dirty.
            }

            var paths = StoreEvidence.Assemble(
                new StoreEvidence.Input(ctx.RepoRoot, releaseManifest, output, listingVersion, candidateDir,
                    wackStatus.Trim().ToLowerInvariant(), wackReason, uiStatus.Trim().ToLowerInvariant(), uiReason,
                    clean, ctx.AppId, gitSha, cleanTree, ctx.ListingSourceDir),
                bundle, tested);

            DevCli.Commands.CliConsole.Success($"Review artifact ready: {paths.OutputDir}", noAnsi);
            DevCli.Commands.CliConsole.Info($"Evidence manifest: {paths.ManifestPath}", noAnsi);
            DevCli.Commands.CliConsole.WriteLine(
                $"Listing status: {DevCli.Commands.CliConsole.StatusWord(bundle.IsApproved ? "approved" : "draft", noAnsi)}",
                $"Listing status: {(bundle.IsApproved ? "approved" : "draft")}",
                noAnsi);
            DevCli.Commands.CliConsole.WriteLine(
                $"Screenshot status: {DevCli.Commands.CliConsole.StatusWord(tested is null ? "not-provided" : "approved", noAnsi)}",
                $"Screenshot status: {(tested is null ? "not-provided" : "approved")}",
                noAnsi);
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "store evidence",
                    outputDir = paths.OutputDir,
                    manifest = paths.ManifestPath,
                    listingApproved = bundle.IsApproved,
                    screenshotsApproved = tested is not null,
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
    }

    private static int RunStoreReview(string? project, string verbosity, string format, bool noAnsi,
        string version, string candidate)
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
            version = ResolveStoreVersion(ctx, version, informational: true);
            candidate = ResolveCandidateDir(ctx, candidate, version);
            if (!File.Exists(Path.Combine(candidate, "screenshot-manifest.json")))
            {
                throw new DecoException($"Screenshot manifest missing: {Path.Combine(candidate, "screenshot-manifest.json")}.", "shots-manifest-missing");
            }

            using var manifestDoc = StoreJson.Load(Path.Combine(candidate, "screenshot-manifest.json"), "Screenshot manifest");
            (string gallery, string summary, string? baselineVer) = BuildReviewForCandidate(ctx, version, candidate, manifestDoc.RootElement);

            Console.WriteLine($"Review gallery: {gallery}");
            Console.WriteLine($"Review summary: {summary}");
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "store review",
                    candidate,
                    gallery,
                    summary,
                    baseline = baselineVer,
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
    }

    private static (string Gallery, string Summary, string? Baseline) BuildReviewForCandidate(
        ProjectContext ctx, string version, string candidate, System.Text.Json.JsonElement manifestRoot)
    {
        var locales = manifestRoot.GetProperty("locales").EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty).ToList();

        string listingSource = ctx.ListingSourceDir;
        var listingText = new List<(string Locale, string Description, string Short, List<string> Features, string Notes, List<string> Missing)>();
        foreach (string loc in locales)
        {
            var missing = new List<string>();
            string description = string.Empty, shortDesc = string.Empty, notes = string.Empty;
            var features = new List<string>();
            string lp = Path.Combine(listingSource, loc, "listing.json");
            if (File.Exists(lp))
            {
                using var ldoc = StoreJson.Load(lp, "Listing");
                description = ReviewStr(ldoc.RootElement, "description");
                shortDesc = ReviewStr(ldoc.RootElement, "shortDescription");
                if (ldoc.RootElement.TryGetProperty("productFeatures", out var feats))
                {
                    features.AddRange(feats.EnumerateArray().Select(f => ReviewStr(f, "text")));
                }
            }
            else
            {
                missing.Add("listing.json");
            }

            string np = Path.Combine(listingSource, "release-notes", version, loc + ".json");
            if (File.Exists(np))
            {
                using var ndoc = StoreJson.Load(np, "Release notes");
                notes = ReviewStr(ndoc.RootElement, "whatsNew");
            }
            else
            {
                missing.Add("release-notes");
            }

            listingText.Add((loc, description, shortDesc, features, notes, missing));
        }

        StoreReview.Baseline? baseline = null;
        string storeRoot = ctx.StoreDir;
        if (Directory.Exists(storeRoot))
        {
            foreach (string dir in Directory.GetDirectories(storeRoot)
                .Where(d => Path.GetFileName(d) != version)
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)))
            {
                string bm = Path.Combine(dir, "screenshot-manifest.json");
                if (File.Exists(bm))
                {
                    try
                    {
                        var bdoc = StoreJson.Load(bm, "Baseline manifest");
                        baseline = new StoreReview.Baseline(Path.GetFileName(dir), bdoc.RootElement.Clone());
                        bdoc.Dispose();
                        break;
                    }
                    catch
                    {
                        // Skip unreadable baselines.
                    }
                }
            }
        }

        System.Text.Json.JsonElement? pipeline = null;
        string? pipelineName = null;
        string msixDir = ctx.MsixDir;
        System.Text.Json.JsonDocument? pipelineDoc = null;
        if (Directory.Exists(msixDir))
        {
            foreach (string cand in Directory.GetFiles(msixDir, "release-manifest-*.json")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f)))
            {
                try
                {
                    var doc = StoreJson.Load(cand, "Pipeline manifest");
                    string v = doc.RootElement.TryGetProperty("version", out var vv) && vv.ValueKind == System.Text.Json.JsonValueKind.String
                        ? vv.GetString() ?? string.Empty : string.Empty;
                    if (v == version)
                    {
                        pipelineDoc = doc;
                        pipeline = doc.RootElement.Clone();
                        doc.Dispose();
                        pipelineName = Path.GetFileName(cand);
                        break;
                    }

                    doc.Dispose();
                }
                catch
                {
                    // Skip unreadable manifests.
                }
            }
        }

        try
        {
            StoreShots.WriteReviewAssets(candidate, manifestRoot, locales);
            StoreReview.Build(ctx.AppId, version, candidate, ctx.RepoRoot, listingText, baseline, pipeline, pipelineName);
        }
        finally
        {
            pipelineDoc?.Dispose();
        }

        return (Path.Combine(candidate, "review", "index.html"),
            Path.Combine(candidate, "review", "index.json"),
            baseline?.Version);
    }

    private static string ReviewStr(System.Text.Json.JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int RunStorePlan(string? project, string verbosity, string format, bool noAnsi,
        string snapshot, string bundlePath, string candidate, string version,
        string configuration, string[] locales, bool dryRun)
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
            locales = DefaultStoreLocales(locales, ctx);
            version = ResolveStoreVersion(ctx, version, informational: true);
            candidate = ResolveCandidateDir(ctx, candidate, version);
            if (!Directory.Exists(candidate))
            {
                throw new DecoException($"Screenshot candidate not found: {candidate}", "shots-candidate-missing");
            }

            if (string.IsNullOrWhiteSpace(snapshot) || !File.Exists(snapshot))
            {
                throw new DecoException($"Submission snapshot not found: {snapshot}", "store-snapshot-missing");
            }

            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            {
                throw new DecoException($"Submission bundle is missing: {bundlePath}", "store-bundle-missing");
            }

            var bundle = LoadStoreBundle(ctx, string.Empty, version, locales, allowDraft: false);
            IProcessRunner runner = new ProcessRunner();
            ShotProvenance prov = GatherShotProvenance(runner, ctx, version, configuration, locales);
            StoreShots.TestedManifest tested = StoreShots.TestManifest(
                candidate, version, configuration, prov.Plan, prov.PlanSha, prov.Provenance, requireApproval: false);

            if (dryRun)
            {
                string reviewDir = Path.Combine(candidate, "review");
                Console.WriteLine($"[dry-run] would plan offline submission for {version}:");
                Console.WriteLine($"  locales: {string.Join(",", locales)}, images: {tested.Manifest.GetProperty("images").GetArrayLength()}");
                Console.WriteLine($"  plan: {Path.Combine(reviewDir, "submission-payload.json")}");
                Console.WriteLine($"  zip: {Path.Combine(reviewDir, "submission-upload.zip")}");
                Console.WriteLine("No Partner Center API request, upload, draft creation, or submission commit would be performed.");
                return ExitCodes.Success;
            }

            StorePlan.BuiltPlan built = StorePlan.Build(
                Path.GetFullPath(snapshot), Path.GetFullPath(bundlePath), candidate, version,
                bundle, tested, locales);
            Console.WriteLine($"Offline submission contract plan created: {built.PlanPath}");
            Console.WriteLine($"Payload SHA-256: {built.PayloadSha256}");
            Console.WriteLine($"Upload archive: {built.ZipPath}");
            Console.WriteLine("No Partner Center API request, upload, draft creation, or submission commit was performed.");
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "store plan",
                    plan = built.PlanPath,
                    zip = built.ZipPath,
                    payloadSha256 = built.PayloadSha256,
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
    }

    private static int RunStoreSubmit(string? project, string verbosity, string format, bool noAnsi,
        string msixUpload, string appId, string tenantId, string clientId, string clientSecret,
        string publishMode, string certNotes, bool wait, int pollTimeoutSec, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        if (publishMode != "Immediate" && publishMode != "Manual")
        {
            Console.Error.WriteLine($"Error: --publish-mode must be Immediate|Manual, got '{publishMode}'.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (pollTimeoutSec <= 0)
        {
            pollTimeoutSec = 1200;
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

        if (string.IsNullOrWhiteSpace(msixUpload))
        {
            string msixDir = ctx.MsixDir;
            if (Directory.Exists(msixDir))
            {
                msixUpload = Directory.GetFiles(msixDir, "*.msixupload", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault() ?? string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(msixUpload) || !File.Exists(msixUpload))
        {
            Console.Error.WriteLine("Error: No .msixupload found. Build one first (deco build msix --store-upload) or pass --msixupload.");
            return ExitCodes.Error;
        }

        appId = StoreCred(appId, "DEVCLOUD_STORE_APP_ID", "DECO_STORE_APP_ID", "DEVTEM_STORE_APP_ID");
        if (string.IsNullOrWhiteSpace(appId))
        {
            appId = ctx.StoreAppId ?? string.Empty;
        }

        tenantId = StoreCred(tenantId, "DEVCLOUD_STORE_TENANT_ID", "DECO_STORE_TENANT_ID", "DEVTEM_STORE_TENANT_ID");
        clientId = StoreCred(clientId, "DEVCLOUD_STORE_CLIENT_ID", "DECO_STORE_CLIENT_ID", "DEVTEM_STORE_CLIENT_ID");
        clientSecret = StoreCred(clientSecret, "DEVCLOUD_STORE_CLIENT_SECRET", "DECO_STORE_CLIENT_SECRET", "DEVTEM_STORE_CLIENT_SECRET");
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(appId))
        {
            missing.Add("AppId (DECO_STORE_APP_ID)");
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            missing.Add("TenantId (DECO_STORE_TENANT_ID)");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            missing.Add("ClientId (DECO_STORE_CLIENT_ID)");
        }

        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            missing.Add("ClientSecret (never logged)");
        }

        if (missing.Count > 0)
        {
            Console.Error.WriteLine("Error: Missing " + string.Join(", ", missing) + ". Pass as flags or env vars.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        string fileName = Path.GetFileName(msixUpload);
        if (dryRun)
        {
            Console.WriteLine($"[dry-run] would submit {fileName} to {appId}: auth -> create -> PUT packages -> blob upload -> commit ({publishMode}){(wait ? " -> poll" : string.Empty)}.");
            Console.WriteLine("Secrets never logged; certification itself takes days.");
            return ExitCodes.Success;
        }

        try
        {
            using var http = new HttpClient();
            var api = new StoreApiClient(http);
            Console.WriteLine("Authenticating tenant (secret never logged)...");
            string token = api.GetTokenAsync(tenantId, clientId, clientSecret).GetAwaiter().GetResult();
            Console.WriteLine($"Creating submission for {fileName}...");
            var created = api.CreateSubmissionAsync("1.2", appId, token).GetAwaiter().GetResult();
            string submissionId = StoreApiClient.Str(created, "id");
            Console.WriteLine($"Submission: {submissionId}");

            string update = string.IsNullOrWhiteSpace(certNotes)
                ? $"{{\"applicationPackages\":[{{\"fileName\":\"{fileName}\"}}]}}"
                : $"{{\"applicationPackages\":[{{\"fileName\":\"{fileName}\"}}],\"notesForCertification\":{System.Text.Json.JsonSerializer.Serialize(certNotes)}}}";
            Console.WriteLine("Registering package...");
            (_, string uploadUrl) = api.UpdateSubmissionAsync("1.2", appId, submissionId, token, update).GetAwaiter().GetResult();
            Console.WriteLine($"Uploading package ({fileName})...");
            api.UploadBlobAsync(uploadUrl, msixUpload).GetAwaiter().GetResult();
            Console.WriteLine($"Committing (publish mode: {publishMode})...");
            api.CommitAsync("1.2", appId, submissionId, token, publishMode).GetAwaiter().GetResult();
            Console.WriteLine($"Committed submission {submissionId}.");

            if (wait)
            {
                string final = api.WaitForPublishedAsync("1.2", appId, submissionId, token, pollTimeoutSec, Console.WriteLine).GetAwaiter().GetResult();
                Console.WriteLine($"Done: {final}.");
            }
            else
            {
                Console.WriteLine($"Track certification in Partner Center (submission {submissionId}).");
            }

            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "store submit",
                    dryRun = false,
                    appId,
                    submissionId,
                    package = fileName,
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
    }

    private static int RunStoreCertWatch(string? project, string verbosity, string format, bool noAnsi,
        string appId, int timeoutMin, int intervalMin, bool once, bool notify,
        bool registerTask, bool unregisterTask, bool noCache, bool list, bool all)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();

        // App-id memory: explicit flag wins and is remembered per project;
        // otherwise reuse the remembered id so bare commands just work.
        string repoKey = "global";
        try
        {
            var discovered = ProjectDiscovery.Discover(project);
            if (!string.IsNullOrWhiteSpace(discovered.RepoRoot))
            {
                repoKey = DevCli.Commands.StoreCertWatch.RepoKey(discovered.RepoRoot);
            }
        }
        catch
        {
        }

        if (list)
        {
            var remembered = DevCli.Commands.StoreCertWatch.ListRemembered();
            if (remembered.Count == 0)
            {
                Console.WriteLine("No apps remembered yet. Pass --app-id once per app; it is remembered afterwards.");
                return ExitCodes.Success;
            }

            DevCli.Commands.CliConsole.Rule("Remembered apps (last known state)", noAnsi);
            var rows = new List<string[]>();
            int n = 0;
            foreach (var app in remembered)
            {
                n++;
                string state = string.IsNullOrWhiteSpace(app.LastStatus)
                    ? "never checked"
                    : $"{app.LastStatus} ({DevCli.Commands.StoreCertWatch.AgeLabel(app.UpdatedUtc)})";
                rows.Add([n.ToString(), app.AppId,
                    DevCli.Commands.CliConsole.StateCell(state, noAnsi)]);
            }

            DevCli.Commands.CliConsole.Table(["#", "App", "State"], rows, noAnsi, new HashSet<int> { 2 });
            return ExitCodes.Success;
        }

        if (!string.IsNullOrWhiteSpace(appId))
        {
            appId = appId.Trim();
            DevCli.Commands.StoreCertWatch.SaveLastAppId(repoKey, appId);
            if (repoKey != "global")
            {
                DevCli.Commands.StoreCertWatch.SaveLastAppId("global", appId);
            }
        }
        else if (!all)
        {
            // Deterministic per-project memory only here; outside any known
            // project the multi-app picker below decides (never a silent
            // global guess when several apps are known).
            string? remembered = repoKey != "global"
                ? DevCli.Commands.StoreCertWatch.LoadLastAppId(repoKey)
                : null;
            if (!string.IsNullOrWhiteSpace(remembered))
            {
                appId = remembered;
                if (format != "json")
                {
                    DevCli.Commands.CliConsole.WriteLine(
                        $"Using remembered Store Product ID {DevCli.Commands.CliConsole.Cyan(appId, noAnsi)} (override with --app-id).",
                        $"Using remembered Store Product ID {appId} (override with --app-id).",
                        noAnsi);
                }
            }
        }

        var everyone = DevCli.Commands.StoreCertWatch.ListRemembered();

        if (string.IsNullOrWhiteSpace(appId))
        {
            if (everyone.Count == 1)
            {
                appId = everyone[0].AppId;
                if (format != "json")
                {
                    DevCli.Commands.CliConsole.WriteLine(
                        $"Using remembered Store Product ID {DevCli.Commands.CliConsole.Cyan(appId, noAnsi)} (override with --app-id).",
                        $"Using remembered Store Product ID {appId} (override with --app-id).",
                        noAnsi);
                }
            }
            else if (everyone.Count > 1 && !all)
            {
                if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine($"Error: {everyone.Count} apps remembered. Rerun with --app-id, --all, or --list (or use an interactive terminal to pick).");
                    return ExitCodes.UsageOrAuthNeeded;
                }

                Console.WriteLine("Multiple apps remembered — pick one:");
                int i = 0;
                foreach (var app in everyone)
                {
                    i++;
                    string state = string.IsNullOrWhiteSpace(app.LastStatus)
                        ? "never checked"
                        : $"{app.LastStatus} ({DevCli.Commands.StoreCertWatch.AgeLabel(app.UpdatedUtc)})";
                    DevCli.Commands.CliConsole.WriteLine(
                        $"  {i}. {DevCli.Commands.CliConsole.Cyan(app.AppId, noAnsi)} — {DevCli.Commands.CliConsole.StateCell(state, noAnsi)}",
                        $"  {i}. {app.AppId} — {state}",
                        noAnsi);
                }

                Console.Write("Select [1]: ");
                int? pick = DevCli.Commands.StoreCertWatch.ParseSelection(Console.ReadLine(), everyone.Count);
                if (pick is null)
                {
                    Console.Error.WriteLine($"Error: pick a number 1-{everyone.Count}.");
                    return ExitCodes.UsageOrAuthNeeded;
                }

                appId = everyone[pick.Value].AppId;
            }
        }

        if (string.IsNullOrWhiteSpace(appId) && !all)
        {
            Console.Error.WriteLine("Error: Missing --app-id (Store Product ID, e.g. 9ABCDEF12345). Pass it once; it is remembered per project afterwards.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (all && (registerTask || unregisterTask))
        {
            Console.Error.WriteLine("Error: --all cannot be combined with --register-task/--unregister-task (tasks are per-app).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (timeoutMin <= 0)
        {
            timeoutMin = once ? 1 : 120;
        }

        if (intervalMin < 1)
        {
            intervalMin = 15;
        }

        bool detailed = verbosity.Trim().ToLowerInvariant() == "detailed";
        IProcessRunner runner = new ProcessRunner();

        try
        {
            if (unregisterTask)
            {
                DevCli.Commands.StoreCertWatch.UnregisterTaskAsync(runner, appId, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
                DevCli.Commands.CliConsole.Success($"Removed hourly task '{DevCli.Commands.StoreCertWatch.TaskName(appId)}'.", noAnsi);
                return ExitCodes.Success;
            }
            string msstore = MsstoreConfig.RequireCli(runner);

            if (all)
            {
                var targets = DevCli.Commands.StoreCertWatch.ListRemembered();
                if (targets.Count == 0)
                {
                    Console.Error.WriteLine("Error: no apps remembered yet. Pass --app-id once per app first.");
                    return ExitCodes.UsageOrAuthNeeded;
                }

                int failed = 0;
                foreach (var target in targets)
                {
                    string appStatus;
                    try
                    {
                        appStatus = DevCli.Commands.StoreCertWatch.PollOnceAsync(runner, msstore, target.AppId, TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
                    }
                    catch (DecoException ex)
                    {
                        Console.WriteLine($"{target.AppId}: poll failed ({ex.Message.Split('\n')[0]})");
                        failed++;
                        continue;
                    }

                    bool published = DevCli.Commands.StoreCertWatch.IsPublished(appStatus);
                    bool isFailed = DevCli.Commands.StoreCertWatch.IsFailed(appStatus);
                    DevCli.Commands.StoreCertWatch.SaveState(new DevCli.Commands.StoreCertWatch.WatchState(
                        target.AppId, appStatus, DateTimeOffset.UtcNow, published || isFailed, published, null));
                    Console.WriteLine($"{target.AppId}: {DevCli.Commands.CliConsole.StatusWord(appStatus, noAnsi)}");
                    if (isFailed)
                    {
                        failed++;
                    }
                }

                if (format == "json")
                {
                    EmitJson(new { tool = CliConstants.ToolName, version = CliConstants.Version, command = "store cert-watch", mode = "all", failed });
                }

                return failed == 0 ? ExitCodes.Success : ExitCodes.Error;
            }

            if (registerTask)
            {
                (string exe, List<string> pre) = DevCli.Commands.SelfLaunch.LaunchPrefix();
                DevCli.Commands.StoreCertWatch.RegisterTaskAsync(runner, exe, pre, appId, Math.Min(timeoutMin, 55), TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
                DevCli.Commands.CliConsole.Success($"Registered hourly task '{DevCli.Commands.StoreCertWatch.TaskName(appId)}' (one bounded pass per hour).", noAnsi);
                DevCli.Commands.CliConsole.Info($"State file: {DevCli.Commands.StoreCertWatch.StatePath(appId)}", noAnsi);
                DevCli.Commands.CliConsole.Info($"Remove with: deco store cert-watch --app-id {appId} --unregister-task", noAnsi);
                return ExitCodes.Success;
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(timeoutMin);
            string? lastSeen = DevCli.Commands.StoreCertWatch.LoadState(appId)?.LastStatus;
            if (!string.IsNullOrWhiteSpace(lastSeen) && !once)
            {
                DevCli.Commands.CliConsole.Info($"Resuming watch (last known state: {lastSeen}).", noAnsi);
            }

            // Instant answer for --once: show the cached state now, verify live below.
            bool showedCache = false;
            string? cachedStatus = null;
            if (once && !noCache && format != "json")
            {
                var cached = DevCli.Commands.StoreCertWatch.LoadState(appId);
                if (cached is not null && !string.IsNullOrWhiteSpace(cached.LastStatus))
                {
                    showedCache = true;
                    cachedStatus = cached.LastStatus;
                    string age = DevCli.Commands.StoreCertWatch.AgeLabel(cached.UpdatedUtc);
                    DevCli.Commands.CliConsole.WriteLine(
                        DevCli.Commands.StoreCertWatchLines.CachedMarkup(
                            age,
                            DevCli.Commands.CliConsole.Cyan(appId, noAnsi),
                            DevCli.Commands.CliConsole.StateCell(cached.LastStatus, noAnsi)),
                        $"Last checked {age}: {appId}: {cached.LastStatus} — checking live...",
                        noAnsi);
                }
            }

            string status = "Unknown";
            while (true)
            {
                try
                {
                    status = DevCli.Commands.StoreCertWatch.PollOnceAsync(runner, msstore, appId, TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
                }
                catch (DecoException ex)
                {
                    Console.Error.WriteLine($"Poll failed ({DateTimeOffset.UtcNow:HH:mm:ss}Z): {ex.Message.Split('\n')[0]}");
                    if (once)
                    {
                        if (showedCache)
                        {
                            Console.Error.WriteLine("Live poll failed; cached state above may be stale.");
                        }
                        return ExitCodes.Error;
                    }

                    Thread.Sleep(TimeSpan.FromMinutes(intervalMin));
                    continue;
                }

                bool changed = !string.Equals(status, lastSeen, StringComparison.OrdinalIgnoreCase);
                if (changed || detailed)
                {
                    string stamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss") + "Z";
                    DevCli.Commands.CliConsole.WriteLine(
                        DevCli.Commands.StoreCertWatchLines.PollMarkup(
                            stamp,
                            DevCli.Commands.CliConsole.Cyan(appId, noAnsi),
                            DevCli.Commands.CliConsole.StatusWord(status, noAnsi)),
                        $"[{stamp}] {appId}: {status}",
                        noAnsi);
                }

                lastSeen = status;
                bool published = DevCli.Commands.StoreCertWatch.IsPublished(status);
                bool failed = DevCli.Commands.StoreCertWatch.IsFailed(status);
                DevCli.Commands.StoreCertWatch.SaveState(new DevCli.Commands.StoreCertWatch.WatchState(
                    appId, status, DateTimeOffset.UtcNow, published || failed, published, null));

                if (published)
                {
                    DevCli.Commands.CliConsole.Success($"PUBLISHED: {appId} is live in the Store.", noAnsi);
                    if (notify)
                    {
                        DevCli.Commands.CliConsole.Info($"[notify] certification complete (Published). State: {DevCli.Commands.StoreCertWatch.StatePath(appId)}", noAnsi);
                    }

                    if (format == "json")
                    {
                        EmitJson(new { tool = CliConstants.ToolName, version = CliConstants.Version, command = "store cert-watch", appId, status, published = true });
                    }

                    return ExitCodes.Success;
                }

                if (failed)
                {
                    DevCli.Commands.CliConsole.Fail($"FAILED: submission {appId} ended as '{status}'. Check Partner Center for the certification report.", noAnsi);
                    return ExitCodes.Error;
                }

                if (once)
                {
                    if (showedCache && string.Equals(status, cachedStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Live check: unchanged.");
                    }

                    if (format == "json")
                    {
                        EmitJson(new { tool = CliConstants.ToolName, version = CliConstants.Version, command = "store cert-watch", appId, status, published = false });
                    }

                    return ExitCodes.Success;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    DevCli.Commands.CliConsole.Warn($"Watch timed out after {timeoutMin} min at '{status}'. Re-run, or register unattended checks: deco store cert-watch --app-id {appId} --register-task", noAnsi);
                    return ExitCodes.UsageOrAuthNeeded;
                }

                Thread.Sleep(TimeSpan.FromMinutes(intervalMin));
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
    }

    private static int RunPortalAuth(string? project, string verbosity, string format, bool noAnsi,
        string profileDir, bool logout, bool dryRun, int loginTimeoutSec)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (loginTimeoutSec <= 0)
        {
            loginTimeoutSec = 600;
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

        if (string.IsNullOrWhiteSpace(profileDir))
        {
            profileDir = PortalSession.DefaultProfileDir(ctx.AppId);
        }

        if (logout)
        {
            if (dryRun)
            {
                Console.WriteLine($"DRYRUN would delete: {profileDir}");
                return ExitCodes.Success;
            }

            try
            {
                PortalSession.WipeProfile(profileDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ExitCodes.Error;
            }

            Console.WriteLine("Portal profile wiped. Next run will ask you to log in again.");
            return ExitCodes.Success;
        }

        if (dryRun)
        {
            Console.WriteLine($"DRYRUN would: launch headed browser (profile {profileDir}) at Partner Center,");
            Console.WriteLine($"wait up to {loginTimeoutSec}s for a manual login, verify the dashboard, exit 0/2.");
            return ExitCodes.Success;
        }

        try
        {
            // Auth is headed by design: Microsoft login hardening (2FA, CAPTCHA,
            // risk checks) requires the visible browser. Automation runs headless.
            PortalSession.SessionResult result = PortalSession.EnsureLoggedInAsync(
                profileDir, headed: true, timeoutSec: loginTimeoutSec, Console.WriteLine)
                .GetAwaiter().GetResult();
            if (result.LoggedIn)
            {
                if (format.Trim().ToLowerInvariant() == "json")
                {
                    EmitJson(new
                    {
                        tool = CliConstants.ToolName,
                        version = CliConstants.Version,
                        command = "store portal-auth",
                        loggedIn = true,
                        engine = result.Engine,
                    });
                }

                return ExitCodes.Success;
            }

            Console.WriteLine($"Login still needed: re-run and complete the sign-in within {loginTimeoutSec} seconds.");
            return ExitCodes.UsageOrAuthNeeded;
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
    }

    private static int RunStorePortal(string? project, string verbosity, string format, bool noAnsi,
        string upload, string descriptionFile, string shotsDir, string releaseNotesOverride,
        string publishMode, string storeId, string profileDir, string version, string listingSource,
        bool autoConfirm, bool headed, bool noLoginAssist, bool skipShots, bool skipPackages,
        bool certifiedConfirmed, int loginTimeoutSec, string progressFile, bool dryRun, bool selfTest)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (selfTest)
        {
            Console.WriteLine("Portal engine self-test (local fixtures, zero Partner Center contact)...");
            List<PortalSelfTest.Check> checks;
            try
            {
                checks = PortalSelfTest.RunAsync(Console.WriteLine).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ExitCodes.Error;
            }

            int failed = checks.Count(c => !c.Passed);
            Console.WriteLine(failed == 0
                ? $"SELF-TEST: {checks.Count}/{checks.Count} engine checks green."
                : $"SELF-TEST: {failed} of {checks.Count} checks FAILED.");
            return failed == 0 ? ExitCodes.Success : ExitCodes.Error;
        }

        format = format.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(publishMode.Trim(), "^(Immediate|Manual)$", RegexOptions.IgnoreCase))
        {
            Console.Error.WriteLine($"Error: --publish-mode must be Immediate or Manual, got '{publishMode}'.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (loginTimeoutSec <= 0)
        {
            loginTimeoutSec = 600;
        }

        if (string.IsNullOrWhiteSpace(storeId))
        {
            Console.Error.WriteLine("Error: Missing --store-id (Store Product ID).");
            return ExitCodes.UsageOrAuthNeeded;
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

        if (string.IsNullOrWhiteSpace(upload))
        {
            string msixDir = ctx.MsixDir;
            if (Directory.Exists(msixDir))
            {
                upload = Directory.GetFiles(msixDir, "*.msixupload")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault() ?? string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(upload) || !File.Exists(upload))
        {
            Console.Error.WriteLine("Error: No .msixupload found. Build one first.");
            return ExitCodes.Error;
        }

        string descriptionOverride = string.Empty;
        if (!string.IsNullOrWhiteSpace(descriptionFile))
        {
            if (!File.Exists(descriptionFile))
            {
                Console.Error.WriteLine($"Error: Description file not found: {descriptionFile}.");
                return ExitCodes.Error;
            }

            descriptionOverride = File.ReadAllText(descriptionFile, Encoding.UTF8).Trim();
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            version = ctx.InformationalVersion ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            Console.Error.WriteLine("Error: Cannot resolve listing version: pass --version or keep InformationalVersion in the csproj.");
            return ExitCodes.Error;
        }

        if (string.IsNullOrWhiteSpace(listingSource))
        {
            listingSource = ctx.ListingSourceDir;
        }

        string[] locales = ctx.Locales;
        Dictionary<string, PortalContent.LocaleContent> localeContent;
        try
        {
            localeContent = PortalContent.ResolveLocaleContent(
                listingSource, version, locales, descriptionOverride, releaseNotesOverride, certifiedConfirmed);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        var warnings = new List<string>();
        Dictionary<string, List<string>> shotsByLocale =
            PortalContent.DiscoverShots(shotsDir, locales, warnings.Add);
        foreach (string w in warnings)
        {
            Console.WriteLine(w);
        }

        if (string.IsNullOrWhiteSpace(profileDir))
        {
            profileDir = PortalSession.DefaultProfileDir(ctx.AppId);
        }

        if (string.IsNullOrWhiteSpace(progressFile))
        {
            progressFile = Path.Combine(Path.GetTempPath(), $"portal-progress-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.jsonl");
        }

        if (dryRun)
        {
            double mb = Math.Round(new FileInfo(upload).Length / 1048576.0, 1);
            Console.WriteLine("DRYRUN preflight OK:");
            Console.WriteLine($"  upload: {upload} ({mb} MB)");
            foreach (string loc in locales)
            {
                var c = localeContent[loc];
                int shots = shotsByLocale.TryGetValue(loc, out var files) ? files.Count : 0;
                Console.WriteLine($"  {loc}: description {c.Description.Length} chars, notes {c.Notes.Length} chars, shots {shots}");
            }

            Console.WriteLine($"  listing version: {version} (publish mode {publishMode})");
            Console.WriteLine("  price gate: snapshot-then-compare the Portal price table (abort on any delta)");
            Console.WriteLine($"  profile: {profileDir}");
            Console.WriteLine($"  window: {(headed ? "headed (forced via --headed)" : "headless (login window only if needed)")}");
            Console.WriteLine($"  progress: {progressFile}");
            Console.WriteLine("DRYRUN would: ensure login -> Apps and games card -> app -> start update -> "
                + "price baseline -> upload package -> per-locale listings (paste + read-back) -> "
                + "per-locale screenshots -> price verify -> publish-mode verify -> checkpoint review -> submit -> manifest.");
            return ExitCodes.Success;
        }

        IProcessRunner runner = new ProcessRunner();
        string msstore = MsstoreConfig.RequireCli(runner, "API read-back baseline needs it");
        var texts = localeContent.ToDictionary(
            kv => kv.Key,
            kv => new PortalSubmission.LocaleText(kv.Key, kv.Value.Description, kv.Value.Notes),
            StringComparer.OrdinalIgnoreCase);
        var runPlan = new PortalSubmission.RunPlan(
            upload, texts, shotsByLocale, version, storeId, ctx.AppId, profileDir,
            publishMode, "See what's new in the app (Home > About) and the changelog.",
            autoConfirm, headed, noLoginAssist, skipShots, skipPackages, certifiedConfirmed,
            loginTimeoutSec, 120, progressFile,
            Path.Combine(ctx.MsixDir, "portal-manifest.json"),
            new PortalSubmission.Site(), msstore);

        try
        {
            PortalSubmission.RunResult result = PortalSubmission.RunAsync(runPlan, Console.WriteLine)
                .GetAwaiter().GetResult();
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "store portal",
                    dryRun = false,
                    manifest = result.ManifestPath,
                    steps = result.Steps,
                });
            }

            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Portal submitter failed: {ex.Message}");
            Console.Error.WriteLine($"Trace (open in Playwright Trace Viewer): {Path.Combine(Path.GetTempPath(), "portal-trace-*.zip")}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Portal submitter failed: {ex.Message}");
            return ExitCodes.Error;
        }
    }


    private static int RunStorePortalPrice(string? project, string verbosity, string format, bool noAnsi,
        string storeId, string submissionId, string currency, string price, string profileDir,
        bool apply, bool submit, bool allowFree, bool autoConfirm, bool headed, bool noLoginAssist,
        int loginTimeoutSec, string runDir, bool dryRun, bool selfTest)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (selfTest)
        {
            Console.WriteLine("Portal base-price self-test (behavioral fixture, zero Partner Center contact)...");
            List<PortalSelfTest.Check> checks;
            try
            {
                checks = PortalPricingSelfTest.RunAsync(Console.WriteLine).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ExitCodes.Error;
            }

            int failed = checks.Count(c => !c.Passed);
            Console.WriteLine(failed == 0
                ? $"SELF-TEST: {checks.Count}/{checks.Count} pricing checks green."
                : $"SELF-TEST: {failed} of {checks.Count} checks FAILED.");
            return failed == 0 ? ExitCodes.Success : ExitCodes.Error;
        }

        format = format.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(storeId))
        {
            Console.Error.WriteLine("Error: Missing --store-id (Store Product ID).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (string.IsNullOrWhiteSpace(submissionId))
        {
            Console.Error.WriteLine("Error: Missing --submission-id (pending submission ID).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (submit && !apply)
        {
            Console.Error.WriteLine("Error: --submit requires --apply in the same run (only verified pricing gets committed).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (apply)
        {
            var target = PortalPriceMatch.ValidateTarget(currency, price);
            if (!target.Ok)
            {
                Console.Error.WriteLine($"Error: {target.Error}");
                return ExitCodes.UsageOrAuthNeeded;
            }

            if (target.FreeShaped && !allowFree)
            {
                Console.Error.WriteLine("Error: Refusing to set a Free-shaped base price without --allow-free (store pricing rule).");
                return ExitCodes.UsageOrAuthNeeded;
            }
        }

        if (loginTimeoutSec <= 0)
        {
            loginTimeoutSec = 600;
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

        if (string.IsNullOrWhiteSpace(profileDir))
        {
            profileDir = PortalSession.DefaultProfileDir(ctx.AppId);
        }

        if (string.IsNullOrWhiteSpace(runDir))
        {
            runDir = Path.Combine(Path.GetTempPath(), $"deco-portal-price-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        }

        if (dryRun)
        {
            Console.WriteLine("DRYRUN plan (no browser, nothing changed):");
            Console.WriteLine($"  submission: {submissionId} (product {storeId})");
            Console.WriteLine($"  mode: {(apply ? $"APPLY currency '{currency}' + price '{price}' -> Save -> read-back verify" : "diagnose (read-only: open pricing page, read base price, screenshot)")} ");
            Console.WriteLine($"  commit: {(submit ? "yes (portal Submit for certification)" : "no")}");
            Console.WriteLine($"  profile: {profileDir}");
            Console.WriteLine($"  run dir: {runDir}");
            Console.WriteLine($"  window: {(headed ? "headed (forced via --headed)" : "headless (login window only if needed)")}");
            return ExitCodes.Success;
        }

        var plan = new PortalPricing.Plan(
            storeId, submissionId, currency, price, profileDir, apply, submit, autoConfirm, headed,
            noLoginAssist, loginTimeoutSec, runDir, SkipLogin: false,
            AvailabilityUrlOverride: string.Empty, new PortalSubmission.Site());
        PortalPricing.RunResult result;
        try
        {
            result = PortalPricing.RunAsync(plan, Console.WriteLine).GetAwaiter().GetResult();
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Portal pricing failed: {ex.Message}");
            Console.Error.WriteLine($"Run dir (screenshots + manifest): {runDir}");
            Console.Error.WriteLine($"Trace (open in Playwright Trace Viewer): {Path.Combine(Path.GetTempPath(), "portal-trace-*.zip")}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Portal pricing failed: {ex.Message}");
            return ExitCodes.Error;
        }

        bool submitted = result.Submitted;
        if (submit && submitted)
        {
            // Independent read-only confirmation that the submission left the
            // pending state (no commit call here — the portal did the submit).
            try
            {
                IProcessRunner runner = new ProcessRunner();
                string msstore = MsstoreConfig.RequireCli(runner, "status check needs it");
                ProcessResult st = runner.RunAsync(msstore,
                    ["submission", "status", storeId, "--output-stream", "stderr"],
                    timeout: TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
                string tail = (st.StdOut + "\n" + st.StdErr).Trim();
                Console.WriteLine($"Submission status: {tail[^Math.Min(400, tail.Length)..]}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Status check hiccup ({ex.Message}) — confirm in Partner Center.");
            }
        }

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "store portal-price",
                runDir = result.RunDir,
                manifest = result.ManifestPath,
                baseline = new { currency = result.Baseline.Currency, price = result.Baseline.Price },
                final = result.Final is null ? null : new { currency = result.Final.Currency, price = result.Final.Price },
                saved = result.Saved,
                submitted,
                steps = result.Steps,
            });
        }

        return ExitCodes.Success;
    }

    private static int RunStorePricing(string verbosity, string format, bool noAnsi,
        string appId, string[] sets, string baseTier, bool allowMigration, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(appId))
        {
            Console.Error.WriteLine("Error: Missing --app-id (Store Product ID, e.g. 9ABCDEF12345).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        IProcessRunner runner = new ProcessRunner();
        try
        {
            string msstore = MsstoreConfig.RequireCli(runner);
            var markets = DevCli.Commands.StorePricing.ParseMarketPrices(sets);
            bool changingBase = !string.IsNullOrWhiteSpace(baseTier);

            ProcessResult get = runner.RunAsync(msstore,
                ["submission", "get", appId, "--output-stream", "stderr"],
                null, TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
            if (get.ExitCode != 0)
            {
                throw new DecoException($"msstore submission get failed (exit {get.ExitCode}).", "pricing-get");
            }

            string raw = get.StdOut + "\n" + get.StdErr;
            int openAt = raw.IndexOf('{');
            int closeAt = raw.LastIndexOf('}');
            if (openAt < 0 || closeAt <= openAt)
            {
                throw new DecoException("Could not parse the submission JSON.", "pricing-parse");
            }

            using var sub = System.Text.Json.JsonDocument.Parse(raw.Substring(openAt, closeAt - openAt + 1));
            bool advanced = sub.RootElement.TryGetProperty("Pricing", out var pricingEl)
                && pricingEl.TryGetProperty("IsAdvancedPricingModel", out var adv)
                && adv.ValueKind == System.Text.Json.JsonValueKind.True;
            if (changingBase && advanced && !allowMigration)
            {
                throw new DecoException(
                    "Product uses advanced/per-market pricing: changing the base tier MIGRATES it to a single tier. "
                    + "Pass --allow-pricing-migration (explicit owner decision), or set per-market overrides with --set (migration-free).",
                    "pricing-migration",
                    ExitCodes.UsageOrAuthNeeded);
            }

            if (!changingBase && markets.Count == 0)
            {
                string baseId = pricingEl.ValueKind == System.Text.Json.JsonValueKind.Object
                    && pricingEl.TryGetProperty("PriceId", out var pid)
                    && pid.ValueKind == System.Text.Json.JsonValueKind.String
                    ? pid.GetString() ?? string.Empty : string.Empty;
                var rows = new List<(string Key, string Value)> { ("Base", baseId) };
                if (pricingEl.ValueKind == System.Text.Json.JsonValueKind.Object
                    && pricingEl.TryGetProperty("MarketSpecificPricings", out var mp)
                    && mp.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var m in mp.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        rows.Add((m.Name, m.Value.ValueKind == System.Text.Json.JsonValueKind.String ? m.Value.GetString() ?? string.Empty : string.Empty));
                    }
                }

                if (format == "json")
                {
                    EmitJson(new
                    {
                        tool = CliConstants.ToolName,
                        version = CliConstants.Version,
                        command = "store pricing",
                        appId,
                        advanced,
                        markets = rows.Select(r => new { market = r.Key, tier = r.Value }).ToArray(),
                    });
                    return ExitCodes.Success;
                }

                DevCli.Commands.CliConsole.Rule($"Pricing for {appId}" + (advanced ? " (advanced/per-market)" : string.Empty), noAnsi);
                DevCli.Commands.CliConsole.KeyValues(rows, noAnsi, ["Market", "Tier"]);
                return ExitCodes.Success;
            }

            string merged = DevCli.Commands.StorePricing.MergePricing(
                pricingEl.ValueKind == System.Text.Json.JsonValueKind.Object ? pricingEl : default, markets, baseTier);
            if (dryRun)
            {
                Console.WriteLine("[dry-run] merged pricing (nothing sent):");
                Console.WriteLine(merged);
                return ExitCodes.Success;
            }

            string full = DevCli.Commands.StorePricing.ApplyPricing(sub.RootElement, merged);
            string tmp = Path.Combine(Path.GetTempPath(), "deco-pricing-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(tmp, full, new UTF8Encoding(false));
            try
            {
                ProcessResult put = runner.RunAsync(msstore,
                    ["submission", "updateMetadata", appId, "-p", tmp, "--output-stream", "stderr"],
                    null, TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
                if (put.ExitCode != 0)
                {
                    throw new DecoException($"msstore submission update failed (exit {put.ExitCode}).", "pricing-update");
                }
            }
            finally
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                }
            }

            DevCli.Commands.CliConsole.Success($"Pricing updated on the pending draft for {appId} (uncommitted).", noAnsi);
            DevCli.Commands.CliConsole.Info("Review in Partner Center, then commit (msstore submission publish <appId>).", noAnsi);
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
    }

}
