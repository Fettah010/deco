// Command handlers: sandbox. Shared helpers, types, Main, and the command tree stay in Program.cs.
using DevCli.Commands;
using DevCli.Core;
using DevCli.Infrastructure;

static partial class Program
{
    private static int RunSandboxProvision(string? project, string verbosity, string format, bool noAnsi,
        string flaUIVersion, string depsDir, bool force, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(flaUIVersion))
        {
            flaUIVersion = SandboxProvision.FlaUIVersion;
        }

        if (string.IsNullOrWhiteSpace(depsDir))
        {
            depsDir = SandboxProvision.DepsDir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), flaUIVersion);
        }

        if (dryRun)
        {
            Console.WriteLine("DRYRUN plan (nothing downloaded):");
            foreach (var p in SandboxProvision.Packages(flaUIVersion))
            {
                Console.WriteLine($"  {p.FileName} <- {SandboxProvision.NupkgUrl(p)}");
            }

            Console.WriteLine($"  layout: {depsDir}");
            return ExitCodes.Success;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            SandboxProvision.DepsManifest m = SandboxProvision.ProvisionAsync(
                depsDir, flaUIVersion, http, force, Console.WriteLine).GetAwaiter().GetResult();
            Console.WriteLine($"Guest deps ready: {depsDir} ({m.Files.Count} files, FlaUI {m.FlaUIVersion}).");
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "sandbox provision",
                    depsDir,
                    files = m.Files.Select(f => new { name = f.Name, sha256 = f.Sha256, bytes = f.Bytes }).ToList(),
                });
            }

            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    private static int RunSandboxCapture(string? project, string verbosity, string format, bool noAnsi,
        string exe, string msix, string plan, string version, int memoryMb, bool noNetwork,
        int timeoutMin, bool preflightOnly, bool selfTest, bool dryRun, string depsDir,
        bool detach, string runId, bool foreground, bool vgpu)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        if (timeoutMin <= 0)
        {
            timeoutMin = 15;
        }

        if (memoryMb <= 0)
        {
            memoryMb = 4096;
        }

        if (selfTest && (!string.IsNullOrWhiteSpace(exe) || !string.IsNullOrWhiteSpace(msix)))
        {
            Console.Error.WriteLine("Error: --self-test takes no --exe/--msix (it drives notepad).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (!selfTest && string.IsNullOrWhiteSpace(exe) == string.IsNullOrWhiteSpace(msix))
        {
            Console.Error.WriteLine("Error: Pass exactly one of --exe (unpackaged) or --msix (package).");
            return ExitCodes.UsageOrAuthNeeded;
        }

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

        if (!string.IsNullOrWhiteSpace(exe) && !File.Exists(exe))
        {
            Console.Error.WriteLine($"Error: --exe not found: {exe}");
            return ExitCodes.Error;
        }

        if (!string.IsNullOrWhiteSpace(msix) && !File.Exists(msix))
        {
            Console.Error.WriteLine($"Error: --msix not found: {msix}");
            return ExitCodes.Error;
        }

        StoreShots.ShotPlan? shotPlan = null;
        string shotPlanSha = string.Empty;
        if (!string.IsNullOrWhiteSpace(plan))
        {
            if (!File.Exists(plan))
            {
                Console.Error.WriteLine($"Error: --plan not found: {plan}");
                return ExitCodes.Error;
            }

            try
            {
                shotPlan = StoreShots.LoadPlan(plan, ctx.Locales);
                shotPlanSha = StoreJson.Sha256File(plan);
            }
            catch (DecoException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ex.ExitCode;
            }
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            version = ctx.InformationalVersion ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            Console.Error.WriteLine("Error: Cannot resolve version: pass --version or keep InformationalVersion in the csproj.");
            return ExitCodes.Error;
        }

        if (string.IsNullOrWhiteSpace(depsDir))
        {
            depsDir = SandboxProvision.DepsDir(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                SandboxProvision.FlaUIVersion);
        }

        if (!SandboxProvision.IsProvisioned(depsDir))
        {
            Console.Error.WriteLine($"Error: Guest deps missing at {depsDir}. Run `deco sandbox provision` first.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (dryRun)
        {
            Console.WriteLine("DRYRUN plan (no staging, no launch):");
            Console.WriteLine($"  target: {(selfTest ? "self-test (notepad, host-run)" : (!string.IsNullOrWhiteSpace(exe) ? exe : msix))}");
            Console.WriteLine($"  plan: {(shotPlan is null ? "none (single main-window shot)" : $"{plan} ({shotPlan.Shots.Count} shots, {shotPlan.Shots.Sum(s => s.Steps.Count)} steps)")}");
            Console.WriteLine($"  output: {Path.Combine(ctx.StoreDir, version, "screenshots")}");
            Console.WriteLine($"  sandbox: {memoryMb} MB, network {(noNetwork ? "off" : "on")}, timeout {timeoutMin} min");
            return ExitCodes.Success;
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            runId = SandboxRuns.NewRunId();
        }

        if (detach)
        {
            var (fileName, preArgs) = DevCli.Commands.SelfLaunch.LaunchPrefix();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                Console.Error.WriteLine("Error: Cannot determine own executable for detached launch.");
                return ExitCodes.Error;
            }

            var childArgs = SandboxRuns.ChildArgs(ctx.RepoRoot, verbosity, format,
                exe, msix, plan, version, memoryMb, noNetwork, timeoutMin, depsDir, runId,
                vgpu, foreground);
            var psi = new System.Diagnostics.ProcessStartInfo(fileName)
            {
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            foreach (string a in preArgs)
            {
                psi.ArgumentList.Add(a);
            }

            foreach (string a in childArgs)
            {
                psi.ArgumentList.Add(a);
            }

            try
            {
                System.Diagnostics.Process? child = System.Diagnostics.Process.Start(psi);
                if (child is null)
                {
                    Console.Error.WriteLine("Error: Detached launch did not start.");
                    return ExitCodes.Error;
                }

                SandboxRuns.WriteOwner(SandboxRuns.StageDir(Path.GetTempPath(), runId), child.Id);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: Detached launch failed: {ex.Message.Split('\n')[0]}");
                return ExitCodes.Error;
            }

            Console.WriteLine($"Detached run {runId} (survives closed windows; only closing the Sandbox kills it).");
            Console.WriteLine($"  watch: deco sandbox status --run {runId}");
            Console.WriteLine($"  progress: {Path.Combine(SandboxRuns.StageDir(Path.GetTempPath(), runId), "out", "progress.log")}");
            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "sandbox capture",
                    detached = true,
                    runId,
                });
            }

            return ExitCodes.Success;
        }

        if (selfTest)
        {
            return RunSandboxSelfTest(depsDir, timeoutMin, format);
        }

        // --- stage ---
        string stage = SandboxRuns.StageDir(Path.GetTempPath(), runId);
        SandboxRuns.HostLog(stage, $"capture start (target={(selfTest ? "selftest" : (!string.IsNullOrWhiteSpace(exe) ? exe : msix))}, timeout={timeoutMin}m)");
        string flauiStage = Path.Combine(stage, "flaui");
        string outStage = Path.Combine(stage, "out");
        try
        {
            Directory.CreateDirectory(flauiStage);
            Directory.CreateDirectory(outStage);
            foreach (string dll in Directory.GetFiles(depsDir, "*.dll"))
            {
                File.Copy(dll, Path.Combine(flauiStage, Path.GetFileName(dll)), overwrite: true);
            }

            File.WriteAllText(Path.Combine(stage, "capture.ps1"), SandboxGuest.Bootstrap, System.Text.Encoding.UTF8);
            string? guestPlan = null;
            if (!string.IsNullOrWhiteSpace(plan))
            {
                guestPlan = Path.Combine(stage, "plan.json");
                File.Copy(plan, guestPlan, overwrite: true);
            }

            string appHostDir = Path.GetDirectoryName(Path.GetFullPath(!string.IsNullOrWhiteSpace(exe) ? exe : msix))!;
            if (!Directory.Exists(appHostDir))
            {
                Console.Error.WriteLine($"Error: App dir missing: {appHostDir}");
                return ExitCodes.Error;
            }

            var maps = new List<SandboxWsb.Mapping>
            {
                new(stage, "C:\\deco-stage", false),
                new(appHostDir, "C:\\deco-app", true),
            };
            string guestApp = "C:\\deco-app\\" + Path.GetFileName(!string.IsNullOrWhiteSpace(exe) ? exe : msix);
            string logon = "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\deco-stage\\capture.ps1\""
                + " -StageDir \"C:\\deco-stage\" -Mode run"
                + (guestPlan is null ? string.Empty : " -PlanPath \"C:\\deco-stage\\plan.json\"")
                + (!string.IsNullOrWhiteSpace(exe) ? $" -AppExe \"{guestApp}\"" : $" -MsixPath \"{guestApp}\"")
                + $" -TimeoutSec {timeoutMin * 60}";
            string wsb = SandboxWsb.Build(memoryMb, !noNetwork, maps, logon, vgpu);
            string wsbPath = Path.Combine(stage, "run.wsb");
            File.WriteAllText(wsbPath, wsb, System.Text.Encoding.UTF8);

            if (preflightOnly)
            {
                Console.WriteLine("PREFLIGHT OK (no launch):");
                Console.WriteLine($"  stage: {stage}");
                Console.WriteLine($"  mappings: {maps.Count} (stage rw, app ro)");
                Console.WriteLine($"  guest target: {guestApp}");
                Console.WriteLine($"  wsb: {wsbPath}");
                return ExitCodes.Success;
            }

            IProcessRunner runner = new ProcessRunner();
            List<int> beforePids = new();
            bool? slot = false;
            try
            {
                slot = SandboxRunner.IsRunningAsync(runner).GetAwaiter().GetResult();
                beforePids = SandboxRunner.ListSandboxPidsAsync(runner).GetAwaiter().GetResult();
            }
            catch
            {
                slot = null;
            }

            if (slot == true)
            {
                Console.Error.WriteLine("Error: Another sandbox is already running (only one per session).");
                Console.Error.WriteLine("Fix: close it, wait for it to finish, or run `deco sandbox kill` (closes sandbox windows).");
                SandboxRuns.HostLog(stage, "guard: slot occupied, aborting");
                return ExitCodes.Error;
            }

            if (slot is null)
            {
                Console.WriteLine("(slot probe inconclusive — continuing; an overlapping run would fail.)");
            }

            if (!File.Exists(SandboxRunner.SandboxExe))
            {
                Console.Error.WriteLine($"Error: Windows Sandbox is not installed ({SandboxRunner.SandboxExe} missing).");
                Console.Error.WriteLine("Fix: enable the Windows Sandbox feature (Pro/Enterprise/Education, elevated, then reboot).");
                return ExitCodes.UsageOrAuthNeeded;
            }

            Console.WriteLine($"Launching sandbox ({memoryMb} MB, {Path.GetFileName(wsbPath)})...");
            try
            {
                ProcessResult launched =
                    SandboxRunner.LaunchAsync(runner, wsbPath).GetAwaiter().GetResult();
                if (launched.ExitCode != 0)
                {
                    string tail = (launched.StdOut + "\n" + launched.StdErr).Trim();
                    Console.Error.WriteLine($"Error: Sandbox launch failed (exit {launched.ExitCode}): {tail[^Math.Min(500, tail.Length)..]}");
                    SandboxRuns.HostLog(stage, $"launch failed (exit {launched.ExitCode})");
                    return ExitCodes.Error;
                }

                SandboxRuns.HostLog(stage, "launched");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: Sandbox launch failed: {ex.Message}");
                SandboxRuns.HostLog(stage, "launch threw: " + ex.Message.Split('\n')[0]);
                return ExitCodes.Error;
            }

            // Ownership record: teardown kills ONLY these PIDs (re-verified
            // at kill time), never anything newer — a stale run can no
            // longer murder a fresh session.
            List<int> sessionPids = SandboxRunner.WaitForSessionAsync(
                runner, TimeSpan.FromSeconds(90), Console.WriteLine).GetAwaiter().GetResult();
            if (!foreground)
            {
                List<int> fresh = SandboxDiagnostics.NewPids(beforePids, sessionPids);
                if (fresh.Count > 0 && OperatingSystem.IsWindows())
                {
                    Console.WriteLine("Minimizing the sandbox window (pass --foreground to keep it up)...");
                    SandboxNative.MinimizeWindows(fresh, Console.WriteLine);
                }
            }
            try
            {
                File.WriteAllText(Path.Combine(stage, "session.json"),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        pids = sessionPids,
                        launchedUtc = DateTime.UtcNow.ToString("o"),
                    }));
                SandboxRuns.HostLog(stage, $"session recorded: [{string.Join(", ", sessionPids)}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(session record hiccup: {ex.Message.Split('\n')[0]})");
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    SandboxNative.StayAwake();
                }

                SandboxRunner.WaitResult wait = SandboxRunner.WaitSentinelAsync(
                    outStage, TimeSpan.FromMinutes(timeoutMin)).GetAwaiter().GetResult();
                if (!wait.Done)
                {
                    Console.Error.WriteLine($"Error: {wait.Note}");
                    Console.Error.WriteLine($"Stage kept for forensics: {stage}");
                    SandboxRuns.HostLog(stage, "wait failed: " + wait.Note);
                    return ExitCodes.Error;
                }

                string shotsLocal = Path.Combine(ctx.StoreDir, version, "screenshots");
                Directory.CreateDirectory(shotsLocal);
                foreach (string shot in Directory.GetFiles(Path.Combine(stage, "shots"), "*.png"))
                {
                    File.Copy(shot, Path.Combine(shotsLocal, Path.GetFileName(shot)), overwrite: true);
                }

                File.Copy(Path.Combine(outStage, "manifest.json"),
                    Path.Combine(shotsLocal, "sandbox-run.json"), overwrite: true);
                int count = Directory.GetFiles(shotsLocal, "*.png").Length;
                Console.WriteLine($"Captured {count} screenshot(s) -> {shotsLocal}");
                SandboxRuns.HostLog(stage, $"success: {count} shots -> {shotsLocal}");
                try
                {
                    Directory.Delete(stage, recursive: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"(stage cleanup hiccup, left at {stage}: {ex.Message.Split('\n')[0]})");
                }
                if (format == "json")
                {
                    EmitJson(new
                    {
                        tool = CliConstants.ToolName,
                        version = CliConstants.Version,
                        command = "sandbox capture",
                        versionDir = Path.Combine(ctx.StoreDir, version),
                        shots = count,
                        backend = "sandbox",
                    });
                }

                return ExitCodes.Success;
            }
            finally
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        SandboxNative.AllowSleep();
                    }
                }
                catch
                {
                }

                try
                {
                    SandboxRunner.KillPidsAsync(runner, sessionPids, Console.WriteLine).GetAwaiter().GetResult();
                }
                catch
                {
                }
            }
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            try
            {
                if (Directory.Exists(stage))
                {
                    SandboxRuns.HostLog(stage, "error: " + ex.Message.Split('\n')[0]);
                }
            }
            catch
            {
            }

            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            try
            {
                if (Directory.Exists(stage))
                {
                    SandboxRuns.HostLog(stage, "error: " + ex.Message.Split('\n')[0]);
                }
            }
            catch
            {
            }

            return ExitCodes.Error;
        }
    }

    private static int RunSandboxKill(string verbosity, string format, bool noAnsi)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        IProcessRunner runner = new ProcessRunner();
        SandboxRunner.KillAsync(runner, Console.WriteLine).GetAwaiter().GetResult();
        Console.WriteLine("Sandbox teardown signalled (no-op if none was running).");
        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "sandbox kill",
            });
        }

        return ExitCodes.Success;
    }

    private static int RunSandboxStatus(string verbosity, string format, bool noAnsi, string run)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        format = format.Trim().ToLowerInvariant();
        string? stage = SandboxRuns.FindRun(Path.GetTempPath(), run);
        if (stage is null)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(run)
                ? "Error: No sandbox runs found yet."
                : $"Error: No sandbox run '{run.Trim()}'.");
            return ExitCodes.Error;
        }

        SandboxRuns.RunStatus st = SandboxRuns.ReadStatus(stage);
        IProcessRunner diagRunner = new ProcessRunner();
        List<SandboxDiagnostics.SessionVital> vitals = new();
        List<SandboxDiagnostics.VmEvent> events = new();
        try
        {
            vitals = SandboxDiagnostics.SessionVitalsAsync(diagRunner).GetAwaiter().GetResult();
            DateTimeOffset since;
            try
            {
                since = Directory.GetCreationTimeUtc(stage);
            }
            catch
            {
                since = DateTimeOffset.UtcNow.AddHours(-1);
            }

            events = SandboxDiagnostics.RecentVmEventsAsync(diagRunner, since).GetAwaiter().GetResult();
        }
        catch
        {
        }

        if (format == "json")
        {
            EmitJson(new
            {
                tool = CliConstants.ToolName,
                version = CliConstants.Version,
                command = "sandbox status",
                runId = st.RunId,
                state = st.State,
                detail = st.Detail,
                shots = st.Shots,
                sessionPids = st.SessionPids,
                progress = st.ProgressTail,
            });
        }
        else
        {
            Console.WriteLine($"run {st.RunId}: {st.State.ToUpperInvariant()} — {st.Detail}");
            Console.WriteLine($"  shots: {st.Shots}, stage: {st.StageDir}");
            if (st.SessionPids.Count > 0)
            {
                Console.WriteLine($"  session pids: {string.Join(", ", st.SessionPids)}");
            }

            Console.WriteLine(vitals.Count == 0
                ? "  sessions alive now: none"
                : $"  sessions alive now: {string.Join("; ", vitals.Select(v => $"{v.Image} pid={v.Pid} cpu={v.CpuSeconds:F0}s"))}");
            if (events.Count == 0)
            {
                Console.WriteLine("  vm events since stage birth: none");
            }
            else
            {
                foreach (var e in events)
                {
                    Console.WriteLine($"  vm event {e.At:HH:mm:ss} id={e.Id}{(e.Note.Length == 0 ? string.Empty : " " + e.Note)}");
                }
            }

            foreach (string line in st.ProgressTail)
            {
                Console.WriteLine($"  | {line}");
            }

            List<string> host = SandboxRuns.HostTail(st.StageDir, 6);
            if (host.Count > 0)
            {
                Console.WriteLine("  host log:");
                foreach (string line in host)
                {
                    Console.WriteLine($"  H| {line}");
                }
            }
        }

        return ExitCodes.Success;
    }

    private static int RunSandboxSelfTest(string depsDir, int timeoutMin, string format)
    {
        string stage = Path.Combine(Path.GetTempPath(), $"deco-sandbox-selftest-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        string flauiStage = Path.Combine(stage, "flaui");
        string outStage = Path.Combine(stage, "out");
        try
        {
            Directory.CreateDirectory(flauiStage);
            Directory.CreateDirectory(outStage);
            foreach (string dll in Directory.GetFiles(depsDir, "*.dll"))
            {
                File.Copy(dll, Path.Combine(flauiStage, Path.GetFileName(dll)), overwrite: true);
            }

            string script = Path.Combine(stage, "capture.ps1");
            File.WriteAllText(script, SandboxGuest.Bootstrap, System.Text.Encoding.UTF8);

            IProcessRunner runner = new ProcessRunner();
            Console.WriteLine("Sandbox guest self-test (host-run, drives notepad)...");
            ProcessResult r = runner.RunAsync("powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                    "-StageDir", stage, "-Mode", "selftest"],
                timeout: TimeSpan.FromMinutes(Math.Max(timeoutMin, 5))).GetAwaiter().GetResult();
            string tail = (r.StdOut + "\n" + r.StdErr).Trim();
            bool done = File.Exists(Path.Combine(outStage, "done"));
            int shots = 0;
            try
            {
                shots = Directory.GetFiles(Path.Combine(stage, "shots"), "*.png").Length;
            }
            catch
            {
            }

            Console.WriteLine(done && shots > 0 && r.ExitCode == 0
                ? $"SELF-TEST: green ({shots} shot(s))."
                : $"SELF-TEST: FAILED (exit {r.ExitCode}, done={done}, shots={shots}).");
            if (!done || shots == 0 || r.ExitCode != 0)
            {
                Console.WriteLine(tail[^Math.Min(1500, tail.Length)..]);
                return ExitCodes.Error;
            }

            if (format == "json")
            {
                EmitJson(new
                {
                    tool = CliConstants.ToolName,
                    version = CliConstants.Version,
                    command = "sandbox capture",
                    selfTest = true,
                    shots,
                });
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Error;
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch
            {
            }
        }
    }
}
