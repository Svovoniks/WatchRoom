param([ValidateSet('public','local')][string]$Mode,[Parameter(Mandatory)][string]$PasswordFile,[int]$SteadySeconds=180,[string]$TestRoot='artifacts/sync-followup/desktop',[string]$GuestRoot='C:\Users\vmuser\Desktop\WatchroomSyncFollowup',[int]$StartPhase=0,[switch]$LaunchGuest,[int]$HostSequenceOffset=0,[string]$GuestExecutable,[switch]$ExperimentalSync)
$ErrorActionPreference='Stop'
$testRootFull=(Resolve-Path $TestRoot).Path
$testVBox='C:\Program Files\Oracle\VirtualBox\VBoxManage.exe'
$testGuest="$GuestRoot\$Mode-guest"
$testExecutable=if($GuestExecutable){$GuestExecutable}else{"$GuestRoot\app\Watchroom.exe"}
$testPolicy=if($ExperimentalSync){"1"}else{"0"}
$testInvite=Get-Content -Raw -LiteralPath (Join-Path $testRootFull "$Mode-host/test-invite.txt")
if($StartPhase -eq 0 -or $LaunchGuest){
& $testVBox guestcontrol 'Windows 11' start --username vmuser --passwordfile $PasswordFile --exe $testExecutable --putenv "WATCHROOM_DATA=$testGuest" --putenv "WATCHROOM_DIAGNOSTICS=$testGuest" --putenv 'WATCHROOM_TEST_ROLE=guest' --putenv "WATCHROOM_TEST_INVITE=$testInvite" --putenv "WATCHROOM_SYNC_EXPERIMENTAL=$testPolicy"
if($LASTEXITCODE -ne 0){throw 'Guest launch failed'}
$testReadyDeadline=[DateTimeOffset]::UtcNow.AddMinutes(3)
do {
 Start-Sleep -Seconds 5
 $testReady=& $testVBox guestcontrol 'Windows 11' run --username vmuser --passwordfile $PasswordFile --exe 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' --wait-stdout --timeout 10000 -- -NoProfile -Command "if(Test-Path '$testGuest\diagnostics.jsonl'){Get-Content '$testGuest\diagnostics.jsonl' -Tail 100 | ForEach-Object {try{ConvertFrom-Json -InputObject `$_}catch{}} | Where-Object {`$_.kind -eq 'sample'} | Select-Object -Last 1 | ForEach-Object {if(`$_.data.ready -and `$_.data.connected){'READY'}}}"
 if([DateTimeOffset]::UtcNow -gt $testReadyDeadline){throw 'Guest did not become ready before the test deadline'}
} until ($testReady -contains 'READY')
Write-Output 'Guest connected and ready; starting matrix.'
if($StartPhase -eq 0){
$testNow=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$testCaptureSchedule=(@(($testNow+60000),($testNow+120000))|ForEach-Object { $_.ToString() }) -join "`n"
$testCaptureHost=Join-Path $testRootFull "$Mode-host/test-captures.txt"
[IO.File]::WriteAllText($testCaptureHost+'.tmp',$testCaptureSchedule)
[IO.File]::Move($testCaptureHost+'.tmp',$testCaptureHost,$true)
$testCaptureTransfer=Join-Path $testRootFull 'test-captures.txt'
[IO.File]::WriteAllText($testCaptureTransfer,$testCaptureSchedule)
& $testVBox guestcontrol 'Windows 11' copyto --username vmuser --passwordfile $PasswordFile $testCaptureTransfer "$testGuest\test-captures.txt"
if($LASTEXITCODE -ne 0){throw 'Frame capture schedule transfer failed'}
} 
}
$testPhases=@(
 @('host',1,'play',0,$SteadySeconds),@('host',2,'pause',0,8),
 @('host',3,'seek',90000,8),@('host',4,'play',0,20),
 @('host',5,'toggle',0,8),@('host',6,'pause',0,5),
 @('guest',1,'seek',150000,8),@('host',7,'controls',1,6),
 @('guest',2,'play',0,20),@('guest',3,'pause',0,8),
 @('guest',4,'seek',150000,8),@('host',8,'controls',0,6),
 @('guest',5,'seek',30000,8),@('host',9,'stop',0,8))
for($testPhaseIndex=$StartPhase;$testPhaseIndex -lt $testPhases.Count;$testPhaseIndex++){
 $testPhase=$testPhases[$testPhaseIndex]
 $testRole,$testSequence,$testAction,$testPosition,$testWait=$testPhase
 if($testRole -eq "host"){$testSequence += $HostSequenceOffset}
 $testData=@{sequence=$testSequence;action=$testAction;position=$testPosition}|ConvertTo-Json -Compress
 Add-Content (Join-Path $testRootFull "$Mode-phases.jsonl") (@{serverMs=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();monoMs=[Environment]::TickCount64;role=$testRole;sequence=$testSequence;action=$testAction;position=$testPosition;wait=$testWait}|ConvertTo-Json -Compress)
 if($testRole -eq 'host'){
  $testFile=Join-Path $testRootFull "$Mode-host/test-command.json"
  [IO.File]::WriteAllText($testFile+'.tmp',$testData)
  [IO.File]::Move($testFile+'.tmp',$testFile,$true)
 }else{
  $testFile=Join-Path $testRootFull 'guest-command.json.tmp'
  [IO.File]::WriteAllText($testFile,$testData)
  & $testVBox guestcontrol 'Windows 11' copyto --username vmuser --passwordfile $PasswordFile $testFile "$testGuest\test-command.json.tmp"
  if($LASTEXITCODE -ne 0){throw 'Guest command transfer failed'}
  & $testVBox guestcontrol 'Windows 11' run --username vmuser --passwordfile $PasswordFile --exe 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' --arg0 powershell.exe --wait-stdout --wait-stderr --timeout 20000 -- -NoProfile -Command "Move-Item -LiteralPath '$testGuest\test-command.json.tmp' -Destination '$testGuest\test-command.json' -Force"
  if($LASTEXITCODE -ne 0){throw 'Guest atomic command transfer failed'}
 }
 Write-Output "$Mode $testRole $testAction $testPosition ($testWait seconds)"
 Start-Sleep -Seconds $testWait
}
# A unified log includes native-time, seeks, settling, cache, rates, reads and clocks.
& $testVBox guestcontrol 'Windows 11' copyfrom --username vmuser --passwordfile $PasswordFile "$testGuest\diagnostics.jsonl" (Join-Path $testRootFull "$Mode-guest/diagnostics.jsonl")
if($LASTEXITCODE -ne 0){throw 'Guest diagnostic collection failed'}
New-Item -ItemType Directory -Force (Join-Path $testRootFull "$Mode-guest-collected") | Out-Null
& $testVBox guestcontrol 'Windows 11' copyfrom --username vmuser --passwordfile $PasswordFile --recursive "$testGuest\" (Join-Path $testRootFull "$Mode-guest-collected")
if($LASTEXITCODE -ne 0){throw 'Guest frame collection failed'}
Write-Output "$Mode host-to-VM matrix complete."

