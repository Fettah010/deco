namespace DevCli.Infrastructure;

/// <summary>
/// Behavioral fixture E2E for the base-price driver: a local page that
/// mimics the Partner Center pricing controls (che-select currency +
/// listbox retail price + Save + saved toast, with real open/select/save
/// behavior in inline script). Run via `deco store portal-price --self-test`.
/// </summary>
public static class PortalPricingSelfTest
{
    public static async Task<List<PortalSelfTest.Check>> RunAsync(Action<string>? log = null)
    {
        var checks = new List<PortalSelfTest.Check>();
        void Report(string name, bool passed, string detail = "")
        {
            checks.Add(new PortalSelfTest.Check(name, passed, detail));
            log?.Invoke($"  [{(passed ? "PASS" : "FAIL")}] {name} {detail}");
        }

        string dir = Path.Combine(Path.GetTempPath(), "deco-portalprice-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "pricing.html"), FixturePricing());
        string runDir = Path.Combine(dir, "run");
        // NOTE: the launch probe and the run use DIFFERENT profiles — two
        // persistent contexts may never share one profile dir (lock fight).
        string probeProfile = Path.Combine(dir, "probe-profile");
        string runProfile = Path.Combine(dir, "run-profile");
        try
        {
            try
            {
                PortalDriver.Session probe = await PortalDriver.LaunchAsync(probeProfile, headed: false, trace: false, log);
                Report("launch", true, probe.Engine);
                try
                {
                    await probe.Close();
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                Report("launch", false, ex.Message);
                return checks;
            }

                var plan = new PortalPricing.Plan(
                    "SELFTEST", "SELFTEST", "EUR - France", "€ 2.99",
                    runProfile, Apply: true, Submit: false, AutoConfirm: true, Headed: false,
                NoLoginAssist: true, LoginTimeoutSec: 10, runDir,
                SkipLogin: true,
                new Uri(Path.Combine(dir, "pricing.html")).AbsoluteUri,
                new PortalSubmission.Site());
            PortalPricing.RunResult result;
            try
            {
                result = await PortalPricing.RunAsync(plan, log);
            }
            catch (Exception ex)
            {
                Report("run", false, ex.Message);
                return checks;
            }

            Report("run", true, $"steps={result.Steps.Count}");
            Report("baseline-read",
                result.Baseline.Currency == "USD - United States" && result.Baseline.Price == "$ 1.99",
                $"was '{result.Baseline.Currency}'/'{result.Baseline.Price}'");
            Report("currency-set",
                result.Final?.Currency == "EUR - France",
                $"now '{result.Final?.Currency}'");
            Report("price-set",
                Core.PortalPriceMatch.Normalize(result.Final?.Price) == "€ 2.99",
                $"now '{result.Final?.Price}'");
                Report("saved", result.Saved);
                Report("not-submitted", !result.Submitted, "self-test never submits");
            Report("save-signal", result.SaveSignal, "page showed the saved confirmation");
            Report("shots", Directory.GetFiles(runDir, "*.png").Length >= 5,
                $"{Directory.GetFiles(runDir, "*.png").Length} pngs");
            Report("manifest", File.Exists(Path.Combine(runDir, "manifest.json")));
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

    internal static string FixturePricing() => """
        <!doctype html><html><head><title>Pricing fixture</title></head><body>
        <h1>Pricing and availability</h1>
        <table><tr><th>Market</th><th>Price</th></tr><tr><td>France</td><td>$ 1.99</td></tr></table>
        <div class="price-tier-selection" pricetierkey="Retail">
          <che-select label="Currency"><div class="trig" id="curTrig">USD - United States</div>
            <div class="opts" id="curOpts" hidden></div>
          </che-select>
          <div class="price-row"><span>Retail price</span>
            <div role="listbox" aria-label="Retail price" id="priceBox"><div class="trig" id="priceTrig">$ 1.99</div>
              <div class="opts" id="priceOpts" hidden></div>
            </div>
          </div>
          <button id="saveBtn">Save</button><div id="toast" hidden></div>
        </div>
        <script>
          // Options render ONLY when opened (like the real Angular controls).
          const data = {
            curOpts: ['USD - United States', 'EUR - France'],
            priceOpts: ['$ 1.99', '€  2.99', '£ 1.79'],
          };
          const wire = (trigId, optsId, optClass) => {
            const trig = document.getElementById(trigId);
            const opts = document.getElementById(optsId);
            trig.addEventListener('click', () => {
              if (opts.hidden) {
                if (opts.children.length === 0) {
                  for (const t of data[optsId]) {
                    const o = document.createElement('div');
                    o.textContent = t;
                    if (optClass === 'opt') o.setAttribute('role', 'option');
                    else o.className = optClass;
                    opts.appendChild(o);
                  }
                }
                opts.hidden = false;
              } else {
                opts.hidden = true;
              }
            });
            opts.addEventListener('click', (ev) => {
              const o = ev.target.closest('[role="option"], .che-option');
              if (!o) return;
              opts.querySelectorAll('[aria-selected="true"]').forEach(x => x.removeAttribute('aria-selected'));
              o.setAttribute('aria-selected', 'true');
              trig.textContent = o.textContent;
              opts.hidden = true;
            });
          };
          wire('curTrig', 'curOpts', 'che-option');
          wire('priceTrig', 'priceOpts', 'opt');
          document.getElementById('saveBtn').addEventListener('click', () => {
            const t = document.getElementById('toast');
            t.textContent = 'Saved';
            t.hidden = false;
          });
        </script>
        </body></html>
        """;
}
