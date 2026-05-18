# Install the logging proxy into the target directory.
# Renames the original library to <name>_real.dll (if not already done)
# and copies the built hook over the top.
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Re-launching elevated..."
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"')
    )
    exit
}

$dps        = 'C:\DPS'
$realName   = 'sa015bcr.dll'
$backupName = 'sa015bcr_real.dll'
$src        = Join-Path $PSScriptRoot 'sa015bcr_hook.dll'
$logFile    = Join-Path $dps 'Logs\sa015bcr_hook.txt'

if (-not (Test-Path $src))                        { throw "Hook DLL not built. Run build.ps1 first ($src)." }
if (-not (Test-Path (Join-Path $dps $realName))) { throw "C:\DPS\sa015bcr.dll not found." }

# Backup the genuine library only the first time.
$backup = Join-Path $dps $backupName
if (-not (Test-Path $backup)) {
    Copy-Item -Path (Join-Path $dps $realName) -Destination $backup
    Write-Host "Backed up original -> $backup"
} else {
    Write-Host "Backup already present at $backup"
}

Copy-Item -Path $src -Destination (Join-Path $dps $realName) -Force
Write-Host "Installed proxy at $dps\$realName"

$logDir = Join-Path $dps 'Logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
if (Test-Path $logFile) { Remove-Item $logFile -Force }
Write-Host ""
Write-Host "Now run DPS against the simulator and trigger Algo 92."
Write-Host "Log will appear at: $logFile"
