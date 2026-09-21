<#
.SYNOPSIS
    Publishes AIClockFlasher into dist\ as a single .exe.

.DESCRIPTION
    Deliberately small compared to windows-app\build-release.ps1: the flasher
    is a developer/maintenance tool, not the shipped bridge, so there is no
    test project, no changelog check and no zip. Framework-dependent by
    default (needs the .NET 8 Desktop Runtime, which anyone building this repo
    already has); pass -SelfContained for a standalone exe to hand to someone
    who does not.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\windows-app\AIClockFlasher\build-flasher.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$projectPath = Join-Path $projectDir 'AIClockFlasher.csproj'
$repoDir = Split-Path -Parent (Split-Path -Parent $projectDir)
$distDir = Join-Path $repoDir 'dist'

function Write-Step { param([string]$m) Write-Host "==> $m" -ForegroundColor Cyan }

# `dotnet` on PATH is often a runtime-only host with no SDK, which fails late
# and confusingly. Same resolution order as build-release.ps1.
function Resolve-DotnetCli {
    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    if ($env:DOTNET_ROOT)         { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    if ($env:ProgramFiles)        { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe') }
    if ($env:LOCALAPPDATA)        { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }

    foreach ($c in $candidates | Select-Object -Unique) {
        if (-not $c -or -not (Test-Path -LiteralPath $c)) { continue }
        $sdks = & $c --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $c }
    }
    throw "No .NET SDK found. Install the .NET 8 SDK (x64): https://dotnet.microsoft.com/download/dotnet/8.0"
}

$dotnet = Resolve-DotnetCli
Write-Step "Using dotnet: $dotnet"

[xml]$csproj = Get-Content -LiteralPath $projectPath
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "<Version> is missing from $projectPath." }

$outDir = Join-Path $distDir "AIClockFlasher-$version-Windows-$($Runtime -replace '^win-', '')"
if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$selfContainedFlag = if ($SelfContained) { 'true' } else { 'false' }
Write-Step "Publishing $version (single file, self-contained=$selfContainedFlag)"
& $dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedFlag `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -p:SatelliteResourceLanguages=en `
    -o $outDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$exePath = Join-Path $outDir 'AIClockFlasher.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw "Publish finished but $exePath is missing." }

Get-ChildItem -LiteralPath $outDir -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Step 'Done'
'    {0}' -f $exePath
'    {0:N0} bytes' -f (Get-Item -LiteralPath $exePath).Length
if (-not $SelfContained) {
    Write-Host 'Requires .NET 8 Desktop Runtime (x64) on the target machine.' -ForegroundColor Yellow
}
