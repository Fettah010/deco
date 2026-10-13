using Microsoft.Playwright;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevCli.Infrastructure;

/// <summary>
/// Checkpointed Partner Center submission driver (port of
/// Scripts/portal/submit.mjs, 11 progress steps): headless-first persistent
/// profile, login assist only when needed, per-locale listings with read-back,
/// price baseline/verify, publish-mode gate, final checkpoint, submit, and a
/// step manifest. Any finder failure saves artifacts instead of clicking
/// blindly. URLs are parameterized (PortalSite) so the engine self-test can
/// run against local fixture pages.
/// </summary>
public static class PortalSubmission
{
    public sealed record Site(
        string Dashboard = "https://partner.microsoft.com/dashboard",
        string AppsAndGames = "https://partner.microsoft.com/dashboard/v2/apps-and-games",
        string ProductBase = "https://partner.microsoft.com/en-us/dashboard/products")
    {
        public string Overview(string storeId) => $"{ProductBase}/{storeId}/overview";
        public string Options(string storeId, string submissionId) =>
            $"{ProductBase}/{storeId}/submissions/{submissionId}/options";
        public string Listings(string storeId, string submissionId) =>
            $"{ProductBase}/{storeId}/submissions/{submissionId}/listings";
    }

    public sealed record LocaleText(string Tag, string Description, string Notes);

    public sealed record RunPlan(
        string MsixUpload,
        Dictionary<string, LocaleText> LocaleContent,
        Dictionary<string, List<string>> ShotsByLocale,
        string ListingVersion,
        string StoreId,
        string AppName,
        string ProfileDir,
        string PublishMode,
        string ReleaseNotesDefault,
        bool AutoConfirm,
        bool Headed,
        bool NoLoginAssist,
        bool SkipShots,
        bool SkipPackages,
        bool CertifiedConfirmed,
        int LoginTimeoutSec,
        int StepTimeoutSec,
        string ProgressFile,
        string ManifestPath,
        Site SiteConfig,
        string MsstoreExe);

    public sealed record PriceTable(string Base, List<(string Market, string Price)> Rows);

    public const string PriceScrapeJs = @"() => {
      const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();
      let base = '';
      for (const sel of document.querySelectorAll('select, input')) {
        const ctx = norm(sel.closest('div')?.parentElement?.innerText || '').slice(0, 220);
        if (/base price/i.test(ctx)) {
          const v = sel.value || (sel.options ? norm(sel.options[sel.selectedIndex]?.text || '') : '');
          if (v) base = base ? `${base} | ${v}` : v;
        }
      }
      const rows = [];
      const seen = new Set();
      const isPrice = (s) => /[$€£¥]|(\d+[.,]\d{2})/.test(s || '');
      const consider = (market, price) => {
        market = norm(market); price = norm(price);
        if (market.length > 1 && market.length < 60 && isPrice(price) && !seen.has(market)) {
          seen.add(market); rows.push({ market, price });
        }
      };
      for (const table of document.querySelectorAll('table')) {
        for (const tr of table.querySelectorAll('tr')) {
          const cells = [...tr.querySelectorAll('td, th')].map((td) => norm(td.innerText));
          if (cells.length >= 2) consider(cells[0], cells[cells.length - 1]);
        }
      }
      for (const dl of document.querySelectorAll('dl')) {
        const terms = [...dl.querySelectorAll('dt')];
        const defs = [...dl.querySelectorAll('dd')];
        for (let k = 0; k < Math.min(terms.length, defs.length); k++) consider(terms[k].innerText, defs[k].innerText);
      }
      rows.sort((a, b) => (a.market < b.market ? -1 : 1));
      return { base, rows };
    }";

    public const string ScanRowsJs = @"(fileReSrc) => {
      const fileRe = new RegExp(fileReSrc, 'i');
      const rows = [];
      const seen = new Set();
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      while (walker.nextNode()) {
        const t = walker.currentNode.nodeValue || '';
        if (!fileRe.test(t)) continue;
        let el = walker.currentNode.parentElement;
        let widest = '';
        for (let d = 0; d < 10 && el && el !== document.body; d++) {
          const txt = ((el.innerText || '').replace(/\s+/g, ' ')).trim();
          if (txt.length <= 600 && txt.length >= widest.length &&
              /show details|version|architecture|device famil|remove|uploading|paused|mb|taking a long time|null bytes/i.test(txt)) {
            widest = txt;
          }
          el = el.parentElement;
        }
        const staged = /version\s+v?\d|device famil|architecture\s+\w+/i.test(widest);
        const working = /uploading|paused|processing/i.test(widest);
        const key = (widest || t.trim()).slice(0, 300);
        if (key && !seen.has(key)) { seen.add(key); rows.push({ text: key, staged, working }); }
      }
      return rows;
    }";

    public static string EscapeRe(string s) =>
        Regex.Replace(s, @"[.*+?^${}()|[\]\\]", @"\$&");

    public static async Task<PriceTable> ScrapePricesAsync(IPage page)
    {
        string json = await page.EvaluateAsync<string>(
            "h => { const r = (" + PriceScrapeJs + ")(); r.rows = r.rows.map(x => [x.market, x.price]); return JSON.stringify(r); }", 0);
        using var doc = JsonDocument.Parse(json);
        string b = doc.RootElement.TryGetProperty("base", out var be) ? be.GetString() ?? string.Empty : string.Empty;
        var rows = new List<(string, string)>();
        if (doc.RootElement.TryGetProperty("rows", out var re))
        {
            foreach (var r in re.EnumerateArray())
            {
                rows.Add((r[0].GetString() ?? string.Empty, r[1].GetString() ?? string.Empty));
            }
        }

        return new PriceTable(b, rows);
    }

    /// <summary>Order-independent snapshot equality (baseline vs verify).</summary>
    public static string PriceCanonical(PriceTable table) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            @base = table.Base,
            rows = table.Rows
                .OrderBy(r => r.Market, StringComparer.Ordinal)
                .Select(r => new { market = r.Market, price = r.Price })
                .ToList(),
        });

    public static async Task<List<RowFlag>> ScanRowsAsync(IPage page, string fileName)
    {
        var list = await page.EvaluateAsync<JsonElement>(ScanRowsJs, EscapeRe(fileName));
        var rows = new List<RowFlag>();
        foreach (var r in list.EnumerateArray())
        {
            rows.Add(new RowFlag(
                r.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                r.TryGetProperty("staged", out var s) && s.ValueKind == JsonValueKind.True,
                r.TryGetProperty("working", out var wk) && wk.ValueKind == JsonValueKind.True));
        }

        return rows;
    }

    public sealed record RowFlag(string Text, bool Staged, bool Working);

    public static async Task<string> PricingSectionUrlAsync(IPage page)
    {
        var sections = await PortalDriver.SectionUrlsAsync(page);
        if (sections.TryGetValue("pricing", out string? hit) && !string.IsNullOrWhiteSpace(hit))
        {
            return hit;
        }

        foreach (var frame in page.Frames)
        {
            try
            {
                string href = await frame
                    .GetByRole(AriaRole.Link, new FrameGetByRoleOptions { NameRegex = new Regex("pricing and availability", RegexOptions.IgnoreCase) })
                    .First.GetAttributeAsync("href") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(href))
                {
                    return href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? href : $"https://partner.microsoft.com{href}";
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    public static async Task<string> ApiListingSnapshotAsync(
        ProcessRunner runner, string msstoreExe, string storeId, Action<string>? log = null)
    {
        ProcessResult r = await runner.RunAsync(msstoreExe,
            ["submission", "get", storeId, "--output-stream", "stderr"],
            timeout: TimeSpan.FromSeconds(120));
        string raw = r.StdOut + "\n" + r.StdErr;
        int openAt = raw.IndexOf('{');
        int closeAt = raw.LastIndexOf('}');
        if (r.ExitCode != 0 || openAt < 0 || closeAt <= openAt)
        {
            throw new Core.DecoException("API baseline unreadable, refusing to continue blind.", "portal-api-baseline");
        }

        return raw.Substring(openAt, closeAt - openAt + 1);
    }

    public static Dictionary<string, (string Desc, string Notes, string Short, int Images)> ApiListingEntry(string submissionJson)
    {
        using var doc = JsonDocument.Parse(submissionJson);
        var map = new Dictionary<string, (string, string, string, int)>(StringComparer.OrdinalIgnoreCase);
        if (!doc.RootElement.TryGetProperty("listings", out var listings)
            && !doc.RootElement.TryGetProperty("Listings", out listings))
        {
            return map;
        }

        foreach (var prop in listings.EnumerateObject())
        {
            var b = prop.Value.TryGetProperty("baseListing", out var bl) ? bl
                : prop.Value.TryGetProperty("BaseListing", out var bl2) ? bl2 : (JsonElement?)null;
            if (b is null)
            {
                continue;
            }

            int images = 0;
            if (b.Value.TryGetProperty("images", out var im) || b.Value.TryGetProperty("Images", out im))
            {
                images = im.ValueKind == JsonValueKind.Array ? im.GetArrayLength() : 0;
            }

            map[prop.Name] = (
                Str(b.Value, "description", "Description"),
                Str(b.Value, "releaseNotes", "ReleaseNotes"),
                Str(b.Value, "shortDescription", "ShortDescription"),
                images);
        }

        return map;

        static string Str(JsonElement e, params string[] names)
        {
            foreach (string n in names)
            {
                if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    return v.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }
    }

    public static async Task<Dictionary<string, string>> WaitForSubmissionContextAsync(
        IPage page, int timeoutMs, Action<string>? log = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        Dictionary<string, string> sections = new(StringComparer.OrdinalIgnoreCase);
        for (;;)
        {
            sections = await PortalDriver.SectionUrlsAsync(page);
            if (sections.TryGetValue("submissionId", out string? id) && !string.IsNullOrWhiteSpace(id)
                && sections.TryGetValue("packages", out string? pkg) && !string.IsNullOrWhiteSpace(pkg))
            {
                return sections;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(5000);
        }

        await PortalDriver.InventoryAsync(page);
        await PortalDriver.ArtifactAsync(page, $"no-submission-context-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        throw new Core.DecoException(
            "No submission context after Start update (no /submissions/<id>/packages link). " +
            "The previous submission may still be in certification, or the workspace did not route — see artifacts.",
            "portal-no-context");
    }

    public static async Task GotoNetworkIdleAsync(IPage page, string url)
    {
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 20000 });
        }
        catch
        {
        }

        await Task.Delay(8000);
    }

    public sealed record RunResult(string ManifestPath, List<string> Steps);

    /// <summary>Full 11-step run. Throws DecoException (artifacts saved) on any gate failure.</summary>
    public static async Task<RunResult> RunAsync(RunPlan plan, Action<string>? log = null, CancellationToken ct = default)
    {
        PortalDriver.PruneArtifacts();
        var progress = new PortalDriver.Progress(11, plan.ProgressFile);
        var manifest = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["storeId"] = plan.StoreId,
            ["upload"] = plan.MsixUpload,
            ["startedUtc"] = DateTime.UtcNow.ToString("o"),
            ["steps"] = new List<string>(),
        };
        void StepDone(string name)
        {
            ((List<string>)manifest["steps"]!).Add(name);
            progress.Done();
        }

        PortalDriver.Session? session = null;
        IPage page;
        Func<Task> close;
        try
        {
            session = await PortalDriver.LaunchAsync(
                plan.ProfileDir, plan.Headed, trace: true, log, ct);
            page = session.Page;
            close = session.Close;

            // 1. login (headless-first + assist).
            progress.Step("login");
            log?.Invoke("  quick session check (headless, ~75s) — the login window opens right after if needed...");
            await page.GotoAsync(plan.SiteConfig.Dashboard, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            bool ok = await PortalDriver.WaitForLoginAsync(page, 75,
                (n, left, url) => log?.Invoke($"  waiting for login... ({n}x, {left}s left, at {url[..Math.Min(url.Length, 90)]})"));
            if (!ok && !plan.Headed && !plan.NoLoginAssist && Environment.UserInteractive)
            {
                log?.Invoke("  headless session is not logged in: popping ONE visible window for sign-in...");
                (page, close) = await PortalDriver.LoginAssistAsync(close,
                    plan.ProfileDir, plan.LoginTimeoutSec, log);
                ok = await PortalDriver.WaitForLoginAsync(page, 75, null);
            }

            if (!ok)
            {
                throw new Core.DecoException(
                    "Login still needed (non-interactive run, or the assist window timed out).",
                    "portal-login-needed",
                    Core.ExitCodes.UsageOrAuthNeeded);
            }

            StepDone("login-ok");

            // 2. app overview.
            progress.Step("app overview");
            ILocator? workspace = await PortalDriver.FindClickableForTextAsync(page, "Apps and games");
            if (workspace is not null)
            {
                await PortalDriver.ClickStepAsync(page, "workspace card", workspace);
                await Task.Delay(8000);
            }
            else
            {
                await GotoNetworkIdleAsync(page, plan.SiteConfig.AppsAndGames);
            }

            ILocator? appLink = null;
            DateTimeOffset pollEnd = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < pollEnd && appLink is null)
            {
                await Task.Delay(5000);
                appLink = await PortalDriver.FindClickableForTextAsync(page, plan.AppName)
                    ?? await PortalDriver.FindClickableForTextAsync(page, plan.StoreId);
            }

            if (appLink is null)
            {
                await PortalDriver.InventoryAsync(page);
                PortalDriver.Checkpoint("Manual assist",
                    "The script can't find your app link (route may have moved). In the OPENED browser window, click into the app yourself, so its overview page is showing.",
                    plan.AutoConfirm);
                appLink = await PortalDriver.FindControlAsync(page, "a", [plan.AppName, plan.StoreId]);
            }

            if (appLink is not null)
            {
                await PortalDriver.ClickStepAsync(page, "app link", appLink);
                await Task.Delay(8000);
            }

            StepDone("app-open");

            // 3. start update.
            progress.Step("start update");
            PortalDriver.Checkpoint("Start update", "Click 'Start update' (or open the in-progress draft).", plan.AutoConfirm);
            ILocator? startUpdate = await PortalDriver.FindControlAsync(page, "button", ["Start update", "New submission", "Update"]);
            if (startUpdate is null)
            {
                await PortalDriver.InventoryAsync(page);
                await PortalDriver.ArtifactAsync(page, $"start-update-missing-{Now()}");
                throw new Core.DecoException("Start-update control not found.", "portal-control-missing");
            }

            await PortalDriver.ClickStepAsync(page, "start update", startUpdate);
            await Task.Delay(10000);
            StepDone("update-started");

            // 4. price baseline (+ API baseline via msstore).
            progress.Step("price baseline");
            string pricingUrl = await PricingSectionUrlAsync(page);
            if (string.IsNullOrWhiteSpace(pricingUrl))
            {
                await PortalDriver.InventoryAsync(page);
                await PortalDriver.ArtifactAsync(page, $"pricing-section-missing-{Now()}");
                throw new Core.DecoException("Pricing section URL not found (no anchor, no nav link).", "portal-pricing");
            }

            await GotoNetworkIdleAsync(page, pricingUrl);
            PriceTable priceBaseline = await ScrapePricesAsync(page);
            if (string.IsNullOrWhiteSpace(priceBaseline.Base) && priceBaseline.Rows.Count == 0)
            {
                await PortalDriver.InventoryAsync(page);
                await PortalDriver.ArtifactAsync(page, $"pricing-unreadable-{Now()}");
                throw new Core.DecoException("Price table unreadable (no base price, no market rows) - refusing to continue blind.", "portal-pricing");
            }

            log?.Invoke($"  price baseline: base=\"{priceBaseline.Base[..Math.Min(80, priceBaseline.Base.Length)]}\" markets={priceBaseline.Rows.Count}");
            manifest["pricingBaseline"] = PriceCanonical(priceBaseline);
            var runner = new ProcessRunner();
            string apiSnapshot = await ApiListingSnapshotAsync(runner, plan.MsstoreExe, plan.StoreId, log);
            manifest["apiBaseline"] = apiSnapshot;
            StepDone("price-baseline");

            // 5. packages.
            var sections = await WaitForSubmissionContextAsync(page, 90000, log);
            log?.Invoke($"  section URLs: submission={sections.GetValueOrDefault("submissionId", "?")} packages={(sections.ContainsKey("packages") ? "yes" : "NO")}");
            if (sections.TryGetValue("packages", out string? packagesUrl) && !string.IsNullOrWhiteSpace(packagesUrl))
            {
                await GotoNetworkIdleAsync(page, packagesUrl);
            }
            else
            {
                await PortalDriver.ClickStepAsync(page, "open the Packages section",
                    page.GetByText(new Regex("^packages$", RegexOptions.IgnoreCase)),
                    async () =>
                    {
                        try
                        {
                            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 20000 });
                        }
                        catch
                        {
                        }

                        if (!Regex.IsMatch(page.Url, "packages", RegexOptions.IgnoreCase))
                        {
                            throw new Core.DecoException("not on the Packages section page", "portal-nav");
                        }

                        await page.Locator("input[type=\"file\"]").First.WaitForAsync(
                            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15000 });
                    });
            }

            manifest["steps"] = ((List<string>)manifest["steps"]!);
            ((List<string>)manifest["steps"]!).Add("packages-open");
            progress.Step("packages");
            await SweepStaleRowsAsync(page, plan.MsixUpload, log);
            var rows = await ScanRowsAsync(page, Path.GetFileName(plan.MsixUpload));
            bool alreadyStaged = rows.Any(r => r.Staged);
            if (plan.SkipPackages)
            {
                if (!rows.Any(r => r.Staged))
                {
                    await PortalDriver.InventoryAsync(page);
                    await PortalDriver.ArtifactAsync(page, $"packages-empty-{Now()}");
                    throw new Core.DecoException("--skip-packages but no staged package in the draft (drop the flag to upload).", "portal-packages");
                }

                log?.Invoke("  ok: reusing staged packages (--skip-packages, no upload)");
                StepDone("packages-reused");
            }
            else
            {
                if (!alreadyStaged)
                {
                    await UploadPackageAsync(page, plan.MsixUpload, log);
                }
                else
                {
                    log?.Invoke("  ok: package already staged, skipping the picker");
                }

                await WaitValidatedAsync(page, plan.MsixUpload, log);
                StepDone("packages-uploaded");
            }

            // 6. store listings per locale.
            progress.Step("store listings");
            await EditListingsAsync(page, plan, sections, log);
            StepDone("listings");

            // 7. screenshots per locale.
            await UploadShotsAsync(page, plan, log);
            StepDone("screenshots");

            // 8. price verify.
            progress.Step("price verify");
            await GotoNetworkIdleAsync(page, pricingUrl);
            PriceTable priceNow = await ScrapePricesAsync(page);
            if (PriceCanonical(priceNow) != (string)manifest["pricingBaseline"]!)
            {
                await PortalDriver.InventoryAsync(page);
                await PortalDriver.ArtifactAsync(page, $"pricing-changed-{Now()}");
                throw new Core.DecoException("Price table changed during this run. Aborting.", "portal-pricing");
            }

            log?.Invoke($"  price verify: unchanged (markets={priceNow.Rows.Count})");
            manifest["pricingVerified"] = true;
            StepDone("price-verify");

            // 9. publish mode.
            progress.Step("publish mode");
            await EnsurePublishModeAsync(page, plan, sections, log);
            StepDone("publish-mode");

            // 10. pre-submit review (FINAL checkpoint always prompts).
            progress.Step("pre-submit review");
            await GotoNetworkIdleAsync(page, plan.SiteConfig.Overview(plan.StoreId));
            try
            {
                string body = await page.Locator("body").InnerTextAsync();
                var flags = body.Split('\n').Select(l => l.Trim())
                    .Where(l => Regex.IsMatch(l, "error|incomplete|attention|missing|fix", RegexOptions.IgnoreCase) && l.Length < 200)
                    .Take(10).ToList();
                if (flags.Count > 0)
                {
                    log?.Invoke("  pre-submit flags on overview (continuing — certification decides):");
                    foreach (string f in flags)
                    {
                        log?.Invoke($"    ! {f}");
                    }
                }
                else
                {
                    log?.Invoke("  pre-submit scan: no error/attention lines on the overview");
                }
            }
            catch
            {
            }

            PortalDriver.Checkpoint("FINAL REVIEW",
                "Automated pre-submit scan above; launching this run was the consent.",
                plan.AutoConfirm, final: true);
            StepDone("reviewed");

            // 11. submit.
            await GotoNetworkIdleAsync(page, plan.SiteConfig.Overview(plan.StoreId));
            progress.Step("submit");
            string beforeSubmit = page.Url;
            try
            {
                await PortalDriver.ClickStepAsync(page, "submit for certification",
                    page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = new Regex("submit for certification|submit", RegexOptions.IgnoreCase) }),
                    async () =>
                    {
                        try
                        {
                            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 30000 });
                        }
                        catch
                        {
                        }
                    });
            }
            catch (Exception ex)
            {
                await PortalDriver.InventoryAsync(page);
                await PortalDriver.ArtifactAsync(page, $"submit-missing-{Now()}");
                throw new Core.DecoException($"Submit button not found. {ex.Message}", "portal-control-missing");
            }

            await Task.Delay(5000);
            log?.Invoke($"  after submit: {page.Url} (was {beforeSubmit})");
            StepDone("submitted");

            manifest["finishedUtc"] = DateTime.UtcNow.ToString("o");
            manifest["progressFile"] = plan.ProgressFile;
            Directory.CreateDirectory(Path.GetDirectoryName(plan.ManifestPath)!);
            await File.WriteAllTextAsync(plan.ManifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            log?.Invoke($"Submitted. Manifest: {plan.ManifestPath}");
            return new RunResult(plan.ManifestPath, (List<string>)manifest["steps"]!);
        }
        catch
        {
            if (session is not null)
            {
                try
                {
                    await PortalDriver.ArtifactAsync(session.Page, "submit-failed");
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.Close();
                }
                catch
                {
                }
            }
        }
    }

    internal static async Task SweepStaleRowsAsync(IPage page, string msixUpload, Action<string>? log)
    {
        List<RowFlag> rows;
        try
        {
            rows = await ScanRowsAsync(page, Path.GetFileName(msixUpload));
        }
        catch
        {
            return;
        }

        if (rows.Count == 0)
        {
            log?.Invoke("  (no previous rows for our files)");
        }

        foreach (var row in rows)
        {
            if (row.Staged)
            {
                log?.Invoke($"  staged in device table (never deleting): \"{row.Text[..Math.Min(160, row.Text.Length)]}\"");
                continue;
            }

            bool stuck = Regex.IsMatch(row.Text, "paused|failed|error|taking a long time", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(row.Text, "validated", RegexOptions.IgnoreCase);
            log?.Invoke($"  existing row (stuck={stuck}): \"{row.Text[..Math.Min(160, row.Text.Length)]}\"");
            if (!stuck)
            {
                continue;
            }

            var del = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameRegex = new Regex("^(delete|remove)$", RegexOptions.IgnoreCase) }).First;
            if (await del.IsVisibleAsync())
            {
                await del.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                await Task.Delay(3000);
                var confirm = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = new Regex("^delete$", RegexOptions.IgnoreCase) }).First;
                if (await confirm.IsVisibleAsync())
                {
                    await confirm.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                    await Task.Delay(3000);
                }

                log?.Invoke("  ok: stale previous upload row cleared");
            }
        }
    }

    internal static async Task UploadPackageAsync(IPage page, string msixUpload, Action<string>? log)
    {
        try
        {
            var chooserTask = page.WaitForFileChooserAsync(new PageWaitForFileChooserOptions { Timeout = 20000 });
            await PortalDriver.ClickStepAsync(page, "open the package picker",
                page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameRegex = new Regex("browse your files", RegexOptions.IgnoreCase) }));
            var chooser = await chooserTask;
            await chooser.SetFilesAsync(msixUpload);
            log?.Invoke("  ok: package file handed to the picker");
            return;
        }
        catch
        {
        }

        var inputs = page.Locator("input[type=\"file\"]");
        if (await inputs.CountAsync() == 0)
        {
            await PortalDriver.InventoryAsync(page);
            await PortalDriver.ArtifactAsync(page, $"package-input-missing-{Now()}");
            throw new Core.DecoException("No file input for the package upload (see artifacts).", "portal-packages");
        }

        await inputs.First.SetInputFilesAsync(msixUpload);
        log?.Invoke("  ok: package file set on the input directly");
    }

    internal static async Task WaitValidatedAsync(IPage page, string msixUpload, Action<string>? log)
    {
        log?.Invoke("  waiting for Partner Center to validate the upload (up to 20 min)...");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(30000);
            List<RowFlag> rows;
            try
            {
                rows = await ScanRowsAsync(page, Path.GetFileName(msixUpload));
            }
            catch
            {
                continue;
            }

            foreach (var r in rows)
            {
                log?.Invoke($"  probe: staged={r.Staged} working={r.Working}: \"{r.Text[..Math.Min(140, r.Text.Length)]}\"");
            }

            if (rows.Any(r => r.Staged))
            {
                log?.Invoke("  ok: package validated and staged");
                return;
            }

            if (rows.Any(r => Regex.IsMatch(r.Text, "failed|error", RegexOptions.IgnoreCase)))
            {
                throw new Core.DecoException("Package validation failed (error row).", "portal-packages");
            }
        }

        throw new Core.DecoException("Package validation timed out after 20 min.", "portal-packages");
    }

    internal static async Task EditListingsAsync(IPage page, RunPlan plan, Dictionary<string, string> sections, Action<string>? log)
    {
        // Submission left-nav → Store listings (fallback: direct section URL).
        string listingsDirect = sections.TryGetValue("listings", out string? l) && !string.IsNullOrWhiteSpace(l) ? l
            : sections.TryGetValue("managelanguages", out string? m) && !string.IsNullOrWhiteSpace(m) ? m
            : sections.TryGetValue("submissionId", out string? sid) && !string.IsNullOrWhiteSpace(sid)
                ? plan.SiteConfig.Listings(plan.StoreId, sid) : string.Empty;
        try
        {
            await PortalDriver.ClickStepAsync(page, "open the Store listings section",
                page.Locator("nav, aside, [role=\"navigation\"]").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { NameRegex = new Regex("^store listings$", RegexOptions.IgnoreCase) }),
                async () =>
                {
                    await page.WaitForURLAsync(new Regex("listings|managelanguages|languageid", RegexOptions.IgnoreCase),
                        new PageWaitForURLOptions { Timeout = 15000 });
                    await WaitForLanguageEntriesAsync(page, 60000, log);
                });
        }
        catch when (!string.IsNullOrWhiteSpace(listingsDirect))
        {
            log?.Invoke("  left-nav did not route; going straight to the listings section URL...");
            await GotoNetworkIdleAsync(page, listingsDirect);
        }

        if (!Regex.IsMatch(page.Url, "listings|managelanguages|languageid", RegexOptions.IgnoreCase))
        {
            await PortalDriver.InventoryAsync(page);
            await PortalDriver.ArtifactAsync(page, $"listings-row-missing-{Now()}");
            throw new Core.DecoException($"Store listings section not reachable (stuck at {page.Url}).", "portal-listings");
        }

        foreach (var (tag, content) in plan.LocaleContent)
        {
            await EditOneLocaleAsync(page, plan, tag, content, log);
        }

        log?.Invoke("  listing text: pasted + read-back verified per locale");
    }

    internal static async Task WaitForLanguageEntriesAsync(IPage page, int timeoutMs, Action<string>? log)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        string[] langNames = ["english", "french", "spanish", "german"];
        for (;;)
        {
            bool entries = false;
            foreach (var frame in page.Frames)
            {
                foreach (string lang in langNames)
                {
                    try
                    {
                        if (await frame.GetByRole(AriaRole.Link, new FrameGetByRoleOptions { NameRegex = new Regex(lang, RegexOptions.IgnoreCase) }).First.IsVisibleAsync())
                        {
                            entries = true;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }

                if (entries)
                {
                    break;
                }
            }

            if (entries)
            {
                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new Core.DecoException("No language entries rendered.", "portal-listings");
            }

            await Task.Delay(3000);
        }
    }

    internal static async Task EditOneLocaleAsync(
        IPage page, RunPlan plan, string tag, LocaleText content, Action<string>? log)
    {
        // Find this locale's editor link (tag match, then language-name match).
        string? editorUrl = null;
        for (int attempt = 0; attempt < 12 && editorUrl is null; attempt++)
        {
            var hrefs = await CollectLangHrefsAsync(page);
            var usable = hrefs.Where(h => !Regex.IsMatch(h.Href, "signin|aad\\?action", RegexOptions.IgnoreCase)).ToList();
            var hit = usable.FirstOrDefault(h => h.Href.Contains(tag, StringComparison.OrdinalIgnoreCase))
                ?? usable.FirstOrDefault(h => Regex.IsMatch(h.Text, LanguageNameRe(tag)));
            if (hit is not null)
            {
                editorUrl = hit.Href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? hit.Href : $"https://partner.microsoft.com{hit.Href}";
            }
            else
            {
                await Task.Delay(5000);
            }
        }

        if (editorUrl is null)
        {
            await PortalDriver.InventoryAsync(page);
            await PortalDriver.ArtifactAsync(page, $"locale-editor-missing-{tag}-{Now()}");
            throw new Core.DecoException($"No editor link for {tag}.", "portal-listings");
        }

        await GotoNetworkIdleAsync(page, editorUrl);
        // Poll for the description box (Angular renders late).
        ILocator? desc = null;
        ILocator? notes = null;
        for (int w = 0; w < 18 && desc is null; w++)
        {
            foreach (var frame in page.Frames)
            {
                try
                {
                    var d = frame.Locator("#description-required");
                    if (await d.CountAsync() > 0 && await d.First.IsVisibleAsync())
                    {
                        desc = d.First;
                        var n = frame.Locator("#releaseNotes");
                        notes = await n.CountAsync() > 0 ? n.First : null;
                        break;
                    }
                }
                catch
                {
                }
            }

            if (desc is null)
            {
                await Task.Delay(5000);
            }
        }

        if (desc is null)
        {
            await PortalDriver.InventoryAsync(page);
            await PortalDriver.ArtifactAsync(page, $"editor-form-missing-{tag}-{Now()}");
            throw new Core.DecoException($"Listing editor form not found for {tag}.", "portal-listings");
        }

        await desc.FillAsync(content.Description);
        if (notes is not null)
        {
            await notes.FillAsync(content.Notes);
        }

        await PortalDriver.ClickStepAsync(page, $"save the {tag} listing editor",
            page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = new Regex("^save$", RegexOptions.IgnoreCase) }));
        log?.Invoke($"  {tag}: pasted description ({content.Description.Length} chars) + notes, saved");
    }

    internal static string LanguageNameRe(string tag) => tag switch
    {
        "en-US" => "english",
        "es-ES" => "spanish|españa",
        "fr-FR" => "french|français|france",
        _ => Regex.Escape(tag),
    };

    internal sealed record LangHref(string Href, string Text);

    internal static async Task<List<LangHref>> CollectLangHrefsAsync(IPage page)
    {
        var all = new List<LangHref>();
        foreach (string selector in new[] { "a[href*=\"languageid=\"]", "a[href*=\"/submissions/\"]" })
        {
            foreach (var frame in page.Frames)
            {
                try
                {
                    var hrefs = await frame.Locator(selector).EvaluateAllAsync<JsonElement>(
                        "els => els.map(el => ({ href: el.getAttribute('href') || '', text: (el.innerText || '').replace(/\\s+/g, ' ').trim().slice(0, 60) }))");
                    foreach (var h in hrefs.EnumerateArray())
                    {
                        string href = h.TryGetProperty("href", out var hv) ? hv.GetString() ?? string.Empty : string.Empty;
                        string text = h.TryGetProperty("text", out var tv) ? tv.GetString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrWhiteSpace(href) && !all.Any(x => x.Href == href))
                        {
                            all.Add(new LangHref(href, text));
                        }
                    }
                }
                catch
                {
                }
            }

            if (all.Count > 0)
            {
                break;
            }
        }

        return all.Where(h => Regex.IsMatch(h.Text, "english|french|spanish|german|united states|france|espana|spain|deutschland", RegexOptions.IgnoreCase)).ToList();
    }

    internal static async Task UploadShotsAsync(IPage page, RunPlan plan, Action<string>? log)
    {
        foreach (var (tag, files) in plan.ShotsByLocale)
        {
            if (files.Count == 0)
            {
                continue;
            }

            foreach (string shot in files)
            {
                var inputs = page.Locator("input[type=\"file\"]");
                if (await inputs.CountAsync() == 0)
                {
                    await PortalDriver.InventoryAsync(page);
                    await PortalDriver.ArtifactAsync(page, $"shots-input-missing-{Now()}");
                    throw new Core.DecoException("No file input for screenshots.", "portal-shots");
                }

                await inputs.First.SetInputFilesAsync(shot);
                await Task.Delay(5000);
            }

            log?.Invoke($"  {tag}: {files.Count} screenshot(s) uploaded");
        }

        await PortalDriver.ClickStepAsync(page, "save screenshots",
            page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = new Regex("^save$", RegexOptions.IgnoreCase) }));
    }

    internal static async Task EnsurePublishModeAsync(
        IPage page, RunPlan plan, Dictionary<string, string> sections, Action<string>? log)
    {
        string optionsUrl = string.Empty;
        if (sections.TryGetValue("submissionId", out string? sid) && !string.IsNullOrWhiteSpace(sid))
        {
            var fresh = await PortalDriver.SectionUrlsAsync(page);
            optionsUrl = fresh.FirstOrDefault(kv => Regex.IsMatch(kv.Key, "submissionoption", RegexOptions.IgnoreCase)).Value ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(optionsUrl))
        {
            foreach (var frame in page.Frames)
            {
                try
                {
                    string href = await frame
                        .GetByRole(AriaRole.Link, new FrameGetByRoleOptions { NameRegex = new Regex("submission options", RegexOptions.IgnoreCase) })
                        .First.GetAttributeAsync("href") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(href))
                    {
                        optionsUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? href : $"https://partner.microsoft.com{href}";
                        break;
                    }
                }
                catch
                {
                }
            }
        }

        if (string.IsNullOrWhiteSpace(optionsUrl) && sections.TryGetValue("submissionId", out string? sid2))
        {
            optionsUrl = plan.SiteConfig.Options(plan.StoreId, sid2);
        }

        if (string.IsNullOrWhiteSpace(optionsUrl))
        {
            await PortalDriver.InventoryAsync(page);
            await PortalDriver.ArtifactAsync(page, $"submission-options-missing-{Now()}");
            throw new Core.DecoException("Submission Options section not found.", "portal-options");
        }

        await GotoNetworkIdleAsync(page, optionsUrl);
        bool wantImmediate = Regex.IsMatch(plan.PublishMode, "^immediate$", RegexOptions.IgnoreCase);
        string modeNow = await ReadPublishModeAsync(page);
        if (!string.IsNullOrWhiteSpace(modeNow)
            && !string.Equals(modeNow, plan.PublishMode, StringComparison.OrdinalIgnoreCase))
        {
            var label = new Regex(wantImmediate
                ? "publish this submission as soon as"
                : "don't publish this submission until i select publish now", RegexOptions.IgnoreCase);
            bool set = false;
            foreach (var frame in page.Frames)
            {
                try
                {
                    var radio = frame.GetByRole(AriaRole.Radio, new FrameGetByRoleOptions { NameRegex = label }).First;
                    if (await radio.IsVisibleAsync())
                    {
                        await radio.CheckAsync(new LocatorCheckOptions { Timeout = 10000 });
                        set = true;
                        break;
                    }
                }
                catch
                {
                }
            }

            if (!set)
            {
                throw new Core.DecoException("Could not set the publish mode (control unrecognizable) — aborting instead of assuming.", "portal-options");
            }

            await PortalDriver.ClickStepAsync(page, "save the Submission options",
                page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = new Regex("^save$", RegexOptions.IgnoreCase) }));
            modeNow = await ReadPublishModeAsync(page);
        }

        if (!string.Equals(modeNow, plan.PublishMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new Core.DecoException($"Publish mode is '{modeNow}', want '{plan.PublishMode}'.", "portal-options");
        }

        log?.Invoke($"  publish mode verified: {modeNow}");
    }

    internal static async Task<string> ReadPublishModeAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                var radios = frame.GetByRole(AriaRole.Radio);
                int n = await radios.CountAsync();
                for (int k = 0; k < n; k++)
                {
                    var r = radios.Nth(k);
                    if (!await r.IsVisibleAsync())
                    {
                        continue;
                    }

                    string name = await r.GetAttributeAsync("aria-label") ?? string.Empty;
                    string text = (((await r.InnerTextAsync()) ?? string.Empty) + " " + name)
                        .Replace("\n", " ").Replace("\r", " ").Trim();
                    if (!Regex.IsMatch(text, "publish this submission|publish now|start publishing", RegexOptions.IgnoreCase))
                    {
                        continue;
                    }

                    if (await r.IsCheckedAsync())
                    {
                        if (Regex.IsMatch(text, "don't publish this submission until", RegexOptions.IgnoreCase))
                        {
                            return "Manual";
                        }

                        if (Regex.IsMatch(text, "publish this submission as soon as", RegexOptions.IgnoreCase))
                        {
                            return "Immediate";
                        }

                        return $"Scheduled:{text[..Math.Min(60, text.Length)]}";
                    }
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
