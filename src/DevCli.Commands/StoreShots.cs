using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace DevCli.Commands;

/// <summary>
/// Port of StoreListing/StoreShots.psm1: screenshot-plan validation, PNG
/// identity (IHDR bytes, no decoder needed), manifest create/test
/// (provenance freshness, matrix, hashes, metadata, approval), approval
/// receipts, and review contact sheets + gallery. Locales come from the plan
/// (default en-US/es-ES/fr-FR); provenance inputs travel as a record so tests
/// run without git binaries.
/// </summary>
public static class StoreShots
{
    public sealed record ShotPlan(
        int SchemaVersion,
        string DeviceFamily,
        List<string> Locales,
        int MinimumWidth,
        int MinimumHeight,
        long MaximumFileSizeBytes,
        int MinimumShotsPerLocale,
        int MaximumShotsPerLocale,
        List<Shot> Shots);

    public sealed record Shot(
        string Id,
        int Order,
        string FileName,
        Dictionary<string, string> Caption,
        Dictionary<string, string> ExpectedMarkerText);

    public sealed record Provenance(
        string GitCommit,
        bool GitDirty,
        string StatusSha256,
        string? BinaryPath,
        string? BinarySha256,
        string? ProductVersion,
        string? HarnessSha256,
        Dictionary<string, string> ListingHashes);

    public static ShotPlan LoadPlan(string planPath, string[]? expectedLocales = null)
    {
        using var doc = StoreJson.Load(planPath, "Screenshot plan");
        JsonElement plan = doc.RootElement;
        int schemaVersion = plan.TryGetProperty("schemaVersion", out JsonElement sv) && sv.TryGetInt32(out int s) ? s : 0;
        string family = plan.TryGetProperty("deviceFamily", out JsonElement df) && df.ValueKind == JsonValueKind.String
            ? df.GetString() ?? string.Empty : string.Empty;
        if (schemaVersion != 1 || family != "Desktop")
        {
            throw new Core.DecoException("Screenshot plan has an unsupported schema version or device family.", "shots-plan");
        }

        var locales = plan.GetProperty("locales").EnumerateArray()
            .Select(l => l.GetProperty("tag").GetString() ?? string.Empty).ToList();
        string[] expected = expectedLocales ?? StoreJson.DefaultLocales;
        if (!locales.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new Core.DecoException(
                $"Screenshot plan locales must be exactly {string.Join(", ", expected)}.",
                "shots-plan-locales");
        }

        int minShots = plan.GetProperty("minimumShotsPerLocale").GetInt32();
        int maxShots = plan.GetProperty("maximumShotsPerLocale").GetInt32();
        var shots = plan.GetProperty("shots").EnumerateArray()
            .Select(s => new Shot(
                s.GetProperty("id").GetString() ?? string.Empty,
                s.GetProperty("order").GetInt32(),
                s.GetProperty("fileName").GetString() ?? string.Empty,
                s.GetProperty("caption").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal),
                s.GetProperty("expectedMarkerText").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal)))
            .OrderBy(s => s.Order)
            .ToList();

        if (shots.Count < minShots || shots.Count > maxShots)
        {
            throw new Core.DecoException($"Screenshot plan must contain {minShots}-{maxShots} shots per locale.", "shots-plan-count");
        }

        if (shots.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != shots.Count
            || shots.Select(s => s.FileName).Distinct(StringComparer.Ordinal).Count() != shots.Count)
        {
            throw new Core.DecoException("Screenshot plan IDs and file names must be unique.", "shots-plan-unique");
        }

        foreach (Shot shot in shots)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(shot.FileName, @"^\d{2}-[a-z0-9-]+\.png$"))
            {
                throw new Core.DecoException($"Screenshot '{shot.Id}' must use an ordered, lowercase PNG file name.", "shots-plan-name");
            }

            foreach (string locale in locales)
            {
                shot.Caption.TryGetValue(locale, out string? caption);
                shot.ExpectedMarkerText.TryGetValue(locale, out string? marker);
                if (string.IsNullOrWhiteSpace(caption) || caption.Length > 200)
                {
                    throw new Core.DecoException($"Screenshot '{shot.Id}' needs a {locale} caption of 1-200 characters.", "shots-plan-caption");
                }

                if (string.IsNullOrWhiteSpace(marker))
                {
                    throw new Core.DecoException($"Screenshot '{shot.Id}' needs an expected localized UI marker for {locale}.", "shots-plan-marker");
                }
            }

            var captions = locales.Select(l => shot.Caption[l]).ToList();
            if (captions.Distinct(StringComparer.Ordinal).Count() != captions.Count)
            {
                throw new Core.DecoException($"Screenshot '{shot.Id}' contains duplicate localized captions.", "shots-plan-caption");
            }
        }

        return new ShotPlan(
            schemaVersion, family, locales,
            plan.GetProperty("minimumWidth").GetInt32(),
            plan.GetProperty("minimumHeight").GetInt32(),
            plan.GetProperty("maximumFileSizeBytes").GetInt64(),
            minShots, maxShots, shots);
    }

    /// <summary>PNG width/height from the IHDR chunk (no decoder). Throws when not a PNG.</summary>
    public static (int Width, int Height) PngIdentity(string path)
    {
        byte[] header = new byte[33];
        using (var fs = File.OpenRead(path))
        {
            if (fs.Read(header, 0, 33) < 33)
            {
                throw new Core.DecoException($"Screenshot is not a readable PNG (too short): {path}", "shots-not-png");
            }
        }

        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        for (int i = 0; i < 8; i++)
        {
            if (header[i] != sig[i])
            {
                throw new Core.DecoException($"Screenshot is not PNG: {path}", "shots-not-png");
            }
        }

        if (Encoding.ASCII.GetString(header, 12, 4) != "IHDR")
        {
            throw new Core.DecoException($"PNG IHDR chunk missing: {path}", "shots-not-png");
        }

        int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return (width, height);
    }

    public static List<string> ExpectedRelativePaths(ShotPlan plan)
    {
        var expected = new List<string>();
        foreach (string locale in plan.Locales)
        {
            foreach (Shot shot in plan.Shots)
            {
                expected.Add($"{locale}/desktop/{shot.FileName}");
            }
        }

        expected.Sort(StringComparer.Ordinal);
        return expected;
    }

    /// <summary>Builds + writes screenshot-manifest.json. Returns (manifestJson, manifestSha).</summary>
    public static (string ManifestJson, string ManifestSha) NewManifest(
        string candidateDir, string version, string configuration, ShotPlan plan,
        string planSha256, Provenance provenance, DateTime? utcNow = null)
    {
        string screenshotsDir = Path.Combine(candidateDir, "screenshots");
        if (!Directory.Exists(screenshotsDir))
        {
            throw new Core.DecoException($"Screenshot output is missing: {screenshotsDir}", "shots-missing-dir");
        }

        List<string> expected = ExpectedRelativePaths(plan);
        List<string> actual = Directory.GetFiles(screenshotsDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(screenshotsDir, f).Replace('\\', '/'))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            var missing = expected.Except(actual, StringComparer.Ordinal).ToList();
            var extra = actual.Except(expected, StringComparer.Ordinal).ToList();
            throw new Core.DecoException(
                $"Screenshot set is incomplete or stale. Missing: [{string.Join(", ", missing)}]. Unexpected: [{string.Join(", ", extra)}].",
                "shots-matrix");
        }

        string createdUtc = (utcNow ?? DateTime.UtcNow).ToString("o");
        var imagePayloads = new List<string>();
        foreach (string locale in plan.Locales)
        {
            foreach (Shot shot in plan.Shots)
            {
                string rel = $"screenshots/{locale}/desktop/{shot.FileName}";
                string full = Path.Combine(candidateDir, "screenshots", locale, "desktop", shot.FileName);
                var info = new FileInfo(full);
                if (info.Length <= 0 || info.Length > plan.MaximumFileSizeBytes)
                {
                    throw new Core.DecoException($"Screenshot '{rel}' must be non-empty and no larger than {plan.MaximumFileSizeBytes} bytes.", "shots-size");
                }

                (int w, int h) = PngIdentity(full);
                if (w < plan.MinimumWidth || h < plan.MinimumHeight)
                {
                    throw new Core.DecoException($"Screenshot '{rel}' is {w}x{h}; Desktop requires at least {plan.MinimumWidth}x{plan.MinimumHeight}.", "shots-dims");
                }

                string sha = StoreJson.Sha256File(full);
                imagePayloads.Add(StoreJson.CanonicalWriter.Write(img =>
                {
                    img.BeginObject();
                    img.PropertyName("id"); img.Value(shot.Id);
                    img.PropertyName("locale"); img.Value(locale);
                    img.PropertyName("deviceFamily"); img.Value(plan.DeviceFamily);
                    img.PropertyName("fileName"); img.Value(shot.FileName);
                    img.PropertyName("relativePath"); img.Value(rel);
                    img.PropertyName("caption"); img.Value(shot.Caption[locale]);
                    img.PropertyName("captionReviewStatus"); img.Value("draft");
                    img.PropertyName("expectedMarkerText"); img.Value(shot.ExpectedMarkerText[locale]);
                    img.PropertyName("width"); img.Value((long)w);
                    img.PropertyName("height"); img.Value((long)h);
                    img.PropertyName("bytes"); img.Value(info.Length);
                    img.PropertyName("sha256"); img.Value(sha);
                    img.PropertyName("capturedUtc"); img.Value(info.LastWriteTimeUtc.ToString("o"));
                    img.EndObject();
                }));
            }
        }

        string payload = StoreJson.CanonicalWriter.Write(m =>
        {
            m.BeginObject();
            m.PropertyName("schemaVersion"); m.Value((long)1);
            m.PropertyName("version"); m.Value(version);
            m.PropertyName("gitCommit"); m.Value(provenance.GitCommit);
            m.PropertyName("gitDirty"); m.Value(provenance.GitDirty);
            m.PropertyName("gitStatusSha256"); m.Value(provenance.StatusSha256);
            m.PropertyName("deviceFamily"); m.Value(plan.DeviceFamily);
            m.PropertyName("locales");
            m.BeginArray();
            foreach (string l in plan.Locales)
            {
                m.Value(l);
            }

            m.EndArray();
            m.PropertyName("build");
            m.BeginObject();
            m.PropertyName("configuration"); m.Value(configuration);
            m.PropertyName("platform"); m.Value("x64");
            m.PropertyName("executable"); m.Value(provenance.BinaryPath ?? string.Empty);
            m.PropertyName("productVersion"); m.Value(provenance.ProductVersion ?? string.Empty);
            m.PropertyName("executableSha256"); m.Value(provenance.BinarySha256 ?? string.Empty);
            m.PropertyName("harness"); m.Value("Tests/UI/StoreShotsTests.cs");
            m.PropertyName("harnessSha256"); m.Value(provenance.HarnessSha256 ?? string.Empty);
            m.EndObject();
            m.PropertyName("shotPlanSha256"); m.Value(planSha256);
            m.PropertyName("listingSourceHashes");
            m.BeginObject();
            foreach ((string k, string v) in provenance.ListingHashes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                m.PropertyName(k); m.Value(v);
            }

            m.EndObject();
            m.PropertyName("createdUtc"); m.Value(createdUtc);
            m.PropertyName("images");
            m.BeginArray();
            foreach (string ip in imagePayloads)
            {
                m.Raw(ip);
            }

            m.EndArray();
            m.EndObject();
        });

        string hash = StoreJson.Sha256Text(payload);
        string final = payload[..^1] + ",\"manifestSha256\":\"" + hash + "\"}";
        string manifestPath = Path.Combine(candidateDir, "screenshot-manifest.json");
        File.WriteAllText(manifestPath, PrettyJson(final) + Environment.NewLine, new UTF8Encoding(false));
        return (final, hash);
    }

    internal static string PrettyJson(string compact)
    {
        using var doc = JsonDocument.Parse(compact);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Recomputes the manifest hash from file bytes (document order).</summary>
    public static string RecomputeManifestHash(string manifestPath)
    {
        using var doc = StoreJson.Load(manifestPath, "Screenshot manifest");
        string canonical = StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.NameEquals("manifestSha256"))
                {
                    continue;
                }

                w.PropertyName(p.Name);
                StoreJson.WriteElement(w, p.Value);
            }

            w.EndObject();
        });
        return StoreJson.Sha256Text(canonical);
    }

    public sealed record TestedManifest(JsonElement Manifest, string ManifestSha);

    /// <summary>
    /// Byte-identical screenshot groups ("locale/file", sha256 match) for
    /// the no-duplicate-screenshots rule. Pure and tested.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> FindDuplicateImages(JsonElement manifest)
    {
        var bySha = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (manifest.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("images", out JsonElement imgs)
            || imgs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (JsonElement im in imgs.EnumerateArray())
        {
            if (im.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string sha = im.TryGetProperty("sha256", out JsonElement s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(sha))
            {
                continue;
            }

            string loc = im.TryGetProperty("locale", out JsonElement l) && l.ValueKind == JsonValueKind.String
                ? l.GetString() ?? string.Empty : string.Empty;
            string fn = im.TryGetProperty("fileName", out JsonElement f) && f.ValueKind == JsonValueKind.String
                ? f.GetString() ?? string.Empty : string.Empty;
            string label = string.IsNullOrWhiteSpace(loc) ? fn : loc + "/" + fn;
            if (!bySha.TryGetValue(sha, out List<string>? list))
            {
                list = [];
                bySha[sha] = list;
            }

            list.Add(label);
        }

        return bySha.Values
            .Where(l => l.Count > 1)
            .Select(l => (IReadOnlyList<string>)l.AsReadOnly())
            .ToList();
    }

    /// <summary>Full gate: schema/version, provenance freshness, manifest hash, matrix, file hashes, metadata, approval.</summary>
    public static TestedManifest TestManifest(
        string candidateDir, string version, string configuration, ShotPlan plan,
        string planSha256, Provenance provenance, bool requireApproval)
    {
        string manifestPath = Path.Combine(candidateDir, "screenshot-manifest.json");
        using var doc = StoreJson.Load(manifestPath, "Screenshot manifest");
        JsonElement manifest = doc.RootElement;

        int schemaVersion = manifest.TryGetProperty("schemaVersion", out JsonElement sv) && sv.TryGetInt32(out int s) ? s : 0;
        string mversion = manifest.TryGetProperty("version", out JsonElement mv) && mv.ValueKind == JsonValueKind.String
            ? mv.GetString() ?? string.Empty : string.Empty;
        if (schemaVersion != 1 || mversion != version)
        {
            throw new Core.DecoException("Screenshot manifest schema or version does not match this candidate.", "shots-manifest");
        }

        string Str(string name) => manifest.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty : string.Empty;
        if (Str("gitCommit") != provenance.GitCommit
            || Str("gitStatusSha256") != provenance.StatusSha256
            || Str("shotPlanSha256") != planSha256)
        {
            throw new Core.DecoException("Screenshot provenance is stale: commit, worktree, or plan changed.", "shots-stale");
        }

        JsonElement build = manifest.GetProperty("build");
        string BStr(string n) => build.TryGetProperty(n, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty : string.Empty;
        if (BStr("executableSha256") != (provenance.BinarySha256 ?? string.Empty)
            || BStr("configuration") != configuration
            || BStr("productVersion") != (provenance.ProductVersion ?? string.Empty)
            || BStr("harnessSha256") != (provenance.HarnessSha256 ?? string.Empty))
        {
            throw new Core.DecoException("Screenshot provenance is stale: harness or app binary changed.", "shots-stale");
        }

        foreach (string locale in plan.Locales)
        {
            string expected = provenance.ListingHashes.TryGetValue(locale, out string? h) ? h : string.Empty;
            string actualHash = manifest.TryGetProperty("listingSourceHashes", out JsonElement lh)
                && lh.TryGetProperty(locale, out JsonElement le) && le.ValueKind == JsonValueKind.String
                ? le.GetString() ?? string.Empty : string.Empty;
            if (actualHash != expected)
            {
                throw new Core.DecoException($"Screenshot approval is stale because the {locale} listing source changed.", "shots-stale-listing");
            }
        }

        if (RecomputeManifestHash(manifestPath) != Str("manifestSha256"))
        {
            throw new Core.DecoException("Screenshot manifest hash is invalid.", "shots-hash");
        }

        var images = manifest.GetProperty("images").EnumerateArray().ToList();
        if (images.Count != plan.Locales.Count * plan.Shots.Count)
        {
            throw new Core.DecoException("Screenshot manifest does not contain the exact locale and shot matrix.", "shots-matrix");
        }

        var expectedPaths = ExpectedRelativePaths(plan).Select(p => "screenshots/" + p).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var manifestPaths = images.Select(i => i.GetProperty("relativePath").GetString() ?? string.Empty)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (!manifestPaths.SequenceEqual(expectedPaths, StringComparer.Ordinal))
        {
            throw new Core.DecoException("Screenshot manifest entries do not match the exact locale and shot matrix.", "shots-matrix");
        }

        var shotsById = plan.Shots.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (JsonElement image in images)
        {
            string rel = image.GetProperty("relativePath").GetString() ?? string.Empty;
            string full = Path.Combine(candidateDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full) || StoreJson.Sha256File(full) != (image.GetProperty("sha256").GetString() ?? string.Empty).ToLowerInvariant())
            {
                throw new Core.DecoException($"Screenshot hash mismatch or missing file: {rel}", "shots-file");
            }

            string id = image.GetProperty("id").GetString() ?? string.Empty;
            string locale = image.GetProperty("locale").GetString() ?? string.Empty;
            if (!shotsById.TryGetValue(id, out Shot? shot)
                || !plan.Locales.Contains(locale, StringComparer.Ordinal)
                || (image.GetProperty("caption").GetString() ?? string.Empty) != shot.Caption[locale]
                || (image.GetProperty("fileName").GetString() ?? string.Empty) != shot.FileName
                || (image.GetProperty("expectedMarkerText").GetString() ?? string.Empty) != shot.ExpectedMarkerText[locale]
                || (image.GetProperty("deviceFamily").GetString() ?? string.Empty) != plan.DeviceFamily)
            {
                throw new Core.DecoException($"Screenshot metadata differs from the current plan: {rel}", "shots-meta");
            }

            long bytes = new FileInfo(full).Length;
            if (bytes != image.GetProperty("bytes").GetInt64() || bytes > plan.MaximumFileSizeBytes)
            {
                throw new Core.DecoException($"Screenshot size metadata is invalid: {rel}", "shots-size");
            }

            (int w, int h) = PngIdentity(full);
            if (w < plan.MinimumWidth || h < plan.MinimumHeight
                || w != image.GetProperty("width").GetInt32() || h != image.GetProperty("height").GetInt32())
            {
                throw new Core.DecoException($"Screenshot format or dimensions are invalid: {rel}", "shots-dims");
            }
        }

        string approvalPath = Path.Combine(candidateDir, "review", "approval.json");
        if (requireApproval && !File.Exists(approvalPath))
        {
            throw new Core.DecoException("Screenshot approval is missing. Review the locale galleries, then run deco store approve --reviewer <name>.", "shots-approval-missing");
        }

        if (File.Exists(approvalPath))
        {
            using var approval = StoreJson.Load(approvalPath, "Screenshot approval");
            JsonElement a = approval.RootElement;
            string AStr(string n) => a.TryGetProperty(n, out JsonElement e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() ?? string.Empty : string.Empty;
            int AInt(string n) => a.TryGetProperty(n, out JsonElement e) && e.TryGetInt32(out int v) ? v : -1;
            bool ok = !string.IsNullOrWhiteSpace(AStr("approvedBy"))
                && AStr("screenshotManifestSha256") == Str("manifestSha256")
                && AStr("shotPlanSha256") == planSha256
                && AStr("reviewScope") == "All localized screenshots and captions in the candidate manifest"
                && AInt("localeCount") == plan.Locales.Count
                && AInt("screenshotCount") == images.Count;
            if (ok)
            {
                foreach (string locale in plan.Locales)
                {
                    string expected = provenance.ListingHashes.TryGetValue(locale, out string? h) ? h : string.Empty;
                    string got = a.TryGetProperty("listingSourceHashes", out JsonElement lh)
                        && lh.TryGetProperty(locale, out JsonElement le) && le.ValueKind == JsonValueKind.String
                        ? le.GetString() ?? string.Empty : string.Empty;
                    if (got != expected)
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (!ok)
            {
                throw new Core.DecoException("Screenshot approval does not match the current manifest, plan, or reviewer.", "shots-approval");
            }
        }

        return new TestedManifest(manifest.Clone(), Str("manifestSha256"));
    }

    public sealed record Approval(string ApprovedBy, string ApprovedUtc, int ScreenshotCount);

    public static Approval NewApproval(
        string candidateDir, TestedManifest tested, ShotPlan plan, string planSha256,
        Dictionary<string, string> listingHashes, string reviewer)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            throw new Core.DecoException("Reviewer identity cannot be empty or whitespace.", "shots-reviewer", Core.ExitCodes.UsageOrAuthNeeded);
        }

        if (reviewer.Trim().Length > 120)
        {
            throw new Core.DecoException("Reviewer identity must be 120 characters or fewer.", "shots-reviewer", Core.ExitCodes.UsageOrAuthNeeded);
        }

        string reviewDir = Path.Combine(candidateDir, "review");
        if (!File.Exists(Path.Combine(reviewDir, "index.html")))
        {
            throw new Core.DecoException($"Review gallery is missing: {reviewDir}", "shots-review-missing");
        }

        foreach (string locale in plan.Locales)
        {
            if (!File.Exists(Path.Combine(reviewDir, $"{locale}-contact-sheet.png")))
            {
                throw new Core.DecoException($"Review contact sheet is missing for {locale}: {reviewDir}", "shots-review-missing");
            }
        }

        var approval = new
        {
            schemaVersion = 1,
            approvedBy = reviewer.Trim(),
            approvedUtc = DateTime.UtcNow.ToString("o"),
            screenshotManifestSha256 = tested.ManifestSha,
            shotPlanSha256 = planSha256,
            listingSourceHashes = listingHashes,
            reviewScope = "All localized screenshots and captions in the candidate manifest",
            localeCount = plan.Locales.Count,
            screenshotCount = tested.Manifest.GetProperty("images").GetArrayLength(),
        };
        string json = JsonSerializer.Serialize(approval, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(reviewDir, "approval.json"), json + Environment.NewLine, new UTF8Encoding(false));
        return new Approval(reviewer.Trim(), approval.approvedUtc, approval.screenshotCount);
    }

    /// <summary>Contact sheets (3-up grid + captions) + simple gallery index.html per locale.</summary>
    public static void WriteReviewAssets(string candidateDir, JsonElement manifest, List<string> locales)
    {
        string reviewDir = Path.Combine(candidateDir, "review");
        Directory.CreateDirectory(reviewDir);

        using var nameFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 12);
        var images = manifest.GetProperty("images").EnumerateArray().ToList();
        foreach (string locale in locales)
        {
            var localeImages = images
                .Where(i => (i.GetProperty("locale").GetString() ?? string.Empty) == locale)
                .OrderBy(i => i.GetProperty("fileName").GetString() ?? string.Empty, StringComparer.Ordinal)
                .ToList();
            const int cellW = 520, cellH = 350;
            int rows = Math.Max(1, (localeImages.Count + 2) / 3);
            using var sheet = new SKBitmap(cellW * 3, cellH * rows);
            using (var canvas = new SKCanvas(sheet))
            {
                canvas.Clear(SKColors.White);
                using var paint = new SKPaint { IsAntialias = true };
                using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                var font = nameFont;
                for (int index = 0; index < localeImages.Count; index++)
                {
                    JsonElement entry = localeImages[index];
                    string rel = entry.GetProperty("relativePath").GetString() ?? string.Empty;
                    using var shot = SKBitmap.Decode(Path.Combine(candidateDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                    int x = (index % 3) * cellW + 12;
                    int y = (index / 3) * cellH + 12;
                    if (shot is not null)
                    {
                        canvas.DrawBitmap(shot, new SKRect(x, y, x + 496, y + 279),
                            new SKSamplingOptions(SKCubicResampler.Mitchell));
                    }

                    string caption = (entry.GetProperty("fileName").GetString() ?? string.Empty)
                        + " - " + (entry.GetProperty("caption").GetString() ?? string.Empty);
                    using var blob = SKTextBlob.Create(caption, font);
                    canvas.DrawText(blob, x, y + 285 + 16, text);
                }
            }

            using var sheetImg = SKImage.FromBitmap(sheet);
            using var data = sheetImg.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(Path.Combine(reviewDir, $"{locale}-contact-sheet.png"));
            data.SaveTo(fs);
        }

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Store screenshot review</title>");
        sb.Append("<style>body{font:16px Segoe UI,sans-serif;margin:2rem}figure{display:inline-block;vertical-align:top;width:min(48%,700px);margin:1%}img{max-width:100%;height:auto;border:1px solid #888}</style></head><body>");
        foreach (string locale in locales)
        {
            sb.Append($"<section><h2>{System.Net.WebUtility.HtmlEncode(locale)}</h2><p><a href=\"{locale}-contact-sheet.png\">Contact sheet</a></p>");
            foreach (JsonElement entry in images
                .Where(i => (i.GetProperty("locale").GetString() ?? string.Empty) == locale)
                .OrderBy(i => i.GetProperty("fileName").GetString() ?? string.Empty, StringComparer.Ordinal))
            {
                string caption = entry.GetProperty("caption").GetString() ?? string.Empty;
                string name = entry.GetProperty("fileName").GetString() ?? string.Empty;
                string src = "../" + (entry.GetProperty("relativePath").GetString() ?? string.Empty);
                sb.Append($"<figure><img src=\"{src}\" alt=\"{System.Net.WebUtility.HtmlEncode(caption)}\"><figcaption><strong>{System.Net.WebUtility.HtmlEncode(name)}</strong> - {System.Net.WebUtility.HtmlEncode(caption)}</figcaption></figure>");
            }

            sb.Append("</section>");
        }

        sb.Append("<p>Draft preview only. A human must review every image and localized caption.</p></body></html>");
        File.WriteAllText(Path.Combine(reviewDir, "index.html"), sb.ToString(), new UTF8Encoding(false));
    }
}
