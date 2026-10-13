using DevCli.Commands;

namespace DevCli.Commands.Tests;

public sealed class SandboxRunsTests : IDisposable
{
    private readonly string _tmp;

    public SandboxRunsTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "deco-sbruns-" + Guid.NewGuid().ToString("N"));
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

    private string Stage(string runId, string? sentinel = null, string? sentinelText = null,
        string[]? progress = null, int shots = 0)
    {
        string dir = SandboxRuns.StageDir(_tmp, runId);
        string outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);
        if (sentinel is not null)
        {
            File.WriteAllText(Path.Combine(outDir, sentinel), sentinelText ?? string.Empty);
        }

        if (progress is not null)
        {
            File.WriteAllLines(Path.Combine(outDir, "progress.log"), progress);
        }

        if (shots > 0)
        {
            string shotsDir = Path.Combine(dir, "shots");
            Directory.CreateDirectory(shotsDir);
            for (int i = 0; i < shots; i++)
            {
                File.WriteAllBytes(Path.Combine(shotsDir, $"0{i}.png"), [0x89]);
            }
        }

        return dir;
    }

    [Fact]
    public void FindRun_PrefersExplicitId()
    {
        Stage("111");
        string two = Stage("222");

        Assert.Equal(two, SandboxRuns.FindRun(_tmp, "222"));
        Assert.Null(SandboxRuns.FindRun(_tmp, "999"));
    }

    [Fact]
    public void FindRun_PicksNewest()
    {
        string one = Stage("111");
        string two = Stage("222");
        Directory.SetLastWriteTimeUtc(one, DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(two, SandboxRuns.FindRun(_tmp, null));
        Assert.Null(SandboxRuns.FindRun(Path.Combine(_tmp, "empty-missing"), null));
    }

    [Fact]
    public void ReadStatus_Done()
    {
        string dir = Stage("111", sentinel: "done", sentinelText: "manifest.json",
            progress: ["a", "b"], shots: 2);

        SandboxRuns.RunStatus s = SandboxRuns.ReadStatus(dir);

        Assert.Equal("done", s.State);
        Assert.Equal("111", s.RunId);
        Assert.Equal(2, s.Shots);
        Assert.Equal(["a", "b"], s.ProgressTail);
        Assert.Empty(s.SessionPids);
    }

    [Fact]
    public void ReadStatus_FailedFallsBackToErrorTxt()
    {
        string dir = Stage("111", sentinel: "failed", sentinelText: string.Empty);
        File.WriteAllText(Path.Combine(dir, "out", "error.txt"), "boom detail");

        SandboxRuns.RunStatus s = SandboxRuns.ReadStatus(dir);

        Assert.Equal("failed", s.State);
        Assert.Equal("boom detail", s.Detail);
    }

    [Fact]
    public void ReadStatus_Running()
    {
        string dir = Stage("111", progress: ["l1", "l2", "l3"]);

        SandboxRuns.RunStatus s = SandboxRuns.ReadStatus(dir);

        Assert.Equal("running", s.State);
        Assert.Contains("l3", s.Detail);
        Assert.Equal(0, s.Shots);
    }

    [Fact]
    public void ReadStatus_EmptyIsBooting()
    {
        string dir = Stage("111");

        SandboxRuns.RunStatus s = SandboxRuns.ReadStatus(dir);

        Assert.Equal("running", s.State);
        Assert.Contains("stage age", s.Detail);
    }

    [Fact]
    public void ReadStatus_ExposesSessionPids()
    {
        string dir = Stage("111", progress: ["x"]);
        File.WriteAllText(Path.Combine(dir, "session.json"),
            """{"pids":[11,22],"launchedUtc":"2026-10-13T00:00:00Z"}""");

        SandboxRuns.RunStatus s = SandboxRuns.ReadStatus(dir);

        Assert.Equal([11, 22], s.SessionPids);
    }

    [Fact]
    public void HostLog_AppendsAndTails()
    {
        string dir = Path.Combine(_tmp, "run1");
        SandboxRuns.HostLog(dir, "first");
        SandboxRuns.HostLog(dir, "second");

        var tail = SandboxRuns.HostTail(dir, 5);
        Assert.Equal(2, tail.Count);
        Assert.Contains("first", tail[0]);
        Assert.Contains("second", tail[1]);
        Assert.Empty(SandboxRuns.HostTail(Path.Combine(_tmp, "missing"), 5));
    }

    [Fact]
    public void OwnerAlive_CurrentDeadAndMissing()
    {
        string dir = Path.Combine(_tmp, "run1");
        Assert.Null(SandboxRuns.OwnerAlive(dir));

        int me;
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
        {
            me = self.Id;
        }

        SandboxRuns.WriteOwner(dir, me);
        Assert.True(SandboxRuns.OwnerAlive(dir));

        SandboxRuns.WriteOwner(dir, int.MaxValue);
        Assert.False(SandboxRuns.OwnerAlive(dir));
    }

    [Fact]
    public void ReadStatus_OrphanedWhenOwnerGone()
    {
        string dir = Stage("111", progress: ["x"]);
        SandboxRuns.WriteOwner(dir, int.MaxValue);

        SandboxRuns.RunStatus s = SandboxRuns.ReadStatus(dir);

        Assert.Equal("orphaned", s.State);
        Assert.Contains("owner gone", s.Detail);
    }

    [Fact]
    public void ChildArgs_PinsRunIdDropsDetach()
    {
        var args = SandboxRuns.ChildArgs("C:\\repo", "normal", "table",
            "C:\\a\\app.exe", string.Empty, string.Empty, "1.0", 4096, false, 15, string.Empty, "abc",
            vgpu: false, foreground: false);

        Assert.DoesNotContain("--detach", args);
        Assert.Contains("--run-id", args);
        Assert.Equal("abc", args[args.IndexOf("--run-id") + 1]);
        Assert.DoesNotContain("--msix", args);
        Assert.DoesNotContain("--no-network", args);
        Assert.Contains("C:\\a\\app.exe", args);
    }

    [Fact]
    public void ChildArgs_KeepsFlags()
    {
        var args = SandboxRuns.ChildArgs("C:\\repo", "minimal", "json",
            string.Empty, "C:\\a\\app.msix", "C:\\p.json", string.Empty, 2048, true, 10, "C:\\deps", "z",
            vgpu: true, foreground: true);

        Assert.Contains("--no-network", args);
        Assert.Contains("C:\\a\\app.msix", args);
        Assert.DoesNotContain("--exe", args);
        Assert.Contains("--vgpu", args);
        Assert.Contains("--foreground", args);
    }
}
