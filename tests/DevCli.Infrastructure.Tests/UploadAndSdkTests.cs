using DevCli.Infrastructure;

namespace DevCli.Infrastructure.Tests;

public sealed class SdkToolsTests : IDisposable
{
    private readonly string _tmp;

    public SdkToolsTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-sdk-" + Guid.NewGuid().ToString("N"));
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
    public void Find_PrefersNewestKitsVersion()
    {
        string kits = Path.Combine(_tmp, "Kits");
        Directory.CreateDirectory(Path.Combine(kits, "10.0.10000.0", "x64"));
        Directory.CreateDirectory(Path.Combine(kits, "10.0.28000.0", "x64"));
        File.WriteAllText(Path.Combine(kits, "10.0.10000.0", "x64", "makeappx.exe"), "x");
        string newest = Path.Combine(kits, "10.0.28000.0", "x64", "makeappx.exe");
        File.WriteAllText(newest, "x");

        Assert.Equal(newest, SdkTools.Find("makeappx", [kits], []));
    }

    [Fact]
    public void Find_ReturnsNull_WhenMissing()
    {
        Assert.Null(SdkTools.Find("makeappx", [Path.Combine(_tmp, "nope")], []));
    }

    [Fact]
    public void Find_ChecksPathFirst()
    {
        string bin = Path.Combine(_tmp, "bin");
        Directory.CreateDirectory(bin);
        string onPath = Path.Combine(bin, "signtool.exe");
        File.WriteAllText(onPath, "x");
        Assert.Equal(onPath, SdkTools.Find("signtool", [Path.Combine(_tmp, "nope")], [bin]));
    }
}

public sealed class FakeRunner : IProcessRunner
{
    private readonly Func<string, IReadOnlyList<string>, ProcessResult> _respond;

    public List<string> Calls { get; } = [];

    public FakeRunner(Func<string, IReadOnlyList<string>, ProcessResult> respond) => _respond = respond;

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? env = null)
    {
        Calls.Add(exe + " " + string.Join(" ", args));
        return Task.FromResult(_respond(exe, args));
    }

    public static ProcessResult Ok(string stdout = "") => new(0, stdout, string.Empty, TimeSpan.FromMilliseconds(5));

    public static ProcessResult Fail(string stderr = "boom") => new(1, string.Empty, stderr, TimeSpan.FromMilliseconds(5));
}

public sealed class GithubUploaderTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _asset;

    public GithubUploaderTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-gh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _asset = Path.Combine(_tmp, "app.nupkg");
        File.WriteAllBytes(_asset, new byte[10]);
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

    private static GithubUploader.Plan Plan(string tag = "v1.0.0") =>
        new(tag, "T 1.0.0", [], "owner/repo", string.Empty, Publish: false, PreRelease: false);

    [Fact]
    public void Validate_RejectsEmptyAndMissing()
    {
        Assert.Throws<Core.DecoException>(() => GithubUploader.Validate(Plan() with { Files = [] }, File.Exists));
        Assert.Throws<Core.DecoException>(() => GithubUploader.Validate(
            Plan() with { Files = [Path.Combine(_tmp, "nope.bin")] }, File.Exists));
    }

    [Fact]
    public void ReleaseUploadArgs_CarriesClobberAndRepo()
    {
        var plan = Plan() with { Files = [_asset] };
        var args = GithubUploader.ReleaseUploadArgs(plan, _asset);
        Assert.Equal(["release", "upload", "v1.0.0", _asset, "--repo", "owner/repo", "--clobber"], args);
    }

    [Fact]
    public async Task UploadAsync_CreatesDraft_WhenMissing_ThenUploads()
    {
        var plan = Plan() with { Files = [_asset] };
        var runner = new FakeRunner((exe, args) =>
        {
            string joined = string.Join(" ", args);
            if (joined.StartsWith("release view", StringComparison.Ordinal))
            {
                return FakeRunner.Fail("not found");
            }

            return FakeRunner.Ok();
        });

        var logs = new List<string>();
        GithubUploader.Result r = await GithubUploader.UploadAsync(
            runner, plan, logs.Add, (_, _) => Task.CompletedTask);

        Assert.Empty(r.Failed);
        Assert.Single(r.Uploaded);
        Assert.Equal(10, r.TotalBytes);
        Assert.Contains(runner.Calls, c => c.Contains("release create") && c.Contains("--draft"));
        Assert.Contains(runner.Calls, c => c.Contains("release upload") && c.Contains("--clobber"));
        Assert.Contains(logs, l => l.Contains("Created draft release"));
    }

    [Fact]
    public async Task UploadAsync_RetriesThenFails()
    {
        var plan = Plan() with { Files = [_asset] };
        var runner = new FakeRunner((exe, args) =>
            string.Join(" ", args).StartsWith("release view", StringComparison.Ordinal)
                ? FakeRunner.Ok()
                : FakeRunner.Fail());

        await Assert.ThrowsAsync<Core.DecoException>(() =>
            GithubUploader.UploadAsync(runner, plan, null, (_, _) => Task.CompletedTask));
        Assert.Equal(3, runner.Calls.Count(c => c.Contains("release upload")));
    }
}
