using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class VmGuestStateTests : IDisposable
{
    private readonly string _tmp;

    public VmGuestStateTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void RandomPassword_Shape()
    {
        string pw = VmCredentialStore.RandomPassword();
        Assert.Equal(20, pw.Length);
        Assert.True(VmCredentialStore.IsRandomPassword(pw));
        Assert.NotEqual(pw, VmCredentialStore.RandomPassword());
    }

    [Fact]
    public void Credential_RoundTrip_Dpapi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        VmCredentialStore.SavePassword("s3cret-pw", "DecoTest", "vm1", _tmp);
        Assert.True(File.Exists(Path.Combine(_tmp, "vmcred-vm1.txt")));
        // Wire format = ps1-compatible SecureString hex (shared file with the .ps1 kit).
        string stored = File.ReadAllText(Path.Combine(_tmp, "vmcred-vm1.txt")).Trim();
        Assert.Matches("^[0-9a-f]+$", stored);
        Assert.Equal("s3cret-pw", VmCredentialStore.ResolvePassword("", "DecoTest", "vm1", _tmp));
        Assert.Equal("flag-wins", VmCredentialStore.ResolvePassword("flag-wins", "DecoTest", "vm1", _tmp));
        Assert.Throws<Core.DecoException>(() =>
            VmCredentialStore.ResolvePassword("", "DecoTest", "missing", _tmp));
    }

    [Fact]
    public void Credential_ReadsPs1SecureStringFormat()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The exact bytes PowerShell's ConvertFrom-SecureString produces for
        // "s3cret-pw" can't be hardcoded (DPAPI is machine-bound), but the
        // ps1-format decode path is what SavePassword now writes — and the
        // real ps1-written store on this machine resolves through it.
        // Here: legacy deco base64/UTF-8 blobs must still read.
        byte[] legacyProtected = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes("legacy-pw"), null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        string legacyText = Convert.ToBase64String(legacyProtected);
        Assert.Equal("legacy-pw", VmCredentialStore.DecodeStoreText(legacyText));
    }

    [Fact]
    public void ParseQuser_FindsSession()
    {
        string q = "USERNAME SESSIONNAME ID STATE IDLE TIME LOGON TIME\n"
            + ">cap console 1 Active none 10/8/2026 8:00 AM";
        var s = VmGuestState.ParseQuser(q, "cap");
        Assert.NotNull(s);
        Assert.Equal("console", s.SessionName);
        Assert.Equal(1, s.SessionId);
        Assert.Equal("Active", s.State);
        Assert.Null(VmGuestState.ParseQuser(q, "other"));
    }

    [Fact]
    public void Classify_Readiness()
    {
        var ready = new VmGuestState.GuestProbe(
            DateTime.UtcNow, new VmGuestState.SessionInfo("cap", "rdp-tcp#1", 2, "Active"),
            Locked: false, HasExplorer: true, FreeGB: 50, HasDotnet: true);
        Assert.Equal(VmGuestState.Readiness.Ready, VmGuestState.Classify(ready, 1920, 1080));
        Assert.Equal(VmGuestState.Readiness.TooSmall, VmGuestState.Classify(ready, 1024, 768));
        Assert.Equal(VmGuestState.Readiness.Locked, VmGuestState.Classify(ready with { Locked = true }, 1920, 1080));
        Assert.Equal(VmGuestState.Readiness.NoExplorer, VmGuestState.Classify(ready with { HasExplorer = false }, 1920, 1080));
        Assert.Equal(VmGuestState.Readiness.NoSession, VmGuestState.Classify(ready with { Session = null }, 1920, 1080));
        Assert.True(VmGuestState.IsBigEnough("1920x1080"));
        Assert.False(VmGuestState.IsBigEnough("1024x768"));
    }

    [Fact]
    public void RdpFileContent_SilentConnect()
    {
        string[] lines = VmGuestState.RdpFileContent("1.2.3.4", "cap");
        Assert.Contains("desktopwidth:i:1920", lines);
        Assert.Contains("full address:s:1.2.3.4", lines);
        Assert.Contains("username:s:cap", lines);
        Assert.Contains("authentication level:i:0", lines);
        Assert.False(VmGuestState.NeedsRewrite(string.Join("\n", lines), lines));
        Assert.True(VmGuestState.NeedsRewrite("other", lines));
    }
}
