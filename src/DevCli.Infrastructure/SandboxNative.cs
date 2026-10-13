namespace DevCli.Infrastructure;

/// <summary>Best-effort native helpers for sandbox runs (all failure-swallowed).</summary>
public static class SandboxNative
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const int SwMinimize = 6;

    /// <summary>Keeps a waiting run alive across host sleep timers (the child waits minutes).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void StayAwake()
    {
        try
        {
            SetThreadExecutionState(EsContinuous | EsSystemRequired);
        }
        catch
        {
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void AllowSleep()
    {
        try
        {
            SetThreadExecutionState(EsContinuous);
        }
        catch
        {
        }
    }

    /// <summary>Minimizes the given processes' main windows (so capture runs don't take over the desktop).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void MinimizeWindows(IReadOnlyList<int> pids, Action<string>? log = null)
    {
        foreach (int pid in pids.Distinct())
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(pid);
                IntPtr hwnd = proc.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                {
                    continue;
                }

                if (ShowWindow(hwnd, SwMinimize))
                {
                    log?.Invoke($"  sandbox window minimized (PID {pid})");
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"  minimize (PID {pid}): {ex.Message.Split('\n')[0]}");
            }
        }
    }
}
