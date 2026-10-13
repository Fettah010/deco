using DevCli.Infrastructure;

namespace DevCli.Infrastructure.Tests;

public sealed class PortalSessionTests
{
    [Theory]
    [InlineData("https://partner.microsoft.com/dashboard/overview", "Partner Center", true)]
    [InlineData("https://login.microsoftonline.com/tenant", "Sign in", false)]
    [InlineData("https://partner.microsoft.com/dashboard", "Sign in to your account", false)]
    [InlineData("https://account.microsoft.com/profile", "Profile", false)]
    [InlineData("", "", false)]
    public void IsLoggedInUrl_Classifies(string url, string title, bool expected)
    {
        Assert.Equal(expected, PortalSession.IsLoggedInUrl(url, title));
    }

    [Fact]
    public void DefaultProfileDir_UsesAppId()
    {
        string dir = PortalSession.DefaultProfileDir("SampleApp");
        Assert.EndsWith(Path.Combine("SampleApp", "portal-profile"), dir);
    }

    [Fact]
    public void WipeProfile_DeletesDir_WithoutTouchingBrowsers()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-prof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "x.txt"), "x");
        // Empty process list: no browser may be closed, dir still goes.
        var fake = new FakeRunner((_, _) => FakeRunner.Ok("[]"));
        PortalSession.WipeProfile(tmp, fake);
        Assert.False(Directory.Exists(tmp));
        Assert.Contains(fake.Calls, c => c.Contains("powershell.exe") && c.Contains("msedge.exe"));
        // Idempotent.
        PortalSession.WipeProfile(tmp, fake);
    }

    [Fact]
    public void WipeProfile_IgnoresBrowsersUsingOtherProfiles()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "deco-prof-" + Guid.NewGuid().ToString("N"));
        string other = Path.Combine(Path.GetTempPath(), "deco-prof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        // A running Edge with a DIFFERENT profile must not block the wipe
        // (and must never be closed — selection is command-line based).
        string json = "{\"ProcessId\":424242,\"CommandLine\":\"\\\"C:\\\\Program Files\\\\microsoft\\\\Edge\\\\Application\\\\msedge.exe\\\" --user-data-dir=\\\"" + other.Replace("\\", "\\\\", StringComparison.Ordinal) + "\\\" --other-flag\"}";
        var fake = new FakeRunner((_, _) => FakeRunner.Ok(json));
        PortalSession.WipeProfile(tmp, fake);
        Assert.False(Directory.Exists(tmp));
    }

    [Fact]
    public void SelectProfileProcesses_KeepsOnlyOurs()
    {
        var all = new List<HostProcess>
        {
            new(1, "\"C:\\Program Files\\microsoft\\Edge\\Application\\msedge.exe\" --user-data-dir=\"C:\\Users\\me\\AppData\\Local\\Formixa\\portal-profile\""),
            new(2, "\"C:\\Program Files\\microsoft\\Edge\\Application\\msedge.exe\" --user-data-dir=\"C:\\Users\\me\\AppData\\Local\\Microsoft\\Edge\\User Data\""),
            new(3, null),
        };
        var ours = PortalSession.SelectProfileProcesses(
            all, "C:\\Users\\me\\AppData\\Local\\Formixa\\portal-profile");
        Assert.Equal([1], ours.Select(p => p.Id).ToList());
    }
}
