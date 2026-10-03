param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$runtimeRoot = Split-Path -Parent $PSScriptRoot
$runtimeCache = Join-Path $runtimeRoot '.tools/vcredist'
$runtimeBundle = Join-Path $runtimeCache 'vc_redist.x64.exe'
# Pin the signed Microsoft payload; deliberately update this hash when servicing.
$runtimeHash = 'CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B'
New-Item -ItemType Directory -Force $runtimeCache,$Destination | Out-Null
if (!(Test-Path -LiteralPath $runtimeBundle)) {
    Invoke-WebRequest 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $runtimeBundle
}
if ((Get-FileHash -LiteralPath $runtimeBundle -Algorithm SHA256).Hash -ne $runtimeHash) {
    throw 'The Microsoft runtime payload changed. Verify its signature and update the pinned hash before publishing.'
}
$runtimeSignature = Get-AuthenticodeSignature -LiteralPath $runtimeBundle
if ($runtimeSignature.Status -ne 'Valid' -or $runtimeSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'Invalid Microsoft runtime signature' }
$runtimeWix = Join-Path $runtimeRoot '.tools/wix5/wix.exe'
if (!(Test-Path -LiteralPath $runtimeWix)) {
    $runtimeSdk = Join-Path $runtimeRoot '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $runtimeSdk)) { $runtimeSdk = 'dotnet' }
    & $runtimeSdk tool install wix --version 5.0.2 --tool-path (Join-Path $runtimeRoot '.tools/wix5') --configfile (Join-Path $runtimeRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Native runtime extractor restore failed' }
}
$runtimeExtracted = Join-Path $runtimeCache 'extracted'
& $runtimeWix burn extract $runtimeBundle -o $runtimeExtracted
if ($LASTEXITCODE -ne 0) { throw 'Microsoft runtime extraction failed' }
$runtimeFiles = Join-Path $runtimeCache 'x64'
New-Item -ItemType Directory -Force $runtimeFiles | Out-Null
# Burn payload identifiers are not filenames. Locate the cabinet by its contents.
$runtimeCabinet = Get-ChildItem -LiteralPath $runtimeExtracted -File | Where-Object {
    $runtimeMagic = [IO.File]::ReadAllBytes($_.FullName)[0..3]
    [Text.Encoding]::ASCII.GetString($runtimeMagic) -eq 'MSCF' -and ((& expand.exe -D $_.FullName) -match ': msvcp140.dll_amd64$')
} | Select-Object -First 1
if (!$runtimeCabinet) { throw 'Microsoft x64 CRT cabinet was not found' }
& expand.exe '-F:*' $runtimeCabinet.FullName $runtimeFiles | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Microsoft CRT cabinet extraction failed' }
foreach ($runtimeFile in Get-ChildItem -LiteralPath $runtimeFiles -Filter '*.dll_amd64' -File) {
    $runtimeDllSignature = Get-AuthenticodeSignature -LiteralPath $runtimeFile.FullName
    if ($runtimeDllSignature.Status -ne 'Valid' -or $runtimeDllSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "Invalid runtime DLL signature: $($runtimeFile.Name)" }
    Copy-Item -LiteralPath $runtimeFile.FullName -Destination (Join-Path $Destination ($runtimeFile.Name -replace '_amd64$', ''))
}
foreach ($runtimeRequired in 'msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $Destination $runtimeRequired))) { throw "Missing required runtime: $runtimeRequired" }
}
@{ source = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'; sha256 = $runtimeHash; version = (Get-Item -LiteralPath $runtimeBundle).VersionInfo.ProductVersion } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination 'native-runtime.json')
Write-Output 'Verified Microsoft x64 runtime included in the app directory.'
