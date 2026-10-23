# Agent Signaler installers

## Build

Run from the repository root on Windows x64 with the .NET 10 SDK, Windows SDK,
and Visual Studio x64 C++ tools installed:

```powershell
.\scripts\Build-Installers.ps1 -Version 1.0.19
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
# Build both application MSIs (no bundle or prerequisites).
.\scripts\Build-Installers.ps1 -Version 1.0.19 -ApplicationMsisOnly

# Also copy validated application MSIs and the bundle to a trusted release folder.
.\scripts\Build-Installers.ps1 -Version 1.0.19 -DestinationPath C:\Releases\AgentSignaler
```

Use `-SkipPublish` only when the existing publish output already matches the
requested version. Use `-NoRestore` when the required SDK/package assets already
exist to build and inspect fresh publish output without restoring dependencies.
Increment the three-part version for every release; keep
UpgradeCodes unchanged. Downgrades are blocked.
Publishing refreshes only the Dashboard/Remote payload directories and uses a
unique merge-staging directory. Packaging, signing, inspection, copying, and CI
uploads select only the two application packages explicitly; unrelated historical
output is not a release input and is not broadly deleted. Release asset staging
uses a fresh run-specific directory.

### In-app release servicing

Both application packages include `AgentSignaler.Updates.dll`, the installed
`Update-AgentSignaler.ps1`, `UpdaterPolicy.ps1`, `UpdateHandoff.ps1`, and a
versioned `UpdaterDependencies.json` descriptor. Scripts travel inside the MSI;
an update never downloads and executes a replacement script. Release signing
includes all three scripts, regenerates their SHA-256 pins after signing, and
requires valid timestamped Authenticode signatures from **Andy Sterland** with
code-signing authorization. Certificate renewal is supported without pinning a
short-lived certificate thumbprint. Unsigned local builds cannot service an
installed application through the in-app path.

The first supporting release must be installed manually. Existing older versions
cannot discover or bootstrap the newly bundled updater. Offers use the newest
published non-draft release, including clearly marked prereleases, and never
downgrade or install an uninstalled product. Client and Configurator are serviced
together as Remote; Dashboard-only servicing does not stop a separate Client.

Only an explicit in-app authorization enables captured restarts. The staged,
non-elevated Windows PowerShell 5.1 process validates a protected, pinned manifest
and acknowledges its exact current-user pipe before apps exit. Unsupported,
foreign-session, nonconsenting, or stalled participants prevent MSI execution.
There is one overall 60-second quiescence budget, no force-kill fallback, and no
credential lookup for public app updates. Reporting pauses while Remote is being
serviced. Manual invocation and `-WhatIf` retain their no-restart contract.
The authoritative operation gate remains in `Restarting` through application
health acknowledgement. Ordinary startups and competing updates stay blocked;
only a protected, one-time grant for the exact new PID, creation time and captured
launch context admits an updater-authorized restart.
Authenticated local health may initially report not ready; the updater polls
within one 30-second deadline, while malformed replies or identity changes fail
immediately. Remote receiver connectivity is not a restart-health requirement.
Client restart additionally requires its exact process handle, captured before
readiness, to report the dedicated update-owned shutdown code. Ordinary Client
Exit keeps reporting stopped, including during pre-servicing failure recovery;
unknown shutdown intent never authorizes an automatic restart.

The selected release, asset IDs/sizes, checksum manifest, package hash, publisher,
MSI identity and exact current-user installation are revalidated before servicing.
Read-only pins prevent package replacement. Windows Installer runs only with
`/passive /norestart REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable`.
Exit `3010` leaves apps stopped and reports reboot required; Windows is never
rebooted automatically. Exit `1618`, cancellation and other failures are distinct.
There is no blind retry or automatic downgrade/rollback across products.

Each operation retains its bounded `result.json` and capped installer log under
`%LOCALAPPDATA%\AgentSignaler\Updates\<installation-key>\<run-id>`. Only unchanged
downloads owned by a completed run are removed. A running or unknown MSI outcome
retains its gate and exact installer PID/creation time for recovery; do not delete
the gate or restart apps until Windows Installer is proven finished. A failed
update restores captured apps before MSI only when the unchanged prior registered
product and signed binary versions remain intact. After MSI failure, old binary
versions alone do not prove configuration compatibility: leave apps stopped for
explicit repair rather than assuming rollback completed. Restart health means local application readiness,
not remote Dashboard connectivity. Otherwise leave apps stopped, inspect the
result category, and repair manually as the original installing user. Do not
automatically run an updater at sign-in or execute a package while diagnosing it.

Development validation is fake-backed and build-and-inspect only. Production
signing, real MSI execution, clean-image upgrade/rollback, reboot-required behavior
and interactive update walkthroughs require separately authorized acceptance.

### Manual local Azure Artifact Signing

Normal `Build-Installers.ps1` builds remain **unsigned**. Explicitly invoke
`Sign-Installers.ps1` to produce locally signed application payloads and the two
application MSIs using the same existing account/profile as the release workflow.
After local signing succeeds, it copies only the two signed application MSIs to
`\\tsclient\C\public\AgentSignaler` by default.
This is a repository PowerShell script, not a Visual Studio or scheduled task.

Prerequisites, installed/configured separately:

- 64-bit PowerShell 5.1 or 7 on Windows, plus the ordinary installer build tools.
- Official x64 SignTool and `Azure.CodeSigning.Dlib.dll`, and the .NET 8 runtime
  required by the signing client. SignTool must have a valid Microsoft signature.
  The current official Dlib is unsigned: obtain it only through Microsoft's
  official distribution; its filename and x64 PE architecture are checked.
  If a Dlib carries a signature, it must be a valid Microsoft signature. See
  [Microsoft's signing integration instructions](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations).
  The official WinGet package is `Microsoft.Azure.ArtifactSigningClientTools`.
- An existing Azure CLI sign-in authorized to sign with the account/profile.
  The wrapper checks `az account show --output none`; it never logs in, changes
  roles/subscriptions or prints access tokens. Profile permission is checked by
  the signing service when signing actually starts.

Supply `-Endpoint`, `-Account`, and `-Profile`, or set these **process-level**
environment variables to the existing release configuration:

```powershell
$env:ARTIFACT_SIGNING_ENDPOINT = 'https://<region>.codesigning.azure.net/'
$env:ARTIFACT_SIGNING_ACCOUNT = '<existing-account-name>'
$env:ARTIFACT_SIGNING_PROFILE = '<existing-profile-name>'

# Preview only: no build, authentication command, network, signing or writes.
.\scripts\Sign-Installers.ps1 -Version 1.0.25 -WhatIf

# Explicit opt-in: this contacts Azure Artifact Signing and its timestamp service.
.\scripts\Sign-Installers.ps1 -Version 1.0.25

# Override delivery to another existing, trusted local or UNC directory.
.\scripts\Sign-Installers.ps1 -Version 1.0.25 -DestinationPath C:\Releases\AgentSignaler
```

Replace the placeholders; do not commit account-specific settings or secrets.
If GitHub CLI is authenticated for this repository, load the existing release
configuration into the current terminal without saving it to a file:

```powershell
foreach ($name in @('ARTIFACT_SIGNING_ENDPOINT', 'ARTIFACT_SIGNING_ACCOUNT', 'ARTIFACT_SIGNING_PROFILE')) {
    $value = gh variable get $name --repo andysterland/agent-signaler --env artifact-signing
    if ($LASTEXITCODE -ne 0) { throw "Could not read $name." }
    Set-Item -LiteralPath "Env:$name" -Value $value
}
az login
```

Open a new terminal after installing Azure CLI so `az` is on PATH. Sign in with
an identity that has the Artifact Signing Certificate Profile Signer role for
the selected profile; GitHub's workflow identity does not grant the local user
that permission. Choose the MSI version appropriate for your deployment; the
example does not reserve or publish a GitHub release.

`-WhatIf` validates these configuration values but needs no Azure CLI login and
does not invoke the signing or authentication tools. It displays the destination
and both MSI filenames without inspecting/contacting the destination.
The endpoint must be a public HTTPS `*.codesigning.azure.net` base URI without
credentials, a port, path, query or fragment. `-Version` is required and must be a
canonical three-part MSI version within `255.255.65535`. `-Configuration Release`
(default) or `Debug` and `-NoRestore` are forwarded to both existing build phases.
`-ExpectedPublisher` defaults to `Andy Sterland`, matching the release workflow.

Use `-SignToolPath` and `-DlibPath` to select explicit local x64 files, including
the official signing package's `bin\x64` layout. Automatic Dlib discovery first
checks the current-user WinGet installation's flat directory:
`%LOCALAPPDATA%\Microsoft\MicrosoftArtifactSigningClientTools`.
Other Dlib installation locations require `-DlibPath`; speculative installation
directories are not searched. SignTool discovery uses
installed Windows Kits 10 SDK `bin\<version>\x64\signtool.exe`, newest SDK first.
The client tools' `signtool.bat` and `ArtifactSigning.WindowsSignToolInstaller.exe`
helpers are **not invoked**: tool installation/download remains a separate,
explicit operation. No arbitrary downloads or repository executables are searched.
Azure CLI resolution accepts application
`az.exe`/`az.cmd` on PATH and the standard Program Files
`Microsoft SDKs\Azure\CLI2\wbin\az.cmd` installation. Its directory is added to
PATH only inside the operation and the previous process PATH is restored.

The wrapper:

1. Validates inputs, tool architecture, available tool signatures and the existing Azure CLI
   session before any destructive publishing.
2. Invokes `Build-Installers.ps1 -PublishOnly`, then signs and verifies only
   `AgentSignaler.*.exe` / `AgentSignaler.*.dll` and the three bundled updater
   scripts inside the Dashboard/Remote publish trees. Other dependencies are not
   signed. The updater dependency hashes are regenerated after signing.
3. Invokes `Build-Installers.ps1 -SkipPublish -ApplicationMsisOnly`, then signs
   exactly `AgentSignaler.Dashboard.msi` and `AgentSignaler.Remote.msi`.
4. Requires valid Authenticode signatures, the exact expected publisher, and
   timestamps for every selected binary and MSI. Signing uses SHA-256 and
   RFC3161 `/tr http://timestamp.acs.microsoft.com /td SHA256`; SignTool
   verification uses `/pa /all /tw`. Timestamping is mandatory because the
   service's signing certificates are short-lived.
5. Writes `artifacts\msi\SHA256SUMS.txt` as the **last local signing output**, hashing the final signed MSIs
   in the release workflow's `SHA256␠␠filename` format. No bundle, prerequisite,
   retired package or unrelated historical artifact is signed or included.
6. Commits local signing, then verifies that the two signed MSIs still match
   `SHA256SUMS.txt` and copies only those MSIs to `-DestinationPath`. It does not
   copy the checksum manifest, payload directories, bundle or other files.

Only `AzureCliCredential` is enabled in the temporary signing metadata; all
other documented credentials are excluded, including interactive browser,
environment, managed identity, Visual Studio and PowerShell credentials.
There is no GitHub CLI dependency, release publication, installation,
registry/firewall/startup change or automatic tool installation.

Run without concurrent publish/package/signing operations. Payload traversal is
bounded (10,000 entries / 8 GiB per tree, 1 GiB per file, at most 256 selected
first-party binaries), and reparse points and escaping paths are rejected.
Each target is signed and verified on an independent staged copy before replacing
its output path, preserving other names of hard-linked build/cache files.
Existing owned payload directories, the two MSIs and their checksum
manifest are moved to run-specific backups in `artifacts\signing-staging`.
Backup hard links are detached before building so a rebuild cannot change the
saved originals through another build/cache path.
This transaction covers the publish payloads, selected MSI packages and checksum
manifest, not ordinary compiler/WiX intermediate files under `bin`/`obj`.
During local signing, ordinary errors/cancellation restore the originals; a fresh failed run leaves
no partially signed payload/MSI or success checksum manifest. Signing metadata
is removed in cleanup. If rollback is blocked, backups and `recovery.json`
remain and no new checksum manifest is accepted. Force-killing PowerShell or
power loss can interrupt cleanup: the persistent transaction marker blocks a
retry. Review `recovery.json`, restore the mapped backups (manifest last, only
after its MSIs), and resolve the interrupted transaction before removing that
run's marker. Never blindly delete backups or assume a leftover manifest is a
successful new release.

#### MSI delivery and copy-only retries

The destination must already exist; the script does not create directories.
Before expensive signing it checks destination accessibility, directory/file
shape and any interrupted copy marker, without creating probe files. A later
write/access failure is a **delivery failure**, not a failed local signature.
Valid local signed payloads, MSIs and `SHA256SUMS.txt` remain available for retry.
The script rejects drive/share roots, repository/ancestor/artifact overlap,
reparse directories or targets, and directory-vs-file collisions. Unrelated
destination files are untouched. An existing selected MSI must itself have a
valid timestamped signature from the expected publisher before replacement;
a same-named unsigned/foreign file is not silently overwritten.

Delivery stages both files under unique names in the exact destination and
verifies their hashes before replacing either selected target. Remote hash reads
use 1 MiB buffering. Prior targets are retained as unique backups until both
replacements verify. Failures restore the prior pair when it is safe; concurrent
edits are never deliberately overwritten or deleted. A rollback conflict reports
the exact recovery paths and retains `.AgentSignaler-copy.lock`, a small recovery
journal containing the selected filenames, hashes and backup names. An interrupted
copy can leave a mixed pair: do not use it or retry blindly; inspect the reported
backups/journal and preserve concurrent edits before resolving that marker.
Cleanup addresses only this operation's exact staging/backup files, never the
destination directory recursively. Do not run concurrent deliveries.

To deliver an already completed signing run—without rebuilding, signing again,
Azure CLI authentication or a signing profile—load the helper from the repository
root:

```powershell
. .\scripts\LocalSigning.ps1
Copy-AgentSignalerSignedMsis -Root (Get-Location).Path -WhatIf
Copy-AgentSignalerSignedMsis -Root (Get-Location).Path

# Optional override; the same switch is available on Sign-Installers.ps1.
Copy-AgentSignalerSignedMsis -Root (Get-Location).Path -DestinationPath C:\Releases\AgentSignaler
```

The helper defaults to `\\tsclient\C\public\AgentSignaler` and accepts
`-ExpectedPublisher` and `-SignToolPath`. It rechecks the exact two-entry local
manifest, both local signatures/publisher/timestamps and the copied bytes.
It never changes the signed local files or their checksum manifest.

Synthetic validation needs no signing account, tools or network:

```powershell
.\scripts\Test-LocalSigning.ps1
```

It covers phase ordering and forwarded flags, exact target selection,
signing/authentication/verification failures, publisher/timestamp checks,
rollback and interrupted recovery, final signed hashes, input/path bounds,
`az.cmd`, x64 checks and genuinely non-mutating `-WhatIf`.
Copy fixtures additionally cover exact/default/overridden destinations, no remote
access during preview, staging failures, hash mismatches, two-file rollback,
concurrent-edit recovery, unchanged local signed outputs and unrelated files.

## Install

Install as the intended Windows user. Both application packages are per-user
and retain their non-elevated installation behavior.

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
Normal `-WhatIf` may download verified packages to a disposable staging
directory and use the existing private-release credential lookup, but never starts
application/servicing processes, invokes MSI/elevation, or changes installed
products/firewall. The synthetic fixture gate does not access real inventory,
credentials, downloads or user data and does not mutate its fixture.

Before the first Dashboard/Remote-only update, refresh the repository to the
matching release revision. Both `Update-AgentSignaler.ps1` and `UpdaterPolicy.ps1`
must be refreshed: old copies reject the reduced checksum manifest even when
only Remote is selected. The new updater rejects historical manifests with
retired assets; use it only after the matching reduced release is published.
If refreshing the scripts is not possible, manually install the signed Dashboard
and Remote MSIs. See the [retirement release notes](..\docs\rpc-host-retirement.md)
for manual removal and independent ownership-verified firewall cleanup; the
updater does not perform retirement or update itself.

Migrate configuration only after that coordinated upgrade: old Clients cannot
read v5. Loading an old configuration must not silently enable detail. Repair and
interrupted-transaction recovery must preserve a saved
`"detailedReportingEnabled": false`; replacing it with an older true is a recovery conflict, not successful
rollback. Failed opt-out persistence leaves the current Client run suspended.

DNS TXT discovery is an explicit v7 configuration choice after the complete Remote
binary set has been updated. Existing direct v1-v6 configurations keep their
meaning; installation never switches connection modes. V7 retains detailed-sharing
and source preferences and stores the DNS record name, not its resolved tunnel
URL. Older binaries reject v7. Never edit the version discriminator or copy a
transient resolved address into settings to bypass coordinated configuration.
See [DNS TXT discovery](..\docs\dns-txt-discovery.md).

Azure Blob private discovery has been removed. Legacy private-Azure configurations
are rejected with reconfiguration guidance, not silently migrated. Existing saved
credential files, publisher state and Azure resources are retained without being
read, rewritten, revoked or deleted. Published payload inspection rejects the
retired discovery assembly and Blob SDK dependencies.

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

Removing the former Azure Blob feature does not revoke existing reader access.
Normal MSI uninstall never signs in to Azure or removes discovery cloud storage,
policies, account settings, role assignments or protected credentials. Retained
resources can incur charges; any revocation or cleanup requires separate explicit
authorization. Do not copy protected credential or signing state as plaintext.

## Release checks

The build runs read-only installer validation; it never executes the installers.
To rerun checks:

```powershell
.\scripts\Test-Installers.ps1 -Version 1.0.19
.\scripts\Test-DashboardBundle.ps1 -Version 1.0.19
.\scripts\Test-PrerequisiteSecurity.ps1
.\scripts\Test-Updater.ps1
```

Retirement validation (2026-09-30): fresh self-contained 1.0.20 Dashboard and
Remote payloads were published with `-ApplicationMsisOnly -NoRestore`. The first
MSI build reported missing WiX restore assets; rerunning with
`-ApplicationMsisOnly -SkipPublish` restored only the installer projects and
built both packages. Read-only inspection passed for Dashboard's 674 payload
files and Remote's 532 payload files. Both builds reported `WIX1105`: system
policy prevented ICE validation, so ICE is **not** claimed as passed. No
installer, application listener, signing, upload, or firewall operation ran.
Updater fixtures passed on PowerShell 5.1 and 7. Synthetic workflow catalog and
release-staging checks excluded stale retired assets without deleting them.

Bundle inputs are pinned in
`AgentSignaler.Dashboard.Bundle\Prerequisites.props` and validated for signature,
hash, architecture, version, and identity. Normal builds do not change pins;
use `scripts\Update-PrerequisiteMetadata.ps1` for an explicit refresh.

Before shipping, resolve the Azure CLI blocker and test clean install, Apply,
repair, upgrade, uninstall, and failed-servicing rollback in a disposable Windows
11 x64 account/VM. Confirm unrelated integrations survive and owned integrations
are cleaned up or restored correctly. These manual checks remain outstanding.

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

The manual **Release MSIs** GitHub Actions workflow publishes the Dashboard and
Remote application MSIs and checksums. It runs on the
repository-scoped signing runner, signs first-party application binaries before
WiX packages them, signs the resulting MSIs, and verifies the publisher before
release creation. By default it selects the highest published release tag
matching `vMAJOR.MINOR.PATCH` and increments the patch component. The optional
`version` input selects an explicit `MAJOR.MINOR.PATCH` instead; it must be
within MSI limits and newer than every published release. The selected version
is passed into the application and MSI builds, so a source version change is not
required before release. Runs are serialized, default to prereleases, and will
not replace an existing release. Local builds, CI artifacts, and historical
releases may remain unsigned; hashes do not authenticate the publisher.

The signing runner requires Windows x64, PowerShell 5.1 and 7, .NET 8 and the
SDK selected by `global.json`, Azure CLI, Node.js/npm, and the NuGet package
provider used by the Artifact Signing action. The runner is repository-scoped
and carries the `artifact-signing` label. Azure CLI state is isolated under the
job's temporary directory and removed after signing.

Automated updater fixtures cover the reduced release asset set, retired-asset
rejection, current-user inventory, hash/size, MSI identity/architecture/version,
no-downgrade/no-op behavior, and non-mutating `-WhatIf`. CI/release also run the
retained service, remote, integration, tunneling, and Dashboard Core tests plus
dependency/privacy audits.

**Not run - deferred release acceptance:** MSI install/upgrade/uninstall and
failed-servicing rollback in a disposable Windows 11 VM; clean-image application
execution without installed .NET/Windows App SDK. Synthetic/read-only checks do
not prove these live behaviors. Do not execute generated installers on a shared
development workstation. Building these files never dispatches a release.
