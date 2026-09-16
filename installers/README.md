# Agent Signaler installers

Preferred installation entry point: **AgentSignaler.Dashboard.Setup.exe**.
Build on Windows x64 with the existing .NET 10 SDK, Windows SDK, and Visual Studio
x64 C++ tools (the custom native bootstrapper has no additional runtime dependency):

```powershell
.\scripts\Build-Installers.ps1 -Version 1.0.13
```

The script uses configured NuGet sources. It restores and publishes the dashboard,
Client, Configurator, and Relay as self-contained Windows x64 applications, then builds
both application MSIs, validates them, then builds the Dev Tunnels prerequisite MSI
and custom WiX Burn 4.0.6 bundle. The remote applications are published separately and merged;
duplicate files must have identical SHA-256 hashes, rather than silently replacing
potentially incompatible runtime or shared assemblies. WiX Heat harvests each
complete publish tree. A build-time transform adds per-user HKCU component key
paths and empty-directory removal; a deterministic UUID helper gives those
components stable GUIDs across versions without relying on file versions.

Outputs:

* `artifacts\publish\dashboard`
* `artifacts\publish\remote`
* `artifacts\msi\AgentSignaler.Dashboard.msi`
* `artifacts\msi\AgentSignaler.Remote.msi`
* `artifacts\msi\AgentSignaler.DevTunnelsPrerequisite.msi`
* `artifacts\bundle\AgentSignaler.Dashboard.Setup.exe`

The default build retains all local artifacts and does not copy, install, or
publish them. To copy the validated application MSIs and bundle to an explicitly
trusted release folder, pass an absolute local or UNC path:

```powershell
.\scripts\Build-Installers.ps1 -Version 1.0.13 `
  -DestinationPath C:\Releases\AgentSignaler
```

Installer tests never execute a bundle/MSI.

The manually triggered **Release MSIs** GitHub Actions workflow builds and
validates the complete installer set, then publishes only
`AgentSignaler.Dashboard.msi`, `AgentSignaler.Remote.msi`, and their SHA-256
checksums to a versioned GitHub Release. The workflow requires explicit
confirmation that the packages are unsigned, marks releases as prereleases by
default, and refuses to replace an existing release. Its release notes warn that
Windows may display SmartScreen, publisher, or reputation prompts.

`-SkipPublish` rebuilds packages from existing publish trees; use it only after a
successful publish of the desired version. The script never installs either MSI,
opens firewall ports, or registers hooks or scheduled tasks.

The solution lists the WiX projects with `<Build Project="false" />` so ordinary
solution builds do not require published payloads or invoke WiX. Build the packages
through the script instead. WiX projects target `native`, not the root .NET target.
The script explicitly rebuilds MSI projects so changing only `-Version` cannot
reuse incremental output carrying the previous product version.

## WiX prerequisite

These projects pin `WixToolset.Sdk` and `WixToolset.Heat` 4.0.6. Restore obtains
the compiler and harvester; no separately installed WiX command-line tool is needed.
WiX 4 builds without the WiX 7 maintenance-fee EULA acceptance gate. No build script
accepts third-party license agreements on your behalf.

The native BA compiles against SHA-256-pinned, upstream WiX **v4.0.6** ABI headers
downloaded into `artifacts\installer-tools`. Their upstream Microsoft Reciprocal
License is at https://github.com/wixtoolset/wix/blob/v4.0.6/LICENSE.TXT.
It uses Win32 UI, Windows trust/MSI/package APIs, and a statically linked C++ runtime;
Dashboard and Remote remain self-contained .NET 10 / WinUI 3 x64 applications.

## Prerequisite selection, detection and consent

The interactive bundle shows **Installed**, **Update required**, or **Missing**.
Azure CLI and Dev Tunnels are preselected when not supported; either can be declined,
with one explicit warning before continuing. License buttons open the Microsoft
license pages. Acceptance is a hidden, non-persisted Burn variable for this run only.
Windows App has a Microsoft Store button and **Check again**; it is never chained.
Setup never signs in, changes PATH, creates credentials, or performs Azure operations.

The chain is Azure CLI, Dev Tunnels, then the existing per-user Dashboard MSI.
Azure CLI detection requires 64-bit Microsoft MSI family metadata plus a trusted,
Microsoft-signed x64 `%ProgramFiles%\Microsoft SDKs\Azure\CLI2\wbin\az.exe` at least
2.90.0. Dev Tunnels discovery checks the existing WinGet link first, then
`%LOCALAPPDATA%\Programs\Microsoft Dev Tunnels CLI\devtunnel.exe`; exact version
`1.0.2030+fc9273aa0f`, signature, x64 architecture, pinned hash and bounded `--version`
output are validated. Newer prerequisites are not downgraded. An unsupported newer
CLI is retained and requires explicit decline or remediation, not replacement.
The interactive status and warning explicitly explain that it will never be
downgraded and let the user clear its installation checkbox without restarting
Setup. Quiet/passive selection of an unsupported newer prerequisite exits 1638
before Burn planning; a concurrent upgrade found immediately before execution
also aborts rather than overwriting it.
Windows App requires package family `MicrosoftCorporationII.Windows365_8wekyb3d8bbwe`,
version 2.0.804.0 or later, and merged `ms-cloudpc` registration.

Accepted prerequisites are re-detected before MSI execution and before Dashboard
is allowed to run. Concurrent prerequisite upgrades abort safely instead of being
overwritten. Both prerequisite packages are permanent in Burn: bundle repair,
upgrade, rollback, and uninstall never uninstall them. Only Dashboard is removed.
The CLI wrapper MSI is per-user, has its own stable upgrade code and contains only
the pinned executable plus its HKCU MSI component key path; it does not modify PATH.

Quiet and passive examples (run only during an authorized deployment):

```powershell
.\AgentSignaler.Dashboard.Setup.exe /quiet ACCEPT_PREREQUISITE_LICENSES=1
.\AgentSignaler.Dashboard.Setup.exe /passive ACCEPT_PREREQUISITE_LICENSES=1
```

Both unattended modes require that exact property, including on user-requested
maintenance runs. The sole exception is the old bundle's uninstall launched by
Burn during a major upgrade (`UNINSTALL` action plus `UPGRADE` relationship).
That removal neither selects nor installs prerequisites, and Burn does not forward
the new bundle's consent property to it. No install, repair, cache, layout, ordinary
uninstall, or other related-bundle action gains this exemption.
Missing, malformed, or duplicate consent exits **5100 before detection, planning,
download or installation**. Passive mode displays progress; quiet mode has no UI.
Interactive cancellation returns 1602. Success returns 0 or 3010 (restart required);
Setup never automatically restarts Windows. Unsupported actions return 87,
unsupported OS/native architecture 1633, payload acquisition failures 5101, integrity failures 5102, accepted prerequisite
re-detection failures 5104, and downgrade/race conflicts 1638. Other MSI failures
retain their Win32 error code. First failure wins over cancellation/restart.
Malformed installed MSI versions and expected native detection errors fail explicitly
with 5104; they are never treated as a missing installation or a downgrade opportunity.
Burn variable failures retain their HRESULT (Win32-facility HRESULTs use the Win32
code). Allocation failure maps specifically to `E_OUTOFMEMORY` / exit 14.
Only typed expected native failures and `std::bad_alloc` are handled at native
boundaries; unexpected programming faults fail fast rather than being relabeled
as prerequisite failures or allocation failures.
BA logs contain only fixed product identifiers, pinned versions and numeric results;
no account, authentication, CLI output, or connection URI is collected.

### Exact payload metadata and offline bundles

`AgentSignaler.Dashboard.Bundle\Prerequisites.props` is the single source of truth.
The normal build **never updates pins**. It downloads missing build inputs from the
versioned Microsoft URLs to `artifacts\prerequisites` and verifies Microsoft
Authenticode, SHA-256, architecture, exact version and MSI identity. Redirects are
bounded and HTTPS-only, with an exact allowlist of the qualified Microsoft hosts.
Invalid existing cache files fail validation rather than being silently replaced.
All runtime payloads are embedded, with Burn SHA-512 verification and no network
fallback. Consequently no runtime download occurs before or after license consent.

Explicit metadata refresh:

```powershell
.\scripts\Update-PrerequisiteMetadata.ps1
```

This downloads into a unique workspace scratch directory, verifies signatures
before hashing, resolves the official Dev Tunnels redirect to its versioned artifact,
requires the exact `TunnelValidation.SupportedCliVersion`, and always deletes
scratch files. A changed current redirect version fails without changing metadata.
No metadata/hash is guessed and no package is installed.

The checked build uses **real** Microsoft-signed prerequisite payloads, not fake
payloads. If an external artifact is unavailable, a fake may only be used in a
separately identified structure-test fixture; it must never bypass production
validation or be distributed as `AgentSignaler.Dashboard.Setup.exe`.

### Azure CLI release compatibility gate

The exact signed Azure CLI 2.90.0 x64 MSI currently published at the pinned URL has
SHA-256 `D5C1918EAB32063219BEA575E0D545149969C24751CB5F90AD2388B5DF72222F` and
product code `{CB3186C1-BF23-488F-A4D4-8A1BFE220427}`. Read-only inspection shows
`az.cmd` but **no `az.exe`** in its File table. The mandated executable policy is
not weakened: a stock installation of this MSI cannot satisfy accepted Azure CLI
re-detection and therefore prevents Dashboard MSI execution (5104). Interactive
users can explicitly decline Azure CLI and install Dashboard. Dashboard runtime
now supports configuring an `az.exe` or the official `az.cmd` installation under
**Settings > Dev Box**, with a **Test Azure CLI status** action. This runtime
configuration does not change the bundle's prerequisite detection: resolving that
installer contract mismatch remains a release blocker, not an installer build failure.

### Direct Dashboard MSI

`AgentSignaler.Dashboard.msi` remains independently buildable and installable.
Its standard read-only AppSearch properties report Azure MSI/executable evidence,
the two Dev Tunnels paths, and Windows App protocol evidence. An AppSearch ActionText
warning and `PREREQUISITE_WARNING` appear in verbose MSI logs. These are advisory,
not trust validation; only the bundle/application performs full validation. Direct
MSI never launches nested installers, downloads prerequisites, or adds prerequisite
custom actions. Use `/l*v` during authorized deployment to retain detection warnings.

Mappings and cached connection URIs in the per-user database survive repair, upgrade
and uninstall, and a later reinstall reuses them. Only removing a machine or
**Clear connection mapping** deletes a mapping.

## Installation and servicing

### Managed tray and HTTPS endpoint migration

The remote package updates **Client, Configurator, and Relay as one versioned unit**
with v3 managed configuration and anonymous HTTP/HTTPS reporting. The tray/heartbeat
behavior is implemented; installed lifecycle, rollback, and manual acceptance remain
outstanding, not established by this document or a successful package build.
Dashboard includes a dashboard-only Tunneling library for opt-in CLI hosting; no
SDK/MSAL dependencies or cloud actions have been added to any installer.
Explicitly saved LAN mode is preserved. Dashboard settings without a connection
mode now default to Internet and automatic sharing at application startup, using
an existing CLI sign-in and without an additional consent checkbox. Missing CLI or
credentials fail visibly without enabling a public HTTP listener. Remote v1/v2 settings
are not rewritten during load, install, repair or upgrade.

Upgrade **Dashboard first**, then all three remote binaries before approving v3
Apply. Managed setup verifies `GET /api/v2/health` returning exactly
`{"protocolVersion":2,"status":"ok"}`; the strict
legacy `/health` response remains compatible. Missing capabilities block setup
with upgrade guidance, never a direct-v1 fallback.
Apply previews and persists `heartbeatIntervalSeconds` (default 300), whole **1–60**
minutes, exact executable paths, the owned HKCU Run sign-in command, immediate first
launch (both first installation and first legacy v1/v2 integration migration),
and removal of any remaining ownership-verified legacy heartbeat task in
published-folder setups. MSI servicing removes the owned task before Apply through
the separate migration below. Configurator backs up
previous bytes and ownership state transactionally. Failure restores previous
config, hook, manifest, startup value and any removed owned task. Disk commit and
runtime activation are separate outcomes: saved-but-not-applied settings require
visible retry/restart guidance, not an unqualified success message. UUID is retained.
Hooks keep the 3-second timeout and Relay's 2.2-second bounded IPC work budget.

Old remote binaries cannot read v3. Before migration, retain timestamped
`remote.json.agent-signaler.*.backup` files and matching integration/startup/task backups.
For an explicitly authorized old-release recovery (ordinary MSI downgrades remain
blocked), restore a consistent backed-up integration/configuration set, not just
edit the version number; HTTPS has no version-1 representation. Stop the managed
Client and remove its owned startup/hooks through normal Configurator actions
before legacy recovery.
Once managed reporting has activated a machine, Dashboard rejects leftover v1
reports with 409 persistently, even duplicates. Deliberate downgrade also requires explicit local machine
removal/reset in Dashboard (losing saved details/mapping and replay protection);
preserve display name, notes and mappings manually first. There is no automatic
downgrade endpoint. Do not delete machine identity or ordering files
to bypass the guard. Normal endpoint recovery does not need that destructive reset.

Client is the sole managed network reporter. Relay validates hooks and sends
sanitized events via bounded current-user IPC, with no HTTP fallback. Client stays
alive after Configurator closes and reports every five minutes by default. Its
failure deadline is **two intervals plus one minute** (**11 minutes** by default);
legacy v1-only machines keep the old five-minute hook timeout.
Client Exit stops all reporting and attempts terminal offline within a bounded
normal shutdown budget of at most five seconds. Acknowledgement immediately makes
Dashboard Offline; unreachable Dashboard falls back to the deadline. No hook,
watchdog, task or retry process restarts it after Exit. Use **Start Client** explicitly
or the next sign-in; opening/testing Configurator or settings-only Apply of an
already-managed integration cannot resume it. First approved Configurator v3
migration launches Client; MSI task-only migration does not.
New runs start Idle until fresh hooks because stopped-period hooks are lost.
Network outages while Client remains alive instead recover through current snapshots.

Internet mode requires an explicit dashboard-user installation of the qualified
Microsoft-signed `devtunnel` CLI (see `..\README.md`) and explicit sign-in.
The preferred Dashboard bundle offers its pinned per-user CLI prerequisite; direct
Dashboard MSI does not install it. It is unnecessary for LAN mode and all
remote clients. MSI acceptance must verify that Tunneling is in Dashboard only,
and that no interactive sign-in or required cloud deletion runs in custom actions.

Before uninstalling a tunnel-enabled dashboard, stop sharing and explicitly delete
its cloud resource while signed in. An offline deletion failure retains
`tunnel-state.json` with non-secret identity for later authenticated
`devtunnel delete TUNNELID`. Do not remove the shared CLI installation or credential
cache on uninstall. Optional `devtunnel user logout` clears its cached credentials
and may affect other CLI sessions; it does not sign Windows out or clear browser
cookies. Installed lifecycle and servicing acceptance remain release gates.

Both packages are per-user and should be installed and uninstalled as the same
Windows user, without running `msiexec` as another account. They add Start menu
shortcuts, including **Start Client**, but do not install a Windows service or
enable startup integration merely by installing. Approved remote configuration
registers the exact quoted Client command with `--background --config` and absolute config path under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. This is **user sign-in**, not
pre-login. Exit retains registration for next sign-in; owned integration removal
removes it. Foreign or modified values must never be overwritten.
The dashboard's separately requested firewall operation is not an MSI action.
If you enabled its Private-profile firewall rule, remove that rule using Dashboard
Settings before uninstalling when it is no longer wanted. A per-user MSI does not
silently elevate to remove an administrator-managed firewall rule.

The stable application directories are:

* `%LOCALAPPDATA%\Programs\AgentSignaler\Dashboard`
* `%LOCALAPPDATA%\Programs\AgentSignaler\Remote`

Client, Configurator, and Relay stay together in the stable Remote directory, so
configured integrations and the Start Client shortcut use stable absolute paths
across major upgrades. Keep each
package's `UpgradeCode` unchanged and increment its three-part MSI version for a
release. Downgrades are blocked. Major upgrades remove the old version inside the
installation transaction so a failed upgrade can restore it.

Remote uninstall invokes the installed relay in the installing user's context;
dashboard uninstall invokes the dashboard's headless startup-cleanup handler.
The rollback action is scheduled first, cleanup second, and file removal afterward.
Remote cleanup must stop only the exact owned current-user Client through bounded
IPC before removing owned startup/hooks. It never kills by executable image name
or stops another user's process. An unconfirmed offline notification does not
prevent uninstall; inability to safely stop the owner or remove owned integration
is a separate actionable failure.
Cleanup is required to succeed; failure aborts the uninstall instead of silently
leaving integrations pointing at a removed executable. If subsequent uninstall
work fails, Windows Installer restores the files before running the integration
rollback action. These actions are skipped for repair and major-upgrade removal.
No integration is created automatically by install or repair.

Repair and upgrade separately invoke the installed
`AgentSignaler.Relay.exe --stop-client-for-update` before replacing files. This
bounded current-user IPC helper stops the exact owned Client, including an owner
in another Windows session; it never kills by image name. Servicing does not
restart Client automatically: use **Start Client** afterward. Old installations
without Client skip the helper. Ordinary install still requires approved
Configurator Apply before startup integration is created.

For the 1.0.9 -> 1.0.10 IPC v1 -> v2 transition, this shutdown action is
**immediate, synchronous, and exit-code checked**, between `InstallInitialize`
and `RemoveExistingProducts`. It invokes the **still-installed old Relay and
matching IPC implementation**, not the new v2 helper. A deferred action at that
position would only queue shutdown and could allow old binaries to be removed
first. Ordinary uninstall likewise runs its installed matching helper before
`RemoveFiles`; major-upgrade removal skips integration deletion. A failed or
timed-out shutdown aborts servicing: ownership-lock release, not just an IPC
response, establishes completion.

Actual 1.0.9 upgrade/rollback remains a manual release gate. Exit the exact tray
Client before upgrading if the installed helper is unavailable or mismatched,
and retry only after shutdown is confirmed; do not replace locked binaries or
force-kill by image name. Hand-copied mixed-version installations require repair
with matching binaries. Never use a 1.0.9 helper to downgrade a v4 configuration
or collection manifest; the updated applications and installer must remain a
coordinated unit.

After `InstallFiles`, Remote MSI install/repair/update runs a **task-only** legacy
heartbeat migration using the newly installed Relay:

```text
AgentSignaler.Relay.exe --migrate-legacy-heartbeat --transaction-id "<captured transaction>"
AgentSignaler.Relay.exe --rollback-legacy-heartbeat --transaction-id "<captured transaction>"
AgentSignaler.Relay.exe --commit-legacy-heartbeat --transaction-id "<captured transaction>"
```

These are MSI-orchestrated transaction helpers, not manual setup commands.
`--transaction-id` is required; `--config "<absolute path>"` is optional. Migration
does not require Client, including when upgrading the old Configurator/Relay pair.
It removes only the ownership-verified legacy heartbeat task, leaves settings,
hooks and startup untouched, and performs no GUI or cloud operation. Successful
install/repair/update leaves no owned heartbeat task even before Apply; rollback
restores the exact owned task unless a foreign replacement now occupies its name;
foreign/modified tasks are never overwritten. Missing manifest or task is a no-op.
Recovery uses a task-only `heartbeat-migration-<hash>.json` journal and timestamped
task XML backup. Simple
configuration load/preview remains non-mutating. Published-folder installations
continue using Configurator Apply's safe task cleanup.

Both uninstall-integration handlers receive one transaction identifier captured before the MSI
transaction starts, combining product code, date, and time. They hash that opaque
identifier into a safe snapshot filename, preventing a later reinstall/uninstall
from reusing an earlier removal's recovery snapshot. The cleanup protocol is:

```text
AgentSignaler.Relay.exe --uninstall-integration --transaction-id "<captured transaction>"
AgentSignaler.Relay.exe --rollback-uninstall-integration --transaction-id "<captured transaction>"
AgentSignaler.Dashboard.exe --uninstall-integration --transaction-id "<captured transaction>"
AgentSignaler.Dashboard.exe --rollback-uninstall-integration --transaction-id "<captured transaction>"
```

Cleanup removes only Agent Signaler-owned integrations and preserves unrelated
user configuration. Dashboard cleanup removes the `AgentSignaler` Run value only
when it exactly matches that installed executable's owned startup command; changed
values are preserved. Remote cleanup likewise verifies exact Client startup command
and legacy task ownership; rollback restores only its journaled owned changes.
Successful setup/repair/update leaves no owned heartbeat scheduled task, duplicate
startup registration, timer, or icon. Remote settings, machine identity, original hook backups,
and transaction recovery snapshots deliberately survive MSI uninstall.
If installed binaries have been manually removed, repair the MSI first so its
cleanup executable and dependencies are available.

## Updating installed computers

Place the newer `AgentSignaler.Dashboard.msi` and `AgentSignaler.Remote.msi` in a
trusted local or UNC release folder. Increment the three-part MSI version for each
release; rebuilding the same version does not trigger an update.

Run `scripts\Update-AgentSignaler.ps1` in a **non-elevated** PowerShell 5.1 or later
as the Windows user who originally installed the apps:

```powershell
.\scripts\Update-AgentSignaler.ps1 -SourcePath C:\Releases\AgentSignaler -WhatIf
.\scripts\Update-AgentSignaler.ps1 -SourcePath C:\Releases\AgentSignaler
# Update only the remote components from a trusted share:
.\scripts\Update-AgentSignaler.ps1 -Apps Remote -SourcePath '\\server\releases\AgentSignaler'
```

`-SourcePath` is required so the updater never guesses or silently trusts a
machine-specific release location. Use only a release folder whose contents and
publisher you have verified.

Only existing current-user MSI installations are updated. Missing apps, equal
versions, and older releases are skipped; there is no fresh install, repair,
downgrade, or automatic update schedule. `-WhatIf` checks installed versions and
applicable MSI metadata without copying files or running Windows Installer.
Exit Dashboard explicitly (not just to its tray), close Configurator, and pause
active Copilot CLI work before applying updates. The updater invokes the same
bounded `--stop-client-for-update` helper for the owned current-user Client across
Windows sessions; old releases without Client skip it. Other running selected apps
or Relay invocations must finish before file replacement. It never kills processes
by image name or directly edits hooks, tasks, startup registration, or firewall rules;
the invoked MSI performs its owned task-only migration transaction.
There is no automatic Client restart after servicing.

Packages are staged locally, SHA-256 checked, and validated for the expected
product family, version, x64 architecture and per-user scope before either app
is updated. These checks do **not** authenticate a publisher: only use trusted,
access-controlled release folders and sign release packages before distribution.
Windows Installer runs with basic progress UI and without automatic restarts or
Restart Manager app shutdown. Each resulting installed version is checked.
Exit code `0` means success/no updates; `3010` means an update succeeded but
Windows requires a restart. Errors stop subsequent updates, but do not roll back
an earlier successful package. Logs remain in
`%LOCALAPPDATA%\AgentSignaler\Updates\Logs`; temporary MSI copies are removed.
Reopen Dashboard and explicitly **Start Client** after updating (or sign in again
after restarting Windows if requested). Upgrade the receiver before remote migration.

## Release verification

The build script runs `scripts\Test-Installers.ps1`, which opens each MSI read-only
through Windows Installer COM. It verifies every published file's relative path and
size, required application XBF/PRI and self-contained runtime assets, package
architecture/scope/version, stable upgrade codes and application
directories, Start menu targets, embedded cabinets, and servicing action tables.
The Remote checks must require nonempty Client, Configurator, and Relay executables,
verify their co-installation in the Main feature, and verify the Start Client shortcut.
Run the same checks separately with:

```powershell
.\scripts\Test-Installers.ps1 -Version 1.0.5
.\scripts\Test-DashboardBundle.ps1 -Version 1.0.5
.\scripts\Test-PrerequisiteSecurity.ps1
```

WiX also attempts standard ICE validation. Some developer system policies prevent
that validation, producing warning `WIX1105`; do not mistake a successful build in
that environment for a passed ICE validation run. Run full ICE validation in your
authorized release environment. The scripts do not elevate or suppress ICE checks.

The bundle test uses `wix burn extract`, never execution, to inspect the x64 engine,
native BA, exact source metadata, hashes, chain order, scope, conditions and permanence.
The native policy/callback tests use fake detection and a fake Burn engine, including
license gates, declines, no-downgrade planning and accepted-prerequisite re-detection.
Security tests reject bad origins, signatures, hashes, versions, MSI identity and
architecture. Building packages does not exercise Windows Installer rollback or Task Scheduler.
Before shipping, use a disposable Windows 11 x64 account/VM to test clean install,
explicit integration setup, repair, an increasing-version upgrade, ordinary
uninstall, and rollback from a deliberately failed uninstall. Confirm unrelated
hooks/startup values/tasks survive, hook and Client paths still work after upgrade,
and successful removal leaves no owned Client process, startup value, hook or
legacy task. Verify failed servicing restores owned registration/task snapshots and
preserves identity; network-offline shutdown must remain bounded. These manual
acceptance checks remain outstanding. Do not run these mutation tests on a
developer's real profile. Sign release MSIs and executables with your organization's
certificate; unsigned development output can trigger Windows reputation warnings.
