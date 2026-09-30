$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & ./build.ps1
    $sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
    if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
    & $sdk run --project tests/Watchroom.Smoke -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed' }
} finally { Pop-Location }
