#Requires -Version 7
<#
.SYNOPSIS
    Builds Fernplay's release files into dist\<version>\.

.DESCRIPTION
    - Fernplay-<v>-win-x64.msi              per-user installer, standalone build (no .NET needed)
    - Fernplay-<v>-win-x64-standalone.zip   portable, standalone build
    - Fernplay-<v>-win-x64.zip              portable, needs the .NET 10 Desktop Runtime
    Both zips carry portable.txt, so they keep settings in a Data folder next to the exe.
    The version comes from src\Fernplay.App\Fernplay.App.csproj. Nothing is signed, tagged or uploaded.

.PARAMETER SkipMsi
    Build only the two zips (for example while the WiX EULA is not accepted).
#>
[CmdletBinding()]
param([switch]$SkipMsi)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$appProj = Join-Path $repo 'src\Fernplay.App\Fernplay.App.csproj'
$version = ([xml](Get-Content $appProj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $appProj" }

$dist = Join-Path $repo "dist\$version"
$stage = Join-Path $repo 'dist\stage'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $dist, $stage | Out-Null

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed ($LASTEXITCODE)" }
}

$portableNote = @'
This file makes Fernplay portable: settings, library and skins are kept in the Data folder next to Fernplay.exe.
On its first start a portable Fernplay copies them from an installed Fernplay (%APPDATA%\CodeKunde\Fernplay), if there is one.
Delete this file to use %APPDATA%\CodeKunde\Fernplay instead.
'@

# Framework-dependent single file (the csproj's Release settings).
$fd = Join-Path $stage 'fd'
Invoke-Dotnet publish $appProj -c Release -o $fd -nologo -v q

# Standalone single file, .NET inside.
$sa = Join-Path $stage 'standalone'
Invoke-Dotnet publish $appProj -c Release -o $sa -nologo -v q `
    '-p:SelfContained=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true'

if (-not $SkipMsi) {
    # The MSI gets the standalone files without portable.txt.
    Invoke-Dotnet build (Join-Path $repo 'installer\Fernplay.Installer.wixproj') -c Release -nologo -v q `
        "-p:Version=$version" "-p:PublishDir=$sa\" "-p:OutDir=$dist\"
    Get-ChildItem $dist -Exclude *.msi | Remove-Item -Recurse -Force # .wixpdb and the like
}

foreach ($pair in @(@{ Dir = $fd; Zip = "Fernplay-$version-win-x64.zip" }, @{ Dir = $sa; Zip = "Fernplay-$version-win-x64-standalone.zip" })) {
    Set-Content (Join-Path $pair.Dir 'portable.txt') $portableNote -Encoding utf8
    Compress-Archive -Path (Join-Path $pair.Dir '*') -DestinationPath (Join-Path $dist $pair.Zip) -CompressionLevel Optimal
}

Remove-Item $stage -Recurse -Force
Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }, LastWriteTime
