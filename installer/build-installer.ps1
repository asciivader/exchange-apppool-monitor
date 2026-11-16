# Copyright (c) 2026 Nathan Kelly. Licensed under the MIT License; see the LICENSE file.
<#
.SYNOPSIS
  Builds the Exchange AppPool Monitor installers.

.DESCRIPTION
  For each edition (see "Two editions" in the README):
    1. Publishes the app for 64-bit Windows with dotnet publish -p:Flavor=<edition>.
    2. Adds the README, LICENSE and docs folder to the published files.
    3. Runs NSIS (makensis) on ExchangeAppPoolMonitor.nsi to produce the setup exe.

  Output, in ..\artifacts\ (ignored by git):
    ExchangeAppPoolMonitor-Setup-<version>-standalone.exe   for servers without .NET
    ExchangeAppPoolMonitor-Setup-<version>-dotnet6.exe      for servers with the ASP.NET Core Runtime 6.0

  Needs: the .NET 6 SDK (or newer) and NSIS 3 (https://nsis.sourceforge.io, installs to
  C:\Program Files (x86)\NSIS). Neither is needed on the Exchange servers.

.PARAMETER Flavor
  Standalone, DotNet6, or All (the default) to build both.

.PARAMETER Version
  Optional. Defaults to the <Version> in ExchangeAppPoolMonitor.csproj.

.PARAMETER PackageSource
  Optional. A folder of NuGet package files to restore from INSTEAD of the internet, for
  air-gapped build machines (the evaluation package's "packages" folder). See docs\DEPENDENCIES.md.

.EXAMPLE
  .\build-installer.ps1
.EXAMPLE
  .\build-installer.ps1 -Flavor Standalone
.EXAMPLE
  .\build-installer.ps1 -PackageSource ..\..\packages
#>
param(
    [ValidateSet('All', 'Standalone', 'DotNet6')][string]$Flavor = 'All',
    [string]$Version,
    [string]$PackageSource
)

$ErrorActionPreference = 'Stop'
$project   = Split-Path $PSScriptRoot -Parent          # infra\exchange-apppool-monitor
$csproj    = Join-Path $project 'ExchangeAppPoolMonitor.csproj'
$artifacts = Join-Path $project 'artifacts'

if (-not $Version) {
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "No <Version> found in $csproj" }

# Find makensis: on the PATH, or the default NSIS install folder.
$makensis = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source
if (-not $makensis) { $makensis = Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe' }
if (-not (Test-Path $makensis)) { throw "makensis.exe not found. Install NSIS 3 from https://nsis.sourceforge.io" }

$flavors = if ($Flavor -eq 'All') { 'Standalone', 'DotNet6' } else { , $Flavor }
foreach ($f in $flavors) {
    $suffix  = $f.ToLowerInvariant()
    $publish = Join-Path $artifacts "publish-$suffix"
    $setup   = Join-Path $artifacts "ExchangeAppPoolMonitor-Setup-$Version-$suffix.exe"

    Write-Host "Publishing $f edition, version $Version..."
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    if ($PackageSource) {
        # Offline: restore only from the given folder, then build without touching the network.
        dotnet restore $csproj "-p:Flavor=$f" --source (Resolve-Path $PackageSource)
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $f" }
        dotnet publish $csproj -c Release "-p:Flavor=$f" --no-restore -o $publish
    } else {
        dotnet publish $csproj -c Release "-p:Flavor=$f" -o $publish
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $f" }
    Copy-Item (Join-Path $project 'README.md') $publish
    Copy-Item (Join-Path $project 'LICENSE') $publish
    Copy-Item (Join-Path $project 'docs') (Join-Path $publish 'docs') -Recurse

    Write-Host "Building $setup..."
    & $makensis /V2 "/DVERSION=$Version" "/DFLAVOR=$f" "/DPUBLISH_DIR=$publish" "/DOUT_FILE=$setup" (Join-Path $PSScriptRoot 'ExchangeAppPoolMonitor.nsi')
    if ($LASTEXITCODE -ne 0) { throw "makensis failed for $f" }
    Write-Host "Done: $setup"
}
