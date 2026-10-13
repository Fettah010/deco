using DevCli.Infrastructure;

namespace DevCli.Infrastructure.Tests;

/// <summary>
/// The ownership seam: deco may only close/hide processes whose command
/// line proves they belong to deco. These tests pin that contract.
/// </summary>
public sealed class HostProcessListerTests
{
    [Theory]
    [InlineData(
        "\"C:\\Program Files\\microsoft\\Edge\\Application\\msedge.exe\" --user-data-dir=\"C:\\Users\\me\\AppData\\Local\\Formixa\\portal-profile\"",
        "C:\\Users\\me\\AppData\\Local\\Formixa\\portal-profile",
        true)]
    [InlineData(
        "\"C:\\Program Files\\microsoft\\Edge\\Application\\msedge.exe\" --user-data-dir=\"c:/users/me/appdata/local/formixa/portal-profile\"",
        "C:\\Users\\me\\AppData\\Local\\Formixa\\portal-profile",
        true)]
    [InlineData(
        "\"C:\\Windows\\System32\\mstsc.exe\" \"C:\\Users\\me\\AppData\\Local\\Temp\\vmcapture-cap.rdp\"",
        "vmcapture-cap.rdp",
        true)]
    [InlineData(
        "\"C:\\Program Files\\microsoft\\Edge\\Application\\msedge.exe\" --user-data-dir=\"C:\\Users\\me\\AppData\\Local\\Microsoft\\Edge\\User Data\"",
        "C:\\Users\\me\\AppData\\Local\\Formixa\\portal-profile",
        false)]
    [InlineData(null, "anything", false)]
    [InlineData("", "anything", false)]
    [InlineData("\"C:\\x\\msedge.exe\"", "", false)]
    public void ReferencesPath_MatchesOnlyOurs(string? cmd, string fragment, bool expected)
    {
        Assert.Equal(expected, HostProcessLister.ReferencesPath(cmd, fragment));
    }

    [Fact]
    public void ParseCimJson_Empty_IsEmpty()
    {
        Assert.Empty(HostProcessLister.ParseCimJson(""));
        Assert.Empty(HostProcessLister.ParseCimJson("   "));
    }

    [Fact]
    public void ParseCimJson_SingleObject_IsOneRow()
    {
        var rows = HostProcessLister.ParseCimJson(
            """{"ProcessId":1234,"CommandLine":"mstsc.exe foo.rdp"}""");
        var row = Assert.Single(rows);
        Assert.Equal(1234, row.Id);
        Assert.Equal("mstsc.exe foo.rdp", row.CommandLine);
    }

    [Fact]
    public void ParseCimJson_Array_SkipsNullsAndBadRows()
    {
        var rows = HostProcessLister.ParseCimJson(
            """[{"ProcessId":1,"CommandLine":null},null,{"noid":2},{"ProcessId":3,"CommandLine":"x"}]""");
        Assert.Equal([1, 3], rows.Select(r => r.Id).ToList());
        Assert.Null(rows[0].CommandLine);
    }

    [Fact]
    public void EscapeWql_DoublesQuotes()
    {
        Assert.Equal("a''b", HostProcessLister.EscapeWql("a'b"));
    }

    [Fact]
    public async Task ListAsync_NeverThrows_ReturnsEmptyOnFailure()
    {
        var fake = new FakeRunner((_, _) => FakeRunner.Fail("no cim here"));
        var rows = await HostProcessLister.ListAsync(fake, "msedge.exe", TimeSpan.FromSeconds(5));
        Assert.Empty(rows);
    }

    [Fact]
    public async Task MstscConnectAsync_TestSeam_ReturnsPid()
    {
        int pid = await VmGuest.MstscConnectAsync("C:\\Temp\\x.rdp", _ => 4242);
        Assert.Equal(4242, pid);
    }

    [Fact]
    public void StopViewerPid_Noop_OnZeroOrNegative()
    {
        Assert.False(VmGuest.StopViewerPid(0));
        Assert.False(VmGuest.StopViewerPid(-7));
    }

    [Fact]
    public void HideShowViewerPid_Noop_OnZero_NeverThrows()
    {
        var ex = Record.Exception(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                VmGuest.HideViewerPid(0);
                VmGuest.ShowViewerPid(0);
            }
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task StopViewersForRdpAsync_OnlyStopsOurs_NeverKillAll()
    {
        string json = """[{"ProcessId":111,"CommandLine":"mstsc.exe C:\\Temp\\vmcapture-cap.rdp"},{"ProcessId":222,"CommandLine":"mstsc.exe C:\\Temp\\user-session.rdp"}]""";
        var fake = new FakeRunner((_, _) => FakeRunner.Ok(json));
        // PID 111 does not exist on this machine -> stop attempt fails safely.
        int stopped = await VmGuest.StopViewersForRdpAsync(fake, "vmcapture-cap.rdp", TimeSpan.FromSeconds(5));
        Assert.Equal(0, stopped);
        // And crucially: no image-name kill was ever issued.
        Assert.DoesNotContain(fake.Calls, c => c.Contains("/IM"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("Stop-Process"));
    }

    [Fact]
    public async Task FindViewerPidOnceAsync_FindsOnlyOurs()
    {
        string json = """[{"ProcessId":111,"CommandLine":"mstsc.exe C:\\Temp\\vmcapture-cap.rdp"},{"ProcessId":222,"CommandLine":"mstsc.exe C:\\Temp\\user-session.rdp"}]""";
        var fake = new FakeRunner((_, _) => FakeRunner.Ok(json));
        Assert.Equal(111, await VmGuest.FindViewerPidOnceAsync(fake, "vmcapture-cap.rdp"));
        Assert.Equal(0, await VmGuest.FindViewerPidOnceAsync(fake, "nothing-matches.rdp"));
    }

    [Theory]
    [InlineData("New-PSSession : [vm] An error has occurred which Windows PowerShell cannot handle. A remote session might have ended. FullyQualifiedErrorId : PSSessionOpenFailed", true)]
    [InlineData("Guest command failed (exit 1): throw 'guest expand failed.'", false)]
    [InlineData("", false)]
    public void IsSessionOpenFailure_Classifies(string output, bool expected)
    {
        Assert.Equal(expected, VmGuest.IsSessionOpenFailure(output));
    }

    [Fact]
    public async Task InvokeGuestAsync_RetriesSessionOpenFailure_ThenSucceeds()
    {
        int calls = 0;
        var fake = new FakeRunner((_, _) =>
        {
            calls++;
            return calls < 3
                ? FakeRunner.Fail("New-PSSession : PSSessionOpenFailed, remote session might have ended")
                : FakeRunner.Ok("ok");
        });
        ProcessResult r = await VmGuest.InvokeGuestAsync(
            fake, "vm", "cap", "pw", "'hi'", TimeSpan.FromSeconds(30));
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task InvokeGuestAsync_NoRetry_OnSnippetFailure()
    {
        int calls = 0;
        var fake = new FakeRunner((_, _) =>
        {
            calls++;
            return FakeRunner.Fail("guest expand failed");
        });
        await Assert.ThrowsAsync<Core.DecoException>(() => VmGuest.InvokeGuestAsync(
            fake, "vm", "cap", "pw", "'hi'", TimeSpan.FromSeconds(30)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HostPsAsync_RetriesSessionOpenFailure_ThenSucceeds()
    {
        int calls = 0;
        var fake = new FakeRunner((_, _) =>
        {
            calls++;
            return calls < 2
                ? FakeRunner.Fail("New-PSSession : PSSessionOpenFailed")
                : FakeRunner.Ok("Running");
        });
        ProcessResult r = await VmGuest.HostPsAsync(fake, "'x'", TimeSpan.FromSeconds(30));
        Assert.Equal("Running", r.StdOut);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CmdkeySetAsync_ScopesToTarget()
    {
        var fake = new FakeRunner((_, _) => FakeRunner.Ok("stored"));
        await VmGuest.CmdkeySetAsync(fake, "10.0.0.9", "cap", "pw", TimeSpan.FromSeconds(30));
        string call = Assert.Single(fake.Calls);
        Assert.StartsWith("cmdkey.exe ", call);
        Assert.Contains("/generic:TERMSRV/10.0.0.9", call);
    }
}
