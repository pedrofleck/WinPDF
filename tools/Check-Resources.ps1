<#
.SYNOPSIS
    Verifies that every localized Resources.resw has exactly the same keys as the en-US fallback.
.DESCRIPTION
    Exits with code 1 and lists missing/extra keys when any language is out of sync.
#>
[CmdletBinding()]
param(
    [string]$StringsRoot = (Join-Path $PSScriptRoot '..\src\WinPDF\Strings'),
    [string]$DefaultLanguage = 'en-US'
)

function Get-ResourceKeys([string]$Path) {
    [xml]$xml = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    return @($xml.root.data | ForEach-Object { $_.name }) | Sort-Object
}

$defaultFile = Join-Path $StringsRoot "$DefaultLanguage\Resources.resw"
$defaultKeys = Get-ResourceKeys $defaultFile
$failed = $false

Get-ChildItem -LiteralPath $StringsRoot -Directory | Where-Object Name -ne $DefaultLanguage | ForEach-Object {
    $file = Join-Path $_.FullName 'Resources.resw'
    $keys = Get-ResourceKeys $file
    $missing = $defaultKeys | Where-Object { $_ -notin $keys }
    $extra = $keys | Where-Object { $_ -notin $defaultKeys }

    if ($missing -or $extra) {
        $failed = $true
        Write-Host "[$($_.Name)] out of sync with $DefaultLanguage" -ForegroundColor Red
        $missing | ForEach-Object { Write-Host "  missing: $_" }
        $extra | ForEach-Object { Write-Host "  extra:   $_" }
    }
    else {
        Write-Host "[$($_.Name)] OK ($($keys.Count) keys)" -ForegroundColor Green
    }
}

if ($failed) { exit 1 }
