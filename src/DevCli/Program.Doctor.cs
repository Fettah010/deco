// Command handlers: doctor. Shared helpers, types, Main, and the command tree stay in Program.cs.
using DevCli.Commands;
using DevCli.Core;
using DevCli.Infrastructure;

static partial class Program
{
    private static int RunDoctor(string? project, string verbosity, string format, bool noAnsi,
        string vm, string user, bool full)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        IProcessRunner runner = new ProcessRunner();
        List<DoctorChecks.Check> checks = DoctorChecks.RunAll(ctx, runner, vm, user, full);

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "doctor",
                checks = checks.Select(c => new
                {
                    id = c.Id,
                    status = c.Status,
                    detail = c.Detail,
                    fix = c.Fix,
                }).ToList(),
            });
        }
        else
        {
            foreach (DoctorChecks.Check c in checks)
            {
                Console.WriteLine($"{c.Status.ToUpperInvariant(),-4} {c.Id} — {c.Detail}");
                if (!string.IsNullOrWhiteSpace(c.Fix))
                {
                    Console.WriteLine($"      fix: {c.Fix}");
                }
            }
        }

        int fails = checks.Count(c => c.Status == DoctorChecks.Fail);
        int warns = checks.Count(c => c.Status == DoctorChecks.Warn);
        Console.WriteLine(fails > 0
            ? $"DOCTOR: {fails} failing check(s), {warns} warning(s)."
            : warns > 0
                ? $"DOCTOR: clean with {warns} warning(s)."
                : "DOCTOR: all green.");
        return fails == 0 ? ExitCodes.Success : ExitCodes.Error;
    }
}
