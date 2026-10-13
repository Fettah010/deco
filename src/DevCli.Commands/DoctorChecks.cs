namespace DevCli.Commands;

/// <summary>
/// Release-readiness checks for `deco doctor`. Every external probe goes
/// through <c>IProcessRunner</c> and every environment read through an
/// injectable lookup, so the whole matrix is unit-testable with zero
/// process-global mutation. Statuses: pass | warn | fail | skip.
/// Warnings and skips never fail the command; anything fail does.
/// </summary>
public static class DoctorChecks
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Skip = "skip";

    public sealed record Check(string Id, string Status, string Detail, string Fix);

    public static List<Check> RunAll(
        Core.ProjectContext ctx,
        Infrastructure.IProcessRunner runner,
        string vmFlag,
        string userFlag,
        bool full,
        Func<string, string?>? envLookup = null,
        Func<string[]>? pathDirs = null,
        Func<Check>? loginProbe = null)
    {
        envLookup ??= Environment.GetEnvironmentVariable;
        pathDirs ??= () => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var checks = new List<Check>
        {
            DotnetSdk(runner),
            DotnetRuntime(runner),
            MsstoreCli(runner, pathDirs()),
            EntraEnv(envLookup),
            Manifest(ctx),
            CaptureVm(ctx, vmFlag, userFlag),
            PortalProfile(ctx),
        };
        Check hyper = HyperV(runner);
        checks.Add(hyper);
        checks.Add(Sandbox(runner));
        checks.Add(VmExists(ctx, runner, vmFlag, hyper.Status == Pass));
        if (full)
        {
            checks.Add(loginProbe is null ? PortalLogin(ctx) : loginProbe());
        }

        return checks;
    }

    public static Check DotnetSdk(Infrastructure.IProcessRunner runner)
    {
        Infrastructure.ProcessResult r;
        try
        {
            r = runner.RunAsync("dotnet", ["--list-sdks"], timeout: TimeSpan.FromSeconds(60))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new Check("dotnet-sdk", Fail, "dotnet not runnable: " + OneLine(ex.Message),
                "Install the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10).");
        }

        string hit = r.StdOut.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("10.", StringComparison.Ordinal)) ?? string.Empty;
        return r.ExitCode == 0 && hit.Length > 0
            ? new Check("dotnet-sdk", Pass, "SDK " + hit, string.Empty)
            : new Check("dotnet-sdk", Fail, "No .NET 10 SDK in `dotnet --list-sdks`.",
                "Install the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10).");
    }

    public static Check DotnetRuntime(Infrastructure.IProcessRunner runner)
    {
        Infrastructure.ProcessResult r;
        try
        {
            r = runner.RunAsync("dotnet", ["--list-runtimes"], timeout: TimeSpan.FromSeconds(60))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new Check("dotnet-runtime", Fail, "dotnet not runnable: " + OneLine(ex.Message),
                "Install the .NET 10 runtime (winget install Microsoft.DotNet.Runtime.10).");
        }

        string hit = r.StdOut.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("Microsoft.NETCore.App 10.", StringComparison.Ordinal)) ?? string.Empty;
        return r.ExitCode == 0 && hit.Length > 0
            ? new Check("dotnet-runtime", Pass, hit, string.Empty)
            : new Check("dotnet-runtime", Fail, "No .NET 10 runtime in `dotnet --list-runtimes`.",
                "Install the .NET 10 runtime (winget install Microsoft.DotNet.Runtime.10).");
    }

    public static Check MsstoreCli(Infrastructure.IProcessRunner runner, string[] pathDirs)
    {
        string? cli = null;
        try
        {
            cli = MsstoreConfig.FindCli(runner, pathDirs);
        }
        catch
        {
        }

        return cli is null
            ? new Check("msstore-cli", Warn, "MSStore CLI not found on PATH.",
                "winget install \"Microsoft Store Developer CLI\". Needed for API flows; portal flows work without it.")
            : new Check("msstore-cli", Pass, cli, string.Empty);
    }

    public static Check EntraEnv(Func<string, string?> lookup)
    {
        var c = MsstoreConfig.Resolve(string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, lookup);
        var missing = MsstoreConfig.MissingIdentity(c);
        if (missing.Count == 0)
        {
            return new Check("entra-env", Pass, "tenant + client + secret resolvable (names only, never values).", string.Empty);
        }

        return new Check("entra-env", Warn,
            "Missing " + string.Join(", ", missing) + ".",
            "Set DECO_STORE_TENANT_ID / DECO_STORE_SELLER_ID / DECO_STORE_CLIENT_ID / DECO_STORE_CLIENT_SECRET. Needed for API flows; portal flows work without them.");
    }

    public static Check Manifest(Core.ProjectContext ctx)
    {
        if (!ctx.IsManifestFound)
        {
            return new Check("manifest", Warn, $"No .devcli.json (appId '{ctx.AppId}' guessed).",
                "Run `deco init` to pin appId, locales, capture lab and paths.");
        }

        string vm = string.IsNullOrWhiteSpace(ctx.VmName) ? "unpinned" : ctx.VmName;
        return new Check("manifest", Pass,
            $"appId {ctx.AppId}, locales [{string.Join(", ", ctx.Locales)}], vm {vm}.", string.Empty);
    }

    public static Check CaptureVm(Core.ProjectContext ctx, string vmFlag, string userFlag)
    {
        string vm = VmConfig.ResolveName(vmFlag, ctx.VmName);
        string user = VmConfig.ResolveUser(userFlag, ctx.VmUser);
        if (VmConfig.UsedLegacyFallback(vmFlag, ctx.VmName))
        {
            return new Check("capture-vm", Warn, $"Using legacy defaults ({user}@{vm}).",
                "Set capture.vmName/vmUser in .devcli.json or pass --vm/--user.");
        }

        return new Check("capture-vm", Pass, $"{user}@{vm}.", string.Empty);
    }

    public static Check PortalProfile(Core.ProjectContext ctx)
    {
        string dir = Infrastructure.PortalSession.DefaultProfileDir(ctx.AppId);
        bool ready = false;
        try
        {
            ready = Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length > 0;
        }
        catch
        {
        }

        return ready
            ? new Check("portal-profile", Pass, dir, string.Empty)
            : new Check("portal-profile", Warn, "No portal login session.",
                "Run `deco store portal-auth` once (headed sign-in, session is reused).");
    }

    public static Check HyperV(Infrastructure.IProcessRunner runner)
    {
        const string fix = "Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All (elevated). Only needed for the vm backend.";
        Check dism = WindowsFeature(runner, "hyperv", "Microsoft-Hyper-V-All", fix);
        if (dism.Status == Pass || !dism.Detail.Contains("needs elevation", StringComparison.OrdinalIgnoreCase))
        {
            return dism;
        }

        // Non-elevated fallback: a running hypervisor proves the platform.
        try
        {
            Infrastructure.ProcessResult r = runner.RunAsync("sc.exe", ["query", "vmcompute"],
                timeout: TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            if ((r.StdOut + "\n" + r.StdErr).Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                return new Check("hyperv", Pass, "hypervisor active (feature state unverified without elevation).", string.Empty);
            }
        }
        catch
        {
        }

        return dism;
    }

    public static Check Sandbox(Infrastructure.IProcessRunner runner, Func<string, bool>? fileExists = null)
    {
        const string fix = "Enable the Windows Sandbox feature (Pro/Enterprise/Education) if you want the sandbox backend.";
        Check dism = WindowsFeature(runner, "sandbox", "Containers-DisposableClientVM", fix);
        if (dism.Status == Pass || !dism.Detail.Contains("needs elevation", StringComparison.OrdinalIgnoreCase))
        {
            return dism;
        }

        // Non-elevated fallback: the installed launcher proves the feature.
        fileExists ??= File.Exists;
        try
        {
            if (fileExists(Infrastructure.SandboxRunner.SandboxExe))
            {
                return new Check("sandbox", Pass, "installed (first boot activates; state unverified without elevation).", string.Empty);
            }
        }
        catch
        {
        }

        return dism;
    }

    public static Check WindowsFeature(Infrastructure.IProcessRunner runner, string id, string feature, string fix)
    {
        Infrastructure.ProcessResult r;
        try
        {
            r = runner.RunAsync("powershell.exe",
                ["-NoProfile", "-Command",
                    $"Get-WindowsOptionalFeature -Online -FeatureName {feature} | Select-Object -ExpandProperty State"],
                timeout: TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new Check(id, Warn, "Probe failed: " + OneLine(ex.Message), fix);
        }

        string state = (r.StdOut + "\n" + r.StdErr).Trim();
        if (state.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            return new Check(id, Pass, $"{feature} Enabled.", string.Empty);
        }

        // DISM queries need elevation; without it the blob is useless — say so.
        if (state.Contains("requires elevation", StringComparison.OrdinalIgnoreCase))
        {
            return new Check(id, Warn, $"{feature} state: query needs elevation.",
                "Re-run `deco doctor` elevated for a definitive answer. " + fix);
        }

        return new Check(id, Warn, $"{feature} state: {OneLine(state)}.", fix);
    }

    public static Check VmExists(
        Core.ProjectContext ctx, Infrastructure.IProcessRunner runner, string vmFlag, bool hyperVOn)
    {
        if (!hyperVOn)
        {
            return new Check("vm-exists", Skip, "Hyper-V off.", string.Empty);
        }

        string vm = VmConfig.ResolveName(vmFlag, ctx.VmName);
        string esc = vm.Replace("'", "''", StringComparison.Ordinal);
        Infrastructure.ProcessResult r;
        try
        {
            r = runner.RunAsync("powershell.exe",
                ["-NoProfile", "-Command",
                    $"Get-VM -Name '{esc}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name"],
                timeout: TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new Check("vm-exists", Warn, "Probe failed: " + OneLine(ex.Message),
                "Run elevated once to confirm visibility.");
        }

        return string.Equals(r.StdOut.Trim(), vm, StringComparison.OrdinalIgnoreCase)
            ? new Check("vm-exists", Pass, vm, string.Empty)
            : new Check("vm-exists", Warn, $"VM '{vm}' not found.",
                "Build it (`deco vm new --dry-run`, elevated) or pin another name.");
    }

    public static Check PortalLogin(Core.ProjectContext ctx)
    {
        try
        {
            var result = Infrastructure.PortalSession.EnsureLoggedInAsync(
                Infrastructure.PortalSession.DefaultProfileDir(ctx.AppId),
                headed: false, timeoutSec: 60, log: null).GetAwaiter().GetResult();
            return result.LoggedIn
                ? new Check("portal-login", Pass, $"engine {result.Engine}.", string.Empty)
                : new Check("portal-login", Warn, "Headless session is not logged in.",
                    "Run `deco store portal-auth` once (headed sign-in, session is reused).");
        }
        catch (Exception ex)
        {
            return new Check("portal-login", Warn, "Check failed: " + OneLine(ex.Message),
                "Run `deco store portal-auth` once (headed sign-in, session is reused).");
        }
    }

    internal static string OneLine(string s)
    {
        string line = (s ?? string.Empty).Split('\n')[0].Trim();
        return line.Length <= 160 ? line : line[..160];
    }
}
