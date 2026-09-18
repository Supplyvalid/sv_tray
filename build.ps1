# Builds the agent exe and (unless -SkipInstaller) the Windows installer.
#
#   .\build.ps1                       # exe + installer into dist\
#   .\build.ps1 -SkipInstaller        # exe only
#   .\build.ps1 -MakeNsis "C:\...\makensis.exe"
#   .\build.ps1 -BuildNumber 42         # 1.0.0 -> 1.0.0.42
#   .\build.ps1 -Version 1.2.3          # override the version outright
#
# Requires: .NET 8 SDK, and NSIS (makensis.exe) for the installer step.
param(
    [switch]$SkipInstaller,
    [string]$MakeNsis,
    [string]$Version,
    [string]$BuildNumber
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

# One version for the exe and the installer alike. <Version> in the csproj is
# the single source; this just decides what to layer on top of it.
function Resolve-Version {
    if ($Version) { return $Version }

    $csproj = Join-Path $root 'src/ShelivoPrintAgent.csproj'
    $base = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $base) { throw "No <Version> element found in $csproj" }

    # CI passes its run number so every build off main is distinguishable at
    # /health; a local build just gets the plain base version.
    if ($BuildNumber) { return "$base.$BuildNumber" }
    return $base
}

function Resolve-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $sdks = & $cmd.Source --list-sdks 2>$null
        if ($sdks) { return $cmd.Source }
    }
    # The official install script's default per-user location, used when the
    # SDK isn't on PATH or the one on PATH is runtime-only.
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }
    throw "No .NET SDK found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 (or run dotnet-install.ps1 -Channel 8.0)."
}

function Resolve-MakeNsis {
    if ($MakeNsis) {
        if (-not (Test-Path $MakeNsis)) { throw "makensis.exe not found at $MakeNsis" }
        return $MakeNsis
    }
    $cmd = Get-Command makensis -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # electron-builder caches its own NSIS copy (under a version-stamped
    # folder such as Cache\nsis-3.0.4.1\...); reuse it if this machine has
    # ever built an Electron app, so NSIS needn't be installed separately.
    $cacheRoot = Join-Path $env:LOCALAPPDATA 'electron-builder\Cache'
    if (Test-Path $cacheRoot) {
        $cached = Get-ChildItem $cacheRoot -Recurse -Filter 'makensis.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($cached) { return $cached.FullName }
    }
    throw "makensis.exe not found. Install NSIS (https://nsis.sourceforge.io), or pass -MakeNsis <path>, or use -SkipInstaller."
}

$dotnet = Resolve-Dotnet
Write-Host "Using dotnet: $dotnet"

# Not $version: PowerShell variable names are case-insensitive, so that
# would silently overwrite the -Version parameter.
$appVersion = Resolve-Version
Write-Host "Version: $appVersion"

& $dotnet publish (Join-Path $root 'src\ShelivoPrintAgent.csproj') `
    -c Release -r win-x64 --self-contained true -o $dist `
    -p:Version=$appVersion
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if ($SkipInstaller) {
    Write-Host "`nBuilt: $dist\shelivo-print-agent.exe (installer skipped)"
    return
}

$makensis = Resolve-MakeNsis
Write-Host "Using makensis: $makensis"

& $makensis "/DAPP_VERSION=$appVersion" (Join-Path $root 'installer\shelivo-print-agent.nsi')
if ($LASTEXITCODE -ne 0) { throw "makensis failed" }

Write-Host "`nBuilt ${appVersion}:"
Write-Host "  $dist\shelivo-print-agent.exe      (the agent itself)"
Write-Host "  $dist\ShelivoPrintAgentSetup.exe   (installer -- this is what goes to tills)"
