namespace DevCli.Commands;

/// <summary>
/// Detached-run bookkeeping for `sandbox capture`: stable run ids, stage
/// lookup (explicit id or newest), and status reads (sentinels + progress
/// tail + shot count). Pure filesystem, fully unit-tested. A detached run
/// survives aborted/closed CLI windows — only closing the Sandbox window
/// itself kills it.
/// </summary>
public static class SandboxRuns
{
    public const string Prefix = "deco-sandbox-";

    public static string NewRunId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    public static string StageDir(string temp, string runId) =>
        Path.Combine(temp, Prefix + runId);

    public static string TempRoot() => Path.GetTempPath();

    /// <summary>Explicit id, else the newest stage. Null when nothing matches.</summary>
    public static string? FindRun(string temp, string? runId)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(runId))
            {
                string dir = StageDir(temp, runId.Trim());
                return Directory.Exists(dir) ? dir : null;
            }

            return Directory.GetDirectories(temp, Prefix + "*")
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public sealed record RunStatus(
        string RunId, string StageDir, string State, string Detail,
        List<string> ProgressTail, int Shots, List<int> SessionPids);

    /// <summary>Unbuffered host-side log (the detached child's console goes nowhere).</summary>
    public static void HostLog(string stageDir, string message)
    {
        try
        {
            Directory.CreateDirectory(stageDir);
            File.AppendAllText(Path.Combine(stageDir, "host.log"),
                $"{DateTimeOffset.UtcNow:o} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static List<string> HostTail(string stageDir, int n)
    {
        try
        {
            string path = Path.Combine(stageDir, "host.log");
            if (!File.Exists(path))
            {
                return [];
            }

            var lines = File.ReadAllLines(path);
            return lines.Skip(Math.Max(0, lines.Length - n)).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Records the detached owner so status can tell running from orphaned.</summary>
    public static void WriteOwner(string stageDir, int childPid)
    {
        try
        {
            Directory.CreateDirectory(stageDir);
            File.WriteAllText(Path.Combine(stageDir, "owner.json"),
                "{\"childPid\":" + childPid + ",\"launchedUtc\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\"}");
        }
        catch
        {
        }
    }

    /// <summary>Is the recorded owner still alive? Null = no record (pre-detach runs).</summary>
    public static bool? OwnerAlive(string stageDir)
    {
        try
        {
            string path = Path.Combine(stageDir, "owner.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("childPid", out var p)
                || p.ValueKind != System.Text.Json.JsonValueKind.Number
                || !p.TryGetInt32(out int pid) || pid <= 0)
            {
                return null;
            }

            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                return !proc.HasExited;
            }
            catch
            {
                return false;
            }
        }
        catch
        {
            return null;
        }
    }

    public static RunStatus ReadStatus(string stageDir)
    {
        string runId = Path.GetFileName(stageDir);
        if (runId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            runId = runId.Substring(Prefix.Length);
        }

        string outDir = Path.Combine(stageDir, "out");
        var tail = ProgressTail(outDir, 8);
        int shots = 0;
        try
        {
            string shotsDir = Path.Combine(stageDir, "shots");
            if (Directory.Exists(shotsDir))
            {
                shots = Directory.GetFiles(shotsDir, "*.png").Length;
            }
        }
        catch
        {
        }

        string done = Path.Combine(outDir, "done");
        if (File.Exists(done))
        {
            string note = ReadTrim(done);
            string manifest = Path.Combine(outDir, "manifest.json");
            return new RunStatus(runId, stageDir, "done",
                note.Length > 0 ? note : (File.Exists(manifest) ? manifest : "finished"),
                tail, shots, SessionPids(stageDir));
        }

        string failed = Path.Combine(outDir, "failed");
        if (File.Exists(failed))
        {
            string note = ReadTrim(failed);
            string err = Path.Combine(outDir, "error.txt");
            if (note.Length == 0 && File.Exists(err))
            {
                note = ReadTrim(err);
            }

            return new RunStatus(runId, stageDir, "failed",
                note.Length > 0 ? note : "guest reported failure", tail, shots, SessionPids(stageDir));
        }

        string detail;
        if (tail.Count == 0)
        {
            string age = "?";
            try
            {
                TimeSpan a = DateTimeOffset.UtcNow - Directory.GetCreationTimeUtc(stageDir);
                age = a.TotalHours >= 1 ? $"{(int)a.TotalHours}h{(int)a.Minutes}m" : $"{(int)a.TotalMinutes}m";
            }
            catch
            {
            }

            detail = $"no guest output yet (stage age {age}: booting, LogonCommand pending, or stale)";
        }
        else
        {
            detail = "last: " + tail[^1];
        }

        bool? owner = OwnerAlive(stageDir);
        if (owner == false)
        {
            var host = HostTail(stageDir, 4);
            string why = host.Count == 0 ? "no host trace" : "host: " + host[^1];
            return new RunStatus(runId, stageDir, "orphaned",
                $"owner gone ({why}); guest may still finish — watch progress.log, or relaunch",
                tail, shots, SessionPids(stageDir));
        }

        return new RunStatus(runId, stageDir, "running", detail, tail, shots, SessionPids(stageDir));
    }

    internal static List<int> SessionPids(string stageDir)
    {
        try
        {
            string path = Path.Combine(stageDir, "session.json");
            if (!File.Exists(path))
            {
                return [];
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var pids = new List<int>();
            if (doc.RootElement.TryGetProperty("pids", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind == System.Text.Json.JsonValueKind.Number && e.TryGetInt32(out int pid) && pid > 0)
                    {
                        pids.Add(pid);
                    }
                }
            }

            return pids;
        }
        catch
        {
            return [];
        }
    }

    public static List<string> ProgressTail(string outDir, int n)
    {
        try
        {
            string log = Path.Combine(outDir, "progress.log");
            if (!File.Exists(log))
            {
                return [];
            }

            var lines = File.ReadAllLines(log);
            return lines.Skip(Math.Max(0, lines.Length - n)).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Child argv for --detach (drops --detach itself, pins --run-id).</summary>
    public static List<string> ChildArgs(
        string projectRoot, string verbosity, string format,
        string exe, string msix, string plan, string version,
        int memoryMb, bool noNetwork, int timeoutMin, string depsDir, string runId,
        bool vgpu, bool foreground)
    {
        var args = new List<string>
        {
            "--project", projectRoot,
            "--verbosity", verbosity,
            "--format", format,
            "sandbox", "capture",
        };
        void Opt(string flag, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                args.Add(flag);
                args.Add(value.Trim());
            }
        }

        Opt("--exe", exe);
        Opt("--msix", msix);
        Opt("--plan", plan);
        Opt("--version", version);
        args.Add("--memory-mb");
        args.Add(memoryMb.ToString());
        if (noNetwork)
        {
            args.Add("--no-network");
        }

        args.Add("--timeout-min");
        args.Add(timeoutMin.ToString());
        Opt("--deps-dir", depsDir);
        args.Add("--run-id");
        args.Add(runId);
        if (vgpu)
        {
            args.Add("--vgpu");
        }

        if (foreground)
        {
            args.Add("--foreground");
        }

        return args;
    }

    private static string ReadTrim(string path)
    {
        try
        {
            return (File.ReadAllText(path) ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
