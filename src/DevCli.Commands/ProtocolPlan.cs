namespace DevCli.Commands;

/// <summary>
/// Pure port of Scripts/register-protocol.ps1 planning + Windows execution.
/// Planning (scheme normalization, action list) is pure and unit-tested;
/// <see cref="Apply"/> touches HKCU and is Windows-only by design.
/// </summary>
public static class ProtocolPlan
{
    public sealed record RegistryAction(string Key, string? ValueName, string Value);

    public static string NormalizeScheme(string scheme)
    {
        string s = (scheme ?? string.Empty).Trim().TrimEnd('/').TrimEnd(':').Trim();
        if (string.IsNullOrWhiteSpace(s))
        {
            throw new Core.DecoException(
                "Scheme must not be empty (e.g. \"formixa://\").",
                "invalid-scheme",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        return s;
    }

    /// <summary>Registry actions for register (create/update) or unregister (delete marker).</summary>
    public static List<RegistryAction> Plan(string scheme, string exePath, bool unregister)
    {
        string name = NormalizeScheme(scheme);
        if (!unregister && string.IsNullOrWhiteSpace(exePath))
        {
            throw new Core.DecoException(
                "Exe path is required to register (build first, or pass --exe).",
                "exe-not-found",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        var actions = new List<RegistryAction>();
        if (unregister)
        {
            actions.Add(new RegistryAction(@$"Software\Classes\{name}", null, "<DELETE KEY>"));
            return actions;
        }

        actions.Add(new RegistryAction(@$"Software\Classes\{name}", "(Default)", $"URL:{name} deep link"));
        actions.Add(new RegistryAction(@$"Software\Classes\{name}", "URL Protocol", string.Empty));
        actions.Add(new RegistryAction(@$"Software\Classes\{name}\DefaultIcon", "(Default)", $"\"{exePath}\",0"));
        actions.Add(new RegistryAction(@$"Software\Classes\{name}\shell\open\command", "(Default)", $"\"{exePath}\" \"%1\""));
        return actions;
    }

    public static bool IsRegistered(string scheme)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string name = NormalizeScheme(scheme);
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@$"Software\Classes\{name}");
        return key is not null;
    }

    /// <summary>Executes the plan against HKCU. Windows-only; throws otherwise (callers offer --dry-run).</summary>
    public static void Apply(string scheme, string exePath, bool unregister)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new Core.DecoException(
                "Protocol registration writes HKCU and requires Windows. Use --dry-run to preview the actions anywhere.",
                "windows-only",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        string name = NormalizeScheme(scheme);
        if (unregister)
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@$"Software\Classes\{name}", throwOnMissingSubKey: false);
            return;
        }

        if (!File.Exists(exePath))
        {
            throw new Core.DecoException(
                $"Exe not found: {exePath} (build first, or pass --exe).",
                "exe-not-found",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        using (var baseKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{name}"))
        {
            baseKey?.SetValue(string.Empty, $"URL:{name} deep link");
            baseKey?.SetValue("URL Protocol", string.Empty);
        }

        using (var icon = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{name}\DefaultIcon"))
        {
            icon?.SetValue(string.Empty, $"\"{exePath}\",0");
        }

        using (var cmd = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{name}\shell\open\command"))
        {
            cmd?.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
        }
    }
}
