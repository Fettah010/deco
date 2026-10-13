namespace DevCli.Infrastructure;

/// <summary>
/// Windows Sandbox process plumbing for `sandbox capture`: detached launch
/// (PowerShell Start-Process — RunAsync waits, so a direct launch would
/// block for the whole run), sentinel polling, and ownership-bound teardown.
/// Only one sandbox runs per logon session by OS design.
/// Teardown rule: kill ONLY PIDs recorded as ours at boot (see
/// WaitForSessionAsync). Image-wide taskkill is reserved for the explicit
/// `sandbox kill` command — a stale run must never murder a newer session.
/// </summary>
public static class SandboxRunner
{
    public sealed record WaitResult(bool Done, string Note);

    private static readonly string[] Images =
    [
        "WindowsSandboxRemoteSession.exe", "WindowsSandboxServer.exe",
        "WindowsSandbox.exe", "WindowsSandboxClient.exe",
    ];

    public static string SandboxExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsSandbox.exe");

    /// <summary>
    /// Live sandbox processes right now (empty = none). Basis for ownership.
    /// </summary>
    public static async Task<List<(string Image, int Pid)>> ListSandboxProcessesAsync(IProcessRunner runner)
    {
        var found = new List<(string Image, int Pid)>();
        ProcessResult r = await runner.RunAsync("tasklist.exe", ["/FO", "CSV", "/NH"],
            timeout: TimeSpan.FromSeconds(30));
        if (r.ExitCode != 0)
        {
            return found;
        }

        foreach (string line in (r.StdOut + "\n" + r.StdErr).Split('\n'))
        {
            string[] cells = line.Split(',');
            if (cells.Length < 2)
            {
                continue;
            }

            string image = cells[0].Trim().Trim('"');
            bool ours = false;
            foreach (string name in Images)
            {
                if (string.Equals(image, name, StringComparison.OrdinalIgnoreCase))
                {
                    ours = true;
                    break;
                }
            }

            if (ours && int.TryParse(cells[1].Trim().Trim('"'), out int pid) && pid > 0)
            {
                found.Add((image, pid));
            }
        }

        return found;
    }

    /// <summary>
    /// Live sandbox PIDs right now (empty = none). Basis for everything
    /// ownership-sensitive.
    /// </summary>
    public static async Task<List<int>> ListSandboxPidsAsync(IProcessRunner runner)
    {
        List<(string Image, int Pid)> all = await ListSandboxProcessesAsync(runner);
        return all.Select(p => p.Pid).Distinct().ToList();
    }

    /// <summary>Stable session processes (the persistent ones — never the momentary launcher stub).</summary>
    public static List<int> StableSessionPids(IReadOnlyList<(string Image, int Pid)> all) =>
        all.Where(p =>
                string.Equals(p.Image, "WindowsSandboxRemoteSession.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Image, "WindowsSandboxServer.exe", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid)
            .Distinct()
            .ToList();

    /// <summary>
    /// Is a sandbox alive right now? Null = probe failed (unknown).
    /// </summary>
    public static async Task<bool?> IsRunningAsync(IProcessRunner runner)
    {
        try
        {
            List<int> pids = await ListSandboxPidsAsync(runner);
            return pids.Count > 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for OUR session to appear (up to <paramref name="timeout"/>) and
    /// returns its PIDs for ownership-bound teardown. Prefers the stable
    /// session processes (never the momentary launcher stub); falls back to
    /// whatever was last seen. Empty = never appeared.
    /// </summary>
    public static async Task<List<int>> WaitForSessionAsync(
        IProcessRunner runner, TimeSpan timeout, Action<string>? log = null,
        CancellationToken ct = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        List<(string Image, int Pid)> last = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = await ListSandboxProcessesAsync(runner);
            }
            catch (Exception ex)
            {
                log?.Invoke($"  session watch hiccup: {ex.Message.Split('\n')[0]}");
                last = [];
            }

            List<int> stable = StableSessionPids(last);
            if (stable.Count > 0)
            {
                log?.Invoke($"  session up: {string.Join(", ", stable)}");
                return stable;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        List<int> fallback = last.Select(p => p.Pid).Distinct().ToList();
        log?.Invoke(fallback.Count == 0
            ? "  session never appeared (proceeding without ownership record)"
            : $"  stable session never appeared; recording last-seen: {string.Join(", ", fallback)}");
        return fallback;
    }

    /// <summary>
    /// Kills ONLY the given PIDs, after re-verifying each still belongs to
    /// the sandbox family (PIDs get recycled — never trust a stale number).
    /// </summary>
    public static async Task KillPidsAsync(IProcessRunner runner, IReadOnlyList<int> pids, Action<string>? log = null)
    {
        foreach (int pid in pids.Distinct())
        {
            try
            {
                ProcessResult probe = await runner.RunAsync("tasklist.exe",
                    ["/FI", $"PID eq {pid}", "/FO", "CSV", "/NH"],
                    timeout: TimeSpan.FromSeconds(30));
                string all = probe.StdOut + "\n" + probe.StdErr;
                bool ours = false;
                foreach (string name in Images)
                {
                    if (all.Contains($"\"{name}\"", StringComparison.OrdinalIgnoreCase))
                    {
                        ours = true;
                        break;
                    }
                }

                if (!ours)
                {
                    log?.Invoke($"  teardown: PID {pid} is no longer sandbox-owned, skipping");
                    continue;
                }

                await runner.RunAsync("taskkill.exe", ["/PID", pid.ToString(), "/F"],
                    timeout: TimeSpan.FromSeconds(60));
                log?.Invoke($"  teardown: PID {pid} signalled");
            }
            catch (Exception ex)
            {
                log?.Invoke($"  teardown (PID {pid}): {ex.Message.Split('\n')[0]}");
            }
        }
    }

    /// <summary>
    /// Fire-and-forget: PowerShell Start-Process detaches (no console
    /// semantics, spaces handled by quoting) and returns once spawning.
    /// </summary>
    public static Task<ProcessResult> LaunchAsync(IProcessRunner runner, string wsbPath) =>
        runner.RunAsync("powershell.exe",
            ["-NoProfile", "-Command",
                $"Start-Process -FilePath '{SandboxExe}' -ArgumentList '{wsbPath.Replace("'", "''", StringComparison.Ordinal)}'"],
            timeout: TimeSpan.FromMinutes(2));

    public static async Task<WaitResult> WaitSentinelAsync(
        string outDir, TimeSpan timeout, TimeSpan? poll = null, CancellationToken ct = default)
    {
        TimeSpan step = poll ?? TimeSpan.FromSeconds(5);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            string done = Path.Combine(outDir, "done");
            if (File.Exists(done))
            {
                string note;
                try
                {
                    note = (File.ReadAllText(done) ?? string.Empty).Trim();
                }
                catch
                {
                    note = string.Empty;
                }

                return new WaitResult(true, note);
            }

            string failed = Path.Combine(outDir, "failed");
            if (File.Exists(failed))
            {
                string note;
                try
                {
                    note = (File.ReadAllText(failed) ?? string.Empty).Trim();
                }
                catch
                {
                    note = string.Empty;
                }

                if (note.Length == 0)
                {
                    note = "guest reported failure (see error.txt)";
                }

                return new WaitResult(false, note);
            }

            try
            {
                await Task.Delay(step, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new WaitResult(false, $"timeout after {timeout.TotalMinutes:F0} min (no done/failed sentinel in {outDir})");
    }

    public static async Task KillAsync(IProcessRunner runner, Action<string>? log = null)
    {
        foreach (string img in new[]
                 {
                     "WindowsSandboxRemoteSession.exe", "WindowsSandboxServer.exe",
                     "WindowsSandbox.exe", "WindowsSandboxClient.exe",
                 })
        {
            try
            {
                await runner.RunAsync("taskkill.exe", ["/IM", img, "/F"], timeout: TimeSpan.FromSeconds(60));
                log?.Invoke($"  sandbox teardown: {img} signalled");
            }
            catch (Exception ex)
            {
                log?.Invoke($"  sandbox teardown ({img}): {ex.Message.Split('\n')[0]}");
            }
        }
    }
}
