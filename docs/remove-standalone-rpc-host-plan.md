# Remove the standalone RpcHost

## Decision

Remove the standalone `AgentSignaler.RpcHost` product, its loopback WebSocket
JSON-RPC API, its installer and release assets, and all host-specific shared
contracts and settings. Agent Signaler will retain these product roles:

- `AgentSignaler.Dashboard` as the receiver, persistence owner, operational UI,
  and Dev Tunnels owner.
- `AgentSignaler.Client` as the sole persistent remote reporter.
- `AgentSignaler.Relay` and `AgentSignaler.Configurator` for hook forwarding and
  user-managed remote integration.

The reporting path remains:

`verified hook -> Relay -> current-user named pipe -> Client -> Dashboard receiver -> SQLite -> WinUI`

This work must not add a replacement control listener, move JSON-RPC into the
Dashboard, or create another headless receiver.

Installed RpcHost retirement is manual. The repository owner will stop and
uninstall the MSI/installed app on each affected machine. Do not implement an
automatic uninstaller, installed-product migration release, or replacement
servicing helper. Shared libraries needed by Dashboard and Remote remain.

## Final-state requirements

- No active standalone RpcHost project, executable, MSI, native installer action,
  publish output, notices file, new release asset, updater target, firewall
  provisioning target, command-line option, or protocol contract remains.
- Dashboard does not expose a WebSocket JSON-RPC route or another control port.
- Existing Dashboard settings files containing `RpcPort` remain readable.
  `RpcPort` is ignored and removed on the next successful settings save.
- Dashboard continues to own the canonical data-directory lease, receiver,
  SQLite store, tunnel lifecycle, settings, and operational workflows.
- Existing installations are removed manually through Windows Installed Apps.
  Source changes neither uninstall products nor mutate machine firewall rules.
  Script-created rules require separate ownership-verified manual cleanup.
- Old GitHub releases remain historical artifacts; new releases contain only
  Dashboard and Remote application packages plus existing prerequisites.

## Phase 0: prepare manual retirement and preserve cleanup capability

1. Preserve unrelated uncommitted work. Carry the in-flight Dashboard firewall
   work forward without its RpcHost targets; do not discard it without approval.
2. Before deleting firewall discovery paths, identify the revision of each
   previously used ownership-aware helper and retain its cleanup instructions.
   Inventory MSI-owned rules separately from script-created application/test
   rules. Complete the manual cleanup sequence in Phase 5 before retiring the
   relevant cleanup capability on an affected machine.
3. The repository owner will stop the exact installed RpcHost and uninstall it
   through Windows Installed Apps on every affected machine, accepting the MSI's
   elevation request where required. Stop and remove any separately managed
   standalone copy manually. Preserve shared Dashboard settings, SQLite data,
   tunnel identity, and unrelated integrations.
4. Record the last release containing RpcHost and its stable UpgradeCode
   `{A8D1F2A9-762B-4B2C-A5A3-451463D743B2}` in release notes for manual recovery.
   Do not retain this identity in normal runtime or updater code after retirement.
5. Document the updater transition in Phase 4 before publishing the first
   Dashboard/Remote-only release. Manual MSI removal does not update a user's
   existing updater scripts.

The phases below describe work areas, not independently publishable commits.
Follow the atomic implementation order near the end of this plan.

## Phase 1: remove the executable and JSON-RPC protocol

Delete the complete `src/AgentSignaler.RpcHost` project:

- `AgentSignaler.RpcHost.csproj`
- `Program.cs`
- `InstalledReceiverMetadata.cs`
- `RpcApplication.cs`
- `RpcConnection.cs`
- `RpcEndpointPolicy.cs`
- `RpcHostOptions.cs`
- `RpcParameters.cs`
- `RpcProtocol.cs`
- `RpcServer.cs`
- `RpcShutdown.cs`
- `RuntimeRpcApplication.cs`

Delete the host-only wire contracts:

- `src/AgentSignaler.Contracts/Rpc/V1/RpcContracts.cs`
- `src/AgentSignaler.Contracts/Rpc/V1/RpcStateContracts.cs`

Then:

1. Remove the RpcHost project from `AgentSignaler.slnx`.
2. Remove `InternalsVisibleTo Include="AgentSignaler.RpcHost"` from
   `AgentSignaler.Dashboard.Core.csproj`.
3. Confirm no Dashboard or Service endpoint maps `/rpc`, upgrades WebSockets for
   control traffic, or references the deleted DTOs.
4. Retain HTTP receiver and Dev Tunnel behavior unchanged. The Dashboard receiver
   remains bounded by the existing report/transcript protocol, body limits,
   rate/concurrency limits, and loopback-versus-LAN binding rules.

## Phase 2: simplify Dashboard Core and settings

Remove RpcHost-only state from the shared runtime while preserving behavior used
by the Dashboard:

Before editing, inventory the production callers of shared methods, models,
events, options, and helpers in `AgentSignaler.Dashboard.Core` and Contracts.
Classify each as Dashboard-used, Remote-used, RPC-only, or settings compatibility.
A remaining test caller alone is not a reason to retain an RPC-only API.

Remove `DashboardRuntime.GetSessions`, `GetNote`, and `RuntimeNote`: their only
current production consumer is `RuntimeRpcApplication`. Trace helpers and
contracts made unreachable by those removals and remove them as well. Retain
Dashboard-used `GetMachines`, shared pagination helpers where still used,
resource ownership, domain revisions, cancellation, and operational workflows.
Do not delete the shared runtime merely because it originated with RpcHost.

Apply the known settings/runtime removals:

1. Remove `DashboardSettings.RpcPort`.
2. Remove `DashboardRuntimeOptions.RpcPortOverride`,
   `RejectInvalidSavedPorts`, and `InstalledReceiverPort` if no non-RpcHost caller
   remains.
3. Remove `RuntimeSettings.RpcPortOverridden`.
4. Remove the `"rpcPort"` restart-required calculation.
5. Remove RpcHost-only receiver firewall metadata and instructions from
   `RuntimeReceiver` if they have no Dashboard consumer.
6. Update the `DashboardRuntime` summary from "shared resource owner for WinUI
   and RPC" to Dashboard-only ownership.
7. Preserve `DashboardResourceLease`; it still prevents multiple Dashboard
   processes or Windows sessions from concurrently owning one SQLite/settings
   directory. Remove only RpcHost-specific error text from
   `DashboardOwnershipException` and Dashboard startup messages.

### Settings migration

Implement a narrow migration for existing `dashboard-settings.json` files:

1. Accept a legacy top-level `RpcPort` property as ignored input so upgrades do
   not fail or enter recovery mode.
2. Validate all remaining known settings exactly as before.
3. Remove only the app-owned legacy `RpcPort` property on the next successful
   save; preserve unrelated extension data.
4. Do not rewrite settings merely because the application launched.
5. Add tests for valid, invalid-type, and out-of-range legacy `RpcPort` values to
   prove they no longer affect Dashboard startup or receiver configuration.
6. Allow the literal `RpcPort` only in this narrow legacy-field migration and
   its compatibility fixtures. Preserve duplicate-property rejection, JSON
   limits, atomic saving, and unrelated extension data. Do not retain a typed
   RPC port setting, runtime override, or listener for backward compatibility.

## Phase 3: remove the RpcHost installer and servicing implementation

Delete `installers/AgentSignaler.RpcHost` in full:

- WiX project and package authoring
- native installer actions
- firewall/metadata policy
- native policy and boundary tests

Delete the dedicated scripts:

- `scripts/Build-RpcHostInstallerActions.ps1`
- `scripts/Set-RpcHostInstallerPrivileges.ps1`
- `scripts/Test-RpcHostInstaller.ps1`
- `scripts/Test-RpcHostPublish.ps1`
- `scripts/Write-RpcHostNotices.ps1`

Update shared packaging:

1. Remove the RpcHost WiX project from `AgentSignaler.slnx`.
2. Remove RpcHost publish, native-action build, notices generation, smoke gates,
   MSI build, signing staging, copied EXE/notices, hashes, and status messages
   from `scripts/Build-Installers.ps1`.
3. Remove `-SkipRpcHostSmoke` and any other RpcHost-only parameters.
4. Remove the RpcHost inspector invocation from `scripts/Test-Installers.ps1`.
5. Update installer counts and wording from three application MSIs to Dashboard
   and Remote only.
6. Do not execute an old RpcHost MSI during validation. Installer retirement is
   build-and-inspect or explicit disposable-machine acceptance only.
7. Use fresh or explicitly scoped staging directories so a stale RpcHost MSI or
   publish folder cannot enter wildcard signing, inspection, or artifact upload.
   Never broadly delete shared output directories or hand-edit generated files.

## Phase 4: remove updater and release support

Update the updater to recognize only Dashboard and Remote:

1. Remove RpcHost from `Update-AgentSignaler.ps1` parameter validation and
   defaults.
2. Remove the RpcHost UpgradeCode, HKCU installer metadata reads, process
   inventory checks, exact-file comparisons, elevation guidance, and
   `Assert-AgentSignalerRpcHost*` helpers.
3. Remove RpcHost assets and checksum grammar from `UpdaterPolicy.ps1`.
4. Retarget shared fixture coverage in `Test-Updater.ps1` and
   `Test-UpdaterFixture.ps1` to Dashboard and Remote. Preserve hash, package
   identity, architecture, version, no-downgrade, no-op, duplicate-asset, and
   non-mutating `-WhatIf` assertions; delete only RpcHost-specific cases such as
   receiver-port servicing and host process identity.
5. Ensure a release manifest containing only the supported Dashboard and Remote
   assets is accepted, while missing, duplicate, case-variant, or unexpected
   current-release assets still fail closed.
6. Reject legacy manifests containing retired assets as unsupported. The new
   updater targets Dashboard/Remote-only releases; do not retain RpcHost
   checksum grammar or install/update logic for historical releases.

### First-release updater transition

The current `Read-AgentSignalerChecksums` requires RpcHost MSI and EXE entries
before checking selected applications. An old updater therefore rejects the
first reduced manifest even with `-Apps Remote` or after RpcHost is uninstalled.

1. Publish the matching updater source and Dashboard/Remote-only release as one
   rollout. Tell users to refresh the repository to the release revision before
   invoking the updater; both `Update-AgentSignaler.ps1` and its companion
   `UpdaterPolicy.ps1` must come from that revision.
2. State in the release notes that old updater copies are incompatible with the
   reduced manifest. Refreshing only the entry script is insufficient.
3. Do not run the new updater against the previous release while it remains the
   latest release. Historical releases stay available for manual recovery, not
   as supported inputs to the new updater.
4. Users unable to refresh the scripts can manually install the signed Dashboard
   and Remote MSIs from the new release using the existing installation guidance.
5. Cover the transition using synthetic manifests: the new policy accepts the
   reduced set, rejects retired assets, and preserves integrity checks. Record
   the old policy's missing-RpcHost rejection as the reason for the documented
   refresh requirement, not as compatibility that has been preserved.

There is no updater self-update mechanism, automatic uninstall, or transitional
RpcHost migration release in this plan.

Update GitHub Actions:

1. Remove RpcHost MSI/EXE artifact paths from `.github/workflows/ci.yml`.
2. Remove RpcHost signing inputs, copied release files, checksum entries, release
   notes, and artifact assertions from `.github/workflows/release-msis.yml`.
3. Confirm no workflow restores Node/Playwright or .NET projects solely for the
   removed API.
4. Confirm new releases publish no `AgentSignaler.RpcHost.*` asset.

## Phase 5: clean up owned firewall rules before removing support

MSI uninstall only cleans up MSI-owned resources; it does not remove rules
created by `Set-TestFirewall.ps1` or `Set-ApplicationFirewall.ps1`. The test
helper discovers rules through its expected executable-path list. Removing
those paths first makes existing rules invisible to its removal action.

1. Before replacing the helpers on an affected machine, use the previously
   reviewed ownership-aware version to preview and explicitly remove its
   RpcHost rules. Cover each previously used checkout, build configuration,
   executable path, and network profile. If its removal action operates on the
   entire owned group, explicitly approve that scope and reapply retained
   Dashboard/test rules with the updated helper afterward.
2. Keep cleanup receipts and exact helper revision references until manual
   retirement is complete. Verify removal through ownership-aware status
   inspection. Never remove by display name alone, delete foreign/edited rules,
   or assume MSI uninstall handles script-created rules.
3. After this cleanup path is recorded and available, remove RpcHost built,
   published, and temporary fixture paths from
   `scripts/Set-TestFirewall.ps1`.
4. Remove RpcHost targets and `RpcHostReceiverPort` from
   `scripts/Set-ApplicationFirewall.ps1`; retain the requested Dashboard setup.
5. Remove RpcHost-specific block-rule cleanup eligibility and environment
   variables.
6. Update rule counts, required-path checks, receipts, and firewall documentation.
7. Preserve Dashboard's existing explicit Private-profile receiver rule workflow.
   Do not add Public/LAN receiver access; any Public rule must remain
   `127.0.0.1`-only.

These are manual administrator cleanup instructions, not operations to execute
while implementing or validating the source change. Keep no new automatic
retirement helper in the final product.

## Phase 6: update tests

Remove obsolete Dashboard Core assertions:

- `RpcPort` parsing/default/boundary tests in
  `OwnershipAndSettingsTests.cs`
- `RpcPortOverride`, effective/saved RPC port, and `"rpcPort"` restart tests in
  `DashboardRuntimeTests.cs`
- installed-RpcHost firewall metadata projections
- tests exclusively exercising removed RPC-only shared operations

Add or retain coverage for:

1. Legacy `RpcPort` settings compatibility and removal-on-save.
2. Dashboard-only data-directory lease contention and cross-session ownership.
3. Dashboard receiver startup, shutdown, LAN/loopback binding, persistence, and
   tunnel lifecycle.
4. Client reporting through the required Relay -> named pipe -> Client ->
   Dashboard path.
5. Updater acceptance of the reduced asset set, retained Dashboard/Remote
   integrity coverage, and the documented old-to-new updater transition.
6. Installer builds containing only intended Dashboard and Remote payloads.
7. Firewall scripts modifying only exact owned Dashboard/test rules.

Do not recreate JSON-RPC protocol tests under another project.

## Phase 7: documentation and repository policy

Delete obsolete current documentation:

- `docs/rpc-host-protocol.md`
- `docs/rpc-host-implementation-plan.md`

Delete `docs/dashboard-hosted-rpc-implementation-plan.md` as superseded by this
decision. No replacement RPC listener is planned.

Update:

- `README.md`
- `SECURITY.md`
- `docs/user-guide.md`
- `docs/ACCEPTANCE.md`
- `docs/test-firewall.md`
- `installers/README.md`
- `.github/copilot-instructions.md`
- repository-level architecture instructions

Remove references to:

- headless/alternative receiver choices
- JSON-RPC/WebSocket control
- `RpcPort`, `--rpc-port`, and RPC URLs
- RpcHost MSI/EXE/notices and firewall servicing
- RpcHost updater behavior
- accepted unauthenticated localhost-control risk
- Dashboard/RpcHost shared ownership wording

Retain the distinct warning that the Dashboard's report receiver and anonymous
Dev Tunnel mode do not authenticate senders.

Historical references are permitted in this plan and the retirement release
notes, including manual uninstall/cleanup and updater-refresh instructions.
Do not present those references as supported runtime features.

## Implementation order

1. **Prepare:** preserve in-flight work, record the manual MSI/firewall retirement
   procedure, and identify the updater-refresh requirement.
2. **One atomic removal commit:** remove the executable and RPC contracts,
   simplify shared code/settings, and update dependent tests, installer projects,
   build scripts, updater, firewall helpers, CI/release workflows, and current
   documentation together.
3. **Validate and release:** verify the complete changeset, then publish the
   Dashboard/Remote-only release with manual retirement and updater-refresh
   instructions.

Do not publish a runtime-only deletion followed later by packaging cleanup:
CI's installer job invokes `Build-Installers.ps1`, which still publishes the
RpcHost project until its consumers are updated. Every published commit must
support both the solution build and installer CI. Local work may be split for
review, but squash dependent changes before pushing.

## Validation

Run on Windows x64 with live tunnel tests disabled:

```powershell
$env:AGENT_SIGNALER_LIVE_TUNNEL_TEST = '0'
dotnet restore AgentSignaler.slnx -p:Platform=x64
dotnet build AgentSignaler.slnx --no-restore --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj --no-build --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Remote.Tests\AgentSignaler.Remote.Tests.csproj --no-build --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj --no-build --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Tunneling.Tests\AgentSignaler.Tunneling.Tests.csproj --no-build --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Dashboard.Core.Tests\AgentSignaler.Dashboard.Core.Tests.csproj --no-build --configuration Release -p:Platform=x64
```

Also run:

- `scripts/Test-PublicRepository.ps1`
- updater tests for the reduced Dashboard/Remote asset set and transition policy
- application-installer build and read-only inspection
- PowerShell parser and `-WhatIf`/status checks for retained firewall scripts

Do not run generated installers or enable
`AGENT_SIGNALER_LIVE_TUNNEL_TEST=1`.

## Completion gates

The removal is complete when:

1. `AgentSignaler.slnx` contains no RpcHost source or installer project.
2. No active source, project, workflow, installer, or script depends on
   `AgentSignaler.RpcHost`, its assets, CLI options, RPC DTOs, or control routes.
   Allowlist `RpcPort` only in the narrow legacy-settings migration and its
   compatibility fixtures. Historical retirement documentation and
   absence/rejection assertions are permitted; they must not provision or
   retain the retired API.
3. No `AgentSignaler.Contracts.Rpc` namespace remains.
4. Build/install/release outputs contain no RpcHost MSI, EXE, notices, native
   action DLL, or publish directory.
5. New updater manifests and checksums contain only supported products.
   Release instructions require matching refreshed updater scripts; old copies
   are not claimed to support the reduced manifest.
6. Dashboard starts with a legacy settings file containing `RpcPort`, ignores
   it, and removes it on the next successful settings save.
7. Dashboard and Client still complete the supported reporting path without a
   second receiver or control daemon.
8. Documentation and security guidance describe Dashboard and Remote as the
   only supported product roles.
9. The caller inventory contains no remaining shared operation or model whose
   only production consumer was RpcHost. Dashboard/Remote-used shared behavior
   remains intact.
10. Manual MSI/standalone-app retirement and independent script-rule cleanup
    have documented ownership-safe paths. Completion of source removal does
    not claim that remote machines were automatically cleaned up.
