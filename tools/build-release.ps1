#requires -Version 5.1
<#
.SYNOPSIS
  Build, test and package IP Manager for a GitHub release.

.DESCRIPTION
  Produces:
    build/publish/                     self-contained win-x64 application
    release/IPManager-Setup-x64.msi    per-user MSI
    release/IPManager-Setup-x64.exe    WiX Burn bootstrapper
    release/IPManager-Portable-x64.zip portable build (contains portable.flag)
    release/checksums.txt              SHA-256 for the release assets

  The script expects WiX 5 on PATH and will download the Bal extension into the
  per-user WiX cache if it is not already present. It does not publish to GitHub.

.EXAMPLE
  pwsh -File tools/build-release.ps1 -Version 1.0.0

.EXAMPLE
  pwsh -File tools/build-release.ps1 -Version 1.0.0 -SkipTests -NoInstaller
#>
param(
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$NoInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publishDir = Join-Path $root "build\publish"
$portableDir = Join-Path $root "build\portable"
$releaseDir = Join-Path $root "release"
$msiPath = Join-Path $releaseDir "IPManager-Setup-x64.msi"
$exePath = Join-Path $releaseDir "IPManager-Setup-x64.exe"
$zipPath = Join-Path $releaseDir "IPManager-Portable-x64.zip"
$checksumsPath = Join-Path $releaseDir "checksums.txt"

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Invoke-Checked([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory = $root) {
    Write-Host "    $FilePath $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath exited with code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Get-BalExtension {
    $cacheRoot = Join-Path $env:USERPROFILE ".wix\extensions\WixToolset.Bal.wixext"
    $existing = @()
    if (Test-Path $cacheRoot) {
        $existing = @(Get-ChildItem $cacheRoot -Recurse -Filter "WixToolset.BootstrapperApplications.wixext.dll" -ErrorAction SilentlyContinue)
    }
    if ($existing.Count -gt 0) {
        return ($existing | Sort-Object FullName -Descending | Select-Object -First 1).FullName
    }

    Write-Step "Downloading WixToolset.Bal.wixext 5.0.2"
    $work = Join-Path $env:TEMP ("ipmanager-bal-" + [Guid]::NewGuid().ToString("N"))
    $nupkg = Join-Path $work "bal.nupkg"
    $zip = Join-Path $work "bal.zip"
    $extract = Join-Path $work "extract"
    New-Item -ItemType Directory -Path $work, $extract -Force | Out-Null
    try {
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/WixToolset.Bal.wixext/5.0.2" -OutFile $nupkg -UseBasicParsing
        Copy-Item $nupkg $zip
        Expand-Archive -Path $zip -DestinationPath $extract -Force
        $dll = Get-ChildItem $extract -Recurse -Filter "WixToolset.BootstrapperApplications.wixext.dll" | Select-Object -First 1
        if (-not $dll) { throw "The downloaded Bal package did not contain the expected extension DLL." }

        $targetDir = Join-Path $cacheRoot "5.0.2\wixext5"
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        $target = Join-Path $targetDir "WixToolset.BootstrapperApplications.wixext.dll"
        Copy-Item $dll.FullName $target -Force
        return $target
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be three numeric parts, for example 1.0.0."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK 10 is required. Install it and reopen the shell."
}
if (-not $NoInstaller -and -not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "WiX Toolset 5 is required for the installer. Install it or pass -NoInstaller."
}

Write-Step "Preparing directories"
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
Get-ChildItem $releaseDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in ".exe", ".msi", ".zip", ".wixpdb", ".nupkg" } |
    Remove-Item -Force
Remove-Item $publishDir, $portableDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Step "Building the solution (Release)"
Invoke-Checked "dotnet" @("build", "IPManager.slnx", "-c", "Release", "--nologo", "-v", "minimal")

if (-not $SkipTests) {
    Write-Step "Running tests"
    Invoke-Checked "dotnet" @("test", "tests\IPManager.Tests\IPManager.Tests.csproj", "-c", "Release", "--nologo", "-v", "q", "--no-build")
}
else {
    Write-Host "    Tests skipped by request." -ForegroundColor Yellow
}

Write-Step "Publishing self-contained win-x64 build"
Invoke-Checked "dotnet" @(
    "publish", "src\IPManager.App\IPManager.App.csproj",
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-o", $publishDir, "--nologo", "-v", "minimal"
)

if (-not (Test-Path (Join-Path $publishDir "IPManager.exe"))) {
    throw "Publish did not produce IPManager.exe."
}

Write-Step "Creating portable ZIP"
Copy-Item $publishDir $portableDir -Recurse -Force
Set-Content -Path (Join-Path $portableDir "portable.flag") -Value "Portable mode: data is stored in the data folder next to this file." -Encoding ASCII
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host ("    {0:N1} MB" -f ((Get-Item $zipPath).Length / 1MB))

if (-not $NoInstaller) {
    Write-Step "Building per-user MSI"
    $iconPath = Join-Path $root "src\IPManager.App\Assets\app.ico"
    Invoke-Checked "wix" @(
        "build", "installer\Package.wxs",
        "-d", "PublishDir=$publishDir",
        "-d", "ProductVersion=$Version",
        "-d", "IconPath=$iconPath",
        "-arch", "x64",
        "-o", $msiPath
    )

    Write-Step "Building Burn bootstrapper EXE"
    $bal = Get-BalExtension
    if (-not (Test-Path $bal)) { throw "Bal extension was not found at $bal." }
    Invoke-Checked "wix" @(
        "build", "installer\Bundle.wxs",
        "-d", "MsiPath=$msiPath",
        "-d", "ProductVersion=$Version",
        "-ext", $bal,
        "-arch", "x64",
        "-o", $exePath
    )
}

Write-Step "Writing checksums"
$assets = @($msiPath, $exePath, $zipPath) | Where-Object { Test-Path $_ }
$lines = foreach ($asset in $assets) {
    $hash = (Get-FileHash -Path $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    "{0}  {1}" -f $hash, (Split-Path -Leaf $asset)
}
Set-Content -Path $checksumsPath -Value $lines -Encoding ASCII

Write-Step "Release summary"
Get-ChildItem $releaseDir -File | Where-Object { $_.Extension -in ".exe", ".msi", ".zip", ".txt", ".md" } |
    Sort-Object Name |
    Format-Table Name, @{ Label = "Size"; Expression = { "{0:N1} MB" -f ($_.Length / 1MB) } }, LastWriteTime -AutoSize

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  Version:   $Version"
Write-Host "  Publish:   $publishDir"
Write-Host "  Release:   $releaseDir"
Write-Host ""
Write-Host "Next: verify the installer, then publish the assets with GitHub CLI:"
Write-Host "  gh release create v$Version `"$exePath`" `"$msiPath`" `"$zipPath`" `"$checksumsPath`" --title `"IP Manager $Version`" --notes-file release/RELEASE_NOTES.md"
