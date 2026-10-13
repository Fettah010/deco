using System.Text.Json;

namespace DevCli.Commands;

/// <summary>
/// Certification watch (v0.1.2): polls `msstore submission status` until a
/// terminal state, records a state file, and optionally registers an hourly
/// Windows Scheduled Task so multi-day certifications need no babysitting.
/// Certification takes hours, sometimes days — this is the "don't keep
/// rechecking" system. Pure parsing/state here (tested); the loop lives in
/// Program.cs next to the other store commands.
/// </summary>
public static class StoreCertWatch
{
    public sealed record WatchState(
        string AppId, string LastStatus, DateTimeOffset UpdatedUtc,
        bool Terminal, bool Published, string? Detail);

    /// <summary>
    /// Parses msstore status output. Handles both shapes:
    /// "Submission Status = X" (status command) and "Submission Status - X"
    /// (poll command). Last match wins (progress lines repeat). Null when
    /// no status line is present.
    /// </summary>
    public static string? ParseStatus(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        string? found = null;
        string[] seps = ["Submission Status = ", "Submission Status - "];
        foreach (string line in output.Replace("\r", "").Split('\n'))
        {
            string t = line.Trim();
            foreach (string sep in seps)
            {
                if (t.StartsWith(sep, StringComparison.OrdinalIgnoreCase))
                {
                    found = t[sep.Length..].Trim();
                    break;
                }
            }
        }

        return string.IsNullOrWhiteSpace(found) ? null : found;
    }

    public static bool IsPublished(string status) =>
        status.Equals("Published", StringComparison.OrdinalIgnoreCase)
        || status.Equals("PublishCompleted", StringComparison.OrdinalIgnoreCase);

    public static bool IsFailed(string status) =>
        status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Deleted", StringComparison.OrdinalIgnoreCase)
        || status.Contains("Fail", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Known in-flight states (all render yellow). Anything unrecognized is
    /// also yellow — pending attention, never red. Tested.
    /// </summary>
    public static bool IsPending(string status) =>
        !IsTerminal(status) && (
            status.Equals("CommitStarted", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Certification", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Publishing", StringComparison.OrdinalIgnoreCase)
            || status.Equals("InProgress", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Uploading", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PendingUpload", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Unknown", StringComparison.OrdinalIgnoreCase));

    public static bool IsTerminal(string status) => IsPublished(status) || IsFailed(status);

    /// <summary>State file: %LOCALAPPDATA%\deco\certwatch-&lt;safe-appid&gt;.json.</summary>
    public static string StatePath(string appId, string? baseDir = null)
    {
        baseDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "deco");
        string safe = new string(appId.Where(c => char.IsLetterOrDigit(c)).ToArray());
        if (string.IsNullOrEmpty(safe))
        {
            safe = "unknown";
        }

        return Path.Combine(baseDir, $"certwatch-{safe}.json");
    }

    public static void SaveState(WatchState state, string? baseDir = null)
    {
        string path = StatePath(state.AppId, baseDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static WatchState? LoadState(string appId, string? baseDir = null)
    {
        string path = StatePath(appId, baseDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            return new WatchState(
                r.TryGetProperty("AppId", out var a) ? a.GetString() ?? appId : appId,
                r.TryGetProperty("LastStatus", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                r.TryGetProperty("UpdatedUtc", out var u) && u.TryGetDateTimeOffset(out var dt) ? dt : DateTimeOffset.MinValue,
                r.TryGetProperty("Terminal", out var t) && t.ValueKind == JsonValueKind.True,
                r.TryGetProperty("Published", out var p) && p.ValueKind == JsonValueKind.True,
                r.TryGetProperty("Detail", out var d) ? d.GetString() : null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One status poll through the runner. Returns the raw status string.</summary>
    public static async Task<string> PollOnceAsync(
        Infrastructure.IProcessRunner runner, string msstore, string appId, TimeSpan timeout)
    {
        Infrastructure.ProcessResult r = await runner.RunAsync(
            msstore, ["submission", "status", appId, "--output-stream", "Stdout"],
            timeout: timeout);
        string? status = ParseStatus(r.StdOut + "\n" + r.StdErr);
        if (r.ExitCode != 0 && status is null)
        {
            string tail = (r.StdErr + "\n" + r.StdOut).Trim();
            if (tail.Length > 1500)
            {
                tail = tail[^1500..];
            }

            throw new Core.DecoException($"msstore status failed (exit {r.ExitCode}).\n{tail}", "certwatch-poll");
        }

        return status ?? "Unknown";
    }

    public static string TaskName(string appId) => "deco-certwatch-" + new string(
        appId.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();

    /// <summary>
    /// App-id memory: remembers the last --app-id per project directory so
    /// bare `deco store cert-watch --once` just works. File:
    /// %LOCALAPPDATA%\deco\certwatch-memory.json. Pure IO, tested.
    /// </summary>
    public static string MemoryPath(string? baseDir = null)
    {
        baseDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "deco");
        return Path.Combine(baseDir, "certwatch-memory.json");
    }

    public static string RepoKey(string? repoRoot) =>
        string.IsNullOrWhiteSpace(repoRoot) ? "global" : repoRoot.Trim();

    public static void SaveLastAppId(string repoKey, string appId, string? baseDir = null)
    {
        string path = MemoryPath(baseDir);
        Dictionary<string, string> map = LoadMemoryMap(path);
        map[repoKey] = appId;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(map,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string? LoadLastAppId(string repoKey, string? baseDir = null)
    {
        Dictionary<string, string> map = LoadMemoryMap(MemoryPath(baseDir));
        if (map.TryGetValue(repoKey, out string? appId) && !string.IsNullOrWhiteSpace(appId))
        {
            return appId;
        }

        return null;
    }

    private static Dictionary<string, string> LoadMemoryMap(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return map is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed record RememberedApp(string AppId, string RepoKey, string LastStatus, DateTimeOffset UpdatedUtc);

    /// <summary>
    /// All remembered apps, deduplicated by AppId, most-recently-seen state
    /// first (global key preferred on ties). Pure IO, tested.
    /// </summary>
    public static IReadOnlyList<RememberedApp> ListRemembered(string? baseDir = null)
    {
        Dictionary<string, string> map = LoadMemoryMap(MemoryPath(baseDir));
        var seen = new Dictionary<string, RememberedApp>(StringComparer.OrdinalIgnoreCase);
        foreach ((string repoKey, string appId) in map
            .OrderBy(kv => kv.Key.Equals("global", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(appId) || seen.ContainsKey(appId))
            {
                continue;
            }

            WatchState? st = LoadState(appId, baseDir);
            seen[appId] = new RememberedApp(appId, repoKey,
                st?.LastStatus ?? string.Empty,
                st?.UpdatedUtc ?? DateTimeOffset.MinValue);
        }

        return seen.Values.ToList();
    }

    /// <summary>
    /// Parses an interactive menu choice ("", "2") into a 0-based index.
    /// Empty means default (0). Null when invalid. Pure and tested.
    /// </summary>
    public static int? ParseSelection(string? input, int count)
    {
        if (count <= 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return 0;
        }

        if (int.TryParse(input.Trim(), out int n) && n >= 1 && n <= count)
        {
            return n - 1;
        }

        return null;
    }

    /// <summary>Fresh enough to show instantly while the live poll runs?</summary>
    public static bool IsFresh(WatchState? state, TimeSpan maxAge) =>
        state is not null && !string.IsNullOrWhiteSpace(state.LastStatus)
        && (DateTimeOffset.UtcNow - state.UpdatedUtc) <= maxAge;

    /// <summary>Human age label for cached states ("12m ago", "3h ago", "2d ago"). Pure and tested.</summary>
    public static string AgeLabel(DateTimeOffset utc)
    {
        TimeSpan age = DateTimeOffset.UtcNow - utc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        return $"{(int)age.TotalDays}d ago";
    }

    /// <summary>
    /// Registers an hourly per-user task that runs one bounded watch pass.
    /// Runner-based (schtasks) so tests assert the exact invocation.
    /// selfPrefix = ("dotnet", [dll]) when dll-hosted, else (exe, []).
    /// </summary>
    public static async Task RegisterTaskAsync(
        Infrastructure.IProcessRunner runner, string selfFileName, IReadOnlyList<string> selfPreArgs,
        string appId, int timeoutMin, TimeSpan timeout)
    {
        var args = new List<string> { "/create", "/tn", TaskName(appId), "/sc", "hourly", "/mo", "1", "/f", "/tr",
            selfFileName + (selfPreArgs.Count == 0 ? string.Empty : " " + string.Join(" ", selfPreArgs))
            + $" store cert-watch --app-id {appId} --timeout-min {timeoutMin} --notify --no-ansi" };
        Infrastructure.ProcessResult r = await runner.RunAsync("schtasks.exe", args, timeout: timeout);
        if (r.ExitCode != 0)
        {
            throw new Core.DecoException($"Could not register the watch task:\n{(r.StdErr + "\n" + r.StdOut).Trim()}",
                "certwatch-task");
        }
    }

    public static async Task UnregisterTaskAsync(
        Infrastructure.IProcessRunner runner, string appId, TimeSpan timeout)
    {
        Infrastructure.ProcessResult r = await runner.RunAsync(
            "schtasks.exe", ["/delete", "/tn", TaskName(appId), "/f"], timeout: timeout);
        if (r.ExitCode != 0)
        {
            throw new Core.DecoException($"Could not remove the watch task:\n{(r.StdErr + "\n" + r.StdOut).Trim()}",
                "certwatch-task");
        }
    }
}
