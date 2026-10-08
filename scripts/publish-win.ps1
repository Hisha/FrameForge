#!/usr/bin/env pwsh
#Requires -Version 5.1
param(
    [string]$Version,
    [switch]$Installer
)
$ErrorActionPreference = 'Stop'

function Test-PngIcon {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Application icon is missing: $Path" }
    $Bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($Bytes.Length -lt 26 -or
        -not ($Bytes[0] -eq 137 -and $Bytes[1] -eq 80 -and $Bytes[2] -eq 78 -and $Bytes[3] -eq 71 -and
              $Bytes[4] -eq 13 -and $Bytes[5] -eq 10 -and $Bytes[6] -eq 26 -and $Bytes[7] -eq 10)) {
        throw "Application icon is not a valid PNG: $Path"
    }
    $Width = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($Bytes, 16))
    $Height = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($Bytes, 20))
    if ($Width -ne 512 -or $Height -ne 512) {
        throw "Application PNG icon must be 512x512; got ${Width}x${Height}: $Path"
    }
    if ($Bytes[25] -notin 4, 6) {
        throw "Application PNG icon must carry an alpha channel; color type is $($Bytes[25]): $Path"
    }
}

function Test-IcoIcon {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Application icon is missing: $Path" }
    $ExpectedSizes = @(16, 24, 32, 48, 64, 128, 256)
    $Stream = [System.IO.File]::OpenRead($Path)
    $Reader = [System.IO.BinaryReader]::new($Stream)
    try {
        if ($Reader.ReadUInt16() -ne 0 -or $Reader.ReadUInt16() -ne 1) { throw "Invalid ICO header: $Path" }
        $Count = $Reader.ReadUInt16()
        if ($Count -ne $ExpectedSizes.Count) { throw "ICO must contain $($ExpectedSizes.Count) frames; got ${Count}: $Path" }
        $ActualSizes = @()
        for ($Index = 0; $Index -lt $Count; $Index++) {
            $Width = $Reader.ReadByte(); $Height = $Reader.ReadByte()
            [void]$Reader.ReadByte(); [void]$Reader.ReadByte()
            [void]$Reader.ReadUInt16(); $BitsPerPixel = $Reader.ReadUInt16()
            $Length = $Reader.ReadUInt32(); $Offset = $Reader.ReadUInt32()
            $Width = if ($Width -eq 0) { 256 } else { [int]$Width }
            $Height = if ($Height -eq 0) { 256 } else { [int]$Height }
            if ($Width -ne $Height -or $BitsPerPixel -ne 32 -or $Length -eq 0 -or $Offset + $Length -gt $Stream.Length) {
                throw "Invalid ${Width}x${Height} ICO frame: $Path"
            }
            $ActualSizes += $Width
        }
        if ([string]::Join(',', $ActualSizes) -ne [string]::Join(',', $ExpectedSizes)) {
            throw "ICO frame sizes must be $($ExpectedSizes -join ', '); got $($ActualSizes -join ', '): $Path"
        }
    }
    finally {
        $Reader.Dispose()
        $Stream.Dispose()
    }
}

function Test-ExecutableIconResource {
    param([Parameter(Mandatory)][string]$Path)

    if (-not ('FrameForge.NativeResourceProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace FrameForge {
    public static class NativeResourceProbe {
        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
        private const uint DONT_RESOLVE_DLL_REFERENCES = 0x00000001;
        private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr parameter);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);

        public static bool HasGroupIcon(string path) {
            IntPtr module = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | DONT_RESOLVE_DLL_REFERENCES);
            if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try {
                int count = 0;
                EnumResNameProc callback = (m, t, n, p) => { count++; return true; };
                EnumResourceNames(module, new IntPtr(14), callback, IntPtr.Zero); // RT_GROUP_ICON
                GC.KeepAlive(callback);
                return count > 0;
            }
            finally { FreeLibrary(module); }
        }
    }
}
'@
    }
    if (-not [FrameForge.NativeResourceProbe]::HasGroupIcon($Path)) {
        throw "Published executable has no embedded RT_GROUP_ICON resource: $Path"
    }
}

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
$IconPng = Join-Path $Root 'assets\branding\frameforge-icon.png'
$IconIco = Join-Path $Root 'assets\branding\frameforge-icon.ico'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet was not found in PATH.'
    exit 1
}
if (-not (Get-Command Compress-Archive -ErrorAction SilentlyContinue)) {
    Write-Error 'Compress-Archive was not found.'
    exit 1
}

Test-PngIcon $IconPng
Test-IcoIcon $IconIco

if (-not $Version) {
	$Version = & dotnet msbuild $Project -getProperty:Version
	if ($LASTEXITCODE -ne 0 -or -not $Version) {
    	Write-Error 'Failed to read the project version.'
    	exit 1
	}
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

$AppExe = Join-Path $PackageDir 'FrameForge.exe'
if (-not (Test-Path -LiteralPath $AppExe -PathType Leaf)) {
    $LegacyAppExe = Join-Path $PackageDir 'FrameForge.Desktop.exe'
    if (-not (Test-Path -LiteralPath $LegacyAppExe -PathType Leaf)) {
        throw "FrameForge.exe missing from publish output: $AppExe"
    }
    Rename-Item -Path $LegacyAppExe -NewName 'FrameForge.exe' -Force
}
Test-ExecutableIconResource $AppExe

Copy-Item $IconPng (Join-Path $PackageDir 'frameforge-icon.png')

if (Test-Path -LiteralPath (Join-Path $Root 'README.md')) { Copy-Item (Join-Path $Root 'README.md') (Join-Path $PackageDir 'README.md') }
if (Test-Path -LiteralPath (Join-Path $Root 'LICENSE')) { Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $PackageDir 'LICENSE') }
if (Test-Path -LiteralPath (Join-Path $Root 'THIRD_PARTY_NOTICES.md')) { Copy-Item (Join-Path $Root 'THIRD_PARTY_NOTICES.md') (Join-Path $PackageDir 'THIRD_PARTY_NOTICES.md') }
$ThirdPartyDir = Join-Path $PackageDir 'third_party'
New-Item -ItemType Directory -Path $ThirdPartyDir -Force | Out-Null
Copy-Item (Join-Path $Root 'third_party\Nmpq.Standard-LICENSE.txt') (Join-Path $ThirdPartyDir 'Nmpq.Standard-LICENSE.txt')

Get-ChildItem -Path $PackageDir -Recurse -File |
    Where-Object { $_.Name -like '*.pdb' -or $_.Name -like '*.Development.json' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Compress-Archive -Path $PackageDir -DestinationPath $Archive -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$Zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
try {
    $EntryNames = @($Zip.Entries | ForEach-Object FullName)
    if (-not ($EntryNames | Where-Object { $_ -match '(^|/)FrameForge\.exe$' })) {
        throw "FrameForge.exe is missing from completed ZIP: $Archive"
    }
    if (-not ($EntryNames | Where-Object { $_ -match '(^|/)frameforge-icon\.png$' })) {
        throw "frameforge-icon.png is missing from completed ZIP: $Archive"
    }
}
finally {
    $Zip.Dispose()
}

if ($Installer) {
    $Iscc = Find-Iscc
    if (-not $Iscc) { Write-Error 'Inno Setup (ISCC.exe) was not found. Install Inno Setup 6 or ensure it is in PATH.'; exit 1 }
    $IssFile = Join-Path $Root 'installer\windows\FrameForge.iss'
    $Args = @('/Q', "/DMyAppVersion=$Version", "/DMyAppSourceDir=$PackageDir", "/DMyAppOutputDir=$Dist", $IssFile)
    & $Iscc $Args
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not (Test-Path -LiteralPath $SetupExe -PathType Leaf)) {
        throw "Inno Setup did not create the expected installer: $SetupExe"
    }
    Test-ExecutableIconResource $SetupExe
}

Write-Host 'Release package(s) created in:' $Dist
Get-ChildItem -Path $Dist -Filter "FrameForge*-$Version*" | Format-Table Name, Length
