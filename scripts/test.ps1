$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & ./build.ps1
    $sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
    if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
    & $sdk run --project tests/Watchroom.Smoke -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed' }
    & $sdk run --project tests/Watchroom.DesktopChecks -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Desktop layout tests failed' }
    & $sdk run --project tests/Watchroom.DesktopChecks -c Release --no-build -- --guest-library-layout
    if ($LASTEXITCODE -ne 0) { throw 'Guest library layout tests failed' }
} finally { Pop-Location }
