using CloakBrowser;
using Microsoft.Playwright;

namespace DevCli.Infrastructure;

/// <summary>
/// Browser engine primitives for Partner Center automation (port of
/// Scripts/portal/lib.mjs): CloakBrowser-first stealth launch with msedge
/// fallback, tracing, checkpoints, failure artifacts (screenshot + DOM),
/// control inventory, verified click steps, section-URL mapping, login
/// waiting/assist, and JSONL progress. Headless-first everywhere; a headed
/// window appears only for manual login assist (closes itself after).
/// </summary>
public static class PortalDriver
{
    public sealed record Session(IPage Page, Func<Task> Close, string Engine, string TracePath);

    public static string FailDir =>
        Path.Combine(Path.GetTempPath(), "deco-portal-fail");

    /// <summary>Prunes our failure artifacts older than 30 days (best effort).</summary>
    public static int PruneArtifacts()
    {
        int pruned = 0;
        var dirs = new[] { FailDir, Path.GetTempPath() };
        foreach (string dir in dirs)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (string f in files)
            {
                string n = Path.GetFileName(f);
                bool ours = dir == FailDir
                    || System.Text.RegularExpressions.Regex.IsMatch(n, @"^portal-trace-.*\.zip$")
                    || System.Text.RegularExpressions.Regex.IsMatch(n, @"^portal-progress-.*\.jsonl$");
                if (!ours)
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30))
                    {
                        File.Delete(f);
                        pruned++;
                    }
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        return pruned;
    }

    public static async Task<Session> LaunchAsync(
        string profileDir, bool headed, bool trace, Action<string>? log = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(profileDir);
        string tracePath = Path.Combine(Path.GetTempPath(), $"portal-trace-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.zip");

        // Engine 1: CloakBrowser stealth persistent context.
        try
        {
            var handle = await CloakLauncher.LaunchPersistentContextAsync(
                profileDir,
                new LaunchContextOptions { Headless = !headed, Humanize = true });
            IBrowserContext cloakCtx = handle.Context;
            if (trace)
            {
                await cloakCtx.Tracing.StartAsync(new TracingStartOptions
                {
                    Screenshots = true,
                    Snapshots = true,
                });
            }

            IPage cloakPage = await cloakCtx.NewPageAsync();
            log?.Invoke("  engine: cloak (stealth)");
            return new Session(cloakPage, async () =>
            {
                try
                {
                    if (trace)
                    {
                        await cloakCtx.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });
                    }
                }
                catch
                {
                }

                await handle.CloseAsync();
            }, "cloak", tracePath);
        }
        catch (Exception ex)
        {
            log?.Invoke($"  cloak unavailable ({ex.Message}); falling back to msedge channel.");
        }

        // Engine 2: stock Playwright, installed Edge channel (no downloads).
        var playwright = await Playwright.CreateAsync();
        IBrowserContext context = await playwright.Chromium.LaunchPersistentContextAsync(
            profileDir,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Channel = "msedge",
                Headless = !headed,
            });
        if (trace)
        {
            await context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
            });
        }

        IPage page = await context.NewPageAsync();
        log?.Invoke("  engine: msedge");
        return new Session(page, async () =>
        {
            try
            {
                if (trace)
                {
                    await context.Tracing.StopAsync(new TracingStopOptions { Path = tracePath });
                }
            }
            catch
            {
            }

            await context.CloseAsync();
            playwright.Dispose();
        }, "msedge", tracePath);
    }

    /// <summary>
    /// Confirmation gate before every irreversible step. Auto-confirm covers
    /// all but the final review (which always prompts in an interactive
    /// console, like the mjs).
    /// </summary>
    public static void Checkpoint(string label, string detail, bool autoConfirm, bool final = false)
    {
        Console.WriteLine(string.Empty);
        Console.WriteLine($"CHECKPOINT: {label}");
        Console.WriteLine(detail);
        if (autoConfirm && !final)
        {
            Console.WriteLine("(auto-confirmed)");
            return;
        }

        if (!Environment.UserInteractive)
        {
            throw new Core.DecoException(
                "This step needs an interactive console: re-run in a normal window (or pass --auto-confirm, final review included).",
                "portal-not-interactive",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        Console.Write("Type YES to continue, anything else aborts: ");
        if (Console.ReadLine() != "YES")
        {
            throw new Core.DecoException($"Aborted at checkpoint: {label}.", "portal-aborted");
        }
    }

    /// <summary>Saves screenshot + DOM dump; prints paths instead of clicking blindly.</summary>
    public static async Task ArtifactAsync(IPage page, string name)
    {
        try
        {
            Directory.CreateDirectory(FailDir);
            string shot = Path.Combine(FailDir, $"{name}.png");
            string dom = Path.Combine(FailDir, $"{name}.html");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot, FullPage = true });
            string html = await page.ContentAsync();
            await File.WriteAllTextAsync(dom, html);
            Console.WriteLine($"  artifact: {shot}");
            Console.WriteLine($"  artifact: {dom}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (artifact failed: {ex.Message})");
        }
    }

    /// <summary>Dumps visible buttons/inputs for finder hardening (paste back).</summary>
    public static async Task InventoryAsync(IPage page)
    {
        Console.WriteLine("Control inventory (paste back for finder hardening):");
        try
        {
            var buttons = await page.Locator("button").AllAsync();
            int n = 0;
            foreach (var b in buttons.Take(40))
            {
                try
                {
                    string text = await b.InnerTextAsync();
                    string aria = await b.GetAttributeAsync("aria-label") ?? string.Empty;
                    Console.WriteLine($"  BTN text='{text.Trim()}' aria='{aria}'");
                    if (++n >= 40)
                    {
                        break;
                    }
                }
                catch
                {
                }
            }

            var inputs = page.Locator("//input | //textarea");
            int count = await inputs.CountAsync();
            for (int i = 0; i < Math.Min(count, 20); i++)
            {
                try
                {
                    var el = inputs.Nth(i);
                    Console.WriteLine($"  IN type='{await el.GetAttributeAsync("type")}' name='{await el.GetAttributeAsync("name")}' ph='{await el.GetAttributeAsync("placeholder")}'");
                }
                catch
                {
                }
            }
        }
        catch
        {
            Console.WriteLine("  (inventory unavailable)");
        }
    }

    /// <summary>Click with normal + JS fallback, then run the verify callback.</summary>
    public static async Task ClickStepAsync(
        IPage page, string description, ILocator locator, Func<Task>? verify = null,
        int timeoutMs = 30000)
    {
        try
        {
            await locator.First.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs });
            await Task.Delay(3000);
        }
        catch
        {
            try
            {
                await locator.First.EvaluateAsync("el => el.click()");
                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                throw new Core.DecoException($"Click failed ({description}): {ex.Message}", "portal-click");
            }
        }

        if (verify is not null)
        {
            await verify();
        }
    }

    /// <summary>Multi-strategy control finder: exact/contains text, aria-label, title.</summary>
    public static async Task<ILocator?> FindControlAsync(IPage page, string kind, string[] texts)
    {
        foreach (string text in texts)
        {
            string escaped = text.Replace("'", "", StringComparison.Ordinal);
            string[] xpaths =
            [
                $"//{kind}[normalize-space(.)='{escaped}']",
                $"//{kind}[contains(normalize-space(),'{escaped}')]",
                $"//*[@aria-label='{escaped}']",
                $"//{kind}[@title='{escaped}']",
            ];
            foreach (string xp in xpaths)
            {
                try
                {
                    var el = page.Locator($"xpath={xp}").First;
                    if (await el.IsVisibleAsync())
                    {
                        return el;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    /// <summary>Innermost element with exactly this text, else nearest clickable ancestor.</summary>
    public static async Task<ILocator?> FindClickableForTextAsync(IPage page, string text)
    {
        string escaped = text.Replace("'", "", StringComparison.Ordinal);
        ILocator node;
        try
        {
            node = page.Locator($"xpath=//*[normalize-space(.)='{escaped}' and not(*[normalize-space(.)='{escaped}'])]").First;
            await node.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = 5000,
            });
        }
        catch
        {
            return null;
        }

        foreach (string axis in new[]
                 {
                     "./ancestor::a[1]",
                     "./ancestor::button[1]",
                     "./ancestor::*[@role='button'][1]",
                     "./ancestor::*[@onclick][1]",
                 })
        {
            try
            {
                var hit = node.Locator($"xpath={axis}");
                if (await hit.IsVisibleAsync())
                {
                    return hit;
                }
            }
            catch
            {
            }
        }

        return node;
    }

    public static async Task<ILocator> RequireControlAsync(IPage page, string kind, string[] texts, string label)
    {
        ILocator? el = await FindControlAsync(page, kind, texts);
        if (el is null)
        {
            await InventoryAsync(page);
            throw new Core.DecoException(
                $"Control not found: {label} (tried: {string.Join(" / ", texts)}).",
                "portal-control-missing");
        }

        return el;
    }

    /// <summary>Maps dashboard DOM anchors to section URLs (incl. submission id).</summary>
    public static async Task<Dictionary<string, string>> SectionUrlsAsync(IPage page)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string json = await page.EvaluateAsync<string>(@"() => {
        const out = {};
        for (const a of document.querySelectorAll('a[href]')) {
          const href = a.getAttribute('href') || '';
          const text = (a.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 80);
          if (/\/submissions\/[^/]+\/packages/i.test(href)) out.packages = new URL(href, location.href).href;
          const m = href.match(/\/submissions\/([^/]+)/i);
          if (m) out.submissionId = m[1];
          if (/pricing|availability/i.test(href) || /pricing and availability/i.test(text)) {
            try { out.pricing = new URL(href, location.href).href; } catch {}
          }
          if (/submission.?options|options/i.test(href) && /option/i.test(text)) {
            try { out.options = new URL(href, location.href).href; } catch {}
          }
        }
        return JSON.stringify(out);
      }");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                map[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Empty map: callers fail loudly with inventory instead.
        }

        return map;
    }

    public static async Task<bool> WaitForLoginAsync(IPage page, int timeoutSec, Action<int, int, string>? onTick = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSec);
        int attempt = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(5000);
            attempt++;
            string url, title;
            try
            {
                url = page.Url;
            }
            catch
            {
                url = string.Empty;
            }

            try
            {
                title = await page.TitleAsync();
            }
            catch
            {
                title = string.Empty;
            }

            if (PortalSession.IsLoggedInUrl(url, title))
            {
                return true;
            }

            onTick?.Invoke(attempt, (int)(deadline - DateTimeOffset.UtcNow).TotalSeconds, url);
        }

        return false;
    }

    /// <summary>Headless session needs login: pop ONE headed window, then hand back to headless.</summary>
    public static async Task<(IPage Page, Func<Task> Close)> LoginAssistAsync(
        Func<Task> closeCurrent, string profileDir, int timeoutSec, Action<string>? log = null)
    {
        await closeCurrent();
        Session headed = await LaunchAsync(profileDir, headed: true, trace: false, log);
        try
        {
            await headed.Page.GotoAsync(PortalSession.DashboardUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        catch (Exception ex)
        {
            log?.Invoke($"  assist navigation hiccup ({ex.Message}) — use the window if it shows the login.");
        }

        bool ok = await WaitForLoginAsync(headed.Page, timeoutSec,
            (n, left, url) => log?.Invoke($"  waiting for login... ({n}x, {left}s left, at {url[..Math.Min(url.Length, 90)]})"));
        await headed.Close();
        if (!ok)
        {
            throw new Core.DecoException(
                "Login still needed (assist window timed out).",
                "portal-login-needed",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        Session headless = await LaunchAsync(profileDir, headed: false, trace: true, log);
        try
        {
            await headless.Page.GotoAsync(PortalSession.DashboardUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
        catch (Exception ex)
        {
            log?.Invoke($"  post-login navigation hiccup ({ex.Message}) — the login check decides.");
        }

        return (headless.Page, headless.Close);
    }

    public sealed class Progress
    {
        private readonly string _file;
        private readonly int _total;
        private int _step;
        private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow;

        public Progress(int total, string file)
        {
            _total = total;
            _file = file;
        }

        public string ProgressFile => _file;

        public void Step(string label)
        {
            _step++;
            Write(label, "start");
            Console.WriteLine($"[{_step}/{_total}] {label}");
        }

        public void Done() => Write(string.Empty, "done");

        private void Write(string label, string state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file) ?? _file);
                File.AppendAllText(_file,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        step = _step,
                        total = _total,
                        label,
                        state,
                        elapsedSec = (int)(DateTimeOffset.UtcNow - _t0).TotalSeconds,
                    }) + Environment.NewLine);
            }
            catch
            {
                // Progress is observability, never fatal.
            }
        }
    }
}
