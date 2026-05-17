# Restore the original sa015bcr.dll.
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
$backup     = Join-Path $dps $backupName

if (Test-Path $backup) {
    Copy-Item -Path $backup -Destination (Join-Path $dps $realName) -Force
    Remove-Item $backup -Force
    Write-Host "Restored original sa015bcr.dll. Backup deleted."
} else {
    Write-Host "No backup found at $backup. Nothing to restore."
}
