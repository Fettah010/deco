using DevCli.Infrastructure;

namespace DevCli.Infrastructure.Tests;

public sealed class SandboxDiagnosticsTests
{
    [Fact]
    public async Task SessionVitals_ParsesSandboxRows()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(
            "\"Image Name\",\"PID\",\"Session Name\",\"Session#\",\"Mem Usage\",\"Status\",\"User Name\",\"CPU Time\",\"Window Title\"\n" +
            "\"WindowsSandboxRemoteSession.exe\",\"2892\",\"RDP-Tcp#1\",\"8\",\"277,180 K\",\"Running\",\"A\",\"0:02:31\",\"Sandbox\"\n" +
            "\"explorer.exe\",\"11\",\"Console\",\"1\",\"10,000 K\",\"Running\",\"B\",\"0:00:05\",\"Desktop\"\n"));

        var vitals = await SandboxDiagnostics.SessionVitalsAsync(fake);

        var one = Assert.Single(vitals);
        Assert.Equal(2892, one.Pid);
        Assert.Equal(151.0, one.CpuSeconds);
    }

    [Fact]
    public async Task SessionVitals_EmptyWhenNone()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(
            "\"explorer.exe\",\"11\",\"Console\",\"1\",\"10,000 K\",\"Running\",\"B\",\"0:00:05\",\"Desktop\"\n"));

        Assert.Empty(await SandboxDiagnostics.SessionVitalsAsync(fake));
    }

    [Fact]
    public void NewPids_Diffs()
    {
        Assert.Equal([4, 5], SandboxDiagnostics.NewPids([1, 2, 3], [2, 3, 4, 5]));
        Assert.Empty(SandboxDiagnostics.NewPids([1], [1]));
    }

    [Fact]
    public async Task RecentVmEvents_ParsesAndFilters()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(
            "2026-10-13T10:00:00.0000000+02:00 33104\n" +
            "2026-10-13T11:00:00.0000000+02:00 18502\n" +
            "garbage\n"));

        var events = await SandboxDiagnostics.RecentVmEventsAsync(
            fake, new DateTimeOffset(2026, 10, 13, 10, 30, 0, TimeSpan.FromHours(2)));

        var one = Assert.Single(events);
        Assert.Equal(18502, one.Id);
        Assert.Equal("VM turned off", one.Note);
    }

    [Fact]
    public async Task RecentVmEvents_EmptyOnFailure()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Fail("no log"));

        Assert.Empty(await SandboxDiagnostics.RecentVmEventsAsync(
            fake, DateTimeOffset.MinValue));
    }
}
