#Requires -Version 5.1
<#
.SYNOPSIS
  Installs deco (DevEcosystem Commander) for the current user: publishes a
  single-file deco.exe, puts it on the user PATH, and wires shell completions.
  Idempotent — safe to re-run after every upgrade.

.EXAMPLE
  packaging\install.ps1
  packaging\install.ps1 -Uninstall
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [switch]$Uninstall,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\deco')
)

$ErrorActionPreference = 'Stop'

$BeginMarker = '# >>> deco completions >>>'
$EndMarker = '# <<< deco completions <<<'

function Get-UserPath
{
    $key = 'HKCU:\Environment'
    $cur = (Get-ItemProperty -LiteralPath $key -Name Path -ErrorAction SilentlyContinue).Path
    if ([string]::IsNullOrWhiteSpace($cur)) { return @() }
    return @($cur.Split(';') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Set-UserPath([string[]]$dirs)
{
    Set-ItemProperty -LiteralPath 'HKCU:\Environment' -Name Path -Value ($dirs -join ';')
    $env:Path = ((@($dirs) + @($env:Path.Split(';'))) | Select-Object -Unique | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ';'
    Broadcast-EnvironmentChange
}

# Tell running apps (Explorer, Terminal) the environment changed so NEW
# windows pick up PATH without a logoff. Registry writes alone don't notify.
function Broadcast-EnvironmentChange
{
    $sig = @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
    try
    {
        Add-Type -MemberDefinition $sig -Name DecoEnvNotify -Namespace Win32 -ErrorAction Stop | Out-Null
        [UIntPtr]$res = [UIntPtr]::Zero
        [void][Win32.DecoEnvNotify]::SendMessageTimeout([IntPtr]0xffff, 0x1a, [UIntPtr]::Zero, 'Environment', 0x0002, 5000, [ref]$res)
    }
    catch
    {
    }
}

function Remove-CompletionBlock([string]$profilePath)
{
    if (-not (Test-Path -LiteralPath $profilePath)) { return $false }
    $lines = @(Get-Content -LiteralPath $profilePath -Encoding utf8)
    $start = -1; $end = -1
    for ($i = 0; $i -lt $lines.Count; $i++)
    {
        if ($lines[$i].Trim() -eq $BeginMarker) { $start = $i }
        if ($lines[$i].Trim() -eq $EndMarker) { $end = $i; break }
    }
    if ($start -ge 0 -and $end -gt $start)
    {
        $kept = @()
        if ($start -gt 0) { $kept += $lines[0..($start - 1)] }
        if ($end + 1 -lt $lines.Count) { $kept += $lines[($end + 1)..($lines.Count - 1)] }
        while ($kept.Count -gt 0 -and [string]::IsNullOrWhiteSpace($kept[-1])) { $kept = $kept[0..($kept.Count - 2)] }
        if ($kept.Count -eq 0) { Remove-Item -LiteralPath $profilePath -Force }
        else { $kept | Set-Content -LiteralPath $profilePath -Encoding utf8 }
        return $true
    }
    return $false
}

if ($Uninstall)
{
    if ($PSCmdlet.ShouldProcess($InstallDir, 'Uninstall deco'))
    {
        if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
        $dirs = @(Get-UserPath | Where-Object { $_ -ne $InstallDir })
        Set-UserPath $dirs
        if (Remove-CompletionBlock($PROFILE)) { Write-Host 'Removed deco completions from $PROFILE.' }
        Write-Host 'deco uninstalled. Restart your terminals.'
    }
    return
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csproj = Join-Path $repoRoot 'src\DevCli\DevCli.csproj'

# 0. Preflight: .NET 10 runtime (framework-dependent single file needs it).
$runtime = (& dotnet --list-runtimes 2>$null | Where-Object { $_ -match 'Microsoft\.NETCore\.App 10\.' } | Select-Object -First 1)
if (-not $runtime)
{
    throw '.NET 10 runtime not found. Install it (winget install Microsoft.DotNet.Runtime.10) or publish self-contained instead.'
}

# 1. Publish single-file, framework-dependent win-x64.
$publishDir = Join-Path ([System.IO.Path]::GetTempPath()) ('deco-publish-' + [Guid]::NewGuid().ToString('N'))
if ($PSCmdlet.ShouldProcess($InstallDir, 'Install deco'))
{
    # Single file, native libraries self-extract on first run: the install
    # dir holds exactly one file (deco.exe). Framework-dependent (needs the
    # .NET 10 runtime, preflighted above) for small size + fast startup.
    & dotnet publish $csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    $exe = Join-Path $publishDir 'deco.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Publish produced no deco.exe.' }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $InstallDir 'deco.exe') -Force
    Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue

    # Browser driver folder: single-file publish drops the Playwright
    # `.playwright` driver tree (node + protocol package) that CloakBrowser
    # and stock Playwright resolve beside the entry assembly. Without it,
    # every `store portal*` command fails with "Driver not found".
    $driverSrc = Join-Path $repoRoot 'src\DevCli\bin\Release\net10.0\.playwright'
    if (-not (Test-Path -LiteralPath $driverSrc)) { throw "Playwright driver folder missing: $driverSrc (build first)." }
    $driverDst = Join-Path $InstallDir '.playwright'
    if (Test-Path -LiteralPath $driverDst) { Remove-Item -LiteralPath $driverDst -Recurse -Force }
    Copy-Item -LiteralPath $driverSrc -Destination $driverDst -Recurse -Force
    Write-Host "Installed browser drivers to $driverDst"

    # 2. User PATH (idempotent).
    $dirs = @(Get-UserPath)
    if ($dirs -notcontains $InstallDir)
    {
        Set-UserPath (@($InstallDir) + $dirs)
        Write-Host "Added to user PATH: $InstallDir"
    }
    else
    {
        Write-Host "Already on user PATH: $InstallDir"
    }

    # 3. Completions (idempotent marker block).
    $profileDir = Split-Path -Parent $PROFILE
    if (-not [string]::IsNullOrWhiteSpace($profileDir)) { New-Item -ItemType Directory -Path $profileDir -Force | Out-Null }
    [void](Remove-CompletionBlock($PROFILE))
    $block = @($BeginMarker, '(& deco completions --shell powershell | Out-String) | Invoke-Expression', $EndMarker, '')
    Add-Content -LiteralPath $PROFILE -Value $block -Encoding utf8
    Write-Host "Wired completions into `$PROFILE."

    $ver = & (Join-Path $InstallDir 'deco.exe') --version
    Write-Host ""
    Write-Host "deco $ver installed to $InstallDir"
    Write-Host 'Restart your terminals (or `refreshenv`), then run: deco --help'
}
