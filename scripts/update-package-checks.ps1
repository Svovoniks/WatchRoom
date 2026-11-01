# Offline checks for update installers: a small fixture app is installed under a separate test identity (no file
# associations or shortcuts) into a scratch folder, updated with an installer of only its changes, then uninstalled.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'package-updates.ps1')
$root = Split-Path -Parent $PSScriptRoot
$checks = 0
function Check { param([bool]$Value, [string]$Name) if (!$Value) { throw "FAIL: $Name" }; Write-Host "PASS: $Name"; $script:checks++ }
$guid = 'F3A1D2C4-5B6E-4F70-8A91-B2C3D4E5F607'
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$guid}_is1"
$defines = @("/DAppGuid=$guid", '/DTestInstall')
$artifacts = Join-Path $root 'artifacts'
$test = Join-Path $artifacts ('update-checks-' + [Guid]::NewGuid().ToString('N'))
# A space and non-ASCII letters, as in many profile folders.
$installed = Join-Path $test 'installed app ÿ Яблоко'
# The fixtures are copies of real files: antivirus products quarantine installers of a few tiny made-up files.
function Copy-Fixture {
    param([string]$Directory, [hashtable]$Files, [string[]]$Changed = @())
    foreach ($path in $Files.Keys) {
        $target = Join-Path $Directory $path
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $Files[$path]) -Destination $target
        if ($path -in $Changed) { [IO.File]::AppendAllText($target, "`nChanged in the new version.`n") }
    }
}
try {
    $compiler = Get-InstallerCompiler $root
    $script = Join-Path $root 'packaging/BlackPearl.iss'
    $old = Join-Path $test 'old'; $new = Join-Path $test 'new'; $output = Join-Path $test 'out'
    $kept = @{ 'app.md' = 'AGENTS.md'; 'ISPP.dll' = '.nuget/packages/tools.innosetup/7.1.0/tools/ISPP.dll'; 'Models\notices.md' = 'THIRD-PARTY-NOTICES.md'; 'sub dir\changed.md' = 'README.md' }
    Copy-Fixture $old ($kept + @{ 'gone\old.md' = 'UPDATES.md' })
    Copy-Fixture $new ($kept + @{ 'added\new {1}.md' = 'UPDATES.md' }) @('app.md', 'sub dir\changed.md')
    & $compiler /Qp '/DAppVersion=0.0.1' "/DAppFiles=$old" "/DOutputFolder=$output" @defines $script
    if ($LASTEXITCODE -ne 0) { throw 'Fixture installer compilation failed' }
    Install-Silently (Join-Path $output 'BlackPearl-Setup-0.0.1-win-x64.exe') $installed (Join-Path $test 'install.log')
    Check ((Get-ItemProperty $key).DisplayVersion -eq '0.0.1') 'the fixture installs under its own test identity'

    $list = Join-Path $test 'BlackPearl-files-0.0.1-win-x64.txt'
    Write-FileList (Get-FileTable $old) $list
    $published = Read-FileList $list
    Assert-SameTable (Get-FileTable $installed) $published 'The installed fixture and its file list'
    Check (@($published.Values | Where-Object Path -eq 'sub dir\changed.md').Size -eq (Get-Item (Join-Path $old 'sub dir\changed.md')).Length) 'a published file list describes the installed release exactly'
    $update = New-UpdateInstaller $compiler $script '0.0.1' '0.0.2' $published $new (Join-Path $test 'stage') $output $defines
    Check (((@($update.Changed) | Sort-Object) -join '|') -eq 'added\new {1}.md|app.md|sub dir\changed.md') 'only new and changed files go into the update'
    Check ((@($update.Removed) -join '|') -eq 'gone\old.md') 'files the new version dropped are listed for deletion'
    $full = Get-Item (Join-Path $output 'BlackPearl-Setup-0.0.1-win-x64.exe')
    Check ((Get-Item $update.Path).Length -lt 4MB) "the update installer is small ($([Math]::Round((Get-Item $update.Path).Length / 1MB, 2)) MB, full fixture $([Math]::Round($full.Length / 1MB, 2)) MB)"

    $log = Join-Path $test 'update.log'
    Check ((Invoke-Installer $update.Path @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"", "/DIR=`"$installed`"")) -eq 0) 'the update installs over the release it was built from'
    Assert-SameFiles $installed $new 'The update'
    Check $true 'the updated folder matches the new build exactly'
    Check ((Get-ItemProperty $key).DisplayVersion -eq '0.0.2') 'the installation is registered as the new version'

    [IO.File]::WriteAllText((Join-Path $installed 'app.md'), 'touched')
    $code = Invoke-Installer $update.Path @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"", "/DIR=`"$installed`"")
    Check ($code -ne 0 -and [IO.File]::ReadAllText((Join-Path $installed 'app.md')) -eq 'touched') 'an update refuses an installation of another version'
    $elsewhere = Join-Path $test 'elsewhere'
    $code = Invoke-Installer $update.Path @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"", "/DIR=`"$elsewhere`"")
    Check ($code -ne 0 -and !(Test-Path (Join-Path $elsewhere 'app.md'))) 'an update refuses a folder without the installation'

    # An installation further behind runs a chain from a script like the one the app writes: each installer with
    # start /wait, stopping at the first failure.
    $newer = Join-Path $test 'newer'
    Copy-Item -LiteralPath $new -Destination $newer -Recurse
    [IO.File]::AppendAllText((Join-Path $newer 'Models\notices.md'), "`nChanged again.`n")
    $second = New-UpdateInstaller $compiler $script '0.0.2' '0.0.3' (Get-FileTable $new) $newer (Join-Path $test 'stage') $output $defines
    Uninstall-Silently $installed
    Install-Silently (Join-Path $output 'BlackPearl-Setup-0.0.1-win-x64.exe') $installed (Join-Path $test 'install.log')
    function Invoke-Chain {
        param([string[]]$Installers)
        $marker = Join-Path $test 'chain-failed'
        if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
        # As in the app: installers named relative to the script, the installation folder from the environment.
        $lines = @('@echo off') + @($Installers | ForEach-Object { "start `"`" /wait `"%~dp0$(Split-Path -Leaf $_)`" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `"/DIR=%BLACKPEARL_INSTALL%`""; 'if errorlevel 1 goto failed' }) +
            @('exit /b 0', ':failed', 'echo failed> "%~dp0..\chain-failed"', 'exit /b 1')
        $chain = Join-Path $output 'install-chain.cmd'
        [IO.File]::WriteAllLines($chain, [string[]]$lines)
        $env:BLACKPEARL_INSTALL = $installed
        try { $code = Invoke-Installer $env:ComSpec @('/d', '/s', '/c', "`"`"$chain`"`"") } finally { $env:BLACKPEARL_INSTALL = $null }
        return [pscustomobject]@{ Code = $code; Failed = (Test-Path -LiteralPath $marker) }
    }
    $run = Invoke-Chain @($update.Path, $second.Path)
    Assert-SameFiles $installed $newer 'The chain'
    Check ($run.Code -eq 0 -and !$run.Failed -and (Get-ItemProperty $key).DisplayVersion -eq '0.0.3') 'a chain of update installers brings an older installation to the newest build'
    $run = Invoke-Chain @($update.Path, $second.Path)
    Assert-SameFiles $installed $newer 'The refused chain'
    Check ($run.Code -ne 0 -and $run.Failed) 'a chain stops at the first refused step and runs its failure handling'

    Uninstall-Silently $installed
    Check (!(Test-Path $key) -and !(Test-Path (Join-Path $installed 'added\new {1}.md')) -and !(Test-Path (Join-Path $installed 'Models\notices.md'))) 'uninstalling removes files from the release and from the update'
    Write-Host "$checks update installer checks passed."
}
finally {
    if (Test-Path $key) { Uninstall-Silently $installed }
    $resolved = [IO.Path]::GetFullPath($test)
    if ((Split-Path -Parent $resolved) -eq [IO.Path]::GetFullPath($artifacts) -and (Split-Path -Leaf $resolved) -match '^update-checks-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
