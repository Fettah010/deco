namespace DevCli.Infrastructure;

/// <summary>
/// Port of Invoke-GithubParallelUpload.ps1 over `gh`: draft-first release,
/// one concurrent upload per file (3 attempts, linear backoff), --clobber
/// re-runs, totals line (MB, elapsed, effective MB/s), optional publish.
/// Concurrency via Task.WhenAll (replaces Start-Job); interact only through
/// IProcessRunner so tests run fully offline with a fake.
/// </summary>
public static class GithubUploader
{
    public sealed record Plan(
        string Tag,
        string Title,
        string[] Files,
        string Repo,
        string NotesFile,
        bool Publish,
        bool PreRelease,
        int MaxAttempts = 3);

    public sealed record Result(
        string[] Uploaded,
        string[] Failed,
        long TotalBytes,
        TimeSpan Elapsed);

    public static void Validate(Plan plan, Func<string, bool> fileExists)
    {
        if (plan.Files.Length == 0)
        {
            throw new Core.DecoException("No files to upload.", "upload-no-files", Core.ExitCodes.UsageOrAuthNeeded);
        }

        foreach (string f in plan.Files)
        {
            if (!fileExists(f))
            {
                throw new Core.DecoException($"Asset not found: {f}", "upload-asset-missing");
            }
        }

        if (string.IsNullOrWhiteSpace(plan.Tag) || string.IsNullOrWhiteSpace(plan.Repo))
        {
            throw new Core.DecoException(
                "Tag and --repo are required (e.g. --tag v1.0.0 --repo owner/name).",
                "upload-no-target",
                Core.ExitCodes.UsageOrAuthNeeded);
        }
    }

    public static List<string> ReleaseViewArgs(Plan plan) =>
        ["release", "view", plan.Tag, "--repo", plan.Repo, "--json", "tagName"];

    public static List<string> ReleaseCreateArgs(Plan plan)
    {
        var args = new List<string>
            { "release", "create", plan.Tag, "--repo", plan.Repo, "--draft", "--title", plan.Title };
        if (plan.PreRelease)
        {
            args.Add("--prerelease");
        }

        if (!string.IsNullOrWhiteSpace(plan.NotesFile))
        {
            args.AddRange(["--notes-file", plan.NotesFile]);
        }

        return args;
    }

    public static List<string> ReleaseUploadArgs(Plan plan, string file) =>
        ["release", "upload", plan.Tag, file, "--repo", plan.Repo, "--clobber"];

    public static async Task<Result> UploadAsync(
        IProcessRunner runner,
        Plan plan,
        Action<string>? log = null,
        Func<int, CancellationToken, Task>? delay = null,
        CancellationToken ct = default)
    {
        delay ??= Task.Delay;
        var started = DateTimeOffset.UtcNow;
        long totalBytes = plan.Files.Sum(f =>
        {
            try
            {
                return new FileInfo(f).Length;
            }
            catch
            {
                return 0;
            }
        });

        ProcessResult view = await runner.RunAsync("gh", ReleaseViewArgs(plan), timeout: TimeSpan.FromSeconds(60), cancellationToken: ct);
        if (view.ExitCode != 0)
        {
            ProcessResult created = await runner.RunAsync("gh", ReleaseCreateArgs(plan), timeout: TimeSpan.FromSeconds(120), cancellationToken: ct);
            if (created.ExitCode != 0)
            {
                throw new Core.DecoException(
                    $"gh release create failed for {plan.Tag}: {Tail(created)}",
                    "upload-create-failed");
            }

            log?.Invoke($"Created draft release {plan.Tag}.");
        }
        else if (plan.PreRelease)
        {
            ProcessResult edited = await runner.RunAsync(
                "gh", ["release", "edit", plan.Tag, "--repo", plan.Repo, "--prerelease"],
                timeout: TimeSpan.FromSeconds(60), cancellationToken: ct);
            if (edited.ExitCode != 0)
            {
                throw new Core.DecoException($"gh release edit --prerelease failed for {plan.Tag}.", "upload-edit-failed");
            }
        }

        log?.Invoke($"Uploading {plan.Files.Length} asset(s) in parallel to {plan.Tag} ...");
        var tasks = plan.Files.Select(f => UploadOneAsync(runner, plan, f, log, delay, ct)).ToArray();
        bool[] oks = await Task.WhenAll(tasks);

        var failed = plan.Files.Where((f, i) => !oks[i]).Select(Path.GetFileName).ToArray();
        var uploaded = plan.Files.Where((f, i) => oks[i]).ToArray();
        if (failed.Length > 0)
        {
            throw new Core.DecoException(
                "Parallel upload failed for: " + string.Join(", ", failed),
                "upload-failed");
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;
        if (plan.Publish)
        {
            ProcessResult edited = await runner.RunAsync(
                "gh", ["release", "edit", plan.Tag, "--repo", plan.Repo, "--draft=false"],
                timeout: TimeSpan.FromSeconds(60), cancellationToken: ct);
            if (edited.ExitCode != 0)
            {
                throw new Core.DecoException($"gh release edit --draft=false failed for {plan.Tag}.", "upload-publish-failed");
            }

            log?.Invoke($"Published {plan.Tag}.");
        }

        return new Result(uploaded, [], totalBytes, elapsed);
    }

    private static async Task<bool> UploadOneAsync(
        IProcessRunner runner,
        Plan plan,
        string file,
        Action<string>? log,
        Func<int, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= plan.MaxAttempts; attempt++)
        {
            log?.Invoke($"[{(Path.GetFileName(file))}] attempt {attempt} of {plan.MaxAttempts}");
            ProcessResult r;
            try
            {
                r = await runner.RunAsync("gh", ReleaseUploadArgs(plan, file), timeout: TimeSpan.FromMinutes(30), cancellationToken: ct);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[{Path.GetFileName(file)}] attempt {attempt} error: {ex.Message}");
                r = new ProcessResult(1, string.Empty, ex.Message, TimeSpan.Zero);
            }

            if (r.ExitCode == 0)
            {
                return true;
            }

            log?.Invoke($"[{Path.GetFileName(file)}] attempt {attempt} failed with exit {r.ExitCode}.");
            if (attempt < plan.MaxAttempts)
            {
                await delay(15_000 * attempt, ct);
            }
        }

        return false;
    }

    private static string Tail(ProcessResult r)
    {
        string tail = (r.StdErr + "\n" + r.StdOut).Trim();
        return tail.Length > 1000 ? tail[^1000..] : tail;
    }
}
