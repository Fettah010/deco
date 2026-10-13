using DevCli.Infrastructure;

namespace DevCli.Infrastructure.Tests;

public sealed class SandboxRunnerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _out;

    public SandboxRunnerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-sbrun-" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_tmp, "out");
        Directory.CreateDirectory(_out);
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
    public async Task WaitSentinel_DoneWins()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await File.WriteAllTextAsync(Path.Combine(_out, "done"), "manifest.json");
        });

        SandboxRunner.WaitResult r = await SandboxRunner.WaitSentinelAsync(
            _out, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100));

        Assert.True(r.Done);
        Assert.Equal("manifest.json", r.Note);
    }

    [Fact]
    public async Task WaitSentinel_FailedCarriesNote()
    {
        await File.WriteAllTextAsync(Path.Combine(_out, "failed"), "FlaUI missing");

        SandboxRunner.WaitResult r = await SandboxRunner.WaitSentinelAsync(
            _out, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100));

        Assert.False(r.Done);
        Assert.Equal("FlaUI missing", r.Note);
    }

    [Fact]
    public async Task WaitSentinel_TimesOut()
    {
        SandboxRunner.WaitResult r = await SandboxRunner.WaitSentinelAsync(
            _out, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100));

        Assert.False(r.Done);
        Assert.Contains("timeout", r.Note);
    }

    [Fact]
    public async Task Launch_UsesDetachedStartProcess()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(string.Empty));
        string wsb = Path.Combine(_tmp, "run dir", "run.wsb");
        Directory.CreateDirectory(Path.GetDirectoryName(wsb)!);
        await File.WriteAllTextAsync(wsb, "<Configuration/>");

        await SandboxRunner.LaunchAsync(fake, wsb);

        Assert.Single(fake.Calls);
        Assert.StartsWith("powershell.exe", fake.Calls[0], StringComparison.Ordinal);
        Assert.Contains("Start-Process", fake.Calls[0], StringComparison.Ordinal);
        Assert.Contains("WindowsSandbox.exe", fake.Calls[0], StringComparison.Ordinal);
        Assert.Contains(wsb, fake.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kill_SignalsAllSandboxImages()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(string.Empty));

        await SandboxRunner.KillAsync(fake, log: null);

        Assert.Equal(4, fake.Calls.Count);
        Assert.All(fake.Calls, c => Assert.StartsWith("taskkill.exe", c, StringComparison.Ordinal));
        Assert.Contains(fake.Calls, c => c.Contains("WindowsSandboxRemoteSession.exe"));
        Assert.Contains(fake.Calls, c => c.Contains("WindowsSandboxServer.exe"));
    }
}
