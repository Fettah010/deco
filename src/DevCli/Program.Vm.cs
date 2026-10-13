// Command handlers: Vm. Shared helpers, types, Main, and the command tree stay in Program.cs.
using System.CommandLine;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using DevCli.Commands;
using DevCli.Core;
using DevCli.Infrastructure;
using Spectre.Console;

static partial class Program
{
    private static string ReadPasswordMasked(string prompt)
    {
        Console.Write($"{prompt}: ");
        var sb = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (k.Key == ConsoleKey.Backspace && sb.Length > 0)
            {
                sb.Length--;
            }
            else if (!char.IsControl(k.KeyChar))
            {
                sb.Append(k.KeyChar);
            }
        }

        Console.WriteLine();
        return sb.ToString();
    }

    private static int RelaunchSelfElevated(string[] decoArgs)
    {
        string self = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(self))
        {
            Console.Error.WriteLine("Error: Cannot determine own executable for elevation.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        static string Q(string s) => "\"" + s.Replace("\"", "", StringComparison.Ordinal) + "\"";
        string fileName = self;
        string arguments = string.Join(" ", decoArgs.Select(Q));
        if (self.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "dotnet";
            arguments = $"{Q(self)} {arguments}";
        }

        Console.WriteLine("Re-launching elevated (UAC prompt)...");
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    Verb = "runas",
                    UseShellExecute = true,
                },
            };
            proc.Start();
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Elevation was declined or failed: {ex.Message}");
            return ExitCodes.UsageOrAuthNeeded;
        }
    }

    private static int RequireAdminOrElevate(string[] decoArgs, bool elevate, string what)
    {
        if (IsCurrentUserAdmin())
        {
            return 0;
        }

        if (!elevate)
        {
            Console.Error.WriteLine($"Error: {what} requires elevation. Re-run with --elevate (one UAC click) or from an admin shell.");
            return ExitCodes.UsageOrAuthNeeded;
        }

        return RelaunchSelfElevated(decoArgs);
    }

    private static string ResolveVmPassword(ProjectContext ctx, string flag, string vm)
    {
        // Throws DecoException (callers print it once).
        return VmCredentialStore.ResolvePassword(flag, ctx.AppId, vm);
    }

    private static GuestProbeResult ProbeGuest(
        IProcessRunner runner, string vm, string user, string password, TimeSpan timeout)
    {
        var env = new Dictionary<string, string> { ["DECO_VM_USER"] = user };
        ProcessResult r = VmGuest.InvokeGuestAsync(runner, vm, user, password, GuestProbePs, timeout, env)
            .GetAwaiter().GetResult();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.StdOut.Trim());
            var root = doc.RootElement;
            int? sid = root.TryGetProperty("Sid", out var se) && se.ValueKind == System.Text.Json.JsonValueKind.Number
                ? se.GetInt32() : (int?)null;
            return new GuestProbeResult(
                Str(root, "Q"), Str(root, "Boot"), sid,
                Str(root, "SName"), Str(root, "State"),
                Bool(root, "Locked"), Bool(root, "Explorer"),
                root.TryGetProperty("FreeGB", out var fe) && fe.TryGetDouble(out double f) ? f : 0,
                Bool(root, "HasDotnet"));
        }
        catch (Exception ex)
        {
            throw new DecoException($"Guest probe returned unreadable data: {ex.Message}", "vm-probe");
        }

        static string Str(System.Text.Json.JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        static bool Bool(System.Text.Json.JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private static int RunVmCredential(string? project, string verbosity, string format, bool noAnsi,
        string vm, string user, string password, string storeDir, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
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

        vm = DevCli.Commands.VmConfig.ResolveName(vm, ctx.VmName);
        user = DevCli.Commands.VmConfig.ResolveUser(user, ctx.VmUser);
        string storePath = VmCredentialStore.StorePath(ctx.AppId, vm,
            string.IsNullOrWhiteSpace(storeDir) ? null : storeDir);
        if (dryRun)
        {
            Console.WriteLine($"[dry-run] would prompt for '{user}' on '{vm}', verify against the guest, store DPAPI at {storePath}.");
            return ExitCodes.Success;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            if (!Environment.UserInteractive)
            {
                Console.Error.WriteLine("Error: No --password and no interactive console for the secure prompt.");
                return ExitCodes.UsageOrAuthNeeded;
            }

            password = ReadPasswordMasked($"Password for '{user}' on '{vm}'");
        }

        try
        {
            IProcessRunner runner = new ProcessRunner();
            ProcessResult r = VmGuest.InvokeGuestAsync(runner, vm, user, password,
                "\"$env:COMPUTERNAME\\$env:USERNAME\"", TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
            Console.WriteLine($"Guest accepted the password ({r.StdOut.Trim()}).");
            VmCredentialStore.SavePassword(password, ctx.AppId, vm,
                string.IsNullOrWhiteSpace(storeDir) ? null : storeDir);
            Console.WriteLine("Stored. From now on run the vm commands with no password argument.");
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

    private static int RunVmResetPassword(string? project, string verbosity, string format, bool noAnsi,
        string vm, string user, string current, string next, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
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

        vm = DevCli.Commands.VmConfig.ResolveName(vm, ctx.VmName);
        user = DevCli.Commands.VmConfig.ResolveUser(user, ctx.VmUser);
        if (dryRun)
        {
            Console.WriteLine($"[dry-run] would rotate '{user}@{vm}': guest account + autologon + host store (new value never printed).");
            return ExitCodes.Success;
        }

        try
        {
            current = ResolveVmPassword(ctx, current, vm);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (string.IsNullOrWhiteSpace(next))
        {
            next = VmCredentialStore.RandomPassword();
        }

        try
        {
            string safeUser = user.Replace("'", "", StringComparison.Ordinal);
            string rotateScript =
                $"$u = '{safeUser}'\n"
                + "net user $u $env:DECO_VM_NEW | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'net user failed.' }\n"
                + VmGuestState.AutologonScript("$u", "DECO_VM_NEW");
            IProcessRunner runner = new ProcessRunner();
            var extra = new Dictionary<string, string> { ["DECO_VM_NEW"] = next };
            VmGuest.InvokeGuestAsync(runner, vm, user, current, rotateScript,
                TimeSpan.FromMinutes(5), extra).GetAwaiter().GetResult();
            ProcessResult r = VmGuest.InvokeGuestAsync(runner, vm, user, next,
                "\"$env:COMPUTERNAME\\$env:USERNAME\"", TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
            Console.WriteLine($"Verified ({r.StdOut.Trim()}).");
            string? ip = VmGuest.GetVmIpv4Async(runner, vm, TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            foreach (string t in new[] { vm, "CAPTURE", ip }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).Distinct())
            {
                try
                {
                    runner.RunAsync("cmdkey.exe", [$"/delete:TERMSRV/{t}"], timeout: TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                }
                catch
                {
                }
            }

            VmCredentialStore.SavePassword(next, ctx.AppId, vm);
            Console.WriteLine("Rotated: guest account + autologon + host store all agree. RDP re-saves creds on next connect.");
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

    private static int RunVmWaitUnlocked(string? project, string vm, string user, string password, int timeoutMin, bool noAnsi)
    {
        if (timeoutMin <= 0)
        {
            timeoutMin = 6;
        }

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
            vm = DevCli.Commands.VmConfig.ResolveName(vm, ctx.VmName);
            user = DevCli.Commands.VmConfig.ResolveUser(user, ctx.VmUser);
            password = ResolveVmPassword(ctx, password, vm);
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        IProcessRunner runner = new ProcessRunner();
        DateTimeOffset dead = DateTimeOffset.UtcNow.AddMinutes(timeoutMin);
        while (DateTimeOffset.UtcNow < dead)
        {
            Thread.Sleep(15000);
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            try
            {
                GuestProbeResult p = ProbeGuest(runner, vm, user, password, TimeSpan.FromMinutes(2));
                DevCli.Commands.CliConsole.Info($"{stamp} boot={p.Boot} session={p.SessionName} locked={p.Locked} explorer={p.Explorer}", noAnsi);
                if (p.Sid is not null && !p.Locked && p.Explorer)
                {
                    DevCli.Commands.CliConsole.Success("READY.", noAnsi);
                    return ExitCodes.Success;
                }
            }
            catch
            {
                DevCli.Commands.CliConsole.Info($"{stamp} (guest not up yet)", noAnsi);
            }
        }

        DevCli.Commands.CliConsole.Fail($"TIMEOUT - session still locked or {user} not logged on.", noAnsi);
        return ExitCodes.Error;
    }

    private static int RunVmResolution(string? project, string vm, string user, string password, int width, int height, bool dryRun)
    {
        if (width <= 0)
        {
            width = 1920;
        }

        if (height <= 0)
        {
            height = 1080;
        }

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
            vm = DevCli.Commands.VmConfig.ResolveName(vm, ctx.VmName);
            user = DevCli.Commands.VmConfig.ResolveUser(user, ctx.VmUser);
            if (!dryRun)
            {
                password = ResolveVmPassword(ctx, password, vm);
            }
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        string script =
            "Add-Type -TypeDefinition '\n"
            + "using System; using System.Runtime.InteropServices;\n"
            + "public static class DecoDisp { [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Ansi)]\n"
            + "public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;\n"
            + "public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;\n"
            + "public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput;\n"
            + "public short dmColor; public short dmDuplex; public short dmYResolution; public short dmCollate;\n"
            + "[MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;\n"
            + "public short dmLogPixels; public short dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;\n"
            + "public int dmDisplayFlags; public int dmDisplayFrequency; }\n"
            + "[DllImport(\"user32.dll\")] public static extern int ChangeDisplaySettings(ref DEVMODE d, int f);\n"
            + "[DllImport(\"user32.dll\")] public static extern bool EnumDisplaySettings(string n, int m, ref DEVMODE d); }'\n"
            + "$sz = [Runtime.InteropServices.Marshal]::SizeOf((New-Object DecoDisp+DEVMODE))\n"
            + "$dm = New-Object DecoDisp+DEVMODE; $dm.dmSize = $sz\n"
            + "[void][DecoDisp]::EnumDisplaySettings($null, -1, [ref]$dm)\n"
            + $"$dm.dmPelsWidth = {width}; $dm.dmPelsHeight = {height}; $dm.dmFields = 0x80000 -bor 0x100000\n"
            + "$rc = [DecoDisp]::ChangeDisplaySettings([ref]$dm, 0)\n"
            + "Start-Sleep -Seconds 2\n"
            + "$dm2 = New-Object DecoDisp+DEVMODE; $dm2.dmSize = $sz\n"
            + "[void][DecoDisp]::EnumDisplaySettings($null, -1, [ref]$dm2)\n"
            + "\"rc=$rc now=$($dm2.dmPelsWidth)x$($dm2.dmPelsHeight)\"\n"
            + "if ($dm2.dmPelsWidth -lt 1366 -or $dm2.dmPelsHeight -lt 768) { throw 'Resolution stuck (need >= 1366x768).' }";

        if (dryRun)
        {
            Console.WriteLine($"[dry-run] would set guest {vm} resolution to {width}x{height} (needs a console desktop; remote sessions reject mode changes).");
            return ExitCodes.Success;
        }

        try
        {
            IProcessRunner runner = new ProcessRunner();
            ProcessResult r = VmGuest.InvokeGuestAsync(runner, vm, user, password, script, TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
            Console.WriteLine(r.StdOut.Trim());
            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message} Maximize the VMConnect window once - the console follows it.");
            return ex.ExitCode;
        }
    }

    private static string PsQ(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static int RunVmNew(string? project, string password, string vm, string dir,
        int memoryGb, int cpu, int diskGb, bool dryRun, bool elevate)
    {
        if (memoryGb <= 0)
        {
            memoryGb = 8;
        }

        if (cpu <= 0)
        {
            cpu = 4;
        }

        if (diskGb <= 0)
        {
            diskGb = 80;
        }

        if (string.IsNullOrWhiteSpace(password) && !dryRun)
        {
            Console.Error.WriteLine("Error: --password is required (travels on your command line only, never written to the repo).");
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

        vm = DevCli.Commands.VmConfig.ResolveName(vm, ctx.VmName);
        int code = RequireAdminOrElevate(
            ["vm", "new", "--password", password, "--vm", vm, "--dir", dir,
                "--memory-gb", memoryGb.ToString(), "--cpu", cpu.ToString(), "--disk-gb", diskGb.ToString()],
            elevate, "VM creation");
        if (code != 0)
        {
            return code;
        }

        // Re-resolve after a possible elevation relaunch (the child re-runs fully).
        try
        {
            IProcessRunner runner = new ProcessRunner();
            ProcessResult iso = VmGuest.HostPsAsync(runner,
                $"Get-ChildItem -LiteralPath {PsQ(Path.Combine(dir, "iso"))} -Filter 'Win11_*.iso' -ErrorAction SilentlyContinue "
                + "| Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName",
                TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(iso.StdOut.Trim()))
            {
                Console.Error.WriteLine($"Error: No Win11 ISO in {dir}\\iso (fetch with Fido.ps1 first).");
                return ExitCodes.Error;
            }

            string isoPath = iso.StdOut.Trim();
            string answerTemplate = Path.Combine(ctx.RepoRoot, "Scripts", "vm", "autounattend.xml");
            if (!File.Exists(answerTemplate))
            {
                Console.Error.WriteLine($"Error: Answer template missing: {answerTemplate}");
                return ExitCodes.Error;
            }

            string template = File.ReadAllText(answerTemplate, Encoding.UTF8);
            if (!template.Contains("%%VM_PASSWORD%%"))
            {
                Console.Error.WriteLine("Error: Answer template lost its password tokens.");
                return ExitCodes.Error;
            }

            string vhdPath = Path.Combine(dir, "VHDs", vm + ".vhdx");
            string answerVhd = Path.Combine(dir, "answer.vhdx");
            if (dryRun)
            {
                Console.WriteLine($"[dry-run] would build '{vm}' (Gen2, {memoryGb}GB, {cpu} vCPU, {diskGb}GB dynamic VHD):");
                Console.WriteLine($"  ISO: {isoPath}");
                Console.WriteLine($"  answer disk: {answerVhd} (autounattend with password tokens)");
                Console.WriteLine($"  VHD: {vhdPath}, switch: Default Switch, boot: DVD then disk");
                return ExitCodes.Success;
            }

            if (File.Exists(vhdPath))
            {
                Console.Error.WriteLine($"Error: {vhdPath} exists (delete it or use another --vm).");
                return ExitCodes.Error;
            }

            var env = new Dictionary<string, string> { [VmGuest.PasswordEnv] = password };
            ProcessResult disk = VmGuest.HostPsAsync(runner,
                $"$answerVhdPath = {PsQ(answerVhd)}\n"
                + "if (Test-Path -LiteralPath $answerVhdPath) {\n"
                + "  try { Dismount-VHD -Path $answerVhdPath -ErrorAction SilentlyContinue } catch { }\n"
                + "  Remove-Item -LiteralPath $answerVhdPath -Force\n"
                + "  Write-Host 'Removed stale answer disk.' }\n"
                + "New-VHD -Path $answerVhdPath -SizeBytes 100MB -Dynamic | Out-Null\n"
                + "Mount-VHD -Path $answerVhdPath | Out-Null\n"
                + "$diskNo = (Get-Disk | Where-Object { $_.Location -like '*answer.vhdx*' } | Select-Object -First 1).Number\n"
                + "if ($null -eq $diskNo) { throw 'Answer VHD did not appear as a disk after mount.' }\n"
                + "Set-Disk -Number $diskNo -IsOffline $false -ErrorAction SilentlyContinue\n"
                + "Set-Disk -Number $diskNo -IsReadOnly $false -ErrorAction SilentlyContinue\n"
                + "$parts = @(Get-Partition -DiskNumber $diskNo -ErrorAction SilentlyContinue)\n"
                + "if ($parts.Count -eq 0) {\n"
                + "  Initialize-Disk -Number $diskNo -PartitionStyle GPT\n"
                + "  $used = @(Get-PSDrive -PSProvider FileSystem | Select-Object -ExpandProperty Name)\n"
                + "  $free = 71..90 | ForEach-Object { [char]$_ } | Where-Object { $used -notcontains [string]$_ } | Select-Object -First 1\n"
                + "  if ([string]::IsNullOrWhiteSpace($free)) { throw 'No free drive letter for the answer disk.' }\n"
                + "  New-Partition -DiskNumber $diskNo -UseMaximumSize -DriveLetter $free | Out-Null\n"
                + "  Start-Sleep -Seconds 3\n"
                + "  $parts = @(Get-Partition -DiskNumber $diskNo -ErrorAction SilentlyContinue) }\n"
                + "$target = $parts | Where-Object { $_.Type -eq 'Basic' } | Select-Object -First 1\n"
                + "if (-not $target) { $target = $parts | Sort-Object Size -Descending | Select-Object -First 1 }\n"
                + "if (-not $target) { throw 'No usable partition on the answer disk.' }\n"
                + "$target | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'Answer' -Confirm:$false | Out-Null\n"
                + "Start-Sleep -Seconds 3\n"
                + "$target = Get-Partition -DiskNumber $diskNo -PartitionNumber $target.PartitionNumber\n"
                + "$drive = $target.DriveLetter\n"
                + "if ([string]::IsNullOrWhiteSpace($drive)) { throw 'Answer volume got no drive letter.' }\n"
                + "$t = Get-Content -LiteralPath " + PsQ(answerTemplate) + " -Raw -Encoding UTF8\n"
                + "$t.Replace('%%VM_PASSWORD%%', $env:DECO_VM_PASSWORD) | Set-Content -LiteralPath (Join-Path ($drive + ':') 'autounattend.xml') -Encoding UTF8\n"
                + "Dismount-VHD -Path $answerVhdPath\n"
                + $"Write-Host 'Answer disk ready: {answerVhd}'",
                TimeSpan.FromMinutes(15), env).GetAwaiter().GetResult();
            Console.WriteLine(disk.StdOut.Trim());

            ProcessResult created = VmGuest.HostPsAsync(runner,
                $"$vhdPath = {PsQ(vhdPath)}\n"
                + $"$vm = New-VM -Name {PsQ(vm)} -MemoryStartupBytes ({memoryGb}GB) -Generation 2 "
                + $"-NewVHDPath $vhdPath -NewVHDSizeBytes ({diskGb}GB) -SwitchName 'Default Switch'\n"
                + $"Set-VM -VM $vm -ProcessorCount {cpu} -AutomaticStopAction ShutDown -CheckpointType Disabled\n"
                + $"Add-VMDvdDrive -VMName {PsQ(vm)} -ControllerNumber 0 -ControllerLocation 1 -Path {PsQ(isoPath)}\n"
                + $"Add-VMHardDiskDrive -VMName {PsQ(vm)} -Path {PsQ(answerVhd)}\n"
                + $"Set-VMFirmware -VM $vm -BootOrder (Get-VMDvdDrive -VMName {PsQ(vm)}), (Get-VMHardDiskDrive -VMName {PsQ(vm)} | Select-Object -First 1)\n"
                + $"Write-Host 'VM {vm} created.'",
                TimeSpan.FromMinutes(15)).GetAwaiter().GetResult();
            Console.WriteLine(created.StdOut.Trim());
            Console.WriteLine($"VM {vm} created. Start it: Start-VM {vm}, then watch via Virtual Machine Connection.");
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

    private static int RunVmProtect(string? project, string vm, string user, string password, bool dryRun)
    {
        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
            vm = DevCli.Commands.VmConfig.ResolveName(vm, ctx.VmName);
            user = DevCli.Commands.VmConfig.ResolveUser(user, ctx.VmUser);
            if (!dryRun)
            {
                password = ResolveVmPassword(ctx, password, vm);
            }
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        string safeUser = user.Replace("'", "", StringComparison.Ordinal);
        string script = $"$u = '{safeUser}'\n" + VmGuestState.ProtectScript()
            + "\n" + VmGuestState.AutologonScript("$u", VmGuest.PasswordEnv)
            + "\nWrite-Host 'Hardened $u (reboot to take effect).'";
        if (dryRun)
        {
            Console.WriteLine($"[dry-run] would harden '{user}@{vm}': WU reboot block, power/sleep off, screensaver+lock off, Europe/Paris timezone, unlimited autologon.");
            return ExitCodes.Success;
        }

        try
        {
            IProcessRunner runner = new ProcessRunner();
            ProcessResult r = VmGuest.InvokeGuestAsync(runner, vm, user, password, script, TimeSpan.FromMinutes(10)).GetAwaiter().GetResult();
            Console.WriteLine(r.StdOut.Trim());
            Console.WriteLine($"Hardened {vm}. Reboot to take effect (autologon signs {user} straight back in).");
            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
    }

    private static int RunVmProvision(string? project, string vm, string user, string password, string guestScript, bool dryRun)
    {
        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
            vm = DevCli.Commands.VmConfig.ResolveName(vm, ctx.VmName);
            user = DevCli.Commands.VmConfig.ResolveUser(user, ctx.VmUser);
            if (string.IsNullOrWhiteSpace(guestScript))
            {
                guestScript = Path.Combine(ctx.RepoRoot, "Scripts", "vm", "Install-GuestDeps.ps1");
            }

            if (!File.Exists(guestScript))
            {
                throw new DecoException($"Guest provisioning script not found: {guestScript}", "vm-script-missing");
            }

            if (!dryRun)
            {
                password = ResolveVmPassword(ctx, password, vm);
            }
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        if (dryRun)
        {
            Console.WriteLine($"[dry-run] would run guest provisioning inside '{vm}': {guestScript}");
            return ExitCodes.Success;
        }

        try
        {
            IProcessRunner runner = new ProcessRunner();
            ProcessResult r = VmGuest.InvokeGuestAsync(runner, vm, user, password,
                File.ReadAllText(guestScript, Encoding.UTF8), TimeSpan.FromMinutes(30)).GetAwaiter().GetResult();
            Console.WriteLine(r.StdOut.Trim());
            return ExitCodes.Success;
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }
    }

    private static int RunVmRdpTrust(string? project, bool dryRun, bool elevate)
    {
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

        string cn = $"CN={ctx.AppId}-VM-Automation";
        if (dryRun)
        {
            Console.WriteLine($"[dry-run] would (ELEVATED): ensure code-signing cert '{cn}' in CurrentUser\\My, "
                + "add to TrustedPublisher + Root, set HKLM trusted-.rdp-publisher policy, sign %TEMP%\\vmcapture-cap.rdp if present.");
            return ExitCodes.Success;
        }

        int admin = RequireAdminOrElevate(
            ["vm", "rdp-trust"], elevate, "RDP trust installation");
        if (admin != 0)
        {
            return admin;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Error: RDP trust requires Windows (cert stores + HKLM policy).");
            return ExitCodes.UsageOrAuthNeeded;
        }

        try
        {
            using System.Security.Cryptography.X509Certificates.X509Store my =
                new(System.Security.Cryptography.X509Certificates.StoreName.My,
                    System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            my.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
            System.Security.Cryptography.X509Certificates.X509Certificate2? cert = my.Certificates
                .FirstOrDefault(c => c.Subject == cn);
            if (cert is null)
            {
                using var rsa = System.Security.Cryptography.RSA.Create(2048);
                var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                    cn, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                    System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, false));
                req.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                    System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, false));
                req.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                    new System.Security.Cryptography.OidCollection
                    {
                        new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.3"),
                    }, false));
                using var created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
                var persisted = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                    created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx),
                    null,
                    System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.PersistKeySet
                    | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
                my.Add(persisted);
                cert = persisted;
                Console.WriteLine($"Created signing cert {cert.Thumbprint}.");
            }
            else
            {
                Console.WriteLine($"Signing cert present ({cert.Thumbprint}).");
            }

            string thumbprint = cert.Thumbprint;
            my.Close();

            using System.Security.Cryptography.X509Certificates.X509Store tp =
                new("TrustedPublisher", System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            tp.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
            try
            {
                if (!tp.Certificates.Any(c => c.Thumbprint == thumbprint))
                {
                    using System.Security.Cryptography.X509Certificates.X509Store lookup =
                        new(System.Security.Cryptography.X509Certificates.StoreName.My,
                            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
                    lookup.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                    var found = lookup.Certificates.FirstOrDefault(c => c.Thumbprint == thumbprint);
                    if (found is not null)
                    {
                        tp.Add(found);
                        Console.WriteLine("Added to TrustedPublisher.");
                    }

                    lookup.Close();
                }
                else
                {
                    Console.WriteLine("Already in TrustedPublisher.");
                }
            }
            finally
            {
                tp.Close();
            }

            using System.Security.Cryptography.X509Certificates.X509Store lookupMy =
                new(System.Security.Cryptography.X509Certificates.StoreName.My,
                    System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            lookupMy.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
            var signer = lookupMy.Certificates.FirstOrDefault(c => c.Thumbprint == thumbprint);
            if (signer is not null)
            {
                string tmpCer = Path.Combine(Path.GetTempPath(), "deco-vm-auto.cer");
                File.WriteAllBytes(tmpCer, signer.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
                IProcessRunner runner = new ProcessRunner();
                runner.RunAsync("certutil.exe", ["-addstore", "-user", "-f", "root", tmpCer], timeout: TimeSpan.FromMinutes(2))
                    .GetAwaiter().GetResult();
                try
                {
                    File.Delete(tmpCer);
                }
                catch
                {
                }

                Console.WriteLine("Root trust ensured.");
            }

            lookupMy.Close();

            using Microsoft.Win32.RegistryKey policy = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services");
            policy.SetValue("AllowSignedFiles", 1, Microsoft.Win32.RegistryValueKind.DWord);
            string tpUpper = thumbprint.ToUpperInvariant();
            var cur = (policy.GetValue("TrustedCertThumbprints") as string ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (!cur.Contains(tpUpper, StringComparer.OrdinalIgnoreCase))
            {
                cur.Add(tpUpper);
                policy.SetValue("TrustedCertThumbprints", string.Join(";", cur), Microsoft.Win32.RegistryValueKind.String);
            }

            Console.WriteLine($"Policy trust: {tpUpper}.");

        string rdp = Path.Combine(Path.GetTempPath(), VmGuest.CaptureRdpFileName);
            if (File.Exists(rdp))
            {
                IProcessRunner runner2 = new ProcessRunner();
                ProcessResult sign = runner2.RunAsync("rdpsign.exe", ["/sha256", thumbprint, rdp], timeout: TimeSpan.FromMinutes(2))
                    .GetAwaiter().GetResult();
                if (sign.ExitCode != 0)
                {
                    Console.Error.WriteLine("WARNING: rdpsign failed - a publisher warning may appear.");
                }
                else
                {
                    Console.WriteLine("Signed the capture .rdp.");
                }
            }

            Console.WriteLine("RDP trust installed. Future connects are silent - close this window.");
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

    private static VmPreflight VmTestPreflight(
        IProcessRunner runner, ProjectContext ctx, string vm, string user, string password,
        string phase, string? knownBoot, bool noAnsi)
    {
        GuestProbeResult? st = null;
        for (int i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(10000);
            }

            try
            {
                st = ProbeGuest(runner, vm, user, password, TimeSpan.FromMinutes(3));
                if (st.Sid is not null)
                {
                    break;
                }
            }
            catch
            {
                st = null;
            }
        }

        if (st is null)
        {
            throw new DecoException($"[{phase}] guest unreachable over PowerShell Direct (3 attempts).", "vm-guest-down");
        }

        if (st.Sid is null)
        {
            DevCli.Commands.CliConsole.Info($"[{phase}] no session - trying a fresh RDP logon.", noAnsi);
            int sid = VmRdpConnect(runner, ctx, vm, user, password, noAnsi).Sid;
            st = ProbeGuest(runner, vm, user, password, TimeSpan.FromMinutes(3));
            if (st.Sid is null || st.Locked || !st.Explorer)
            {
                throw new DecoException($"[{phase}] {user} is not logged on (reboot the VM; autologon signs in).", "vm-no-session");
            }
        }

        if (st.Locked || string.Equals(st.State, "Disc", StringComparison.OrdinalIgnoreCase))
        {
            string why = string.Equals(st.State, "Disc", StringComparison.OrdinalIgnoreCase) ? "disconnected" : "locked";
            DevCli.Commands.CliConsole.Warn($"[{phase}] session {st.Sid} ({st.SessionName}) {why} - logging off for a fresh auto-logon.", noAnsi);
            VmGuest.InvokeGuestAsync(runner, vm, user, password,
                $"logoff {st.Sid}", TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
            VmGuest.StopViewersForRdpAsync(runner, VmGuest.CaptureRdpFileName, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
            int sid = VmRdpConnect(runner, ctx, vm, user, password, noAnsi).Sid;
            st = ProbeGuest(runner, vm, user, password, TimeSpan.FromMinutes(3));
            if (st.Sid is null || st.Locked || !st.Explorer)
            {
                throw new DecoException($"[{phase}] session did not come back unlocked after logoff recovery.", "vm-no-session");
            }
        }

        if (!st.Explorer)
        {
            throw new DecoException($"[{phase}] no explorer in session {st.Sid} (desktop not up yet).", "vm-no-session");
        }

        if (!st.HasDotnet)
        {
            throw new DecoException($"[{phase}] guest dotnet SDK missing (run deco vm provision).", "vm-no-dotnet");
        }

        if (st.FreeGB < 15)
        {
            throw new DecoException($"[{phase}] only {st.FreeGB} GB free on guest C: (need 15).", "vm-disk");
        }

        if (knownBoot is not null && st.Boot != knownBoot)
        {
            throw new DecoException($"[{phase}] VM rebooted mid-run (boot changed).", "vm-rebooted");
        }

        string size = VmSessionSize(runner, vm, user, password, st.Sid.Value);
        bool big = VmGuestState.IsBigEnough(size);
        int sessionId = st.Sid.Value;
        if (!big)
        {
            if (st.SessionName == "console")
            {
                DevCli.Commands.CliConsole.Info($"[{phase}] console is {size} (too small) - opening a 1920x1080 RDP session.", noAnsi);
                sessionId = VmRdpConnect(runner, ctx, vm, user, password, noAnsi).Sid;
                size = VmSessionSize(runner, vm, user, password, sessionId);
            }
            else
            {
                throw new DecoException($"[{phase}] session {st.Sid} is {size} (too small) - reconnect RDP at 1920x1080.", "vm-too-small");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            // Host-safe: only deco's own viewer (tracked by its .rdp file) is hidden.
            try
            {
                int viewer = VmGuest.FindViewerPidOnceAsync(runner, VmGuest.CaptureRdpFileName).GetAwaiter().GetResult();
                VmGuest.HideViewerPid(viewer);
            }
            catch
            {
            }
        }

        DevCli.Commands.CliConsole.Success($"[{phase}] preflight ok (boot {st.Boot}Z, session {sessionId} {size}, C: {st.FreeGB} GB free).", noAnsi);
        return new VmPreflight(st.Boot, sessionId, size);
    }

    private static string VmSessionSize(IProcessRunner runner, string vm, string user, string password, int sid)
    {
        string probe = "Add-Type -AssemblyName System.Windows.Forms; $b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; "
            + "[System.IO.File]::WriteAllText('C:\\Windows\\Temp\\sess-size.txt', ($b.Width.ToString() + 'x' + $b.Height.ToString()))";
        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(probe));
        try
        {
            VmGuest.InvokeGuestAsync(runner, vm, user, password,
                "$pst = 'C:\\Windows\\Temp\\PSTools'\n"
                + "if (-not (Test-Path -LiteralPath (Join-Path $pst 'PsExec64.exe'))) {\n"
                + "  New-Item -ItemType Directory -Path $pst -Force | Out-Null\n"
                + "  Invoke-WebRequest -Uri 'https://download.sysinternals.com/files/PSTools.zip' -OutFile (Join-Path $pst 'pst.zip') -UseBasicParsing\n"
                + "  Expand-Archive -LiteralPath (Join-Path $pst 'pst.zip') -DestinationPath $pst -Force }\n"
                + $"& (Join-Path $pst 'PsExec64.exe') -accepteula -i {sid} -s powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {b64} 2>$null | Out-Null\n"
                + "$txt = Join-Path $env:TEMP 'vm-sess-size.txt'\n"
                + "Remove-Item -LiteralPath $txt -Force -ErrorAction SilentlyContinue\n"
                + "Copy-Item -FromSession $null -LiteralPath 'x' -Destination $txt -ErrorAction SilentlyContinue",
                TimeSpan.FromMinutes(3));
        }
        catch
        {
        }

        // The size file travels through a host temp copy (Copy-Item -FromSession
        // needs a live session handle, so fetch it with a dedicated snippet).
        try
        {
            ProcessResult r = VmGuest.HostPsAsync(runner,
                $"$sec = ConvertTo-SecureString $env:{VmGuest.PasswordEnv} -AsPlainText -Force\n"
                + $"$c = New-Object System.Management.Automation.PSCredential('{user.Replace("'", "", StringComparison.Ordinal)}', $sec)\n"
                + $"$s = New-PSSession -VMName '{vm.Replace("'", "", StringComparison.Ordinal)}' -Credential $c -ErrorAction Stop\n"
                + "try { Copy-Item -FromSession $s -LiteralPath 'C:\\Windows\\Temp\\sess-size.txt' "
                + "-Destination '" + Path.Combine(Path.GetTempPath(), "vm-sess-size.txt") + "' -Force -ErrorAction Stop } finally { Remove-PSSession -Session $s }",
                TimeSpan.FromMinutes(2),
                new Dictionary<string, string> { [VmGuest.PasswordEnv] = password }).GetAwaiter().GetResult();
            if (r.ExitCode == 0)
            {
                string txt = Path.Combine(Path.GetTempPath(), "vm-sess-size.txt");
                if (File.Exists(txt))
                {
                    return File.ReadAllText(txt, Encoding.UTF8).Trim();
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static VmSession VmRdpConnect(IProcessRunner runner, ProjectContext ctx, string vm, string user, string password, bool noAnsi)
    {
        try
        {
            GuestProbeResult st = ProbeGuest(runner, vm, user, password, TimeSpan.FromMinutes(2));
            if (st.Sid is not null && st.SessionName.StartsWith("rdp-tcp#", StringComparison.Ordinal) && !st.Locked)
            {
                string size = VmSessionSize(runner, vm, user, password, st.Sid.Value);
                if (VmGuestState.IsBigEnough(size))
                {
                    DevCli.Commands.CliConsole.Success($"Reusing RDP session {st.Sid} ({size}).", noAnsi);
                    int reused = 0;
                    if (OperatingSystem.IsWindows())
                    {
                        try
                        {
                            reused = VmGuest.FindViewerPidOnceAsync(runner, VmGuest.CaptureRdpFileName).GetAwaiter().GetResult();
                            VmGuest.HideViewerPid(reused);
                        }
                        catch
                        {
                        }
                    }

                    return new VmSession(st.Sid.Value, reused);
                }
            }
        }
        catch
        {
        }

        string? ip = VmGuest.GetVmIpv4Async(runner, vm, TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(ip))
        {
            throw new DecoException($"No IPv4 address on {vm} (Default Switch DHCP?).", "vm-no-ip");
        }

        VmGuest.InvokeGuestAsync(runner, vm, user, password,
            "Set-ItemProperty -LiteralPath 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server' -Name fDenyTSConnections -Value 0\n"
            + "Enable-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue | Out-Null",
            TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();

        string target = ip;
        if (OperatingSystem.IsWindows())
        {
            VmImportRdpCertTrust(runner, vm, user, password, noAnsi);
        }

        // Stored password for silent logon (no prompt on the hidden viewer).
        // Parity note: the password briefly appears on the cmdkey command
        // line (same as the ps1 kit); scoped to the VM's address only.
        VmGuest.CmdkeySetAsync(runner, target, user, password, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();

        string rdp = Path.Combine(Path.GetTempPath(), VmGuest.CaptureRdpFileName);
        string[] content = VmGuestState.RdpFileContent(target, user);
        bool needWrite = true;
        if (File.Exists(rdp))
        {
            needWrite = VmGuestState.NeedsRewrite(File.ReadAllText(rdp, Encoding.UTF8), content);
        }

        if (needWrite)
        {
            File.WriteAllLines(rdp, content, new UTF8Encoding(false));
        }

        if (OperatingSystem.IsWindows())
        {
            VmSignRdp(rdp, ctx.AppId, noAnsi);
        }

        VmGuest.StopViewersForRdpAsync(runner, VmGuest.CaptureRdpFileName, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
        Thread.Sleep(2000);
        int viewerPid = VmGuest.MstscConnectAsync(rdp).GetAwaiter().GetResult();
        DevCli.Commands.CliConsole.Info($"RDP session opening ({target}, 1920x1080).", noAnsi);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(10000);
            try
            {
                GuestProbeResult st2 = ProbeGuest(runner, vm, user, password, TimeSpan.FromMinutes(2));
                if (st2.Sid is not null && st2.SessionName.StartsWith("rdp-tcp#", StringComparison.Ordinal))
                {
                    string size2 = VmSessionSize(runner, vm, user, password, st2.Sid.Value);
                    if (VmGuestState.IsBigEnough(size2))
                    {
                        DevCli.Commands.CliConsole.Success($"RDP session {st2.Sid} Active ({size2}).", noAnsi);
                        Thread.Sleep(8000);
                        if (OperatingSystem.IsWindows())
                        {
                            VmGuest.HideViewerPid(viewerPid);
                        }

                        DevCli.Commands.CliConsole.Success("Viewer hidden - nothing to click, nothing to watch, impossible to close by accident.", noAnsi);
                        return new VmSession(st2.Sid.Value, viewerPid);
                    }
                }
            }
            catch
            {
            }
        }

        throw new DecoException("RDP session did not go Active at 1920x1080. Run deco vm rdp-trust ELEVATED once, then retry.", "vm-rdp");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VmImportRdpCertTrust(IProcessRunner runner, string vm, string user, string password, bool noAnsi)
    {
        try
        {
            ProcessResult r = VmGuest.InvokeGuestAsync(runner, vm, user, password,
                "$c = Get-ChildItem 'Cert:\\LocalMachine\\Remote Desktop' -ErrorAction SilentlyContinue | Select-Object -First 1\n"
                + "if ($c) { [Convert]::ToBase64String($c.Export('Cert')) } else { '' }",
                TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
            string cer = r.StdOut.Trim();
            if (string.IsNullOrWhiteSpace(cer))
            {
                return;
            }

            string tmp = Path.Combine(Path.GetTempPath(), "vmcapture-rdp.cer");
            File.WriteAllBytes(tmp, Convert.FromBase64String(cer));
            try
            {
                runner.RunAsync("certutil.exe", ["-addstore", "-user", "-f", "root", tmp], timeout: TimeSpan.FromMinutes(2))
                    .GetAwaiter().GetResult();
                DevCli.Commands.CliConsole.Success("RDP cert trust ensured.", noAnsi);
            }
            finally
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VmSignRdp(string rdp, string appId, bool noAnsi)
    {
        string cn = $"CN={appId}-VM-Automation";
        using System.Security.Cryptography.X509Certificates.X509Store my =
            new(System.Security.Cryptography.X509Certificates.StoreName.My,
                System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
        my.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
        var cert = my.Certificates.FirstOrDefault(c => c.Subject == cn);
        my.Close();
        if (cert is null)
        {
            DevCli.Commands.CliConsole.Warn("no signing cert - run deco vm rdp-trust once (a publisher warning may appear).", noAnsi);
            return;
        }

        bool signed = File.ReadAllText(rdp, Encoding.UTF8).Contains("signature:s:");
        // Rewritten files re-trigger consent: sign only when unsigned.
        if (signed)
        {
            return;
        }

        IProcessRunner runner = new ProcessRunner();
        try
        {
            ProcessResult r = runner.RunAsync("rdpsign.exe", ["/sha256", cert.Thumbprint, rdp], timeout: TimeSpan.FromMinutes(2))
                .GetAwaiter().GetResult();
            if (r.ExitCode == 0)
            {
                DevCli.Commands.CliConsole.Success("Signed the .rdp.", noAnsi);
            }
            else
            {
                DevCli.Commands.CliConsole.Warn("rdpsign failed - a publisher warning may appear.", noAnsi);
            }
        }
        catch
        {
            DevCli.Commands.CliConsole.Warn("rdpsign failed - a publisher warning may appear.", noAnsi);
        }
    }

    private static int RunVmCapture(string? project, string verbosity, string format, bool noAnsi,
        string version, string vm, string user, string password, bool preflightOnly, bool noClean,
        bool noReboot, int syncMin, int buildMin, int captureMin, bool dryRun)
    {
        if (!ValidateGlobals(verbosity, format, out string globalError))
        {
            Console.Error.WriteLine(globalError);
            return ExitCodes.UsageOrAuthNeeded;
        }

        if (syncMin <= 0)
        {
            syncMin = 15;
        }

        if (buildMin <= 0)
        {
            buildMin = 20;
        }

        if (captureMin <= 0)
        {
            captureMin = 30;
        }

        ProjectContext ctx;
        try
        {
            ctx = ProjectDiscovery.Discover(project);
            vm = DevCli.Commands.VmConfig.ResolveName(vm, ctx.VmName);
            user = DevCli.Commands.VmConfig.ResolveUser(user, ctx.VmUser);
            if (string.IsNullOrWhiteSpace(version))
            {
                version = ctx.InformationalVersion ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                throw new DecoException("InformationalVersion missing; pass --version.", "vm-no-version", ExitCodes.UsageOrAuthNeeded);
            }

            if (!dryRun)
            {
                password = ResolveVmPassword(ctx, password, vm);
            }
        }
        catch (DecoException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ex.ExitCode;
        }

        string guestRoot = $"C:\\{ctx.AppId}";
        string guestCsproj = $"{guestRoot}\\{Path.GetFileName(ctx.CsprojPath ?? ctx.AppId + ".csproj")}";
        if (dryRun)
        {
            Console.WriteLine($"[dry-run] would capture {version} on '{vm}': ensure running -> preflight console "
                + $"-> tar-sync tree -> guest build -> PsExec capture on the interactive desktop -> copy shots back to Releases\\Store\\{version}.");
            Console.WriteLine($"  clean={(!noClean).ToString().ToLowerInvariant()}, preflight-only={preflightOnly.ToString().ToLowerInvariant()}, reboot-heal={(!noReboot).ToString().ToLowerInvariant()}");
            Console.WriteLine("  host-safe: only deco's own RDP viewer is hidden/closed; guest Edge is closed for clean shots (inside the VM only).");
            return ExitCodes.Success;
        }

        IProcessRunner runner = new ProcessRunner();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Host-safe: only deco's own viewer (tracked by its .rdp file) is hidden.
                try
                {
                    int stale = VmGuest.FindViewerPidOnceAsync(runner, VmGuest.CaptureRdpFileName).GetAwaiter().GetResult();
                    VmGuest.HideViewerPid(stale);
                }
                catch
                {
                }
            }

            string state = VmGuest.GetVmStateAsync(runner, vm, TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
            if (state == "Missing")
            {
                throw new DecoException($"VM {vm} not found (build it with deco vm new first).", "vm-missing");
            }

            if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
            {
            DevCli.Commands.CliConsole.Info($"Starting {vm}...", noAnsi);
            VmGuest.HostPsAsync(runner, $"Start-VM -Name '{vm}'", TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(5);
                do
                {
                    Thread.Sleep(10000);
                    state = VmGuest.GetVmStateAsync(runner, vm, TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
                }
                while (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.UtcNow < deadline);

                if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DecoException("VM did not reach Running state.", "vm-not-running");
                }
            }

            string? bootUtc = null;
            try
            {
                VmPreflight pre = VmTestPreflight(runner, ctx, vm, user, password, "preflight", null, noAnsi);
                bootUtc = pre.BootUtc;
            }
            catch (Exception ex) when (!preflightOnly && !noReboot)
            {
                Console.WriteLine(ex.Message);
                DevCli.Commands.CliConsole.Warn($"Self-heal: rebooting {vm} through autologon and retrying once.", noAnsi);
                VmGuest.HostPsAsync(runner, $"Restart-VM -Name '{vm}' -Force", TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
                int wcode = RunVmWaitUnlocked(null, vm, user, password, 8, noAnsi);
                if (wcode != 0)
                {
                    throw new DecoException("VM did not come back after reboot.", "vm-reboot");
                }

                VmPreflight pre2 = VmTestPreflight(runner, ctx, vm, user, password, "preflight-retry", null, noAnsi);
                bootUtc = pre2.BootUtc;
            }

            if (preflightOnly)
            {
                DevCli.Commands.CliConsole.Success($"READY ({vm}, {version}).", noAnsi);
                VmGuest.StopViewersForRdpAsync(runner, VmGuest.CaptureRdpFileName, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
                return ExitCodes.Success;
            }

            if (bootUtc is null)
            {
                throw new DecoException("Preflight did not complete.", "vm-preflight");
            }

            try
            {
                // --- sync: single tarball over the VM bus ---
                DevCli.Commands.CliConsole.Rule($"phase: sync (timeout {syncMin}m)", noAnsi);
                VmTestPreflight(runner, ctx, vm, user, password, "sync", bootUtc, noAnsi);
                string tgz = "sync.tgz";
                string tmpTgz = Path.Combine(Path.GetTempPath(), "deco-vmsync-" + Guid.NewGuid().ToString("N") + ".tgz");
                ProcessResult tar = runner.RunAsync("tar.exe",
                    ["-czf", tmpTgz, "--exclude", "bin", "--exclude", "obj", "--exclude", "Releases",
                        "--exclude", ".vs", "--exclude", "TestResults", "--exclude", "node_modules",
                        "-C", ctx.RepoRoot, "."],
                    ctx.RepoRoot, TimeSpan.FromMinutes(syncMin)).GetAwaiter().GetResult();
                if (tar.ExitCode != 0)
                {
                    throw new DecoException("Host tar failed.", "vm-sync");
                }

                DevCli.Commands.CliConsole.Info($"Tarball: {Math.Round(new FileInfo(tmpTgz).Length / 1048576.0, 1)} MB.", noAnsi);
                try
                {
                    VmGuest.InvokeGuestAsync(runner, vm, user, password,
                        $"$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')\n"
                        + $"if ($boot -ne '{bootUtc}') {{ throw 'VM rebooted mid-run.' }}\n"
                        + $"New-Item -ItemType Directory -Path '{guestRoot}' -Force | Out-Null\n"
                        + $"Remove-Item -LiteralPath {guestRoot}\\*.txt, {guestRoot}\\*.log, {guestRoot}\\*.tgz, {guestRoot}\\*.zip -Force -ErrorAction SilentlyContinue\n"
                        + $"$t = [System.IO.Path]::Combine($env:TEMP, '{tgz}')\n"
                        + $"if (Test-Path -LiteralPath $t) {{ Remove-Item -LiteralPath $t -Force }}\n"
                        + $"'Guest slot ready.'",
                        TimeSpan.FromMinutes(syncMin)).GetAwaiter().GetResult();
                    // Copy the tarball through a host-side session (Copy-Item -ToSession).
                    VmGuest.HostPsAsync(runner,
                        $"$sec = ConvertTo-SecureString $env:{VmGuest.PasswordEnv} -AsPlainText -Force\n"
                        + $"$c = New-Object System.Management.Automation.PSCredential('{user.Replace("'", "", StringComparison.Ordinal)}', $sec)\n"
                        + $"$s = New-PSSession -VMName '{vm.Replace("'", "", StringComparison.Ordinal)}' -Credential $c -ErrorAction Stop\n"
                        + "try {\n"
                        + $"  Copy-Item -ToSession $s -LiteralPath '{tmpTgz}' -Destination '{guestRoot}\\{tgz}' -Force -ErrorAction Stop\n"
                        + "} finally { Remove-PSSession -Session $s }\n",
                        TimeSpan.FromMinutes(syncMin),
                        new Dictionary<string, string> { [VmGuest.PasswordEnv] = password }).GetAwaiter().GetResult();
                    VmGuest.InvokeGuestAsync(runner, vm, user, password,
                        $"& tar -xzf '{guestRoot}\\{tgz}' -C {guestRoot}; if ($LASTEXITCODE -ne 0) {{ throw 'guest expand failed.' }}\n"
                        + $"Remove-Item -LiteralPath '{guestRoot}\\{tgz}' -Force -ErrorAction SilentlyContinue\n"
                        + "'Tree synced.'",
                        TimeSpan.FromMinutes(syncMin)).GetAwaiter().GetResult();
                    DevCli.Commands.CliConsole.Success("Tree synced.", noAnsi);
                }
                finally
                {
                    try
                    {
                        File.Delete(tmpTgz);
                    }
                    catch
                    {
                    }
                }

                DevCli.Commands.CliConsole.Success("-- phase done: sync", noAnsi);

                // --- build in guest ---
                DevCli.Commands.CliConsole.Rule($"phase: build (timeout {buildMin}m)", noAnsi);
                VmTestPreflight(runner, ctx, vm, user, password, "build", bootUtc, noAnsi);
                string guestDotnet = $"C:\\Users\\{user}\\AppData\\Local\\Microsoft\\dotnet\\dotnet.exe";
                VmGuest.InvokeGuestAsync(runner, vm, user, password,
                    $"$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')\n"
                    + $"if ($boot -ne '{bootUtc}') {{ throw 'VM rebooted mid-run.' }}\n"
                    + $"& '{guestDotnet}' build '{guestCsproj}' -c Release -p:Platform=x64 --nologo -v minimal\n"
                    + "if ($LASTEXITCODE -ne 0) { throw \"guest build failed (exit $LASTEXITCODE).\" }\n"
                    + "'Guest build green.'",
                    TimeSpan.FromMinutes(buildMin)).GetAwaiter().GetResult();
                DevCli.Commands.CliConsole.Success("-- phase done: build", noAnsi);

                // --- capture on the interactive desktop ---
                DevCli.Commands.CliConsole.Rule($"phase: capture (timeout {captureMin}m)", noAnsi);
                VmPreflight cap = VmTestPreflight(runner, ctx, vm, user, password, "capture", bootUtc, noAnsi);
                int captureViewer = 0;
                try
                {
                    captureViewer = VmGuest.FindViewerPidOnceAsync(runner, VmGuest.CaptureRdpFileName).GetAwaiter().GetResult();
                }
                catch
                {
                }

                using (var hideCts = new CancellationTokenSource())
                {
                    var hideTask = Task.Run(async () =>
                    {
                        while (!hideCts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                if (OperatingSystem.IsWindows())
                                {
                                    // Scoped: only deco's own viewer PID, never other windows.
                                    VmGuest.HideViewerPid(captureViewer);
                                }

                                await Task.Delay(30000, hideCts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch
                            {
                            }
                        }
                    });
                    try
                    {
                        string inner = BuildGuestCaptureInner(ctx.AppId, version, !noClean, guestDotnet);
                        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(inner));
                        VmGuest.InvokeGuestAsync(runner, vm, user, password,
                            "$pst = 'C:\\Windows\\Temp\\PSTools'\n"
                            + "if (-not (Test-Path -LiteralPath (Join-Path $pst 'PsExec64.exe'))) {\n"
                            + "  New-Item -ItemType Directory -Path $pst -Force | Out-Null\n"
                            + "  Invoke-WebRequest -Uri 'https://download.sysinternals.com/files/PSTools.zip' -OutFile (Join-Path $pst 'pst.zip') -UseBasicParsing\n"
                            + "  Expand-Archive -LiteralPath (Join-Path $pst 'pst.zip') -DestinationPath $pst -Force }\n"
                            + $"$ErrorActionPreference='Continue'\n"
                            + $"& (Join-Path $pst 'PsExec64.exe') -accepteula -i {cap.SessionId} -s -h -w {guestRoot} powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {b64} 2>$null\n"
                            + "$px=$LASTEXITCODE\n"
                            + "$ErrorActionPreference='Stop'\n"
                            + "if ($px -ne 0) { throw \"Guest capture failed (exit $px).\" }\n"
                            + "'Guest capture finished (exit 0).'",
                            TimeSpan.FromMinutes(captureMin)).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        hideCts.Cancel();
                        try
                        {
                            hideTask.Wait(TimeSpan.FromSeconds(10));
                        }
                        catch
                        {
                        }
                    }
                }

                DevCli.Commands.CliConsole.Success("-- phase done: capture", noAnsi);

                ProcessResult count = VmGuest.InvokeGuestAsync(runner, vm, user, password,
                    $"(Get-ChildItem -LiteralPath '{guestRoot}\\Releases\\Store\\{version}\\screenshots' -Filter *.png -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count",
                    TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
                if (count.StdOut.Trim() == "0")
                {
                    try
                    {
                        ProcessResult tail = VmGuest.InvokeGuestAsync(runner, vm, user, password,
                            $"Get-Content -LiteralPath 'C:\\Windows\\Temp\\capture-{version}.log' | Select-Object -Last 30",
                            TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
                        DevCli.Commands.CliConsole.Rule("guest capture log tail", noAnsi);
                        Console.WriteLine(tail.StdOut.Trim());
                    }
                    catch
                    {
                        DevCli.Commands.CliConsole.Info("(no guest capture log to show)", noAnsi);
                    }

                    throw new DecoException("Capture exited 0 but produced no screenshots.", "vm-no-shots");
                }

                DevCli.Commands.CliConsole.Success($"Guest produced {count.StdOut.Trim()} screenshots.", noAnsi);
                string shotsLocal = Path.Combine(ctx.StoreDir, version, "screenshots");
                string storeLocal = Path.Combine(ctx.StoreDir, version);
                Directory.CreateDirectory(shotsLocal);
                VmGuest.HostPsAsync(runner,
                    $"$sec = ConvertTo-SecureString $env:{VmGuest.PasswordEnv} -AsPlainText -Force\n"
                    + $"$c = New-Object System.Management.Automation.PSCredential('{user.Replace("'", "", StringComparison.Ordinal)}', $sec)\n"
                    + $"$s = New-PSSession -VMName '{vm.Replace("'", "", StringComparison.Ordinal)}' -Credential $c -ErrorAction Stop\n"
                    + "try {\n"
                    + $"  Copy-Item -FromSession $s -LiteralPath '{guestRoot}\\Releases\\Store\\{version}\\screenshots\\*' -Destination '{shotsLocal}' -Recurse -Force\n"
                    + $"  Copy-Item -FromSession $s -LiteralPath '{guestRoot}\\Releases\\Store\\{version}\\screenshot-manifest.json' -Destination '{storeLocal}' -Force -ErrorAction SilentlyContinue\n"
                    + $"  Copy-Item -FromSession $s -LiteralPath '{guestRoot}\\Releases\\Store\\{version}\\review' -Destination '{storeLocal}' -Recurse -Force -ErrorAction SilentlyContinue\n"
                    + "} finally { Remove-PSSession -Session $s }",
                    TimeSpan.FromMinutes(15),
                    new Dictionary<string, string> { [VmGuest.PasswordEnv] = password }).GetAwaiter().GetResult();
                DevCli.Commands.CliConsole.Success($"Shots copied to {shotsLocal}", noAnsi);
                DevCli.Commands.CliConsole.Info("Next: review the gallery, then deco store approve --reviewer '<name>'.", noAnsi);
                return ExitCodes.Success;
            }
            finally
            {
                // Host-safe teardown: stop only deco's own viewer; if that
                // fails, re-show it so no hidden window is ever left behind.
                try
                {
                    int stopped = VmGuest.StopViewersForRdpAsync(runner, VmGuest.CaptureRdpFileName, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
                    if (stopped == 0 && OperatingSystem.IsWindows())
                    {
                        int leftover = VmGuest.FindViewerPidOnceAsync(runner, VmGuest.CaptureRdpFileName).GetAwaiter().GetResult();
                        VmGuest.ShowViewerPid(leftover);
                    }
                }
                catch
                {
                }
            }
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

    private static string BuildGuestCaptureInner(string appId, string version, bool clean, string guestDotnet)
    {
        var sb = new StringBuilder();
        sb.Append("$env:PATH='").Append(guestDotnet.Replace("dotnet.exe", "", StringComparison.Ordinal)).Append(";'")
            .Append("+$env:PATH; $ConfirmPreference='None'; ");
        sb.Append("Start-Transcript -LiteralPath 'C:\\Windows\\Temp\\capture-").Append(version).Append(".log'; ");
        sb.Append("& 'C:\\").Append(appId).Append("\\Scripts\\capture-store-shots.ps1'");
        if (clean)
        {
            sb.Append(" -Clean");
        }

        sb.Append("; $ok=$?; $c=$LASTEXITCODE; Stop-Transcript; ");
        sb.Append("if ($c -ne 0) { exit $c }; if (-not $ok) { exit 1 }");
        return sb.ToString();
    }

}
