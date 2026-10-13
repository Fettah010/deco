using Microsoft.Playwright;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevCli.Infrastructure;

/// <summary>
/// Dedicated Partner Center BASE-PRICE driver. Reads the base price through
/// JS that understands both native selects and the custom che-select /
/// listbox controls, sets currency + retail price, saves, reads back and
/// verifies. Every step screenshots into the run dir; diagnose mode is
/// strictly read-only (JS reads + screenshots, zero clicks).
/// </summary>
public static class PortalPricing
{
    public sealed record Plan(
        string StoreId,
        string SubmissionId,
        string Currency,
        string Price,
        string ProfileDir,
        bool Apply,
        bool Submit,
        bool AutoConfirm,
        bool Headed,
        bool NoLoginAssist,
        int LoginTimeoutSec,
        string RunDir,
        bool SkipLogin,
        string AvailabilityUrlOverride,
        PortalSubmission.Site SiteConfig);

    public sealed record BasePrice(
        string Currency, string Price,
        string CurrencyStrategy, string PriceStrategy,
        List<string> PriceOptions, List<string> CurrencyOptions,
        string PricePrefix, List<string> PriceRawOptions, List<string> VisiblePriceRawOptions,
        List<string> AllOptions);

    public sealed record RunResult(
        string RunDir, string ManifestPath,
        BasePrice Baseline, BasePrice? Final,
        bool Saved, bool SaveSignal, bool Submitted, string SubmitNote,
        List<string> Steps, List<string> Shots);

    public const string ReadBasePriceJs = """
        () => {
          const norm = (s) => (s || '').replace(/[\s ]+/g, ' ').trim();
          const out = { currency: '', price: '', cstrat: '', pstrat: '', prefix: '', options: [], coptions: [], praw: [], visraw: [], alloptions: [] };
          const ctxText = (el, depth) => {
            let t = ''; let e = el;
            for (let d = 0; d < depth && e; d++) { e = e.parentElement; if (!e) break; t += ' ' + norm(e.innerText || ''); }
            return t.slice(0, 500);
          };
          for (const sel of document.querySelectorAll('select')) {
            if (/currency/i.test(ctxText(sel, 4))) {
              const t = norm(sel.options[sel.selectedIndex]?.text || sel.value || '');
              if (t) { out.currency = t; out.cstrat = 'native-select'; out.coptions = [...sel.options].map(o => norm(o.text)).filter(x => x); break; }
            }
          }
          // he-select controls (label attr, he-option children, selected via
          // aria-selected; the retail control prefixes a slot="start" symbol).
          for (const hs of document.querySelectorAll('he-select')) {
            const lab = norm(hs.getAttribute('label') || hs.getAttribute('aria-label') || '');
            const selOpt = hs.querySelector('he-option[aria-selected="true"], he-option[selected]');
            const selText = norm(selOpt ? selOpt.innerText : '');
            const local = [...hs.querySelectorAll('he-option')].map(o => norm(o.innerText)).filter(t => t);
            if (/^currency$/i.test(lab)) {
              if (selText) { out.currency = selText; out.cstrat = 'he-select'; }
              out.coptions = local;
            }
            if (/retail price/i.test(lab)) {
              const slot = hs.querySelector('[slot="start"]');
              const prefix = norm(slot ? slot.innerText : '');
              out.prefix = prefix;
              if (selText) { out.price = norm(prefix + ' ' + selText); out.pstrat = 'he-select'; }
              out.options = local.map(t => norm(prefix + ' ' + t)).filter(t => t);
              out.praw = local;
              const isVis = (el) => {
                try {
                  const r = el.getBoundingClientRect();
                  const cs = getComputedStyle(el);
                  return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none';
                } catch { return false; }
              };
              out.visraw = [...hs.querySelectorAll('he-option')].filter(isVis).map(o => norm(o.innerText)).filter(t => t);
            }
          }
          // Global option pool (covers body-level overlays + shadow roots).
          const seen = new Set();
          const walk = (root) => {
            for (const o of root.querySelectorAll('he-option, [role="option"], option')) {
              const t = norm(o.innerText);
              if (t && !seen.has(t)) { seen.add(t); out.alloptions.push(t); }
            }
            for (const el of root.querySelectorAll('*')) { if (el.shadowRoot) walk(el.shadowRoot); }
          };
          try { walk(document); } catch {}
          if (!out.currency) {
            for (const c of document.querySelectorAll('che-select, [role="combobox"]')) {
              const lab = norm(c.getAttribute('label') || c.getAttribute('aria-label') || '');
              if (/^currency$/i.test(lab)) {
                const first = norm((c.innerText || '').split('\n')[0]);
                if (first) {
                  out.currency = first; out.cstrat = 'labeled-control';
                  out.coptions = [...c.querySelectorAll('[role="option"], option, .che-option, li')].map(o => norm(o.innerText)).filter(x => x && x !== first);
                  break;
                }
              }
            }
          }
          const lbs = [...document.querySelectorAll('[role="listbox"]')];
          const lb = lbs.find(e => /retail price/i.test(e.getAttribute('aria-label') || ''));
          if (lb) {
            const opts = [...lb.querySelectorAll('[role="option"]')].map(o => norm(o.innerText)).filter(t => t);
            out.options = opts;
            const sel = lb.querySelector('[aria-selected="true"], .selected, .active');
            if (sel) { out.price = norm(sel.innerText); out.pstrat = 'listbox-selected'; }
            else if (opts.length === 0) { out.price = norm(lb.innerText); out.pstrat = 'collapsed-trigger'; }
          }
          if (!out.price) {
            const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
            let labelNode = null;
            while (walker.nextNode()) {
              if (/retail price/i.test(walker.currentNode.nodeValue || '')) { labelNode = walker.currentNode; break; }
            }
            if (labelNode) {
              let box = labelNode.parentElement;
              for (let d = 0; d < 6 && box && box !== document.body; d++) box = box.parentElement;
              const cands = [];
              const w2 = document.createTreeWalker(box || document.body, NodeFilter.SHOW_TEXT);
              while (w2.nextNode()) {
                const t = norm(w2.currentNode.nodeValue || '');
                if (t.length > 0 && t.length < 40 && /[$€£¥]\s*\d/.test(t)) cands.push(t);
              }
              if (cands.length > 0) { out.price = cands[0]; out.pstrat = 'label-proximity'; }
            }
          }
          return JSON.stringify(out);
        }
        """;

    public const string ClickTextJs = """
        (want) => {
          const norm = (s) => (s || '').replace(/[\s ]+/g, ' ').trim();
          const scopeSel = '.price-tier-selection, [role="listbox"], che-select, he-select';
          const inScope = (el) => el && el.closest && el.closest(scopeSel);
          const isOption = (el) => el && ((el.tagName === 'HE-OPTION' || el.tagName === 'OPTION') || (el.getAttribute && el.getAttribute('role') === 'option'));
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          const cands = [];
          while (walker.nextNode()) {
            if (norm(walker.currentNode.nodeValue) === want) {
              let el = walker.currentNode.parentElement;
              while (el && el !== document.body && norm(el.innerText) !== want) el = el.parentElement;
              if (el && el !== document.body) cands.push(el);
            }
          }
          if (cands.length === 0) return 'not-found';
          cands.sort((a, b) => ((inScope(a) ? 0 : 1) - (inScope(b) ? 0 : 1)) || ((isOption(a) ? 0 : 1) - (isOption(b) ? 0 : 1)));
          const el = cands[0];
          try { el.scrollIntoView({ block: 'center' }); } catch {}
          try { el.click(); return 'clicked:' + (el.tagName || '?') + '#' + (el.id || ''); }
          catch (e) { return 'click-threw:' + e; }
        }
        """;

    public const string OpenSelectJs = """
        (label) => {
          const norm = (s) => (s || '').replace(/[\s ]+/g, ' ').trim();
          const hs = [...document.querySelectorAll('he-select')].find(e => norm(e.getAttribute('label') || '') === label);
          if (!hs) return 'no-select';
          try { hs.scrollIntoView({ block: 'center' }); } catch {}
          try { hs.click(); return 'opened'; }
          catch (e) { return 'click-threw:' + e; }
        }
        """;

    public static string AvailabilityUrl(Plan plan) =>
        string.IsNullOrWhiteSpace(plan.AvailabilityUrlOverride)
            ? $"{plan.SiteConfig.ProductBase}/{plan.StoreId}/submissions/{plan.SubmissionId}/availability"
            : plan.AvailabilityUrlOverride;

    public static async Task<BasePrice> ReadBasePriceAsync(IPage page)
    {
        string json = await page.EvaluateAsync<string>(ReadBasePriceJs);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        var options = new List<string>();
        if (root.TryGetProperty("options", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in arr.EnumerateArray())
            {
                if (o.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(o.GetString()))
                {
                    options.Add(o.GetString()!);
                }
            }
        }

        var coptions = new List<string>();
        if (root.TryGetProperty("coptions", out var carr) && carr.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in carr.EnumerateArray())
            {
                if (o.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(o.GetString()))
                {
                    coptions.Add(o.GetString()!);
                }
            }
        }

        var alloptions = new List<string>();
        if (root.TryGetProperty("alloptions", out var aarr) && aarr.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in aarr.EnumerateArray())
            {
                if (o.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(o.GetString()))
                {
                    alloptions.Add(o.GetString()!);
                }
            }
        }

        var praw = new List<string>();
        if (root.TryGetProperty("praw", out var prawArr) && prawArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in prawArr.EnumerateArray())
            {
                if (o.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(o.GetString()))
                {
                    praw.Add(o.GetString()!);
                }
            }
        }

        var visraw = new List<string>();
        if (root.TryGetProperty("visraw", out var visArr) && visArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in visArr.EnumerateArray())
            {
                if (o.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(o.GetString()))
                {
                    visraw.Add(o.GetString()!);
                }
            }
        }

        return new BasePrice(
            Str(root, "currency"), Str(root, "price"),
            Str(root, "cstrat"), Str(root, "pstrat"),
            options.Distinct().ToList(), coptions.Distinct().ToList(),
            Str(root, "prefix"), praw.Distinct().ToList(), visraw.Distinct().ToList(),
            alloptions.Distinct().ToList());
    }

    public static async Task<string> ShotAsync(IPage page, string runDir, string name, Action<string>? log = null)
    {
        Directory.CreateDirectory(runDir);
        string path = Path.Combine(runDir, $"{name}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
        log?.Invoke($"  shot: {path}");
        return path;
    }

    internal static async Task<string> ClickTextAsync(IPage page, string normalizedText)
    {
        string res = await page.EvaluateAsync<string>(ClickTextJs, normalizedText);
        await Task.Delay(2500);
        return res;
    }

    /// <summary>
    /// Opens an he-select dropdown (container click, toggle-click fallback)
    /// until its local option pool grows past a single collapsed entry.
    /// Returns the latest read (options may live in a body overlay — callers
    /// fall back to the global pool, which the reader also collects).
    /// </summary>
    internal static async Task<BasePrice> OpenHeSelectAsync(
        IPage page, string label, Func<BasePrice, List<string>> localOf, Action<string>? log)
    {
        BasePrice state = await ReadBasePriceAsync(page);
        int initial = localOf(state).Count;
        for (int i = 0; i < 4 && localOf(state).Count <= initial && localOf(state).Count <= 1; i++)
        {
            string res;
            if (i % 2 == 0)
            {
                res = await page.EvaluateAsync<string>(OpenSelectJs, label);
            }
            else
            {
                string shown = label == "Currency" ? state.Currency : state.Price;
                res = string.IsNullOrWhiteSpace(shown) ? "no-target"
                    : await ClickTextAsync(page, Core.PortalPriceMatch.Normalize(shown));
            }

            await Task.Delay(2000);
            log?.Invoke($"  open '{label}' try {i + 1}: {res}");
            state = await ReadBasePriceAsync(page);
        }

        return state;
    }

    internal static async Task SelectCurrencyAsync(IPage page, Plan plan, Action<string>? log)
    {
        // Strategy 1: native select in a currency context.
        foreach (var sel in await page.Locator("select").AllAsync())
        {
            string ctx;
            try
            {
                ctx = await sel.EvaluateAsync<string>(
                    "el => { let t=''; for (let d=0; d<4 && el; d++){ el=el.parentElement; if(!el) break; t += ' ' + ((el.innerText||'').replace(/\\s+/g,' ')); } return t.slice(0,500); }");
            }
            catch
            {
                continue;
            }

            if (ctx.Contains("currency", StringComparison.OrdinalIgnoreCase))
            {
                string[] texts;
                try
                {
                    texts = await sel.EvaluateAsync<string[]>(
                        "el => [...el.options].map(o => (o.text||'').replace(/[\\s\\u00a0]+/g,' ').trim())");
                }
                catch
                {
                    continue;
                }

                string? match = Core.PortalPriceMatch.TryMatchCurrency(texts, plan.Currency);
                if (match is null)
                {
                    throw new Core.DecoException(
                        $"Currency '{plan.Currency}' not among native options [{string.Join(" | ", texts)}].",
                        "portal-price-option");
                }

                log?.Invoke($"  currency: native select → '{match}'");
                await sel.SelectOptionAsync(new SelectOptionValue { Label = match });
                await Task.Delay(2500);
                return;
            }
        }

        // Strategy 2: labeled/he-select custom control — explicit open, match
        // against local options (global pool fallback), pick, read-back assert.
        BasePrice before = await ReadBasePriceAsync(page);
        if (string.IsNullOrWhiteSpace(before.Currency)
            && before.CurrencyOptions.Count == 0 && before.AllOptions.Count == 0)
        {
            throw new Core.DecoException("Currency control not readable (no native select, no labeled control, no options anywhere).", "portal-price-control");
        }

        BasePrice opened = await OpenHeSelectAsync(page, "Currency", s => s.CurrencyOptions, log);
        List<string> copts = opened.CurrencyOptions.Count > 0 ? opened.CurrencyOptions : opened.AllOptions;

        log?.Invoke($"  currency options: [{string.Join(" | ", copts)}]");
        string? pick = Core.PortalPriceMatch.TryMatchCurrency(copts, plan.Currency);
        if (pick is null)
        {
            throw new Core.DecoException(
                $"Currency '{plan.Currency}' not among options [{string.Join(" | ", copts)}].",
                "portal-price-option");
        }

        log?.Invoke($"  currency: picking '{pick}'");
        string clicked = await ClickTextAsync(page, Core.PortalPriceMatch.Normalize(pick));
        log?.Invoke($"  currency pick: {clicked}");
        if (clicked == "not-found")
        {
            throw new Core.DecoException($"Currency option '{pick}' vanished before the click.", "portal-price-option");
        }

        BasePrice after = await ReadBasePriceAsync(page);
        if (Core.PortalPriceMatch.TryMatchCurrency([after.Currency], plan.Currency) is null)
        {
            throw new Core.DecoException(
                $"Currency did not stick: want '{plan.Currency}', control shows '{after.Currency}'.",
                "portal-price-verify");
        }

        log?.Invoke($"  currency: now '{after.Currency}'");
    }

    public const string ScrollTextJs = """
        (want) => {
          const norm = (s) => (s || '').replace(/[\s ]+/g, ' ').trim();
          const els = [...document.querySelectorAll('he-select[label="Retail price"] he-option')];
          const hit = els.find(e => norm(e.innerText) === want);
          if (!hit) return 'not-found';
          try { hit.scrollIntoView({ block: 'center' }); return 'scrolled'; }
          catch (e) { return 'scroll-threw:' + e; }
        }
        """;

    /// <summary>
    /// Fills the retail filter with ONE text (input if rendered, keystrokes
    /// otherwise). Callers try locale variants and check visibility between.
    /// </summary>
    internal static async Task FillFilterAsync(IPage page, string text, Action<string>? log)
    {
        try
        {
            var inputs = page.Locator("he-select[label=\"Retail price\"] input");
            int n = await inputs.CountAsync();
            if (n > 0)
            {
                await inputs.First.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                await inputs.First.FillAsync(text, new LocatorFillOptions { Timeout = 10000 });
                log?.Invoke($"  price filter: typed '{text}'");
            }
            else
            {
                var sel = page.Locator("he-select[label=\"Retail price\"]");
                await sel.First.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                await sel.First.PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Timeout = 10000 });
                log?.Invoke($"  price filter (keys): typed '{text}'");
            }

            await Task.Delay(4000);
        }
        catch (Exception ex)
        {
            log?.Invoke($"  price filter hiccup ({ex.Message}) — the option read decides");
        }
    }

    internal static bool TargetVisible(BasePrice state, string want) =>
        Core.PortalPriceMatch.TryMatchComposedPrice(
            state.VisiblePriceRawOptions, state.PricePrefix, want) is not null;

    internal static async Task SelectPriceAsync(IPage page, Plan plan, Action<string>? log)
    {
        BasePrice before = await ReadBasePriceAsync(page);
        if (string.IsNullOrWhiteSpace(before.Price) && string.IsNullOrWhiteSpace(before.Currency)
            && before.PriceOptions.Count == 0 && before.PriceRawOptions.Count == 0 && before.AllOptions.Count == 0)
        {
            throw new Core.DecoException("Retail price control not readable (no value, no options anywhere).", "portal-price-control");
        }

        // Explicit open (container clicks — the trigger text vanishes once
        // open, so no text-toggle here), then type-ahead if the tier list
        // still hasn't rendered, then match + pick + read-back assert.
        BasePrice opened = await ReadBasePriceAsync(page);
        bool heSelect = false;
        for (int i = 0; i < 2 && opened.PriceRawOptions.Count <= 1; i++)
        {
            string res = await page.EvaluateAsync<string>(OpenSelectJs, "Retail price");
            if (res == "opened")
            {
                heSelect = true;
            }
            if (res == "no-select" && !string.IsNullOrWhiteSpace(opened.Price))
            {
                // No he-select (older/custom controls): toggle via the
                // currently displayed value instead.
                res = await ClickTextAsync(page, Core.PortalPriceMatch.Normalize(opened.Price));
                opened = await ReadBasePriceAsync(page);
            }
            else
            {
                await Task.Delay(2500);
                opened = await ReadBasePriceAsync(page);
            }

            log?.Invoke($"  open 'Retail price' try {i + 1}: {res}");
        }

        // Patient poll: the per-currency tier fetch can lag the open by many
        // seconds. A list that renders on its own needs no filter (and no
        // filter text means nothing can shadow the pick at save time).
        for (int w = 0; w < 6 && opened.PriceRawOptions.Count <= 1; w++)
        {
            await Task.Delay(5000);
            opened = await ReadBasePriceAsync(page);
        }

        if (opened.PriceRawOptions.Count > 1)
        {
            log?.Invoke("  tier list rendered without filter");
        }

        if (opened.PriceRawOptions.Count <= 1)
        {
            // Render trigger: the want's own number token first, the
            // separator-swapped variant second — a locale-wrong filter hides
            // the target, so visibility (not DOM presence) decides.
            string token = new(Core.PortalPriceMatch.Normalize(plan.Price)
                .Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray());
            if (!string.IsNullOrWhiteSpace(token))
            {
                await FillFilterAsync(page, token, log);
                opened = await ReadBasePriceAsync(page);
                log?.Invoke($"  price visible options: [{string.Join(" | ", opened.VisiblePriceRawOptions.Take(12))}{(opened.VisiblePriceRawOptions.Count > 12 ? " | ..." : string.Empty)}]");
                if (!TargetVisible(opened, plan.Price))
                {
                    string swapped = Core.PortalPriceMatch.SwapSeparators(token);
                    if (swapped != token)
                    {
                        await FillFilterAsync(page, swapped, log);
                        opened = await ReadBasePriceAsync(page);
                        log?.Invoke($"  price visible options (swapped): [{string.Join(" | ", opened.VisiblePriceRawOptions.Take(12))}{(opened.VisiblePriceRawOptions.Count > 12 ? " | ..." : string.Empty)}]");
                    }
                }
            }
        }

        List<string> raws = opened.PriceRawOptions.Count > 0 ? opened.PriceRawOptions : opened.AllOptions;
        log?.Invoke($"  price options (raw): [{string.Join(" | ", raws)}] prefix='{opened.PricePrefix}'");

        string? pick = Core.PortalPriceMatch.TryMatchComposedPrice(raws, opened.PricePrefix, plan.Price);
        if (pick is null)
        {
            throw new Core.DecoException(
                $"Price '{plan.Price}' not among options (raw [{string.Join(" | ", raws)}], prefix '{opened.PricePrefix}').",
                "portal-price-option");
        }

        log?.Invoke($"  price: picking '{pick}'");
        string clicked;
        if (heSelect)
        {
            // Keyboard commit first (component-native path): focus the filter,
            // step to the match, commit with Enter. A mouse click only paints
            // the marker; only the component's own commit reaches the model.
            bool kbCommitted = false;
            try
            {
                var inputs = page.Locator("he-select[label=\"Retail price\"] input");
                if (await inputs.CountAsync() > 0)
                {
                    await inputs.First.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                    await page.Keyboard.PressAsync("ArrowDown");
                    await Task.Delay(1000);
                    await page.Keyboard.PressAsync("Enter");
                    await Task.Delay(2500);
                    BasePrice kb = await ReadBasePriceAsync(page);
                    kbCommitted = Core.PortalPriceMatch.TryMatchPrice([kb.Price], plan.Price) is not null;
                    log?.Invoke($"  price keyboard commit: marker='{kb.Price}' committed={kbCommitted}");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  price keyboard commit hiccup ({ex.Message.Split('\n')[0]})");
            }

            if (kbCommitted)
            {
                clicked = "clicked:keyboard";
            }
            else
            {
                // Mouse fallback: scroll into view (layout only), then a real
                // trusted click on the option.
                try
                {
                    string normPick = Core.PortalPriceMatch.Normalize(pick);
                    log?.Invoke($"  price scroll: {await page.EvaluateAsync<string>(ScrollTextJs, normPick)}");
                    await Task.Delay(1000);
                    var opt = page.Locator("he-select[label=\"Retail price\"] he-option",
                        new PageLocatorOptions
                        {
                            HasTextRegex = new Regex(Core.PortalPriceMatch.OptionTextPattern(normPick)),
                        });
                    await opt.First.ClickAsync(new LocatorClickOptions { Timeout = 15000 });
                    await Task.Delay(2500);
                    clicked = "clicked:real";
                }
                catch (Exception ex)
                {
                    log?.Invoke($"  price real-click failed ({ex.Message.Split('\n')[0]}) — JS fallback");
                    clicked = await ClickTextAsync(page, Core.PortalPriceMatch.Normalize(pick));
                }
            }
        }
        else
        {
            clicked = await ClickTextAsync(page, Core.PortalPriceMatch.Normalize(pick));
        }

        log?.Invoke($"  price pick: {clicked}");
        if (clicked == "not-found")
        {
            throw new Core.DecoException($"Price option '{pick}' vanished before the click.", "portal-price-option");
        }

        // Commit the combobox selection WITHOUT Enter (on a freeform control
        // Enter converts the pick back into typed text, which the server then
        // drops): Tab out, then a REAL trusted click on neutral page text so
        // focus genuinely leaves the control. A synthetic JS click does not
        // move focus, so it cannot commit.
        try
        {
            await page.Keyboard.PressAsync("Tab");
            await Task.Delay(1500);
            var neutral = page.GetByText("Base price", new PageGetByTextOptions { Exact = true });
            string blurred;
            if (await neutral.CountAsync() > 0)
            {
                await neutral.First.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                blurred = "clicked:real-neutral";
            }
            else
            {
                blurred = await ClickTextAsync(page, "Base price");
            }

            log?.Invoke($"  price blur: {blurred}");
            await Task.Delay(2000);
        }
        catch (Exception ex)
        {
            log?.Invoke($"  price blur hiccup ({ex.Message.Split('\n')[0]}) — the read-back decides");
        }

        BasePrice afterPrice = await ReadBasePriceAsync(page);
        if (Core.PortalPriceMatch.TryMatchPrice([afterPrice.Price], plan.Price) is null)
        {
            throw new Core.DecoException(
                $"Price did not stick: want '{plan.Price}', control shows '{afterPrice.Price}'.",
                "portal-price-verify");
        }

        log?.Invoke($"  price: now '{afterPrice.Price}'");
    }

    internal static async Task<bool> SaveAsync(IPage page, Action<string>? log)
    {
        // The draft save is an input[type=submit][value="Save draft"], not a
        // <button> — role-based lookup (which maps submit inputs) first, raw
        // input selector second. The button enables only once dirty.
        ILocator save = page.GetByRole(AriaRole.Button,
            new PageGetByRoleOptions { NameRegex = new Regex("^save( draft)?$", RegexOptions.IgnoreCase) });
        bool visible = false;
        try
        {
            await save.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 15000,
            });
            visible = true;
        }
        catch
        {
        }

        if (!visible)
        {
            var inp = page.Locator("input#saveButtonPricing, input[value=\"Save draft\"], input[type=\"submit\"]");
            try
            {
                if (await inp.CountAsync() > 0)
                {
                    save = inp.First;
                    visible = await save.IsVisibleAsync();
                }
            }
            catch
            {
            }
        }

        if (!visible)
        {
            await PortalDriver.InventoryAsync(page);
            await PortalDriver.ArtifactAsync(page, $"portal-price-save-missing-{Now()}");
            throw new Core.DecoException("Save draft control not found (tried role=button 'save draft', input#saveButtonPricing).", "portal-control-missing");
        }

        for (int i = 0; i < 6; i++)
        {
            try
            {
                if (await save.IsEnabledAsync())
                {
                    break;
                }
            }
            catch
            {
            }

            await Task.Delay(2500);
        }

        await PortalDriver.ClickStepAsync(page, "save pricing draft", save);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        bool saved = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(5000);
            string body;
            try
            {
                body = await page.Locator("body").InnerTextAsync() ?? string.Empty;
            }
            catch
            {
                continue;
            }

            if (body.Contains("saved", StringComparison.OrdinalIgnoreCase)
                || body.Contains("save successful", StringComparison.OrdinalIgnoreCase)
                || body.Contains("changes saved", StringComparison.OrdinalIgnoreCase))
            {
                saved = true;
                break;
            }
        }

        log?.Invoke(saved ? "  save: confirmed (saved signal on page)" : "  save: no saved toast seen — verifying by read-back");
        return saved;
    }

    internal sealed record SubmitOutcome(bool Submitted, string Note);

    /// <summary>
    /// Clicks "Submit for certification" on the submission overview. This is
    /// the operation that translates portal-side selections (which the API
    /// never sees — split-brain) into a committed submission. Classifies the
    /// outcome from page signals; anything but clear success throws loud.
    /// </summary>
    internal static async Task<SubmitOutcome> SubmitForCertificationAsync(
        IPage page, Plan plan, string runDir, Action<string>? log)
    {
        string overview = $"{plan.SiteConfig.ProductBase}/{plan.StoreId}/submissions/{plan.SubmissionId}";
        await PortalSubmission.GotoNetworkIdleAsync(page, overview);
        await ShotAsync(page, runDir, "06-before-submit", log);
        var btn = page.GetByRole(AriaRole.Button,
            new PageGetByRoleOptions { NameRegex = new Regex("submit for certification", RegexOptions.IgnoreCase) });
        bool found = false;
        try
        {
            await btn.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 20000,
            });
            found = true;
        }
        catch
        {
        }

        if (!found)
        {
            await PortalDriver.InventoryAsync(page);
            await PortalDriver.ArtifactAsync(page, $"portal-submit-missing-{Now()}");
            throw new Core.DecoException(
                $"No 'Submit for certification' button at {overview} (see 06-before-submit.png).",
                "portal-control-missing");
        }

        await PortalDriver.ClickStepAsync(page, "submit for certification", btn.First);
        await Task.Delay(10000);
        await ShotAsync(page, runDir, "07-after-submit", log);
        string body;
        try
        {
            body = await page.Locator("body").InnerTextAsync() ?? string.Empty;
        }
        catch
        {
            body = string.Empty;
        }

        Match bad = Regex.Match(body, "CommitFailed|InvalidState|cannot commit|failed to submit|not submitted",
            RegexOptions.IgnoreCase);
        if (bad.Success)
        {
            int at = Math.Max(0, bad.Index - 120);
            string snippet = body.Substring(at, Math.Min(300, body.Length - at))
                .Replace('\n', ' ').Replace('\r', ' ').Trim();
            return new SubmitOutcome(false, $"portal refused: {snippet}");
        }

        if (Regex.IsMatch(body, "certification|in review|submitted for|submission complete",
            RegexOptions.IgnoreCase))
        {
            return new SubmitOutcome(true, "success signals on page");
        }

        return new SubmitOutcome(false, "unclear outcome — see 07-after-submit.png");
    }

    /// <summary>Full run: login → pricing page → baseline → (diagnose | apply+verify) → (portal submit). Throws DecoException with artifacts on any gate failure.</summary>
    public static async Task<RunResult> RunAsync(Plan plan, Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(plan.RunDir);
        string progressFile = Path.Combine(plan.RunDir, "steps.jsonl");
        int total = !plan.Apply ? 3 : (plan.Submit ? 8 : 7);
        var progress = new PortalDriver.Progress(total, progressFile);
        var steps = new List<string>();
        var shots = new List<string>();
        var manifest = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["storeId"] = plan.StoreId,
            ["submissionId"] = plan.SubmissionId,
            ["currency"] = plan.Currency,
            ["price"] = plan.Price,
            ["apply"] = plan.Apply,
            ["startedUtc"] = DateTime.UtcNow.ToString("o"),
            ["steps"] = steps,
        };
        void StepDone(string name)
        {
            steps.Add(name);
            progress.Done();
        }

        async Task WriteManifestAsync()
        {
            manifest["finishedUtc"] = DateTime.UtcNow.ToString("o");
            manifest["progressFile"] = progressFile;
            await File.WriteAllTextAsync(Path.Combine(plan.RunDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        PortalDriver.Session? session = null;
        try
        {
            session = await PortalDriver.LaunchAsync(plan.ProfileDir, plan.Headed, trace: true, log, ct);
            IPage page = session.Page;
            manifest["engine"] = session.Engine;

            progress.Step("login");
            bool loggedIn;
            if (plan.SkipLogin)
            {
                loggedIn = true;
            }
            else
            {
                await page.GotoAsync(PortalSession.DashboardUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                loggedIn = await PortalDriver.WaitForLoginAsync(page, 75, null);
                if (!loggedIn && !plan.Headed && !plan.NoLoginAssist && Environment.UserInteractive)
                {
                    log?.Invoke("  headless session is not logged in: popping ONE visible window for sign-in...");
                    Func<Task> close = session.Close;
                    (page, close) = await PortalDriver.LoginAssistAsync(close, plan.ProfileDir, plan.LoginTimeoutSec, log);
                    session = new PortalDriver.Session(page, close, session.Engine, session.TracePath);
                    loggedIn = await PortalDriver.WaitForLoginAsync(page, 75, null);
                }
            }

            if (!loggedIn)
            {
                throw new Core.DecoException("Login still needed (non-interactive run, or the assist window timed out).",
                    "portal-login-needed", Core.ExitCodes.UsageOrAuthNeeded);
            }

            StepDone("login-ok");
            shots.Add(await ShotAsync(page, plan.RunDir, "01-dashboard", log));

            progress.Step("pricing page");
            await PortalSubmission.GotoNetworkIdleAsync(page, AvailabilityUrl(plan));
            shots.Add(await ShotAsync(page, plan.RunDir, "02-pricing-before", log));
            StepDone("pricing-open");

            progress.Step("read baseline");
            BasePrice baseline = await ReadBasePriceAsync(page);
            log?.Invoke($"  baseline: currency='{baseline.Currency}' price='{baseline.Price}' (via {baseline.CurrencyStrategy}/{baseline.PriceStrategy})");
            manifest["baseline"] = new { currency = baseline.Currency, price = baseline.Price };
            if (string.IsNullOrWhiteSpace(baseline.Price) && string.IsNullOrWhiteSpace(baseline.Currency))
            {
                await PortalDriver.InventoryAsync(page);
                await PortalDriver.ArtifactAsync(page, $"portal-price-unreadable-{Now()}");
                throw new Core.DecoException("Base price unreadable (no currency, no retail-price value) — refusing to continue blind.", "portal-price");
            }

            if (string.IsNullOrWhiteSpace(baseline.Price))
            {
                log?.Invoke("  baseline price empty (currency readable) — apply will set it explicitly.");
            }

            StepDone("baseline-read");

            if (!plan.Apply)
            {
                await WriteManifestAsync();
                log?.Invoke($"Diagnose complete (read-only, no clicks). Run dir: {plan.RunDir}");
                return new RunResult(plan.RunDir, Path.Combine(plan.RunDir, "manifest.json"), baseline, null, false, false, false, string.Empty, steps, shots);
            }

            PortalDriver.Checkpoint("Apply base price",
                $"Submission {plan.SubmissionId}: set Currency '{plan.Currency}' + Retail price '{plan.Price}' (was '{baseline.Currency}' / '{baseline.Price}'), then Save. Screenshots land in {plan.RunDir}.",
                plan.AutoConfirm);

            progress.Step("set currency");
            await SelectCurrencyAsync(page, plan, log);
            shots.Add(await ShotAsync(page, plan.RunDir, "03-currency-set", log));
            StepDone("currency-set");

            progress.Step("set price");
            await SelectPriceAsync(page, plan, log);
            shots.Add(await ShotAsync(page, plan.RunDir, "04-price-set", log));
            StepDone("price-set");

            progress.Step("save");
            bool saveSignal = await SaveAsync(page, log);
            // Save draft posts back and may route away: if we landed
            // elsewhere, return to the pricing section; if the URL is
            // unchanged, just settle — the read-back must be meaningful.
            string target = AvailabilityUrl(plan);
            static string Canon(string u) => u.Split('#')[0].TrimEnd('/');
            if (!string.Equals(Canon(page.Url), Canon(target), StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("  save routed away — returning to the pricing section...");
                await PortalSubmission.GotoNetworkIdleAsync(page, target);
            }
            else
            {
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15000 });
                }
                catch
                {
                }

                await Task.Delay(3000);
            }

            shots.Add(await ShotAsync(page, plan.RunDir, "05-after-save", log));
            StepDone("saved");

            progress.Step("verify read-back");
            BasePrice final = await ReadBasePriceAsync(page);
            log?.Invoke($"  read-back: currency='{final.Currency}' price='{final.Price}'");
            manifest["final"] = new { currency = final.Currency, price = final.Price };
            bool currencyOk = Core.PortalPriceMatch.TryMatchCurrency([final.Currency], plan.Currency) is not null;
            bool priceOk = Core.PortalPriceMatch.TryMatchPrice([final.Price], plan.Price) is not null;
            if (!currencyOk || !priceOk)
            {
                await PortalDriver.ArtifactAsync(page, $"portal-price-verify-fail-{Now()}");
                throw new Core.DecoException(
                    $"Read-back mismatch: want '{plan.Currency}'/'{plan.Price}', page shows '{final.Currency}'/'{final.Price}'.",
                    "portal-price-verify");
            }

            log?.Invoke("  verify: read-back matches — pricing is set.");
            StepDone("verified");
            manifest["saved"] = true;
            manifest["saveSignal"] = saveSignal;

            bool submitted = false;
            string submitNote = string.Empty;
            if (plan.Submit)
            {
                progress.Step("submit for certification");
                PortalDriver.Checkpoint("Submit for certification",
                    $"Submission {plan.SubmissionId}: submit the draft with verified base price '{final.Currency}' / '{final.Price}'. This starts certification and translates the portal-side pricing into the submission.",
                    plan.AutoConfirm);
                SubmitOutcome outcome = await SubmitForCertificationAsync(page, plan, plan.RunDir, log);
                submitted = outcome.Submitted;
                submitNote = outcome.Note;
                manifest["submitted"] = submitted;
                manifest["submitNote"] = submitNote;
                if (!submitted)
                {
                    await WriteManifestAsync();
                    throw new Core.DecoException(
                        $"Submit did not complete: {submitNote} (run dir {plan.RunDir}).",
                        "portal-submit");
                }

                StepDone("submitted");
            }

            await WriteManifestAsync();
            log?.Invoke($"Pricing run complete. Manifest: {Path.Combine(plan.RunDir, "manifest.json")}");
            return new RunResult(plan.RunDir, Path.Combine(plan.RunDir, "manifest.json"), baseline, final, true, saveSignal, submitted, submitNote, steps, shots);
        }
        catch
        {
            if (session is not null)
            {
                try
                {
                    await PortalDriver.ArtifactAsync(session.Page, "portal-price-failed");
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

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
