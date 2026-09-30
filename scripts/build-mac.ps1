param([ValidateSet('arm64','x64')][string]$Architecture = 'arm64', [string]$PythonExecutable = 'python3')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
    if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools/cli'
    & $sdk publish src/Watchroom.Mac -c Release -r "osx-$Architecture" --self-contained true --configfile NuGet.Config -o "artifacts/Watchroom-Mac-$Architecture"
    if ($LASTEXITCODE -ne 0) { throw 'Mac publish failed' }
    & $PythonExecutable scripts/package-mac.py --arch $Architecture
    if ($LASTEXITCODE -ne 0) { throw 'Mac packaging failed' }
} finally { Pop-Location }
