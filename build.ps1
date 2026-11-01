param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/cli'
$env:DOTNET_NOLOGO = '1'
$sdk = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
& $sdk restore (Join-Path $PSScriptRoot 'BlackPearl.slnx') --configfile (Join-Path $PSScriptRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
& $sdk build (Join-Path $PSScriptRoot 'BlackPearl.slnx') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if ($Publish) {
    & $sdk publish (Join-Path $PSScriptRoot 'src/BlackPearl.Desktop') -c Release -r win-x64 --self-contained true --configfile (Join-Path $PSScriptRoot 'NuGet.Config') -o (Join-Path $PSScriptRoot 'artifacts/BlackPearl') -p:VlcWindowsX86Enabled=false -p:VlcWindowsArm64Enabled=false -p:PublishReadyToRun=true
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    & (Join-Path $PSScriptRoot 'scripts/copy-native-runtime.ps1') -Destination (Join-Path $PSScriptRoot 'artifacts/BlackPearl')
    Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') (Join-Path $PSScriptRoot 'artifacts/BlackPearl')
    Copy-Item (Join-Path $PSScriptRoot 'README.md') (Join-Path $PSScriptRoot 'artifacts/BlackPearl')
}
