using System.Runtime.Versioning;
using System.Text;

namespace DevCli.Infrastructure;

/// <summary>
/// Hyper-V + PowerShell Direct bridge (the .NET port of the Scripts/vm/*.ps1
/// kit's transport): guest sessions via powershell.exe (5.1, which owns the
/// Hyper-V + PSSession VM cmdlets), secrets via process environment (never
/// the command line), host Hyper-V/RDP/cert helpers, and the hidden-viewer
/// P/Invoke. Pure guest-state parsing lives in Commands/VmGuestState.
/// </summary>
public static class VmGuest
{
    public const string PasswordEnv = "DECO_VM_PASSWORD";

    /// <summary>Runs a PowerShell snippet on the host (Hyper-V cmdlets live here).</summary>
    public static async Task<ProcessResult> HostPsAsync(
        IProcessRunner runner, string psSnippet, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? env = null)
    {
        string file = Path.Combine(Path.GetTempPath(), "deco-vm-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(file, "$ErrorActionPreference='Stop'\n" + psSnippet);
        try
        {
            ProcessResult r = await TryHostPsAsync(runner, file, timeout, env);
            if (r.ExitCode != 0)
            {
                throw new Core.DecoException(
                    $"Host PowerShell failed (exit {r.ExitCode}): {Tail(r)}",
                    "vm-host-ps");
            }

            return r;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }
    }

    private static async Task<ProcessResult> TryHostPsAsync(
        IProcessRunner runner, string file, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? env)
    {
        TimeSpan[] backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
        for (int attempt = 0; ; attempt++)
        {
            ProcessResult r = await runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file],
                timeout: timeout,
                env: env);
            if (r.ExitCode == 0 || attempt >= backoff.Length || !IsSessionOpenFailure(Tail(r)))
            {
                return r;
            }

            Console.WriteLine(
                $"[vm] Host PowerShell Direct session hiccup (attempt {attempt + 1}/3) - retrying in {backoff[attempt].TotalSeconds:F0}s...");
            await Task.Delay(backoff[attempt]);
        }
    }

    /// <summary>
    /// Stores the RDP password for TERMSRV/&lt;target&gt; so mstsc connects
    /// silently (no interactive prompt on a hidden window). Port of the
    /// ps1's cmdkey line — including its caveat: the password briefly
    /// appears on the local cmdkey command line. Scoped to the VM's address;
    /// nothing else is stored. The entry is intentionally left in place
    /// afterwards (parity: enables silent reconnects; rdp-trust clears
    /// stale ones).
    /// </summary>
    public static async Task CmdkeySetAsync(
        IProcessRunner runner, string target, string user, string password, TimeSpan timeout)
    {
        ProcessResult r = await runner.RunAsync(
            "cmdkey.exe",
            [$"/generic:TERMSRV/{target}", $"/user:{user}", $"/pass:{password}"],
            timeout: timeout);
        if (r.ExitCode != 0)
        {
            throw new Core.DecoException(
                $"cmdkey could not store the RDP password for '{target}': {Tail(r)}",
                "vm-cmdkey");
        }
    }

    /// <summary>
    /// Runs a PowerShell snippet INSIDE the guest via a PowerShell Direct
    /// session. The password travels in $env (never argv); the snippet goes
    /// through a temp file (never inline quoting). Output streams back.
    ///
    /// Session establishment is retried (3 attempts, 5s/15s backoff) when the
    /// failure is at New-PSSession open (transient vmbus wedges, e.g. during
    /// RDP logon storms — observed live on Formixa-Capture). Failures INSIDE
    /// the guest snippet are never retried (snippets may be non-idempotent).
    /// </summary>
    public static async Task<ProcessResult> InvokeGuestAsync(
        IProcessRunner runner, string vm, string user, string password,
        string guestSnippet, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        string wrapper =
            "$ErrorActionPreference='Stop'\n"
            + $"$sec = ConvertTo-SecureString $env:{PasswordEnv} -AsPlainText -Force\n"
            + "$cred = New-Object System.Management.Automation.PSCredential('"
            + user.Replace("'", "", StringComparison.Ordinal) + "', $sec)\n"
            + $"$s = New-PSSession -VMName '{vm.Replace("'", "", StringComparison.Ordinal)}' -Credential $cred -ErrorAction Stop\n"
            + "try {\n"
            + "  Invoke-Command -Session $s -ScriptBlock {\n"
            + guestSnippet + "\n"
            + "  }\n"
            + "} finally { Remove-PSSession -Session $s }\n";
        string file = Path.Combine(Path.GetTempPath(), "deco-vmguest-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(file, wrapper);
        try
        {
            var env = new Dictionary<string, string> { [PasswordEnv] = password };
            if (extraEnv is not null)
            {
                foreach ((string k, string v) in extraEnv)
                {
                    env[k] = v;
                }
            }

            ProcessResult r = await TryInvokeGuestAsync(runner, file, env, timeout);
            if (r.ExitCode != 0)
            {
                throw new Core.DecoException(
                    $"Guest command failed on '{vm}' (exit {r.ExitCode}): {Tail(r)}",
                    "vm-guest-failed");
            }

            return r;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }
    }

    private static async Task<ProcessResult> TryInvokeGuestAsync(
        IProcessRunner runner, string file,
        IReadOnlyDictionary<string, string> env, TimeSpan timeout)
    {
        TimeSpan[] backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
        for (int attempt = 0; ; attempt++)
        {
            ProcessResult r = await runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file],
                timeout: timeout,
                env: env);
            if (r.ExitCode == 0 || attempt >= backoff.Length || !IsSessionOpenFailure(Tail(r)))
            {
                return r;
            }

            Console.WriteLine(
                $"[vm] PowerShell Direct session hiccup (attempt {attempt + 1}/3) - retrying in {backoff[attempt].TotalSeconds:F0}s...");
            await Task.Delay(backoff[attempt]);
        }
    }

    /// <summary>
    /// True when a failed guest call died at New-PSSession open (transient
    /// transport wedge) rather than inside the snippet. Pure and tested.
    /// </summary>
    public static bool IsSessionOpenFailure(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        return output.Contains("PSSessionOpenFailed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("remote session might have ended", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> GetVmStateAsync(IProcessRunner runner, string vm, TimeSpan timeout)
    {
        ProcessResult r = await HostPsAsync(runner,
            $"if (Get-VM -Name '{vm}' -ErrorAction SilentlyContinue) {{ (Get-VM -Name '{vm}').State }} else {{ 'Missing' }}",
            timeout);
        return r.StdOut.Trim();
    }

    public static async Task<string?> GetVmIpv4Async(IProcessRunner runner, string vm, TimeSpan timeout)
    {
        ProcessResult r = await HostPsAsync(runner,
            $"Get-VMNetworkAdapter -VMName '{vm}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty IPAddresses",
            timeout);
        return r.StdOut.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .FirstOrDefault(s => System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+\."));
    }

    /// <summary>
    /// Name of the .rdp file deco writes for the capture viewer. The viewer
    /// launched with this file is deco's — scoping by this name is what keeps
    /// hide/stop operations off the user's own RDP sessions.
    /// </summary>
    public const string CaptureRdpFileName = "vmcapture-cap.rdp";

    /// <summary>
    /// Opens the RDP viewer DETACHED (mstsc stays up after we return) and
    /// returns its PID for later scoped hide/stop. Deliberately NOT run
    /// through IProcessRunner: the runner waits for exit and would kill the
    /// viewer on timeout. The optional launcher seam exists for tests.
    /// </summary>
    public static Task<int> MstscConnectAsync(string rdpPath, Func<string, int>? launcher = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new Core.DecoException("RDP connect needs Windows.", "vm-no-rdp");
        }

        try
        {
            int pid = launcher is not null
                ? launcher(rdpPath)
                : System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "mstsc.exe",
                    Arguments = "\"" + rdpPath + "\"",
                    UseShellExecute = true,
                })?.Id ?? 0;
            if (pid <= 0)
            {
                throw new Core.DecoException("mstsc.exe did not start.", "vm-rdp");
            }

            return Task.FromResult(pid);
        }
        catch (Core.DecoException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Core.DecoException($"Could not start mstsc.exe: {ex.Message}", "vm-rdp");
        }
    }

    /// <summary>
    /// Single CIM pass for deco's viewer PID (0 when absent). Best-effort
    /// hide/show sites use this — fast, never waits.
    /// </summary>
    public static async Task<int> FindViewerPidOnceAsync(IProcessRunner runner, string rdpFileName)
    {
        IReadOnlyList<HostProcess> all =
            await HostProcessLister.ListAsync(runner, "mstsc.exe", TimeSpan.FromSeconds(30));
        return all.FirstOrDefault(p => HostProcessLister.ReferencesPath(p.CommandLine, rdpFileName))?.Id ?? 0;
    }

    /// <summary>
    /// Finds deco's viewer PID: the mstsc.exe whose command line references
    /// our .rdp file. Retries while the viewer spins up. Returns 0 when no
    /// such viewer exists (hide/stop treat 0 as no-op). Never matches other
    /// sessions — the rdp filename is the ownership proof.
    /// </summary>
    public static async Task<int> FindViewerPidForRdpAsync(
        IProcessRunner runner, string rdpFileName, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        do
        {
            int pid = await FindViewerPidOnceAsync(runner, rdpFileName);
            if (pid != 0)
            {
                return pid;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return 0;
            }

            await Task.Delay(2000);
        }
        while (true);
    }

    /// <summary>
    /// Stops ONLY the given viewer PID (verified to still be mstsc.exe).
    /// Graceful close first, Kill on stragglers. pid &lt;= 0 is a no-op.
    /// Returns true when a process was stopped.
    /// </summary>
    public static bool StopViewerPid(int pid)
    {
        if (pid <= 0 || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            if (!string.Equals(proc.ProcessName, "mstsc", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                if (proc.CloseMainWindow())
                {
                    proc.WaitForExit(2000);
                }
            }
            catch
            {
            }

            if (!proc.HasExited)
            {
                try
                {
                    proc.Kill();
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Scoped stop: only mstsc.exe processes using OUR .rdp file. Returns the
    /// number stopped. This replaces the old kill-all — a user's own RDP
    /// sessions are never enumerated for killing.
    /// </summary>
    public static async Task<int> StopViewersForRdpAsync(
        IProcessRunner runner, string rdpFileName, TimeSpan timeout)
    {
        IReadOnlyList<HostProcess> all =
            await HostProcessLister.ListAsync(runner, "mstsc.exe", TimeSpan.FromSeconds(30));
        int stopped = 0;
        foreach (HostProcess p in all.Where(p => HostProcessLister.ReferencesPath(p.CommandLine, rdpFileName)))
        {
            if (StopViewerPid(p.Id))
            {
                stopped++;
            }
        }

        return stopped;
    }

    /// <summary>
    /// Hides ONLY the windows owned by the given viewer PID (off-screen
    /// toolwindow: keeps painting, unlike minimizing). pid &lt;= 0 is a no-op.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void HideViewerPid(int pid)
    {
        SetViewerVisible(pid, visible: false);
    }

    /// <summary>
    /// Re-shows windows owned by the given viewer PID (safety net so a failed
    /// stop never leaves hidden windows behind). pid &lt;= 0 is a no-op.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void ShowViewerPid(int pid)
    {
        SetViewerVisible(pid, visible: true);
    }

    [SupportedOSPlatform("windows")]
    private static void SetViewerVisible(int pid, bool visible)
    {
        if (pid <= 0 || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var wins = new List<IntPtr>();
            NativeMethods.EnumWindows(
                (h, _) =>
                {
                    NativeMethods.GetWindowThreadProcessId(h, out uint wpid);
                    if (wpid == (uint)pid)
                    {
                        wins.Add(h);
                    }

                    return true;
                }, IntPtr.Zero);
            foreach (IntPtr h in wins)
            {
                try
                {
                    if (visible)
                    {
                        int ex = NativeMethods.GetWindowLong(h, -20);
                        NativeMethods.SetWindowLong(h, -20, ex & ~0x80);
                        NativeMethods.SetWindowPos(h, IntPtr.Zero, 64, 64, 0, 0, 0x0001 | 0x0002 | 0x0040 | 0x0020);
                        NativeMethods.ShowWindow(h, 9);
                    }
                    else
                    {
                        int ex = NativeMethods.GetWindowLong(h, -20);
                        NativeMethods.SetWindowLong(h, -20, ex | 0x80);
                        NativeMethods.SetWindowPos(h, IntPtr.Zero, -2000, 0, 0, 0, 0x0001 | 0x0002 | 0x0040 | 0x0020);
                        NativeMethods.ShowWindow(h, 0);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static string Tail(ProcessResult r)
    {
        string tail = (r.StdErr + "\n" + r.StdOut).Trim();
        return tail.Length > 1500 ? tail[^1500..] : tail;
    }

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr h, int c);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern int SetWindowLong(IntPtr h, int n, int v);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr h, int n);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    }
}
