# Registers the GM ECU Simulator as a J2534 PassThru device.
#
# J2534-1 v04.04 device discovery is registry-driven. The standard layout
# is FLAT: each immediate subkey of PassThruSupport.04.04 IS a device, with
# all values (Name / Vendor / FunctionLibrary / ProtocolsSupported / per-
# protocol DWORD flags) on that subkey directly. We register one entry per
# bitness:
#   HKLM\SOFTWARE\PassThruSupport.04.04\GmEcuSim          (64-bit hosts)
#   HKLM\SOFTWARE\WOW6432Node\PassThruSupport.04.04\GmEcuSim (32-bit hosts)
#
# Earlier versions of this script created an extra "Device1" sub-level -
# wrong layout that most hosts do not follow. The apply path here strips
# any legacy nesting before writing the flat values, and the uninstall
# path removes both layouts.
#
# Run elevated. Pass -Build to rebuild both shim DLLs first.

[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

# --- Check elevation -------------------------------------------------------
# Both Register and Unregister write to HKLM and need admin. Checking up
# front gives a clean error before we do anything destructive.

$id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object System.Security.Principal.WindowsPrincipal($id)
if (-not $pr.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Register.ps1 must be run elevated (Administrator) to write HKLM."
}

# --- Resolve paths ---------------------------------------------------------
#
# Two supported layouts:
#   1. Release bundle from a GitHub release - the zip is flat: the exe and
#      both shim DLLs sit in the same folder as the ShimInstaller\ subdir.
#   2. Source tree - shims under PassThruShim\{x64\,}Debug, exe under
#      GmEcuSimulator\bin\Debug\net9.0-windows.
# Probe (1) first; fall back to (2) for developer machines.

$repoRoot = Split-Path -Parent $PSScriptRoot

$flatShim64 = Join-Path $repoRoot "PassThruShim64.dll"
$flatShim32 = Join-Path $repoRoot "PassThruShim32.dll"
$flatExe    = Join-Path $repoRoot "GmEcuSimulator.exe"

if ((Test-Path $flatShim64) -and (Test-Path $flatShim32) -and (Test-Path $flatExe)) {
    $shim64 = $flatShim64
    $shim32 = $flatShim32
    $exe    = $flatExe
} else {
    $shim64 = Join-Path $repoRoot "PassThruShim\x64\Debug\PassThruShim64.dll"
    $shim32 = Join-Path $repoRoot "PassThruShim\Debug\PassThruShim32.dll"
    $exe    = Join-Path $repoRoot "GmEcuSimulator\bin\Debug\net9.0-windows\GmEcuSimulator.exe"
}

# --- Optional build --------------------------------------------------------

if ($Build) {
    $msbuild = "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path $msbuild)) { throw "MSBuild not found at $msbuild" }

    Write-Host "Building 64-bit shim..."
    & $msbuild "$repoRoot\PassThruShim\PassThruShim.vcxproj" /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "MSBuild x64 failed (exit $LASTEXITCODE)" }
    Write-Host "Building 32-bit shim..."
    & $msbuild "$repoRoot\PassThruShim\PassThruShim.vcxproj" /p:Configuration=Debug /p:Platform=Win32 /nologo /v:minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "MSBuild Win32 failed (exit $LASTEXITCODE)" }
    Write-Host "Building C# simulator..."
    & dotnet build "$repoRoot\GM ECU Simulator.sln" -c Debug | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
}

# --- Validate ---------------------------------------------------------------

if (-not $Uninstall) {
    foreach ($p in @($shim64, $shim32, $exe)) {
        if (-not (Test-Path $p)) { throw "Required artifact missing: $p (run with -Build first)" }
    }
}

# --- Registry layout (FLAT - values directly on the device subkey) ---------

$key64 = "HKLM:\SOFTWARE\PassThruSupport.04.04\GmEcuSim"
$key32 = "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\GmEcuSim"

# Old (wrong) layout this script previously wrote - apply path cleans them
# before writing the flat values; uninstall removes them recursively.
$oldKey64 = "HKLM:\SOFTWARE\PassThruSupport.04.04\GmEcuSim\Device1"
$oldKey32 = "HKLM:\SOFTWARE\WOW6432Node\PassThruSupport.04.04\GmEcuSim\Device1"

function Set-Device([string]$key, [string]$shimPath) {
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    Set-ItemProperty -Path $key -Name "Name"               -Value "GM ECU Simulator"
    Set-ItemProperty -Path $key -Name "Vendor"             -Value "hjtrbo"
    # ConfigApplication is left empty intentionally. ForScan (v2.4.8 beta,
    # confirmed via ProcMon on 2026-05-20) reads registry values via
    # RegEnumValue in declared order and silently abandons the entry when
    # ConfigApplication exceeds ~128 wchars - the simulator's source-tree
    # exe path is 128+ chars and tripped the limit, so ForScan never read
    # FunctionLibrary and never called LoadLibrary on the shim. Hosts that
    # *do* use ConfigApplication (a "Configure..." button on the device
    # entry) just won't show one for us; the simulator UI is what they
    # would have launched anyway, and the user already has it open. The
    # peer entries (Tactrix OpenPort) ship with this value blank too, so
    # we're in good company.
    Set-ItemProperty -Path $key -Name "ConfigApplication"  -Value ""
    Set-ItemProperty -Path $key -Name "FunctionLibrary"    -Value $shimPath
    # ProtocolsSupported is the legacy comma-separated string; all the
    # peer entries on this machine (MDI, Drew, Tactrix) leave it blank and
    # use the per-protocol DWORDs below as the authoritative advertisement.
    # Matching that shape is what gets us past ForScan's enumeration filter
    # alongside the empty ConfigApplication above.
    Set-ItemProperty -Path $key -Name "ProtocolsSupported" -Value ""
    Set-ItemProperty -Path $key -Name "CAN"                -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name "ISO15765"           -Value 1 -Type DWord
    # Classic protocols we do not support, but explicitly zero. Some hosts
    # use "key present at all" as a coarse "is this a real v04.04 entry"
    # filter; absence and 0 are semantically equivalent per spec but cheap
    # to write defensively.
    Set-ItemProperty -Path $key -Name "J1850VPW"           -Value 0 -Type DWord
    Set-ItemProperty -Path $key -Name "J1850PWM"           -Value 0 -Type DWord
    Set-ItemProperty -Path $key -Name "ISO9141"            -Value 0 -Type DWord
    Set-ItemProperty -Path $key -Name "ISO14230"           -Value 0 -Type DWord
    Write-Host "  Registered: $key -> $shimPath"
}

function Remove-Device([string]$key) {
    if (Test-Path $key) {
        # Recurse so any old "Device1" sub-key from the prior layout goes too.
        Remove-Item $key -Recurse -Force
        Write-Host "  Removed: $key"
    } else {
        Write-Host "  Not present: $key"
    }
}

# --- Uninstall path ---------------------------------------------------------

if ($Uninstall) {
    Write-Host "Removing GM ECU Simulator J2534 entries (only - no other vendors are touched)..."
    Remove-Device $key64
    Remove-Device $key32
    Write-Host ""
    Write-Host "Done. Run ShimInstaller\List.ps1 to confirm your other J2534 DLL entries are still present."
    return
}

# --- Apply ------------------------------------------------------------------

Write-Host "Registering GM ECU Simulator..."

# Strip residue from the old (wrong) two-level layout before writing the
# flat one. Without this the legacy "Device1" sub-key would linger
# underneath the now-flat GmEcuSim entry.
foreach ($old in @($oldKey64, $oldKey32)) {
    if (Test-Path $old) {
        Remove-Item $old -Recurse -Force
        Write-Host "  Cleaned up legacy nested key: $old"
    }
}

Set-Device $key64 $shim64
Set-Device $key32 $shim32
Write-Host ""
Write-Host "Done. J2534 hosts should now discover 'GM ECU Simulator' as a device."
Write-Host "Run ShimInstaller\List.ps1 to verify the layout."
