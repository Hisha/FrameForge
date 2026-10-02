#!/usr/bin/env pwsh
#Requires -Version 5.1
param(
    [switch]$Installer
)
$ErrorActionPreference = 'Stop'

function Find-Iscc {
    $Cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($Cmd) { return $Cmd.Source }
    # search common paths...
    $CandidateDirs = @()
    foreach ($Lookup in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if (-not $Lookup) { continue }
        $CandidateDirs += Join-Path $Lookup 'Inno Setup 6'
        $CandidateDirs += Join-Path $Lookup 'Inno Setup 5'
        $CandidateDirs += Join-Path $Lookup 'Programs\Inno Setup 6'
    }
    foreach ($Dir in $CandidateDirs) {
        $Candidate = Join-Path $Dir 'ISCC.exe'
        if (Test-Path -LiteralPath $Candidate) { return $Candidate }
    }
    $UninstallRoots = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($RootKey in $UninstallRoots) {
        Get-ItemProperty $RootKey -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'Inno Setup*' -and $_.InstallLocation } |
            ForEach-Object {
                $Candidate = Join-Path $_.InstallLocation 'ISCC.exe'
                if (Test-Path -LiteralPath $Candidate) { return $Candidate }
            }
    }
    return $null
}

$Root = Split-Path -Parent $PSScriptRoot
$Rid = 'win-x64'
$Dist = Join-Path $Root 'dist'
$Project = Join-Path $Root 'src\FrameForge.Desktop\FrameForge.Desktop.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet was not found in PATH.'
    exit 1
}
if (-not (Get-Command Compress-Archive -ErrorAction SilentlyContinue)) {
    Write-Error 'Compress-Archive was not found.'
    exit 1
}

$Version = & dotnet msbuild $Project -getProperty:Version
if ($LASTEXITCODE -ne 0 -or -not $Version) {
    Write-Error 'Failed to read the project version.'
    exit 1
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([.+-][A-Za-z0-9.-]+)?$') {
    Write-Error "Invalid project version: $Version"
    exit 1
}
$PackageDir = Join-Path $Dist "FrameForge-$Version-$Rid"
$Archive = Join-Path $Dist "FrameForge-$Version-$Rid.zip"

Write-Host "Publishing FrameForge $Version for $Rid..."
if (Test-Path -LiteralPath $PackageDir) { Remove-Item -LiteralPath $PackageDir -Recurse -Force }
if (Test-Path -LiteralPath $Archive) { Remove-Item -LiteralPath $Archive -Force }
if ($Installer) {
    $SetupExe = Join-Path $Dist "FrameForge-Setup-$Version.exe"
    if (Test-Path -LiteralPath $SetupExe) { Remove-Item -LiteralPath $SetupExe -Force }
}
New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

& dotnet publish $Project -c Release -r $Rid --self-contained true -o $PackageDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$AppExe = Join-Path $PackageDir 'FrameForge.Desktop.exe'
if (-not (Test-Path -LiteralPath $AppExe -PathType Leaf)) {
    throw "FrameForge.Desktop.exe missing from publish output: $AppExe"
}
Rename-Item -Path $AppExe -NewName 'FrameForge.exe' -Force

if (Test-Path -LiteralPath (Join-Path $Root 'assets\branding\frameforge-icon.png')) {
    Copy-Item (Join-Path $Root 'assets\branding\frameforge-icon.png') (Join-Path $PackageDir 'frameforge-icon.png')
}

if (Test-Path -LiteralPath (Join-Path $Root 'README.md')) { Copy-Item (Join-Path $Root 'README.md') (Join-Path $PackageDir 'README.md') }
if (Test-Path -LiteralPath (Join-Path $Root 'LICENSE')) { Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $PackageDir 'LICENSE') }

Get-ChildItem -Path $PackageDir -Recurse -File |
    Where-Object { $_.Name -like '*.pdb' -or $_.Name -like '*.Development.json' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Compress-Archive -Path $PackageDir -DestinationPath $Archive -Force

if ($Installer) {
    $Iscc = Find-Iscc
    if (-not $Iscc) { Write-Error 'Inno Setup (ISCC.exe) was not found. Install Inno Setup 6 or ensure it is in PATH.'; exit 1 }
    $IssFile = Join-Path $Root 'installer\windows\FrameForge.iss'
    $Args = @('/Q', "/DMyAppVersion=$Version", "/DMyAppSourceDir=$PackageDir", "/DMyAppOutputDir=$Dist", $IssFile)
    & $Iscc $Args
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host 'Release package(s) created in:' $Dist
Get-ChildItem -Path $Dist -Filter "FrameForge*-$Version*" | Format-Table Name, Length
