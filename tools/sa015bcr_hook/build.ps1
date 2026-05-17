# Build the logging proxy as a 32-bit DLL with statically-linked CRT.
$ErrorActionPreference = 'Stop'

$vcvars = "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars32.bat"
if (-not (Test-Path $vcvars)) {
    $vcvars = "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars32.bat"
}
if (-not (Test-Path $vcvars)) {
    throw "vcvars32.bat not found. Edit build.ps1 with the right Visual Studio path."
}

$here = $PSScriptRoot
$src  = Join-Path $here 'sa015bcr_hook.cpp'
$def  = Join-Path $here 'sa015bcr_hook.def'
$out  = Join-Path $here 'sa015bcr_hook.dll'
$obj  = Join-Path $here 'sa015bcr_hook.obj'

# Single-line cl command: 32-bit DLL, static CRT, no PDB clutter.
$cl = "cl /nologo /LD /MT /O2 /EHsc /W3 ""$src"" /Fe:""$out"" /Fo:""$obj"" /link /DEF:""$def"""

cmd /c "`"$vcvars`" >nul && $cl"
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit=$LASTEXITCODE)" }

if (Test-Path $obj) { Remove-Item $obj -Force }
$expLib = Join-Path $here 'sa015bcr_hook.exp'; if (Test-Path $expLib) { Remove-Item $expLib -Force }
$libLib = Join-Path $here 'sa015bcr_hook.lib'; if (Test-Path $libLib) { Remove-Item $libLib -Force }

Write-Host "Built: $out"
