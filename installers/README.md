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
requested version. Increment the three-part version for every release; keep
UpgradeCodes unchanged. Downgrades are blocked.

## Install

Install as the intended Windows user. Both application packages are per-user.

1. Install **Dashboard** first, using `AgentSignaler.Dashboard.Setup.exe`, or the
   Dashboard MSI if you manage prerequisites separately.
2. Install `AgentSignaler.Remote.msi` on each reporting computer.
3. Open Configurator, review the settings, and approve **Apply** to enable remote
   reporting and Client startup at user sign-in. Installing the MSI alone does
   not enable these integrations.

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

Servicing stops the owned Client but does not restart it. Reopen Dashboard and
use **Start Client** afterward. Logs are in
`%LOCALAPPDATA%\AgentSignaler\Updates\Logs`. A failed update stops subsequent
updates but does not undo an earlier successful package.

## Uninstall

Uninstall as the same Windows user who installed the apps. Owned startup and
remote hook integrations are removed; settings, machine identity, connection
mappings, and recovery backups are retained.

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

The manual **Release MSIs** GitHub Actions workflow publishes only the two
application MSIs and checksums, defaults to prereleases, and will not replace an
existing release. Development packages are unsigned: hashes do not authenticate
the publisher. Use trusted release sources and sign packages before distribution.
