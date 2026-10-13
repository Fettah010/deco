using Microsoft.Playwright;

namespace DevCli.Infrastructure;

/// <summary>
/// Engine self-test for the portal driver: builds local fixture pages and
/// exercises launch, finders, verified clicks, inventory, artifacts, section
/// mapping, price scraping, file inputs, and progress — with zero Partner
/// Center contact. Run via `deco store portal --self-test`.
/// </summary>
public static class PortalSelfTest
{
    public sealed record Check(string Name, bool Passed, string Detail);

    public static async Task<List<Check>> RunAsync(Action<string>? log = null)
    {
        var checks = new List<Check>();
        void Report(string name, bool passed, string detail = "")
        {
            checks.Add(new Check(name, passed, detail));
            log?.Invoke($"  [{(passed ? "PASS" : "FAIL")}] {name} {detail}");
        }

        string dir = Path.Combine(Path.GetTempPath(), "deco-portal-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string profile = Path.Combine(dir, "profile");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "page1.html"), FixturePage1());
            await File.WriteAllTextAsync(Path.Combine(dir, "editor.html"), FixtureEditor());
            string shot = Path.Combine(dir, "shot.png");
            await File.WriteAllBytesAsync(shot, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));

            PortalDriver.Session session;
            try
            {
                session = await PortalDriver.LaunchAsync(profile, headed: false, trace: false, log);
            }
            catch (Exception ex)
            {
                Report("launch", false, ex.Message);
                return checks;
            }

            Report("launch", true, session.Engine);
            IPage page = session.Page;
            try
            {
                await page.GotoAsync(new Uri(Path.Combine(dir, "page1.html")).AbsoluteUri);
                var btn = await PortalDriver.FindControlAsync(page, "button", ["Start update", "Begin"]);
                Report("find-control", btn is not null);
                var clickable = await PortalDriver.FindClickableForTextAsync(page, "Apps and games");
                Report("find-clickable", clickable is not null);
                bool verified = false;
                if (btn is not null)
                {
                    await PortalDriver.ClickStepAsync(page, "test click", btn, () =>
                    {
                        verified = true;
                        return Task.CompletedTask;
                    });
                }

                Report("click-step+verify", verified);
                try
                {
                    await PortalDriver.InventoryAsync(page);
                    Report("inventory", true);
                }
                catch (Exception ex)
                {
                    Report("inventory", false, ex.Message);
                }

                await PortalDriver.ArtifactAsync(page, $"selftest-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                Report("artifact", Directory.GetFiles(PortalDriver.FailDir, "selftest-*").Length >= 2);

                var sections = await PortalDriver.SectionUrlsAsync(page);
                Report("section-urls", sections.TryGetValue("submissionId", out string? sid) && sid == "abc123"
                    && sections.ContainsKey("packages") && sections.ContainsKey("pricing"),
                    $"keys={string.Join(",", sections.Keys)}");

                var prices = await PortalSubmission.ScrapePricesAsync(page);
                // "Free" is not price-shaped (no currency/decimal) so only the
                // $1.99 market row counts — same as the submit.mjs scraper.
                Report("price-scrape", prices.Rows.Count == 1 && prices.Base.Contains("Free"),
                    $"base='{prices.Base}' rows={prices.Rows.Count}");

                var inputs = page.Locator("input[type=\"file\"]");
                bool inputOk = await inputs.CountAsync() > 0;
                if (inputOk)
                {
                    await inputs.First.SetInputFilesAsync(shot);
                }

                Report("file-input", inputOk);

                string progressFile = Path.Combine(dir, "progress.jsonl");
                var progress = new PortalDriver.Progress(2, progressFile);
                progress.Step("one");
                progress.Done();
                Report("progress-jsonl", File.Exists(progressFile) && File.ReadAllLines(progressFile).Length == 2);

                await page.GotoAsync(new Uri(Path.Combine(dir, "editor.html")).AbsoluteUri);
                bool editorFound = false;
                foreach (var frame in page.Frames)
                {
                    var d = frame.Locator("#description-required");
                    if (await d.CountAsync() > 0 && await d.First.IsVisibleAsync())
                    {
                        await d.First.FillAsync("Hello");
                        string val = await d.First.InputValueAsync();
                        editorFound = val == "Hello";
                        break;
                    }
                }

                Report("editor-fill", editorFound);

                var missing = await PortalDriver.FindControlAsync(page, "button", ["No Such Button Xyz"]);
                Report("finder-miss-null", missing is null);
            }
            finally
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
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }

        return checks;
    }

    internal static string FixturePage1() => """
        <!doctype html><html><head><title>Fixture</title></head><body>
        <nav><a href="/en-us/dashboard/products/9ABC/submissions/abc123/listings">Store listings</a>
        <a href="/en-us/dashboard/products/9ABC/submissions/abc123/packages">Packages</a>
        <a href="/pricing">Pricing and availability</a></nav>
        <a href="/x">Apps and games</a>
        <button>Start update</button>
        <button aria-label="Save">Save</button>
        <button>Submit for certification</button>
        <input type="file" />
        <div><div><div><label>Base price</label><select><option>Free</option></select></div></div></div>
        <table><tr><th>Market</th><th>Price</th></tr>
        <tr><td>United States</td><td>Free</td></tr>
        <tr><td>France</td><td>$1.99</td></tr></table>
        <a href="/en-us/x">English</a>
        </body></html>
        """;

    internal static string FixtureEditor() => """
        <!doctype html><html><head><title>Editor</title></head><body>
        <textarea id="description-required"></textarea>
        <textarea id="releaseNotes"></textarea>
        <button>Save</button>
        </body></html>
        """;
}
