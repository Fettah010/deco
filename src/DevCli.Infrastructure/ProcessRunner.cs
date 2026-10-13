using System.Diagnostics;

namespace DevCli.Infrastructure;

/// <summary>Outcome of one child process. StdOut/StdErr are captured fully (no live streaming in Phase 2a).</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration);

/// <summary>
/// Argument-list-only process execution (never shell strings — paths like
/// "VS studio projects" stay safe). All external tools (dotnet, vpk, gh,
/// makeappx, git) go through this seam so handlers stay unit-testable.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? env = null);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (env is not null)
        {
            foreach ((string k, string v) in env)
            {
                psi.Environment[k] = v;
            }
        }

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
            {
                throw new Core.DecoException($"Could not start '{exe}'.", "process-start-failed");
            }
        }
        catch (Core.DecoException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Core.DecoException($"Could not start '{exe}': {ex.Message}", "process-start-failed");
        }

        Task<string> outTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue)
        {
            cts.CancelAfter(timeout.Value);
        }

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort; the timeout error below is what matters.
            }

            throw new Core.DecoException(
                $"'{exe}' timed out after {timeout?.TotalSeconds}s.",
                "process-timeout");
        }

        string stdout = await outTask;
        string stderr = await errTask;
        sw.Stop();
        return new ProcessResult(proc.ExitCode, stdout, stderr, sw.Elapsed);
    }
}
