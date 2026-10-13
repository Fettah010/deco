# Regenerates src/DevCli.Commands/SandboxGuest.cs from guest/capture.ps1.
# The pin test (SandboxGuestTests.Bootstrap_MatchesGuestScript) enforces sync;
# run this after editing capture.ps1.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$src = [IO.File]::ReadAllText((Join-Path $repo 'guest/capture.ps1'))
$src = $src.TrimEnd("`r", "`n")
$nl = "`r`n"
$head = 'namespace DevCli.Commands;' + $nl + $nl `
    + '/// <summary>' + $nl `
    + '/// Sandbox guest bootstrap (FlaUI runner). GENERATED - do not hand-edit:' + $nl `
    + '/// edit guest/capture.ps1 and re-run guest/emit-const.ps1.' + $nl `
    + '/// </summary>' + $nl `
    + 'public static class SandboxGuest' + $nl `
    + '{' + $nl `
    + '    public static readonly string Bootstrap = """' + $nl
$tail = $nl + '""" + "\n";' + $nl + '}' + $nl
[IO.File]::WriteAllText(
    (Join-Path $repo 'src/DevCli.Commands/SandboxGuest.cs'),
    $head + $src + $tail)
Write-Output 'SandboxGuest.cs regenerated.'
