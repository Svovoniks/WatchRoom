#Requires -Version 7.0
# The release checks, in one list shared by ship.ps1 and the Build and release workflow so the two cannot drift apart.
# Run from a checkout that already has a Release build of BlackPearl.slnx.
#   release-checks.ps1 -Lane all       every check on one machine (ship.ps1); the full smoke suite runs beside the rest
#   release-checks.ps1 -Lane desktop   the update, TMDB and desktop suites (one CI runner)
#   release-checks.ps1 -Lane smoke-1   the shipping script checks and the first half of the full smoke suite
#   release-checks.ps1 -Lane smoke-2   the second half of the full smoke suite, with the metadata cache, shared library
#                                      transport and room checks
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('all', 'desktop', 'smoke-1', 'smoke-2')][string]$Lane,
    [string]$Sdk = 'dotnet'
)
$ErrorActionPreference = 'Stop'

# Each runs as its own BlackPearl.DesktopChecks process after the main desktop suite.
$desktopSuites = @(
    '--acquisition-layout', '--home', '--ui-findings', '--ui-experience', '--guest-library-layout', '--host-library-browse',
    '--player-layout', '--metadata-picker', '--room-playback-status', '--language-detection', '--voice-processing', '--video-frames')

function Invoke-Check {
    param([string]$Name, [string]$Command, [string[]]$Arguments)
    $grouped = $env:GITHUB_ACTIONS -eq 'true'
    if ($grouped) { Write-Host "::group::$Name" }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
    }
    finally { if ($grouped) { Write-Host '::endgroup::' } }
    Write-Host ("{0} passed in {1:N0} s" -f $Name, $watch.Elapsed.TotalSeconds)
}
function Invoke-Project {
    param([string]$Name, [string]$Project, [string[]]$Arguments = @())
    $runArguments = @('run', '--project', "tests/$Project", '-c', 'Release', '--no-build')
    if ($Arguments.Count) { $runArguments += @('--') + $Arguments }
    Invoke-Check $Name $Sdk $runArguments
}
# Some nested metadata fixtures create long SQLite backup filenames, so the smoke suite gets a short fresh directory
# under the temp folder.
function New-SmokeDirectory { Join-Path ([IO.Path]::GetTempPath()) ('wr-smoke-' + [guid]::NewGuid().ToString('N')) }
# The main desktop suite's input responsiveness gates are timing-sensitive, so it runs before anything else starts.
function Invoke-MainDesktopSuite { Invoke-Project 'Desktop regression checks' 'BlackPearl.DesktopChecks' }
function Invoke-DesktopSuites {
    Invoke-Project 'Update safety checks' 'BlackPearl.UpdateChecks'
    Invoke-Project 'TMDB access and retention checks' 'BlackPearl.Smoke' @('--tmdb-access')
    foreach ($suite in $desktopSuites) { Invoke-Project "Desktop checks $suite" 'BlackPearl.DesktopChecks' @($suite) }
}
function Invoke-ShipScriptChecks {
    $shell = (Get-Process -Id $PID).Path
    Invoke-Check 'Shipping script checks' $shell @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'ship-checks.ps1'))
}

switch ($Lane) {
    'desktop' { Invoke-MainDesktopSuite; Invoke-DesktopSuites }
    'smoke-1' {
        Invoke-ShipScriptChecks
        Invoke-Project 'Full smoke suite, part 1 of 2' 'BlackPearl.Smoke' @((New-SmokeDirectory), '--part', '1')
    }
    'smoke-2' { Invoke-Project 'Full smoke suite, part 2 of 2' 'BlackPearl.Smoke' @((New-SmokeDirectory), '--part', '2') }
    'all' {
        Invoke-MainDesktopSuite
        # The whole smoke suite, the longest, then runs in the background beside everything else.
        $smokeDirectory = New-SmokeDirectory
        $smokeLog = "$smokeDirectory.log"
        $smoke = Start-Process -FilePath $Sdk -ArgumentList @('run', '--project', 'tests/BlackPearl.Smoke', '-c', 'Release', '--no-build', '--', "`"$smokeDirectory`"") `
            -WorkingDirectory $PWD.Path -RedirectStandardOutput $smokeLog -RedirectStandardError "$smokeLog.err" -NoNewWindow -PassThru
        $null = $smoke.Handle # keeps the exit code readable after the process ends
        try {
            Invoke-DesktopSuites
            Invoke-ShipScriptChecks
            Write-Host 'Waiting for the full smoke suite...'
            $smoke.WaitForExit()
            Get-Content -LiteralPath $smokeLog, "$smokeLog.err" -Tail 20 | Write-Host
            if ($smoke.ExitCode -ne 0) { throw "The full smoke suite failed with exit code $($smoke.ExitCode). Output: $smokeLog" }
            Write-Host 'Full smoke suite passed'
        }
        finally { if (!$smoke.HasExited) { $smoke.Kill($true) } }
    }
}
