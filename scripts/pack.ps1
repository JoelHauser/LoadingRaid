<#
.SYNOPSIS
    Builds Release and packs releases\DeployScreen_V<ver>.zip.

.DESCRIPTION
    The zip is laid out to extract straight into an SPT folder:

        BepInEx\plugins\DeployScreen\DeployScreen.Client.dll
        BepInEx\plugins\DeployScreen\environments.txt
        BepInEx\plugins\DeployScreen\banners\README.txt
        BepInEx\plugins\DeployScreen\banners\_default\

    Nothing else. No README at the top of the zip: a loose file in an archive meant
    to be extracted over an SPT folder lands in the install root, where it is litter.

    It checks that both version strings agree before building anything -- the
    csproj <Version> and DeployScreenPlugin.PluginVersion.

    It writes the zip through System.IO.Compression with forward-slash entry names.
    Compress-Archive writes backslashes, which extract on Linux as one file with
    slashes in its name rather than as a tree.

    Unlike the CamoPatch build there is no -GameAssembly: this plugin holds no
    Assembly-CSharp reference and resolves every game type by name at runtime, so an
    install that has never been through the SPT Launcher builds it fine.

.PARAMETER SPTPath
    The SPT install to build against -- used for BepInEx and the UnityEngine modules.

.PARAMETER Install
    Also copy the staged folder into BepInEx\plugins\DeployScreen under SPTPath.
    Existing banner images and environments.txt are left alone.

.EXAMPLE
    scripts\pack.ps1
    scripts\pack.ps1 -SPTPath C:\HUH -Install
#>
[CmdletBinding()]
param(
    [string]$SPTPath = "C:\HUH",
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'src\DeployScreen.Client'

function Get-Match {
    param([string]$Path, [string]$Pattern)

    $text = Get-Content -Raw -Path (Join-Path $root $Path)
    $found = [regex]::Match($text, $Pattern)
    if (-not $found.Success) { throw "no version found in $Path" }
    return $found.Groups[1].Value
}

# ---------------------------------------------------------------- the version

$versions = [ordered]@{
    'DeployScreen.Client.csproj' = Get-Match 'src\DeployScreen.Client\DeployScreen.Client.csproj' '<Version>([^<]+)</Version>'
    'DeployScreenPlugin.cs'      = Get-Match 'src\DeployScreen.Client\DeployScreenPlugin.cs' 'PluginVersion\s*=\s*"([^"]+)"'
}

# Forced to an array: a single string indexes as characters.
$distinct = @($versions.Values | Select-Object -Unique)
if ($distinct.Count -ne 1) {
    $versions.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-30} {1}" -f $_.Key, $_.Value) }
    throw "the version strings disagree"
}

$version = $distinct[0]
Write-Host "Deploy Screen $version" -ForegroundColor Cyan

# ------------------------------------------------------------------ the build

dotnet build (Join-Path $projectDir 'DeployScreen.Client.csproj') -c Release --nologo -v q "-p:SPTPath=$SPTPath"
if ($LASTEXITCODE -ne 0) { throw "the build failed" }

$dll = Join-Path $projectDir 'bin\Release\DeployScreen.Client.dll'
if (-not (Test-Path $dll)) { throw "built, but no DLL at $dll" }

# ------------------------------------------------------------------ the stage

$stage = Join-Path $root 'dist\stage'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

$pluginDir = Join-Path $stage 'BepInEx\plugins\DeployScreen'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item $dll $pluginDir

Copy-Item (Join-Path $root 'assets\environments.txt') $pluginDir

# The banners tree ships with its folders in place, so there is somewhere obvious to
# drop images into. _default needs a file or the zip will not carry the folder at all.
$bannersDir = Join-Path $pluginDir 'banners'
New-Item -ItemType Directory -Force -Path (Join-Path $bannersDir '_default') | Out-Null
Copy-Item (Join-Path $root 'assets\banners-README.txt') (Join-Path $bannersDir 'README.txt')
Copy-Item (Join-Path $root 'assets\default-README.txt') (Join-Path $bannersDir '_default\README.txt')

# -------------------------------------------------------------------- the zip

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$releases = Join-Path $root 'releases'
New-Item -ItemType Directory -Force -Path $releases | Out-Null

$zipPath = Join-Path $releases ("DeployScreen_V{0}.zip" -f $version)
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($file in Get-ChildItem -Recurse -File -Path $stage) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, 'Optimal') | Out-Null
    }
}
finally {
    $zip.Dispose()
}

Write-Host "packed $zipPath" -ForegroundColor Green

# -------------------------------------------------------------- the install

if ($Install) {
    $destination = Join-Path $SPTPath 'BepInEx\plugins\DeployScreen'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null

    Copy-Item $dll $destination -Force

    # Never clobber art or a tuned environments.txt that is already there.
    foreach ($keep in @('environments.txt', 'banners\README.txt', 'banners\_default\README.txt')) {
        $target = Join-Path $destination $keep
        if (-not (Test-Path $target)) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item (Join-Path $stage "BepInEx\plugins\DeployScreen\$keep") $target
        }
    }

    Write-Host "installed to $destination" -ForegroundColor Green
}

Remove-Item -Recurse -Force $stage
