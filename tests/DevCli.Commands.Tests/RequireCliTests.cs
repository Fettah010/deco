using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class RequireCliTests : IDisposable
{
    private readonly string _tmp;

    public RequireCliTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-msstore-" + Guid.NewGuid().ToString("N"));
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
    public void RequireCli_ThrowsWhenMissing()
    {
        string? savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", _tmp);
            var fake = new FakeProc(_ => new Infrastructure.ProcessResult(1, string.Empty, "nope", TimeSpan.FromMilliseconds(5)));
            var ex = Assert.Throws<Core.DecoException>(() => MsstoreConfig.RequireCli(fake));
            Assert.Equal("msstore-missing", ex.Reason);
            Assert.Equal(Core.ExitCodes.UsageOrAuthNeeded, ex.ExitCode);
            Assert.Contains("not found on PATH", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    [Fact]
    public void RequireCli_UsesPurposeInMessage()
    {
        string? savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", _tmp);
            var fake = new FakeProc(_ => new Infrastructure.ProcessResult(1, string.Empty, "nope", TimeSpan.FromMilliseconds(5)));
            var ex = Assert.Throws<Core.DecoException>(() => MsstoreConfig.RequireCli(fake, "API read-back baseline needs it"));
            Assert.Contains("API read-back baseline needs it", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    [Fact]
    public void RequireCli_ReturnsVerifiedCli()
    {
        string? savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", _tmp);
            string exe = Path.Combine(_tmp, "msstore.exe");
            File.WriteAllText(exe, "x");
            var fake = new FakeProc(e =>
                e.EndsWith("msstore.exe", StringComparison.OrdinalIgnoreCase)
                    ? new Infrastructure.ProcessResult(0, "0.4.3", string.Empty, TimeSpan.FromMilliseconds(5))
                    : new Infrastructure.ProcessResult(1, string.Empty, "nope", TimeSpan.FromMilliseconds(5)));
            Assert.Equal(exe, MsstoreConfig.RequireCli(fake));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    private sealed class FakeProc : Infrastructure.IProcessRunner
    {
        private readonly Func<string, Infrastructure.ProcessResult> _respond;

        public FakeProc(Func<string, Infrastructure.ProcessResult> respond) => _respond = respond;

        public Task<Infrastructure.ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? env = null)
        {
            return Task.FromResult(_respond(exe));
        }
    }
}
