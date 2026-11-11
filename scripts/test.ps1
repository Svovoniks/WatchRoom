$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & ./build.ps1
    $sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
    if (!(Test-Path $sdk)) { $sdk = 'dotnet' }
    # The main desktop suite's responsiveness gates are timing-sensitive, so it runs alone before the smoke tests start.
    & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'desktop-main-suite.ps1') -Sdk $sdk
    if ($LASTEXITCODE -ne 0) { throw 'Desktop layout tests failed' }
    # A fresh folder for every run: the smoke suite's default, artifacts/smoke, keeps the last run's library and cached
    # responses, which made reruns fail in ways that looked like regressions.
    $smokeDirectory = Join-Path ([IO.Path]::GetTempPath()) ('wr-smoke-' + [guid]::NewGuid().ToString('N'))
    $smokeLog = "$smokeDirectory.log"
    $smoke = Start-Process -FilePath $sdk -ArgumentList @('run', '--project', 'tests/BlackPearl.Smoke', '-c', 'Release', '--no-build', '--', "`"$smokeDirectory`"") `
        -WorkingDirectory $root -RedirectStandardOutput $smokeLog -RedirectStandardError "$smokeLog.err" -NoNewWindow -PassThru
    $null = $smoke.Handle # keeps the exit code readable after the process ends
    & $sdk run --project tests/BlackPearl.DesktopChecks -c Release --no-build -- --ui-findings
    if ($LASTEXITCODE -ne 0) { throw 'UI findings regression tests failed' }
    & $sdk run --project tests/BlackPearl.DesktopChecks -c Release --no-build -- --ui-experience
    if ($LASTEXITCODE -ne 0) { throw 'UI experience regression tests failed' }
    & $sdk run --project tests/BlackPearl.DesktopChecks -c Release --no-build -- --guest-library-layout
    if ($LASTEXITCODE -ne 0) { throw 'Guest library layout tests failed' }
    $smoke.WaitForExit()
    Get-Content -LiteralPath $smokeLog, "$smokeLog.err" -Tail 20 | Write-Host
    if ($smoke.ExitCode -ne 0) { throw "Smoke tests failed; output in $smokeLog" }
} finally {
    if ($smoke -and !$smoke.HasExited) { $smoke.Kill($true) }
    Pop-Location
}
