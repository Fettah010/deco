using System.Text;
using System.Text.Json;

namespace DevCli.Commands;

/// <summary>
/// Port of Scripts/New-StoreReviewIndex.ps1: read-only review gallery
/// (review/index.html + review/index.json) covering shots, listing words,
/// baseline diff, pipeline facts, approval state, and next actions.
/// App name is generic (ps1 hardcodes Formixa); the approve hint points at
/// `deco store approve`.
/// </summary>
public static class StoreReview
{
    public sealed record Baseline(string Version, JsonElement Manifest);

    public static void Build(
        string appName, string version, string candidateDir, string repoRoot,
        List<(string Locale, string Description, string Short, List<string> Features, string Notes, List<string> Missing)> listingText,
        Baseline? baseline, JsonElement? pipelineManifest, string? pipelineManifestName)
    {
        string manifestPath = Path.Combine(candidateDir, "screenshot-manifest.json");
        using var manifestDoc = StoreJson.Load(manifestPath, "Screenshot manifest");
        JsonElement manifest = manifestDoc.RootElement;
        var images = manifest.GetProperty("images").EnumerateArray().ToList();
        var locales = manifest.GetProperty("locales").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();

        string? approvalBy = null;
        string? approvalUtc = null;
        string approvalPath = Path.Combine(candidateDir, "review", "approval.json");
        if (File.Exists(approvalPath))
        {
            using var approvalDoc = StoreJson.Load(approvalPath, "Screenshot approval");
            approvalBy = approvalDoc.RootElement.TryGetProperty("approvedBy", out JsonElement ab) ? ab.GetString() : null;
            approvalUtc = approvalDoc.RootElement.TryGetProperty("approvedUtc", out JsonElement au) ? au.GetString() : null;
        }

        string manifestSha = manifest.GetProperty("manifestSha256").GetString() ?? string.Empty;
        bool approvalOk = approvalBy is not null
            && File.Exists(approvalPath)
            && manifestSha == ManifestShaOf(approvalPath);
        bool gitDirty = manifest.TryGetProperty("gitDirty", out JsonElement gd) && gd.ValueKind == JsonValueKind.True;
        string gitCommit = manifest.TryGetProperty("gitCommit", out JsonElement gc) && gc.ValueKind == JsonValueKind.String
            ? gc.GetString() ?? string.Empty : string.Empty;

        var baseByKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (baseline is not null)
        {
            foreach (JsonElement im in baseline.Manifest.GetProperty("images").EnumerateArray())
            {
                baseByKey[$"{im.GetProperty("locale").GetString()}/{im.GetProperty("fileName").GetString()}"] = im;
            }
        }

        var diffNew = new List<string>();
        var diffChanged = new List<string>();
        var diffSame = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement im in images)
        {
            string key = $"{im.GetProperty("locale").GetString()}/{im.GetProperty("fileName").GetString()}";
            seen.Add(key);
            if (baseline is null)
            {
                continue;
            }

            if (!baseByKey.TryGetValue(key, out JsonElement b))
            {
                diffNew.Add(key);
            }
            else if ((b.GetProperty("sha256").GetString() ?? string.Empty) != (im.GetProperty("sha256").GetString() ?? string.Empty))
            {
                diffChanged.Add(key);
            }
            else
            {
                diffSame.Add(key);
            }
        }

        var diffRemoved = baseByKey.Keys.Where(k => !seen.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        var checks = new List<(string Check, bool Ok, string Detail)>();
        foreach (string loc in locales)
        {
            int n = images.Count(i => (i.GetProperty("locale").GetString() ?? string.Empty) == loc);
            checks.Add(($"count-{loc}", n > 0, $"{n} shot(s)"));
        }

        var small = images.Where(i => i.GetProperty("width").GetInt32() < 1366 || i.GetProperty("height").GetInt32() < 768)
            .Select(i => i.GetProperty("fileName").GetString() ?? string.Empty).ToList();
        checks.Add(("min-size-1366x768", small.Count == 0, small.Count == 0 ? "all pass" : string.Join(", ", small)));
        checks.Add(("tree-clean-at-capture", !gitDirty, gitDirty ? "captured from a dirty tree" : "clean"));
        checks.Add(("approval-current", approvalOk,
            approvalBy is null ? "pending human review"
            : approvalOk ? $"approved by {approvalBy}" : "approval is for an older manifest"));

        var nextActions = new List<string>();
        if (!approvalOk)
        {
            nextActions.Add($"Owner: review the gallery, then run deco store approve --reviewer <name> --version {version}");
        }

        if (gitDirty)
        {
            nextActions.Add("Agent: re-capture from a clean tree before submitting (provenance is dirty).");
        }

        foreach ((string check, bool ok, string detail) in checks.Where(c => !c.Ok))
        {
            if (!check.StartsWith("approval-", StringComparison.Ordinal) && !check.StartsWith("tree-clean", StringComparison.Ordinal))
            {
                nextActions.Add($"Agent: fix failing check {check} ({detail}).");
            }
        }

        if (nextActions.Count == 0)
        {
            nextActions.Add("Agent: candidate is approved - proceed to Store submission.");
        }

        long totalBytes = images.Sum(i => i.GetProperty("bytes").GetInt64());
        string shortSha = manifestSha.Length > 12 ? manifestSha[..12] : manifestSha;
        string createdDay = manifest.TryGetProperty("createdUtc", out JsonElement cu) && cu.ValueKind == JsonValueKind.String
            ? (cu.GetString() ?? string.Empty) : string.Empty;
        if (createdDay.Length > 10)
        {
            createdDay = createdDay[..10];
        }

        var h = new StringBuilder();
        h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        h.Append($"<title>{E(appName)} {E(version)} release review</title><style>{Css()}</style></head><body>");
        h.Append("<nav class='toc'><a href='#summary'>Summary</a><a href='#release'>Release</a><a href='#approve'>Approve</a><a href='#locales'>Locales</a><a href='#diff'>Diff</a><a href='#manifest'>Manifest</a></nav>");
        h.Append($"<h1>{E(appName)} {E(version)} release review</h1>");
        h.Append($"<p>{Pill(approvalOk, approvalOk ? "APPROVED by " + approvalBy : "PENDING REVIEW")} {Pill(!gitDirty, gitDirty ? "DIRTY TREE" : "CLEAN TREE")} <span class='pill info'>{E(gitCommit)}</span></p>");
        h.Append("<section id='summary'><h2>Summary</h2><div class='dashboard'>");
        h.Append($"<div class='stat'><div class='n'>{images.Count}</div><div class='l'>screenshots</div></div>");
        h.Append($"<div class='stat'><div class='n'>{locales.Count}</div><div class='l'>locales ({E(string.Join(", ", locales))})</div></div>");
        h.Append($"<div class='stat'><div class='n'>{MB(totalBytes)}</div><div class='l'>total pixels on disk</div></div>");
        h.Append($"<div class='stat'><div class='n'>{E(shortSha)}</div><div class='l'>manifest</div></div>");
        h.Append($"<div class='stat'><div class='n'>{E(createdDay)}</div><div class='l'>captured</div></div>");
        h.Append("</div><div class=\"card\"><ul class=\"tight\">");
        foreach ((string check, bool ok, string detail) in checks)
        {
            h.Append($"<li>{Pill(ok, ok ? "PASS" : "FAIL")} <code>{E(check)}</code> - {E(detail)}</li>");
        }

        h.Append("</ul></div></section>");
        h.Append("<section id='release'><h2>Release</h2>");
        if (pipelineManifest is null)
        {
            h.Append($"<div class='card'><p>No pipeline manifest for {E(version)} yet - shots were captured standalone, not through the release pipeline. Run the pipeline to fill this section.</p></div>");
        }
        else
        {
            JsonElement rel = pipelineManifest.Value;
            h.Append($"<div class='card'><p>Pipeline manifest <code>{E(pipelineManifestName ?? "release-manifest.json")}</code> &middot; submission: <strong>{E(Str(rel, "submission"))}</strong></p><table><tr><th>Gate</th><th>Status</th><th>Detail</th></tr>");
            if (rel.TryGetProperty("checks", out JsonElement relChecks) && relChecks.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty g in relChecks.EnumerateObject())
                {
                    string st = g.Value.TryGetProperty("status", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
                    string reason = g.Value.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? string.Empty : string.Empty;
                    string pill = st is "passed" ? Pill(true, st) : st is "skipped" or "reused" or "not-run" ? $"<span class='pill info'>{E(st)}</span>" : Pill(false, st);
                    h.Append($"<tr><td><code>{E(g.Name)}</code></td><td>{pill}</td><td class='muted'>{E(reason)}</td></tr>");
                }
            }

            h.Append("</table></div>");
        }

        h.Append("</section>");
        h.Append("<section id='approve'><h2>Approve</h2><div class='card'>");
        if (approvalOk)
        {
            h.Append($"<p>{Pill(true, "signed")} Approved by <strong>{E(approvalBy!)}</strong> at {E(approvalUtc ?? string.Empty)}.</p>");
        }
        else
        {
            h.Append("<p>Review every image and caption below, then record your human signature. The pipeline never self-approves.</p>");
            h.Append($"<pre>deco store approve --reviewer '&lt;your-name&gt;' --version {E(version)}</pre>");
            h.Append($"<p class='muted'>Approval binds manifest <code>{E(shortSha)}</code>; re-capturing invalidates it.</p>");
        }

        h.Append("</div></section>");
        h.Append("<section id='locales'><h2>Locales</h2>");
        foreach ((string locale, string description, string shortDesc, List<string> features, string notes, List<string> missing) in listingText)
        {
            string sheetLink = File.Exists(Path.Combine(candidateDir, "review", $"{locale}-contact-sheet.png"))
                ? $" (<a href='{locale}-contact-sheet.png'>contact sheet</a>)" : string.Empty;
            h.Append($"<div class='locale'><h3>{E(locale)}{sheetLink}</h3>");
            if (missing.Count > 0)
            {
                h.Append($"<p>{Pill(false, "missing: " + string.Join(", ", missing))}</p>");
            }

            h.Append($"<div class='card'><p><strong>{E(shortDesc)}</strong></p><p>{E(description)}</p>");
            if (features.Count > 0)
            {
                h.Append("<ul class=\"tight\">" + string.Join(string.Empty, features.Select(f => $"<li>{E(f)}</li>")) + "</ul>");
            }

            h.Append($"<p><strong>What is new:</strong> {E(notes)}</p></div>");
            h.Append("<div class='grid'>");
            foreach (JsonElement im in images
                .Where(i => (i.GetProperty("locale").GetString() ?? string.Empty) == locale)
                .OrderBy(i => i.GetProperty("fileName").GetString() ?? string.Empty, StringComparer.Ordinal))
            {
                string fileName = im.GetProperty("fileName").GetString() ?? string.Empty;
                string src = "../" + (im.GetProperty("relativePath").GetString() ?? string.Empty);
                string key = $"{locale}/{fileName}";
                string dcls = diffNew.Contains(key, StringComparer.Ordinal) ? "new"
                    : diffChanged.Contains(key, StringComparer.Ordinal) ? "changed" : "same";
                string caption = im.GetProperty("caption").GetString() ?? string.Empty;
                int w = im.GetProperty("width").GetInt32();
                int hh = im.GetProperty("height").GetInt32();
                long bytes = im.GetProperty("bytes").GetInt64();
                string marker = im.TryGetProperty("expectedMarkerText", out JsonElement mk) && mk.ValueKind == JsonValueKind.String ? mk.GetString() ?? string.Empty : string.Empty;
                string sha = (im.GetProperty("sha256").GetString() ?? string.Empty);
                string figId = $"s-{locale}-{im.GetProperty("id").GetString()}";
                h.Append($"<figure class='zoom' id='{E(figId)}'><a href='{E(src)}' target='_blank' rel='noopener'><img class='shot' loading='lazy' src='{E(src)}' alt='{E(caption)}'></a>");
                h.Append($"<figcaption><strong>{E(fileName)}</strong> ({w}x{hh}, {KB(bytes)})<br>{E(caption)}<br><span class='meta'>marker: {E(marker)} &middot; sha {E(sha.Length > 12 ? sha[..12] : sha)} &middot; {dcls}</span></figcaption></figure>");
            }

            h.Append("</div></div>");
        }

        h.Append("</section>");
        h.Append("<section id='diff'><h2>Diff vs previous</h2>");
        if (baseline is null)
        {
            h.Append("<div class=\"card\"><p>First capture in Store/ - no baseline to compare.</p></div>");
        }
        else
        {
            h.Append($"<p>Baseline: <strong>{E(baseline.Version)}</strong></p>");
            h.Append("<table><tr><th>Status</th><th>Count</th><th>Files</th></tr>");
            foreach ((string k, List<string> v) in new[] { ("New", diffNew), ("Changed", diffChanged), ("Same", diffSame), ("Removed", diffRemoved) })
            {
                h.Append($"<tr><td>{k}</td><td>{v.Count}</td><td class='muted'>{E(string.Join(", ", v.OrderBy(s => s, StringComparer.Ordinal)))}</td></tr>");
            }

            h.Append("</table>");
        }

        h.Append("</section>");
        h.Append($"<section id='manifest'><h2>Manifest</h2><details><summary>screenshot-manifest.json ({E(shortSha)})</summary><pre>{E(File.ReadAllText(manifestPath, Encoding.UTF8))}</pre></details></section>");
        h.Append("<p class='muted'>Generated by deco store review - regenerate after every capture or release.</p>");
        h.Append("</body></html>");

        string reviewDir = Path.Combine(candidateDir, "review");
        Directory.CreateDirectory(reviewDir);
        File.WriteAllText(Path.Combine(reviewDir, "index.html"), h.ToString(), new UTF8Encoding(false));

        string summary = StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            w.PropertyName("schemaVersion"); w.Value((long)2);
            w.PropertyName("version"); w.Value(version);
            w.PropertyName("generatedUtc"); w.Value(DateTime.UtcNow.ToString("o"));
            w.PropertyName("commit"); w.Value(gitCommit);
            w.PropertyName("dirtyTree"); w.Value(gitDirty);
            w.PropertyName("manifestSha256"); w.Value(manifestSha);
            w.PropertyName("imageCount"); w.Value((long)images.Count);
            w.PropertyName("totalBytes"); w.Value(totalBytes);
            w.PropertyName("images");
            w.BeginArray();
            foreach (JsonElement im in images)
            {
                StoreJson.WriteElement(w, im);
            }

            w.EndArray();
            w.PropertyName("locales");
            w.BeginArray();
            foreach (string l in locales)
            {
                w.Value(l);
            }

            w.EndArray();
            w.PropertyName("approval");
            if (approvalOk)
            {
                w.BeginObject();
                w.PropertyName("state"); w.Value("approved");
                w.PropertyName("by"); w.Value(approvalBy!);
                w.PropertyName("at"); w.Value(approvalUtc ?? string.Empty);
                w.EndObject();
            }
            else
            {
                w.BeginObject();
                w.PropertyName("state"); w.Value("pending");
                w.EndObject();
            }

            w.PropertyName("baseline"); w.Value(baseline?.Version);
            w.PropertyName("diff");
            w.BeginObject();
            w.PropertyName("added"); WriteStrings(w, diffNew);
            w.PropertyName("changed"); WriteStrings(w, diffChanged);
            w.PropertyName("unchanged"); WriteStrings(w, diffSame);
            w.PropertyName("removed"); WriteStrings(w, diffRemoved);
            w.EndObject();
            w.PropertyName("checks");
            w.BeginArray();
            foreach ((string check, bool ok, string detail) in checks)
            {
                w.BeginObject();
                w.PropertyName("check"); w.Value(check);
                w.PropertyName("ok"); w.Value(ok);
                w.PropertyName("detail"); w.Value(detail);
                w.EndObject();
            }

            w.EndArray();
            w.PropertyName("pipeline");
            if (pipelineManifest is null)
            {
                w.BeginObject();
                w.PropertyName("present"); w.Value(false);
                w.EndObject();
            }
            else
            {
                w.BeginObject();
                w.PropertyName("present"); w.Value(true);
                w.PropertyName("submission"); w.Value(Str(pipelineManifest.Value, "submission"));
                w.PropertyName("status"); w.Value(Str(pipelineManifest.Value, "status"));
                w.EndObject();
            }

            w.PropertyName("nextActions");
            WriteStrings(w, nextActions);
            w.EndObject();
        });
        File.WriteAllText(Path.Combine(reviewDir, "index.json"), Pretty(summary) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void WriteStrings(StoreJson.CanonicalWriter w, List<string> items)
    {
        w.BeginArray();
        foreach (string s in items)
        {
            w.Value(s);
        }

        w.EndArray();
    }

    private static string ManifestShaOf(string approvalPath)
    {
        try
        {
            using var doc = StoreJson.Load(approvalPath, "Screenshot approval");
            return doc.RootElement.TryGetProperty("screenshotManifestSha256", out JsonElement e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty : string.Empty;

    private static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

    private static string Pill(bool ok, string text) =>
        $"<span class='pill {(ok ? "ok" : "bad")}'>{E(text)}</span>";

    private static string MB(long b) => $"{b / (1024.0 * 1024.0):N1} MB";

    private static string KB(long b) => $"{b / 1024.0:N0} KB";

    private static string Pretty(string compact)
    {
        using var doc = JsonDocument.Parse(compact);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Css() =>
        ":root{--bg:#ffffff;--fg:#1c1c1c;--muted:#5a5a5a;--card:#f6f6f6;--line:#d8d8d8;--accent:#0b5fff;--ok-bg:#d4edda;--ok-fg:#155724;--bad-bg:#f8d7da;--bad-fg:#721c24;--info-bg:#d1ecf1;--info-fg:#0c5460;--code-bg:#efefef}"
        + "@media(prefers-color-scheme:dark){:root{--bg:#141414;--fg:#e2e2e2;--muted:#a8a8a8;--card:#1f1f1f;--line:#3a3a3a;--accent:#8ab4ff;--ok-bg:#1e3a26;--ok-fg:#9fe0ae;--bad-bg:#402024;--bad-fg:#f2a9b0;--info-bg:#17383f;--info-fg:#9adbe8;--code-bg:#2a2a2a}}"
        + "body{font:15px/1.55 system-ui,\"Segoe UI\",sans-serif;margin:0 auto;max-width:1240px;padding:1.5rem;background:var(--bg);color:var(--fg)}"
        + "h1{font-size:1.7rem;margin:.3rem 0}h2{margin:2.2rem 0 .8rem;padding-bottom:.35rem;border-bottom:2px solid var(--accent)}"
        + "a{color:var(--accent)}.muted{color:var(--muted)}.pill{display:inline-block;padding:.15rem .65rem;border-radius:999px;font-size:.8rem;font-weight:700;margin:.1rem .25rem .1rem 0}"
        + ".pill.ok{background:var(--ok-bg);color:var(--ok-fg)}.pill.bad{background:var(--bad-bg);color:var(--bad-fg)}.pill.info{background:var(--info-bg);color:var(--info-fg)}"
        + ".card{background:var(--card);border:1px solid var(--line);border-radius:.6rem;padding:1rem 1.2rem;margin:.8rem 0}"
        + ".dashboard{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:.7rem;margin:1rem 0}.stat{background:var(--card);border:1px solid var(--line);border-radius:.6rem;padding:.7rem .9rem}.stat .n{font-size:1.4rem;font-weight:800}.stat .l{font-size:.82rem;color:var(--muted)}"
        + ".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(340px,1fr));gap:1rem}figure{margin:0}.shot{border:1px solid var(--line);border-radius:.3rem;max-width:100%;height:auto;display:block;background:#fff}"
        + ".meta{font-size:.82rem;color:var(--muted)}code{background:var(--code-bg);padding:.1rem .35rem;border-radius:.3rem;font-size:.85em}"
        + "table{border-collapse:collapse;width:100%;margin:.6rem 0}td,th{border:1px solid var(--line);padding:.4rem .65rem;text-align:left;vertical-align:top}th{background:var(--card)}pre{white-space:pre-wrap;word-break:break-word;background:var(--code-bg);padding:.8rem;border-radius:.4rem;overflow:auto}";
}
