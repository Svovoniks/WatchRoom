#Requires -Version 7.0
# Reviewer side of the shipping flow; see .claude/skills/review-pr/SKILL.md.
#   review-pr.ps1 -Pr 12                                              check out the head in a fresh worktree and summarize it
#   review-pr.ps1 -Pr 12 -Verdict changes -Head <sha> -BodyFile f.md  post the verdict for exactly that head
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$Pr,
    [ValidateSet('', 'approve', 'changes')][string]$Verdict = '',
    [string]$Head = '',
    [string]$BodyFile = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'github-pr.ps1')
$root = Split-Path -Parent $PSScriptRoot
Use-Repository
$pull = Invoke-GitHub "/pulls/$Pr"
if ($pull.state -ne 'open') { throw "Pull request #$Pr is $($pull.state)." }

if ($Verdict) {
    if ($Head -notmatch '^[a-f0-9]{40}$') { throw 'Pass -Head with the full commit you reviewed.' }
    if (!$BodyFile) { throw 'Pass -BodyFile with the findings.' }
    # A verdict for an older commit must not approve commits the reviewer never saw.
    if ($pull.head.sha -cne $Head) { throw "The pull request head moved to $($pull.head.sha) after you reviewed $Head. Review the new head." }
    $label = if ($Verdict -eq 'approve') { 'Approved: OK to merge' } else { 'Changes requested' }
    $findings = (Get-Content -LiteralPath $BodyFile -Raw).Trim()
    $body = "## Review verdict: $label`n`nReviewed head ``$Head``.`n`n$findings`n`n$(Format-ReviewMarker $Head $Verdict)"
    $comment = Invoke-GitHub "/issues/$Pr/comments" -Method Post -Body @{ body = $body }
    Write-Host "Posted $Verdict verdict: $($comment.html_url)"
    return
}

$head = $pull.head.sha
Invoke-Native git @('-C', $root, 'fetch', '--no-recurse-submodules', 'origin', 'master', "pull/$Pr/head")
# A short temp path keeps SQLite fixture paths within legacy Windows limits.
$checkout = Join-Path ([IO.Path]::GetTempPath()) "wr-review-$Pr-$($head.Substring(0, 8))-$([guid]::NewGuid().ToString('N').Substring(0, 6))"
Invoke-Native git @('-C', $root, 'worktree', 'add', '--detach', $checkout, $head) | Out-Null
$base = (Invoke-Native git @('-C', $checkout, 'merge-base', 'origin/master', $head)).Trim()
$state = Get-HeadCheckState (Invoke-GitHub "/actions/runs?head_sha=$head&per_page=100").workflow_runs $head
Write-Host "#$Pr $($pull.title)"
Write-Host $pull.html_url
Write-Host "Head: $head (branch $($pull.head.ref))"
Write-Host "Base: $base$(if ($base -cne (Invoke-Native git @('-C', $root, 'rev-parse', 'origin/master')).Trim()) { ' - behind origin/master; merging will require an update' })"
Write-Host "CI: $($state.State) - $($state.Detail)"
Write-Host "Review worktree: $checkout"
Write-Host "Diff: git -C '$checkout' diff $base $head"
Write-Host ''
Write-Host '--- Description ---'
Write-Host $pull.body
Write-Host ''
Write-Host '--- Changed files ---'
Invoke-Native git @('-C', $checkout, 'diff', '--stat=200', $base, $head)
$previous = @(Get-PullRequestComments $Pr | Where-Object { $_.body -match $reviewMarkerPattern })
if ($previous.Count) {
    Write-Host ''
    Write-Host '--- Earlier verdicts (verify every earlier blocking finding is fixed) ---'
    $previous | ForEach-Object { Write-Host $_.html_url }
}
