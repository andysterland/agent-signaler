# Agent Signaler installers

## Build

Run from the repository root on Windows x64 with the .NET 10 SDK, Windows SDK,
and Visual Studio x64 C++ tools installed:

```powershell
.\scripts\Build-Installers.ps1 -Version 1.0.14
```

The script restores tools, publishes self-contained applications, and builds and
validates the installers. It does not install anything or publish a release.
Use this script rather than a normal solution build to create installers.

| Output | Purpose |
| --- | --- |
| `artifacts\bundle\AgentSignaler.Dashboard.Setup.exe` | Dashboard setup with optional prerequisites |
| `artifacts\msi\AgentSignaler.Dashboard.msi` | Dashboard only |
| `artifacts\msi\AgentSignaler.Remote.msi` | Client, Configurator, and Relay |
| `artifacts\msi\AgentSignaler.DevTunnelsPrerequisite.msi` | Dev Tunnels prerequisite for the bundle |

Useful options:

```powershell
# Build only the two application MSIs (no bundle or prerequisites).
.\scripts\Build-Installers.ps1 -Version 1.0.14 -ApplicationMsisOnly

# Also copy validated application MSIs and the bundle to a trusted release folder.
.\scripts\Build-Installers.ps1 -Version 1.0.14 -DestinationPath C:\Releases\AgentSignaler
```

Use `-SkipPublish` only when the existing publish output already matches the
requested version. Use `-NoRestore` when the required SDK/package assets already
exist to build and inspect fresh publish output without restoring dependencies.
Increment the three-part version for every release; keep
UpgradeCodes unchanged. Downgrades are blocked.

## Install

Install as the intended Windows user. Both application packages are per-user.

1. Install **Dashboard** first, using `AgentSignaler.Dashboard.Setup.exe`, or the
   Dashboard MSI if you manage prerequisites separately.
2. Install `AgentSignaler.Remote.msi` on each reporting computer.
3. Open Configurator, review the settings, and approve **Apply** to enable remote
   reporting and Client startup at user sign-in. Installing the MSI alone does
   not enable these integrations.

### Detailed-conversation prototype deployment

Obtain external informed permission for content, destination, and retention
**before distribution/use**. Neither setup nor the app verifies consent.
Prerequisite-license consent, installation, and Apply confirmation are separate
and do not establish permission to share conversations.

New configuration v5 and explicit migrations default **Share detailed
conversations** on, with opt-out; allowed text may contain PII and is not redacted.
Versions 1–4 remain status-only until deliberately migrated; HTTP/LAN never
carries details. Details require the configured HTTPS Dev Tunnel, normal
certificate validation, a compatible Dashboard and its owned running tunnel on
the loopback Internet listener. Anonymous senders are not authenticated and can
spoof IDs, inject/purge content, or consume capacity. Installers do not create
tunnels, provision certificates/pairing/credentials, verify consent, or start
capture. See `..\SECURITY.md` and `..\docs\user-guide.md` before deployment.

**Known bundle release blocker:** the pinned Azure CLI 2.90.0 MSI provides
`az.cmd`, but bundle detection requires `az.exe`. Accepting this prerequisite
blocks Dashboard installation with exit code **5104**. Interactive users can
decline Azure CLI and continue. Dashboard itself supports the official `az.cmd`
through **Settings > Dev Box**.

The bundle offers Azure CLI and Dev Tunnels with explicit license consent.
Windows App is installed separately through its Microsoft Store button.
Setup does not sign in or perform cloud operations. Prerequisites remain installed
when Dashboard is removed.

Internet sharing needs Dev Tunnels and an explicit sign-in; LAN mode and remote
clients do not. See the [main README](..\README.md) for application setup.

For authorized unattended deployment, both `/quiet` and `/passive` require
`ACCEPT_PREREQUISITE_LICENSES=1`. The Azure CLI blocker above still applies.
Exit code `0` means success; `3010` means a Windows restart is required.

## Update

Close Dashboard completely and close Configurator. Pause active Copilot CLI work.
Run in **non-elevated PowerShell 5.1 or later**, as the original installing user:

```powershell
.\scripts\Update-AgentSignaler.ps1 -WhatIf
.\scripts\Update-AgentSignaler.ps1

# Update only the remote package.
.\scripts\Update-AgentSignaler.ps1 -Apps Remote
```

The updater uses the newest non-draft GitHub Release, **including prereleases**,
and verifies MSI identity, version, size, and published SHA-256 hashes. Private
releases require the current user's Git Credential Manager sign-in or a
process-level `AGENT_SIGNALER_GITHUB_TOKEN`.

Only existing current-user installations are updated; equal or older versions
are skipped. Update Dashboard before migrating remote configuration, and keep
Client, Configurator, and Relay on the same version.

Migrate configuration only after that coordinated upgrade: old Clients cannot
read v5. Loading an old configuration must not silently enable detail. Repair and
interrupted-transaction recovery must preserve a saved
`"detailedReportingEnabled": false`; replacing it with an older true is a recovery conflict, not successful
rollback. Failed opt-out persistence leaves the current Client run suspended.

Normal MSI major upgrades and the updater block older versions. A deliberate
v5-to-v4 recovery requires an explicit status-only configuration transaction:
stop the exact owned Client, discard volatile detail, write validated v4, then
service a compatible binary set through the authorized recovery procedure.
Do not hand-edit only the version or force an older MSI over v5. V4 cannot preserve
the v5 flag; any later v5 migration must preview default-on again. Failed migration
rollback restores the prior readable configuration and runtime state without
starting reporting that was stopped.
The configuration servicing API exposes
`MultiTargetIntegrationManager.PreviewStatusOnlyDowngrade(configPath)` followed
by normal `ApplyAsync`; there is no downgrade UI button or automatic MSI downgrade.
It requires exact owned manifest/configuration identity and successful owned
Client shutdown before writing v4. Binary replacement is a separate coordinated
servicing step, not performed by this configuration transaction.

Servicing stops the owned Client but does not restart it. Reopen Dashboard and
use **Start Client** afterward. Logs are in
`%LOCALAPPDATA%\AgentSignaler\Updates\Logs`. A failed update stops subsequent
updates but does not undo an earlier successful package.

## Uninstall

Uninstall as the same Windows user who installed the apps. Owned startup and
remote hook integrations are removed; settings, machine identity, connection
mappings, and recovery backups are retained.

Conversation retention is different: it is bounded volatile application memory,
not part of the retained database/backups. No conversation archive, spool, export,
reader path/cursor or message fingerprint is installed or migrated. This does not
promise no disk data anywhere: the host's existing history, OS paging/hibernation
and external crash capture remain outside that policy.

Host transcripts are **read-only inputs, never app-owned artifacts**. Installation,
repair, rollback, opt-out, clear and uninstall must neither change nor remove them,
even when the Client previously accepted their local stop reference. Format-profile
updates invalidate volatile contexts; they never migrate or rewrite host data.
No production assistant-file profile is currently independently verified for
Copilot CLI, VS Code, or Visual Studio; packaging a test-only fixture/adapter must
not enable a production reader or imply actual-host support.

Before removing Dashboard, stop sharing and delete its cloud tunnel while signed
in. Remove any unneeded Dashboard firewall rule through Settings. MSI uninstall
does not remove cloud resources, shared CLIs, credentials, or administrator-managed
firewall rules. If installed binaries were manually deleted, repair the MSI first.

## Release checks

The build runs read-only installer validation; it never executes the installers.
To rerun checks:

```powershell
.\scripts\Test-Installers.ps1 -Version 1.0.14
.\scripts\Test-DashboardBundle.ps1 -Version 1.0.14
.\scripts\Test-PrerequisiteSecurity.ps1
```

Bundle inputs are pinned in
`AgentSignaler.Dashboard.Bundle\Prerequisites.props` and validated for signature,
hash, architecture, version, and identity. Normal builds do not change pins;
use `scripts\Update-PrerequisiteMetadata.ps1` for an explicit refresh.

Before shipping, resolve the Azure CLI blocker and test clean install, Apply,
repair, upgrade, uninstall, and failed-servicing rollback in a disposable Windows
11 x64 account/VM. Confirm unrelated integrations survive and owned integrations
are cleaned up or restored correctly. These manual checks remain outstanding.
Warning `WIX1105` means ICE validation did not complete; run full ICE validation
in the release environment.

### Transcript packaging inspection scope

Read-only source inspection for this prototype found:

- `Build-Installers.ps1` publishes self-contained win-x64 Dashboard and merges
  Configurator/Relay/Client publish trees, rejecting unequal shared-file collisions.
  The WiX projects harvest these complete trees and retain deterministic per-user
  component GUIDs. Code added to existing managed assemblies needs no new data
  component; fresh publishing is required. Never hand-edit generated harvest,
  XBF/PRI, `bin`, `obj`, or artifacts.
- Remote MSI uses the still-installed matching Relay to stop the exact owned
  current-user Client before binary replacement. Its remaining custom actions
  are existing legacy-task migration and owned integration cleanup/rollback,
  not a transcript migration, startup, cloud, or file-reader action.
- Authored directories concern installation payloads/shortcuts, not host history;
  harvest cleanup removes empty app directories. There is no authored transcript
  database, host-root registration, reader state, or certificate component.
- `Test-Installers.ps1` opens MSI tables read-only and checks full publish-tree
  membership, x64/per-user identity, binary version agreement, XBF/PRI assets,
  and custom-action sequencing/allowlists. It now also rejects known transcript,
  state/log/recovery, test-fixture and credential payload paths; confines registry
  keys to installer-owned HKCU keys; forbids service/environment/registry-removal
  tables; and limits cleanup to empty app-owned directories, never wildcard files.
  `Test-DashboardBundle.ps1` extracts
  with WiX's reader, never by running Setup.exe. The native bootstrapper build
  runs policy tests, not an installer.
- These filename/table checks and synthetic authoring assertions do **not** alone
  prove v5 false preservation, runtime host-input nonmutation, or absence of
  transcript markers in application-created artifacts. Require
  synthetic servicing/privacy assertions plus fresh payload inspection; keep host
  input fixtures out of published payloads. Installed lifecycle validation remains
  separate. The existing Azure CLI prerequisite blocker above is unchanged.

This inspection is not a claim that fresh prototype packages were built, passed
ICE, installed, upgraded, rolled back, or uninstalled. Manual/live/installer
execution remains deferred to separately authorized disposable acceptance.
The cached pinned Azure CLI and Dev Tunnels prerequisite payloads were checked
read-only for signatures, hashes and identity during this work. Missing installed
Azure CLI is not an application-MSI build prerequisite; `-ApplicationMsisOnly`
skips the bundle/prerequisite build. The `az.cmd` versus required `az.exe` mismatch
is an existing accepted-Azure **bundle installation** blocker, not evidence that
application MSIs cannot be built. Fresh publish/build/ICE outcomes remain separate.

The manual **Release MSIs** GitHub Actions workflow publishes only the two
application MSIs and checksums. It selects the highest published release tag
matching `vMAJOR.MINOR.PATCH`, increments the patch component, and passes that
version into the application and MSI builds, so a source version change is not
required before release. Runs are serialized, default to prereleases, and will
not replace an existing release. Development packages are unsigned: hashes do
not authenticate the publisher. Use trusted release sources and sign packages
before distribution.
