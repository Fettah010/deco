using CloakBrowser;
using Microsoft.Playwright;

namespace DevCli.Infrastructure;

/// <summary>
/// Partner Center browser session (port of portal-auth.ps1): persistent
/// profile dir (login once manually, session reused), login-URL gate polling,
/// logout wipe. Engine order: CloakBrowser stealth persistent context first,
/// stock Playwright (msedge channel) fallback. Auth sessions are HEADED by
/// design (Microsoft login hardening needs a visible browser); submission
/// automation runs headless against the same profile.
/// </summary>
public static class PortalSession
{
    public const string DashboardUrl = "https://partner.microsoft.com/dashboard";

    public static string DefaultProfileDir(string appId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appId, "portal-profile");

    /// <summary>Login classifier (port of the portal-auth.ps1 URL/title gate). Pure and tested.</summary>
    public static bool IsLoggedInUrl(string? url, string? title)
    {
        url ??= string.Empty;
        title ??= string.Empty;
        return url.Contains("partner.microsoft.com", StringComparison.OrdinalIgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(url, "login|signin|auth|account\\.microsoft", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !title.Contains("sign in", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Wipes the portal profile directory. Host-desktop safe by construction:
    /// only Edge processes whose command line references THIS profile dir
    /// (i.e. browsers deco itself launched) are asked to close — gracefully
    /// first (CloseMainWindow so session state is written and the user's tabs
    /// restore), Kill only for stragglers. Any other Edge window — the user's
    /// own browser included — is never touched. If the directory is still
    /// locked afterwards, throws naming the holding PIDs instead of killing
    /// anything else.
    /// </summary>
    public static void WipeProfile(string profileDir, IProcessRunner? runner = null)
    {
        runner ??= new ProcessRunner();
        IReadOnlyList<HostProcess> ours = SelectProfileProcesses(
            HostProcessLister.ListAsync(runner, "msedge.exe", TimeSpan.FromSeconds(60))
                .GetAwaiter().GetResult(),
            profileDir);
        foreach (HostProcess p in ours)
        {
            CloseQuietly(p.Id);
        }

        try
        {
            DeleteProfileDir(profileDir);
            return;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            IReadOnlyList<HostProcess> stillOurs = SelectProfileProcesses(
                HostProcessLister.ListAsync(runner, "msedge.exe", TimeSpan.FromSeconds(60))
                    .GetAwaiter().GetResult(),
                profileDir);
            string pids = stillOurs.Count == 0
                ? "unknown process"
                : string.Join(", ", stillOurs.Select(p => "PID " + p.Id));
            throw new Core.DecoException(
                $"Portal profile is locked by {pids}. Close the portal browser window and retry "
                + "(your other Edge windows were left alone).",
                "portal-profile-locked");
        }
    }

    /// <summary>
    /// Ownership filter: keeps only processes whose command line references
    /// the profile dir. Public and tested — this predicate is what stands
    /// between deco and the user's own browser.
    /// </summary>
    public static IReadOnlyList<HostProcess> SelectProfileProcesses(
        IReadOnlyList<HostProcess> all, string profileDir) =>
        all.Where(p => HostProcessLister.ReferencesPath(p.CommandLine, profileDir)).ToList();

    private static void DeleteProfileDir(string profileDir)
    {
        if (Directory.Exists(profileDir))
        {
            Directory.Delete(profileDir, recursive: true);
        }
    }

    /// <summary>Graceful close first (session state survives), Kill only stragglers. Best-effort.</summary>
    private static void CloseQuietly(int pid)
    {
        if (pid <= 0 || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            if (!string.Equals(proc.ProcessName, "msedge", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                if (proc.CloseMainWindow())
                {
                    proc.WaitForExit(3000);
                }
            }
            catch
            {
                // Headless automation windows have nothing to close; fall through to Kill.
            }

            if (!proc.HasExited)
            {
                try
                {
                    proc.Kill();
                }
                catch
                {
                }
            }
        }
        catch
        {
            // Already gone (or not ours to see) — nothing to do.
        }
    }

    public sealed record SessionResult(bool LoggedIn, string Engine, string ProfileDir);

    /// <summary>
    /// Opens the dashboard in a persistent-profile browser and waits for a
    /// manual login. Returns (LoggedIn, engine). Never throws for
    /// still-needing-login (returns false → caller exits 2).
    /// </summary>
    public static async Task<SessionResult> EnsureLoggedInAsync(
        string profileDir,
        bool headed,
        int timeoutSec,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(profileDir);

        // Engine 1: CloakBrowser stealth persistent context.
        try
        {
            await using var cloak = await CloakLauncher.LaunchPersistentContextAsync(
                profileDir,
                new LaunchContextOptions { Headless = !headed, Humanize = true });
            return await PollLoginAsync(cloak.Context, "cloak", profileDir, headed, timeoutSec, log, ct);
        }
        catch (Exception ex)
        {
            log?.Invoke($"CloakBrowser unavailable ({ex.Message}); falling back to msedge channel.");
        }

        // Engine 2: stock Playwright, installed Edge channel (no downloads).
        using var playwright = await Playwright.CreateAsync();
        await using IBrowserContext context = await playwright.Chromium.LaunchPersistentContextAsync(
            profileDir,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Channel = "msedge",
                Headless = !headed,
            });
        return await PollLoginAsync(context, "msedge", profileDir, headed, timeoutSec, log, ct);
    }

    internal static async Task<SessionResult> PollLoginAsync(
        IBrowserContext context, string engine, string profileDir,
        bool headed, int timeoutSec, Action<string>? log, CancellationToken ct)
    {
        await using (context)
        {
            IPage page = await context.NewPageAsync();
            await page.GotoAsync(DashboardUrl);
            if (headed)
            {
                log?.Invoke("Browser opened with the portal profile.");
                log?.Invoke("If you see a Microsoft login: sign in yourself (password + 2FA), then leave the window open.");
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSec);
            int attempt = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(5000, ct);
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

                if (IsLoggedInUrl(url, title))
                {
                    log?.Invoke("Logged in (dashboard reached).");
                    return new SessionResult(true, engine, profileDir);
                }

                if (headed)
                {
                    double remaining = (deadline - DateTimeOffset.UtcNow).TotalSeconds;
                    log?.Invoke($"  waiting for login... ({attempt}x, {remaining:F0}s left, at {url})");
                }
            }

            return new SessionResult(false, engine, profileDir);
        }
    }
}
