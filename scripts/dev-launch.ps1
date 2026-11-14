#Requires -Version 7.0
# Starts this checkout's latest development build the safe way (see AGENTS.md, "Relaunch after app changes").
#   dev-launch.ps1                        close this checkout's running dev build and start the newest build on the usual library
#   dev-launch.ps1 -Scratch               copy the usual library first (scratch-library.ps1) and start on the copy
#   dev-launch.ps1 -Data <folder>         start on another library folder
#   dev-launch.ps1 -Arguments <file>      open a file, as a double-click would
#   dev-launch.ps1 -Stop                  only close this checkout's dev builds
# What it guards against, each of which has happened:
# - launching a build older than the sources, or one that crashes at start, and reporting it as running;
# - closing or replacing the user's installed app (never closed here; the script stops and says to ask);
# - two copies of BlackPearl on one library;
# - a build without the bundled TMDB key on the real library, where a refresh re-matched titles to TVmaze and Wikipedia;
# - a dev build updating itself over the installed app, or pointing auto-download's Windows task at itself.
[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$Data = '',
    [switch]$Scratch,
    [string[]]$Arguments = @(),
    # Start a build without the bundled TMDB key on the real library anyway. Say why in your report, and do not refresh,
    # rescan or identify anything in it.
    [switch]$AllowKeyless,
    # Also close dev builds from other checkouts, which may have the same library open.
    [switch]$CloseOtherDevBuilds,
    # Let the dev build register auto-download's task, link and notification identity with Windows (off by default).
    [switch]$FollowSystem,
    [switch]$Stop,
    [ValidateRange(5, 120)][int]$WaitSeconds = 30
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$programs = Join-Path $env:LOCALAPPDATA 'Programs'
function Test-Under([string]$Path, [string]$Folder) {
    $Path -and $Path.StartsWith($Folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Get-UsualData {
    $current = Join-Path $env:LOCALAPPDATA 'BlackPearl'; $former = Join-Path $env:LOCALAPPDATA 'Watchroom'
    if (!(Test-Path (Join-Path $current 'library.db')) -and (Test-Path (Join-Path $former 'library.db'))) { $former } else { $current }
}
function Close-DevBuild($Process) {
    Write-Host "Closing dev build $($Process.Id) ($($Process.Path))"
    $null = $Process.CloseMainWindow()
    if (!$Process.WaitForExit(20000)) { Write-Warning "Dev build $($Process.Id) did not close within 20 s; ending it."; $Process.Kill($true); $null = $Process.WaitForExit(5000) }
}

$running = @(Get-Process -Name BlackPearl, Watchroom -ErrorAction SilentlyContinue | ForEach-Object {
    $path = try { $_.Path } catch { $null }
    [pscustomobject]@{ Process = $_; Path = $path; Installed = (Test-Under $path $programs); Mine = (Test-Under $path $root) }
})
foreach ($dev in $running | Where-Object Mine) { Close-DevBuild $dev.Process }
if ($Stop) { return }

# The newest build in this checkout: the Release output, or a self-contained publish under artifacts.
$chosen = [bool]$Exe
if (!$Exe) {
    $candidates = @(Get-ChildItem (Join-Path $root 'src/BlackPearl.Desktop/bin') -Recurse -Filter BlackPearl.exe -ErrorAction SilentlyContinue) +
        @(Get-ChildItem (Join-Path $root 'artifacts') -Recurse -Filter BlackPearl.exe -Depth 2 -ErrorAction SilentlyContinue)
    # The apphost .exe is not rewritten by every build; BlackPearl.dll beside it is.
    $Exe = $candidates | Where-Object { Test-Path (Join-Path $_.DirectoryName 'BlackPearl.dll') } |
        Sort-Object { (Get-Item (Join-Path $_.DirectoryName 'BlackPearl.dll')).LastWriteTimeUtc } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (!$Exe) { throw "No build found in $root. Run ./build.ps1 first." }
}
$Exe = (Resolve-Path $Exe).Path
if (Test-Under $Exe $programs) { throw "$Exe is an installed copy. This script only starts development builds." }
$folder = Split-Path $Exe
$built = (Get-Item (Join-Path $folder 'BlackPearl.dll')).LastWriteTime
# Only the app's own projects count: a change to the room server alone leaves BlackPearl.dll as it was.
$newestSource = @('BlackPearl.Desktop', 'BlackPearl.Core') | ForEach-Object { Get-ChildItem (Join-Path $root "src/$_") -Recurse -File -Include *.cs, *.xaml, *.csproj, *.png, *.ico, *.ttf, *.otf } |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newestSource -and $newestSource.LastWriteTime -gt $built.AddSeconds(2)) {
    $stale = "The build in $folder ($built) is older than $($newestSource.FullName) ($($newestSource.LastWriteTime))."
    # A build named with -Exe is the one meant, such as one built to another folder; say so and start it.
    if ($chosen) { Write-Warning "$stale Starting it because -Exe names it." }
    else { throw "$stale Run ./build.ps1, or pass -Exe with the build you mean." }
}

if ($Scratch) {
    if ($Data) { throw 'Use either -Scratch or -Data.' }
    # Where scratch-library.ps1 puts copies by default, outside %TEMP% (see there).
    $Data = Join-Path $env:LOCALAPPDATA ('BlackPearl-scratch\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    & (Join-Path $PSScriptRoot 'scratch-library.ps1') -Destination $Data
}
$usual = Get-UsualData
if (!$Data) { $Data = $usual }
$Data = [IO.Path]::GetFullPath($Data).TrimEnd('\')
$onUsual = $Data -ieq $usual.TrimEnd('\')

if ($onUsual) {
    $installed = @($running | Where-Object Installed)
    if ($installed) {
        Write-Host "The installed BlackPearl ($(($installed.Process.Id) -join ', ')) has the user's library open."
        Write-Host 'Do not close it without asking the user. Ask them, or start on a copy: ./scripts/dev-launch.ps1 -Scratch'
        exit 2
    }
    $others = @($running | Where-Object { !$_.Installed -and !$_.Mine })
    if ($others -and !$CloseOtherDevBuilds) {
        Write-Host 'Dev builds from other checkouts are running and may have the same library open:'
        $others | ForEach-Object { Write-Host "  $($_.Process.Id)  $($_.Path)" }
        Write-Host 'Two copies on one library can overwrite each other. Rerun with -CloseOtherDevBuilds, or start on -Scratch.'
        exit 2
    }
    foreach ($other in $others) { Close-DevBuild $other.Process }
}

# The bundled key is an embedded resource; its name is stored once, as plain text, in the assembly's metadata.
$core = Join-Path $folder 'BlackPearl.Core.dll'
$keyed = (Test-Path $core) -and [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($core)).Contains('BlackPearl.TmdbKey')
if (!$keyed) {
    $message = 'This build has no bundled TMDB key (it was built without BLACKPEARL_TMDB_KEY_FILE). Without a key saved in the library, metadata comes from TVmaze and Wikipedia instead.'
    if ($onUsual -and !$AllowKeyless) {
        Write-Host $message
        Write-Host 'On the real library, a refresh, rescan or identification in this build re-matches titles away from TMDB.'
        Write-Host 'Start on a copy (-Scratch), build with BLACKPEARL_TMDB_KEY_FILE set, or pass -AllowKeyless for a change that'
        Write-Host 'touches no metadata, and then refresh nothing.'
        exit 2
    }
    Write-Warning $message
}

$variables = @{ BLACKPEARL_DATA = $Data; BLACKPEARL_DISABLE_UPDATES = '1' }
if (!$FollowSystem) { $variables.BLACKPEARL_FOLLOW_SYSTEM = 'off' }
$saved = @{}
foreach ($name in $variables.Keys) { $saved[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $variables[$name]) }
$started = Get-Date
try {
    $start = @{ FilePath = $Exe; WorkingDirectory = $folder; PassThru = $true }
    if ($Arguments) { $start.ArgumentList = $Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } } }
    $process = Start-Process @start
}
finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }

# Started is not running: wait for the window, and report a crash or a hand-off to another copy instead of success.
$deadline = $started.AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    $process.Refresh()
    if ($process.HasExited -or $process.MainWindowHandle -ne 0) { break }
    Start-Sleep -Milliseconds 250
}
if ($process.HasExited) {
    Write-Host "The dev build exited with code $($process.ExitCode) before showing a window."
    if ($Arguments -and $process.ExitCode -eq 0) { Write-Host 'It may have handed the file to a copy of BlackPearl already running on this library.' }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime', 'Application Error'; StartTime = $started.AddSeconds(-2) } -MaxEvents 3 -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "--- $($_.ProviderName) at $($_.TimeCreated)"; Write-Host (($_.Message -split "`n" | Select-Object -First 12) -join "`n") }
    exit 1
}
if ($process.MainWindowHandle -eq 0) { Write-Warning "No window after $WaitSeconds s; the process ($($process.Id)) is still running. Look before reporting it as started." }

$commit = git -C $root rev-parse --short HEAD 2>$null
$dirty = if (git -C $root status --porcelain 2>$null) { ' plus uncommitted changes' } else { '' }
Write-Host "Running dev build $($process.Id): $Exe"
Write-Host "  built $built from $commit$dirty"
Write-Host "  library $Data$(if ($onUsual) { " (the user's)" } else { '' })$(if ($keyed) { ', bundled TMDB key' } else { ', no TMDB key' })"
