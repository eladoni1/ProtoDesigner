# Cross-language conformance check for the C++ generator.
#
# Compiles the checked-in golden headers together with main.cpp and runs the resulting binary. The
# harness asserts two things: that generated encode/decode round-trips, and that the bytes the C++
# encoder produces are identical to the ones the C# ReferenceCodec produced for the same inputs.
# That second assertion is the one that matters - it is what makes "the generator is correct" a
# testable claim rather than a hope.
#
# Requires MSVC (any edition with the C++ workload). Exits non-zero if compilation or a check fails.
#
# NOTE: keep this file pure ASCII. Windows PowerShell 5.1 reads BOM-less files as ANSI, and a stray
# non-ASCII character breaks parsing in ways the error message does not explain.

[CmdletBinding()]
param(
    [string]$GoldenDir,
    [string]$WorkDir
)

$ErrorActionPreference = "Stop"

# $PSScriptRoot is not populated while the param block is being bound under Windows PowerShell 5.1,
# so the defaults are resolved here instead.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $GoldenDir) { $GoldenDir = Join-Path $scriptRoot "..\ProtoDesigner.CodeGen.Tests\Golden" }
if (-not $WorkDir)   { $WorkDir   = Join-Path $scriptRoot "build" }

function Find-VcVars {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found; is Visual Studio installed?" }

    $root = & $vswhere -latest -property installationPath
    if (-not $root) { throw "No Visual Studio installation found." }

    $vcvars = Join-Path $root "VC\Auxiliary\Build\vcvars64.bat"
    if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found under $root - install the C++ workload." }
    return $vcvars
}

if (-not (Test-Path $GoldenDir)) {
    throw "Golden directory not found at $GoldenDir. Run the CodeGen tests first to generate it."
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
Copy-Item (Join-Path $GoldenDir "*.h") -Destination $WorkDir -Force
Copy-Item (Join-Path $scriptRoot "main.cpp") -Destination $WorkDir -Force

$vcvars = Find-VcVars
$bat = Join-Path $WorkDir "_build.bat"

$lines = @(
    '@echo off',
    ('call "' + $vcvars + '" >nul 2>&1'),
    'cl /nologo /EHsc /W4 /std:c++14 main.cpp /Fe:conformance.exe',
    'if errorlevel 1 exit /b 1',
    'echo --- running ---',
    '.\conformance.exe',
    'exit /b %errorlevel%'
)
Set-Content -Path $bat -Value $lines -Encoding ASCII

Push-Location $WorkDir
try {
    & cmd.exe /c ".\_build.bat"
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($code -ne 0) {
    Write-Error "C++ conformance FAILED (exit $code)."
    exit $code
}

Write-Host "C++ conformance passed." -ForegroundColor Green
exit 0
