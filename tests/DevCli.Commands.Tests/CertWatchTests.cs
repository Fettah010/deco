using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class CertWatchTests : IDisposable
{
    private readonly string _tmp;

    public CertWatchTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-certwatch-" + Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData("Submission Status = Certification", "Certification")]
    [InlineData("Submission Status - Certification", "Certification")]
    [InlineData("line1\nSubmission Status = CommitStarted\nSubmission Status = Certification", "Certification")]
    [InlineData("Submission Status = Published", "Published")]
    [InlineData("no status here", null)]
    [InlineData("", null)]
    public void ParseStatus_HandlesBothShapes_LastWins(string output, string? expected)
    {
        Assert.Equal(expected, StoreCertWatch.ParseStatus(output));
    }

    [Theory]
    [InlineData("Published", true, false)]
    [InlineData("published", true, false)]
    [InlineData("Certification", false, false)]
    [InlineData("CommitStarted", false, false)]
    [InlineData("Failed", false, true)]
    [InlineData("Canceled", false, true)]
    [InlineData("Unknown", false, false)]
    public void Terminal_Classification(string status, bool published, bool failed)
    {
        Assert.Equal(published, StoreCertWatch.IsPublished(status));
        Assert.Equal(failed, StoreCertWatch.IsFailed(status));
        Assert.Equal(published || failed, StoreCertWatch.IsTerminal(status));
    }

    [Theory]
    [InlineData("CommitStarted", true)]
    [InlineData("Certification", true)]
    [InlineData("Publishing", true)]
    [InlineData("Uploading", true)]
    [InlineData("Unknown", true)]
    [InlineData("Published", false)]
    [InlineData("Failed", false)]
    [InlineData("SomethingNew", false)]
    public void Pending_Classification(string status, bool expected)
    {
        Assert.Equal(expected, StoreCertWatch.IsPending(status));
    }

    [Fact]
    public void State_RoundTrips()
    {
        var state = new StoreCertWatch.WatchState("9ABCDEF12345", "Certification", DateTimeOffset.UtcNow, false, false, null);
        StoreCertWatch.SaveState(state, _tmp);
        var back = StoreCertWatch.LoadState("9ABCDEF12345", _tmp);
        Assert.NotNull(back);
        Assert.Equal("Certification", back.LastStatus);
        Assert.False(back.Terminal);
        Assert.False(back.Published);
        Assert.Null(StoreCertWatch.LoadState("nope", _tmp));
    }

    [Fact]
    public void StatePath_Sanitizes()
    {
        string path = StoreCertWatch.StatePath("9ABCDEF12345", _tmp);
        Assert.Equal(Path.Combine(_tmp, "certwatch-9ABCDEF12345.json"), path);
        Assert.Contains("unknown", StoreCertWatch.StatePath("!!!", _tmp));
    }

    [Fact]
    public void TaskName_IsStable()
    {
        Assert.Equal("deco-certwatch-9abcdef12345", StoreCertWatch.TaskName("9ABCDEF12345"));
    }

    [Fact]
    public void ListRemembered_DedupsByAppId()
    {
        StoreCertWatch.SaveLastAppId("proj-a", "9AAA", _tmp);
        StoreCertWatch.SaveLastAppId("proj-b", "9AAA", _tmp);
        StoreCertWatch.SaveLastAppId("proj-c", "9BBB", _tmp);
        var list = StoreCertWatch.ListRemembered(_tmp);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, a => a.AppId == "9AAA");
        Assert.Contains(list, a => a.AppId == "9BBB");
        Assert.Empty(StoreCertWatch.ListRemembered(Path.Combine(_tmp, "nope")));
    }

    [Theory]
    [InlineData("", 3, 0)]
    [InlineData("2", 3, 1)]
    [InlineData("3", 3, 2)]
    [InlineData("0", 3, null)]
    [InlineData("4", 3, null)]
    [InlineData("x", 3, null)]
    [InlineData("", 0, null)]
    public void ParseSelection_Maps(string? input, int count, int? expected)
    {
        Assert.Equal(expected, StoreCertWatch.ParseSelection(input, count));
    }

    [Fact]
    public void Memory_RoundTripsPerProject()
    {
        Assert.Null(StoreCertWatch.LoadLastAppId("proj-a", _tmp));
        StoreCertWatch.SaveLastAppId("proj-a", "9AAA", _tmp);
        StoreCertWatch.SaveLastAppId("proj-b", "9BBB", _tmp);
        Assert.Equal("9AAA", StoreCertWatch.LoadLastAppId("proj-a", _tmp));
        Assert.Equal("9BBB", StoreCertWatch.LoadLastAppId("proj-b", _tmp));
        Assert.Equal("9AAA", StoreCertWatch.LoadLastAppId("PROJ-A", _tmp));
        StoreCertWatch.SaveLastAppId("proj-a", "9CCC", _tmp);
        Assert.Equal("9CCC", StoreCertWatch.LoadLastAppId("proj-a", _tmp));
    }

    [Theory]
    [InlineData(null, "global")]
    [InlineData("", "global")]
    [InlineData("C:\\repo", "C:\\repo")]
    public void RepoKey_FallsBackToGlobal(string? root, string expected)
    {
        Assert.Equal(expected, StoreCertWatch.RepoKey(root));
    }

    [Fact]
    public void AgeLabel_Buckets()
    {
        Assert.Equal("just now", StoreCertWatch.AgeLabel(DateTimeOffset.UtcNow));
        Assert.Equal("12m ago", StoreCertWatch.AgeLabel(DateTimeOffset.UtcNow.AddMinutes(-12)));
        Assert.Equal("3h ago", StoreCertWatch.AgeLabel(DateTimeOffset.UtcNow.AddHours(-3)));
        Assert.Equal("2d ago", StoreCertWatch.AgeLabel(DateTimeOffset.UtcNow.AddDays(-2)));
    }

    [Fact]
    public void IsFresh_RespectsMaxAge()
    {
        var fresh = new StoreCertWatch.WatchState("x", "Certification", DateTimeOffset.UtcNow.AddMinutes(-5), false, false, null);
        var stale = new StoreCertWatch.WatchState("x", "Certification", DateTimeOffset.UtcNow.AddHours(-2), false, false, null);
        Assert.True(StoreCertWatch.IsFresh(fresh, TimeSpan.FromMinutes(15)));
        Assert.False(StoreCertWatch.IsFresh(stale, TimeSpan.FromMinutes(15)));
        Assert.False(StoreCertWatch.IsFresh(null, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task RegisterTask_BuildsHourlySchtasks()
    {
        var fake = new FakeProc(_ => new Infrastructure.ProcessResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5)));
        await StoreCertWatch.RegisterTaskAsync(fake, "deco.exe", [], "9ABCDEF12345", 55, TimeSpan.FromSeconds(5));
        string call = Assert.Single(fake.Calls);
        Assert.StartsWith("schtasks.exe ", call);
        Assert.Contains("/create", call);
        Assert.Contains("deco-certwatch-9abcdef12345", call);
        Assert.Contains("hourly", call);
        Assert.Contains("--timeout-min 55", call);
    }

    [Fact]
    public async Task UnregisterTask_DeletesByName()
    {
        var fake = new FakeProc(_ => new Infrastructure.ProcessResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5)));
        await StoreCertWatch.UnregisterTaskAsync(fake, "9ABCDEF12345", TimeSpan.FromSeconds(5));
        string call = Assert.Single(fake.Calls);
        Assert.Contains("/delete", call);
        Assert.Contains("deco-certwatch-9abcdef12345", call);
    }

    [Fact]
    public void SelfLaunch_ReturnsUsablePrefix()
    {
        var (file, pre) = SelfLaunch.LaunchPrefix();
        Assert.False(string.IsNullOrWhiteSpace(file));
        Assert.NotNull(pre);
    }

    private sealed class FakeProc : Infrastructure.IProcessRunner
    {
        private readonly Func<string, Infrastructure.ProcessResult> _respond;

        public List<string> Calls { get; } = [];

        public FakeProc(Func<string, Infrastructure.ProcessResult> respond) => _respond = respond;

        public Task<Infrastructure.ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? env = null)
        {
            Calls.Add(exe + " " + string.Join(" ", args));
            return Task.FromResult(_respond(exe));
        }
    }
}
