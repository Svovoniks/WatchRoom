param([int]$Port = 5080)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/cli'
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
& $sdk run --project (Join-Path $root 'src/Watchroom.Server') -c Release --no-build -- --urls "http://localhost:$Port"
