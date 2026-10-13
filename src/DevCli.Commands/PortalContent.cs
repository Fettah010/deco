using System.Text.Json;

namespace DevCli.Commands;

/// <summary>
/// Pure preflight for `store portal` (port of submit.mjs preflight):
/// per-locale listing content from ListingSource (+ en-US overrides),
/// certified-wording gates, shots discovery with sibling auto-detect and
/// matrix-leaf normalization, and price-snapshot comparison.
/// No browser, no network — fully unit-tested.
/// </summary>
public static class PortalContent
{
    public sealed record LocaleContent(string Tag, string Description, string Notes, string Short, List<string> Features);

    public sealed record PriceSnapshot(string Base, List<(string Market, string Price)> Rows)
    {
        public string Canonical() =>
            StoreJson.CanonicalWriter.Write(w =>
            {
                w.BeginObject();
                w.PropertyName("base"); w.Value(Base);
                w.PropertyName("rows");
                w.BeginArray();
                foreach ((string market, string price) in Rows.OrderBy(r => r.Market, StringComparer.Ordinal))
                {
                    w.BeginObject();
                    w.PropertyName("market"); w.Value(market);
                    w.PropertyName("price"); w.Value(price);
                    w.EndObject();
                }

                w.EndArray();
                w.EndObject();
            });
    }

    public static Dictionary<string, LocaleContent> ResolveLocaleContent(
        string listingSourceDir, string version, string[] locales,
        string descriptionOverride, string releaseNotesOverride, bool certifiedConfirmed)
    {
        var content = new Dictionary<string, LocaleContent>(StringComparer.OrdinalIgnoreCase);
        foreach (string tag in locales)
        {
            string listingPath = Path.Combine(listingSourceDir, tag, "listing.json");
            string notesPath = Path.Combine(listingSourceDir, "release-notes", version, tag + ".json");
            if (!File.Exists(listingPath))
            {
                throw new Core.DecoException($"Listing source missing: {listingPath}", "portal-listing-missing");
            }

            if (!File.Exists(notesPath))
            {
                throw new Core.DecoException(
                    $"Release notes missing: {notesPath} (add Store/ListingSource/release-notes/{version}/{tag}.json)",
                    "portal-notes-missing");
            }

            using var listingDoc = StoreJson.Load(listingPath, "Listing");
            using var notesDoc = StoreJson.Load(notesPath, "Release notes");
            string desc = Str(listingDoc.RootElement, "description");
            string notes = Str(notesDoc.RootElement, "whatsNew");
            string shrt = Str(listingDoc.RootElement, "shortDescription");
            var features = listingDoc.RootElement.TryGetProperty("productFeatures", out var feats)
                ? feats.EnumerateArray()
                    .Select(f => Str(f, "text").Trim())
                    .Where(s => !string.IsNullOrEmpty(s)).ToList()
                : [];
            if (tag == "en-US")
            {
                if (!string.IsNullOrWhiteSpace(descriptionOverride))
                {
                    desc = descriptionOverride;
                }

                if (!string.IsNullOrWhiteSpace(releaseNotesOverride))
                {
                    notes = releaseNotesOverride;
                }
            }

            if (string.IsNullOrWhiteSpace(desc))
            {
                throw new Core.DecoException($"Empty description for {tag} ({listingPath}).", "portal-empty");
            }

            if (desc.Length > 10000)
            {
                throw new Core.DecoException($"Description exceeds the 10000-character Store limit ({tag}).", "portal-too-long");
            }

            if (string.IsNullOrWhiteSpace(notes))
            {
                throw new Core.DecoException($"Empty release notes for {tag} ({notesPath}).", "portal-empty");
            }

            if (desc.Contains("certified", StringComparison.OrdinalIgnoreCase) && !certifiedConfirmed)
            {
                throw new Core.DecoException(
                    $"Certified wording gate ({tag}): pass --certified-confirmed only after the cross-check.",
                    "portal-certified");
            }

            content[tag] = new LocaleContent(tag, desc, notes, shrt, features);
        }

        return content;
    }

    /// <summary>
    /// Shots resolution: matrix root normalizes to its en-US/desktop leaf;
    /// es-ES/fr-FR siblings auto-discover next to it. Locales without a
    /// directory keep existing Store images (warn, never guess).
    /// </summary>
    public static Dictionary<string, List<string>> DiscoverShots(string? shotsDir, string[] locales, Action<string>? warn = null)
    {
        var byLocale = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(shotsDir) && Directory.Exists(shotsDir))
        {
            string leaf = Path.Combine(shotsDir, "en-US", "desktop");
            if (Directory.Exists(leaf))
            {
                shotsDir = leaf;
            }
        }

        if (!string.IsNullOrWhiteSpace(shotsDir) && Directory.Exists(shotsDir))
        {
            var enFiles = Directory.GetFiles(shotsDir, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            byLocale["en-US"] = enFiles;
            foreach (string loc in locales.Where(l => l != "en-US"))
            {
                string sib = System.Text.RegularExpressions.Regex.Replace(
                    shotsDir, @"en-US([/\\]|$)", loc + "$1");
                if (sib != shotsDir && Directory.Exists(sib))
                {
                    byLocale[loc] = Directory.GetFiles(sib, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                }
            }

            foreach (string loc in locales)
            {
                if (!byLocale.ContainsKey(loc))
                {
                    warn?.Invoke($"WARNING: no shots directory for {loc} — those listing images stay as-is.");
                }
            }
        }
        else
        {
            warn?.Invoke("WARNING: no screenshots — listing images stay as-is.");
        }

        return byLocale;
    }

    public static string NewestMsixUpload(string msixDir)
    {
        if (!Directory.Exists(msixDir))
        {
            throw new Core.DecoException("No .msixupload found. Build one first.", "portal-no-package");
        }

        string? newest = Directory.GetFiles(msixDir, "*.msixupload")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
        if (newest is null)
        {
            throw new Core.DecoException("No .msixupload found. Build one first.", "portal-no-package");
        }

        return newest;
    }

    private static string Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
