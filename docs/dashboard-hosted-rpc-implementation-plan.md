# Dashboard-hosted WebSocket JSON-RPC implementation plan

Status: planned, not implemented. Updated September 17, 2026.

## Goal and scope

Remove the standalone `AgentSignaler.RpcHost` executable and its installer.
Instead, let users enable WebSocket JSON-RPC in the existing WinUI
`AgentSignaler.Dashboard`. The Dashboard UI and RPC clients must operate on the
same runtime, receiver, database, settings, and operations.

"Dashboard" here means the monitoring application, not the Remote installer,
Configurator, Client, or Relay. This is a change in hosting, not permission to
expose unauthenticated control over a LAN or the Internet.

This plan supersedes the standalone-host and distribution decisions in
[the original RPC plan](rpc-host-implementation-plan.md). The existing
[v1 protocol](rpc-host-protocol.md) remains the compatibility baseline.
This request authorizes planning only, not implementation, installation, release,
or the original plan's unattended execution contract.

## Product decisions

| Area | Target behavior |
| --- | --- |
| Enablement, confirmed | Remember the setting across Dashboard launches; disabled by default. |
| Shutdown, confirmed | `system.shutdown` exits the entire Dashboard, preserving host-shutdown semantics. |
| UI | Add a **Local API** settings tab with **Enable WebSocket JSON-RPC (localhost only)**, listener status, port, and a copyable endpoint. |
| Activation | Enabling/disabling applies immediately, independently of the settings dialog's Save/Cancel. Explain this beside the control. |
| Port | Keep default `51821`, valid range `1024..65535`, and v1's restart-required port edits. Never silently choose another port. |
| Exposure | A separate listener on IPv4 and IPv6 loopback only, retaining `/rpc` and `/health`. |
| Trust | Preserve the reviewed unauthenticated localhost policy and exact Origin/Host/peer validation. |
| Lifetime | Closing to tray, minimizing, and RPC disconnection do not stop the API. Dashboard Exit, `--exit`, and RPC shutdown stop everything. |
| Startup | Existing Dashboard sign-in startup also starts RPC if explicitly enabled previously. No separate startup registration or daemon. |
| Headless use | No replacement console executable, compatibility launcher, or headless Dashboard mode. Existing `--background` remains a tray/UI mode. |

Display this warning before enabling, and retain it in the help text:

> There is no authentication. Other localhost pages and native clients,
> including other local Windows users that supply an accepted Origin, can
> control this Dashboard as you. This includes changing executable paths,
> performing destructive operations, and exiting the Dashboard. Use only in a
> trusted local desktop environment. Loopback and Origin checks are not
> authentication.

The API must not be presented as an authenticated remote endpoint or a private
WebView channel. A production web UI, new authentication scheme, LAN binding,
TLS setup, firewall rule for RPC, and tunneling the control port are out of scope.

## Current implementation and affected boundaries

| Surface | Existing implementation and consequence |
| --- | --- |
| Runtime ownership | `Dashboard.Core\DashboardRuntime.cs` already owns operations and borrows the entry-point `DashboardResourceLease`. Do not extract or duplicate the runtime again. |
| Standalone composition | `RpcHost\Program.cs` acquires the lease, creates the runtime/application/server, emits stdout readiness, and coordinates shutdown. Replace this composition, not its protocol implementation. |
| Transport | `RpcServer.cs`, `RpcConnection.cs`, `RpcEndpointPolicy.cs`, `RpcProtocol.cs`, and `RpcParameters.cs` implement the bounded listener, controller lease, parsing, and dispatch. |
| Runtime adapter | `RuntimeRpcApplication.cs` projects explicit v1 DTOs and tracks system revisions and runtime events. Reuse it for the Dashboard's runtime. |
| Dashboard composition | `Dashboard\MainWindow.cs` constructs the runtime and owns settings, initialization, tray behavior, and `ExitAsync`. `App.xaml.cs` and `Program.cs` own activation and process lifetime. |
| Settings | `Dashboard.Core\DashboardSettings.cs` already stores `RpcPort`; `DashboardRuntime.Operations.cs` separates saved/effective settings and restart requirements. There is no enablement setting yet. |
| Simultaneous editors | `MainWindow.Runtime.cs` currently supplies the latest settings revision when saving UI values. An open editor must not overwrite newer RPC changes by borrowing their revision. |
| Cancellation | Settings-dialog cleanup in `MainWindow.cs` directly cancels the shared catalog controller. This needs operation ownership once an RPC client can use that controller concurrently. |
| Packaging | `Build-Installers.ps1`, both CI/release workflows, and `UpdaterPolicy.ps1` currently require RpcHost products. Removing only its project would leave builds and updates broken. |

Paths above are under `src` unless stated otherwise. Hook contracts, Relay,
current-user named-pipe IPC, Client reporting, and receiver wire formats are not
being redesigned. Preserve:

```text
verified hook -> Relay -> current-user IPC -> Client
  -> receiver HTTP(S) -> DashboardServer -> SQLite
```

## Target architecture

```text
AgentSignaler.Dashboard.exe
  Program/App: single-instance activation and canonical data-directory lease
  Dashboard application lifetime
    MainWindow / tray / settings
    one DashboardRuntime (Dashboard.Core)
      receiver, SQLite, sharing, discovery, Windows App operations
    optional DashboardRpcEndpoint (Dashboard.Rpc)
      RuntimeRpcApplication -> that same DashboardRuntime
      dedicated loopback RpcServer -> RpcConnection -> JSON-RPC v1
```

Create `src\AgentSignaler.Dashboard.Rpc` as a Windows-targeted, non-WinUI class
library by moving the reusable RpcHost implementation. Keep the dependency
direction `Dashboard -> Dashboard.Rpc -> Dashboard.Core/Contracts`; Core must not
reference either WinUI or the RPC transport. Retain required Service/Tunneling
references without broadening internal types into public APIs unnecessarily.
Update project references, namespaces, and `InternalsVisibleTo` declarations.

The Dashboard application lifetime owns the optional endpoint alongside the
runtime. The endpoint borrows the runtime; stopping it must not dispose the
runtime, release its lease, or create another receiver/store. Keep protocol,
networking, and lifecycle logic outside UI event handlers.

Use one runtime adapter for the Dashboard lifetime, or otherwise preserve its
revision counters across endpoint restarts. A disable/re-enable cycle retains
the runtime's `hostInstanceId` and monotonic domain revisions; only a new
Dashboard runtime gets a new host ID.

## Settings and endpoint lifecycle

Add `RpcEnabled = false` to persisted Dashboard settings and explicitly keep it
false in recovery defaults. Missing fields in older files mean disabled, even
when a custom `RpcPort` exists. Do not infer consent from an old RpcHost install.
Continue strict bounded JSON loading, duplicate-property rejection, preservation
of extension fields, atomic file replacement, and revision-checked writes.

Represent requested/saved enablement separately from effective listener state.
Use a testable endpoint coordinator with serialized transitions, a bounded
operation set, cancellation, and states such as `disabled`, `starting`,
`listening`, `stopping`, and `faulted`. Report configured port, actual bound port,
controller/drain status, and fixed failure categories to the UI. A Core
`transportReady` lifecycle value alone is not proof that this listener is bound.

| Transition | Required behavior |
| --- | --- |
| Startup, disabled | Do not bind either RPC route. Initialize the normal Dashboard unchanged. |
| Startup, enabled | Attempt RPC startup before awaiting operational initialization so status/cancellation remain available during degraded startup. Receiver initialization proceeds even if RPC binding fails. |
| Explicit enable | Show the trust warning; prepare both loopback bindings with admission closed, commit the revision-checked preference, then open admission. If binding or persistence fails, dispose partial resources and do not report success. |
| Disable | Close admission immediately, cancel connection-owned work, and persist disabled. Drain before disposal; do not cancel unrelated UI/runtime work or stop the receiver/sharing. |
| Disable save failure | Stay disabled for this run and visibly warn that the saved preference remains enabled and could reopen RPC next launch. Never silently restore exposure. |
| Bind failure at startup | Preserve the saved preference, show effective `faulted` state and a sanitized corrective message, and keep Dashboard usable. Retry only on an explicit user action or a later launch. |
| Port edit | Save through the shared settings gate and retain `restartRequired: ["rpcPort"]` when applicable. Display the running port separately; disable/re-enable does not silently apply a pending restart-required port. |
| Re-enable | Require the previous connection drain and server teardown to finish. A new server object must not bypass an old draining controller. |
| Exit | Reject new enables and requests, then join the single application shutdown sequence. |

Use an enable/disable generation or equivalent ordering mechanism so a pending
enable, stale settings save, or delayed UI continuation cannot undo a newer
disable. Ordinary settings updates must preserve `RpcEnabled` unless the
explicit local enablement operation changes it.

Validate effective receiver/RPC collisions before every bind. When enabled,
reject settings changes that would cause a receiver/RPC collision on the next
launch, as well as invalid ports. Preserve unrelated valid legacy settings when
RPC is disabled; attempting to enable an incompatible configuration must fail
explicitly. Do not probe ownership and then kill another port owner.

Retain the existing 15-second controller-drain deadline. An incomplete drain
keeps admission closed and the endpoint visibly unavailable; track its task and
ownership until completion. Do not dispose resources underneath a writer or
start a replacement listener with a fresh controller lease.

## UI and RPC must share safe operational ownership

Keep local Dashboard controls usable while one RPC controller is connected.
The WebSocket single-controller policy limits RPC clients, not the local UI.
Both paths must use the existing runtime resource gates and revision checks.

Capture the settings or entity snapshot/revision when an editor opens. Submit
that revision with the edit, or use a narrow typed patch with an explicit
conflict policy. On conflict, preserve the draft and ask the user to refresh or
reapply; never silently overwrite a concurrent change or retry a mutation.
Apply the same rule to immediate settings controls, discovery targets, machine
details, and any UI helper that currently rebuilds a whole settings record.

Audit direct controller calls in the Dashboard partial classes. Dialog closure,
UI cancellation, RPC cancellation, and disconnection must cancel only their own
operations. Do not use shared-controller `Cancel`/`Stop` for dialog disposal.
Explicit global actions such as stopping sharing, cancelling runtime startup,
and exiting the application retain their documented global effect.

Subscribe to immutable runtime/endpoint state and marshal presentation through
the WinUI dispatcher. Keep dirty editor values distinct from live status.
Refresh open views and saved/effective indicators on RPC mutations; prevent
callbacks from updating disposed windows. Reuse existing standard controls,
help layout, InfoBar feedback, busy states, and accessibility names.

## Protocol compatibility and application shutdown

Retain JSON-RPC 2.0 and the existing v1 method list, DTO allowlist, errors,
notifications, batches, ID handling, limits, independent domain revisions, and
stateless revision-checked pagination. Do not serialize Dashboard objects or
new settings wholesale. No new transcript access or diagnostic payloads.

Keep `RpcEnabled` a local UI preference outside v1's mutation allowlist. Existing
`settings.update` patches must preserve it. Keep `rpcPort` and its restart
semantics; retain `rpcPortOverridden` in the v1 response as `false` after removing
the standalone CLI override. A connection proves only transport availability,
not receiver/sharing health.

Keep the RPC Kestrel application separate from `DashboardServer`. Reuse
configuration-source clearing and fixed binding, Origin/Host/remote-address
checks, bounded queues, and one active-or-draining controller. `/rpc` must never
exist on the report receiver or appear in Dev Tunnel provisioning.

Route `system.shutdown`, tray Exit, `--exit`, and actual application closure
through one idempotent coordinator. Preserve acceptance-before-socket-close,
including batches and notifications. Begin cancellation promptly but allow the
bounded acknowledgement send to finish before transport closure. Schedule UI
cleanup through the dispatcher without blocking it on its own queued work.

Maintain one overall 30-second shutdown budget, including the existing final
five-second owned-child cleanup reserve. Do not stack independent UI, RPC, and
runtime deadlines. Drain mutations before storage disposal, await owned cleanup,
and release the entry-point lease last. Repeated shutdown requests join the
same work; they cannot restart the deadline. A failed acknowledgement must not
abandon runtime cleanup.

The product no longer has RpcHost stdout readiness, Ctrl+C behavior, CLI flags,
or its console exit-code contract. Clients should launch/activate Dashboard if
needed, connect to its configured endpoint, then query capabilities/status.
They must refetch on reconnect even when the host ID is unchanged. Do not promise
a replay of `system.ready` to a client connecting after initialization.

## Removal, packaging, and migration

Remove the RpcHost executable project after moving its reusable code. Delete
its `Program.cs`, `RpcHostOptions.cs`, and product-specific
`InstalledReceiverMetadata.cs`. Do not read the legacy RpcHost installer key to
claim Dashboard firewall provisioning. Preserve v1 receiver-status fields with
truthful Dashboard/manual metadata, without adding an elevation helper.

Remove `installers\AgentSignaler.RpcHost`, including its dedicated native custom
actions and tests, from the product and solution. Remove or replace
`Build-RpcHostInstallerActions.ps1`, `Set-RpcHostInstallerPrivileges.ps1`,
`Test-RpcHostInstaller.ps1`, `Test-RpcHostPublish.ps1`, and
`Write-RpcHostNotices.ps1`. Carry applicable dependency notices into Dashboard
distribution rather than dropping attribution with the EXE.

Update `AgentSignaler.slnx`, `Build-Installers.ps1`, `Test-Installers.ps1`,
`Set-TestFirewall.ps1`, CI, and release workflows. Remove RpcHost-specific flags,
publish paths, artifact copies, MSI/EXE/notices assets, and checksum requirements.
Package the new library and required runtime dependencies with the existing
unpackaged, self-contained x64 Dashboard, preserving XBF/PRI resources. New
release assets must be allowlisted so stale generated files cannot ship.

Update `Update-AgentSignaler.ps1`, `UpdaterPolicy.ps1`, and updater fixtures
together. The current checksum parser requires RpcHost MSI/EXE entries:
removing assets without updating this policy breaks even Dashboard updates.
The new updater should service Dashboard/Remote only, accept the new manifest,
and handle explicitly recognized legacy manifests without selecting RpcHost for
installation. Preserve bounded downloads, hash validation, exact MSI identity,
current-user ownership, and downgrade protection.

An already-downloaded old updater cannot consume the first new manifest.
Document a bootstrap path: obtain the updated updater before running it, or
install the new Dashboard MSI manually. Do not ship dummy legacy assets or
silently treat a missing RpcHost asset as a successful RpcHost update.

Migration steps for an existing RpcHost user:

1. Explicitly stop RpcHost in its owning Windows session before starting Dashboard
   against the same canonical data directory. Keep the resource lease and its
   conflict message useful for older installed hosts.
2. Install/update Dashboard and reuse the existing settings/database location.
   Preserve machine history, notes, mappings, tunnel identity, and `RpcPort`;
   no database copy or schema migration is needed for this hosting change.
   Former `--data-directory` users may use the existing `AGENT_SIGNALER_DATA_DIR`
   override; former `--rpc-port` users must save their desired port explicitly.
3. Enable Local API deliberately. Replace launch scripts and stdout-readiness
   assumptions with the Dashboard launch/connect flow.
4. Remove the legacy MSI through an explicit user-managed uninstall if desired.
   Only that owned maintenance path should remove its firewall/registry artifacts.
   Do not automatically uninstall it, delete edited standalone copies, or remove
   another user's resources.

Retain older releases as historical artifacts; removal concerns new builds and
distribution. Do not reuse the RpcHost MSI upgrade identity for Dashboard.
Rollback requires exiting the new Dashboard first, retaining compatible saved
data, and explicitly launching the chosen older host. Older binaries cannot be
trusted to honor the new opt-in setting.

## Implementation sequence

1. **Extract the reusable endpoint.** Move RPC code into Dashboard.Rpc and retarget
   protocol/transport tests without changing v1 behavior. Introduce testable
   endpoint and application-shutdown coordination; keep the runtime single-owned.
2. **Add settings and safe transitions.** Implement default-off persistence,
   status snapshots, prepare/commit/admit enablement, immediate fail-closed
   disablement, collision validation, and cross-restart drain ownership.
3. **Integrate Dashboard.** Wire early startup, Local API UI, shared shutdown,
   dispatcher updates, and the simultaneous-editor/cancellation fixes.
4. **Retire the product end to end.** Remove the executable/MSI, retarget fixtures
   and browser launchers, and update publishing, checksums, updater migration,
   notices, and installer inspection.
5. **Update guidance and acceptance.** Revise `README.md`, `docs\user-guide.md`,
   `docs\rpc-host-protocol.md`, acceptance/manual-test documents, installer/test
   READMEs, and repository architecture instructions. Keep the original plan
   clearly historical rather than leaving competing current designs.

Preserve unrelated worktree edits, especially installer/version work. Do not
hand-edit generated `bin`, `obj`, or `artifacts` content.

## Validation and completion criteria

Reuse the existing .NET/xUnit and TypeScript/Playwright infrastructure. Rename
`AgentSignaler.RpcHost.Tests` and `.Web.Tests` to Dashboard.Rpc equivalents, and
update all fixture paths, environment variables, CI selectors, and friend
assemblies. Retain protocol boundary coverage rather than deleting it with the
executable.

| Area | Required evidence |
| --- | --- |
| Defaults and migration | New/missing/recovered settings bind no RPC socket; old `RpcPort` survives; confirmed enablement persists and reopens on launch. No startup-registration mutation. |
| Lifecycle | Enable, disable, re-enable, occupied port, either-loopback bind failure, cancellation during startup, persistence failure, and disable-versus-enable races. Receiver/UI remain usable on optional-API failure. |
| Drain ownership | Disconnect/disable cancels RPC work only; an incomplete drain blocks re-enable and competitors, even across server-object replacement. |
| Concurrent use | UI and RPC mutations share gates; stale editors cannot overwrite settings/machines; closing a dialog does not cancel RPC work; notifications update visible state. |
| Compatibility | Existing v1 capabilities, DTO privacy, malformed JSON/UTF-8, duplicate properties/IDs, batches, notifications, boundary sizes, queue pressure, pagination, and revision tests still apply. |
| Identity and reconnect | Same runtime keeps host ID and monotonic revisions across toggles; new process changes ID; reconnection refetches and receives no invented readiness replay. |
| Exposure | Inspect actual IPv4/IPv6 listeners and attempt disallowed handshakes; inherited hosting configuration cannot widen binding. Receiver/LAN/tunnel endpoints have no RPC route. |
| Shutdown | Whole Dashboard exits after RPC acceptance; tray/IPC/RPC races join one shutdown; stalled sends/drains preserve the total deadline and child-cleanup reserve. |
| Packaging and updates | Inspect Dashboard payload/resources and exact two-product release assets; no deployable RpcHost remains. Exercise new/legacy manifests, the old-updater bootstrap limitation, and legacy-install migration with fixtures. |

Retarget real-host browser tests to an isolated Dashboard launch with
`AGENT_SIGNALER_DATA_DIR`, synthetic data, explicitly enabled RPC, separately
selected test ports, and sharing disabled. Replace console-readiness reads with
bounded health/connect/status readiness and exact launched-process ownership.
No real accounts, user settings, tunnels, installers, or remote sessions.

Keep non-UI library/process fixtures for deterministic transport and drain
tests; they are test-only and must not be published as a replacement headless
product. Actual Dashboard startup/shutdown must also be exercised on an
interactive Windows runner. If that runner is unavailable, mark WinUI process
coverage as deferred release acceptance rather than claiming a console fixture
proves it.

After implementation, build Release x64 using the repository SDK, run the
smallest affected selectors and then affected projects (Dashboard.Core,
Dashboard.Rpc, Integration, and relevant receiver tests), and run the existing
browser contract commands against the new host. Publish/build-and-inspect
Dashboard installers only; do not execute installers on the development machine.
Human acceptance covers Local API accessibility, tray/background behavior,
actual UI/RPC convergence, and upgrade/uninstall/rollback in a disposable VM.

Completion means the option works in the shipped Dashboard, remains off without
explicit consent, preserves v1 and reporting behavior, and new builds/releases
contain no standalone RpcHost product. Moving files or adding a toggle alone
does not complete the migration.
