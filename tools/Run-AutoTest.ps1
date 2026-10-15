param(
    [string]$PdfPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$exePath = "$repoRoot\src\WinPDF\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WinPDF.exe"

if (-not (Test-Path $exePath)) {
    Write-Warning "WinPDF.exe not found at $exePath."
    Write-Host "Building WinPDF (Debug x64)..." -ForegroundColor Cyan
    dotnet build "$repoRoot\WinPDF.sln" -c Debug -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed. Aborting autotest."
        exit 1
    }
}

# Remove stale reports before test
$artifactsReport = "$repoRoot\artifacts\autotest-report.log"
$localAppReport = "$env:LOCALAPPDATA\WinPDF\logs\autotest-report.log"

if (Test-Path $artifactsReport) { Remove-Item $artifactsReport -Force -ErrorAction SilentlyContinue }
if (Test-Path $localAppReport) { Remove-Item $localAppReport -Force -ErrorAction SilentlyContinue }

$arguments = "--autotest"
if (-not [string]::IsNullOrWhiteSpace($PdfPath)) {
    $arguments += " `"$PdfPath`""
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " Launching WinPDF Diagnostic Auto-Test Runner                    " -ForegroundColor Cyan
Write-Host " Target: $exePath                                                " -ForegroundColor DarkGray
Write-Host " Args:   $arguments                                              " -ForegroundColor DarkGray
Write-Host "=================================================================" -ForegroundColor Cyan

$startTime = [DateTime]::UtcNow
$process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru

$timeoutSeconds = 60
$hasExited = $process.WaitForExit($timeoutSeconds * 1000)

if (-not $hasExited) {
    Write-Error "[FAIL] WinPDF timed out after $timeoutSeconds seconds! Terminating process..."
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    exit 2
}

$exitCode = $process.ExitCode
$elapsed = ([DateTime]::UtcNow - $startTime).TotalSeconds
Write-Host "[AUTOTEST] Process exited in $([math]::Round($elapsed, 2))s with ExitCode: $exitCode" -ForegroundColor $(if ($exitCode -eq 0) { "Green" } else { "Red" })

# Read report file
$reportFound = $false
$reportContent = ""

if (Test-Path $artifactsReport) {
    $reportContent = Get-Content $artifactsReport -Raw
    $reportFound = $true
} elseif (Test-Path $localAppReport) {
    $reportContent = Get-Content $localAppReport -Raw
    $reportFound = $true
}

if ($reportFound) {
    Write-Host "`n$reportContent"
} else {
    Write-Warning "No autotest-report.log found after execution!"
}

# If process failed or crashed, query Windows Event Log for crash details
if ($exitCode -ne 0) {
    Write-Host "`n[CRASH DIAGNOSTICS] Querying Windows Application Event Log (Id 1000/1026)..." -ForegroundColor Yellow
    try {
        $crashEvents = Get-WinEvent -FilterHashtable @{
            LogName = 'Application'
            Id = 1000, 1026
            StartTime = $startTime.AddSeconds(-2)
        } -MaxEvents 5 -ErrorAction SilentlyContinue

        if ($crashEvents) {
            foreach ($evt in $crashEvents) {
                Write-Host "--------------------------------------------------" -ForegroundColor DarkYellow
                Write-Host "Event ID $($evt.Id) - $($evt.TimeCreated)" -ForegroundColor Red
                Write-Host $evt.Message
            }
        } else {
            Write-Host "No Windows Error Reporting crash events detected for this run." -ForegroundColor DarkGray
        }
    } catch {
        Write-Warning "Failed to query Event Log: $_"
    }

    exit $exitCode
}

exit 0
