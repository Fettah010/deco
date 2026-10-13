using DevCli.Infrastructure;

namespace DevCli.Infrastructure.Tests;

public sealed class SandboxSlotTests
{
    [Fact]
    public async Task IsRunning_TrueWhenSessionExists()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(
            "\"WindowsSandboxRemoteSession.exe\",\"2892\",\"RDP-Tcp#1\",\"8\",\"277,180,416 K\"\n" +
            "\"explorer.exe\",\"11\",\"Console\",\"1\",\"10,000 K\"\n"));

        Assert.True(await SandboxRunner.IsRunningAsync(fake));
    }

    [Fact]
    public async Task IsRunning_TrueForLauncherStubAlone()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(
            "\"WindowsSandbox.exe\",\"44\",\"Console\",\"1\",\"9,000 K\"\n"));

        Assert.True(await SandboxRunner.IsRunningAsync(fake));
    }

    [Fact]
    public async Task IsRunning_FalseWhenAbsent()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(
            "\"explorer.exe\",\"11\",\"Console\",\"1\",\"10,000 K\"\n"));

        Assert.False(await SandboxRunner.IsRunningAsync(fake));
    }

    [Fact]
    public async Task IsRunning_NullOnProbeFailure()
    {
        var fake = new FakeRunner((exe, args) => throw new InvalidOperationException("denied"));

        Assert.Null(await SandboxRunner.IsRunningAsync(fake));
    }

    [Fact]
    public async Task ListSandboxPids_ParsesOnlyOurs()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(
            "\"WindowsSandboxServer.exe\",\"28140\",\"Services\",\"0\",\"76,000 K\"\n" +
            "\"explorer.exe\",\"11\",\"Console\",\"1\",\"10,000 K\"\n" +
            "garbage-line\n" +
            "\"WindowsSandboxRemoteSession.exe\",\"notapid\",\"RDP\",\"8\",\"1 K\"\n" +
            "\"WindowsSandboxClient.exe\",\"0\",\"Console\",\"1\",\"1 K\"\n"));

        List<int> pids = await SandboxRunner.ListSandboxPidsAsync(fake);

        Assert.Equal([28140], pids);
    }

    [Fact]
    public async Task WaitForSession_PrefersStableOverStub()
    {
        int calls = 0;
        var fake = new FakeRunner((exe, args) =>
        {
            calls++;
            return calls < 3
                ? FakeRunner.Ok("\"WindowsSandbox.exe\",\"44\",\"Console\",\"1\",\"9,000 K\"\n")
                : FakeRunner.Ok("\"WindowsSandbox.exe\",\"44\",\"Console\",\"1\",\"9,000 K\"\n" +
                    "\"WindowsSandboxServer.exe\",\"28140\",\"Services\",\"0\",\"76,000 K\"\n");
        });

        List<int> pids = await SandboxRunner.WaitForSessionAsync(fake, TimeSpan.FromSeconds(60));

        Assert.Equal([28140], pids);
    }

    [Fact]
    public async Task WaitForSession_ReturnsWhenAppearing()
    {
        int calls = 0;
        var fake = new FakeRunner((exe, args) =>
        {
            calls++;
            return calls < 3
                ? FakeRunner.Ok("\"explorer.exe\",\"11\",\"Console\",\"1\",\"10,000 K\"\n")
                : FakeRunner.Ok("\"WindowsSandboxServer.exe\",\"28140\",\"Services\",\"0\",\"76,000 K\"\n");
        });

        List<int> pids = await SandboxRunner.WaitForSessionAsync(fake, TimeSpan.FromSeconds(30));

        Assert.Equal([28140], pids);
    }

    [Fact]
    public async Task WaitForSession_EmptyOnTimeout()
    {
        var fake = new FakeRunner((exe, args) => FakeRunner.Ok(
            "\"explorer.exe\",\"11\",\"Console\",\"1\",\"10,000 K\"\n"));

        List<int> pids = await SandboxRunner.WaitForSessionAsync(fake, TimeSpan.FromMilliseconds(300));

        Assert.Empty(pids);
    }

    [Fact]
    public async Task KillPids_KillsOnlyVerifiedOurs()
    {
        var fake = new FakeRunner((exe, args) =>
        {
            string joined = string.Join(" ", args);
            if (joined.Contains("PID eq 111"))
            {
                return FakeRunner.Ok("\"WindowsSandboxServer.exe\",\"111\",\"Services\",\"0\",\"1 K\"\n");
            }

            if (joined.Contains("PID eq 222"))
            {
                return FakeRunner.Ok("\"notepad.exe\",\"222\",\"Console\",\"1\",\"1 K\"\n");
            }

            return FakeRunner.Ok(string.Empty);
        });

        await SandboxRunner.KillPidsAsync(fake, [111, 222], log: null);

        Assert.Contains(fake.Calls, c => c.Contains("/PID") && c.Contains("111"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("/PID") && c.Contains("222"));
    }
}
