using DevCli.Infrastructure;

namespace DevCli.Infrastructure.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CapturesDotnetVersion()
    {
        var runner = new ProcessRunner();
        ProcessResult r = await runner.RunAsync("dotnet", ["--version"], timeout: TimeSpan.FromSeconds(60));
        Assert.Equal(0, r.ExitCode);
        Assert.Matches(@"^\d+\.\d+", r.StdOut.Trim());
        Assert.True(r.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_SurfacesCode()
    {
        var runner = new ProcessRunner();
        ProcessResult r = await runner.RunAsync(
            "dotnet", ["nonexistent-subcommand-xyz"], timeout: TimeSpan.FromSeconds(60));
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public async Task RunAsync_MissingExe_ThrowsDeco()
    {
        var runner = new ProcessRunner();
        await Assert.ThrowsAsync<Core.DecoException>(() =>
            runner.RunAsync("deco-definitely-missing-exe-xyz", [], timeout: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task RunAsync_PassesEnv()
    {
        var runner = new ProcessRunner();
        ProcessResult r = await runner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "$env:DECO_PROBE_TEST"],
            timeout: TimeSpan.FromSeconds(30),
            env: new Dictionary<string, string> { ["DECO_PROBE_TEST"] = "env-ok" });
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("env-ok", r.StdOut);
    }
}
