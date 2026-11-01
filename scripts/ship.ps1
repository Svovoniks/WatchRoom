#Requires -Version 7.0
# Ships an app update through a reviewed pull request; see UPDATES.md.
#   ship.ps1 -Message ... -BodyFile ...   check the branch, push it, and open or update its pull request
#   ship.ps1 -Merge                       merge the approved pull request, tag it, and verify the release
#   ship.ps1 -NoRelease [-Merge]          the same for a change the user does not want released: no version bump, tag, or release
#   ship.ps1 -VerifyOnly -Version x.y.z   finish verifying an already pushed tag
[CmdletBinding()]
param(
    [string]$Message = '',
    [string]$BodyFile = '',
    [string]$Title = '',
    [string]$Version = '',
    [string]$Sdk = '',
    [int]$Pr = 0,
    [switch]$Plan,
    [switch]$Merge,
    [switch]$NoRelease,
    [switch]$VerifyOnly,
    [ValidateRange(1, 120)][int]$TimeoutMinutes = 60
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'github-pr.ps1')

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
function Get-PullRequestVersion {
    param([string]$Requested, [string]$Latest, [string]$MasterVersion, [string]$BranchVersion)
    # A branch already above every release keeps its version across review rounds.
    if (!$Requested -and $BranchVersion -match '^\d+\.\d+\.\d+$' -and
        [version]$BranchVersion -gt [version]$Latest -and [version]$BranchVersion -gt [version]$MasterVersion) {
        return $BranchVersion
    }
    return Get-ShipVersion $Requested $Latest $MasterVersion
}
function Get-PropsVersion {
    param([string]$Root, [string]$Revision)
    $props = (Invoke-Native git @('-C', $Root, 'show', "${Revision}:Directory.Build.props")) -join "`n"
    return ([xml]$props).Project.PropertyGroup.Version
}
function Assert-ShipPathName {
    param([string]$Path)
    $relative = $Path.Replace('\', '/')
    # The approved attribution logo and postplay promo belong to the app, unlike local
    # reports and generated media. Keep the exception to these exact source paths.
    $attributionLogo = $relative -ceq 'src/Watchroom.Desktop/Assets/Tmdb.png' -or $relative -ceq 'src/Watchroom.Desktop/Assets/Promo/teaser-clam-intro.mp4'
    if ($relative -match '^\.\.(?:/|$)' -or [IO.Path]::IsPathRooted($relative) -or
        $relative -match '(^|/)(sites|artifacts|\.tools|\.nuget|bin|obj|node_modules|data|logs|credentials)(/|$)' -or
        ($relative -match '(?i)(^|/)(\.env[^/]*|.*\.(html?|png|jpe?g|mp4|mkv|sqlite|db|pfx|pem|key|env))$' -and !$attributionLogo) -or
        $relative -notmatch '^(src/|tests/|scripts/|packaging/|deploy/|\.gitattributes$|\.github/workflows/|\.claude/skills/|[^/]+\.(md|props|targets|slnx|ps1)|NuGet\.Config$|global\.json$)') {
        throw "Not a release source path: $Path"
    }
    return $relative
}
function Get-CheckRecordPath {
    param([string]$Root, [string]$Commit)
    # Shared by every worktree, so -Merge can run anywhere and still see what was checked.
    $common = (Invoke-Native git @('-C', $Root, 'rev-parse', '--path-format=absolute', '--git-common-dir')).Trim()
    return Join-Path $common "watchroom-checks/$Commit.json"
}
# Voice libraries and models are fetched or built once per checkout (see Watchroom.Desktop.csproj), and each check
# copy is a fresh checkout. Like the CI cache, this keeps them between runs while their pinned inputs are unchanged.
function Get-BuildCacheEntries {
    param([string]$Checkout)
    $common = (Invoke-Native git @('-C', $Checkout, 'rev-parse', '--path-format=absolute', '--git-common-dir')).Trim()
    $project = (Invoke-Native git @('-C', $Checkout, 'rev-parse', 'HEAD:src/Watchroom.Desktop/Watchroom.Desktop.csproj')).Trim()
    $native = (Invoke-Native git @('-C', $Checkout, 'rev-parse', 'HEAD:src/native')).Trim()
    return @(
        [pscustomobject]@{ Path = Join-Path $Checkout '.tools/native'; Files = '*.dll'; Kind = 'native'; Cache = Join-Path $common "watchroom-build-cache/native-$native-$project" },
        [pscustomobject]@{ Path = Join-Path $Checkout '.tools/models'; Files = '*'; Kind = 'models'; Cache = Join-Path $common "watchroom-build-cache/models-$project" })
}
function Restore-BuildCache {
    param([string]$Checkout)
    foreach ($entry in Get-BuildCacheEntries $Checkout) {
        if (!(Test-Path -LiteralPath $entry.Cache)) { continue }
        try {
            New-Item -ItemType Directory -Force -Path $entry.Path | Out-Null
            Copy-Item -Path (Join-Path $entry.Cache '*') -Destination $entry.Path -Recurse -Force
        }
        catch {
            # Another run replaced the entry mid-copy; the build fetches everything again.
            Remove-Item -LiteralPath $entry.Path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
function Save-BuildCache {
    param([string]$Checkout)
    foreach ($entry in Get-BuildCacheEntries $Checkout) {
        if ((Test-Path -LiteralPath $entry.Cache) -or !(Test-Path -LiteralPath $entry.Path)) { continue }
        $partial = "$($entry.Cache).partial-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Force -Path $partial | Out-Null
        Copy-Item -Path (Join-Path $entry.Path $entry.Files) -Destination $partial -Recurse
        try { Move-Item -LiteralPath $partial -Destination $entry.Cache }
        catch { Remove-Item -LiteralPath $partial -Recurse -Force; continue } # another run saved it first
        # Only the newest inputs are kept.
        Get-ChildItem -LiteralPath (Split-Path -Parent $entry.Cache) -Directory -Filter "$($entry.Kind)-*" |
            Where-Object { $_.FullName -ne $entry.Cache -and $_.Name -notlike '*.partial-*' } | Remove-Item -Recurse -Force
    }
}
function Invoke-ReleaseChecks {
    param([string]$Checkout, [string]$Sdk, [string]$PackagePath)
    Push-Location $Checkout
    try {
        Restore-BuildCache $Checkout
        Invoke-Native $Sdk @('restore', 'Watchroom.slnx', '--locked-mode', "-p:RestorePackagesPath=$PackagePath")
        Invoke-Native $Sdk @('build', 'Watchroom.slnx', '-c', 'Release', '--no-restore', "-p:RestorePackagesPath=$PackagePath")
        Save-BuildCache $Checkout
        # The main desktop suite's input responsiveness gates are timing-sensitive, so it runs alone first.
        Invoke-Native $Sdk @('run', '--project', 'tests/Watchroom.DesktopChecks', '-c', 'Release', '--no-build')
        # The full smoke suite, the longest, then runs in the background beside the other checks. It includes the
        # metadata cache and shared library transport checks. Some nested metadata fixtures create long SQLite backup
        # filenames, so it gets a short directory under the temp folder.
        $freshSmoke = Join-Path ([IO.Path]::GetTempPath()) ('wr-smoke-' + [guid]::NewGuid().ToString('N'))
        $smokeLog = "$freshSmoke.log"
        $smoke = Start-Process -FilePath $Sdk -ArgumentList @('run', '--project', 'tests/Watchroom.Smoke', '-c', 'Release', '--no-build', '--', "`"$freshSmoke`"") `
            -WorkingDirectory $Checkout -RedirectStandardOutput $smokeLog -RedirectStandardError "$smokeLog.err" -NoNewWindow -PassThru
        $null = $smoke.Handle # keeps the exit code readable after the process ends
        Invoke-Native $Sdk @('run', '--project', 'tests/Watchroom.UpdateChecks', '-c', 'Release', '--no-build')
        Invoke-Native $Sdk @('run', '--project', 'tests/Watchroom.Smoke', '-c', 'Release', '--no-build', '--', '--tmdb-access')
        foreach ($check in @('--acquisition-layout', '--home', '--ui-findings', '--ui-experience', '--guest-library-layout', '--host-library-browse', '--player-layout', '--language-detection', '--voice-processing')) {
            Invoke-Native $Sdk @('run', '--project', 'tests/Watchroom.DesktopChecks', '-c', 'Release', '--no-build', '--', $check)
        }
        Write-Host 'Waiting for the full smoke suite...'
        $smoke.WaitForExit()
        Get-Content -LiteralPath $smokeLog, "$smokeLog.err" -Tail 20 | Write-Host
        if ($smoke.ExitCode -ne 0) { throw "The full smoke suite failed with exit code $($smoke.ExitCode). Output: $smokeLog" }
        Invoke-Native git @('diff', '--exit-code')
    }
    finally {
        if ($smoke -and !$smoke.HasExited) { $smoke.Kill($true) }
        Pop-Location
    }
}
function Get-SyncTree {
    param([string]$Root, [string]$Base, [string]$Master, [string]$Local)
    $merge = @(Invoke-Native git @('-C', $Root, 'merge-tree', '--write-tree', '--no-messages', "--merge-base=$Base", $Master, $Local))
    if (!$merge.Count -or $merge[0] -notmatch '^[a-f0-9]{40,64}$') { throw 'Git did not return a merged tree for local work.' }
    return $merge[0]
}
function Assert-SyncUntracked {
    param([string]$Root, [string]$Head, [string]$Backup, [string[]]$Paths, [string[]]$Selected)
    foreach ($path in $Paths) {
        & git -C $Root cat-file -e "${Head}:$path" 2>$null
        if ($LASTEXITCODE -eq 0 -and $Selected -notcontains $path) {
            $saved = Join-Path (Join-Path $Backup 'untracked') $path
            $savedHash = (Invoke-Native git @('-C', $Root, 'hash-object', "--path=$path", '--', $saved)).Trim()
            $remoteHash = (Invoke-Native git @('-C', $Root, 'rev-parse', "${Head}:$path")).Trim()
            if ($savedHash -cne $remoteHash) { throw "Untracked file conflicts with master: $path" }
        }
    }
}
function Sync-ShippedMaster {
    param([string]$Root, [string]$PublishedCommit, [string]$Destination, [string[]]$Selected = @())
    Invoke-Native git @('-C', $Root, 'fetch', '--no-recurse-submodules', 'origin', 'master') | Out-Null
    $remoteHead = (Invoke-Native git @('-C', $Root, 'rev-parse', 'origin/master')).Trim()
    Invoke-Native git @('-C', $Root, 'merge-base', '--is-ancestor', $PublishedCommit, $remoteHead) | Out-Null
    # A ship command can run in a detached/feature checkout. Update the checkout
    # that actually owns master, without switching or rewriting another branch.
    $masterRoot = $null; $candidate = $null
    foreach ($line in @(Invoke-Native git @('-C', $Root, 'worktree', 'list', '--porcelain'))) {
        if ($line.StartsWith('worktree ')) { $candidate = $line.Substring(9) }
        elseif ($line -ceq 'branch refs/heads/master') { $masterRoot = $candidate }
    }
    if (!$masterRoot) {
        & git -C $Root show-ref --verify --quiet refs/heads/master
        if ($LASTEXITCODE -eq 0) {
            Invoke-Native git @('-C', $Root, 'merge-base', '--is-ancestor', 'master', $remoteHead) | Out-Null
        }
        elseif ($LASTEXITCODE -ne 1) { throw 'Could not inspect local master.' }
        Invoke-Native git @('-C', $Root, 'fetch', '--no-recurse-submodules', 'origin', 'master:master') | Out-Null
        $syncedHead = (Invoke-Native git @('-C', $Root, 'rev-parse', 'master')).Trim()
        Invoke-Native git @('-C', $Root, 'merge-base', '--is-ancestor', $PublishedCommit, $syncedHead) | Out-Null
        return @{ Commit = $syncedHead; Checkout = $null; RecoveryStash = $null }
    }
    $masterRoot = [IO.Path]::GetFullPath($masterRoot)
    $base = (Invoke-Native git @('-C', $masterRoot, 'rev-parse', 'HEAD')).Trim()
    Invoke-Native git @('-C', $masterRoot, 'merge-base', '--is-ancestor', $base, $remoteHead) | Out-Null
    $dirty = @(Invoke-Native git @('-C', $masterRoot, 'status', '--porcelain', '--ignore-submodules=all'))
    $staged = @(Invoke-Native git @('-C', $masterRoot, 'diff', '--cached', '--name-only', '--ignore-submodules=none'))
    $stash = $null; $restored = $false
    $untracked = @(); $backup = Join-Path $Destination 'local-master-sync'
    try {
        if ($dirty.Count -or $staged.Count) {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            # Save raw untracked bytes as well as Git's recovery snapshot. This
            # also handles new release files that already exist after the pull.
            $names = (Invoke-Native git @('-C', $masterRoot, 'ls-files', '--others', '--exclude-standard', '-z')) -join "`n"
            $untracked = @($names.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
            foreach ($path in $untracked) {
                $source = [IO.Path]::GetFullPath($path, $masterRoot)
                if (!$source.StartsWith($masterRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Untracked path is outside the master checkout.' }
                $item = Get-Item -LiteralPath $source
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Cannot safely snapshot untracked link: $path" }
                $saved = Join-Path (Join-Path $backup 'untracked') $path
                New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
                Copy-Item -LiteralPath $source -Destination $saved
            }
            Invoke-Native git @('-C', $masterRoot, 'stash', 'push', '--include-untracked', '-m', "Before syncing verified release $PublishedCommit") | Out-Null
            $stash = (Invoke-Native git @('-C', $masterRoot, 'rev-parse', 'stash@{0}')).Trim()
            $stash | Set-Content -LiteralPath (Join-Path $backup 'recovery-stash.txt')
            # Merge worktree and index independently: unrelated staged edits stay
            # staged, and already-shipped edits disappear instead of reappearing.
            $workTree = Get-SyncTree $masterRoot $base $remoteHead $stash
            $indexTree = Get-SyncTree $masterRoot $base $remoteHead "$stash^2"
            Assert-SyncUntracked $masterRoot $remoteHead $backup $untracked $Selected
        }
        Invoke-Native git @('-C', $masterRoot, 'pull', '--ff-only', '--no-recurse-submodules', 'origin', 'master') | Out-Null
        $syncedHead = (Invoke-Native git @('-C', $masterRoot, 'rev-parse', 'HEAD')).Trim()
        if ($stash) {
            # Account for master advancing again between preflight and the pull.
            if ($syncedHead -cne $remoteHead) {
                $workTree = Get-SyncTree $masterRoot $base $syncedHead $stash
                $indexTree = Get-SyncTree $masterRoot $base $syncedHead "$stash^2"
                Assert-SyncUntracked $masterRoot $syncedHead $backup $untracked $Selected
            }
            Invoke-Native git @('-C', $masterRoot, 'restore', "--source=$workTree", '--staged', '--worktree', '--', '.') | Out-Null
            Invoke-Native git @('-C', $masterRoot, 'read-tree', $indexTree) | Out-Null
            foreach ($path in $untracked) {
                $saved = Join-Path (Join-Path $backup 'untracked') $path
                $target = Join-Path $masterRoot $path
                # For selected new source files, retain any edits made while the
                # release was building as a worktree diff against the new master.
                if (!(Test-Path -LiteralPath $target) -or $Selected -contains $path) {
                    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                    Copy-Item -LiteralPath $saved -Destination $target
                }
            }
            $restored = $true
        }
        Invoke-Native git @('-C', $masterRoot, 'merge-base', '--is-ancestor', $PublishedCommit, 'HEAD') | Out-Null
        return @{ Commit = $syncedHead; Checkout = $masterRoot; RecoveryStash = $stash }
    }
    catch {
        $failure = $_
        # Preflight/network failures before advancing master can restore the exact
        # original index and worktree. Keep the stash even after a successful sync.
        $restoreFailure = ''
        if ($stash -and !$restored -and (Invoke-Native git @('-C', $masterRoot, 'rev-parse', 'HEAD')).Trim() -ceq $base) {
            try { Invoke-Native git @('-C', $masterRoot, 'stash', 'apply', '--index', $stash) | Out-Null }
            catch { $restoreFailure = " Automatic restoration also failed: $_" }
        }
        $recovery = if ($stash) { " Local work is saved in stash $stash and $backup." } else { '' }
        throw "Local master synchronization failed: $failure$recovery$restoreFailure"
    }
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
    # Update installers carry only the files changed since an earlier version and are verified like the full one.
    $updates = @($Release.assets | Where-Object name -CMatch "^Watchroom-Update-\d+\.\d+\.\d+-to-$([regex]::Escape($ExpectedVersion))-win-x64\.exe$")
    foreach ($update in $updates) {
        if ($update.size -le 0 -or $update.digest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw "Missing size or SHA-256 digest for $($update.name)." }
    }
    # The file list lets later releases build update installers from this one and lets the app check its files.
    $files = @($Release.assets | Where-Object name -CEQ "Watchroom-files-$ExpectedVersion-win-x64.txt")
    if ($files.Count -ne 1 -or $files[0].size -le 0 -or $files[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$') { throw 'Missing file list, size, or SHA-256 digest.' }
    $updates = @($updates) + $files
    foreach ($asset in @($installer[0], $checksums[0]) + $updates) {
        $expected = "https://github.com/Svovoniks/WatchRoom/releases/download/v$ExpectedVersion/$($asset.name)"
        if ($asset.browser_download_url -cne $expected) { throw 'Unexpected release asset URL.' }
    }
    return @{ Installer = $installer[0]; Checksums = $checksums[0]; Updates = $updates }
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
    foreach ($update in $assets.Updates) {
        $updatePath = Join-Path $Destination $update.name
        Invoke-WebRequest -Uri $update.browser_download_url -OutFile $updatePath -TimeoutSec 600
        $null = Assert-InstallerBytes $updatePath $checksumPath $update
    }
    $url = "https://github.com/$repository/releases/tag/v$ExpectedVersion"
    $page = Invoke-WebRequest -Uri $url -TimeoutSec 60
    $links = Invoke-WebRequest -Uri "https://github.com/$repository/releases/expanded_assets/v$ExpectedVersion" -TimeoutSec 60
    foreach ($asset in @($assets.Installer, $assets.Checksums) + $assets.Updates) {
        if (!$links.Content.Contains("/releases/download/v$ExpectedVersion/$($asset.name)")) { throw 'Public release page is missing asset links.' }
    }
    if ($page.StatusCode -ne 200) { throw 'Public release page is unavailable.' }
    return @{ Version = $ExpectedVersion; Release = $url; Sha256 = $hash; Installer = $installerPath; Updates = @($assets.Updates | ForEach-Object { "$($_.name) ($([Math]::Round($_.size / 1MB, 1)) MB)" }) }
}


# Dot-sourcing exposes helpers for offline safety checks without starting a release.
if ($MyInvocation.InvocationName -eq '.') { return }
$root = Split-Path -Parent $PSScriptRoot
# Records and sync backups go to the main checkout, which outlives agent worktrees.
$mainRoot = Split-Path -Parent (Invoke-Native git @('-C', $root, 'rev-parse', '--path-format=absolute', '--git-common-dir')).Trim()
$originalPath = $env:PATH
$checkout = $null
Push-Location $root
try {
    $remote = Invoke-Native git @('remote', 'get-url', 'origin')
    if ($remote.Trim() -notmatch '^(https://github\.com/|git@github\.com:)Svovoniks/WatchRoom(?:\.git)?$') { throw 'origin must be the public Svovoniks/WatchRoom repository.' }
    Invoke-Native git @('fetch', '--no-recurse-submodules', 'origin', '--tags')
    $deadline = [datetime]::UtcNow.AddMinutes($TimeoutMinutes)
    if ($VerifyOnly) {
        if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '-VerifyOnly requires -Version major.minor.patch.' }
        $commit = (Invoke-Native git @('rev-parse', "v$Version^{commit}")).Trim()
    }
    elseif ($Merge) {
        if ($Pr) { $pull = Invoke-GitHub "/pulls/$Pr" }
        else {
            $branch = (Invoke-Native git @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
            $pull = Find-BranchPullRequest $branch
            if (!$pull) { throw "No open pull request for branch $branch. Pass -Pr." }
        }
        if ($pull.state -ne 'open' -or $pull.draft -or $pull.base.ref -cne 'master' -or $pull.head.repo.full_name -cne $repository) {
            throw 'Only an open, non-draft pull request from Svovoniks/WatchRoom into master can be shipped.'
        }
        $number = $pull.number; $head = $pull.head.sha
        Invoke-Native git @('fetch', '--no-recurse-submodules', 'origin', "pull/$number/head")
        if ((Invoke-Native git @('rev-parse', 'FETCH_HEAD')).Trim() -cne $head) { throw 'The pull request head changed while it was being read. Rerun -Merge.' }
        if (!(Test-Path -LiteralPath (Get-CheckRecordPath $root $head))) {
            throw "Release checks have not passed on $head. Rerun ship.ps1 from the pull request's worktree; it checks and pushes the branch."
        }
        & git merge-base --is-ancestor origin/master $head
        if ($LASTEXITCODE -ne 0) { throw 'master advanced since this pull request was checked. Merge origin/master into the branch, rerun ship.ps1, and request a re-review.' }
        $approval = Assert-ReviewApproval (Get-PullRequestComments $number) $head
        $headVersion = Get-PropsVersion $root $head
        if ($NoRelease) {
            # An unreleased merge must not leave master claiming a version that was never published.
            if ($headVersion -cne (Get-PropsVersion $root 'origin/master')) { throw "The pull request sets version $headVersion. Merge it without -NoRelease, or drop the version change." }
            $mergeTitle = "$($pull.title) (#$number)"
            Write-Host "Merge #$number ($head) without a release, approved in $($approval.Url)"
        }
        else {
            $Version = $headVersion
            $latest = (Get-PublicJson "$apiRoot/releases/latest").tag_name -replace '^v', ''
            if ([version]$Version -le [version]$latest) { throw "The pull request ships $Version, but $latest is already published. Rerun ship.ps1 to raise the version, then request a re-review." }
            & git show-ref --verify --quiet "refs/tags/v$Version"
            if ($LASTEXITCODE -eq 0) { throw "Tag v$Version already exists. Rerun ship.ps1 with a higher -Version, then request a re-review." }
            $note = Get-ReleaseNote $pull.body
            if (!$note) { $note = $pull.title }
            $mergeTitle = "Release Watchroom ${Version}: $note"
            Write-Host "Merge #$number ($head) as Watchroom $Version, approved in $($approval.Url)"
        }
        if ($Plan) { Write-Host 'Preview complete. Nothing was merged, tagged, or released.'; return }
        $checks = $null
        while (!$checks) {
            $state = Get-HeadCheckState (Invoke-GitHub "/actions/runs?head_sha=$head&per_page=100").workflow_runs $head
            if ($state.Detail -ne $lastState) { Write-Host "Pull request checks: $($state.Detail)"; $lastState = $state.Detail }
            if ($state.State -eq 'failure') { throw "$($state.Detail). Fix the failure, or rerun the failed jobs only if the failure is unrelated to this change, then rerun -Merge." }
            if ($state.State -eq 'success') { $checks = $state; break }
            if ([datetime]::UtcNow -ge $deadline) { throw 'Timed out waiting for pull request checks. Rerun -Merge.' }
            Start-Sleep -Seconds 30
        }
        # Merge only the exact reviewed head, and only while it still contains master.
        Invoke-Native git @('fetch', '--no-recurse-submodules', 'origin', 'master')
        & git merge-base --is-ancestor origin/master $head
        if ($LASTEXITCODE -ne 0) { throw 'master advanced while waiting for checks. Merge origin/master into the branch, rerun ship.ps1, and request a re-review.' }
        # The reviewer may have withdrawn approval while CI ran.
        $approval = Assert-ReviewApproval (Get-PullRequestComments $number) $head
        $merged = Invoke-GitHub "/pulls/$number/merge" -Method Put -Body @{
            merge_method = 'squash'; sha = $head
            commit_title = $mergeTitle
            commit_message = "Reviewed and merged in #$number."
        }
        $commit = $merged.sha
        try {
            Invoke-Native git @('fetch', '--no-recurse-submodules', 'origin', 'master')
            Invoke-Native git @('merge-base', '--is-ancestor', $commit, 'origin/master') | Out-Null
            if ((Invoke-Native git @('rev-parse', "$commit^{tree}")).Trim() -cne (Invoke-Native git @('rev-parse', "$head^{tree}")).Trim()) {
                throw "The merged tree differs from the reviewed head $head, so it was not tagged."
            }
            if (!$NoRelease) {
                Invoke-Native git @('tag', '-a', "v$Version", $commit, '-m', "Watchroom ${Version}: $note")
                Invoke-Native git @('push', 'origin', "v$Version")
            }
        }
        catch {
            # Rerunning -Merge cannot help once the pull request is merged.
            $recover = if ($NoRelease) { 'Inspect master.' } else { "If v$Version is not on GitHub and $commit has the reviewed head's tree, tag $commit as v$Version, push the tag, and run -VerifyOnly -Version $Version. Otherwise ship a higher version once master is correct." }
            throw "Pull request #$number is merged as $commit, but finishing failed: $_ $recover"
        }
        try { Invoke-GitHub "/git/refs/heads/$($pull.head.ref)" -Method Delete | Out-Null }
        catch { Write-Warning "Could not delete merged branch $($pull.head.ref): $($_.Exception.Message)" }
        if ($NoRelease) {
            $record = Join-Path $mainRoot ('artifacts/merge-' + $number + '-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $record -Force | Out-Null
            try { $synced = Sync-ShippedMaster $root $commit $record }
            catch { throw "Pull request #$number is merged as $commit, but local master was not synchronized. $_" }
            Write-Host "Merged #$number without a release: $commit"
            Write-Host "Local master synchronized: $($synced.Commit)"
            return
        }
    }
    else {
        # Open or update the pull request for this worktree's branch.
        $branch = (Invoke-Native git @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
        if ($branch -in 'HEAD', 'master') { throw 'Ship from a feature branch in your own worktree, not from master or a detached HEAD.' }
        $gitDir = (Invoke-Native git @('rev-parse', '--path-format=absolute', '--git-dir')).Trim()
        $commonDir = (Invoke-Native git @('rev-parse', '--path-format=absolute', '--git-common-dir')).Trim()
        if ($gitDir -eq $commonDir) { throw 'Agents ship from a linked worktree: git worktree add -b <branch> <path> origin/master.' }
        if (@(Invoke-Native git @('status', '--porcelain', '--untracked-files=no')).Count) { throw 'Commit or discard tracked changes first; only committed work is reviewed and shipped.' }
        & git merge-base --is-ancestor origin/master HEAD
        if ($LASTEXITCODE -ne 0) { throw 'The branch does not contain the latest origin/master. Merge it (git merge origin/master), resolve conflicts, and rerun.' }
        $changed = @(Invoke-Native git @('diff', '--name-only', '--no-renames', 'origin/master', 'HEAD'))
        if (!$changed.Count) { throw 'The branch has no changes from origin/master.' }
        $changed | ForEach-Object { $null = Assert-ShipPathName $_ }
        $untracked = @(Invoke-Native git @('ls-files', '--others', '--exclude-standard'))
        if ($untracked.Count) { Write-Warning "Untracked files are not shipped: $($untracked -join ', ')" }
        $existing = Find-BranchPullRequest $branch
        if (!$existing -and !$Message -and !$NoRelease) { throw 'Pass -Message with the release note users will read.' }
        if (!$existing -and !$Message -and !$Title) { throw 'Pass -Title for the pull request.' }
        if (!$existing -and !$BodyFile) { throw 'Pass -BodyFile with the pull request description for the reviewer.' }
        $latest = (Get-PublicJson "$apiRoot/releases/latest").tag_name -replace '^v', ''
        $branchVersion = Get-PropsVersion $root 'HEAD'
        if ($NoRelease) {
            if ($branchVersion -cne (Get-PropsVersion $root 'origin/master')) { throw 'A change that is not released keeps the version from master.' }
            $Version = $branchVersion
        }
        else {
            $Version = Get-PullRequestVersion $Version $latest (Get-PropsVersion $root 'origin/master') $branchVersion
            & git show-ref --verify --quiet "refs/tags/v$Version"
            if ($LASTEXITCODE -eq 0) { throw "Tag v$Version already exists. Choose a higher version." }
        }
        Write-Host "$(if ($existing) { "Update #$($existing.number)" } else { 'Open a pull request' }) for $branch $(if ($NoRelease) { 'without a release' } else { "as Watchroom $Version" }) with these files:"
        $changed | ForEach-Object { Write-Host "  $_" }
        if ($Plan) { Write-Host 'Preview complete. No files, commits, branches, or pull requests changed.'; return }
        if (!$Sdk) {
            $Sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
            if (!(Test-Path -LiteralPath $Sdk)) {
                $Sdk = Join-Path (Split-Path -Parent $commonDir) '.tools/dotnet/dotnet.exe'
                if (!(Test-Path -LiteralPath $Sdk)) { $Sdk = (Get-Command dotnet -ErrorAction Stop).Source }
            }
        }
        $Sdk = (Resolve-Path -LiteralPath $Sdk).Path
        $env:PATH = (Split-Path -Parent $Sdk) + [IO.Path]::PathSeparator + $env:PATH
        if ($branchVersion -cne $Version) {
            $propsPath = Join-Path $root 'Directory.Build.props'
            $props = Get-Content -LiteralPath $propsPath -Raw
            if ([regex]::Matches($props, '<Version>[^<]+</Version>').Count -ne 1) { throw 'Expected exactly one Version property.' }
            [IO.File]::WriteAllText($propsPath, [regex]::Replace($props, '<Version>[^<]+</Version>', "<Version>$Version</Version>"))
            Invoke-Native git @('commit', '--quiet', '-m', "Set version $Version", '--', 'Directory.Build.props')
        }
        $head = (Invoke-Native git @('rev-parse', 'HEAD')).Trim()
        # Check a clean copy of the commit: untracked files in the worktree cannot affect the result,
        # and SQLite fixture paths stay within legacy Windows limits under the temp directory.
        $checkout = Join-Path ([IO.Path]::GetTempPath()) ('watchroom-ship-' + [guid]::NewGuid().ToString('N'))
        Invoke-Native git @('worktree', 'add', '--detach', $checkout, $head)
        Invoke-ReleaseChecks $checkout $Sdk (Join-Path (Split-Path -Parent $commonDir) '.nuget/packages')
        Invoke-Native git @('fetch', '--no-recurse-submodules', 'origin', 'master')
        & git merge-base --is-ancestor origin/master $head
        if ($LASTEXITCODE -ne 0) { throw 'master advanced during checks. Merge origin/master into the branch and rerun.' }
        $recordPath = Get-CheckRecordPath $root $head
        New-Item -ItemType Directory -Path (Split-Path -Parent $recordPath) -Force | Out-Null
        @{ Commit = $head; Version = $Version; Checked = [datetime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $recordPath
        Invoke-Native git @('push', '--set-upstream', 'origin', "HEAD:refs/heads/$branch")
        $summary = if ($BodyFile) { Get-Content -LiteralPath $BodyFile -Raw } else { Get-PullRequestSummary $existing.body }
        $note = if ($NoRelease) { 'Not released.' } elseif ($Message) { $Message } else { Get-ReleaseNote $existing.body }
        $body = Format-PullRequestBody $summary $note $(if ($NoRelease) { 'none' } else { $Version })
        $prTitle = if ($Title) { $Title } elseif ($existing) { $existing.title } elseif ($Message.Length -le 100) { $Message } else { $Message.Substring(0, 99) + '…' }
        $pull = if ($existing) { Invoke-GitHub "/pulls/$($existing.number)" -Method Patch -Body @{ title = $prTitle; body = $body } }
        else { Invoke-GitHub '/pulls' -Method Post -Body @{ title = $prTitle; head = $branch; base = 'master'; body = $body } }
        Invoke-Native git @('worktree', 'remove', '--force', $checkout); $checkout = $null
        Write-Host "Pull request: $($pull.html_url)"
        Write-Host "Head: $head ($(if ($NoRelease) { 'not released' } else { "Watchroom $Version" }), release checks passed)"
        Write-Host "Next: ask a separate reviewer agent to review #$($pull.number) at $head with the review-pr skill (.claude/skills/review-pr/SKILL.md)."
        Write-Host "Fix what it finds and rerun this script until it approves, then run ship.ps1 -Merge$(if ($NoRelease) { ' -NoRelease' })."
        return
    }
    $record = Join-Path $mainRoot ('artifacts/ship-' + $Version + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $record | Out-Null
    $run = Wait-ReleaseBuild $Version $commit $deadline
    $result = Test-PublishedRelease $Version $record $deadline
    $result.Actions = $run.html_url
    $result.Commit = $commit
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $record 'verified.json')
    try { $result.LocalMaster = Sync-ShippedMaster $root $commit $record }
    catch { throw "Release v$Version is publicly verified, but local master was not synchronized. $_" }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $record 'verified.json')
    $outcome = if ($VerifyOnly) { 'Verified existing release' } else { 'Shipped' }
    Write-Host "$outcome Watchroom ${Version}: $($result.Release)"
    Write-Host "Actions: $($run.html_url)"
    Write-Host "Verified SHA-256: $($result.Sha256)"
    Write-Host "Verified file list and update installers: $(if ($result.Updates.Count) { $result.Updates -join ', ' } else { 'none' })"
    Write-Host "Verification record: $record"
    Write-Host "Local master synchronized: $($result.LocalMaster.Commit)"
}
catch {
    if ($checkout) { Write-Warning "Check checkout retained at $checkout for investigation." }
    if ($Merge -and $Version) { Write-Warning "If v$Version was pushed, recheck with -VerifyOnly -Version $Version. Never move a published tag." }
    throw
}
finally { $env:PATH = $originalPath; Pop-Location }
