using System.Text.Json;

namespace DevCli.Infrastructure;

/// <summary>One host process with its full command line (for ownership checks).</summary>
public sealed record HostProcess(int Id, string? CommandLine);

/// <summary>
/// Lists host processes WITH command lines via CIM (Win32_Process).
/// This is the ownership seam behind the "never touch the user's desktop"
/// rule: before deco closes or hides anything, it must prove the process
/// belongs to deco by matching its command line (profile dir, .rdp file).
/// Pure parsing/matching lives here and is unit-tested; the CIM call itself
/// is Windows-only and best-effort (empty list on any failure — callers must
/// treat "unknown" as "not ours" and never fall back to kill-all).
/// </summary>
public static class HostProcessLister
{
    /// <summary>WQL string-literal escape (single-quote doubling).</summary>
    public static string EscapeWql(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// Command-line ownership check: does this command line reference the
    /// given path fragment (profile dir, .rdp file)? Case-insensitive,
    /// slash-insensitive, quote-tolerant.
    /// </summary>
    public static bool ReferencesPath(string? commandLine, string fragment)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(fragment))
        {
            return false;
        }

        static string Norm(string s) =>
            s.Replace('/', '\\').Trim().Trim('"').ToUpperInvariant();
        return Norm(commandLine).Contains(Norm(fragment), StringComparison.Ordinal);
    }

    /// <summary>
    /// Lists processes with the given image name (e.g. "msedge.exe").
    /// Returns an empty list on any failure — never throws for listing
    /// problems (callers treat unknown as not-ours).
    /// </summary>
    public static async Task<IReadOnlyList<HostProcess>> ListAsync(
        IProcessRunner runner, string imageName, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        string ps =
            $"Get-CimInstance Win32_Process -Filter \"Name = '{EscapeWql(imageName)}'\" "
            + "| Select-Object ProcessId, CommandLine | ConvertTo-Json -Compress -Depth 2";
        ProcessResult r;
        try
        {
            r = await runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", ps],
                timeout: timeout);
        }
        catch
        {
            return [];
        }

        if (r.ExitCode != 0)
        {
            return [];
        }

        try
        {
            return ParseCimJson(r.StdOut);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Parses ConvertTo-Json output of Select-Object ProcessId, CommandLine.
    /// Handles the PowerShell quirks: empty output, a single object (not an
    /// array), an array, and null rows. Pure and tested.
    /// </summary>
    public static IReadOnlyList<HostProcess> ParseCimJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Null || root.ValueKind == JsonValueKind.Undefined)
        {
            return [];
        }

        var out_ = new List<HostProcess>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in root.EnumerateArray())
            {
                HostProcess? p = ParseRow(el);
                if (p is not null)
                {
                    out_.Add(p);
                }
            }

            return out_;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            HostProcess? p = ParseRow(root);
            if (p is not null)
            {
                out_.Add(p);
            }
        }

        return out_;
    }

    private static HostProcess? ParseRow(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!el.TryGetProperty("ProcessId", out JsonElement idEl) || !idEl.TryGetInt32(out int id))
        {
            return null;
        }

        string? cmd = null;
        if (el.TryGetProperty("CommandLine", out JsonElement cmdEl) && cmdEl.ValueKind == JsonValueKind.String)
        {
            cmd = cmdEl.GetString();
        }

        return new HostProcess(id, cmd);
    }
}
