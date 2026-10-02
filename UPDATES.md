# Builds and app updates

GitHub Actions builds the Windows x64 app and installer on pushes, pull requests,
and manual runs. The workflow checks metadata caching, desktop interactions, and
update download verification before packaging. Successful builds retain installers
as workflow artifacts for 14 days.

Push a version tag such as `v0.2.1` to publish a GitHub Release. Tags must use
`vMAJOR.MINOR.PATCH`; the same version is embedded in the application and installer.
The workflow uploads `Watchroom-Setup-VERSION-win-x64.exe` and `SHA256SUMS.txt`.
Only tagged builds publish releases. Do not replace an existing release with
different bytes; publish a new version instead.

The Windows app checks GitHub's latest stable release 15 seconds after startup
and every six hours. Settings offers **Check for updates** and a saved preference
to disable automatic checks. Checks do not install anything. A newer release
shows an update card; **Later** hides that version until a manual check or a
different release.

**Update** streams the installer into the profile's `updates` folder, reports
progress, and verifies its length and GitHub-provided SHA-256 digest. Failed or
interrupted downloads never launch an installer. After successful verification,
the per-user Inno Setup installer updates the running app's directory, and
reopens Watchroom. Library data, rooms, queues, and credentials remain outside the
installation directory. Updating closes playback and disconnects the current room.
Installation failures are handled by Inno Setup; its setup log is available for
diagnosis. The binaries currently have no publisher code-signing certificate.
Checksums protect against transfer corruption, not a compromised GitHub account.

Update downloads require a public releases repository. No GitHub token is stored
in the distributed app. `UpdateRepository` is an MSBuild property baked into the
Windows assembly; it defaults to `Svovoniks/WatchRoom`.

To test: `dotnet run --project tests/Watchroom.UpdateChecks -c Release`.
For isolated desktop tests set `WATCHROOM_DISABLE_UPDATES=1`; normal users do not
need this environment variable.
