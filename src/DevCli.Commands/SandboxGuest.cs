namespace DevCli.Commands;

/// <summary>
/// Sandbox guest bootstrap (FlaUI runner). GENERATED - do not hand-edit:
/// edit guest/capture.ps1 and re-run guest/emit-const.ps1.
/// </summary>
public static class SandboxGuest
{
    public static readonly string Bootstrap = """
# deco sandbox guest runner (v0.3.0 MVP).
# Runs INSIDE Windows Sandbox (or on the host for --self-test): installs an
# MSIX or launches an exe, drives it through ScreenshotPlan v2 steps with
# FlaUI (net48 over Add-Type, no SDK needed), captures window screenshots,
# and writes manifest.json + a done/failed sentinel the host polls.
#
#   powershell -ExecutionPolicy Bypass -File capture.ps1 -StageDir C:\deco-stage -Mode selftest
#   powershell -ExecutionPolicy Bypass -File capture.ps1 -StageDir C:\deco-stage -Mode run -PlanPath plan.json -AppExe "C:\apps\MyApp.exe"
#
# Stage contract (mapped folder):
#   in:  flaui\*.dll, capture.ps1, [plan.json]
#   out: shots\*.png, manifest.json, guest.log, done | failed (+ error.txt)
# Exit code is best-effort only (Sandbox may vanish); the sentinel is truth.
[CmdletBinding()]
param(
    [string]$StageDir = '',
    [ValidateSet('run', 'selftest')][string]$Mode = 'run',
    [string]$PlanPath = '',
    [string]$AppExe = '',
    [string]$AppArgs = '',
    [string]$MsixPath = '',
    [string]$ShotsDir = '',
    [int]$TimeoutSec = 600
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($StageDir)) { throw '-StageDir is required.' }
$stage = $StageDir.TrimEnd('\')
$shots = if ([string]::IsNullOrWhiteSpace($ShotsDir)) { Join-Path $stage 'shots' } else { $ShotsDir }
$outDir = Join-Path $stage 'out'
$logPath = Join-Path $stage 'guest.log'

function Write-Sentinel([string]$name, [string]$text = '') {
    $p = Join-Path $outDir $name
    if ($text -eq '') { New-Item -ItemType File -Path $p -Force | Out-Null }
    else { Set-Content -LiteralPath $p -Value $text -Encoding utf8 -NoNewline }
}

# Unbuffered progress: the host (and the owner) can watch out\progress.log live,
# even if the run is killed before the transcript flushes.
function Log([string]$message) {
    $line = "$([DateTime]::UtcNow.ToString('o')) $message"
    Write-Host $line
    try { Add-Content -LiteralPath (Join-Path $outDir 'progress.log') -Value $line -Encoding utf8 } catch { }
}

function Fail([string]$message) {
    try {
        Log "GUEST-FAIL: $message"
        Set-Content -LiteralPath (Join-Path $outDir 'error.txt') -Value $message -Encoding utf8
        Write-Sentinel 'failed' $message
        try { Stop-Transcript | Out-Null } catch { }
    }
    catch { }
    exit 1
}

try {
    if ([string]::IsNullOrWhiteSpace($StageDir)) { throw '-StageDir is required.' }
    foreach ($d in @($shots, $outDir)) {
        if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    }
    # Clean-slate: a previous run's sentinels must never satisfy this run's host poll.
    foreach ($stale in @('done', 'failed', 'error.txt', 'manifest.json')) {
        Remove-Item -LiteralPath (Join-Path $outDir $stale) -Force -ErrorAction SilentlyContinue
    }

    # Transcript is best-effort: it must never kill the run (progress.log is the truth).
    try { Start-Transcript -LiteralPath $logPath -Append | Out-Null }
    catch { Write-Host "transcript unavailable, continuing on progress.log" }

    # --- FlaUI bootstrap (transitives resolve from the stage dir) ---
    $flauiDir = Join-Path $stage 'flaui'
    if (-not (Test-Path -LiteralPath (Join-Path $flauiDir 'FlaUI.Core.dll'))) { throw "FlaUI deps missing in $flauiDir (host must provision them)." }
    # Pre-load the full closure so later Add-Type calls bind without any resolve hook.
    foreach ($dll in @('Interop.UIAutomationClient.dll', 'FlaUI.Core.dll', 'FlaUI.UIA3.dll')) {
        [System.Reflection.Assembly]::LoadFrom((Join-Path $flauiDir $dll)) | Out-Null
    }
    Add-Type -Path (Join-Path $flauiDir 'FlaUI.Core.dll')
    Add-Type -Path (Join-Path $flauiDir 'FlaUI.UIA3.dll')
    Add-Type -AssemblyName System.Windows.Forms
    $auto = New-Object FlaUI.UIA3.UIA3Automation
    $cf = $auto.ConditionFactory
    if ($null -eq $cf) { throw 'UIA3 ConditionFactory unavailable.' }

    # --- helpers ---
    function Find-El($root, [string]$target, [int]$seconds) {
        $byName = $false
        $key = $target
        if ($target.StartsWith('name:', [System.StringComparison]::OrdinalIgnoreCase)) {
            $byName = $true
            $key = $target.Substring(5)
        }
        $deadline = (Get-Date).AddSeconds($seconds)
        while ((Get-Date) -lt $deadline) {
            try {
                $cond = if ($byName) { $cf.ByName($key) } else { $cf.ByAutomationId($key) }
                $el = $root.FindFirstDescendant($cond)
                if ($null -ne $el) { return $el }
            }
            catch { }
            Start-Sleep -Milliseconds 500
        }
        return $null
    }

    function Wait-Window([string]$procName, [string[]]$before, [int]$seconds) {
        Log "waiting window (proc='$procName', known=$($before.Count), timeout=${seconds}s)"
        # Win32 truth: main-window handles of the target process. UIA PIDs can
        # lie (an unsupported property reads as ValueOrDefault 0), so a UIA
        # element whose handle matches Win32 truth is accepted regardless.
        $win32hwnds = @()
        $baseName = $procName -replace '\.exe$', ''
        if (-not [string]::IsNullOrWhiteSpace($baseName)) {
            try {
                $win32hwnds = @(Get-Process -Name $baseName -ErrorAction SilentlyContinue |
                    ForEach-Object { "$($_.MainWindowHandle)" } |
                    Where-Object { $_ -ne '' -and $_ -ne '0' })
            }
            catch { }
            Log "win32 handles for $baseName`: [$($win32hwnds -join '; ')]"
        }
        $deadline = (Get-Date).AddSeconds($seconds)
        $n = 0
        $lastBeat = Get-Date
        $win32hwnds = @()
        while ((Get-Date) -lt $deadline) {
            $n++
            $total = 0
            $cands = @()
            if (-not [string]::IsNullOrWhiteSpace($baseName)) {
                try {
                    $win32hwnds = @(Get-Process -Name $baseName -ErrorAction SilentlyContinue |
                        ForEach-Object { "$($_.MainWindowHandle)" } |
                        Where-Object { $_ -ne '' -and $_ -ne '0' })
                }
                catch { }
            }
            try {
                foreach ($w in $auto.GetDesktop().FindAllChildren()) {
                    $total++
                    try {
                        $hw = $w.Properties.NativeWindowHandle.ValueOrDefault
                        if ($hw -eq 0) { continue }
                        $key = "$hw"
                        if ($before -contains $key) { continue }
                        $r = $w.BoundingRectangle
                        $ww = 0
                        $hh = 0
                        try { $ww = $r.Width; $hh = $r.Height } catch { }
                        if ($ww -lt 50 -or $hh -lt 50) { continue }
                        $pname = ''
                        try {
                            $p = Get-Process -Id $w.Properties.ProcessId.ValueOrDefault -ErrorAction SilentlyContinue
                            if ($null -ne $p) { $pname = $p.ProcessName }
                        }
                        catch { }
                        $cands += "hw=$key ${ww}x${hh} proc=$pname"
                        if (-not [string]::IsNullOrWhiteSpace($procName)) {
                            $hwMatch = $win32hwnds -contains $key
                            if ($pname -ne $procName -and -not $hwMatch) { continue }
                        }
                        return $w
                    }
                    catch { }
                }
            }
            catch { }
            if (((Get-Date) - $lastBeat).TotalSeconds -ge 30) {
                $lastBeat = Get-Date
                $beatProcs = @()
                $beatName = $procName -replace '\.exe$', ''
                if (-not [string]::IsNullOrWhiteSpace($beatName)) {
                    try {
                        $beatProcs = @(Get-Process -Name $beatName -ErrorAction SilentlyContinue | ForEach-Object {
                            "$($_.Id):hwnd=$($_.MainWindowHandle):resp=$($_.Responding)" })
                    }
                    catch { }
                }
                $beatElev = $false
                try {
                    $beatElev = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
                        [Security.Principal.WindowsBuiltInRole]::Administrator)
                }
                catch { }
                Log "scan #${n}: total=$total new-visible=$($cands.Count) [$($cands -join '; ')] procs=[$($beatProcs -join '; ')] elevated=$beatElev user=$env:USERNAME"
            }
            Start-Sleep -Milliseconds 1000
        }
        return $null
    }

    function Invoke-Step($win, $step, [int]$defaultTimeout) {
        $action = [string]$step.action
        $target = [string]$step.target
        $text = [string]$step.text
        $timeout = [int]$step.timeoutSec
        if ($timeout -le 0) { $timeout = $defaultTimeout }
        Write-Host "  step: $action target='$target'"
        switch ($action) {
            'click' {
                $el = Find-El $win $target $timeout
                if ($null -eq $el) { throw "click target not found: $target" }
                try { $el.AsButton().Invoke() }
                catch { $el.Click() }
                Start-Sleep -Seconds 1
            }
            'type' {
                $el = Find-El $win $target $timeout
                if ($null -eq $el) { throw "type target not found: $target" }
                $el.Focus()
                Start-Sleep -Milliseconds 300
                [FlaUI.Core.Input.Keyboard]::Type($text)
                Start-Sleep -Milliseconds 300
            }
            'keys' {
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.SendKeys]::SendWait($text)
                Start-Sleep -Milliseconds 500
            }
            'wait' {
                if ([string]::IsNullOrWhiteSpace($target)) { Start-Sleep -Seconds $timeout }
                else {
                    $el = Find-El $win $target $timeout
                    if ($null -eq $el) { throw "wait target never appeared: $target" }
                }
            }
            default { throw "unknown step action: $action" }
        }
    }

    function Write-Shot($win, [string]$path) {
        $dir = Split-Path -Parent $path
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        try {
            $w = $win.Patterns.Window.Pattern
            $w.SetWindowVisualState([FlaUI.Core.Definitions.WindowVisualState]::Maximized)
            Start-Sleep -Seconds 1
        }
        catch { }
        $shotDone = $false
        $lastErr = ''
        for ($a = 1; $a -le 2 -and -not $shotDone; $a++) {
            try {
                $img = [FlaUI.Core.Capturing.Capture]::Element($win)
                try { $img.ToFile($path); $shotDone = $true }
                finally { try { $img.Dispose() } catch { } }
            }
            catch { $lastErr = $_.Exception.Message; Start-Sleep -Seconds 2 }
        }
        if (-not $shotDone) {
            Log "capture Element failed ($lastErr); trying Rectangle"
            try {
                $img = [FlaUI.Core.Capturing.Capture]::Rectangle(
                    $win.BoundingRectangle, (New-Object FlaUI.Core.Capturing.CaptureSettings))
                try { $img.ToFile($path); $shotDone = $true }
                finally { try { $img.Dispose() } catch { } }
            }
            catch { $lastErr = $_.Exception.Message }
        }
        if (-not $shotDone) {
            Log "capture Rectangle failed ($lastErr); trying Screen"
            try {
                $img = [FlaUI.Core.Capturing.Capture]::Screen(
                    0, (New-Object FlaUI.Core.Capturing.CaptureSettings))
                try { $img.ToFile($path); $shotDone = $true }
                finally { try { $img.Dispose() } catch { } }
            }
            catch { $lastErr = $_.Exception.Message }
        }
        if (-not $shotDone) { throw "all capture methods failed: $lastErr" }
        $f = Get-Item -LiteralPath $path
        Log "shot: $path ($($f.Length) bytes)"
    }

    function Write-Manifest($mode, $app, $started, $shotFiles) {
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $shots = @()
        foreach ($s in $shotFiles) {
            $f = Get-Item -LiteralPath $s
            $shots += @{ file = Split-Path -Leaf $s; bytes = $f.Length }
        }
        $m = [ordered]@{
            mode = $mode
            app = $app
            startedUtc = $started.ToString('o')
            finishedUtc = ([DateTime]::UtcNow).ToString('o')
            resolution = "$($bounds.Width)x$($bounds.Height)"
            shots = $shots
        }
        $mp = Join-Path $outDir 'manifest.json'
        ($m | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $mp -Encoding utf8
        return $mp
    }

    $started = [DateTime]::UtcNow
    Log "guest starting (mode=$Mode)"
    if ($Mode -eq 'selftest') {
        Log 'SELFTEST: notepad round-trip'
        $before = @($auto.GetDesktop().FindAllChildren() | ForEach-Object {
            try { "$($_.Properties.NativeWindowHandle.ValueOrDefault)" } catch { '' } } | Where-Object { $_ -ne '' })
        $p = Start-Process -FilePath "$env:SystemRoot\System32\notepad.exe" -PassThru
        try {
            $win = Wait-Window 'notepad' $before 60
            if ($null -eq $win) { throw 'notepad main window never appeared' }
            Start-Sleep -Seconds 1
            [FlaUI.Core.Input.Keyboard]::Type('deco sandbox self-test')
            Start-Sleep -Milliseconds 500
            $shot = Join-Path $shots 'selftest-01.png'
            Write-Shot $win $shot
            $mp = Write-Manifest 'selftest' 'notepad.exe' $started @($shot)
            Write-Sentinel 'done' $mp
            Log 'SELFTEST: green'
        }
        finally {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
            try { $auto.Dispose() } catch { }
        }
    }
    else {
        if ([string]::IsNullOrWhiteSpace($AppExe) -and [string]::IsNullOrWhiteSpace($MsixPath)) {
            throw 'run mode needs -AppExe or -MsixPath.'
        }
        $plan = $null
        if (-not [string]::IsNullOrWhiteSpace($PlanPath)) {
            if (-not (Test-Path -LiteralPath $PlanPath)) { throw "plan not found: $PlanPath" }
            $plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
        }
        $before = @($auto.GetDesktop().FindAllChildren() | ForEach-Object {
            try { "$($_.Properties.NativeWindowHandle.ValueOrDefault)" } catch { '' } } | Where-Object { $_ -ne '' })
        $appLabel = ''
        $proc = $null
        if (-not [string]::IsNullOrWhiteSpace($MsixPath)) {
            Write-Host "installing MSIX: $MsixPath"
            Set-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowAllTrustedApps -Value 1 -Type DWord -Force
            Add-AppxPackage -Path $MsixPath
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $za = [IO.Compression.ZipFile]::OpenRead($MsixPath)
            try {
                $me = $za.Entries | Where-Object { $_.FullName -eq 'AppxManifest.xml' } | Select-Object -First 1
                $sr = New-Object IO.StreamReader($me.Open())
                $xml = [xml]$sr.ReadToEnd()
                $sr.Close()
                $pkgName = $xml.Package.Identity.Name
            }
            finally { $za.Dispose() }
            $pkg = Get-AppxPackage -Name $pkgName | Select-Object -First 1
            if ($null -eq $pkg) { throw "installed package not found: $pkgName" }
            $appLabel = $pkg.PackageFullName
            Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
        }
        else {
            $envBackup = @{}
            if ($null -ne $plan -and $null -ne $plan.launch -and $null -ne $plan.launch.env) {
                foreach ($prop in $plan.launch.env.PSObject.Properties) {
                    $envBackup[$prop.Name] = [Environment]::GetEnvironmentVariable($prop.Name)
                    [Environment]::SetEnvironmentVariable($prop.Name, [string]$prop.Value)
                }
            }
            try {
                $launchArgs = ''
                if ($null -ne $plan -and $null -ne $plan.launch -and $null -ne $plan.launch.args) {
                    $launchArgs = [string]$plan.launch.args
                }
                if (-not [string]::IsNullOrWhiteSpace($AppArgs)) { $launchArgs = $AppArgs }
                Write-Host "launching: $AppExe $launchArgs"
                if ([string]::IsNullOrWhiteSpace($launchArgs)) { $proc = Start-Process -FilePath $AppExe -PassThru }
                else { $proc = Start-Process -FilePath $AppExe -ArgumentList $launchArgs -PassThru }
                $appLabel = Split-Path -Leaf $AppExe
                Log "launched pid=$($proc.Id)"
                Start-Sleep -Seconds 5
                try {
                    $dead = $proc.HasExited
                    Log "launch check: exited=$dead exitCode=$($proc.ExitCode)"
                }
                catch { }
            }
            finally {
                foreach ($k in @($envBackup.Keys)) {
                    if ($null -eq $envBackup[$k]) { [Environment]::SetEnvironmentVariable($k, $null) }
                    else { [Environment]::SetEnvironmentVariable($k, $envBackup[$k]) }
                }
            }
        }
        $procName = ''
        if ($null -ne $proc) {
            try { $procName = $proc.ProcessName } catch { }
        }
        try {
            $win = Wait-Window $procName $before $TimeoutSec
            if ($null -eq $win) { throw 'app main window never appeared' }
            Log 'main window found'
            Start-Sleep -Seconds 2
            $taken = @()
            if ($null -ne $plan -and $null -ne $plan.shots) {
                foreach ($shot in $plan.shots) {
                    if ($null -ne $shot.steps) {
                        foreach ($st in $shot.steps) { Invoke-Step $win $st 30 }
                    }
                    $out = Join-Path $shots ([string]$shot.fileName)
                    Write-Shot $win $out
                    $taken += $out
                }
            }
            else {
                $out = Join-Path $shots 'main-window.png'
                Write-Shot $win $out
                $taken += $out
            }
            $mp = Write-Manifest 'run' $appLabel $started $taken
            Write-Sentinel 'done' $mp
            Log 'RUN: green'
        }
        finally {
            if ($null -ne $proc) {
                try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
            }
            try { $auto.Dispose() } catch { }
        }
    }
}
catch {
    Fail($_.Exception.Message)
}
try { Stop-Transcript | Out-Null } catch { }
""" + "\n";
}
