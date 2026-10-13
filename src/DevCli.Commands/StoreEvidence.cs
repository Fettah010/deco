using System.Text;
using System.Text.Json;

namespace DevCli.Commands;

/// <summary>
/// Port of ReleasePipeline/ReleaseEvidence.psm1: assembles the
/// release-evidence bundle (packages + listing preview + screenshots/review +
/// evidence manifest) under Releases/StoreEvidence/&lt;version&gt;. The
/// listing bundle and tested shot manifest arrive already validated
/// (deco store listings / store shots); this unit copies + hashes + assembles.
/// </summary>
public static class StoreEvidence
{
    public sealed record Input(
        string RepoRoot,
        string ReleaseManifestPath,
        string OutputDir,
        string ListingVersion,
        string CandidateDir,
        string WackStatus,
        string WackReason,
        string UiStatus,
        string UiReason,
        bool Clean,
        string AppId,
        string GitSha,
        bool WorktreeClean,
        string ListingSourceDir);

    public sealed record EvidencePaths(string OutputDir, string ManifestPath);

    public static void ValidateStatus(string value, string name)
    {
        if (value is not ("passed" or "failed" or "skipped" or "not-run"))
        {
            throw new Core.DecoException(
                $"{name} must be passed|failed|skipped|not-run, got '{value}'.",
                "store-bad-status",
                Core.ExitCodes.UsageOrAuthNeeded);
        }
    }

    public static string GuardOutputDir(string repoRoot, string outputDir)
    {
        string outRoot = Path.GetFullPath(Path.Combine(repoRoot, "Releases", "StoreEvidence")).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string outPath = Path.GetFullPath(outputDir);
        if (!outPath.StartsWith(outRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new Core.DecoException($"Evidence output must be inside '{outRoot}'.", "store-evidence-path");
        }

        return outPath;
    }

    public static EvidencePaths Assemble(
        Input input, StoreListingBundle.Bundle bundle, StoreShots.TestedManifest? testedShots)
    {
        ValidateStatus(input.WackStatus, "WackStatus");
        ValidateStatus(input.UiStatus, "PackagedUiSmokeStatus");
        string outPath = GuardOutputDir(input.RepoRoot, input.OutputDir);

        if (Directory.Exists(outPath))
        {
            if (!input.Clean)
            {
                throw new Core.DecoException($"Evidence output already exists: {outPath}. Choose another output path or pass --clean.", "store-evidence-exists");
            }

            Directory.Delete(outPath, recursive: true);
        }

        Directory.CreateDirectory(outPath);
        using var releaseDoc = StoreJson.Load(input.ReleaseManifestPath, "Release manifest");
        JsonElement release = releaseDoc.RootElement;
        string version = release.TryGetProperty("version", out JsonElement rv) && rv.ValueKind == JsonValueKind.String
            ? rv.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new Core.DecoException("Release manifest has no version.", "store-release-manifest");
        }

        string manifestDir = Path.GetDirectoryName(Path.GetFullPath(input.ReleaseManifestPath)) ?? input.RepoRoot;
        string packageDir = Path.Combine(outPath, "packages");
        Directory.CreateDirectory(packageDir);
        var packageEvidence = new List<(string Name, long Bytes, string Sha)>();
        if (release.TryGetProperty("artifacts", out JsonElement artifacts) && artifacts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement artifact in artifacts.EnumerateArray())
            {
                string name = artifact.TryGetProperty("name", out JsonElement an) && an.ValueKind == JsonValueKind.String
                    ? an.GetString() ?? string.Empty : string.Empty;
                string ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext is not (".msixbundle" or ".msixupload"))
                {
                    throw new Core.DecoException($"Release manifest contains an unexpected package artifact name '{name}'.", "store-artifact");
                }

                string sourcePackage = Path.Combine(manifestDir, name);
                if (!File.Exists(sourcePackage))
                {
                    throw new Core.DecoException($"Package listed in release manifest is missing: {sourcePackage}", "store-artifact-missing");
                }

                string actualHash = StoreJson.Sha256File(sourcePackage);
                if (artifact.TryGetProperty("sha256", out JsonElement ah) && ah.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(ah.GetString())
                    && !string.Equals(ah.GetString(), actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Core.DecoException($"Package '{name}' SHA-256 does not match the release manifest.", "store-artifact-hash");
                }

                File.Copy(sourcePackage, Path.Combine(packageDir, name));
                packageEvidence.Add((name, new FileInfo(sourcePackage).Length, actualHash));
            }
        }

        if (packageEvidence.Count == 0)
        {
            throw new Core.DecoException("Release manifest contains no package artifacts.", "store-artifact");
        }

        string listingDir = Path.Combine(outPath, "listing");
        Directory.CreateDirectory(listingDir);
        string sourceDir = string.IsNullOrWhiteSpace(input.ListingSourceDir)
            ? Path.Combine(input.RepoRoot, "Store", "ListingSource")
            : input.ListingSourceDir;
        var listingSourceHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in new[] { "listing.schema.json", "release-notes.schema.json", "ScreenshotPlan.json" })
        {
            string p = Path.Combine(sourceDir, f);
            if (File.Exists(p))
            {
                listingSourceHashes[f] = StoreJson.Sha256File(p);
            }
        }

        var localeRows = new List<string>();
        foreach (StoreListingBundle.LocaleEntry entry in bundle.Locales)
        {
            string listingPath = Path.Combine(sourceDir, entry.Locale, "listing.json");
            string notesPath = Path.Combine(sourceDir, "release-notes", input.ListingVersion, entry.Locale + ".json");
            string listingJson = File.ReadAllText(listingPath, Encoding.UTF8);
            string notesJson = File.Exists(notesPath) ? File.ReadAllText(notesPath, Encoding.UTF8) : "{}";
            listingSourceHashes[$"{entry.Locale}/listing.json"] = StoreJson.Sha256Text(listingJson);
            listingSourceHashes[$"release-notes/{input.ListingVersion}/{entry.Locale}.json"] = StoreJson.Sha256Text(notesJson);
            localeRows.Add(StoreJson.CanonicalWriter.Write(w =>
            {
                w.BeginObject();
                w.PropertyName("locale"); w.Value(entry.Locale);
                w.PropertyName("releaseNotesVersion"); w.Value(input.ListingVersion);
                w.PropertyName("reviewStatus"); w.Value(StatusOf(entry.Listing));
                w.PropertyName("reviewedBy"); w.Value(ReviewerOf(entry.Listing));
                w.PropertyName("listing"); w.Raw(Compact(listingJson));
                w.PropertyName("releaseNotes"); w.Value(notesJson);
                w.PropertyName("listingSha256"); w.Value(StoreJson.Sha256Text(listingJson));
                w.PropertyName("releaseNotesSha256"); w.Value(StoreJson.Sha256Text(notesJson));
                w.EndObject();
            }));
        }

        string preview = StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            w.PropertyName("schemaVersion"); w.Value((long)1);
            w.PropertyName("version"); w.Value(version);
            w.PropertyName("listingVersion"); w.Value(input.ListingVersion);
            w.PropertyName("generatedUtc"); w.Value(DateTime.UtcNow.ToString("o"));
            w.PropertyName("status"); w.Value(bundle.IsApproved ? "approved" : "draft");
            w.PropertyName("reviewIssues");
            w.BeginArray();
            foreach (string issue in bundle.ReviewIssues)
            {
                w.Value(issue);
            }

            w.EndArray();
            w.PropertyName("locales");
            w.BeginArray();
            foreach (string row in localeRows)
            {
                w.Raw(row);
            }

            w.EndArray();
            w.EndObject();
        });
        File.WriteAllText(Path.Combine(listingDir, "listing-preview.json"), Pretty(preview), new UTF8Encoding(false));

        var screenshotRows = new List<string>();
        string screenshotStatus = "not-provided";
        string screenshotReason = "No approved interactive-desktop screenshot candidate was supplied to this run.";
        string? approvalReviewer = null;
        string? approvalUtc = null;
        string? approvalManifestSha = null;
        if (testedShots is not null)
        {
            screenshotStatus = "approved";
            screenshotReason = string.Empty;
            string shotsDir = Path.Combine(outPath, "screenshots");
            foreach (JsonElement image in testedShots.Manifest.GetProperty("images").EnumerateArray())
            {
                string rel = image.GetProperty("relativePath").GetString() ?? string.Empty;
                string src = Path.Combine(input.CandidateDir, rel.Replace('/', Path.DirectorySeparatorChar));
                string dst = Path.Combine(shotsDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                screenshotRows.Add(StoreJson.CanonicalWriter.Write(w =>
                {
                    w.BeginObject();
                    w.PropertyName("locale"); w.Value(image.GetProperty("locale").GetString());
                    w.PropertyName("id"); w.Value(image.GetProperty("id").GetString());
                    w.PropertyName("path"); w.Value(rel);
                    w.PropertyName("sha256"); w.Value(StoreJson.Sha256File(dst));
                    w.PropertyName("caption"); w.Value(image.GetProperty("caption").GetString());
                    w.EndObject();
                }));
            }

            string reviewTarget = Path.Combine(outPath, "review");
            Directory.CreateDirectory(reviewTarget);
            foreach (string reviewName in new[] { "index.html", "approval.json", "screenshot-manifest.json" })
            {
                string reviewSource = reviewName == "screenshot-manifest.json"
                    ? Path.Combine(input.CandidateDir, "screenshot-manifest.json")
                    : Path.Combine(input.CandidateDir, "review", reviewName);
                if (!File.Exists(reviewSource))
                {
                    throw new Core.DecoException($"Approved screenshot candidate is missing review evidence '{reviewSource}'.", "store-review-missing");
                }

                File.Copy(reviewSource, Path.Combine(reviewTarget, reviewName), overwrite: true);
            }

            foreach (string sheet in Directory.GetFiles(Path.Combine(input.CandidateDir, "review"), "*-contact-sheet.png"))
            {
                File.Copy(sheet, Path.Combine(reviewTarget, Path.GetFileName(sheet)), overwrite: true);
            }

            using var approvalDoc = StoreJson.Load(Path.Combine(input.CandidateDir, "review", "approval.json"), "Screenshot approval");
            approvalReviewer = approvalDoc.RootElement.TryGetProperty("approvedBy", out JsonElement ab) ? ab.GetString() : null;
            approvalUtc = approvalDoc.RootElement.TryGetProperty("approvedUtc", out JsonElement au) ? au.GetString() : null;
            approvalManifestSha = approvalDoc.RootElement.TryGetProperty("screenshotManifestSha256", out JsonElement am) ? am.GetString() : null;
        }

        string evidenceReleaseManifest = Path.Combine(outPath, "release-manifest.json");
        File.Copy(input.ReleaseManifestPath, evidenceReleaseManifest, overwrite: true);

        string uiReason = input.UiReason;
        if (string.IsNullOrWhiteSpace(uiReason) && input.UiStatus is "not-run" or "skipped")
        {
            uiReason = "Packaged navigation smoke requires a locally installed and sideloaded release candidate.";
        }

        string wackReason = input.WackReason;
        if (string.IsNullOrWhiteSpace(wackReason) && input.WackStatus == "skipped")
        {
            wackReason = "WACK was explicitly skipped for this package-only workflow run.";
        }

        string manifest = StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            w.PropertyName("schemaVersion"); w.Value((long)1);
            w.PropertyName("artifactType"); w.Value($"{input.AppId}-store-release-evidence");
            w.PropertyName("generatedUtc"); w.Value(DateTime.UtcNow.ToString("o"));
            w.PropertyName("release");
            w.BeginObject();
            w.PropertyName("version"); w.Value(version);
            w.PropertyName("listingSourceVersion"); w.Value(input.ListingVersion);
            w.PropertyName("versionQuad"); w.Value(Str(release, "versionQuad"));
            w.PropertyName("sourceCommit"); w.Value(input.GitSha);
            w.PropertyName("sourceBranch"); w.Value(Str(release, "branch"));
            w.PropertyName("worktreeClean"); w.Value(input.WorktreeClean);
            w.PropertyName("releaseManifestSha256"); w.Value(StoreJson.Sha256File(input.ReleaseManifestPath));
            w.EndObject();
            w.PropertyName("checks");
            w.BeginObject();
            w.PropertyName("unitTests"); w.Raw(Compact(Opt(release, "checks", "unitTests") ?? "{\"status\":\"not-recorded\"}"));
            w.PropertyName("packageBuild"); w.Raw(Compact(Opt(release, "checks", "packageBuild") ?? "{\"status\":\"not-recorded\"}"));
            w.PropertyName("wack");
            w.BeginObject();
            w.PropertyName("status"); w.Value(input.WackStatus);
            w.PropertyName("reason"); w.Value(wackReason);
            w.EndObject();
            w.PropertyName("packagedUiSmoke");
            w.BeginObject();
            w.PropertyName("status"); w.Value(input.UiStatus);
            w.PropertyName("reason"); w.Value(uiReason);
            w.EndObject();
            w.PropertyName("listingValidation");
            w.BeginObject();
            w.PropertyName("status"); w.Value(bundle.IsApproved ? "approved" : "draft");
            w.PropertyName("locales");
            w.BeginArray();
            foreach (StoreListingBundle.LocaleEntry entry in bundle.Locales)
            {
                w.Value(entry.Locale);
            }

            w.EndArray();
            w.PropertyName("issues");
            w.BeginArray();
            foreach (string issue in bundle.ReviewIssues)
            {
                w.Value(issue);
            }

            w.EndArray();
            w.EndObject();
            w.PropertyName("screenshots");
            w.BeginObject();
            w.PropertyName("status"); w.Value(screenshotStatus);
            w.PropertyName("reason"); w.Value(screenshotReason);
            w.PropertyName("count"); w.Value((long)screenshotRows.Count);
            w.EndObject();
            w.PropertyName("submission");
            w.BeginObject();
            w.PropertyName("status"); w.Value(Str(release, "submission") is string s && !string.IsNullOrWhiteSpace(s) ? s : "not-submitted");
            w.PropertyName("submissionId"); w.Value(Str(release, "submissionId"));
            w.PropertyName("publishMode"); w.Value(Str(release, "publishMode"));
            w.PropertyName("partnerCenterStatus"); w.Value(Str(release, "status"));
            w.EndObject();
            w.EndObject();
            w.PropertyName("approvals");
            w.BeginObject();
            w.PropertyName("listingLocales");
            w.BeginArray();
            foreach (StoreListingBundle.LocaleEntry entry in bundle.Locales)
            {
                w.BeginObject();
                w.PropertyName("locale"); w.Value(entry.Locale);
                w.PropertyName("status"); w.Value(StatusOf(entry.Listing));
                w.PropertyName("reviewedBy"); w.Value(ReviewerOf(entry.Listing));
                w.EndObject();
            }

            w.EndArray();
            w.PropertyName("screenshots");
            if (testedShots is not null)
            {
                w.BeginObject();
                w.PropertyName("status"); w.Value("approved");
                w.PropertyName("reviewer"); w.Value(approvalReviewer ?? string.Empty);
                w.PropertyName("approvedUtc"); w.Value(approvalUtc ?? string.Empty);
                w.PropertyName("manifestSha256"); w.Value(approvalManifestSha ?? string.Empty);
                w.EndObject();
            }
            else
            {
                w.BeginObject();
                w.PropertyName("status"); w.Value("not-provided");
                w.EndObject();
            }

            w.EndObject();
            w.PropertyName("contentHashes");
            w.BeginObject();
            w.PropertyName("listingSources");
            w.BeginObject();
            foreach ((string k, string v) in listingSourceHashes)
            {
                w.PropertyName(k); w.Value(v);
            }

            w.EndObject();
            w.PropertyName("screenshots");
            w.BeginArray();
            foreach (string row in screenshotRows)
            {
                w.Raw(row);
            }

            w.EndArray();
            w.PropertyName("packages");
            w.BeginArray();
            foreach ((string name, long bytes, string sha) in packageEvidence)
            {
                w.BeginObject();
                w.PropertyName("name"); w.Value(name);
                w.PropertyName("bytes"); w.Value(bytes);
                w.PropertyName("sha256"); w.Value(sha);
                w.EndObject();
            }

            w.EndArray();
            w.EndObject();
            w.PropertyName("assetDisposition");
            w.BeginObject();
            w.PropertyName("packages"); w.Value("built-for-this-candidate");
            w.PropertyName("listings"); w.Value("preview-of-tracked-source");
            w.PropertyName("screenshots"); w.Value(screenshotStatus == "approved" ? "approved-candidate-included" : "not-provided; no inherited screenshots assumed");
            w.PropertyName("inheritedAssets");
            w.BeginArray();
            w.EndArray();
            w.EndObject();
            w.PropertyName("artifacts");
            w.BeginArray();
            w.BeginObject();
            w.PropertyName("path"); w.Value("listing/listing-preview.json");
            w.PropertyName("sha256"); w.Value(StoreJson.Sha256File(Path.Combine(listingDir, "listing-preview.json")));
            w.EndObject();
            w.BeginObject();
            w.PropertyName("path"); w.Value("release-manifest.json");
            w.PropertyName("sha256"); w.Value(StoreJson.Sha256File(evidenceReleaseManifest));
            w.EndObject();
            foreach ((string name, _, string sha) in packageEvidence)
            {
                w.BeginObject();
                w.PropertyName("path"); w.Value("packages/" + name);
                w.PropertyName("sha256"); w.Value(sha);
                w.EndObject();
            }

            foreach (string row in screenshotRows)
            {
                using var rowDoc = JsonDocument.Parse(row);
                string p = rowDoc.RootElement.GetProperty("path").GetString() ?? string.Empty;
                w.BeginObject();
                w.PropertyName("path"); w.Value("screenshots/" + p.Replace("screenshots/", string.Empty));
                w.PropertyName("sha256"); w.Value(rowDoc.RootElement.GetProperty("sha256").GetString());
                w.EndObject();
            }

            if (Directory.Exists(Path.Combine(outPath, "review")))
            {
                foreach (string f in Directory.GetFiles(Path.Combine(outPath, "review")).OrderBy(p => p, StringComparer.Ordinal))
                {
                    w.BeginObject();
                    w.PropertyName("path"); w.Value("review/" + Path.GetFileName(f));
                    w.PropertyName("sha256"); w.Value(StoreJson.Sha256File(f));
                    w.EndObject();
                }
            }

            w.EndArray();
            w.EndObject();
        });

        string manifestPath = Path.Combine(outPath, "release-evidence.json");
        File.WriteAllText(manifestPath, Pretty(manifest), new UTF8Encoding(false));
        return new EvidencePaths(outPath, manifestPath);
    }

    private static string StatusOf(JsonElement listing) =>
        listing.TryGetProperty("reviewStatus", out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty : string.Empty;

    private static string ReviewerOf(JsonElement listing) =>
        listing.TryGetProperty("reviewedBy", out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty : string.Empty;

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty : string.Empty;

    private static string? Opt(JsonElement obj, params string[] path)
    {
        JsonElement cur = obj;
        foreach (string p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur))
            {
                return null;
            }
        }

        return cur.GetRawText();
    }

    private static string Compact(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string Pretty(string compact)
    {
        using var doc = JsonDocument.Parse(compact);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
}
