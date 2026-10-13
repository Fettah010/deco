using System.Text;
using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// Pure guest-state model for the VM lab (port of the quser/session probes
/// in Wait-VMUnlocked.ps1 + Invoke-VmCapture.ps1): quser parsing, readiness
/// classification, and the signed-.rdp content builder. All PowerShell and
/// process execution stays in Infrastructure/VmGuest.
/// </summary>
public static partial class VmGuestState
{
    public sealed record SessionInfo(string User, string SessionName, int SessionId, string State);

    public sealed record GuestProbe(
        DateTime BootUtc,
        SessionInfo? Session,
        bool Locked,
        bool HasExplorer,
        double FreeGB,
        bool HasDotnet);

    /// <summary>Parses quser output for the user (substring match like the ps1; leading "&gt;" ignored).</summary>
    public static SessionInfo? ParseQuser(string quserOutput, string user)
    {
        string q = Regex.Replace(quserOutput.Trim(), @"\s+", " ");
        Match m = Regex.Match(q,
            Regex.Escape(user) + @"\s+(console|rdp-tcp#\d+)\s+(\d+)\s+(\w+)",
            RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }

        return new SessionInfo(user, m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[3].Value);
    }

    public enum Readiness
    {
        Ready,
        NoSession,
        Locked,
        NoExplorer,
        TooSmall,
    }

    /// <summary>Ready = on a session, unlocked, explorer present, desktop big enough.</summary>
    public static Readiness Classify(GuestProbe probe, int width, int height)
    {
        if (probe.Session is null)
        {
            return Readiness.NoSession;
        }

        if (probe.Locked || string.Equals(probe.Session.State, "Disc", StringComparison.OrdinalIgnoreCase))
        {
            return Readiness.Locked;
        }

        if (!probe.HasExplorer)
        {
            return Readiness.NoExplorer;
        }

        if (width < 1366 || height < 768)
        {
            return Readiness.TooSmall;
        }

        return Readiness.Ready;
    }

    public static bool IsBigEnough(string size)
    {
        Match m = Regex.Match(size.Trim(), @"^(\d+)x(\d+)$");
        return m.Success && int.Parse(m.Groups[1].Value) >= 1366 && int.Parse(m.Groups[2].Value) >= 768;
    }

    /// <summary>Silent-connect .rdp content (RDP-layer listener pair: auth level 0 + trusted signature).</summary>
    public static string[] RdpFileContent(string target, string user) =>
    [
        "screen mode id:i:1",
        "desktopwidth:i:1920",
        "desktopheight:i:1080",
        "session bpp:i:32",
        $"full address:s:{target}",
        $"username:s:{user}",
        "prompt for credentials:i:0",
        "prompt for credentials once:i:1",
        "enablecredsspsupport:i:1",
        "authentication level:i:0",
        "administrative session:i:0",
        "autoreconnection enabled:i:1",
        "redirectclipboard:i:0",
        "redirectprinters:i:0",
        "redirectcomports:i:0",
        "redirectsmartcards:i:0",
        "redirectwebauthn:i:0",
        "drivestoredirect:s:",
    ];

    public static bool NeedsRewrite(string existingContent, string[] desiredLines)
    {
        string norm(string s) => s.Replace("\r\n", "\n").Trim();
        return norm(existingContent) != norm(string.Join("\n", desiredLines));
    }

    public static string ProtectScript() => """
        $au = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU'
        New-Item -Path $au -Force | Out-Null
        New-ItemProperty -LiteralPath $au -Name NoAutoRebootWithLoggedOnUsers -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -LiteralPath $au -Name AUOptions -Value 3 -PropertyType DWord -Force | Out-Null
        powercfg /change monitor-timeout-ac 0 | Out-Null
        powercfg /change standby-timeout-ac 0 | Out-Null
        powercfg /change hibernate-timeout-ac 0 | Out-Null
        powercfg /hibernate off 2>$null
        powercfg /SETACTIVE SCHEME_CURRENT | Out-Null
        Set-ItemProperty -Path 'HKCU:\\Control Panel\\Desktop' -Name ScreenSaveActive -Value 0
        Set-ItemProperty -Path 'HKCU:\\Control Panel\\Desktop' -Name ScreenSaverIsSecure -Value 0
        Set-ItemProperty -Path 'HKCU:\\Control Panel\\Desktop' -Name SCRNSAVE.EXE -Value ''
        $pers = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\Personalization'
        New-Item -Path $pers -Force | Out-Null
        New-ItemProperty -LiteralPath $pers -Name NoLockScreen -Value 1 -PropertyType DWord -Force | Out-Null
        tzutil /s 'Romance Standard Time' | Out-Null
        """;

    /// <param name="psUserExpr">PowerShell expression for the user (e.g. '$u' or a quoted literal).</param>
    public static string AutologonScript(string psUserExpr, string passwordToken) =>
        "$w = 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon'; "
        + "Set-ItemProperty -LiteralPath $w -Name AutoAdminLogon -Value 1; "
        + $"Set-ItemProperty -LiteralPath $w -Name DefaultUserName -Value {psUserExpr}; "
        + "Set-ItemProperty -LiteralPath $w -Name DefaultDomainName -Value $env:COMPUTERNAME; "
        + $"Set-ItemProperty -LiteralPath $w -Name DefaultPassword -Value $env:{passwordToken}; "
        + "Remove-ItemProperty -LiteralPath $w -Name AutoLogonCount -ErrorAction SilentlyContinue";
}
