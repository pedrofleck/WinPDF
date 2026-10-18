<#
.SYNOPSIS
    Packages a self-contained portable release of WinPDF for testing or deployment.
.DESCRIPTION
    Builds and publishes WinPDF in self-contained Release configuration, verifies all required
    native binaries, and creates a clean zip file compatible with standard Windows Explorer extraction.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$Version = 'v0.4.1'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$publishDir = Join-Path $repoRoot 'publish'
$targetFolderName = "WinPDF-$Version-win-$Platform"
$targetFolderPath = Join-Path $publishDir $targetFolderName
$targetZipPath = Join-Path $publishDir "$targetFolderName.zip"

Write-Host "Publishing self-contained $Configuration ($Platform)..." -ForegroundColor Cyan

if (Test-Path $targetFolderPath) {
    Remove-Item -Recurse -Force $targetFolderPath
}
if (Test-Path $targetZipPath) {
    Remove-Item -Force $targetZipPath
}

$projectPath = Join-Path $repoRoot 'src\WinPDF\WinPDF.csproj'
& dotnet publish $projectPath -c $Configuration -p:Platform=$Platform --self-contained true -o $targetFolderPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Verifying essential binaries in $targetFolderPath..." -ForegroundColor Cyan
$requiredFiles = @('WinPDF.exe', 'coreclr.dll', 'pdfium.dll', 'Microsoft.ui.xaml.dll', 'WinPDF.pri')
foreach ($req in $requiredFiles) {
    $full = Join-Path $targetFolderPath $req
    if (-not (Test-Path $full)) {
        throw "Missing required binary: $req"
    }
}

Write-Host "Creating clean standard ZIP archive: $targetZipPath..." -ForegroundColor Cyan
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $targetFolderPath,
    $targetZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true
)

Write-Host "Verifying ZIP archive integrity..." -ForegroundColor Cyan
$zip = [System.IO.Compression.ZipFile]::OpenRead($targetZipPath)
$entryCount = $zip.Entries.Count
$badEntries = @($zip.Entries | Where-Object { $_.FullName.StartsWith('./') -or $_.FullName.StartsWith('.\') })
$zip.Dispose()

if ($badEntries.Count -gt 0) {
    throw "ZIP contains relative dot paths which cause Windows Explorer extraction errors!"
}

Write-Host "Testing standard extraction from ZIP..." -ForegroundColor Cyan
$testExtractDir = Join-Path $publishDir "temp-verify-$Platform"
if (Test-Path $testExtractDir) {
    Remove-Item -Recurse -Force $testExtractDir
}
[System.IO.Compression.ZipFile]::ExtractToDirectory($targetZipPath, $testExtractDir)
$extractedExe = Join-Path $testExtractDir "$targetFolderName\WinPDF.exe"
if (-not (Test-Path $extractedExe)) {
    Remove-Item -Recurse -Force $testExtractDir
    throw "Verification failed: WinPDF.exe was not found after extraction!"
}
Remove-Item -Recurse -Force $testExtractDir
Write-Host "Extraction test PASSED 100%!" -ForegroundColor Green

Write-Host "Archive created successfully ($entryCount entries, $((Get-Item $targetZipPath).Length / 1MB | ForEach-Object { '{0:N2} MB' -f $_ })):" -ForegroundColor Green
Write-Host "  Folder: $targetFolderPath" -ForegroundColor Gray
Write-Host "  Zip:    $targetZipPath" -ForegroundColor Gray
