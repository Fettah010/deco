using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// Port of StoreListing/StoreListings.psm1: validates Store/ListingSource
/// (schemas, locale dirs, release notes, evidence paths, cross-locale name/
/// feature parity, translation-identity, reviewStatus approval) and assembles
/// the bundle. Locales are parameterized (default en-US/es-ES/fr-FR, first =
/// reference) instead of hardcoded. JsonDocuments are cloned; no file writes.
/// </summary>
public static partial class StoreListingBundle
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\b(New in|Novedad en|Nouveau en|Neu in|Novità in)\s+\d", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedNotesRegex();

    public sealed record LocaleEntry(string Locale, JsonElement Listing, JsonElement ReleaseNotes);

    public sealed record Bundle(string Version, List<LocaleEntry> Locales, bool IsApproved, List<string> ReviewIssues);

    /// <summary>
    /// Locales whose description embeds a versioned "New in X" note.
    /// Release notes belong in whatsNew (own Store section), never in the
    /// description. Warn-only (grandfathered listings shipped that way).
    /// Pure and tested.
    /// </summary>
    public static IReadOnlyList<string> FindEmbeddedReleaseNotes(IReadOnlyList<LocaleEntry> entries)
    {
        var hit = new List<string>();
        foreach (LocaleEntry e in entries)
        {
            string desc = e.Listing.TryGetProperty("description", out JsonElement d)
                && d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(desc) && EmbeddedNotesRegex().IsMatch(desc))
            {
                hit.Add(e.Locale);
            }
        }

        return hit;
    }

    public static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || !VersionRegex().IsMatch(version.Trim()))
        {
            throw new Core.DecoException(
                $"Invalid release version '{version}'. Use a version such as 0.6.2-beta.",
                "store-bad-version",
                Core.ExitCodes.UsageOrAuthNeeded);
        }
    }

    public static Bundle Load(string sourceDir, string version, string repoRoot, string[] locales, bool allowDraft)
    {
        ValidateVersion(version);
        version = version.Trim();
        string sourcePath = Path.GetFullPath(sourceDir);
        string repoPath = Path.GetFullPath(repoRoot);
        if (!Directory.Exists(sourcePath))
        {
            throw new Core.DecoException($"Listing source is missing: {sourcePath}", "store-source-missing");
        }

        using var listingSchema = StoreJson.Load(Path.Combine(sourcePath, "listing.schema.json"), "Listing schema");
        using var releaseSchema = StoreJson.Load(Path.Combine(sourcePath, "release-notes.schema.json"), "Release-notes schema");

        AssertLocaleDirectories(sourcePath, locales);

        string releaseDir = Path.Combine(sourcePath, "release-notes", version);
        if (!Directory.Exists(releaseDir))
        {
            throw new Core.DecoException($"Versioned release notes are missing for '{version}': {releaseDir}", "store-notes-missing");
        }

        var releaseFiles = Directory.GetFiles(releaseDir)
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var expectedReleaseFiles = locales.Select(l => l + ".json").OrderBy(n => n, StringComparer.Ordinal).ToList();
        var missingNotes = expectedReleaseFiles.Except(releaseFiles, StringComparer.Ordinal).ToList();
        var unexpectedNotes = releaseFiles.Except(expectedReleaseFiles, StringComparer.Ordinal).ToList();
        if (missingNotes.Count > 0 || unexpectedNotes.Count > 0)
        {
            throw new Core.DecoException(
                $"Release-note locale mismatch for '{version}'. Missing: [{string.Join(", ", missingNotes)}]. Unexpected: [{string.Join(", ", unexpectedNotes)}].",
                "store-notes-mismatch");
        }

        var entries = new List<LocaleEntry>();
        foreach (string locale in locales)
        {
            string listingPath = Path.Combine(sourcePath, locale, "listing.json");
            JsonElement listing = ReadAndValidate(listingPath, listingSchema.RootElement);
            if (StoreJson.RequireString(listing, "locale", listingPath) != locale)
            {
                throw new Core.DecoException($"Listing locale mismatch: '{listingPath}' declares a different locale, expected '{locale}'.", "store-locale-mismatch");
            }

            AssertEvidence(listing, repoPath, listingPath);

            string notesPath = Path.Combine(releaseDir, locale + ".json");
            JsonElement notes = ReadAndValidate(notesPath, releaseSchema.RootElement);
            if (StoreJson.RequireString(notes, "locale", notesPath) != locale)
            {
                throw new Core.DecoException($"Release-note locale mismatch: '{notesPath}' declares a different locale.", "store-locale-mismatch");
            }

            if (StoreJson.RequireString(notes, "releaseVersion", notesPath) != version)
            {
                throw new Core.DecoException($"Release-note version mismatch in '{notesPath}': expected '{version}'.", "store-version-mismatch");
            }

            entries.Add(new LocaleEntry(locale, listing.Clone(), notes.Clone()));
        }

        AssertCrossLocale(entries);
        List<string> reviewIssues = CollectReviewIssues(entries);
        if (!allowDraft && reviewIssues.Count > 0)
        {
            throw new Core.DecoException(
                $"Listing content is not approved for submission: {string.Join(" ", reviewIssues)} Use --preview to inspect drafts.",
                "store-not-approved");
        }

        return new Bundle(version, entries, reviewIssues.Count == 0, reviewIssues);
    }

    internal static JsonElement ReadAndValidate(string path, JsonElement schema)
    {
        using var doc = StoreJson.Load(path, "Listing source");
        var errors = new List<string>();
        StoreJson.Validate(doc.RootElement, schema, Path.GetFileName(path), errors);
        if (errors.Count > 0)
        {
            throw new Core.DecoException($"Schema validation failed for '{path}': {string.Join(" ", errors)}", "store-schema");
        }

        return doc.RootElement.Clone();
    }

    internal static void AssertLocaleDirectories(string sourceDir, string[] expected)
    {
        var dirs = Directory.GetDirectories(sourceDir).Select(Path.GetFileName).ToList();
        var missing = expected.Where(e => !dirs.Contains(e, StringComparer.Ordinal)).ToList();
        var unexpected = dirs.Where(d => !expected.Contains(d, StringComparer.Ordinal)
            && !string.Equals(d, "release-notes", StringComparison.Ordinal)).ToList();
        if (missing.Count > 0 || unexpected.Count > 0)
        {
            throw new Core.DecoException(
                $"Locale directory mismatch. Missing: [{string.Join(", ", missing)}]. Unexpected: [{string.Join(", ", unexpected)}]. Expected exactly: {string.Join(", ", expected)}.",
                "store-locale-dirs");
        }

        foreach (string locale in expected)
        {
            string[] entries = Directory.GetFileSystemEntries(Path.Combine(sourceDir, locale));
            if (entries.Length != 1 || Directory.Exists(entries[0])
                || !string.Equals(Path.GetFileName(entries[0]), "listing.json", StringComparison.Ordinal))
            {
                throw new Core.DecoException(
                    $"Locale '{locale}' must contain exactly one listing.json file; remove duplicate or unexpected locale data.",
                    "store-locale-dirs");
        }
        }
    }

    internal static void AssertEvidence(JsonElement listing, string repoRoot, string path)
    {
        if (!listing.TryGetProperty("productFeatures", out JsonElement features)
            || features.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        string root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (JsonElement feature in features.EnumerateArray())
        {
            string id = feature.TryGetProperty("id", out JsonElement fid) && fid.ValueKind == JsonValueKind.String
                ? fid.GetString() ?? "?" : "?";
            if (!feature.TryGetProperty("evidence", out JsonElement evidence)
                || evidence.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement reference in evidence.EnumerateArray())
            {
                string r = reference.ValueKind == JsonValueKind.String ? reference.GetString() ?? string.Empty : string.Empty;
                string rel = r.Split('#')[0].Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(rel))
                {
                    throw new Core.DecoException($"Feature '{id}' in '{path}' has rooted evidence path '{r}'.", "store-evidence");
                }

                string resolved = Path.GetFullPath(Path.Combine(repoRoot, rel));
                if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Core.DecoException($"Feature '{id}' in '{path}' has evidence outside the repository: '{r}'.", "store-evidence");
                }

                if (!File.Exists(resolved))
                {
                    throw new Core.DecoException($"Feature '{id}' in '{path}' references missing evidence '{r}'.", "store-evidence");
                }
            }
        }
    }

    internal static void AssertCrossLocale(List<LocaleEntry> entries)
    {
        JsonElement english = entries[0].Listing;
        var featureIds = english.GetProperty("productFeatures").EnumerateArray()
            .Select(f => f.GetProperty("id").GetString() ?? string.Empty).ToList();
        var dupes = featureIds.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
        {
            throw new Core.DecoException($"Duplicate product feature IDs in {entries[0].Locale}: {string.Join(", ", dupes)}.", "store-features");
        }

        for (int i = 1; i < entries.Count; i++)
        {
            JsonElement listing = entries[i].Listing;
            if ((listing.GetProperty("productName").GetString() ?? string.Empty)
                != (english.GetProperty("productName").GetString() ?? string.Empty))
            {
                throw new Core.DecoException($"Product name differs across locales in {entries[i].Locale}; retain the reserved product name.", "store-product-name");
            }

            var ids = listing.GetProperty("productFeatures").EnumerateArray()
                .Select(f => f.GetProperty("id").GetString() ?? string.Empty).ToList();
            if (!ids.SequenceEqual(featureIds, StringComparer.Ordinal))
            {
                throw new Core.DecoException($"Product feature IDs/order in {entries[i].Locale} must exactly match {entries[0].Locale}.", "store-features");
            }
        }

        string reference = entries[0].Locale;
        for (int left = 0; left < entries.Count; left++)
        {
            for (int right = left + 1; right < entries.Count; right++)
            {
                foreach (string field in new[] { "shortDescription", "description" })
                {
                    string a = entries[left].Listing.GetProperty(field).GetString() ?? string.Empty;
                    string b = entries[right].Listing.GetProperty(field).GetString() ?? string.Empty;
                    if (a == b)
                    {
                        throw new Core.DecoException(
                            entries[left].Locale == reference
                                ? $"{entries[right].Locale}.{field} is identical to {reference}; provide a real translation."
                                : $"{entries[left].Locale}.{field} and {entries[right].Locale}.{field} are identical; provide separate translations.",
                            "store-translation");
                    }
                }

                for (int fi = 0; fi < featureIds.Count; fi++)
                {
                    string a = entries[left].Listing.GetProperty("productFeatures")[fi].GetProperty("text").GetString() ?? string.Empty;
                    string b = entries[right].Listing.GetProperty("productFeatures")[fi].GetProperty("text").GetString() ?? string.Empty;
                    if (a == b)
                    {
                        throw new Core.DecoException(
                            entries[left].Locale == reference
                                ? $"{entries[right].Locale} feature '{featureIds[fi]}' is identical to {reference}; provide a real translation."
                                : $"Feature '{featureIds[fi]}' is identical in {entries[left].Locale} and {entries[right].Locale}; provide separate translations.",
                            "store-translation");
                    }
                }

                string na = entries[left].ReleaseNotes.TryGetProperty("whatsNew", out JsonElement wa) && wa.ValueKind == JsonValueKind.String
                    ? wa.GetString() ?? string.Empty : string.Empty;
                string nb = entries[right].ReleaseNotes.TryGetProperty("whatsNew", out JsonElement wb) && wb.ValueKind == JsonValueKind.String
                    ? wb.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(na) && na == nb)
                {
                    throw new Core.DecoException(
                        entries[left].Locale == reference
                            ? $"{entries[right].Locale} release notes are identical to {reference}; provide a real translation."
                            : $"Release notes are identical in {entries[left].Locale} and {entries[right].Locale}; provide separate translations.",
                        "store-translation");
                }
            }
        }
    }

    internal static List<string> CollectReviewIssues(List<LocaleEntry> entries)
    {
        var issues = new List<string>();
        foreach (LocaleEntry e in entries)
        {
            string status = e.Listing.TryGetProperty("reviewStatus", out JsonElement rs) && rs.ValueKind == JsonValueKind.String
                ? rs.GetString() ?? string.Empty : string.Empty;
            string reviewer = e.Listing.TryGetProperty("reviewedBy", out JsonElement rb) && rb.ValueKind == JsonValueKind.String
                ? rb.GetString() ?? string.Empty : string.Empty;
            if (status != "approved")
            {
                issues.Add($"{e.Locale} listing is marked '{status}'.");
            }
            else if (string.IsNullOrWhiteSpace(reviewer))
            {
                issues.Add($"{e.Locale} listing is approved but has no reviewer.");
            }

            string nstatus = e.ReleaseNotes.TryGetProperty("reviewStatus", out JsonElement nrs) && nrs.ValueKind == JsonValueKind.String
                ? nrs.GetString() ?? string.Empty : string.Empty;
            string nreviewer = e.ReleaseNotes.TryGetProperty("reviewedBy", out JsonElement nrb) && nrb.ValueKind == JsonValueKind.String
                ? nrb.GetString() ?? string.Empty : string.Empty;
            string whatsNew = e.ReleaseNotes.TryGetProperty("whatsNew", out JsonElement wn) && wn.ValueKind == JsonValueKind.String
                ? wn.GetString() ?? string.Empty : string.Empty;
            if (nstatus != "approved")
            {
                issues.Add($"{e.Locale} release notes are marked '{nstatus}'.");
            }
            else if (string.IsNullOrWhiteSpace(nreviewer))
            {
                issues.Add($"{e.Locale} release notes are approved but have no reviewer.");
            }

            if (string.IsNullOrWhiteSpace(whatsNew))
            {
                issues.Add($"{e.Locale} release notes are empty.");
            }
        }

        return issues;
    }
}
