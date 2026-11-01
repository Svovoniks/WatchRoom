# -Step Files stages the app folder and -Step Installer compiles the full installer from it, so CI can build that
# installer while scripts/package-updates.ps1 builds the update installers from the same folder.
param([switch]$SkipPublish, [string]$Version = '', [ValidateSet('All', 'Files', 'Installer')][string]$Step = 'All')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (!$Version) { $Version = ([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version }
    if (!$SkipPublish -and $Step -ne 'Installer') { & ./build.ps1 -Publish }
    $output = Join-Path $root 'artifacts/BlackPearl'
    if (!(Test-Path (Join-Path $output 'BlackPearl.exe'))) { throw 'Publish the app first' }
    $publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $output 'BlackPearl.dll')).ProductVersion.Split('+')[0]
    if ($publishedVersion -ne $Version) { throw "Published app version $publishedVersion does not match installer version $Version. Publish the correct app first." }
    if ($Step -ne 'Installer') {
        & ./scripts/copy-native-runtime.ps1 -Destination $output
        Copy-Item 'README.md','THIRD-PARTY-NOTICES.md' $output
        $licenses = Join-Path $output 'licenses'
        New-Item -ItemType Directory -Force $licenses | Out-Null
        $lock = Get-Content 'src/BlackPearl.Desktop/packages.lock.json' -Raw | ConvertFrom-Json
        $seen = @{}
        foreach ($framework in $lock.dependencies.PSObject.Properties) {
            foreach ($package in $framework.Value.PSObject.Properties) {
                $packageVersion = $package.Value.resolved
                if (!$packageVersion) { continue }
                $key = $package.Name.ToLowerInvariant() + '/' + $packageVersion
                if ($seen.ContainsKey($key)) { continue }
                $seen[$key] = $true
                $source = Join-Path $root ('.nuget/packages/' + $key)
                if (!(Test-Path $source)) { continue }
                $dest = Join-Path $licenses ($package.Name + '-' + $packageVersion)
                New-Item -ItemType Directory -Force $dest | Out-Null
                Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match 'license|copying|notice|\.nuspec$' } | Copy-Item -Destination $dest
            }
        }
    }
    $sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
    if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools/cli'
    # -Step Installer runs beside the update scripts, which restore the same project, so it reuses the Files restore.
    if ($Step -ne 'Installer' -or !(Test-Path '.nuget/packages/tools.innosetup/7.1.0')) {
        & $sdk restore packaging/Installer.csproj --configfile NuGet.Config --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Installer compiler restore failed' }
    }
    if ($Step -eq 'Files') { return }
    $compiler = Get-ChildItem '.nuget/packages/tools.innosetup/7.1.0' -Recurse -Filter ISCC.exe | Select-Object -First 1
    if (!$compiler) { throw 'Inno Setup compiler not found' }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch' }
    & $compiler.FullName /Qp "/DAppVersion=$Version" packaging/BlackPearl.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    Get-FileHash "artifacts/BlackPearl-Setup-$Version-win-x64.exe" -Algorithm SHA256 | Format-List
} finally { Pop-Location }
