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
    Check ((Assert-ShipPath $testRoot 'src/app.cs') -eq 'src/app.cs') 'explicit source file is accepted'
    foreach ($path in @('../outside.cs','artifacts/report.html','sites/src/app.ts','.tools/helper.ps1','src/credentials/token.json','src/.env','src/library.db')) {
        Expect-Rejected { Assert-ShipPath $testRoot $path } "excluded path $path cannot enter a release"
    }
    Expect-Rejected { Assert-ShipPath $testRoot 'src' } 'directory selection cannot stage unrelated files'
    $fileName = 'Watchroom-Setup-0.3.18-win-x64.exe'
    $filePath = Join-Path $testRoot $fileName
    [IO.File]::WriteAllBytes($filePath, [byte[]](1,2,3,4))
    $hash = (Get-FileHash $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = [pscustomobject]@{ name=$fileName; size=4; digest="sha256:$hash"; browser_download_url="https://github.com/Svovoniks/WatchRoom/releases/download/v0.3.18/$fileName" }
    $sumPath = Join-Path $testRoot 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($sumPath, "$hash  $fileName`n")
    $release = [pscustomobject]@{ tag_name='v0.3.18'; draft=$false; prerelease=$false; assets=@($asset, [pscustomobject]@{name='SHA256SUMS.txt';browser_download_url='https://github.com/Svovoniks/WatchRoom/releases/download/v0.3.18/SHA256SUMS.txt'}) }
    $null = Assert-ReleaseMetadata $release '0.3.18'
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
