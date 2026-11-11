#Requires -Version 7.0
# Runs the main BlackPearl.DesktopChecks suite (release-checks.ps1 and test.ps1 both use this).
# Its startup gate ("startup library loading keeps processing input") fails when the longest pause in input handling
# while the library loads reaches 350 ms. The app normally pauses 250-330 ms, so any other load on the machine (a build,
# an encode, an antivirus scan, a busy CI runner) can push one run over the limit. When that gate alone fails, the
# suite runs again, up to -Attempts times, and the busiest other processes are listed each time. A real regression
# fails every attempt. Any other failure stops at once.
[CmdletBinding()]
param([string]$Sdk = 'dotnet', [ValidateRange(1, 5)][int]$Attempts = 3)
$ErrorActionPreference = 'Stop'
$gate = 'FAIL: startup library loading keeps processing input'

function Get-BusiestProcesses {
    # CPU time used by each process over one second, as a share of the whole machine.
    $before = @{}; Get-Process | ForEach-Object { try { $before[$_.Id] = $_.TotalProcessorTime.TotalMilliseconds } catch { } }
    Start-Sleep -Seconds 1
    $cores = [Environment]::ProcessorCount
    Get-Process | ForEach-Object {
        try {
            if ($before.ContainsKey($_.Id) -and $_.Id -ne $PID) {
                [pscustomobject]@{ Name = $_.ProcessName; Id = $_.Id; Percent = ($_.TotalProcessorTime.TotalMilliseconds - $before[$_.Id]) / 10 / $cores }
            }
        }
        catch { }
    } | Where-Object Percent -ge 2 | Sort-Object Percent -Descending | Select-Object -First 5
}

for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
    $lines = [Collections.Generic.List[string]]::new()
    & $Sdk run --project tests/BlackPearl.DesktopChecks -c Release --no-build 2>&1 | ForEach-Object { $line = "$_"; $lines.Add($line); Write-Host $line }
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        if ($attempt -gt 1) { Write-Host "Desktop regression checks passed on attempt $attempt of $Attempts; earlier attempts failed only the load-sensitive startup gate." }
        exit 0
    }
    if (-not ($lines | Where-Object { $_.Contains($gate) })) { throw "Desktop regression checks failed with exit code $exitCode." }
    $gap = $lines | Select-String -Pattern 'Startup max input gap: ([\d.]+) ms' | Select-Object -Last 1 | ForEach-Object { $_.Matches[0].Groups[1].Value }
    if (!$gap) { $gap = '?' }
    $busy = Get-BusiestProcesses | ForEach-Object { '{0} ({1}) {2:N0}%' -f $_.Name, $_.Id, $_.Percent }
    $load = if ($busy) { 'Busiest other processes now: ' + ($busy -join ', ') + '.' } else { 'No other process is busy now.' }
    if ($attempt -eq $Attempts) {
        throw "The startup gate failed on all $Attempts attempts (last pause $gap ms, limit 350 ms). That is not noise: find what " +
            "now runs on the UI thread while the library loads, and compare with master on the same machine. $load"
    }
    Write-Warning "Startup gate failed on attempt $attempt of $Attempts (longest pause $gap ms, limit 350 ms). It is load-sensitive, so the suite runs again. $load"
}
