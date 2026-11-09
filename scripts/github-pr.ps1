# Pull request, check record and build cache helpers shared by ship.ps1 and review-pr.ps1. Dot-source only.
# The repository was Svovoniks/WatchRoom before it became Svovoniks/BlackPearl. Use-Repository picks whichever name
# exists, so the scripts work on both sides of the rename. GitHub redirects the old name for reads only.
$repository = 'Svovoniks/BlackPearl'
$formerRepository = 'Svovoniks/WatchRoom'
$apiRoot = "https://api.github.com/repos/$repository"
$buildWorkflow = 'Build and release'
# Review verdicts are PR comments carrying this marker. Every account here is the same
# owner, so GitHub's own approve state is unavailable (authors cannot approve their PRs).
# Only a marker ending the comment counts, so findings that quote another marker cannot decide the verdict.
# Markers posted before the rename say watchroom-review.
$reviewMarkerPattern = '<!-- (?:blackpearl|watchroom)-review head=([a-f0-9]{40}) verdict=(approve|changes) -->\s*\z'

function Invoke-Native {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE): $($Arguments -join ' ')" }
}
function Get-CheckRecordPath {
    param([string]$Root, [string]$Commit)
    # Shared by every worktree, so -Merge and reviewers can run anywhere and still see what was checked.
    $common = (Invoke-Native git @('-C', $Root, 'rev-parse', '--path-format=absolute', '--git-common-dir')).Trim()
    return Join-Path $common "blackpearl-checks/$Commit.json"
}
function Write-CheckRecord {
    param([string]$Path, [hashtable]$Record)
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $partial = "$Path.partial-$([guid]::NewGuid().ToString('N'))"
    $Record | ConvertTo-Json | Set-Content -LiteralPath $partial
    Move-Item -LiteralPath $partial -Destination $Path -Force
}
function Get-CheckState {
    param([string]$Path)
    # Release checks run in the background after a push. Their record says running, passed or failed; a record from
    # before that has no state and means they passed. A running record whose process is gone was interrupted.
    if (!(Test-Path -LiteralPath $Path)) { return [pscustomobject]@{ State = 'missing' } }
    $record = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (!$record.PSObject.Properties['State']) { $record | Add-Member State 'passed' }
    if ($record.State -eq 'running') {
        $process = Get-Process -Id $record.Pid -ErrorAction SilentlyContinue
        if (!$process -or $process.StartTime.ToUniversalTime().Ticks -ne [long]$record.ProcessStart) { $record.State = 'interrupted' }
    }
    return $record
}
function Format-CheckEvidence {
    param($State)
    $parts = @()
    if ($State.Log -and (Test-Path -LiteralPath $State.Log)) { $parts += "Log: $($State.Log)." }
    if ($State.Checkout -and (Test-Path -LiteralPath $State.Checkout)) { $parts += "Checked copy kept at $($State.Checkout)." }
    return $(if ($parts.Count) { ($parts -join ' ') + ' ' } else { '' })
}
# Voice libraries and models are fetched or built once per checkout (see BlackPearl.Desktop.csproj), and each check or
# review copy is a fresh checkout. Like the CI cache, this keeps them between runs while their pinned inputs are unchanged.
function Get-BuildCacheEntries {
    param([string]$Checkout)
    $common = (Invoke-Native git @('-C', $Checkout, 'rev-parse', '--path-format=absolute', '--git-common-dir')).Trim()
    $project = (Invoke-Native git @('-C', $Checkout, 'rev-parse', 'HEAD:src/BlackPearl.Desktop/BlackPearl.Desktop.csproj')).Trim()
    $native = (Invoke-Native git @('-C', $Checkout, 'rev-parse', 'HEAD:src/native')).Trim()
    return @(
        [pscustomobject]@{ Path = Join-Path $Checkout '.tools/native'; Files = '*.dll'; Kind = 'native'; Cache = Join-Path $common "blackpearl-build-cache/native-$native-$project" },
        [pscustomobject]@{ Path = Join-Path $Checkout '.tools/models'; Files = '*'; Kind = 'models'; Cache = Join-Path $common "blackpearl-build-cache/models-$project" })
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
function Use-Repository {
    $name = $repository
    try { $name = (Get-PublicJson "https://api.github.com/repos/$repository").full_name }
    catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
        $name = (Get-PublicJson "https://api.github.com/repos/$formerRepository").full_name
    }
    if ($name -cnotin @($repository, $formerRepository)) { throw "Unexpected repository $name." }
    $script:repository = $name
    $script:apiRoot = "https://api.github.com/repos/$name"
}
function Get-PublicJson {
    param([string]$Url)
    # Public verification deliberately does not use GH_TOKEN or a GitHub CLI session.
    Invoke-RestMethod -Uri $Url -Headers @{ 'User-Agent' = 'BlackPearl-release-verification' } -TimeoutSec 60
}
function Get-GitHubToken {
    # The token git already uses for pushes; it is never printed.
    $env:GIT_TERMINAL_PROMPT = '0'; $env:GCM_INTERACTIVE = 'never'
    $fill = "protocol=https`nhost=github.com`n`n" | & git credential fill 2>$null
    $password = @($fill | Where-Object { $_ -like 'password=*' } | Select-Object -First 1)
    if (!$password.Count) { throw 'No GitHub credential is stored for git. Pull requests need the same credential used for pushes.' }
    return $password[0].Substring(9)
}
function Invoke-GitHub {
    param([string]$Path, [string]$Method = 'Get', $Body = $null)
    if (!$script:gitHubToken) { $script:gitHubToken = Get-GitHubToken }
    $request = @{
        Method = $Method; Uri = "$apiRoot$Path"; TimeoutSec = 60
        Headers = @{ 'User-Agent' = 'BlackPearl-pull-requests'; Authorization = "Bearer $script:gitHubToken"; Accept = 'application/vnd.github+json' }
    }
    if ($null -ne $Body) { $request.Body = $Body | ConvertTo-Json -Depth 5; $request.ContentType = 'application/json' }
    # Enumerate JSON arrays so callers can count list responses with @().
    Invoke-RestMethod @request | ForEach-Object { $_ }
}
function Find-BranchPullRequest {
    param([string]$Branch)
    $owner = $repository.Split('/')[0]
    return Invoke-GitHub "/pulls?state=open&base=master&head=${owner}:$([uri]::EscapeDataString($Branch))" | Select-Object -First 1
}
function Get-PullRequestComments {
    param([int]$Number)
    $comments = @(); $page = 1
    do {
        $batch = @(Invoke-GitHub "/issues/$Number/comments?per_page=100&page=$page")
        $comments += $batch; $page++
    } while ($batch.Count -eq 100)
    return $comments
}
function Format-ReviewMarker {
    param([string]$Head, [string]$Verdict)
    return "<!-- blackpearl-review head=$Head verdict=$Verdict -->"
}
function Get-ReviewVerdict {
    param($Comments)
    # The newest marked comment wins. Public commenters cannot approve or block.
    $latest = $null
    foreach ($comment in @($Comments | Sort-Object { [datetime]$_.created_at }, { [long]$_.id })) {
        if ($comment.author_association -notin 'OWNER', 'MEMBER', 'COLLABORATOR') { continue }
        $match = [regex]::Match([string]$comment.body, $reviewMarkerPattern)
        if ($match.Success) { $latest = @{ Head = $match.Groups[1].Value; Verdict = $match.Groups[2].Value; Url = $comment.html_url } }
    }
    return $latest
}
function Assert-ReviewApproval {
    param($Comments, [string]$Head)
    $verdict = Get-ReviewVerdict $Comments
    if (!$verdict) { throw 'The pull request has no reviewer verdict. Ask a separate reviewer agent to review it with the review-pr skill.' }
    if ($verdict.Head -cne $Head) { throw "The latest review covers $($verdict.Head), but the pull request head is $Head. Request a re-review of the current head." }
    if ($verdict.Verdict -cne 'approve') { throw "The reviewer requested changes ($($verdict.Url)). Fix them, rerun ship.ps1, and request a re-review." }
    return $verdict
}
function Get-HeadCheckState {
    param($Runs, [string]$Head)
    # The newest run of the build workflow for each event must pass on this exact commit.
    $latest = @(@($Runs) | Where-Object { $_ -and $_.head_sha -ceq $Head -and $_.name -ceq $buildWorkflow } |
        Group-Object event | ForEach-Object { $_.Group | Sort-Object id, run_attempt -Descending | Select-Object -First 1 })
    if (!$latest.Count) { return @{ State = 'pending'; Detail = 'Waiting for the build workflow to start' } }
    $failed = @($latest | Where-Object { $_.status -eq 'completed' -and $_.conclusion -ne 'success' })
    if ($failed.Count) { return @{ State = 'failure'; Detail = "Build workflow $($failed[0].conclusion): $($failed[0].html_url)" } }
    $detail = ($latest | ForEach-Object { "$($_.event) $($_.status) $($_.conclusion)".Trim() }) -join ', '
    if (@($latest | Where-Object status -ne 'completed').Count) { return @{ State = 'pending'; Detail = $detail } }
    return @{ State = 'success'; Detail = $detail; Urls = @($latest.html_url) }
}
function Format-PullRequestBody {
    param([string]$Summary, [string]$Note, [string]$Version)
    return "$($Summary.Trim())`n`n### Release note`n`n$($Note.Trim())`n`n<!-- blackpearl-release version=$Version -->"
}
function Get-PullRequestSummary {
    param([string]$Body)
    return ([regex]::Split([string]$Body, '\r?\n### Release note\r?\n')[0]).Trim()
}
function Get-ReleaseNote {
    param([string]$Body)
    $match = [regex]::Match([string]$Body, '(?s)### Release note\r?\n(.+?)\r?\n<!-- (?:blackpearl|watchroom)-release version=')
    if (!$match.Success -or !$match.Groups[1].Value.Trim()) { return $null }
    return $match.Groups[1].Value.Trim()
}
