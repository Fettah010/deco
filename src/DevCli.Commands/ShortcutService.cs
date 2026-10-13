using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevCli.Commands;

/// <summary>
/// Port of Scripts/create-shortcut.ps1 / make-shortcut.ps1: two desktop
/// shortcuts (fast-launch exe + dev-loop via run-app/launch-dev) through
/// late-bound WScript.Shell COM (no interop assembly needed), plus the
/// SHChangeNotify icon-cache refresh. Windows-only for writes; planning and
/// --dry-run work anywhere. --output-dir exists so tests never touch Desktop.
/// </summary>
public static class ShortcutService
{
    public sealed record ShortcutPlan(
        string Name,
        string Target,
        string Arguments,
        string WorkingDirectory,
        string? IconLocation,
        string Description);

    public sealed record FullPlan(ShortcutPlan Launch, ShortcutPlan Dev, string OutputDir);

    public static FullPlan Plan(
        string appName,
        string exePath,
        string devScript,
        string scriptsDir,
        string outputDir,
        string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            throw new Core.DecoException("App name must not be empty.", "shortcut-no-name", Core.ExitCodes.UsageOrAuthNeeded);
        }

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            throw new Core.DecoException(
                $"Built exe not found at {exePath}. Build first, or pass --exe.",
                "shortcut-exe-missing");
        }

        if (string.IsNullOrWhiteSpace(devScript) || !File.Exists(devScript))
        {
            throw new Core.DecoException(
                $"Dev launcher script not found: {devScript} (expected Scripts/run-app.ps1 or launch-dev.ps1).",
                "shortcut-dev-missing");
        }

        string psExe = FindPowerShell() ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

        var launch = new ShortcutPlan(
            $"{appName}.lnk",
            exePath,
            string.Empty,
            Path.GetDirectoryName(exePath) ?? scriptsDir,
            iconPath is not null ? $"{iconPath},0" : null,
            $"{appName} Template App");

        var dev = new ShortcutPlan(
            $"{appName} (Dev).lnk",
            psExe,
            $"-NoProfile -ExecutionPolicy Bypass -File \"{devScript}\"",
            scriptsDir,
            iconPath is not null ? $"{iconPath},0" : null,
            $"{appName} Template App");

        return new FullPlan(launch, dev, outputDir);
    }

    public static string? FindPowerShell()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "powershell.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Malformed PATH segment: skip.
                }
            }
        }

        return null;
    }

    /// <summary>Resolves the dev launcher: explicit path, else run-app.ps1, else launch-dev.ps1.</summary>
    public static string? ResolveDevScript(string repoRoot, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        string scripts = Path.Combine(repoRoot, "Scripts");
        string runApp = Path.Combine(scripts, "run-app.ps1");
        if (File.Exists(runApp))
        {
            return runApp;
        }

        string launchDev = Path.Combine(scripts, "launch-dev.ps1");
        return File.Exists(launchDev) ? launchDev : null;
    }

    public static void Apply(FullPlan plan)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new Core.DecoException(
                "Desktop shortcuts (.lnk via WScript.Shell) require Windows. Use --dry-run to preview anywhere.",
                "windows-only",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        Directory.CreateDirectory(plan.OutputDir);
        WriteLnk(Path.Combine(plan.OutputDir, plan.Launch.Name), plan.Launch);
        WriteLnk(Path.Combine(plan.OutputDir, plan.Dev.Name), plan.Dev);
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    [SupportedOSPlatform("windows")]
    private static void WriteLnk(string path, ShortcutPlan s)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            throw new Core.DecoException("WScript.Shell COM object is unavailable.", "shortcut-com-missing");
        }

        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            object? link = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            if (link is null)
            {
                throw new Core.DecoException($"Could not create shortcut at {path}.", "shortcut-create-failed");
            }

            try
            {
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [s.Target]);
                lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, [s.Arguments]);
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, [s.WorkingDirectory]);
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, [s.Description]);
                if (s.IconLocation is not null)
                {
                    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, [s.IconLocation]);
                }

                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
