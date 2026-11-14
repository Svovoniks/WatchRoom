#Requires -Version 7.0
# Copies the user's library to a scratch folder for trying a change on real data (see AGENTS.md, "Try it on real data").
# The copy gets its own artwork, plugins and metadata cache, and every stored path into the library folder is rewritten
# to the copy, so nothing that runs on it can change the user's library, posters or settings. The videos stay where they
# are and are only read.
#   scratch-library.ps1                         copy the usual library to a new folder under %LOCALAPPDATA%\BlackPearl-scratch
#   scratch-library.ps1 -Destination <folder>   copy it to <folder>, which must be new or empty
#   scratch-library.ps1 -Source <folder>        copy another library folder
# Then point the app or a check at the copy: dev-launch.ps1 -Data <copy>, or $env:BLACKPEARL_DATA = '<copy>'.
[CmdletBinding()]
param([string]$Source = '', [string]$Destination = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (!$Source) {
    # The app's own choice (Program.DefaultData): the BlackPearl folder, or the Watchroom one when only it has a library.
    $current = Join-Path $env:LOCALAPPDATA 'BlackPearl'; $former = Join-Path $env:LOCALAPPDATA 'Watchroom'
    $Source = if (!(Test-Path (Join-Path $current 'library.db')) -and (Test-Path (Join-Path $former 'library.db'))) { $former } else { $current }
}
# Not under %TEMP%: copies keep the artwork's own dates, so Windows' temporary-file cleanup (Storage Sense) takes artwork
# more than a week old out of a copy there, and the copy's posters go blank.
if (!$Destination) { $Destination = Join-Path $env:LOCALAPPDATA ('BlackPearl-scratch\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$sdk = Join-Path $root '.tools/dotnet/dotnet.exe'
if (!(Test-Path $sdk)) { $sdk = 'dotnet' }

Push-Location $root
try {
    & $sdk run --project tests/BlackPearl.DesktopChecks -c Release -- --copy-library $Source $Destination
    if ($LASTEXITCODE -ne 0) { throw "Copying the library failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
$Destination = (Get-Item -LiteralPath $Destination).FullName # the long form of a short (8.3) temp path

Write-Host ''
Write-Host "Scratch library: $Destination"
Write-Host "  Launch on it:  ./scripts/dev-launch.ps1 -Data '$Destination'"
Write-Host "  Or set:        `$env:BLACKPEARL_DATA = '$Destination'"
Write-Host 'A saved personal TMDB key belongs to its library folder, so the copy has none. Builds without the bundled key fall'
Write-Host 'back to TVmaze and Wikipedia, so metadata results on the copy can differ from the installed app.'
Write-Host 'Delete the folder when you are done.'
