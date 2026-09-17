# Agent Signaler RPC Host Implementation Plan

> **Design superseded:** The
> [Dashboard-hosted RPC plan](dashboard-hosted-rpc-implementation-plan.md)
> replaces this document's standalone executable and distribution design with
> an opt-in Dashboard endpoint. This document remains historical; the new plan
> is not implemented yet and does not inherit the unattended execution contract
> below.

## Problem and approach

Add a foreground, self-contained Windows executable named
`AgentSignaler.RpcHost` that owns the same non-visual capabilities as
`AgentSignaler.Dashboard` and exposes them to JavaScript or TypeScript through a
WebSocket JSON-RPC 2.0 interface.

The current Dashboard directly composes persistence, the receiver, Dev Tunnel
hosting, Azure CLI/Dev Box discovery, Windows App mapping and activation,
prerequisite diagnostics, polling, cancellation, and shutdown inside the WinUI
`MainWindow`. The implementation should first extract that operational behavior
into a UI-independent runtime library. Both the existing WinUI Dashboard and the
new RPC host will then adapt the same runtime instead of maintaining parallel
implementations.

This document incorporates the design-review and unattended-execution decisions
confirmed on September 16, 2026. The later web UI/WebView client is out of scope;
protocol examples and client interoperability tests are included. This request
updates the plan only. A subsequent instruction to execute this plan authorizes
the unattended implementation scope below, without intermediate design approvals.

## Unattended execution contract

### Authorized scope and completion target

On execution, implement all phases, documentation, automated tests, build/publish
scripts, and local unsigned EXE/MSI artifacts without asking design questions.
Use the decisions and numerical defaults in this document. The implementation
agent may select private APIs, helper names, DTO fields, test organization, and
compatible tooling versions using repository conventions; record consequential
choices in `docs\rpc-host-protocol.md`. Do not reopen confirmed product choices.

The completion target is **implemented and automatically validated, ready for
human release acceptance**, not production certification. Human-interactive
checks below are deliberately not prerequisites for that target. They must
remain documented as **Not run - deferred release acceptance**; never claim
fakes or configuration assertions proved live integration behavior.

No unattended run may publish a GitHub Release, change repository visibility,
install/update/uninstall products on the shared workstation, request elevation,
sign in to real accounts, create/delete cloud resources, alter real firewall or
sign-in registrations, modify actual user data, or launch a real remote session.
Implement those capabilities behind tested adapters without invoking them.
Do not stop unrelated processes. Do not sign artifacts or invent credentials.

Keep code and artifacts local by default; do not commit, push, trigger hosted
workflows, or deploy to the redirected updater path unless the execution request
explicitly includes those actions. Existing release workflow changes are code,
not permission to dispatch them.

### Workspace and failure handling

Before editing, inspect repository instructions, worktree changes, SDK/toolchain,
and targeted baseline results. Preserve all unrelated work. If concurrent changes
overlap implementation files, use a dedicated isolated worktree from the current
commit and record the base SHA and workspace location; do not stash, reset,
auto-merge conflicting work, or overwrite the shared checkout.

Record automatic decisions and results in session artifacts and the final
handoff, not new unrelated repository planning files. Resume from those artifacts
after interruption. Follow repository dependency-installation rules: use existing
tools first; restore/install declared dependencies after manifest changes or a
specific missing-dependency failure. Use the existing .NET/WiX toolchain.

Fix failures introduced by this change. Compare unrelated failures against the
baseline; report them rather than rewriting unrelated code or suppressing tests.
If an external resource, policy, missing privilege, or tool outage prevents a
required automated gate, finish independent work and record the gate as blocked
with evidence and an exact rerun command. End with an incomplete result rather
than prompting, hanging, fabricating success, or weakening requirements. No plan
can guarantee successful execution despite arbitrary environmental failures.

### Automated versus deferred acceptance

| Gate | Unattended execution | Deferred human/release acceptance |
| --- | --- | --- |
| Core, persistence, receiver and RPC | Real isolated SQLite/Kestrel/WebSocket tests, synthetic reports, faults and fake clocks | None for these deterministic contracts |
| JavaScript/TypeScript | Pinned test-only npm manifest/lockfile with TypeScript and Playwright; headless Chromium on synthetic localhost pages | Actual supported WebView shell/browser policy checks |
| Azure and Dev Tunnel actions | Injectable runners, synthetic CLI child fixtures, fake health probes; no account/cache access | Live account login, permissions, real tunnel lifecycle |
| Process ownership | Real isolated subprocess tests, path aliases, parent-exit survival and forced RpcHost termination | Second interactive Windows session when no pre-provisioned test session exists |
| Firewall and foreground activation | Read-only MSI firewall authoring/ownership tests and fake native activation adapters | Real installer UAC approve/deny, firewall rollback, native foreground success/failure |
| EXE publishing | EXE-only execution from an otherwise empty directory, actual SQLite read/write and RPC; isolated extraction cache | Clean Windows image with no .NET/Windows App SDK if unavailable automatically |
| MSI/updater | Build and read-only MSI table/payload inspection, fake installed-product/download/process adapters, non-mutating fixture `-WhatIf` | Install/upgrade/repair/uninstall/rollback in a disposable Windows VM |

If an already-provisioned disposable environment can run a deferred check
non-interactively and safely, record its actual evidence; never require creating
accounts or changing the shared workstation to obtain it. Browser test tooling
belongs under `tests\AgentSignaler.RpcHost.Web.Tests`, not a production web UI.

The final handoff must identify changed files, artifact paths and hashes,
automated results, baseline failures, deferred checks, and remaining operational
risks. Preserve the accepted unauthenticated-control warning.

## Confirmed decisions

- New executable/project: `AgentSignaler.RpcHost`.
- Process model: explicitly launched foreground headless process. Parent-process
  exit does not stop RpcHost; it remains running until explicit RPC shutdown,
  Ctrl+C, OS termination, or an unrecoverable process failure.
- Distribution: one self-contained EXE, allowed to extract bundled native
  libraries into a per-user cache, plus a dedicated RpcHost MSI.
- Prerequisites: Azure CLI, Dev Tunnels CLI, and Windows App remain external.
- Transport: WebSocket carrying JSON-RPC 2.0 messages.
- Listener: loopback only, fixed default port `51821`.
- Configuration: add `RpcPort`, with a command-line override.
- Authentication: none.
- Origin policy: accept only localhost origins, including loopback IP forms;
  reject non-local, missing, malformed, or opaque origins.
- Client model: one controlling WebSocket client; reject additional clients.
- Disconnect: cancel client-owned operations and finish their cleanup before
  admitting another controller. Keep the receiver and established sharing alive.
- Startup: expose RPC during initialization and recoverable failure, with
  progress, cancellation, diagnostics, and settings available.
- Capability scope: all non-visual Dashboard behavior, including receiver and
  machine state, settings, prerequisite checks, Dev Box discovery, tunnel
  lifecycle, Windows App mapping/refresh/sign-in/open/reuse, machine edits, and
  cancellation.
- Coexistence: one resource owner per canonical data directory across Windows
  sessions. Dashboard window activation remains session-local.
- Firewall: the dedicated RpcHost MSI always installs a receiver-only inbound
  TCP rule for the Private profile, including in Internet-sharing mode. There
  are no firewall RPC methods or runtime elevation helpers. Standalone EXE users
  configure LAN firewall access manually; existing WinUI Dashboard actions remain.
- Sign-in startup: preserve existing Dashboard registration; do not add RpcHost
  registration or automatic MSI launch.
- Port collision: fail before operational startup with instructions to use
  `--rpc-port`; never silently change either port.
- Protocol: support bounded JSON-RPC 2.0 batches and correct notifications.
- State synchronization: independent domain snapshots plus lightweight
  invalidation notifications. No globally atomic snapshot, retained cross-domain
  capture, replay history, or snapshot-token cache.
- Updates: extend the existing updater for installed RpcHost MSIs. Standalone
  EXE copies remain manually managed.
- Web UI implementation is explicitly deferred to another plan.

The user explicitly retained unauthenticated access from any localhost origin
after review. This accepts control by unrelated localhost pages and by native
clients, including other local Windows users, that supply an accepted Origin.
Executable-path changes and diagnostics can execute code as the RpcHost user;
data deletion and CLI sign-out are also exposed. Origin validation, confirmation
fields, loopback binding, and the single-client lease are not authentication.
Do not describe the endpoint as restricted to the owning WebView.

## Current-state findings

- `AgentSignaler.Dashboard` is a self-contained WinUI executable and references
  `AgentSignaler.Service` and `AgentSignaler.Tunneling`.
- `MainWindow.InitializeCoreAsync` currently constructs and owns `MachineStore`,
  `DashboardServer`, Azure CLI services, `DevBoxCatalogController`,
  `WindowsAppConnectionController`, `WindowsAppLauncher`, and
  `CliTunnelController`.
- Machine state is durable in SQLite through `MachineStore`; the Dashboard polls
  `GetMachinesAsync` once per second and performs mutations through store methods.
- The receiver already uses slim ASP.NET Core/Kestrel and supports LAN or
  loopback binding depending on Dashboard connection mode.
- Tunnel state is event-driven through `CliTunnelController.StatusChanged`.
- Dev Box discovery and Windows App operations already have controller state,
  cancellation, progress, and completion semantics that can feed RPC
  notifications after their types are moved out of the WinUI assembly.
- Several reusable operational types are still `internal` and located in the
  Dashboard project: settings, Azure CLI wrappers and diagnostics, Dev Box
  catalog services/controllers, Windows App services/controllers, and
  prerequisite orchestration.
- UI-only behavior is interleaved with operational behavior in `MainWindow`,
  especially startup, polling, error presentation, settings application, and
  shutdown.
- The existing Dashboard single-instance mutex is tied to the Dashboard process
  name and does not protect the shared database, receiver port, tunnel identity,
  or operation ownership from a second executable.
- Installer/build/release scripts currently publish and package the Dashboard
  and Remote applications only.

## Target architecture

### 1. Shared headless runtime

Create `src\AgentSignaler.Dashboard.Core\AgentSignaler.Dashboard.Core.csproj` as a
Windows-targeted, non-WinUI class library. Move or refactor the reusable
operational types into it:

- operational Dashboard settings and data paths;
- Azure CLI execution, validation, and diagnostics;
- Dev Box discovery models, service, and controller;
- Windows App mapping, resolution, validation, launch/reuse, operation gate, and
  controller;
- prerequisite diagnostic orchestration;
- connection/tunnel presentation-neutral state;
- a new `DashboardRuntime` composition root.

Keep WinUI-only presentation, windowing, tray, dialogs, compact view,
automation properties, clipboard, startup registration UI, and visual models in
`AgentSignaler.Dashboard`.

`DashboardRuntime` should:

- load and validate settings;
- receive the process-owned resource lease before opening mutable state;
- create `MachineStore`, `DashboardServer`, Azure CLI services, Dev Box catalog,
  Windows App controller, and tunnel controller;
- expose immutable snapshots for runtime, receiver, machines, tunnel, catalog,
  prerequisite checks, and per-machine Windows App operations;
- expose typed asynchronous commands with cancellation;
- generate monotonic revisions independently for each state domain;
- publish typed change events without depending on a UI synchronization context;
- serialize mutations where existing controllers require it;
- implement idempotent, bounded shutdown in reverse ownership order.

The WinUI Dashboard should be refactored to consume this runtime. Its timer may
remain a UI refresh mechanism initially, but operational creation, cancellation,
and disposal must have one implementation shared with the RPC host.

Give the core a small public API for runtime commands, snapshots, and injectable
platform boundaries; keep implementation types internal, with explicit test
assembly access where necessary. Replace linked-source compilation of moved
types in `AgentSignaler.Integration.Tests.csproj` with a core project reference.
Tests must exercise the deployed implementation, not duplicate source copies.
Keep `WindowsAppConnectionActions` navigation and visual progress windows in
WinUI; extract operational results without `RestoreDetails` or compact-view
navigation semantics. Audit `PrerequisiteCheck` and other UI-context-dependent
state machines for thread safety rather than just moving files.

#### Capability parity and intentional exclusions

| Capability | Runtime/RPC responsibility | Host or WinUI responsibility |
| --- | --- | --- |
| Machines and sessions | Effective states, details, notes, edits, removal, expiry and stale-state reporting | Rendering, sorting preferences, relative-time formatting |
| Mapping and Windows App | Catalog selection, validation, sign-in, refresh, cached/fresh open, reuse, clear, cancellation | Explicit user gestures and confirmation UI; foreground permission cooperation |
| Receiver | Existing report protocols, effective port/mode, connection-test events | Connection URL display/copy |
| Sharing | Start/stop, automatic-sharing setting, progress, delete/logout confirmations | Warning and confirmation presentation |
| Prerequisites | Check one/all, cancellation, captured unsaved CLI paths, tested-path result | Path editing and result presentation |
| Settings | Atomic persistence, validation, saved/effective state, restart-required fields | Preserve existing theme/compact preferences; no new headless visual behavior |
| Firewall | No RPC mutation or runtime elevation; expose installed/effective receiver-port mismatch in receiver status | RpcHost MSI always owns its Private-profile receiver rule; standalone setup is manual; existing WinUI actions are preserved |
| Startup registration | Preserve existing Dashboard-only registration and servicing | RpcHost is always launched explicitly |
| Window/tray/clipboard | Not implemented by RpcHost | Existing WinUI retains these; future WebView plan owns equivalents |

Require confirmation for both mapping clear and machine removal, preserving the
warning that removal resets replay history. Confirmation fields prevent
accidental calls, not malicious callers. Expose Configurator test-connection
notifications without storing peer identity. Returning a URL is not a request
to put it on the system clipboard.

### 2. Shared instance ownership

Separate data ownership from Dashboard window activation. Acquire an exclusive
cross-session file-handle lease in the canonical data directory, held for the
process lifetime, before opening SQLite, binding the receiver, or initializing
the tunnel. Do not include a session ID in resource ownership. Resolve directory
aliases to the same identity; test case, junction, explicit-default-directory,
and environment-variable forms. Failure to obtain the lease is fatal; never
fall back to an unprotected run. Creation of the directory and lock file is the
only permitted pre-lease filesystem mutation.

The entry point owns and releases the lease exactly once; `DashboardRuntime`
borrows it. A competing RpcHost exits with a stable nonzero code and sanitized
stderr. Dashboard displays an equivalent native error instead of depending on
a console. Keep its current session-local, current-user activation IPC for a
second Dashboard launch, but never forward Dashboard activation to RpcHost or
across sessions.

Older Dashboard binaries do not honor the new lease. Before sharing state,
require their exit and upgrade; never imply that the lease retroactively
protects against them. Include supported-version coexistence checks and explicit
upgrade guidance. Do not migrate/copy an in-use database as a workaround.

### 3. RPC host process

Create `src\AgentSignaler.RpcHost\AgentSignaler.RpcHost.csproj`:

- `OutputType=Exe`;
- Windows x64, self-contained, single-file, console subsystem;
- references Dashboard Core, Service, Tunneling, and Contracts;
- uses Kestrel through `Microsoft.AspNetCore.App`;
- contains no WinUI or Windows App SDK dependency.

Target `net10.0-windows10.0.22621.0` and `win-x64`, matching the Dashboard's
current Windows baseline. Use the repository-pinned SDK and existing dependency
versions at execution time; do not add a platform/version migration to this work.

Set `PublishSingleFile=true`, `SelfContained=true`, and
`IncludeNativeLibrariesForSelfExtract=true`; keep trimming disabled until
separately proven safe. Bundle the .NET/ASP.NET runtime and native SQLite
dependencies. No adjacent DLL, runtimeconfig, or deps file may be required;
symbols may be separate optional artifacts. Document the .NET per-user
extraction cache, permissions, environment overrides, concurrent startup, and
read-only/failed-extraction behavior. Test the actual published EXE alone on a
clean supported Windows x64 machine without .NET or Windows App SDK installed.

Command-line contract:

```text
AgentSignaler.RpcHost.exe [--rpc-port 51821] [--data-directory <absolute-path>]
```

The command line must reject duplicate, unknown, malformed, privileged, or
conflicting ports. `--data-directory` should use the same validation and
instance-key behavior as `AGENT_SIGNALER_DATA_DIR`. Use the argument if provided,
otherwise the environment value, otherwise the existing default. When argument
and environment are both present, require them to resolve to the same directory.

Startup should:

1. parse and validate arguments;
2. acquire the shared instance lease;
3. load settings and apply the RPC port override without persisting it;
4. validate that the RPC port differs from the receiver port;
5. if ports conflict, exit before starting the receiver or sharing with a
   sanitized instruction to supply `--rpc-port`; apply the same rule to bind failure;
6. bind Kestrel only to IPv4 and IPv6 loopback on `RpcPort`;
7. expose `/health` and `/rpc`;
8. write exactly one JSON stdout line describing transport readiness, effective
   RPC port, protocol version, and a new host-instance GUID; this does not claim
   receiver or public-tunnel readiness;
9. initialize operational services asynchronously and publish progress;
10. remain active after parent exit or WebSocket loss until explicit shutdown,
    Ctrl+C, OS termination, or unrecoverable process failure.

Use separate transport-ready, initializing, operational, degraded, and stopping
states. `system.getStatus`, settings, diagnostics, and startup cancellation must
work while initializing/degraded. `system.ready` denotes completion of the
initial attempt and includes component outcomes; it must not imply a verified
public endpoint. Preserve recovery defaults with automatic sharing disabled on
unreadable settings. Recoverable storage/receiver/tunnel failures leave control
RPC available; saving restart-required fields instructs the caller to restart
explicitly. Do not add a silent automatic restart.

The RPC control server must be a separate Kestrel application/listener from
`DashboardServer`. Never register `/rpc` on the report listener, tunnel the
control port, expose it through LAN mode, or permit configuration/environment
URL overrides to widen its binding. Test actual LAN and tunneled requests, not
just bind configuration values.

Use exit codes 0 for clean explicit shutdown, 2 for invalid arguments/effective
configuration, 3 for resource-ownership conflict, 4 for RPC bind failure, 5 for
fatal runtime failure, and 6 for unclean shutdown. Degraded but running components
are reported through state, not a premature process exit.

On explicit shutdown, acknowledge the accepted request before closing its socket,
stop admission, cancel initialization/client work, drain durable mutations, stop
sharing and receiver services, then release storage and the lease. Use a bounded
overall shutdown deadline and report unclean completion explicitly. Parent exit
must not initiate this sequence. Ctrl+C requires a console or host signal; do
not rely on it when launched without a console.

Retain the tunnel runner's kill-on-job-close ownership and extend equivalent
ownership to Azure CLI children, including abrupt RpcHost termination. Do not
kill Windows App, sign-in browsers, unrelated CLI sessions, or cloud resources
on ordinary shutdown. If launched inside an external kill-on-parent-close job,
document that it cannot promise survival beyond that external OS policy.

### 4. WebSocket and JSON-RPC protocol

Implement a small repository-owned JSON-RPC 2.0 dispatcher over text WebSocket
messages rather than exposing internal CLR types or coupling the contract to a
UI framework.

Transport requirements:

- endpoint: `ws://localhost:51821/rpc` by default;
- require WebSocket upgrade and an `Origin` whose host is `localhost`,
  `127.0.0.1`, or `[::1]`, with a local HTTP/HTTPS scheme;
- validate `Host` and remote endpoint as loopback defense in depth;
- reject missing or non-local origins;
- allow one active client and return a bounded HTTP conflict response for others;
- set request/message size, nesting, property-count, string-length, and
  outstanding-request limits;
- accept text messages only; reject binary and fragmented messages beyond bounds;
- do not log message bodies, machine details, account identifiers, URLs, paths,
  Dev Box names, or user-entered notes;
- use standard JSON-RPC error objects with stable Agent Signaler error codes and
  sanitized messages;
- support bounded batches, including mixed requests/notifications and correctly
  correlated response arrays; never send responses to valid notifications;
- use `operations.cancel` for cancellable long-running operations, avoiding an
  undocumented method in the reserved `rpc.` extension namespace;
- cancel all client-owned work on disconnect while leaving explicitly persistent
  runtime services in their documented state.

Create explicit wire DTOs in a versioned RPC contract namespace. Never serialize
exceptions or operational CLR records directly. All timestamps use UTC ISO 8601,
GUIDs use canonical strings, enums use stable documented strings, and every
snapshot includes a protocol version, host-instance GUID, domain name, and that
domain's revision. Revisions cannot be compared across domains.

Materialize the following fixed profile in `docs\rpc-host-protocol.md` before
coding. This is an implementation deliverable, not a human approval checkpoint:

- Require `jsonrpc: "2.0"`; distinguish missing IDs from explicit null IDs.
  Preserve string/numeric/null IDs without lossy conversion. Reject duplicate
  outstanding request IDs deterministically. Use named parameter objects for
  application methods; nonempty positional arrays return invalid params.
  Parameterless methods accept absent params, `{}`, or `[]`.
- Use standard parse/invalid-request/method/params/internal-error codes and
  the application codes specified below. Exactly one of `result` and `error` is
  allowed. Empty batches produce one invalid-request error; invalid members of
  nonempty batches produce their own errors; valid notifications never receive
  responses. Batch entries may execute concurrently and have no sequencing
  guarantee; callers needing ordering send a new request after the prior reply.
- Assemble valid fragmented text messages with strict UTF-8 decoding and an
  aggregate byte limit; fragmentation itself is not invalid. Reject binary
  messages and excessive nesting/size before dispatch.
- Apply the numerical limits and pagination rules below. Exercise maximum
  supported machine/session/catalog fixtures; never silently truncate state
  to fit a message.
- Encode state revisions, generations, and sequence values as decimal strings
  so JavaScript does not lose 64-bit precision. Use an explicit wire serializer,
  not persistence serializer defaults.

#### Fixed limits and deadlines

These are initial protocol-v1 limits, not prompts for approval. MiB means
1,048,576 bytes. Test exact limits and one unit above them.

| Boundary | Value and required behavior |
| --- | --- |
| RPC port | 1024 through 65535; default 51821; no automatic fallback |
| Inbound WebSocket message | 1 MiB, measured across all fragments |
| Outbound WebSocket message | 4 MiB, including a complete batch response |
| JSON structure | Maximum depth 32 and 128 properties per object; reject duplicate properties |
| Batch | 32 entries; reject larger batches with invalid request before any entry executes |
| IDs | String IDs up to 128 UTF-16 code units, JSON numbers or null; preserve exact numeric JSON when echoing, compare numeric IDs by value, and distinguish numbers from strings |
| Outstanding work | 32 application requests/notifications; reserve 4 additional slots for cancellation/shutdown controls |
| Duplicate active ID | Invalid request; do not execute the duplicate; advise clients to use unique string IDs |
| General input string | 32,768 UTF-16 code units; domain validators may be stricter |
| New/updated note | 16,384 UTF-16 code units; preserve longer legacy notes and expose them through bounded read chunks rather than rewriting them |
| Page size | Default 100, maximum 250; response byte budget still applies |
| Client queue | At most 64 messages and 16 MiB including encoded bytes; close slow consumers rather than silently drop terminal outcomes |
| Handshake and incomplete message | 10 seconds each; incomplete-message clock begins with first fragment |
| Keepalive | Ping every 30 seconds; 60-second pong timeout; no idle disconnect while transport remains healthy |
| Send/close handshake | 10 seconds / 5 seconds |
| Local-only query/mutation deadline | 10 seconds, cancellation-aware; do not imply rollback after commit |
| CLI operations | Preserve existing command/controller timeout when shorter; discovery overall ceiling 5 minutes, explicit sign-in 3 minutes |
| Runtime initialization | Operational attempt ceiling 3 minutes; retain degraded RPC on timeout |
| Disconnect drain | 15 seconds, then reject new controllers while cleanup remains incomplete |
| Explicit shutdown | 30 seconds total, including a final 5-second forced owned-child cleanup reserve |
| Machine poll | 1 second; state/progress coalescing maximum 100 ms |
| Notification latency | At most 2 seconds from an observed state change under isolated test load; test clock-driven expiry independently of process cold start |
| Process ready watchdog | 30 seconds in isolated subprocess tests; a test harness limit, not a promise of OS scheduling latency |
| Domain snapshot retention | Current domain state only; no per-client historical capture or snapshot-token cache |

Numeric request IDs are limited to 128 JSON characters to bound exact-value
comparison. Treat numerically equivalent forms as the same outstanding ID, but
echo the original request's numeric representation. Published client examples
use unique string IDs to avoid JavaScript precision ambiguities.

Use WebSocket close codes 1000 for normal shutdown, 1003 for binary data, 1007
for invalid UTF-8, 1009 for excessive message size, 1008 for resource-policy or
slow-client violations, and 1011 for unexpected server failure. JSON-RPC errors
do not normally close a healthy connection. Reject a competing/draining client
with HTTP 409; reject invalid origin/host/remote address with HTTP 403.

Use JSON-RPC standard errors -32700 and -32600 through -32603. Application
codes are 1001 validation, 1002 not found, 1003 busy, 1004 stale revision,
1005 unavailable/degraded, 1006 cancelled, 1007 timeout, 1008 persistence failure,
1009 confirmation required, 1010 prerequisite failure, and 1011 result/resource
limit. Return sanitized `data` containing stable field/action identifiers,
`retryable`, and `commitState` (`notCommitted`, `committed`, `unknown`) where
applicable. Error codes and WebSocket close codes are separate namespaces.

Reserve output budget before executing a batch. Execute each batch entry
exactly once; if its result would exceed its allocated response budget, return
1011 for that entry with commit state and a snapshot/query recovery instruction,
not a success-shaped truncated result. Do not repeat a mutation automatically.
Valid notification methods execute under the same validation/conflict limits
but have no response; operational state still records their outcome. Encourage
IDs for mutations, confirmations and cancellation, but never reply to a valid
notification. `operations.cancel` accepts `{ "requestId": ... }`, returns
`cancelRequested`, `alreadyCompleted`, or `notFound`, and never waits behind the
target. A cancel in the same batch can race admission; deterministic cancellation
requires a separate message after operation admission is observed.

Read continuously while commands execute so cancellation is not blocked behind
the work it cancels. Serialize WebSocket writes through one bounded writer.
Give each connection an identity; cancellation targets its request ID and
cannot cancel a replacement connection's request. Domain-specific cancel methods
delegate to the same operation registry. State whether each mutation has committed
when cancellation races completion; never imply cancellation rolled back a
persisted mapping/settings write or a created tunnel.

On disconnect, keep the controlling-client lease in a draining state until
client-owned work has finished cleanup. Existing receiver/tunnel hosting is
runtime-owned and remains active. If cleanup cannot finish within the contract's
deadline, report degraded/busy state and fail closed to new controllers rather
than allow overlapping mutations. Normal runtime shutdown remains available via
the process control path.

Enforce this conflict matrix in the core, not only in UI button state:

| Operation group | Exclusive resources |
| --- | --- |
| Azure sign-in, catalog refresh, fresh resolve/open, Azure diagnostics | Azure-account gate |
| Machine edit/remove/map/clear/open | Target machine gate; fresh resolution also takes Azure gate |
| Settings update and discovery-target persistence | Settings gate |
| Sharing start/stop/delete/logout and Dev Tunnel account diagnostics | Tunnel gate; settings persistence also takes settings gate |
| Check all prerequisites | Compose individual checks; serialize shared-account checks |

Acquire required resources atomically in a stable order without holding partial
leases; return busy rather than enqueue conflicting mutations. Cancel, stop and
shutdown signals bypass admission gates to interrupt their current owners.
Read immutable state without mutation gates. Windows App's existing process-wide
activation lock remains short-lived and never spans CLI/network work.
Use expected settings/machine revision plus host-instance GUID for stale-edit
rejection. Persisted operations are never implicitly rolled back on cancellation.

### 5. Version 1 RPC surface

System and lifecycle:

- `system.getCapabilities`
- `system.getStatus` (runtime lifecycle only, not an aggregate of all domains)
- `system.cancelStartup`
- `system.shutdown`
- notifications: `system.ready`, `system.changed`, `system.problem`,
  `system.shuttingDown`

Settings:

- `settings.get`
- `settings.update`
- return validation errors and a `restartRequired` set for receiver port,
  connection mode, CLI paths, and RPC port;
- separate operational settings from WinUI-only theme/compact preferences while
  preserving backward-compatible settings-file loading.

Machines:

- `machines.list`
- `machines.get`
- `machines.getSessions`
- `machines.getNote`
- `machines.updateDetails`
- `machines.remove`
- notifications: `machines.changed`

Receiver and sharing:

- `receiver.getStatus`
- `sharing.getStatus`
- `sharing.start`
- `sharing.stop`
- `sharing.delete` with an explicit confirmation field
- `sharing.logout` with an explicit confirmation field
- `sharing.cancel`
- notifications: `receiver.changed`, `sharing.changed`
- notification: `receiver.connectionTestReceived`

Firewall is an installer responsibility, not an RPC domain. Do not expose
`firewall.*` methods or add RpcHost runtime helper modes that elevate or modify
rules. `receiver.getStatus` may report the recorded MSI receiver port and a
mismatch with the effective port, with instructions to run MSI maintenance.
This metadata is not proof that Windows Firewall permits traffic. Standalone
EXE mode reports firewall provisioning as manually managed.

Prerequisites:

- `prerequisites.getStatus`
- `prerequisites.check`
- `prerequisites.checkAll`
- `prerequisites.cancel`
- notifications: `prerequisites.changed`
- checks capture the requested saved/unsaved path and return that tested path;
  results never silently switch to the path used by runtime operations.

Dev Box discovery:

- `devboxes.getCatalog`
- `devboxes.refresh`
- `devboxes.cancel`
- notifications: `devboxes.changed`

Windows App:

- `windowsApp.getState`
- `windowsApp.map`
- `windowsApp.signIn`
- `windowsApp.refresh`
- `windowsApp.open`
- `windowsApp.openLastKnown`
- `windowsApp.clear`
- `windowsApp.cancel`
- notifications: `windowsApp.changed`

Operations:

- `operations.cancel` targeting an outstanding request on this connection
- valid id-less invocations execute without responses; only ID-bearing requests
  are individually addressable by `operations.cancel`.

Mutation methods must return the resulting typed state, not only a success
boolean, so a WebView can reconcile after missed notifications.

### 6. State publication and reconnect behavior

Each domain owns its current immutable state and a decimal-string revision.
Capture a domain's state and revision atomically within that domain; do not
introduce a global state-owner lock or cross-domain snapshot barrier. Different
domain reads may reflect different instants. This is intentional eventual
consistency, not a globally transactional view.

Use the existing query methods for snapshots: `system.getStatus`,
`settings.get`, `machines.list`/`machines.get`, `receiver.getStatus`,
`sharing.getStatus`, `prerequisites.getStatus`, `devboxes.getCatalog`, and
`windowsApp.getState`. Machine collection snapshots contain summaries;
machine-specific detail/session queries can use entity revisions so updates to
another machine do not invalidate that entity's reads. A mutation response
includes the affected domain/entity revision and resulting state, not a new
cross-domain snapshot.

The existing `<domain>.changed` notifications are invalidations only. Their
payload is `{ hostInstanceId, domain, revision }`, with an optional `machineId`
for entity-specific changes. They contain neither replacement state nor deltas.
Clients mark the domain dirty and refetch it. Coalesce pending invalidations for
the same domain/entity to the highest revision within the existing 100 ms limit.
Keep lifecycle/problem events and terminal operation responses separate from
coalescible invalidations; terminal outcomes must not be silently dropped.

Register the connection for change notifications before processing its first
query. Each domain installs the subscriber against its own publication boundary;
there is no need to lock all domains at once. On first connect or reconnect,
the client fetches runtime status and all domains it displays. Track the highest
invalidated revision independently per domain/entity. If a query returns an older
revision than already observed, retain it only as visibly stale state and refetch.
If an invalidation arrives after a response, mark that domain dirty normally.
Never let a late older response replace newer state. Permit at most one refetch
in flight per domain/entity and coalesce further invalidations during it.

Use a fresh host-instance GUID on every RpcHost process start. On instance
change discard old revision watermarks and refetch all displayed domains.
Reconnection always refetches even when the instance is unchanged; notifications
are not replayed. There are no subscription snapshot tokens or retained captures.

Notifications and responses share one bounded outbound writer. If a client
cannot keep up, close it with the documented code and require normal domain
refetch on reconnect. Do not allocate unbounded queues to preserve a slow view.

Continue reevaluating time-derived state without incoming reports: offline
deadlines and transient session results must change in RPC snapshots. Publish
effective session states using the existing reducer, while retaining their
timestamps. Exclude continuously changing observation timestamps from equality
checks. On poll failure keep the last good domain snapshot, mark it stale, and
invalidate that domain. Report recovery explicitly. Internally poll machines at
the existing one-second cadence and refresh after local mutations; do not require
the browser to poll unchanged domains.

For collections exceeding a bounded response, use stateless `offset`/`limit`
pagination against the current domain/entity revision, not opaque cursors or
retained snapshots. The first page supplies its revision; subsequent requests
must provide that revision as `expectedRevision`. Read the page and validate
the revision atomically within the domain. Changed state returns stale-revision
error and the client discards that partial assembly and restarts. Sort items
deterministically. Return an explicit next offset or completion indicator and
respect the byte limit even when fewer than the requested items fit.
`machines.getNote` uses the same entity-revision rule for bounded text chunks;
never silently truncate or rewrite legacy notes.

After three consecutive pagination restarts without completing a view, the
example client retains the last complete view marked stale, waits one second,
and retries on demand or the next invalidation. The server retains no historical
view to accommodate churn. Small domain snapshots return in one response.
Test invalidation-before-response, invalidation-after-response, concurrent
refetches, domain independence, instance restart, and pagination churn explicitly.

### 7. Settings and compatibility

Extend the settings schema with:

```json
{
  "RpcPort": 51821
}
```

Persist PascalCase fields to match the existing settings serializer; wire DTOs
may use camelCase. Loading older files defaults missing `RpcPort` to `51821`.
Preserve existing theme/compact preferences during headless updates. Preserve
unrecognized top-level persisted fields as JSON extension data; do not expose
them in RPC DTOs or accept unknown mutation fields. Keep an atomic, serialized
settings writer and fail explicitly if data exceeds the 1 MiB settings-file bound.
Validate saved/effective RPC ports in the non-privileged range. An old receiver
using `51821` must remain valid for Dashboard; RpcHost refuses the effective
collision and instructs `--rpc-port`, without overwriting old settings.

Return saved settings, effective settings, override indicators, a settings
revision, and restart-required fields separately. Command-line overrides are
never persisted implicitly. Require the caller's expected settings revision
for updates to prevent stale overwrite.

Keep the current data directory, database, tunnel identity, Dev Box mappings,
and receiver protocol compatible so users can alternate between Dashboard and
RpcHost after one process exits. Do not introduce a database migration unless
the RPC contract requires durable data not already represented.

### 8. Error, privacy, and security behavior

- Keep all external errors sanitized and stable.
- Preserve explicit confirmation for destructive tunnel operations.
- Preserve cancellation boundaries and never report cancellation as success.
- Treat local filesystem, SQLite, CLI, WebSocket, and process-start failures as
  typed RPC failures plus a sanitized problem notification where appropriate.
- Never return Azure CLI raw stdout/stderr or arbitrary exception messages.
- Bound all collections exposed by RPC using existing application limits.
- Document that loopback without authentication is suitable only for a trusted
  local desktop context and that any allowed localhost page can control the
  process.
- Add a threat-model section covering DNS rebinding, hostile local web pages,
  origin spoofing outside browsers, port squatting, stale clients, denial of
  service, sensitive response data, and destructive commands.

Restrict origins by parsed exact HTTP/HTTPS hosts (`localhost`, `127.0.0.1`,
`::1`) on any valid port; reject suffix matches, user-info, non-origin paths,
opaque/null and missing values. Do not equate these checks with user identity.
Response allowlist: effective machine/session status and source descriptors,
display names, notes, timestamps, GUIDs, mapping Dev Box/project/endpoint/account/
tenant identity, catalog metadata, effective report URL, saved/effective CLI
paths, validated diagnostic summaries and operation state. These fields are
available to the unauthenticated controller by the accepted decision, never logs.
Exclude cached `ms-cloudpc` connection URIs, credential material, raw CLI streams,
arbitrary exception text, environment dumps and enumerated window titles. Wire
models are explicit DTOs so adding a CLR property cannot expand exposure.

## Implementation phases

### Phase 1: contract and characterization

1. Use this reviewed document as the implementation baseline. Recheck current
   source and concurrent work before extraction; do not overwrite unrelated work.
2. Add architecture tests that characterize current Dashboard startup,
   settings, machine mutation, catalog, tunnel, Windows App, cancellation, and
   shutdown behavior before extraction.
3. Define JSON-RPC v1 DTOs, method catalog, error codes, notification schemas,
   size limits, close codes, and TypeScript-facing examples in
   `docs\rpc-host-protocol.md`.
4. Complete the parity matrix with source method, wire method, DTOs, ownership,
   conflict/cancellation semantics, and executable acceptance test for each row.
   Add a small browser/TypeScript contract harness, not the deferred web UI.
5. Validate the contract automatically against these fixed decisions and proceed
   to Phase 2; there is no human approval gate.

### Phase 2: extract Dashboard Core

1. Add `AgentSignaler.Dashboard.Core`.
2. Move operational types without behavior changes.
3. Introduce `DashboardRuntime`, independent domain snapshots/revisions, and
   invalidation events; do not build a retained cross-domain snapshot system.
4. Move shared instance ownership into the core.
5. Refactor the WinUI Dashboard to use the runtime.
6. Preserve UI behavior, receiver protocols, and persisted data compatibility;
   migrate tests from moved linked source to the actual core assembly.

### Phase 3: implement RPC transport

1. Add bounded JSON-RPC parsing, batch processing, and concurrent dispatch.
2. Add localhost origin/host/remote-endpoint validation.
3. Add the single-client lease and bounded outbound notification channel.
4. Map runtime queries, mutations, progress, cancellation, and shutdown to the
   version 1 protocol.
5. Add startup readiness output and documented exit codes.

### Phase 4: packaging and automation

1. Add both projects to `AgentSignaler.slnx`.
2. Publish and independently smoke-test the single-file win-x64
   `AgentSignaler.RpcHost.exe`, using the release workflow's calculated version.
3. Add a dedicated per-user x64 `AgentSignaler.RpcHost.msi` with stable, distinct
   product/upgrade identity and installation directory. Include the EXE and
   applicable notices, not another copy of the WinUI runtime. Use
   `%LOCALAPPDATA%\Programs\AgentSignaler\RpcHost` and a new upgrade GUID generated
   once during implementation and committed as a stable installer identity.
   Follow existing WiX product-code/version patterns; never reuse Dashboard or
   Remote upgrade identities.
   Always install the Private-profile receiver firewall rule transactionally as
   specified below; this is not an optional LAN feature.
4. Extend `Build-Installers.ps1` and `Test-Installers.ps1`, including the
   `ApplicationMsisOnly` mode. Retain existing Dashboard/Remote outputs and tests.
   Add raw EXE and MSI to both release asset preparation and upload argument
   lists and to `SHA256SUMS.txt`; propagate one calculated version throughout.
5. Extend `Update-AgentSignaler.ps1` to recognize installed RpcHost MSIs by
   upgrade identity, validate exact asset names/checksums/version/architecture/
   per-user scope, and upgrade only installed products. Preserve private-release
   credentials, trusted download handling, and non-mutating `-WhatIf`.
   Do not discover or replace standalone EXE copies.
6. Add core/RPC test projects to explicit CI test steps as well as the solution;
   include publish and MSI inspection gates and dependency/privacy audits.
7. Never start RpcHost automatically from MSI, never register it at sign-in,
   and never trigger a release merely by implementing this plan.

Servicing policy: installation, update and uninstall fail with an actionable
in-use result while the exact installed RpcHost is running. Never send shutdown
to an unauthenticated TCP port as proof of process identity, force-kill a process,
or silently close a controller's application. The user must explicitly stop it
before servicing; unattended tests simulate this condition. Disable automatic
application shutdown/restart for this MSI. Repair/uninstall preserve shared
settings, SQLite and tunnel identity. Do not add cloud cleanup to servicing.

#### MSI-owned firewall provisioning

The RpcHost MSI always creates an enabled inbound TCP allow rule for its exact
installed EXE and receiver port on the Private profile only. Never include the
RPC port, Public/Domain profiles, all programs, all ports, or edge traversal.
Create the rule even when saved connection mode is Internet sharing; explain
that it is unnecessary in that mode but provisioned for later LAN use. This rule
does not change the receiver's binding or make a loopback listener remotely
reachable.

Use an MSI `RECEIVERPORT` property: explicit validated value first, otherwise a
valid existing port from the installing user's default Dashboard settings,
otherwise 51820 when the settings file is absent. Malformed existing settings
require an explicit property rather than silent fallback. Validate the
non-privileged range and reject a known collision with the configured RPC port.
Capture the installing user's identity, installation directory and default data
path before elevation; do not accidentally read an administrator's profile.
Do not persist receiver settings or launch RpcHost as part of provisioning.

Record the provisioned port as installer-owned metadata. Changing the runtime
receiver port or using a custom data directory does not update the rule.
RpcHost reports mismatches; MSI maintenance with `RECEIVERPORT=<port>` updates
the owned rule transactionally. Do not make settings updates elevate or silently
reconfigure Windows Firewall. Document explicit manual firewall setup for
standalone copies and non-default deployments.

Prefer the repository-compatible WiX firewall extension for transactional
creation/removal. Add narrowly scoped installer actions only where required for
ownership validation and rollback; never invoke a per-user-writable application
EXE as an elevated firewall helper. Use a distinct rule identity scoped to the
RpcHost product and installing user, plus exact installed-path/port/profile
ownership metadata. Leave Dashboard and foreign rules untouched. Refuse
ownership collisions or unexpected external edits with an actionable servicing
error rather than overwriting them.

Keep application installation per-user, but explicitly author and validate the
elevated installer execution needed to change machine firewall policy. If a
small packaged installer custom-action component is required, scope it to these
validated MSI operations; it is not part of the standalone EXE interface.
Interactive servicing may require UAC. If elevation is denied or unavailable,
fail and roll back; never report successful installation with the required rule
missing. Silent servicing requires a correctly elevated installation context
for the intended user and must fail non-interactively otherwise.

Repair verifies/restores the unmodified owned rule; upgrades replace it without
leaving duplicate rules; uninstall removes only the proven-owned rule. Failed
install, upgrade, port change or uninstall restores the prior owned rule and
metadata. Preserve shared settings, database, and tunnel identity. Required
firewall cleanup failure makes servicing fail rather than silently leave an
orphan. Test table authoring and rollback logic automatically; actual elevated
servicing remains a deferred disposable-VM acceptance gate, not a reason for
the implementation agent to request UAC.

`Update-AgentSignaler.ps1` skips uninstalled RpcHost, treats equal/newer installed
versions as no-op, and reports an explicit in-use error for a running installed
host. Implement and test that behavior against fake inventory/process adapters.
Propagate MSI firewall/elevation failures explicitly. Do not add updater-driven
firewall changes or bypass MSI authorization. Preserve the recorded receiver
port during upgrade unless explicitly supplied for MSI maintenance.
Use existing current version metadata for local builds and a separate synthetic
higher version only in upgrade fixtures. Do not bump production versions to run
tests. CI release execution later applies its normal calculated version.

### Phase 5: documentation and acceptance

1. Document command line, endpoint, origin policy, protocol, lifecycle, exit
   codes, state compatibility, and troubleshooting.
2. Add JavaScript and TypeScript connection examples that use only synthetic
   data.
3. Add a manual test matrix for browser/WebView origins, reconnect, process
   ownership conflicts, receiver/tunnel lifecycle, CLI cancellation, Windows App
   activation, and clean shutdown.
4. Update README architecture and release asset descriptions.

## Test strategy

Unit tests:

- argument and settings validation;
- instance-key derivation and mutual exclusion;
- JSON-RPC parsing, IDs, errors, bounds, cancellation, and DTO serialization;
- batches/notifications, exact ID echo, JavaScript-safe integers, and duplicates;
- origin, host, and loopback remote-address validation;
- single-client admission;
- domain/entity revisions, invalidation coalescing, outbound backpressure,
  stateless pagination and domain refetch on reconnect;
- runtime command validation and destructive-operation confirmations.

Runtime integration tests:

- start/stop with isolated data directories and ephemeral test overrides;
- query and mutate machines through a real WebSocket;
- settings persistence and restart-required reporting;
- Dev Box/catalog and prerequisite progress using fakes;
- tunnel state and cancellation using fake runners/probes;
- Windows App mapping/open/reuse using fake platforms;
- client disconnect cancellation and reconnect reconciliation;
- Dashboard/RpcHost mutual exclusion in both launch orders;
- cross-session/path-alias resource exclusion and old-version upgrade handling;
- valid fragmented text and malformed/oversized/binary/non-local-origin rejection;
- actual RPC non-reachability through report listeners, LAN and tunnel exposure;
- disconnect draining, cancellation after commit, concurrent batches and mutation
  conflicts, startup failure recovery, and domain query/invalidation races;
- stdout/stderr privacy and stable exit codes.

Regression tests:

- all existing Service, Remote, Integration, and Tunneling tests;
- WinUI Dashboard build and startup characterization;
- receiver compatibility and persisted database/settings/tunnel-state fixtures;
- self-contained clean publish from a clean-clone-equivalent tree;
- actual EXE-only execution and SQLite access without adjacent dependencies;
- read-only installed-product/upgrade/repair/uninstall/rollback fixtures and
  isolated updater `-WhatIf`; actual MSI servicing is deferred unless an existing
  disposable Windows environment is available.

Deferred human/release validation (not an unattended implementation gate):

- supported WebView/browser connection from localhost;
- rejection of non-local and missing origins;
- real Azure CLI, Dev Tunnel CLI, Windows App, tray Dashboard exclusion, and
  release artifact execution on supported Windows versions;
- parent-exit, forced-termination, and port-conflict checks are already required
  automated subprocess tests; repeat them in the actual host as release coverage;
- installer UAC approve/deny and silent insufficient-privilege failure;
- mandatory Private-profile rule in both connection modes, exact executable/
  receiver port, no RPC-port rule, port maintenance, foreign-rule preservation,
  repair/upgrade/uninstall and rollback;
- browser/WebView foreground activation under Windows restrictions, including
  explicit failure when focus transfer is denied rather than duplicate launch.

## Unattended implementation completion criteria

- `AgentSignaler.RpcHost.exe` starts headlessly, binds only loopback port `51821`
  by default, and accepts exactly one localhost-origin controller, with admission
  blocked while a disconnected controller's operations are draining.
- A JavaScript/TypeScript client can reproduce every non-visual Dashboard
  capability in the parity matrix through documented JSON-RPC v1 methods and
  notifications, except explicitly excluded startup registration, MSI-owned
  firewall setup, and visual UI.
- Dashboard and RpcHost share one tested operational runtime and cannot own the
  same data directory concurrently.
- Existing persisted state and receiver clients remain compatible.
- Domain snapshots are individually coherent; cross-domain atomicity is not
  promised. Invalidation/refetch and reconnect tests converge to current state
  without retained cross-domain snapshots or replay history.
- Explicit shutdown releases owned services, child CLIs, database handles and
  resource lease within the documented deadline. Cancellation preserves already
  committed changes and established runtime services; parent exit leaves RpcHost
  alive. Force-kill tests verify the separately documented OS cleanup guarantees.
- Protocol limits, startup/shutdown deadlines, maximum notification latency,
  snapshot sizes, and extraction behavior meet this plan's numerical criteria
  and are tested at boundary values. Configuration assertions
  are not substitutes for actual runtime, networking, timing, or packaging tests.
- CI produces the EXE and dedicated MSI with the same release version, and
  exercises the actual TypeScript/browser contract without requiring the future
  web UI. The execution run builds local artifacts and runs the automated gates
  available in its environment; it does not dispatch releases. Deferred human/
  release gates remain explicitly Not run and do not block implementation
  completion. A blocked required automated gate means the result is incomplete,
  not approved or silently waived.

## Notes and risks

- Unauthenticated control by unrelated localhost pages and local users is an
  explicitly accepted risk, not a resolved security finding. Do not advertise
  private-to-host access or use this endpoint across a trust boundary.
- Extracting Dashboard Core is the largest risk. Avoid a copy-and-diverge
  implementation even if it appears faster.
- Public DTOs must not inherit accidental behavior from current `internal`
  records or expose private machine/account/path data beyond what the future UI
  actually needs.
- The dedicated MSI adds servicing scope but does not grant independent startup
  behavior. Standalone copies can outlive their launchers and must be shut down
  explicitly before installation/update or ownership transfer.
