#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ship.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('watchroom-ship-checks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'src') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $testRoot 'src/app.cs'), 'fixture')
$checks = 0
function Expect-Rejected {
    param([scriptblock]$Action, [string]$Name)
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw "FAIL: $Name" }
    $script:checks++; Write-Host "PASS: $Name"
}
function Check {
    param([bool]$Value, [string]$Name)
    if (!$Value) { throw "FAIL: $Name" }
    $script:checks++; Write-Host "PASS: $Name"
}
try {
    Check ((Get-ShipVersion '' '0.3.17' '0.3.17') -eq '0.3.18') 'next patch version is chosen automatically'
    Check ((Get-ShipVersion '' '0.3.18' '0.3.17') -eq '0.3.19') 'public version takes precedence over older source'
    foreach ($version in @('0.3.17','0.3.16','0.3.18-beta','0.3')) {
        Expect-Rejected { Get-ShipVersion $version '0.3.17' '0.3.17' } "unsafe version $version is rejected"
    }
    Check ((Get-PullRequestVersion '' '0.3.17' '0.3.17' '0.3.17') -eq '0.3.18') 'a new pull request gets the next patch version'
    Check ((Get-PullRequestVersion '' '0.3.17' '0.3.17' '0.3.18') -eq '0.3.18') 'a bumped branch keeps its version across review rounds'
    Check ((Get-PullRequestVersion '' '0.3.18' '0.3.18' '0.3.18') -eq '0.3.19') 'a branch overtaken by a release is bumped again'
    Check ((Get-PullRequestVersion '' '0.3.17' '0.3.18' '0.3.18') -eq '0.3.19') 'a branch matching a newer master version is bumped'
    Check ((Get-PullRequestVersion '0.4.0' '0.3.17' '0.3.17' '0.3.18') -eq '0.4.0') 'an explicit version replaces the branch version'
    Expect-Rejected { Get-PullRequestVersion '0.3.17' '0.3.17' '0.3.17' '0.3.18' } 'an explicit version must still be higher than every release'
    foreach ($path in @('src/app.cs', 'Directory.Build.props', 'scripts/ship.ps1', '.claude/skills/review-pr/SKILL.md', 'src/Watchroom.Desktop/Assets/Tmdb.png', 'src/Watchroom.Desktop/Assets/Promo/teaser-clam-intro.mp4')) {
        Check ((Assert-ShipPathName $path) -eq $path) "release source path $path is accepted"
    }
    foreach ($path in @('../outside.cs','sites','artifacts/report.html','sites/src/app.ts','.tools/helper.ps1','src/credentials/token.json','src/.env','src/library.db','src/personal.png','src/Watchroom.Desktop/Assets/snapshot.png','src/Watchroom.Desktop/Assets/Promo/other.mp4','src/clip.mp4','TEST-REPORT.html','.claude/settings.json')) {
        Expect-Rejected { Assert-ShipPathName $path } "excluded path $path cannot enter a pull request"
    }
    $summary = "Fixes seeking.`n`nIntentional: removes the old toggle."
    $body = Format-PullRequestBody $summary 'Seeking is faster' '0.3.18'
    Check ((Get-ReleaseNote $body) -ceq 'Seeking is faster' -and (Get-PullRequestSummary $body) -ceq $summary) 'release note and description survive a pull request body round trip'
    Check ($null -eq (Get-ReleaseNote 'Edited by hand')) 'a body without a release note yields none'
    $headA = 'a' * 40; $headB = 'b' * 40
    function New-ReviewComment { param([string]$Head, [string]$Verdict, [string]$Association = 'OWNER', [int]$Minute = 0)
        [pscustomobject]@{ body = "Findings`n$(Format-ReviewMarker $Head $Verdict)"; author_association = $Association; created_at = [datetime]::new(2026, 1, 1, 0, $Minute, 0); html_url = "https://github.com/fixture/$Minute" } }
    Check ((Assert-ReviewApproval @((New-ReviewComment $headA 'changes' -Minute 1), (New-ReviewComment $headA 'approve' -Minute 2)) $headA).Verdict -eq 'approve') 'an approval of the exact head after earlier findings allows the merge'
    Expect-Rejected { Assert-ReviewApproval @() $headA } 'a pull request without a review cannot merge'
    Expect-Rejected { Assert-ReviewApproval @(New-ReviewComment $headA 'approve') $headB } 'an approval of an older head does not cover new commits'
    Expect-Rejected { Assert-ReviewApproval @((New-ReviewComment $headA 'approve' -Minute 1), (New-ReviewComment $headA 'changes' -Minute 2)) $headA } 'requested changes after an approval block the merge'
    Expect-Rejected { Assert-ReviewApproval @((New-ReviewComment $headA 'changes' -Minute 2), (New-ReviewComment $headA 'approve' -Minute 1)) $headA } 'the newest verdict wins regardless of response order'
    Expect-Rejected { Assert-ReviewApproval @(New-ReviewComment $headA 'approve' 'NONE') $headA } 'a public commenter cannot approve'
    $quoting = New-ReviewComment $headA 'changes' -Minute 3
    $quoting.body = "The parser accepts ``$(Format-ReviewMarker $headA 'approve')`` anywhere.`n$(Format-ReviewMarker $headA 'changes')"
    Expect-Rejected { Assert-ReviewApproval @((New-ReviewComment $headA 'approve' -Minute 1), $quoting) $headA } 'findings quoting an approval marker cannot turn requested changes into an approval'
    $sameSecond = @((New-ReviewComment $headA 'changes'), (New-ReviewComment $headA 'approve'))
    $sameSecond[0] | Add-Member id 20; $sameSecond[1] | Add-Member id 10
    Expect-Rejected { Assert-ReviewApproval $sameSecond $headA } 'comments posted in the same second are ordered by id'
    Expect-Rejected { Assert-ReviewApproval @([pscustomobject]@{ body = "LGTM <!-- watchroom-review head=$headA verdict=approved -->"; author_association = 'OWNER'; created_at = [datetime]::UtcNow }) $headA } 'a malformed verdict marker does not approve'
    function New-BuildRun { param([string]$Event, [string]$Status, [string]$Conclusion, [int]$Id = 1, [int]$Attempt = 1, [string]$Head = $headA)
        [pscustomobject]@{ id = $Id; run_attempt = $Attempt; event = $Event; status = $Status; conclusion = $Conclusion; head_sha = $Head; name = 'Build and release'; html_url = "https://github.com/fixture/run/$Id" } }
    Check ((Get-HeadCheckState @() $headA).State -eq 'pending') 'merging waits for the build workflow to start'
    Check ((Get-HeadCheckState @((New-BuildRun 'push' 'completed' 'success'), (New-BuildRun 'pull_request' 'in_progress' $null -Id 2)) $headA).State -eq 'pending') 'merging waits for every build of the head'
    Check ((Get-HeadCheckState @((New-BuildRun 'push' 'completed' 'success'), (New-BuildRun 'pull_request' 'completed' 'success' -Id 2)) $headA).State -eq 'success') 'passing builds of the head allow the merge'
    Check ((Get-HeadCheckState @((New-BuildRun 'push' 'completed' 'success'), (New-BuildRun 'pull_request' 'completed' 'failure' -Id 2)) $headA).State -eq 'failure') 'a failed build of the head blocks the merge'
    Check ((Get-HeadCheckState @((New-BuildRun 'pull_request' 'completed' 'failure'), (New-BuildRun 'pull_request' 'completed' 'success' -Attempt 2)) $headA).State -eq 'success') 'a successful rerun replaces its failed attempt'
    Check ((Get-HeadCheckState @(New-BuildRun 'push' 'completed' 'cancelled') $headA).State -eq 'failure') 'a cancelled build of the head blocks the merge'
    Check ((Get-HeadCheckState @(New-BuildRun 'push' 'completed' 'success' -Head $headB) $headA).State -eq 'pending') 'builds of other commits do not count'
    $fileName = 'Watchroom-Setup-0.3.18-win-x64.exe'
    $filePath = Join-Path $testRoot $fileName
    [IO.File]::WriteAllBytes($filePath, [byte[]](1,2,3,4))
    $hash = (Get-FileHash $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = [pscustomobject]@{ name=$fileName; size=4; digest="sha256:$hash"; browser_download_url="https://github.com/Svovoniks/WatchRoom/releases/download/v0.3.18/$fileName" }
    $sumPath = Join-Path $testRoot 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($sumPath, "$hash  $fileName`n")
    $fileList = [pscustomobject]@{ name='Watchroom-files-0.3.18-win-x64.txt'; size=10; digest="sha256:$('a' * 64)"; browser_download_url='https://github.com/Svovoniks/WatchRoom/releases/download/v0.3.18/Watchroom-files-0.3.18-win-x64.txt' }
    $release = [pscustomobject]@{ tag_name='v0.3.18'; draft=$false; prerelease=$false; assets=@($asset, [pscustomobject]@{name='SHA256SUMS.txt';browser_download_url='https://github.com/Svovoniks/WatchRoom/releases/download/v0.3.18/SHA256SUMS.txt'}, $fileList) }
    $null = Assert-ReleaseMetadata $release '0.3.18'
    $release.assets = @($release.assets | Where-Object { $_ -ne $fileList })
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'a release without its file list cannot be reported as shipped'
    $release.assets = @($release.assets) + $fileList
    Check ((Assert-InstallerBytes $filePath $sumPath $asset) -eq $hash) 'correct installer matches both public digests'
    $release.draft=$true
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'draft cannot be reported as shipped'
    $release.draft=$false; $release.prerelease=$true
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'prerelease cannot be reported as stable'
    $release.prerelease=$false
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.19' } 'wrong latest version cannot pass verification'
    $asset.browser_download_url='https://example.com/installer.exe'
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'installer URL must belong to the expected release'
    $asset.browser_download_url="https://github.com/Svovoniks/WatchRoom/releases/download/v0.3.18/$fileName"
    $asset.digest=''
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'missing GitHub digest prevents verification'
    $asset.digest="sha256:$hash"
    [IO.File]::WriteAllBytes($filePath, [byte[]](4,3,2,1))
    Expect-Rejected { Assert-InstallerBytes $filePath $sumPath $asset } 'same-size corrupted installer is rejected'
    [IO.File]::WriteAllBytes($filePath, [byte[]](1,2,3,4))
    [IO.File]::WriteAllText($sumPath, ('0' * 64) + "  $fileName`n")
    Expect-Rejected { Assert-InstallerBytes $filePath $sumPath $asset } 'checksum-file disagreement is rejected'
    [IO.File]::WriteAllText($sumPath, "$hash  $fileName`n")
    $updateName = 'Watchroom-Update-0.3.17-to-0.3.18-win-x64.exe'
    $updatePath = Join-Path $testRoot $updateName
    [IO.File]::WriteAllBytes($updatePath, [byte[]](5,6))
    $updateHash = (Get-FileHash $updatePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::AppendAllText($sumPath, "$updateHash  $updateName`n")
    $update = [pscustomobject]@{ name=$updateName; size=2; digest="sha256:$updateHash"; browser_download_url="https://github.com/Svovoniks/WatchRoom/releases/download/v0.3.18/$updateName" }
    $release.assets = @($release.assets) + @($update, [pscustomobject]@{ name='Watchroom-Update-0.3.16-to-0.3.17-win-x64.exe'; size=1; digest=''; browser_download_url='https://example.com/old.exe' })
    $verified = Assert-ReleaseMetadata $release '0.3.18'
    Check (@($verified.Updates | Where-Object name -like 'Watchroom-Update-*').Count -eq 1 -and (Assert-InstallerBytes $updatePath $sumPath $update) -eq $updateHash) 'update installers for this version are verified like the full installer'
    $update.browser_download_url = 'https://example.com/update.exe'
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'update installer URL must belong to the expected release'
    $update.browser_download_url = "https://github.com/Svovoniks/WatchRoom/releases/download/v0.3.18/$updateName"
    $update.digest = ''
    Expect-Rejected { Assert-ReleaseMetadata $release '0.3.18' } 'update installer without a GitHub digest prevents verification'
    $update.digest = "sha256:$updateHash"
    [IO.File]::WriteAllBytes($updatePath, [byte[]](6,5))
    Expect-Rejected { Assert-InstallerBytes $updatePath $sumPath $update } 'corrupted update installer is rejected'
    function Get-PublicJson {
        param([string]$Url)
        return @{ workflow_runs = @(
            @{id=1;head_branch='master';head_sha='fixture';name='Build and release';status='completed';conclusion='success';run_attempt=1},
            @{id=2;head_branch='v0.3.18';head_sha='other';name='Build and release';status='completed';conclusion='success';run_attempt=1},
            @{id=3;head_branch='v0.3.18';head_sha='fixture';name='Build and release';status='completed';conclusion=$script:mockConclusion;run_attempt=1;html_url='https://github.com/fixture/run'}
        ) }
    }
    $script:mockConclusion = 'success'
    $run = Wait-ReleaseBuild '0.3.18' 'fixture' ([datetime]::UtcNow.AddSeconds(5))
    Check ($run.id -eq 3) 'only the exact tag and commit workflow count as a release build'
    $script:mockConclusion = 'failure'
    Expect-Rejected { Wait-ReleaseBuild '0.3.18' 'fixture' ([datetime]::UtcNow.AddSeconds(5)) } 'failed tag workflow stops shipping'
    . (Join-Path $PSScriptRoot 'ship-sync-checks.ps1') -TestRoot $testRoot
    Write-Host "$checks shipping safety checks passed. No releases were created."
}
finally {
    # This is the exact fresh directory created above, beneath the system temp root.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -match '^watchroom-ship-checks-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
