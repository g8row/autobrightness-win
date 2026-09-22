<#
.SYNOPSIS
  Publishes the app and builds one MSI per architecture, then checks each MSI really targets its architecture.

.EXAMPLE
  ./build/Build-Installers.ps1 -Version 0.3.0 -OutDir dist
#>
param(
    [string[]] $Arch = @('x64', 'arm64'),
    [string] $Version,
    [string] $OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Windows Installer's summary information: property 7 (Template) is "<platform>;<languages>".
function Get-MsiPlatform([string] $path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $summary = $installer.GetType().InvokeMember('SummaryInformation', 'GetProperty', $null, $installer, @($path, 0))
    $template = $summary.GetType().InvokeMember('Property', 'GetProperty', $null, $summary, @(7))
    return ($template -split ';')[0]
}

$versionArgs = if ($Version) { @("-p:Version=$Version") } else { @() }
$name = if ($Version) { "AutoBrightness-$Version" } else { 'AutoBrightness' }
$built = @()

foreach ($a in $Arch) {
    $rid = "win-$a"
    $publish = Join-Path $root "publish/$rid/"
    $publishArgs = @('publish', (Join-Path $root 'src/AutoBrightness.App'), '-c', 'Release', '-r', $rid,
        '--self-contained', 'true', '-p:ContinuousIntegrationBuild=true', '-o', $publish) + $versionArgs
    & dotnet $publishArgs
    if ($LASTEXITCODE) { throw "publish for $rid failed ($LASTEXITCODE)" }

    # The installer project's intermediate output doesn't depend on the platform, so an incremental build for
    # the second architecture silently reuses the first one's MSI. Start each architecture from scratch.
    Remove-Item -Recurse -Force (Join-Path $root 'installer/obj'), (Join-Path $root 'installer/bin') -ErrorAction SilentlyContinue
    $msiDir = Join-Path $root "msi/$rid"
    $installerArgs = @('build', (Join-Path $root 'installer'), '-c', 'Release', '--no-incremental',
        "-p:InstallerPlatform=$a", "-p:PublishDir=$publish", '-o', $msiDir) + $versionArgs
    & dotnet $installerArgs
    if ($LASTEXITCODE) { throw "installer for $rid failed ($LASTEXITCODE)" }

    $msi = Join-Path $msiDir 'AutoBrightness.msi'
    $platform = Get-MsiPlatform $msi
    if ($platform -ne $a) { throw "$msi targets '$platform', expected '$a'" }

    $target = Join-Path $OutDir "$name-$rid.msi"
    Copy-Item $msi $target -Force
    $built += $target
    Write-Host "$target ($platform)"
}

$hashes = $built | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash }
if (@($hashes | Select-Object -Unique).Count -ne $built.Count) { throw 'Two installers are identical; one architecture was not rebuilt.' }
