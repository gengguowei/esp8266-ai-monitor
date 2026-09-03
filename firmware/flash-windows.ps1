[CmdletBinding()]
param(
    [string]$Port,
    [string]$FirmwarePath,
    [string]$DeviceIp,
    [ValidateRange(115200, 921600)]
    [int]$Baud = 460800,
    [switch]$SkipBackup,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'

$firmwareDir = $PSScriptRoot
$repoDir = Split-Path -Parent $firmwareDir
$toolDir = Join-Path $firmwareDir '.flash-tools'
$distDir = Join-Path $repoDir 'dist'

function Find-Python {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python38\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python39\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python310\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\python.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe')
    )
    $command = Get-Command python -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        & $candidate -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 8) else 1)'
        if ($LASTEXITCODE -eq 0) { return $candidate }
    }
    throw 'Python 3.8+ is required. Install Python and run this script again.'
}

function Invoke-Esptool {
    param([string[]]$Arguments, [switch]$Capture)

    $oldErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Capture) {
            $output = & $script:pythonExe -m esptool @Arguments 2>&1
            $exitCode = $LASTEXITCODE
            $output | ForEach-Object { Write-Host $_ }
            if ($exitCode -ne 0) { throw "esptool failed with exit code $exitCode" }
            return ($output -join "`n")
        }

        & $script:pythonExe -m esptool @Arguments
        if ($LASTEXITCODE -ne 0) { throw "esptool failed with exit code $LASTEXITCODE" }
    }
    finally {
        $ErrorActionPreference = $oldErrorAction
    }
}

function Resolve-Firmware {
    if ($FirmwarePath) {
        return (Resolve-Path -LiteralPath $FirmwarePath).Path
    }

    $platformIoBin = Join-Path $firmwareDir '.pio\build\nodemcuv2\firmware.bin'
    if (Test-Path -LiteralPath $platformIoBin) {
        return (Resolve-Path -LiteralPath $platformIoBin).Path
    }

    $distBin = Get-ChildItem -LiteralPath $distDir -Filter 'firmware-*.bin' -File `
        -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($distBin) { return $distBin.FullName }
    throw 'firmware.bin was not found. Build it first or pass -FirmwarePath.'
}

function Resolve-Port {
    if ($Port) {
        $normalized = $Port.ToUpperInvariant()
        if ($normalized -notmatch '^COM\d+$') { throw "Invalid serial port: $Port" }
        return $normalized
    }

    $ports = @([System.IO.Ports.SerialPort]::GetPortNames() |
        Where-Object { $_ -ne 'COM1' } | Sort-Object)
    if ($ports.Count -eq 1) { return $ports[0] }
    if ($ports.Count -eq 0) {
        throw 'No USB serial port found. Check the data cable and CH340/CP210x driver.'
    }
    throw "Multiple serial ports found ($($ports -join ', ')); select one with -Port COMx."
}

function Resolve-DeviceIp {
    if ($DeviceIp) { return $DeviceIp }
    $settingsPath = Join-Path $env:APPDATA 'AIClockBridge\settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) { return $null }
    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        if (-not $settings.device_host) { return $null }
        return ($settings.device_host -split ':')[0]
    }
    catch { return $null }
}

$pythonExe = Find-Python
$firmwareBin = Resolve-Firmware
$serialPort = Resolve-Port
$deviceAddress = Resolve-DeviceIp

Write-Host "Python : $pythonExe"
Write-Host "Port   : $serialPort"
Write-Host "Image  : $firmwareBin"

New-Item -ItemType Directory -Path $toolDir -Force | Out-Null
$oldPythonPath = $env:PYTHONPATH
$env:PYTHONPATH = $toolDir

try {
    if (-not (Test-Path -LiteralPath (Join-Path $toolDir 'esptool.py'))) {
        Write-Host 'First run: installing isolated esptool 3.0...'
        $oldNoProxy = $env:NO_PROXY
        $oldNoProxyLower = $env:no_proxy
        try {
            # This machine has a stale Windows proxy entry; direct PyPI access
            # was the verified working path. The override is process-local only.
            $env:NO_PROXY = '*'
            $env:no_proxy = '*'
            & $pythonExe -m pip install --disable-pip-version-check `
                --target $toolDir 'esptool==3.0'
            if ($LASTEXITCODE -ne 0) { throw 'Failed to install esptool.' }
        }
        finally {
            $env:NO_PROXY = $oldNoProxy
            $env:no_proxy = $oldNoProxyLower
        }
    }

    & $pythonExe -c 'import esptool'
    if ($LASTEXITCODE -ne 0) { throw 'The isolated esptool installation is invalid.' }

    $common = @('--chip', 'esp8266', '--port', $serialPort)
    $chipOutput = Invoke-Esptool -Capture -Arguments `
        ($common + @('--baud', '115200', 'chip_id'))
    if ($chipOutput -notmatch 'Chip is ESP8266') {
        throw 'The selected serial device is not an ESP8266. Flashing stopped.'
    }

    $flashOutput = Invoke-Esptool -Capture -Arguments `
        ($common + @('--baud', '115200', 'flash_id'))
    if ($flashOutput -notmatch 'Detected flash size: 4MB') {
        throw 'Flash size is not the verified 4MB layout. Flashing stopped.'
    }

    if ($VerifyOnly) {
        Write-Host 'Verified ESP8266EX with 4MB Flash. No data was written.'
        exit 0
    }

    if (-not $SkipBackup) {
        New-Item -ItemType Directory -Path $distDir -Force | Out-Null
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $backupPath = Join-Path $distDir "backup-esp8266-before-flash-$stamp.bin"
        Write-Host "Backing up the complete 4MB Flash: $backupPath"
        Invoke-Esptool -Arguments ($common + @(
            '--baud', $Baud.ToString(), 'read_flash',
            '0x000000', '0x400000', $backupPath
        ))
        $backupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backupPath).Hash
        Write-Host "Backup SHA-256: $backupHash"
    }

    Write-Host 'Writing application sectors (erase_flash is never used)...'
    Invoke-Esptool -Arguments ($common + @(
        '--baud', $Baud.ToString(), 'write_flash',
        '--flash_mode', 'dio', '--flash_freq', '40m', '--flash_size', '4MB',
        '0x000000', $firmwareBin
    ))

    $firmwareHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $firmwareBin).Hash
    Write-Host "Firmware SHA-256: $firmwareHash"

    if ($deviceAddress) {
        Write-Host "Waiting for device: http://$deviceAddress/api/info"
        $deadline = (Get-Date).AddSeconds(75)
        do {
            try {
                $info = Invoke-RestMethod -Uri "http://$deviceAddress/api/info" `
                    -Method Get -TimeoutSec 3
                Write-Host "Device online: fw=$($info.fw), brightness=$($info.brightness), bridge=$($info.bridge)"
                exit 0
            }
            catch { Start-Sleep -Seconds 2 }
        } while ((Get-Date) -lt $deadline)
        Write-Warning 'The image was verified, but the device did not return within 75 seconds. Check its serial log.'
    }
    else {
        Write-Host 'The image was verified. No saved device IP was found, so network verification was skipped.'
    }
}
finally {
    $env:PYTHONPATH = $oldPythonPath
}
