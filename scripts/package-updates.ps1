# Writes the file list of the new build (Watchroom-files-VERSION-win-x64.txt: SHA-256, size and path per line) and
# builds update installers that carry only the files changed since each of the last Previous releases, from their
# published file lists. Releases from before file lists were published are read from their own installer, installed
# silently into a scratch folder on the build machine, which registers it there, so this runs on CI only. The most
# recent release is always installed that way: its update installer is applied over it and must reproduce the new
# build exactly, and the installation must match its published file list.
param([string]$Version = '', [int]$Previous = 40, [int]$InstallLimit = 3, [string]$Repository = 'Svovoniks/WatchRoom')
$ErrorActionPreference = 'Stop'

function Get-InstallerCompiler {
    param([string]$Root)
    $sdk = Join-Path $Root '.tools/dotnet/dotnet.exe'
    if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
    $env:DOTNET_CLI_HOME = Join-Path $Root '.tools/cli'
    & $sdk restore (Join-Path $Root 'packaging/Installer.csproj') --configfile (Join-Path $Root 'NuGet.Config') --locked-mode | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Installer compiler restore failed' }
    $compiler = Get-ChildItem (Join-Path $Root '.nuget/packages/tools.innosetup/7.1.0') -Recurse -Filter ISCC.exe | Select-Object -First 1
    if (!$compiler) { throw 'Inno Setup compiler not found' }
    return $compiler.FullName
}
# Relative path → SHA-256 of every file below Directory, keyed case-insensitively as Windows paths are.
# The uninstaller files Inno Setup adds to an installation are not part of the app.
function Get-FileTable {
    param([string]$Directory)
    $base = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $table = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force) {
        $relative = $file.FullName.Substring($base.Length)
        if ($relative -match '^unins\d+\.(exe|dat|msg)$') { continue }
        $table[$relative.ToLowerInvariant()] = [pscustomobject]@{ Path = $relative; Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash; Size = $file.Length }
    }
    return $table
}
# The published file list: one "SHA-256<tab>size<tab>relative path" line per file, read back by the app before it
# uses an update installer.
function Write-FileList {
    param($Table, [string]$Path)
    [IO.File]::WriteAllLines($Path, [string[]]@($Table.Values | ForEach-Object { "$($_.Hash)`t$($_.Size)`t$($_.Path)" }))
}
function Read-FileList {
    param([string]$Path)
    $table = [ordered]@{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if (!$line) { continue }
        $parts = $line.Split("`t", 3)
        if ($parts.Count -ne 3 -or $parts[0] -notmatch '^[A-Fa-f0-9]{64}$') { throw "Malformed file list $Path" }
        $table[$parts[2].ToLowerInvariant()] = [pscustomobject]@{ Path = $parts[2]; Hash = $parts[0].ToUpperInvariant(); Size = [long]$parts[1] }
    }
    return $table
}
function Assert-SameTable {
    param($Have, $Want, [string]$Name)
    $missing = @($Want.Keys | Where-Object { !$Have.Contains($_) -or $Have[$_].Hash -ne $Want[$_].Hash })
    $extra = @($Have.Keys | Where-Object { !$Want.Contains($_) })
    if ($missing.Count -or $extra.Count) {
        throw "$Name differ. Wrong or missing: $($missing -join ', '). Unexpected: $($extra -join ', ')."
    }
}
# Inno Setup reads braces as constants; a literal one is doubled.
function ConvertTo-InnoPath { param([string]$Path) return $Path.Replace('{', '{{') }
function New-UpdateInstaller {
    param([string]$Compiler, [string]$Script, [string]$From, [string]$To, $Old, [string]$NewFiles, [string]$Stage, [string]$Output, [string[]]$Defines = @())
    $new = Get-FileTable $NewFiles
    if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
    New-Item -ItemType Directory -Force $Stage | Out-Null
    $changed = @(foreach ($key in $new.Keys) { if (!$Old.Contains($key) -or $Old[$key].Hash -ne $new[$key].Hash) { $new[$key].Path } })
    foreach ($path in $changed) {
        $target = Join-Path $Stage $path
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $NewFiles $path) -Destination $target
    }
    $removed = @(foreach ($key in $Old.Keys) { if (!$new.Contains($key)) { $Old[$key].Path } })
    $deletions = Join-Path (Split-Path -Parent $Stage) ((Split-Path -Leaf $Stage) + '-removed.iss')
    $lines = @('[InstallDelete]') + @($removed | ForEach-Object { 'Type: files; Name: "{app}\' + (ConvertTo-InnoPath $_) + '"' })
    [IO.File]::WriteAllLines($deletions, [string[]]$lines)
    $arguments = @('/Qp', "/DAppVersion=$To", "/DUpdateFrom=$From", "/DAppFiles=$Stage", "/DUpdateRemoved=$deletions", "/DOutputFolder=$Output") + $Defines + @($Script)
    & $Compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "Update installer compilation from $From failed" }
    return [pscustomobject]@{ Path = Join-Path $Output "Watchroom-Update-$From-to-$To-win-x64.exe"; Changed = $changed; Removed = $removed }
}
function Invoke-Installer {
    param([string]$Path, [string[]]$Arguments)
    # Waited on directly: some sandboxes refuse Start-Process -Wait, which needs a job object. Antivirus products may
    # deny, lock or even quarantine an installer started the moment it is written, so a fresh one gets a few seconds
    # for their scan first and the first two errors are retried for a minute.
    $age = [datetime]::UtcNow - (Get-Item -LiteralPath $Path).LastWriteTimeUtc
    if ($age -lt [TimeSpan]::FromSeconds(5)) { Start-Sleep -Milliseconds ([int](5000 - $age.TotalMilliseconds)) }
    $start = [Diagnostics.ProcessStartInfo]::new($Path, ($Arguments -join ' '))
    $deadline = [datetime]::UtcNow.AddMinutes(1)
    while ($true) {
        try { $process = [Diagnostics.Process]::Start($start); break }
        catch {
            $failure = $_.Exception; while ($failure -and $failure -isnot [ComponentModel.Win32Exception]) { $failure = $failure.InnerException }
            if (!$failure -or $failure.NativeErrorCode -notin 5, 32 -or [datetime]::UtcNow -ge $deadline) { throw }
        }
        Start-Sleep -Seconds 2
    }
    if (!$process.WaitForExit(600000)) { $process.Kill($true); throw "$(Split-Path -Leaf $Path) did not finish in 10 minutes" }
    return $process.ExitCode
}
function Install-Silently {
    param([string]$Installer, [string]$Directory, [string]$Log)
    # /DIR is quoted for Inno Setup's command line parser; scratch folders have no quotes in them.
    $code = Invoke-Installer $Installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$Log`"", "/DIR=`"$Directory`"")
    if ($code -ne 0) { throw "$(Split-Path -Leaf $Installer) exited with $code; see $Log" }
}
function Uninstall-Silently {
    param([string]$Directory)
    $uninstaller = Join-Path $Directory 'unins000.exe'
    if (!(Test-Path -LiteralPath $uninstaller)) { return }
    $null = Invoke-Installer $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    # The uninstaller hands over to a copy of itself in the temp folder and returns; the copy removes the original last.
    $deadline = [datetime]::UtcNow.AddMinutes(5)
    while ((Test-Path -LiteralPath $uninstaller) -and [datetime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (Test-Path -LiteralPath $uninstaller) { throw "Uninstalling $Directory did not finish" }
}
# The installation must hold exactly the new build: every file with its hash and nothing the new version dropped.
function Assert-SameFiles {
    param([string]$Installed, [string]$Expected, [string]$Name)
    Assert-SameTable (Get-FileTable $Installed) (Get-FileTable $Expected) "$Name and the new build"
}
function Get-VerifiedAsset {
    param($Release, [string]$Name, [string]$Destination)
    $asset = @($Release.assets | Where-Object name -CEQ $Name)
    if ($asset.Count -ne 1 -or $asset[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$') { return $null }
    Invoke-WebRequest -Uri $asset[0].browser_download_url -OutFile $Destination -TimeoutSec 600
    if ('sha256:' + (Get-FileHash $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset[0].digest) { throw "$Name does not match its published digest" }
    return $Destination
}

# Dot-sourcing exposes the helpers to scripts/update-package-checks.ps1 without building anything.
if ($MyInvocation.InvocationName -eq '.') { return }
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'package-updates.ps1 installs earlier releases into the current user profile, so it runs on CI only.' }
$root = Split-Path -Parent $PSScriptRoot
if (!$Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
$current = [Version]$Version
$app = Join-Path $root 'artifacts/Watchroom'
if (!(Test-Path (Join-Path $app 'Watchroom.exe'))) { throw 'Package the app with scripts/package.ps1 first' }
$output = Join-Path $root 'artifacts'
Write-FileList (Get-FileTable $app) (Join-Path $output "Watchroom-files-$Version-win-x64.txt")
$compiler = Get-InstallerCompiler $root
$script = Join-Path $root 'packaging/Watchroom.iss'
$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'Watchroom-build' }
if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
$releases = Invoke-RestMethod "https://api.github.com/repos/$Repository/releases?per_page=100" -Headers $headers
$earlier = @($releases | Where-Object { !$_.draft -and !$_.prerelease -and $_.tag_name -match '^v(\d+\.\d+\.\d+)$' -and [Version]$Matches[1] -lt $current } |
    Sort-Object { [Version]$_.tag_name.Substring(1) } -Descending | Select-Object -First $Previous)
$scratch = Join-Path $output ('update-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
$installs = 0
try {
    foreach ($release in $earlier) {
        $from = $release.tag_name.Substring(1)
        $list = Get-VerifiedAsset $release "Watchroom-files-$from-win-x64.txt" (Join-Path $scratch "files-$from.txt")
        $newest = $release -eq $earlier[0]
        $old = if ($list) { Read-FileList $list }
        $installable = $installs -lt $InstallLimit -and @($release.assets | Where-Object name -CEQ "Watchroom-Setup-$from-win-x64.exe").Count -eq 1
        if ($old -and (!$newest -or !$installable)) {
            $update = New-UpdateInstaller $compiler $script $from $Version $old $app (Join-Path $output 'Watchroom-Update') $output
            Write-Host "Update from $from`: $($update.Changed.Count) changed files, $($update.Removed.Count) removed, $([Math]::Round((Get-Item $update.Path).Length / 1MB, 1)) MB"
            continue
        }
        if (!$installable) { Write-Host "Skipping $from`: no published file list"; continue }
        $installer = Get-VerifiedAsset $release "Watchroom-Setup-$from-win-x64.exe" (Join-Path $scratch "Watchroom-Setup-$from-win-x64.exe")
        if (!$installer) { Write-Host "Skipping $from`: no verifiable installer"; continue }
        $installs++
        $installed = Join-Path $scratch "installed-$from"
        Install-Silently $installer $installed (Join-Path $scratch "install-$from.log")
        try {
            $table = Get-FileTable $installed
            if ($old) { Assert-SameTable $table $old "The installed $from and its published file list" }
            $update = New-UpdateInstaller $compiler $script $from $Version $table $app (Join-Path $output 'Watchroom-Update') $output
            Write-Host "Update from $from`: $($update.Changed.Count) changed files, $($update.Removed.Count) removed, $([Math]::Round((Get-Item $update.Path).Length / 1MB, 1)) MB, applied"
            $code = Invoke-Installer $update.Path @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $scratch "update-$from.log")`"", "/DIR=`"$installed`"")
            if ($code -ne 0) { throw "Update installer from $from exited with $code" }
            Assert-SameFiles $installed $app "The update from $from"
        }
        finally { Uninstall-Silently $installed }
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($path in @((Join-Path $output 'Watchroom-Update'), (Join-Path $output 'Watchroom-Update-removed.iss'))) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}
