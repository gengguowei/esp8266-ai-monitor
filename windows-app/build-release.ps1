<#
.SYNOPSIS
    Builds the Windows release of AIClockBridge into dist\.

.DESCRIPTION
    Publishes AIClockBridge as a framework-dependent SINGLE FILE win-x64 exe
    (~27 MB) into dist\AIClockBridge-<Version>-Windows-x64\. The version comes
    from <Version> in AIClockBridge\AIClockBridge.csproj - that property is the
    single source of truth for the folder and file name, keep it in step with
    FW_VERSION in firmware\include\config.h.

    Framework-dependent means the end user needs the .NET 8 Desktop Runtime
    (x64). Pass -SelfContained for a standalone ~150 MB exe that needs nothing
    installed.

    Unless -KeepStale is given, previous Windows bridge payloads under dist\
    are removed so only the folder this run produced is left. Firmware .bin
    files and flash backups in dist\ are never touched.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\windows-app\build-release.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\windows-app\build-release.ps1 -SelfContained -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot,
    [switch]$SelfContained,
    [switch]$SkipTests,
    [switch]$KeepStale,
    [switch]$StopRunning
)

$ErrorActionPreference = 'Stop'

$appDir      = $PSScriptRoot
$repoDir     = Split-Path -Parent $appDir
$projectPath = Join-Path $appDir 'AIClockBridge\AIClockBridge.csproj'
$testPath    = Join-Path $appDir 'AIClockBridge.Tests\AIClockBridge.Tests.csproj'
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoDir 'dist' }

# Legacy hand-made payload folders from before this script existed. Anything
# matching AIClockBridge-*-Windows-x64 is handled by version instead.
$legacyNames = @(
    'AIClockBridge-Windows-lockscreen',
    'claude-desktop-build',
    'claude-desktop-client'
)

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }

# ---- preconditions -------------------------------------------------------
function Get-SdkVersions {
    param([string]$Exe)
    $output = & $Exe --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $output) { return @() }
    return @($output | ForEach-Object { ($_ -split ' ')[0] })
}

# `dotnet` on PATH is often the shared host from a runtime-only install, which
# resolves fine but has no SDK - `dotnet run` then dies with "No .NET SDKs were
# found". So probe the usual install roots for one that actually carries an 8.x
# SDK instead of trusting whatever PATH points at.
function Resolve-DotnetCli {
    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    if ($env:DOTNET_ROOT)          { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    if ($env:ProgramFiles)         { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    if (${env:ProgramFiles(x86)})  { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe') }
    if ($env:LOCALAPPDATA)         { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }

    $probed = @()
    $seen = @{}
    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate)) { continue }
        $full = (Resolve-Path -LiteralPath $candidate).Path
        if ($seen.ContainsKey($full)) { continue }
        $seen[$full] = $true

        $sdks = Get-SdkVersions -Exe $full
        $probed += [pscustomobject]@{ Path = $full; Sdks = $sdks }
        if ($sdks | Where-Object { $_ -like '8.*' }) { return $full }
    }

    $report = if ($probed) {
        ($probed | ForEach-Object {
            $list = if ($_.Sdks) { $_.Sdks -join ', ' } else { '(no SDK, runtime only)' }
            "  $($_.Path) -> $list"
        }) -join [Environment]::NewLine
    } else {
        '  (no dotnet.exe found on PATH or in the usual install roots)'
    }
    throw @"
.NET 8 SDK not found - the runtime alone cannot build. Probed:
$report
Install the .NET 8 SDK (x64): https://dotnet.microsoft.com/download/dotnet/8.0
"@
}

$dotnet = Resolve-DotnetCli
Write-Step "Using dotnet: $dotnet"

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project not found: $projectPath"
}

# A running copy of an earlier build holds an exclusive lock on its own exe, so
# wiping the output folder below dies with a bare "access denied" that says
# nothing about the actual cause. Catch it here with an actionable message.
$running = @(Get-Process -Name 'AIClockBridge' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if ($StopRunning) {
        Write-Step "Stopping $($running.Count) running AIClockBridge process(es) (-StopRunning)"
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 700   # let Windows release the file lock
    } else {
        $separator = [Environment]::NewLine + '  '
        $where = ($running | ForEach-Object {
            try { $_.Path } catch { "pid $($_.Id)" }
        }) -join $separator
        throw @"
AIClockBridge is currently running and holds a lock on its own exe:
  $where
Quit it from the tray icon (right-click, last menu entry) and run this again,
or pass -StopRunning to have this script close it for you.
"@
    }
}

# ---- version -------------------------------------------------------------
[xml]$csproj = Get-Content -LiteralPath $projectPath -Raw
$versionNode = $csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
if (-not $versionNode) {
    throw "No <Version> property in $projectPath. Add one - it names the release folder."
}
$version = ([string]$versionNode.Version).Trim()

$payloadName = "AIClockBridge-$version-Windows-$($Runtime -replace '^win-', '')"
$outDir      = Join-Path $OutputRoot $payloadName

Write-Step "AIClockBridge $version -> $outDir"

# ---- regression tests ----------------------------------------------------
if (-not $SkipTests) {
    if (Test-Path -LiteralPath $testPath) {
        Write-Step 'Running regression tests (no quota API calls, no settings changes)'
        & $dotnet run --project $testPath -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Regression tests failed (exit $LASTEXITCODE)." }
    } else {
        Write-Warning "Test project not found, skipping: $testPath"
    }
}

# ---- publish -------------------------------------------------------------
if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$selfContainedFlag = if ($SelfContained) { 'true' } else { 'false' }
$publishArgs = @(
    'publish', $projectPath,
    '-c', $Configuration,
    '-r', $Runtime,
    "--self-contained", $selfContainedFlag,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=embedded',        # no loose .pdb next to the exe
    '-p:SatelliteResourceLanguages=en',
    '-o', $outDir,
    '--nologo'
)

Write-Step "Publishing (single file, self-contained=$selfContainedFlag)"
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$exePath = Join-Path $outDir 'AIClockBridge.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Publish finished but $exePath is missing."
}

# ---- tidy dist\ ----------------------------------------------------------
if (-not $KeepStale) {
    $stale = @()
    $stale += Get-ChildItem -LiteralPath $OutputRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $legacyNames -contains $_.Name }
    $stale += Get-ChildItem -Path (Join-Path $OutputRoot 'AIClockBridge-*-Windows-*') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne $payloadName }

    foreach ($dir in ($stale | Where-Object { $_ } | Sort-Object FullName -Unique)) {
        # Tidying old payloads is housekeeping, not part of the build - a locked
        # file in one of them must not fail an otherwise good release.
        try {
            Remove-Item -LiteralPath $dir.FullName -Recurse -Force
            Write-Step "Removed stale payload: $($dir.Name)"
        } catch {
            Write-Warning "Could not remove stale payload $($dir.Name): $($_.Exception.Message)"
        }
    }
}

# ---- report --------------------------------------------------------------
$exe  = Get-Item -LiteralPath $exePath
$hash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
$sizeMb = [math]::Round($exe.Length / 1MB, 1)

Write-Host ''
Write-Host "Release payload : $outDir" -ForegroundColor Green
Get-ChildItem -LiteralPath $outDir | ForEach-Object {
    '  {0,-32} {1,10:N0} bytes' -f $_.Name, $_.Length | Write-Host
}
Write-Host ''
Write-Host "AIClockBridge.exe  $sizeMb MB"
Write-Host "SHA256             $hash"
if (-not $SelfContained) {
    Write-Host 'Requires .NET 8 Desktop Runtime (x64) on the target machine.' -ForegroundColor Yellow
}
