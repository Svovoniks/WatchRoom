# Called by ship-checks.ps1 with its offline helpers and fresh temporary root.
param([Parameter(Mandatory)][string]$TestRoot)
$ErrorActionPreference = 'Stop'
function Set-FixtureFile {
    param([string]$Root, [string]$Path, [string]$Text)
    [IO.File]::WriteAllText((Join-Path $Root $Path), $Text)
}
function Configure-FixtureGit {
    param([string]$Root)
    Invoke-Native git @('-C', $Root, 'config', 'user.name', 'Shipping checks')
    Invoke-Native git @('-C', $Root, 'config', 'user.email', 'checks@example.invalid')
}
function Commit-Fixture {
    param([string]$Root)
    Invoke-Native git @('-C', $Root, 'add', '--all') | Out-Null
    Invoke-Native git @('-C', $Root, 'commit', '--quiet', '-m', 'Shipping sync fixture') | Out-Null
    Invoke-Native git @('-C', $Root, 'push', '--quiet', 'origin', 'master') | Out-Null
    return (Invoke-Native git @('-C', $Root, 'rev-parse', 'HEAD')).Trim()
}
$origin = Join-Path $TestRoot 'sync-origin.git'
$publisher = Join-Path $TestRoot 'sync-publisher'
$local = Join-Path $TestRoot 'sync-local'
$service = Join-Path $TestRoot 'sync-service'
Invoke-Native git @('init', '--quiet', '--bare', '--initial-branch=master', $origin)
Invoke-Native git @('init', '--quiet', '--initial-branch=master', $service)
Configure-FixtureGit $service; Set-FixtureFile $service 'worker.js' 'Original service'
Invoke-Native git @('-C', $service, 'add', '--all')
Invoke-Native git @('-C', $service, 'commit', '--quiet', '-m', 'Service fixture')
Invoke-Native git @('init', '--quiet', '--initial-branch=master', $publisher)
Configure-FixtureGit $publisher
foreach ($path in @('shipped.cs', 'staged.txt', 'notes.txt', 'conflict.txt')) { Set-FixtureFile $publisher $path "Original $path" }
Invoke-Native git @('-C', $publisher, '-c', 'protocol.file.allow=always', 'submodule', 'add', '--quiet', $service, 'sites')
Invoke-Native git @('-C', $publisher, 'remote', 'add', 'origin', $origin)
$null = Commit-Fixture $publisher
Invoke-Native git @('-c', 'protocol.file.allow=always', 'clone', '--quiet', '--recurse-submodules', $origin, $local)
Configure-FixtureGit $local

Set-FixtureFile $local 'shipped.cs' 'Shipped source'
Set-FixtureFile $local 'new.cs' 'New shipped source'
Set-FixtureFile $local 'notes.txt' 'Unrelated draft'
Set-FixtureFile $local 'staged.txt' 'Staged draft'
Invoke-Native git @('-C', $local, 'add', '--', 'staged.txt')
Set-FixtureFile $local 'staged.txt' 'Staged draft with extra work'
[byte[]]$bytes = @(0, 255, 13, 10, 128, 1)
[IO.File]::WriteAllBytes((Join-Path $local 'untracked.bin'), $bytes)
Set-FixtureFile (Join-Path $local 'sites') 'worker.js' 'Unrelated service draft'
Set-FixtureFile (Join-Path $local 'sites') 'local.js' 'Untracked service draft'
$serviceStatus = @(Invoke-Native git @('-C', (Join-Path $local 'sites'), 'status', '--porcelain')) -join "`n"
$serviceHead = (Invoke-Native git @('-C', (Join-Path $local 'sites'), 'rev-parse', 'HEAD')).Trim()
Set-FixtureFile $publisher 'shipped.cs' 'Shipped source'
Set-FixtureFile $publisher 'new.cs' 'New shipped source'
Set-FixtureFile $publisher 'during-build.cs' 'Published new source'
$release = Commit-Fixture $publisher
Set-FixtureFile $local 'during-build.cs' 'New source edited while shipping'
$sync = Sync-ShippedMaster $local $release (Join-Path $TestRoot 'sync-record-1') @('shipped.cs', 'new.cs', 'during-build.cs')
Check ($sync.Commit -ceq $release) 'shipping sync pulls the verified release into local master'
Check (!@(Invoke-Native git @('-C', $local, 'status', '--porcelain', '--', 'shipped.cs', 'new.cs')).Count) 'already shipped edits and new files no longer appear as local changes'
Check ((Get-Content -LiteralPath (Join-Path $local 'during-build.cs') -Raw) -ceq 'New source edited while shipping') 'new selected source edited during the release is retained as a local change'
Check ((Get-Content -LiteralPath (Join-Path $local 'notes.txt') -Raw) -ceq 'Unrelated draft') 'unrelated unstaged edits survive shipping sync'
Check ((Invoke-Native git @('-C', $local, 'show', ':staged.txt')) -ceq 'Staged draft') 'unrelated staged content stays staged after shipping sync'
Check ((Get-Content -LiteralPath (Join-Path $local 'staged.txt') -Raw) -ceq 'Staged draft with extra work') 'additional unstaged edits to a staged file survive shipping sync'
Check ([Convert]::ToHexString([IO.File]::ReadAllBytes((Join-Path $local 'untracked.bin'))) -ceq [Convert]::ToHexString($bytes)) 'untracked binary bytes survive shipping sync exactly'
Check (((Invoke-Native git @('-C', (Join-Path $local 'sites'), 'status', '--porcelain')) -join "`n") -ceq $serviceStatus -and
    (Invoke-Native git @('-C', (Join-Path $local 'sites'), 'rev-parse', 'HEAD')).Trim() -ceq $serviceHead) 'shipping sync leaves dirty submodule files and its checkout unchanged'
Check ($sync.RecoveryStash -and (Test-Path -LiteralPath (Join-Path $TestRoot 'sync-record-1/local-master-sync/recovery-stash.txt'))) 'successful dirty sync retains an explicit recovery snapshot'

# Staged new files and deletions have to retain their index state too.
Set-FixtureFile $local 'staged-new.txt' 'New staged work'
Invoke-Native git @('-C', $local, 'add', '--', 'staged-new.txt')
[IO.File]::Delete((Join-Path $local 'notes.txt'))
Invoke-Native git @('-C', $local, 'add', '--update', '--', 'notes.txt')
Configure-FixtureGit (Join-Path $local 'sites')
Set-FixtureFile (Join-Path $local 'sites') 'committed.js' 'Local service commit'
Invoke-Native git @('-C', (Join-Path $local 'sites'), 'add', '--', 'committed.js')
Invoke-Native git @('-C', (Join-Path $local 'sites'), 'commit', '--quiet', '-m', 'Local service work')
Invoke-Native git @('-C', $local, 'add', '--', 'sites')
$stagedService = (Invoke-Native git @('-C', $local, 'rev-parse', ':sites')).Trim()

# Starting shipping from another worktree must still synchronize master.
$feature = Join-Path $TestRoot 'sync-feature'
Invoke-Native git @('-C', $local, 'worktree', 'add', '--quiet', '-b', 'feature', $feature)
$featureHead = (Invoke-Native git @('-C', $feature, 'rev-parse', 'HEAD')).Trim()
Set-FixtureFile $feature 'feature-draft.txt' 'Feature work'
Set-FixtureFile $publisher 'next-release.txt' 'Second release'
$release = Commit-Fixture $publisher
$sync = Sync-ShippedMaster $feature $release (Join-Path $TestRoot 'sync-record-2')
Check ([IO.Path]::GetFullPath($sync.Checkout) -eq [IO.Path]::GetFullPath($local) -and $sync.Commit -ceq $release) 'shipping from a feature worktree updates the checkout that owns master'
Check ((Invoke-Native git @('-C', $local, 'diff', '--cached', '--name-status', '--', 'staged-new.txt', 'notes.txt')) -contains "A`tstaged-new.txt" -and
    (Invoke-Native git @('-C', $local, 'diff', '--cached', '--name-status', '--', 'notes.txt')) -contains "D`tnotes.txt") 'staged additions and deletions survive shipping sync'
Check ((Invoke-Native git @('-C', $local, 'rev-parse', ':sites')).Trim() -ceq $stagedService -and
    (Invoke-Native git @('-C', (Join-Path $local 'sites'), 'rev-parse', 'HEAD')).Trim() -ceq $stagedService) 'a staged submodule commit and the active service checkout survive shipping sync'
Check ((Invoke-Native git @('-C', $feature, 'rev-parse', 'HEAD')).Trim() -ceq $featureHead -and
    (Get-Content -LiteralPath (Join-Path $feature 'feature-draft.txt') -Raw) -ceq 'Feature work') 'shipping sync does not switch or modify the feature checkout'

# With no master checkout, advance only its ref and preserve the current branch.
$unowned = Join-Path $TestRoot 'sync-unowned'
Invoke-Native git @('clone', '--quiet', $origin, $unowned); Configure-FixtureGit $unowned
Invoke-Native git @('-C', $unowned, 'checkout', '--quiet', '-b', 'feature')
$unownedHead = (Invoke-Native git @('-C', $unowned, 'rev-parse', 'HEAD')).Trim()
Set-FixtureFile $unowned 'unrelated.txt' 'Stay on this branch'
Set-FixtureFile $publisher 'third-release.txt' 'Third release'
$release = Commit-Fixture $publisher
$sync = Sync-ShippedMaster $unowned $release (Join-Path $TestRoot 'sync-record-3')
Check (!$sync.Checkout -and $sync.Commit -ceq $release -and
    (Invoke-Native git @('-C', $unowned, 'rev-parse', 'HEAD')).Trim() -ceq $unownedHead) 'master without a checkout fast-forwards without switching the active branch'

# Conflicts must restore the original dirty checkout, not discard it.
Set-FixtureFile $local 'conflict.txt' 'Local conflicting work'
$beforeConflict = (Invoke-Native git @('-C', $local, 'rev-parse', 'HEAD')).Trim()
Set-FixtureFile $publisher 'conflict.txt' 'Upstream conflicting work'
$release = Commit-Fixture $publisher
Expect-Rejected { Sync-ShippedMaster $local $release (Join-Path $TestRoot 'sync-conflict') } 'conflicting local work prevents automatic synchronization'
Check ((Invoke-Native git @('-C', $local, 'rev-parse', 'HEAD')).Trim() -ceq $beforeConflict -and
    (Get-Content -LiteralPath (Join-Path $local 'conflict.txt') -Raw) -ceq 'Local conflicting work') 'conflict preflight restores local work and leaves master at its original commit'
Check ((Invoke-Native git @('-C', $local, 'show', ':staged.txt')) -ceq 'Staged draft' -and
    [Convert]::ToHexString([IO.File]::ReadAllBytes((Join-Path $local 'untracked.bin'))) -ceq [Convert]::ToHexString($bytes)) 'conflict recovery preserves index layers and untracked bytes'

$collision = Join-Path $TestRoot 'sync-collision'
Invoke-Native git @('clone', '--quiet', $origin, $collision); Configure-FixtureGit $collision
Set-FixtureFile $collision 'collision.txt' 'Local untracked work'
Set-FixtureFile $publisher 'collision.txt' 'Different upstream file'
$release = Commit-Fixture $publisher
Expect-Rejected { Sync-ShippedMaster $collision $release (Join-Path $TestRoot 'sync-collision-record') } 'unrelated untracked collision cannot overwrite upstream or local data'
Check ((Get-Content -LiteralPath (Join-Path $collision 'collision.txt') -Raw) -ceq 'Local untracked work') 'untracked collision restores the original local file'

$diverged = Join-Path $TestRoot 'sync-diverged'
Invoke-Native git @('clone', '--quiet', $origin, $diverged); Configure-FixtureGit $diverged
Set-FixtureFile $diverged 'local-commit.txt' 'Local commit'
Invoke-Native git @('-C', $diverged, 'add', '--', 'local-commit.txt')
Invoke-Native git @('-C', $diverged, 'commit', '--quiet', '-m', 'Do not discard this commit')
$divergedHead = (Invoke-Native git @('-C', $diverged, 'rev-parse', 'HEAD')).Trim()
Expect-Rejected { Sync-ShippedMaster $diverged $release (Join-Path $TestRoot 'sync-diverged-record') } 'divergent local master cannot be reset or force-updated'
Check ((Invoke-Native git @('-C', $diverged, 'rev-parse', 'HEAD')).Trim() -ceq $divergedHead) 'divergence rejection preserves the local commit'

$behind = Join-Path $TestRoot 'sync-published-check'
Invoke-Native git @('clone', '--quiet', $origin, $behind)
$unknown = (Invoke-Native git @('-C', $diverged, 'rev-parse', 'HEAD')).Trim()
Expect-Rejected { Sync-ShippedMaster $behind $unknown (Join-Path $TestRoot 'sync-published-record') } 'a published commit absent from remote master cannot pass sync verification'

$clean = Join-Path $TestRoot 'sync-clean'
Invoke-Native git @('-c', 'protocol.file.allow=always', 'clone', '--quiet', '--recurse-submodules', $origin, $clean)
Configure-FixtureGit $clean; Configure-FixtureGit (Join-Path $clean 'sites')
Set-FixtureFile $clean 'old-stash.txt' 'Previous unrelated stash'
Invoke-Native git @('-C', $clean, 'stash', 'push', '--include-untracked', '--quiet')
$priorStash = (Invoke-Native git @('-C', $clean, 'rev-parse', 'stash@{0}')).Trim()
Set-FixtureFile (Join-Path $clean 'sites') 'local-service.txt' 'Unstaged service head'
Invoke-Native git @('-C', (Join-Path $clean 'sites'), 'add', '--all')
Invoke-Native git @('-C', (Join-Path $clean 'sites'), 'commit', '--quiet', '-m', 'Local service commit')
$cleanServiceHead = (Invoke-Native git @('-C', (Join-Path $clean 'sites'), 'rev-parse', 'HEAD')).Trim()
$sync = Sync-ShippedMaster $clean $release (Join-Path $TestRoot 'sync-clean-record')
Check (!$sync.RecoveryStash -and $sync.Commit -ceq $release) 'a clean master with only submodule work does not create a parent-repository stash'
Check (!(Test-Path -LiteralPath (Join-Path $clean 'old-stash.txt')) -and
    (Invoke-Native git @('-C', $clean, 'rev-parse', 'stash@{0}')).Trim() -ceq $priorStash -and
    (Invoke-Native git @('-C', (Join-Path $clean 'sites'), 'rev-parse', 'HEAD')).Trim() -ceq $cleanServiceHead) 'submodule-only sync neither reapplies an old stash nor changes the service checkout'
