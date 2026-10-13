#Requires -Version 5.1
<#
.SYNOPSIS
  Thin per-repo shim: every old script becomes `deco <command>`.
  Created by `deco init`. Anything not yet ported falls through to docs/archive/ with a pointer.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$DecoArgs
)

$ErrorActionPreference = 'Stop'

function Find-Deco {
    $c = Get-Command deco -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $local = Join-Path $PSScriptRoot '..' '..' 'DevCli' 'src' 'DevCli' 'bin' 'Release' 'net10.0' 'deco.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    throw "deco not found: install it (see DevCli/packaging/install.ps1) or build DevCli."
}

$repo = Split-Path -Parent $PSScriptRoot
$json = Join-Path $repo '.devcli.json'
$extra = @()
if (Test-Path -LiteralPath $json) { $extra += @('--project', $repo) }

& (Find-Deco) @extra @DecoArgs
exit $LASTEXITCODE
