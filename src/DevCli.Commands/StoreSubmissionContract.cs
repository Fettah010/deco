using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DevCli.Commands;

/// <summary>
/// Port of StoreListing/StoreSubmissionContract.psm1: submission shape
/// validation, locale-key canonicalization, offline payload planning
/// (listings text + screenshot PendingUpload entries + package entry) and the
/// upload ZIP builder. No Partner Center calls (offline-only, like the ps1).
/// </summary>
public static class StoreSubmissionContract
{
    public static readonly string[] RequestFields =
    [
        "applicationCategory", "pricing", "visibility", "targetPublishMode", "targetPublishDate",
        "listings", "hardwarePreferences", "automaticBackupEnabled", "canInstallOnRemovableMedia",
        "isGameDvrEnabled", "gamingOptions", "hasExternalInAppProducts", "meetAccessibilityGuidelines",
        "notesForCertification", "applicationPackages", "packageDeliveryOptions", "enterpriseLicensing",
        "allowMicrosoftDecideAppAvailabilityToFutureDeviceFamilies", "allowTargetFutureDeviceFamilies", "trailers",
    ];

    public static readonly string[] ResponseFields = ["id", "status", "statusDetails", "fileUploadUrl", "friendlyName"];

    public sealed record PlannedImage(
        string Locale, string Id, string FileName, string SourcePath,
        string RelativePath, string Sha256, string Caption);

    public sealed record PayloadPlan(JsonElement Payload, List<PlannedImage> Images, List<string> Locales);

    public static string CanonicalLocale(string locale, string[] supported)
    {
        foreach (string s in supported)
        {
            if (string.Equals(s, locale, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }

        throw new Core.DecoException($"Unsupported Partner Center listing locale '{locale}'.", "store-locale");
    }

    public static void AssertTopLevelShape(JsonElement submission)
    {
        var allowed = new HashSet<string>(RequestFields.Concat(ResponseFields), StringComparer.Ordinal);
        var unknown = submission.EnumerateObject().Select(p => p.Name).Where(n => !allowed.Contains(n)).ToList();
        if (unknown.Count > 0)
        {
            throw new Core.DecoException(
                $"Partner Center submission contains unsupported top-level field(s): {string.Join(", ", unknown)}. Update the contract before planning this payload.",
                "store-contract");
        }

        if (!submission.TryGetProperty("listings", out _) || !submission.TryGetProperty("applicationPackages", out _))
        {
            throw new Core.DecoException("Partner Center submission is missing required 'listings'/'applicationPackages' data.", "store-contract");
        }
    }

    public static PayloadPlan NewPayload(
        JsonElement submission, StoreListingBundle.Bundle bundle, JsonElement shotManifest,
        string candidateDir, string bundleFileName, string[] locales)
    {
        if (!bundle.IsApproved)
        {
            throw new Core.DecoException("Localized listing content is not approved; an API payload cannot be planned.", "store-not-approved");
        }

        if (string.IsNullOrWhiteSpace(bundleFileName) || Path.GetFileName(bundleFileName) != bundleFileName)
        {
            throw new Core.DecoException("BundleFileName must be a simple archive filename.", "store-bundle-name");
        }

        AssertTopLevelShape(submission);

        var bundleLocales = bundle.Locales.Select(l => CanonicalLocale(l.Locale, locales)).ToList();
        if (!bundleLocales.SequenceEqual(locales, StringComparer.Ordinal))
        {
            throw new Core.DecoException($"Approved listing bundle locales must be exactly {string.Join(", ", locales)}.", "store-locales");
        }

        var apiLocales = submission.GetProperty("listings").EnumerateObject()
            .Select(p => CanonicalLocale(p.Name, locales)).ToList();
        var apiMap = submission.GetProperty("listings").EnumerateObject()
            .ToDictionary(p => CanonicalLocale(p.Name, locales), p => p.Name, StringComparer.Ordinal);
        if (apiMap.Count != apiLocales.Count)
        {
            throw new Core.DecoException("Partner Center submission contains duplicate locale keys.", "store-locales");
        }

        var expectedKeys = locales.Select(l => l.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var actualKeys = submission.GetProperty("listings").EnumerateObject()
            .Select(p => p.Name.ToLowerInvariant()).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (!actualKeys.SequenceEqual(expectedKeys, StringComparer.Ordinal))
        {
            throw new Core.DecoException(
                $"Partner Center locale mismatch. Missing: [{string.Join(", ", expectedKeys.Except(actualKeys))}]. Extra: [{string.Join(", ", actualKeys.Except(expectedKeys))}].",
                "store-locales");
        }

        var manifestLocales = shotManifest.GetProperty("locales").EnumerateArray()
            .Select(e => CanonicalLocale(e.GetString() ?? string.Empty, locales)).ToList();
        if (!manifestLocales.SequenceEqual(locales, StringComparer.Ordinal))
        {
            throw new Core.DecoException($"Screenshot manifest locales must be exactly {string.Join(", ", locales)}.", "store-locales");
        }

        string candidateRoot = Path.GetFullPath(candidateDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var imagesByLocale = locales.ToDictionary(l => l, _ => new List<PlannedImage>(), StringComparer.Ordinal);
        var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement image in shotManifest.GetProperty("images").EnumerateArray())
        {
            string locale = CanonicalLocale(image.GetProperty("locale").GetString() ?? string.Empty, locales);
            string id = image.TryGetProperty("id", out JsonElement ie) && ie.ValueKind == JsonValueKind.String
                ? ie.GetString() ?? string.Empty : string.Empty;
            string caption = image.GetProperty("caption").GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(caption))
            {
                throw new Core.DecoException($"Screenshot manifest contains an image without a stable ID or localized caption for '{locale}'.", "store-image");
            }

            if (caption.Contains('\r') || caption.Contains('\n') || caption.Length > 200)
            {
                throw new Core.DecoException($"Screenshot caption '{id}' for '{locale}' must be a single line of at most 200 characters.", "store-image");
            }

            string rel = (image.GetProperty("relativePath").GetString() ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(rel))
            {
                throw new Core.DecoException($"Screenshot path must be relative to the candidate directory.", "store-image");
            }

            string sourcePath = Path.GetFullPath(Path.Combine(candidateRoot, rel));
            if (!sourcePath.StartsWith(candidateRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(sourcePath))
            {
                throw new Core.DecoException($"Screenshot is missing or outside the candidate directory.", "store-image");
            }

            if (string.IsNullOrWhiteSpace(image.GetProperty("sha256").GetString())
                || StoreJson.Sha256File(sourcePath) != (image.GetProperty("sha256").GetString() ?? string.Empty).ToLowerInvariant())
            {
                throw new Core.DecoException($"Screenshot does not match its manifest SHA-256.", "store-image");
            }

            string fileName = Path.GetFileName(image.GetProperty("fileName").GetString() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(fileName) || fileName != image.GetProperty("fileName").GetString()
                || !System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^[A-Za-z0-9][A-Za-z0-9._-]*\.png$"))
            {
                throw new Core.DecoException($"Screenshot filename is not a safe PNG archive filename.", "store-image");
            }

            string archiveName = $"{locale}-{fileName}";
            if (!archiveNames.Add(archiveName))
            {
                throw new Core.DecoException($"Screenshot archive filename '{archiveName}' is duplicated.", "store-image");
            }

            imagesByLocale[locale].Add(new PlannedImage(locale, id, archiveName, sourcePath, rel, (image.GetProperty("sha256").GetString() ?? string.Empty).ToLowerInvariant(), caption));
        }

        foreach (string locale in locales)
        {
            int count = imagesByLocale[locale].Count;
            if (count < 4 || count > 10)
            {
                throw new Core.DecoException($"Screenshot locale '{locale}' must contain 4-10 validated images; found {count}.", "store-image-count");
            }

            var dupes = imagesByLocale[locale].GroupBy(i => i.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0)
            {
                throw new Core.DecoException($"Screenshot locale '{locale}' contains duplicate image IDs: {string.Join(", ", dupes)}.", "store-image-count");
            }
        }

        // Round-trip through JSON for a mutable payload copy, then strip response fields.
        using var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(submission));
        var payloadJson = new System.Text.StringBuilder();
        using (var ms = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            {
                WritePayloadWithoutResponseFields(writer, payloadDoc.RootElement);
            }

            payloadJson.Append(Encoding.UTF8.GetString(ms.ToArray()));
        }

        using var mutable = JsonDocument.Parse(payloadJson.ToString());
        var edits = ApplyListingText(mutable.RootElement, bundle, apiMap, imagesByLocale, bundleFileName);
        return new PayloadPlan(edits, imagesByLocale.Values.SelectMany(l => l).ToList(), [.. locales]);
    }

    private static void WritePayloadWithoutResponseFields(Utf8JsonWriter writer, JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty p in el.EnumerateObject())
            {
                if (ResponseFields.Contains(p.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                writer.WritePropertyName(p.Name);
                WritePayloadWithoutResponseFields(writer, p.Value);
            }

            writer.WriteEndObject();
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in el.EnumerateArray())
            {
                WritePayloadWithoutResponseFields(writer, item);
            }

            writer.WriteEndArray();
        }
        else
        {
            el.WriteTo(writer);
        }
    }

    private static JsonElement ApplyListingText(
        JsonElement payload, StoreListingBundle.Bundle bundle, Dictionary<string, string> apiMap,
        Dictionary<string, List<PlannedImage>> imagesByLocale, string bundleFileName)
    {
        // Rebuild listings.perLocale.baseListing + applicationPackages via canonical writer.
        var perLocale = new Dictionary<string, string>();
        foreach (StoreListingBundle.LocaleEntry entry in bundle.Locales)
        {
            string canonical = entry.Locale;
            foreach ((string k, string v) in apiMap)
            {
                if (k == canonical)
                {
                    canonical = v;
                }
            }

            string apiKey = apiMap.First(kv => kv.Key == entry.Locale).Value;
            JsonElement listing = payload.GetProperty("listings").GetProperty(apiKey);
            if (!listing.TryGetProperty("baseListing", out _))
            {
                throw new Core.DecoException($"Partner Center listing '{apiKey}' has no baseListing object.", "store-contract");
            }

            var known = new HashSet<string>(listing.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
            var unknown = known.Where(n => n != "baseListing" && n != "platformOverrides").ToList();
            if (unknown.Count > 0)
            {
                throw new Core.DecoException($"Partner Center listing '{apiKey}' contains unsupported field(s): {string.Join(", ", unknown)}.", "store-contract");
            }

            perLocale[apiKey] = StoreJson.CanonicalWriter.Write(w =>
            {
                w.BeginObject();
                w.PropertyName("title"); w.Value(entry.Listing.GetProperty("productName").GetString());
                w.PropertyName("shortDescription"); w.Value(entry.Listing.GetProperty("shortDescription").GetString());
                w.PropertyName("description"); w.Value(entry.Listing.GetProperty("description").GetString());
                w.PropertyName("features");
                w.BeginArray();
                foreach (JsonElement f in entry.Listing.GetProperty("productFeatures").EnumerateArray())
                {
                    w.Value(f.GetProperty("text").GetString());
                }

                w.EndArray();
                w.PropertyName("releaseNotes"); w.Value(entry.ReleaseNotes.GetProperty("whatsNew").GetString());
                w.PropertyName("images");
                w.BeginArray();
                foreach (PlannedImage image in imagesByLocale[entry.Locale])
                {
                    w.BeginObject();
                    w.PropertyName("fileName"); w.Value(image.FileName);
                    w.PropertyName("fileStatus"); w.Value("PendingUpload");
                    w.PropertyName("description"); w.Value(image.Caption);
                    w.PropertyName("imageType"); w.Value("Screenshot");
                    w.EndObject();
                }

                w.EndArray();
                w.EndObject();
            });
        }

        string rebuilt = StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            foreach (JsonProperty p in payload.EnumerateObject())
            {
                if (p.NameEquals("listings"))
                {
                    w.PropertyName("listings");
                    w.BeginObject();
                    foreach (JsonProperty lp in p.Value.EnumerateObject())
                    {
                        w.PropertyName(lp.Name);
                        if (perLocale.TryGetValue(lp.Name, out string? baseJson))
                        {
                            // Merge: keep sibling props, replace baseListing.
                            w.BeginObject();
                            foreach (JsonProperty sp in lp.Value.EnumerateObject())
                            {
                                w.PropertyName(sp.Name);
                                if (sp.NameEquals("baseListing"))
                                {
                                    w.Raw(baseJson);
                                }
                                else
                                {
                                    StoreJson.WriteElement(w, sp.Value);
                                }
                            }

                            w.EndObject();
                        }
                        else
                        {
                            StoreJson.WriteElement(w, lp.Value);
                        }
                    }

                    w.EndObject();
                }
                else if (p.NameEquals("applicationPackages"))
                {
                    w.PropertyName("applicationPackages");
                    w.BeginArray();
                    w.BeginObject();
                    w.PropertyName("fileName"); w.Value(bundleFileName);
                    w.PropertyName("fileStatus"); w.Value("PendingUpload");
                    w.PropertyName("minimumDirectXVersion"); w.Value("None");
                    w.PropertyName("minimumSystemRam"); w.Value("None");
                    w.EndObject();
                    w.EndArray();
                }
                else if (p.NameEquals("pricing") && p.Value.ValueKind == JsonValueKind.Object)
                {
                    w.PropertyName("pricing");
                    w.BeginObject();
                    foreach (JsonProperty pp in p.Value.EnumerateObject())
                    {
                        if (pp.NameEquals("isAdvancedPricingModel"))
                        {
                            continue;
                        }

                        w.PropertyName(pp.Name);
                        StoreJson.WriteElement(w, pp.Value);
                    }

                    w.EndObject();
                }
                else if (p.NameEquals("packageDeliveryOptions") && p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.TryGetProperty("packageRollout", out JsonElement rollout)
                    && rollout.ValueKind == JsonValueKind.Object)
                {
                    w.PropertyName("packageDeliveryOptions");
                    w.BeginObject();
                    foreach (JsonProperty pp in p.Value.EnumerateObject())
                    {
                        if (pp.NameEquals("packageRollout"))
                        {
                            w.PropertyName("packageRollout");
                            w.BeginObject();
                            foreach (JsonProperty rp in rollout.EnumerateObject())
                            {
                                if (rp.NameEquals("packageRolloutStatus") || rp.NameEquals("fallbackSubmissionId"))
                                {
                                    continue;
                                }

                                w.PropertyName(rp.Name);
                                StoreJson.WriteElement(w, rp.Value);
                            }

                            w.EndObject();
                        }
                        else
                        {
                            w.PropertyName(pp.Name);
                            StoreJson.WriteElement(w, pp.Value);
                        }
                    }

                    w.EndObject();
                }
                else
                {
                    w.PropertyName(p.Name);
                    StoreJson.WriteElement(w, p.Value);
                }
            }

            w.EndObject();
        });

        using var doc = JsonDocument.Parse(rebuilt);
        return doc.RootElement.Clone();
    }

    public static string NewUploadZip(PayloadPlan plan, string bundlePath, string zipPath)
    {
        if (!File.Exists(bundlePath))
        {
            throw new Core.DecoException($"Submission bundle is missing: {bundlePath}", "store-bundle-missing");
        }

        if (File.Exists(zipPath))
        {
            throw new Core.DecoException($"Refusing to overwrite existing submission ZIP: {zipPath}", "store-zip-exists");
        }

        string bundleName = Path.GetFileName(bundlePath);
        if (plan.Images.Any(i => string.Equals(i.FileName, bundleName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new Core.DecoException($"Bundle filename '{bundleName}' collides with a screenshot archive filename.", "store-zip-collision");
        }

        string stage = Path.Combine(Path.GetTempPath(), "store-submission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            File.Copy(bundlePath, Path.Combine(stage, bundleName));
            foreach (PlannedImage image in plan.Images)
            {
                File.Copy(image.SourcePath, Path.Combine(stage, image.FileName));
            }

            ZipFile.CreateFromDirectory(stage, zipPath);
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch
            {
            }
        }

        return zipPath;
    }
}
