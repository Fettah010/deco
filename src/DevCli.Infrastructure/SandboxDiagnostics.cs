namespace DevCli.Infrastructure;

/// <summary>
/// Read-only sandbox observability for `sandbox status`: live session vitals
/// (image/PID/CPU time, so a silent session still tells booting vs wedged)
/// and recent Hyper-V worker events (so a dead session tells when/how it
/// died). Runner-injected and fully unit-tested; never mutates anything.
/// </summary>
public static class SandboxDiagnostics
{
    public sealed record SessionVital(string Image, int Pid, double CpuSeconds);

    public sealed record VmEvent(DateTimeOffset At, int Id, string Note);

    private static readonly string[] Images =
    [
        "WindowsSandboxRemoteSession.exe", "WindowsSandboxServer.exe",
        "WindowsSandbox.exe", "WindowsSandboxClient.exe",
    ];

    public static bool IsSandboxImage(string image) =>
        Images.Any(n => string.Equals(image?.Trim().Trim('"'), n, StringComparison.OrdinalIgnoreCase));

    /// <summary>PIDs present after launch minus PIDs present before = ours (for minimizing).</summary>
    public static List<int> NewPids(IReadOnlyList<int> before, IReadOnlyList<int> after)
    {
        var seen = new HashSet<int>(before);
        return after.Where(p => !seen.Contains(p)).Distinct().ToList();
    }

    /// <summary>Live sandbox processes with CPU time (tasklist /V). Empty = none.</summary>
    public static async Task<List<SessionVital>> SessionVitalsAsync(IProcessRunner runner)
    {
        var vitals = new List<SessionVital>();
        ProcessResult r;
        try
        {
            r = await runner.RunAsync("tasklist.exe", ["/V", "/FO", "CSV", "/NH"],
                timeout: TimeSpan.FromSeconds(30));
        }
        catch
        {
            return vitals;
        }

        if (r.ExitCode != 0)
        {
            return vitals;
        }

        foreach (string line in (r.StdOut + "\n" + r.StdErr).Split('\n'))
        {
            string[] cells = SplitCsv(line);
            // tasklist /V: Image Name, PID, Session Name, Session#, Mem Usage,
            // Status, User Name, CPU Time, Window Title.
            if (cells.Length < 8 || !IsSandboxImage(cells[0]))
            {
                continue;
            }

            if (int.TryParse(cells[1].Trim().Trim('"'), out int pid) && pid > 0)
            {
                vitals.Add(new SessionVital(cells[0].Trim().Trim('"'), pid, ParseCpu(cells[7].Trim().Trim('"'))));
            }
        }

        return vitals;
    }

    /// <summary>Recent Hyper-V worker events at/after <paramref name="sinceUtc"/> (max <paramref name="max"/>).</summary>
    public static async Task<List<VmEvent>> RecentVmEventsAsync(
        IProcessRunner runner, DateTimeOffset sinceUtc, int max = 15)
    {
        var events = new List<VmEvent>();
        ProcessResult r;
        try
        {
            r = await runner.RunAsync("powershell.exe",
                ["-NoProfile", "-Command",
                    "Get-WinEvent -LogName 'Microsoft-Windows-Hyper-V-Worker-Admin' -MaxEvents 40 -ErrorAction SilentlyContinue | ForEach-Object { \"$($_.TimeCreated.ToString('o')) $($_.Id)\" }"],
                timeout: TimeSpan.FromSeconds(60));
        }
        catch
        {
            return events;
        }

        if (r.ExitCode != 0)
        {
            return events;
        }

        foreach (string line in (r.StdOut + "\n" + r.StdErr).Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0)
            {
                continue;
            }

            int sp = t.LastIndexOf(' ');
            if (sp <= 0
                || !DateTimeOffset.TryParse(t[..sp], out DateTimeOffset at)
                || !int.TryParse(t[(sp + 1)..], out int id))
            {
                continue;
            }

            if (at < sinceUtc)
            {
                continue;
            }

            events.Add(new VmEvent(at, id, Interpret(id)));
            if (events.Count >= max)
            {
                break;
            }
        }

        events.Sort((a, b) => a.At.CompareTo(b.At));
        return events;
    }

    internal static string Interpret(int id) => id switch
    {
        18502 => "VM turned off",
        _ => string.Empty,
    };

    internal static double ParseCpu(string hms)
    {
        string[] parts = (hms ?? string.Empty).Trim().Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out int h)
            || !int.TryParse(parts[1], out int m)
            || !int.TryParse(parts[2], out int s))
        {
            return 0;
        }

        return h * 3600 + m * 60 + s;
    }

    internal static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        bool inQuotes = false;
        var cur = new System.Text.StringBuilder();
        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                cur.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }

        cells.Add(cur.ToString());
        return [.. cells];
    }
}
