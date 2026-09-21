<#
.SYNOPSIS
    One-time dev environment setup for the Windows bridge: installs the .NET 8
    SDK for the current user and verifies the project restores.

.DESCRIPTION
    Uses Microsoft's official dotnet-install.ps1 to place the SDK under
    %LOCALAPPDATA%\Microsoft\dotnet. This is a PER-USER install: no administrator
    rights, no UAC prompt, nothing written outside your profile, and it does not
    disturb a system-wide .NET or Visual Studio installation.

    Then it points the user-scope PATH and DOTNET_ROOT at that directory, and
    runs a restore of AIClockBridge.csproj so the first real build does not stall
    on downloading ImageSharp / System.IO.Ports.

    Safe to re-run: an existing 8.x SDK short-circuits the download, and the PATH
    entry is only appended once.

.PARAMETER SkipPath
    Do not touch user PATH / DOTNET_ROOT. build-release.ps1 probes
    %LOCALAPPDATA%\Microsoft\dotnet anyway, so builds still work - you just will
    not have `dotnet` on the command line.

.PARAMETER Force
    Install even when a usable 8.x SDK is already present.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\windows-app\setup-dev-env.ps1

.NOTES
    To undo: delete %LOCALAPPDATA%\Microsoft\dotnet, then remove that path from
    your user PATH and delete the DOTNET_ROOT user variable (Win+R ->
    rundll32 sysdm.cpl,EditEnvironmentVariables). The script prints the exact
    values it set.
#>
[CmdletBinding()]
param(
    [string]$Channel = '8.0',
    [string]$InstallDir,
    [switch]$SkipPath,
    [switch]$SkipRestore,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$appDir  = $PSScriptRoot
$project = Join-Path $appDir 'AIClockBridge\AIClockBridge.csproj'
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' }

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }

# Pure helper so the append stays idempotent and case-insensitive, and so a
# trailing backslash does not create a duplicate entry.
function Add-PathEntry {
    param([string]$Path, [string]$Entry)
    $norm = { param($p) $p.TrimEnd('\', '/').ToLowerInvariant() }
    $parts = @()
    if ($Path) { $parts = @($Path -split ';' | Where-Object { $_ -ne '' }) }
    foreach ($part in $parts) {
        if ((& $norm $part) -eq (& $norm $Entry)) { return $Path }   # already there
    }
    if ($parts.Count -eq 0) { return $Entry }
    return (($parts + $Entry) -join ';')
}

function Get-SdkVersions {
    param([string]$Exe)
    if (-not (Test-Path -LiteralPath $Exe)) { return @() }
    $output = & $Exe --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $output) { return @() }
    return @($output | ForEach-Object { ($_ -split ' ')[0] })
}

function Test-Sdk8 {
    param([string]$Exe)
    return [bool](Get-SdkVersions -Exe $Exe | Where-Object { $_ -like '8.*' })
}

# ---- already installed? --------------------------------------------------
$dotnetExe = Join-Path $InstallDir 'dotnet.exe'
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $Force) {
    foreach ($candidate in @($dotnetExe, $onPath.Source) | Where-Object { $_ }) {
        if (Test-Sdk8 -Exe $candidate) {
            Write-Step "A .NET 8 SDK is already installed: $candidate"
            Get-SdkVersions -Exe $candidate | ForEach-Object { Write-Host "    $_" }
            $dotnetExe = $candidate
            $alreadyInstalled = $true
            break
        }
    }
}

# ---- install -------------------------------------------------------------
if (-not $alreadyInstalled) {
    Write-Step "Installing the .NET $Channel SDK for the current user"
    Write-Host "    target: $InstallDir  (per-user, no admin rights needed)"

    # Windows PowerShell 5.1 may still default to TLS 1.0.
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { }

    $installer = Join-Path ([IO.Path]::GetTempPath()) 'dotnet-install.ps1'
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    } catch {
        throw @"
Could not download Microsoft's installer script from https://dot.net/v1/dotnet-install.ps1
  $($_.Exception.Message)
If you are behind a proxy or it is blocked, install the .NET 8 SDK (x64) by hand:
  https://dotnet.microsoft.com/download/dotnet/8.0
"@
    }

    # -NoPath: the installer only sets PATH for its own session; this script
    # makes it persistent below instead.
    & $installer -Channel $Channel -InstallDir $InstallDir -NoPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet-install.ps1 failed (exit $LASTEXITCODE)." }

    $dotnetExe = Join-Path $InstallDir 'dotnet.exe'
    if (-not (Test-Sdk8 -Exe $dotnetExe)) {
        throw "Install finished but no 8.x SDK is visible at $dotnetExe."
    }
    Write-Step 'SDK installed'
    Get-SdkVersions -Exe $dotnetExe | ForEach-Object { Write-Host "    $_" }
}

# ---- user PATH + DOTNET_ROOT --------------------------------------------
if ($SkipPath) {
    Write-Step 'Skipping PATH / DOTNET_ROOT (-SkipPath)'
} else {
    $installRoot = Split-Path -Parent $dotnetExe
    $userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
    $newPath  = Add-PathEntry -Path $userPath -Entry $installRoot
    if ($newPath -ne $userPath) {
        [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
        Write-Step "Appended to your user PATH: $installRoot"
    } else {
        Write-Step "Already on your user PATH: $installRoot"
    }

    $currentRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'User')
    if ($currentRoot -ne $installRoot) {
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $installRoot, 'User')
        Write-Step "Set user DOTNET_ROOT = $installRoot"
    }

    # Make this session usable without reopening a terminal.
    $env:PATH = (Add-PathEntry -Path $env:PATH -Entry $installRoot)
    $env:DOTNET_ROOT = $installRoot
    Write-Host '    (already-open terminals keep the old PATH - reopen them)'
}

# ---- verify --------------------------------------------------------------
if (-not $SkipRestore) {
    if (Test-Path -LiteralPath $project) {
        Write-Step 'Restoring NuGet packages (ImageSharp, System.IO.Ports)'
        & $dotnetExe restore $project --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Restore failed (exit $LASTEXITCODE). Check network access to nuget.org."
        }
    } else {
        Write-Warning "Project not found, skipping restore: $project"
    }
}

Write-Host ''
Write-Host 'Dev environment ready.' -ForegroundColor Green
Write-Host "  dotnet       $dotnetExe"
Write-Host "  SDKs         $((Get-SdkVersions -Exe $dotnetExe) -join ', ')"
Write-Host ''
Write-Host 'Next:'
Write-Host '  powershell -ExecutionPolicy Bypass -File .\windows-app\build-release.ps1'
if (-not $SkipPath) {
    Write-Host ''
    Write-Host 'To undo the environment changes later:'
    Write-Host "  remove `"$installRoot`" from your user PATH, delete the DOTNET_ROOT user variable,"
    Write-Host "  and delete $InstallDir"
}
