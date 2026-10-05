#Requires -Version 7.0
[CmdletBinding()]
param(
    [string[]]$Paths = @(),
    [string]$Version = '',
    [string]$Message = 'Usability improvements',
    [string]$Sdk = '',
    [switch]$Plan,
    [switch]$VerifyOnly,
    [ValidateRange(1, 120)][int]$TimeoutMinutes = 60
)
$ErrorActionPreference = 'Stop'
$repository = 'Svovoniks/WatchRoom'
$apiRoot = "https://api.github.com/repos/$repository"

function Invoke-Native {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE): $($Arguments -join ' ')" }
}
function Get-ShipVersion {
    param([string]$Requested, [string]$Latest, [string]$Source)
    foreach ($value in @($Latest, $Source)) {
        if ($value -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $value" }
    }
    $floor = if ([version]$Latest -gt [version]$Source) { [version]$Latest } else { [version]$Source }
    if (!$Requested) { $Requested = "$($floor.Major).$($floor.Minor).$($floor.Build + 1)" }
    if ($Requested -notmatch '^\d+\.\d+\.\d+$' -or [version]$Requested -le $floor) {
        throw "Version '$Requested' must have three numeric components and be higher than $floor."
    }
    return $Requested
}
function Assert-ShipPath {
    param([string]$Root, [string]$Path)
    $absolute = [IO.Path]::GetFullPath($Path, $Root)
    $relative = [IO.Path]::GetRelativePath($Root, $absolute).Replace('\', '/')
    if ($relative -match '^\.\.(?:/|$)' -or [IO.Path]::IsPathRooted($relative) -or
        $relative -match '(^|/)(sites|artifacts|\.tools|\.nuget|bin|obj|node_modules|data|logs|credentials)(/|$)' -or
        $relative -match '(?i)(^|/)(\.env[^/]*|.*\.(html?|png|jpe?g|mp4|mkv|sqlite|db|pfx|pem|key))$' -or
        $relative -notmatch '^(src/|tests/|scripts/|packaging/|\.github/workflows/|[^/]+\.(md|props|targets|slnx|ps1)|NuGet\.Config$|global\.json$)') {
        throw "Not a release source path: $Path"
    }
    if (!(Test-Path -LiteralPath $absolute)) {
        & git -C $Root ls-files --error-unmatch -- $relative 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return $relative } # Explicitly selected tracked deletion.
        throw "Source file does not exist and is not a tracked deletion: $Path"
    }
    $item = Get-Item -LiteralPath $absolute
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Select individual regular files, not directories or links: $Path"
    }
    return $relative
}
function Get-PublicJson {
    param([string]$Url)
    # Public verification deliberately does not use GH_TOKEN or a GitHub CLI session.
    Invoke-RestMethod -Uri $Url -Headers @{ 'User-Agent' = 'Watchroom-release-verification' } -TimeoutSec 60
}
function Assert-ReleaseMetadata {
    param($Release, [string]$ExpectedVersion)
    if ($Release.tag_name -cne "v$ExpectedVersion" -or $Release.draft -or $Release.prerelease) {
        throw "The public updater feed does not expose stable v$ExpectedVersion."
    }
    $name = "Watchroom-Setup-$ExpectedVersion-win-x64.exe"
    $installer = @($Release.assets | Where-Object name -CEQ $name)
    $checksums = @($Release.assets | Where-Object name -CEQ 'SHA256SUMS.txt')
    if ($installer.Count -ne 1 -or $installer[0].size -le 0 -or $installer[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$' -or $checksums.Count -ne 1) {
        throw 'Missing installer, size, SHA-256 digest, or checksum asset.'
    }
    foreach ($asset in @($installer[0], $checksums[0])) {
        $expected = "https://github.com/Svovoniks/WatchRoom/releases/download/v$ExpectedVersion/$($asset.name)"
        if ($asset.browser_download_url -cne $expected) { throw 'Unexpected release asset URL.' }
    }
    return @{ Installer = $installer[0]; Checksums = $checksums[0] }
}
function Assert-InstallerBytes {
    param([string]$InstallerPath, [string]$ChecksumPath, $Asset)
    $hash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Item -LiteralPath $InstallerPath).Length -ne $Asset.size -or "sha256:$hash" -cne $Asset.digest) {
        throw 'Installer size or GitHub SHA-256 digest mismatch.'
    }
    $pattern = '(?m)^' + [regex]::Escape($hash) + '\s+\*?' + [regex]::Escape($Asset.name) + '\s*$'
    if ((Get-Content -LiteralPath $ChecksumPath -Raw) -notmatch $pattern) { throw 'SHA256SUMS.txt mismatch.' }
    return $hash
}
function Wait-ReleaseBuild {
    param([string]$ExpectedVersion, [string]$Commit, [datetime]$Deadline)
    $run = $null
    $lastStatus = ''
    while ([datetime]::UtcNow -lt $Deadline) {
        if (!$run) {
            $runs = Get-PublicJson "$apiRoot/actions/runs?head_sha=$Commit&event=push&per_page=100"
            $run = $runs.workflow_runs | Where-Object {
                $_.head_branch -ceq "v$ExpectedVersion" -and $_.head_sha -ceq $Commit -and $_.name -ceq 'Build and release'
            } | Sort-Object run_attempt -Descending | Select-Object -First 1
        }
        else { $run = Get-PublicJson "$apiRoot/actions/runs/$($run.id)" }
        $status = if ($run) { "$($run.status) $($run.conclusion)" } else { 'Waiting for tag workflow' }
        if ($status -ne $lastStatus) { Write-Host $status; $lastStatus = $status }
        if ($run.status -eq 'completed') {
            if ($run.conclusion -ne 'success') { throw "Release build failed: $($run.html_url). Fix it before shipping a higher version; do not move the tag." }
            return $run
        }
        Start-Sleep -Seconds 45
    }
    throw "Timed out waiting for the tag build. Recheck with -VerifyOnly -Version $ExpectedVersion."
}
function Test-PublishedRelease {
    param([string]$ExpectedVersion, [string]$Destination, [datetime]$Deadline)
    $release = $null
    do {
        $release = Get-PublicJson "$apiRoot/releases/latest"
        if ($release.tag_name -ceq "v$ExpectedVersion") { break }
        if ([datetime]::UtcNow -ge $Deadline) { throw "Public latest release is $($release.tag_name), expected v$ExpectedVersion." }
        Start-Sleep -Seconds 15
    } while ($true)
    $assets = Assert-ReleaseMetadata $release $ExpectedVersion
    $installerPath = Join-Path $Destination $assets.Installer.name
    $checksumPath = Join-Path $Destination 'SHA256SUMS.txt'
    Invoke-WebRequest -Uri $assets.Checksums.browser_download_url -OutFile $checksumPath -TimeoutSec 600
    Write-Host "Downloading $($assets.Installer.name) for verification…"
    Invoke-WebRequest -Uri $assets.Installer.browser_download_url -OutFile $installerPath -TimeoutSec 600
    $hash = Assert-InstallerBytes $installerPath $checksumPath $assets.Installer
    $url = "https://github.com/$repository/releases/tag/v$ExpectedVersion"
    $page = Invoke-WebRequest -Uri $url -TimeoutSec 60
    $links = Invoke-WebRequest -Uri "https://github.com/$repository/releases/expanded_assets/v$ExpectedVersion" -TimeoutSec 60
    foreach ($asset in @($assets.Installer, $assets.Checksums)) {
        if (!$links.Content.Contains("/releases/download/v$ExpectedVersion/$($asset.name)")) { throw 'Public release page is missing asset links.' }
    }
    if ($page.StatusCode -ne 200) { throw 'Public release page is unavailable.' }
    return @{ Version = $ExpectedVersion; Release = $url; Sha256 = $hash; Installer = $installerPath }
}

# Dot-sourcing exposes helpers for offline safety checks without starting a release.
if ($MyInvocation.InvocationName -eq '.') { return }
$root = Split-Path -Parent $PSScriptRoot
$originalPath = $env:PATH
$checkout = $null
Push-Location $root
try {
    $remote = Invoke-Native git @('remote', 'get-url', 'origin')
    if ($remote.Trim() -notmatch '^(https://github\.com/|git@github\.com:)Svovoniks/WatchRoom(?:\.git)?$') { throw 'origin must be the public Svovoniks/WatchRoom repository.' }
    Invoke-Native git @('fetch', 'origin', '--tags')
    $head = (Invoke-Native git @('rev-parse', 'HEAD')).Trim()
    $releaseHead = (Invoke-Native git @('rev-parse', 'origin/master')).Trim()
    if (!$VerifyOnly -and $head -cne $releaseHead) {
        throw 'Start from origin/master before shipping. This prevents releasing an old checkout or unrelated local commits.'
    }
    if ($VerifyOnly) {
        if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '-VerifyOnly requires -Version major.minor.patch.' }
        $commit = (Invoke-Native git @('rev-parse', "v$Version^{commit}")).Trim()
    }
    else {
        if (!$Paths.Count) { throw 'Pass -Paths with the individual files to ship.' }
        $selected = @($Paths | ForEach-Object { Assert-ShipPath $root $_ } | Sort-Object -Unique)
        if ($selected -notcontains 'Directory.Build.props') { $selected += 'Directory.Build.props' }
        $latest = Get-PublicJson "$apiRoot/releases/latest"
        $source = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
        $Version = Get-ShipVersion $Version ($latest.tag_name -replace '^v', '') $source
        $tag = "v$Version"
        & git show-ref --verify --quiet "refs/tags/$tag"
        if ($LASTEXITCODE -eq 0) { throw "Tag $tag already exists. Choose a higher version." }
        Write-Host "Release $tag from $head with these files:"
        $selected | ForEach-Object { Write-Host "  $_" }
        if ($Plan) { Write-Host 'Preview complete. No files, commits, tags, or releases changed.'; return }
        if (!$Sdk) {
            $Sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
            if (!(Test-Path -LiteralPath $Sdk)) {
                $commonGit = (Invoke-Native git @('rev-parse', '--path-format=absolute', '--git-common-dir')).Trim()
                $Sdk = Join-Path (Split-Path -Parent $commonGit) '.tools/dotnet/dotnet.exe'
                if (!(Test-Path -LiteralPath $Sdk)) { $Sdk = (Get-Command dotnet -ErrorAction Stop).Source }
            }
        }
        $Sdk = (Resolve-Path -LiteralPath $Sdk).Path
        $env:PATH = (Split-Path -Parent $Sdk) + [IO.Path]::PathSeparator + $env:PATH
    }
    $record = Join-Path $root ('artifacts/ship-' + $Version + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $record | Out-Null
    if (!$VerifyOnly) {
        $patch = Join-Path $record 'selected.patch'
        Invoke-Native git (@('diff', 'HEAD', '--binary', "--output=$patch", '--') + $selected)
        # SQLite fixture paths can exceed legacy Windows limits under a nested worktree.
        $checkout = Join-Path ([IO.Path]::GetTempPath()) ('watchroom-ship-' + [guid]::NewGuid().ToString('N'))
        Invoke-Native git @('worktree', 'add', '--detach', $checkout, 'origin/master')
        if ((Get-Item -LiteralPath $patch).Length -gt 0) { Invoke-Native git @('-C', $checkout, 'apply', '--index', $patch) }
        foreach ($path in $selected) {
            & git ls-files --error-unmatch -- $path 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) {
                $target = Join-Path $checkout $path
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                Copy-Item -LiteralPath (Join-Path $root $path) -Destination $target
            }
        }
        Push-Location $checkout
        try {
            $propsPath = Join-Path $checkout 'Directory.Build.props'
            $props = Get-Content -LiteralPath $propsPath -Raw
            if ([regex]::Matches($props, '<Version>[^<]+</Version>').Count -ne 1) { throw 'Expected exactly one Version property.' }
            [IO.File]::WriteAllText($propsPath, [regex]::Replace($props, '<Version>[^<]+</Version>', "<Version>$Version</Version>"))
            Invoke-Native git (@('add', '--') + $selected)
            Invoke-Native git @('diff', '--cached', '--check')
            $packagePath = Join-Path $root '.nuget/packages'
            Invoke-Native $Sdk @('restore', 'Watchroom.slnx', '--locked-mode', "-p:RestorePackagesPath=$packagePath")
            Invoke-Native $Sdk @('build', 'Watchroom.slnx', '-c', 'Release', '--no-restore', "-p:RestorePackagesPath=$packagePath")
            Invoke-Native $Sdk @('run', '--project', 'tests/Watchroom.UpdateChecks', '-c', 'Release', '--no-build')
            foreach ($check in @('--metadata-cache', '--shared-library')) {
                Invoke-Native $Sdk @('run', '--project', 'tests/Watchroom.Smoke', '-c', 'Release', '--no-build', '--', $check)
            }
            Invoke-Native $Sdk @('run', '--project', 'tests/Watchroom.DesktopChecks', '-c', 'Release', '--no-build')
            foreach ($check in @('--ui-findings', '--guest-library-layout', '--host-library-browse', '--player-layout')) {
                Invoke-Native $Sdk @('run', '--project', 'tests/Watchroom.DesktopChecks', '-c', 'Release', '--no-build', '--', $check)
            }
            # Some nested metadata fixtures also create long SQLite backup filenames.
            $freshSmoke = Join-Path ([IO.Path]::GetTempPath()) ('wr-smoke-' + [guid]::NewGuid().ToString('N'))
            Invoke-Native $Sdk @('run', '--project', 'tests/Watchroom.Smoke', '-c', 'Release', '--no-build', '--', $freshSmoke)
            Invoke-Native git @('diff', '--exit-code')
            Invoke-Native git @('fetch', 'origin')
            if ((Invoke-Native git @('rev-parse', 'origin/master')).Trim() -cne $releaseHead) { throw 'master advanced during testing. Integrate it and rerun shipping.' }
            Invoke-Native git @('commit', '-m', "Release Watchroom ${Version}: $Message")
            $commit = (Invoke-Native git @('rev-parse', 'HEAD')).Trim()
            Invoke-Native git @('push', 'origin', 'HEAD:master')
            Invoke-Native git @('tag', '-a', "v$Version", '-m', "Watchroom ${Version}: $Message")
            Invoke-Native git @('push', 'origin', "v$Version")
        }
        finally { Pop-Location }
    }
    $deadline = [datetime]::UtcNow.AddMinutes($TimeoutMinutes)
    $run = Wait-ReleaseBuild $Version $commit $deadline
    $result = Test-PublishedRelease $Version $record $deadline
    $result.Actions = $run.html_url
    $result.Commit = $commit
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $record 'verified.json')
    if ($checkout) { Invoke-Native git @('worktree', 'remove', $checkout) }
    $outcome = if ($VerifyOnly) { 'Verified existing release' } else { 'Shipped' }
    Write-Host "$outcome Watchroom ${Version}: $($result.Release)"
    Write-Host "Actions: $($run.html_url)"
    Write-Host "Verified SHA-256: $($result.Sha256)"
    Write-Host "Verification record: $record"
}
catch {
    if ($checkout) { Write-Warning "Release checkout retained at $checkout. If the tag was pushed, recheck with -VerifyOnly -Version $Version. Never move a published tag." }
    throw
}
finally { $env:PATH = $originalPath; Pop-Location }
