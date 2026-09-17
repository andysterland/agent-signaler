# RpcHost protocol v1

This document is the implementation contract for the foreground
`AgentSignaler.RpcHost` adapter to Dashboard Core. The
[implementation plan](rpc-host-implementation-plan.md) records the confirmed
product decisions. The production web UI is not part of this feature.

## Trust boundary

**There is no authentication. Any unrelated localhost page, or native client
including another local Windows user, that supplies an accepted Origin can
control RpcHost as its user.** This includes changing executable paths, running
diagnostics, deleting local machine history, signing out of CLI accounts, and
explicitly confirmed destructive tunnel commands. Only use it in a trusted
local desktop context. Origin checks, confirmations, loopback binding and the
single-controller lease are not authentication or a private WebView channel.

The receiver and control server are separate Kestrel applications. Only the
receiver may bind LAN interfaces or be exposed by Dev Tunnel. `/rpc` is never
registered on the receiver and the RPC port must never be tunneled. Configuration
and environment URL overrides cannot widen the control server's binding.

## Process and distribution

```text
AgentSignaler.RpcHost.exe [--rpc-port 51821] [--data-directory <absolute-path>]
```

The Windows 11 x64 console EXE is self-contained, untrimmed and single-file. It
does not require WinUI or Windows App SDK. Azure CLI, Dev Tunnels CLI and Windows
App remain external optional prerequisites. A separate per-user MSI installs to
`%LOCALAPPDATA%\Programs\AgentSignaler\RpcHost`; standalone copies are managed
manually. Neither MSI nor updater registers startup or launches RpcHost.

Port values are decimal integers from 1024 to 65535. Duplicate/unknown arguments,
missing values, malformed ports and effective receiver/RPC collisions fail
before operational startup. There is no fallback port. Use `--rpc-port` to
resolve a collision or occupied control port. The default is 51821.

The data-directory argument takes precedence over `AGENT_SIGNALER_DATA_DIR`,
then the existing `%LOCALAPPDATA%\AgentSignaler` default. When argument and
environment both exist, they must identify the same canonical directory.
Directory aliases, case and environment-variable forms must not bypass ownership.
An entry-point-owned exclusive file handle protects the canonical directory
across sessions before SQLite, receiver or tunnel initialization. The runtime
borrows this lease. Only directory/lock-file creation is allowed before ownership.
Dashboard activation remains current-user, session-local and Dashboard-only.

Stop and upgrade older Dashboards before alternating hosts: old binaries do not
honor the new lease. Never copy or migrate a live database to bypass ownership.
Supported Dashboard and RpcHost versions retain the settings, database, tunnel
identity and Dev Box mappings so users can alternate after the owner exits.

Native SQLite and runtime libraries may extract into the .NET per-user bundle
cache (normally beneath `%TEMP%\.net`). `DOTNET_BUNDLE_EXTRACT_BASE_DIR` changes
that cache, not the data directory. Use an absolute, user-controlled directory
with write/execute permissions; do not share it across untrusted users. Concurrent
bundle extraction is handled by the .NET host, not the resource lease. A read-only
EXE directory is supported when extraction/data locations are writable. Failed
extraction can prevent managed startup and the readiness line entirely; diagnose
cache permissions, free space and endpoint protection instead of changing ports.
The automated EXE-only test uses an otherwise empty directory and isolated cache;
execution on a clean image without installed .NET remains release acceptance.

### Lifecycle

The transport binds IPv4 and IPv6 loopback, exposes `/health` and `/rpc`, then
writes exactly one JSON stdout line with transport readiness, effective RPC port,
protocol version and fresh `hostInstanceId` GUID. This does not assert receiver
or public sharing health. Diagnostic output must remain sanitized; stdout is not
a progress/log stream.

States are `transportReady`, `initializing`, `operational`, `degraded` and
`stopping`. Control status, settings, prerequisites and startup cancellation
remain available during initialization or recoverable failure. `system.ready`
means the first initialization attempt completed and carries component outcomes;
it is not proof of an externally reachable endpoint. Unreadable settings use
recovery defaults with automatic sharing disabled. No automatic process restart
is performed after settings edits.

Parent exit and WebSocket loss do not stop RpcHost. Receiver and established
sharing are runtime-owned. Explicit shutdown, Ctrl+C, OS termination or fatal
failure ends the process. An external kill-on-parent-close Windows job can
override parent-exit survival; no application can override that external policy.
CLI children are owned jobs; Windows App and sign-in browser windows are not.

Native bundle-bootstrap failures happen before managed `Main`: failed extraction
uses the .NET host's own diagnostic and exit-code behavior, not the managed
2--6 profile below. Such a failure produces no transport-readiness line and must
not be mistaken for a running degraded host.

| Exit | Meaning |
| --- | --- |
| 0 | Clean explicit shutdown |
| 2 | Invalid arguments/effective configuration |
| 3 | Data resource ownership conflict |
| 4 | RPC bind failure; use `--rpc-port` |
| 5 | Fatal runtime failure |
| 6 | Unclean bounded shutdown |

Shutdown acknowledges acceptance before closing the socket, stops admission,
cancels initialization/client work, drains durable mutations, then stops sharing,
receiver, storage and finally releases the entry-point lease. The total budget
is 30 seconds including a final five-second owned-child cleanup reserve. Ctrl+C
requires an attached console or equivalent host signal. Do not rely on it from
a launcher without a console.

## WebSocket admission

Connect to `ws://localhost:51821/rpc`. Browser origins must be exact parsed
HTTP/HTTPS origins with host `localhost`, `127.0.0.1` or `[::1]`, on any valid
port. Reject absent, `null`, opaque, user-info, path-bearing, query/fragment and
suffix-match origins. Host and remote endpoint must also be loopback. Native
clients must send Origin explicitly; this is not identity verification.

Invalid Origin/Host/remote address returns bounded HTTP 403. An active or draining
controller causes HTTP 409 for competitors. The subscriber is installed before
processing the first query. Disconnect cancels connection-owned requests, not
receiver/sharing services, and keeps admission closed until cleanup completes.
After 15 seconds of incomplete drain the host remains fail-closed/degraded;
never admit a replacement while mutations still run.

| Close code | Meaning |
| --- | --- |
| 1000 | Normal shutdown |
| 1003 | Binary messages unsupported |
| 1007 | Invalid UTF-8 |
| 1009 | Excessive message size |
| 1008 | Resource policy or slow consumer |
| 1011 | Unexpected server failure |

JSON-RPC errors normally leave a healthy connection open. Valid fragmented text
is assembled with strict UTF-8 and an aggregate byte bound.

## Fixed profile

MiB means 1,048,576 bytes. Bounds include envelopes/encoded bytes, not just the
state payload. Boundary tests must exercise the exact maximum and one above.

| Boundary | v1 limit |
| --- | --- |
| Inbound message, all fragments | 1 MiB |
| Outbound message, including entire batch | 4 MiB |
| JSON nesting / properties per object | 32 / 128; duplicate properties rejected |
| Batch | 32 entries; reject larger batch before executing any entry |
| String ID | 128 UTF-16 code units |
| Numeric ID | 128 raw JSON characters; exact numeric-value comparison |
| General input string / new note | 32,768 / 16,384 UTF-16 code units |
| Page size | Default 100, maximum 250, further reduced by response byte budget |
| Outstanding application work | 32, including valid notifications |
| Reserved control slots | 4 for cancellation/shutdown |
| Outbound queue | 64 messages and 16 MiB encoded total |
| Handshake / incomplete message | 10 seconds / 10 seconds from first fragment |
| Keepalive ping / pong timeout | 30 seconds / 60 seconds |
| Send / close handshake | 10 seconds / 5 seconds |
| Local query/mutation | 10 seconds; cancellation does not imply rollback |
| Discovery / explicit sign-in | 5 minutes / 3 minutes, or existing shorter timeout |
| Initialization / disconnect drain | 3 minutes / 15 seconds |
| Shutdown | 30 seconds, including final five-second forced child cleanup |
| Machine poll / invalidation coalescing | 1 second / at most 100 ms |
| Notification latency | At most 2 seconds from observed change in isolated tests |
| Test process readiness watchdog | 30 seconds; not an OS scheduling guarantee |
| Settings file | 1 MiB, fail explicitly before oversized persistence |
| Snapshot retention | Current state per domain only |

### Requests, IDs, notifications and batches

Require `jsonrpc: "2.0"`. An absent `id` is a notification; explicit `null` is
an ID-bearing request. Echo string, number or null unchanged. Numeric IDs compare
by mathematical value, separately from string IDs; `1`, `1.0` and `1e0` identify
the same active request. Duplicate active IDs fail deterministically without
executing a second command. Prefer unique string IDs in JavaScript.

Application parameters are named objects. Parameterless methods accept absent
params, `{}` or `[]`. Nonempty positional arrays are invalid params. Unknown
mutation fields are rejected. Revisions, sequence values and generations are
decimal strings; timestamps are UTC ISO 8601; GUIDs are canonical strings.
Enums have explicit stable wire strings.

```json
{"jsonrpc":"2.0","id":"view-1","method":"system.getStatus","params":{}}
```

Responses contain exactly one of `result` and `error`. Valid notifications
execute and never receive responses, including method/parameter/operation
failures; their resulting operational state is still observable. Encourage
ID-bearing calls for mutations, confirmations and cancellation.

Batches may mix requests and notifications and execute concurrently with no
ordering guarantee. Empty batch returns one invalid-request error. An invalid
member of a nonempty batch gets its own error. All-notification batches produce
no response; otherwise correlate the response array by ID, not position.
Reserve output budget before dispatch. Execute each entry once only. A result
exceeding its allocated budget yields 1011 for that entry, with commit state and
query/reconciliation guidance. Never retry a mutation to recover a large result.

The reader continues receiving while operations run. `operations.cancel` takes
`{ "requestId": ... }` and returns `cancelRequested`, `alreadyCompleted` or
`notFound`; it does not queue behind the target. Cancellation is scoped to this
connection's registry. Id-less calls cannot be individually targeted. Cancellation
inside the same batch can race admission; send a separate message after observing
admission for deterministic cancellation. Domain cancel methods use the same
operation ownership. A cancelled committed write is not a rolled-back write.

The connection remembers only its last 256 completed IDs for `alreadyCompleted`;
after eviction, cancellation of an old completed ID returns `notFound`. This
bounded operation-ID bookkeeping is not snapshot retention or notification replay.
Domain cancellation targets this connection's registry, including id-less work,
not runtime-owned automatic startup sharing. A shutdown in a batch cancels
client-owned work before acknowledging the complete batch; its 30-second total
deadline begins at shutdown admission, not after an arbitrarily long batch wait.

### Errors

| Code | Meaning |
| --- | --- |
| -32700 | Parse error |
| -32600 | Invalid request |
| -32601 | Method not found |
| -32602 | Invalid params |
| -32603 | Internal error |
| 1001 | Validation |
| 1002 | Not found |
| 1003 | Busy/conflicting operation |
| 1004 | Stale revision/host instance |
| 1005 | Unavailable/degraded |
| 1006 | Cancelled |
| 1007 | Timeout |
| 1008 | Persistence failure |
| 1009 | Confirmation required |
| 1010 | Prerequisite failure |
| 1011 | Result/resource limit |

Application error data uses stable field/action identifiers, `retryable` and,
where applicable, `commitState`: `notCommitted`, `committed` or `unknown`.
Error numbers are distinct from the WebSocket close-code namespace. Messages
must not contain arbitrary exceptions, raw CLI streams or private diagnostics.

## Independent state and stateless paging

Every domain snapshot has protocol version, host instance, domain and that
domain's decimal-string revision. Capture state/revision atomically inside its
domain, never through a global snapshot barrier. Cross-domain reads deliberately
reflect different instants. Mutations return resulting typed state with the
affected revision, not a Boolean or a new aggregate snapshot.

The envelope is
`{ protocolVersion: 1, hostInstanceId, domain, revision, state, isStale }`.
`isStale` is always a Boolean. Collection `state` contains `items`, `offset`,
`limit`, `totalCount` and `nextOffset`; completion is explicit `nextOffset: null`,
not an omitted field. Revisions are strings even for the first revision.

`<domain>.changed` carries only:

```json
{"hostInstanceId":"00000000-0000-0000-0000-000000000001","domain":"machines","revision":"42","machineId":"00000000-0000-0000-0000-000000000002"}
```

`machineId` is optional and identifies entity-specific invalidation. Changed
notifications have no replacement state or delta. Coalesce only pending
invalidations for the same domain/entity to the highest revision within 100 ms.
Lifecycle/problem events and terminal operation outcomes are separate and may
not be silently dropped. Responses and notifications use one bounded writer;
slow consumers are closed, not accommodated with an unbounded queue.

On initial connection and every reconnect, fetch runtime and displayed domains,
even when host instance is unchanged. Track independent highest invalidation
watermarks; a late older response must not replace newer state. Keep at most one
refetch per domain/entity in flight and coalesce additional invalidations.
On host-instance change discard watermarks and refetch all displayed state.
There is no replay history, subscription token, retained capture or cursor cache.

Collections sort deterministically and accept `offset` and `limit`. First page
returns its revision and an explicit next offset/completion indicator. Subsequent
pages require `expectedRevision`; page read and revision validation are atomic
within the domain/entity. A change yields 1004 and the client discards the partial
assembly and restarts. Byte budgets may return fewer items than requested, never
silent truncation. Machine summaries do not inline unbounded sessions/notes.
`machines.getSessions` and `machines.getNote` use entity revisions so unrelated
machines do not invalidate their reads. Longer legacy notes are preserved and
returned in bounded text chunks, never rewritten merely to fit current limits.

After three consecutive incomplete pagination restarts, the example client keeps
the last complete view visibly stale, waits one second, then retries on demand
or the next invalidation. The host retains no historical view to rescue churn.

Machine polling uses the existing effective-state reducer every second and after
local mutations. Offline deadlines and transient results change even without
new reports. Retain timestamps but exclude constantly changing observation times
from equality. A failed poll retains the last good snapshot marked stale and
invalidates it; recovery is explicit. Browsers need not poll unchanged domains.

## Method catalog and ownership

The runtime is the sole operational composition/cancellation/disposal owner.
WinUI retains windows, dialogs, tray, clipboard, compact navigation and existing
Dashboard firewall/startup actions. The protocol intentionally adds neither a
transcript export surface nor a production WebView UI.

| Domain | Methods | State/events and command semantics |
| --- | --- | --- |
| System | `system.getCapabilities`, `system.getStatus`, `system.cancelStartup`, `system.shutdown` | Lifecycle only, never all-domain aggregate. `system.ready`, `system.changed`, `system.problem`, `system.shuttingDown`. |
| Settings | `settings.get`, `settings.update` | Saved/effective settings, overrides, revision and restart-required fields separately. Expected host/revision required for updates. |
| Machines | `machines.list`, `machines.get`, `machines.getSessions`, `machines.getNote`, `machines.updateDetails`, `machines.remove` | Summary/detail/session/note snapshots and `machines.changed`. Expected host/entity revision for edits; explicit confirmation for removal, which resets replay history. The local machine cannot be removed (`1001`, field `localMachine`). |
| Receiver | `receiver.getStatus` | Listener state, connection mode, effective report URL and MSI provisioning metadata; `receiver.changed`, `receiver.connectionTestReceived` without peer identity. |
| Sharing | `sharing.getStatus`, `sharing.start`, `sharing.stop`, `sharing.delete`, `sharing.logout`, `sharing.cancel` | Typed tunnel state and `sharing.changed`; explicit delete/logout confirmation. Established hosting survives controller disconnect. |
| Prerequisites | `prerequisites.getStatus`, `prerequisites.check`, `prerequisites.checkAll`, `prerequisites.cancel` | `prerequisites.changed`; capture requested saved/unsaved executable paths and return tested paths without changing effective runtime paths. |
| Dev Boxes | `devboxes.getCatalog`, `devboxes.refresh`, `devboxes.cancel` | Catalog metadata/entries, progress, `devboxes.changed`; bounded discovery, saved target changes use settings gate. |
| Windows App | `windowsApp.getState`, `windowsApp.map`, `windowsApp.signIn`, `windowsApp.refresh`, `windowsApp.open`, `windowsApp.openLastKnown`, `windowsApp.clear`, `windowsApp.cancel` | Per-machine mapping and operation state, `windowsApp.changed`; explicit clear confirmation, cached URI never exposed. Native focus failure is not permission to duplicate-launch. |
| Operations | `operations.cancel` | Connection-local request ID cancellation without waiting on target resource gates. |

### Conflict and cancellation contract

Acquire required resources atomically in stable order; never hold a partial
lease or enqueue conflicting operations. Return 1003. State reads are lock-free
with respect to mutation gates. Stop, cancellation and shutdown interrupt current
owners without acquiring their admission resources.

| Commands | Exclusive resources |
| --- | --- |
| Azure sign-in, discovery, fresh resolution/open, Azure diagnostics | Azure account |
| Machine edit/remove/map/clear/open | Target machine; fresh resolve also Azure |
| Settings and discovery-target persistence | Settings |
| Sharing start/stop/delete/logout and tunnel account diagnostics | Tunnel; persistence also settings |
| All prerequisite checks | Compose individual checks, serialize shared accounts |

The Windows App process-wide activation lock stays short-lived and does not span
CLI/network work. Durable commits are never implicitly reversed on cancellation.

### Parameter shapes

All object fields below use exact camelCase spelling. Reject unrecognized fields.
The host/revision pair always refers to the resource being edited, not a global
revision. Query the relevant snapshot before issuing a mutation; revisions from
different domains cannot substitute for each other.

| Method/group | Named parameters |
| --- | --- |
| Parameterless status/capabilities/shutdown/startup cancellation | `{}`; absent params or `[]` also accepted |
| `settings.update` | `{ hostInstanceId, expectedRevision, settings: { ...patch } }` |
| `machines.list`, `devboxes.getCatalog` | `{ offset?, limit?, expectedRevision? }` |
| `machines.getSessions` | `{ machineId, offset?, limit?, expectedRevision? }` |
| `machines.getNote` | `{ machineId, offset?, length?, expectedRevision? }`; text length at most 16,384, not `limit` |
| `sharing.start`, `sharing.stop`, `sharing.getStatus` | `{}` |
| `sharing.delete`, `sharing.logout` | `{ confirmed: true }` |
| `prerequisites.check` | `{ id, path? }`; ID is `azureCli`, `devCenterExtension`, `devTunnel` or `windowsApp` |
| `prerequisites.checkAll` | `{ azureCliPath?, devTunnelCliPath? }` |
| `devboxes.refresh` | `{ hostInstanceId, expectedRevision, subscriptionId?, devCenterName? }`; expected revision is settings revision; nullable subscription GUID and Dev Center name are mutually exclusive |
| `windowsApp.map` | `{ machineId, hostInstanceId, expectedRevision, selection: { devCenterEndpoint, projectName, devBoxName, azureAccountUpn, azureTenantId } }`; expected revision is machine entity revision |
| Windows App sign-in/refresh/open/openLastKnown | `{ machineId, hostInstanceId, expectedRevision }` |
| `windowsApp.clear` | Same machine mutation envelope plus `{ confirmed: true }` |
| `operations.cancel` | `{ requestId }`, preserving the target ID's JSON type/value |

The account/mapping strings above are deliberately exposed operational DTO
fields under the accepted localhost policy, not values to log. `selection`
contains validated mapping identity, never a cached `ms-cloudpc` URI. Query
`system.getCapabilities` for the complete supported method catalog.

### Executable capability parity

The table links original Dashboard behavior to its shared runtime implementation
and wire DTOs. DTO names are in `AgentSignaler.Contracts.Rpc.V1` and domain
state is wrapped in `RpcSnapshot<T>`. Ownership and cancellation are specified
above; evidence names below refer to executable tests, not static wiring checks.

| Dashboard source behavior | Shared API | RPC / DTO | Behavioral test keys |
| --- | --- | --- | --- |
| `Program.Main` ownership | `DashboardResourceLease.Acquire` | Host startup / `RpcTransportReady` | O1, O2, O3 |
| `MainWindow.InitializeCoreAsync` | `InitializeAsync`, `Status` | `system.getStatus` / `RpcSystemState` | T1, T2 |
| Startup cancellation | `CancelStartup` | `system.cancelStartup` / `RpcSystemState` | T2 |
| `MainWindow.ShutdownAsync` | `ShutdownAsync` | `system.shutdown` / `RpcShutdownResult` | T3, R1 |
| Settings dialog/save | `Settings`, `UpdateSettingsAsync` | `settings.get`, `settings.update` / `RpcSettingsState`, `RpcOperationalSettings` | T4, T5 |
| Machine refresh/details | `GetMachines`, `GetMachine`, `GetSessions` | `machines.list`, `machines.get`, `machines.getSessions` / `RpcPage<RpcMachine>`, `RpcMachine`, `RpcPage<RpcSession>`, `RpcSource` | T6, T7, T8 |
| Legacy note display | `GetNote` | `machines.getNote` / `RpcNoteChunk` | T6 |
| Edit/remove machine | `UpdateMachineAsync`, `RemoveMachineAsync` | `machines.updateDetails`, `machines.remove` / machine/resulting collection state | T6, T9, T10 |
| Receiver/connection-test presentation | `Receiver`, `ConnectionTestReceived` | `receiver.getStatus`, `receiver.connectionTestReceived` / `RpcReceiverState`, `RpcConnectionTest` | T1 |
| Sharing presentation and start/stop/delete/logout/cancel | `Sharing`, `SharingAsync`, `CancelSharing` | `sharing.*` / `RpcSharingState` | T11, R2 |
| Catalog discovery and saved target | `Catalog`, `RefreshCatalogAsync`, `CancelCatalog` | `devboxes.*` / `RpcCatalogState`, `RpcDevBox`, `RpcCatalogProgress` | T12, C1, C2 |
| Prerequisite diagnostics/unsaved paths | `Prerequisites`, `CheckPrerequisiteAsync`, `CheckAllPrerequisitesAsync`, `CancelPrerequisites` | `prerequisites.*` / `RpcPrerequisiteState`, `RpcPrerequisite` | T13, T14, T15 |
| Mapping and connection presentation | `GetWindowsAppState` | `windowsApp.getState` / `RpcWindowsAppState`, `RpcMapping` | T16, R3 |
| Map/sign-in/refresh/open/cached-open/clear | `WindowsAppAsync` | Corresponding `windowsApp.*` methods / `RpcWindowsAppState` | T9, T10, T16, T17, T18, T19 |
| Per-machine cancellation | `CancelWindowsApp` | `windowsApp.cancel` / `RpcWindowsAppState` | T10, R2 |
| Request-ID cancellation (new host adapter) | Propagated command token | `operations.cancel` / `RpcCancelResult` | R2 |
| UI refresh, expiry and recovery | `Changed`, current domain snapshots | `*.changed` / `RpcInvalidation` | T6, T20, T21 |
| Startup outcomes/problems | `Ready`, `Problem` | `system.ready`, `system.problem` / system snapshot, `RpcProblem` | T1, T5 |

Tests with prefix **T** are methods of
`AgentSignaler.Dashboard.Core.Tests.DashboardRuntimeTests`:

| Key | Executable method |
| --- | --- |
| T1 | `EntryPointOwnsLeaseAcrossRuntimeDisposalAndReceiverReallyStartsAndStops` |
| T2 | `InitializationAndCliDeadlinesUseClockAndRetainControlState` |
| T3 | `ShutdownWaitsForClientOwnedCleanupAndDoesNotReleaseEntrypointLease` |
| T4 | `SettingsPersistSavedNotOverriddenValuesAndPreserveUnknownAndVisualFields` |
| T5 | `CorruptSettingsDisableAutomaticSharingAndExposeRecoveryWhileStorageFailureStaysControllable` |
| T6 | `MachineStateAndRevisionsAreIndependentAndLegacyNotesAreChunkedWithoutRewrite` |
| T7 | `PaginationUsesCurrentRevisionAndRejectsStaleMissingAndInvalidRanges` |
| T8 | `MaximumSupportedMachinesAndSessionsRemainCompleteAndDeterministicallyPaged` |
| T9 | `MutationsRequireCurrentHostRevisionAndDestructiveConfirmation` |
| T10 | `PerMachineGateBlocksRemovalWhileFreshOpenOwnsItAndAllowsOtherMachine` |
| T11 | `SharingLifecycleUsesOwnedIdentityExplicitConfirmationsAndPreservesEstablishedServiceOnClientCancellation` |
| T12 | `CatalogProgressAndResultsPublishFromOneRuntimeAndPersistDiscoveryTarget` |
| T13 | `DiagnosticsCaptureUnsavedPathsAndNeverSwitchOperationalSettings` |
| T14 | `AzureAccountGateIsAtomicAndCancellationHoldsItUntilCleanupFinishes` |
| T15 | `DomainCancellationCancelsCheckAllIncludingItsNotYetAdmittedAzureExtension` |
| T16 | `EveryWindowsAppCommandUsesTheSharedRuntime` (six operation cases) |
| T17 | `SuccessfulSignInRemainsCommittedWhenSubsequentConnectionResolutionFails` |
| T18 | `MappingCommitSurvivesCancellationBeforeWindowsActivation` |
| T19 | `CachedActivationReportsUnknownWhenPlatformCannotConfirmItsSideEffect` |
| T20 | `ClockOnlySessionExpiryPublishesWithinPollingIntervalWithoutReports` |
| T21 | `PollFailureRetainsLastGoodStateAndRecoveryInvalidatesWithoutChangingUnrelatedDomains` |

Additional executable identifiers:

- **O1:** `ProcessOwnershipTests.IndependentProcessesContendAndForcedExitReleasesExactlyOneLease`
- **O2:** `ProcessOwnershipTests.JunctionAliasResolvesToTheSameProcessLease`
- **O3:** `ProcessOwnershipTests.AzureCliJobContainsChildAndGrandchildDuringNormalAndAbruptOwnerExit`
- **C1:** `DevBoxCatalogControllerTests.ClosureAndShutdownAwaitCleanupAndRejectConcurrentRefresh`
- **C2:** `DevBoxCatalogControllerTests.PreCancellationAndLateSuccessfulResponseNeverReplaceSnapshot`
- **R1:** `RpcProcessTests.SubprocessReadyIsSingleLineAndExplicitShutdownAcknowledgesBeforeExit`
- **R2:** `RpcTransportTests.ReservedCancellationAdmissionWorksWhileAllApplicationSlotsAreOccupied`
  and `DomainCancellationIncludesNotificationsButCannotCancelOtherMachines`
- **R3:** `RpcRuntimeTests.WindowsMappingDtoNeverExposesCachedConnectionUri`

The current execution handoff records which tests actually ran and their results.
Fake native/CLI tests prove deterministic operational behavior, not live
foreground, account, tunnel, or installed-servicing acceptance.

## Settings compatibility

Persist PascalCase fields and add `"RpcPort": 51821`; older files default this
missing field. Keep visual theme/compact preferences unchanged during headless
edits. Preserve unknown top-level persisted JSON through extension data, but do
not expose it through wire DTOs or accept unknown update fields. Serialize atomic
file writes and fail explicitly beyond 1 MiB.

Saved and effective settings are distinct. CLI overrides are never implicitly
persisted. Receiver port, connection mode, CLI paths and RPC port edits return
restart-required identifiers; restart remains explicit. Existing Dashboard files
using receiver 51821 remain valid for Dashboard; RpcHost refuses an effective
collision and explains `--rpc-port` without rewriting the settings.

Disabling detailed reception takes effect immediately for this run, including
when durable saving is rejected or fails. Unrelated settings, discovery-target
or sharing writes must not silently undo that run-only opt-out. Only an explicit,
successfully persisted enable request may clear it. Report persistence failure
and saved/effective divergence rather than claiming the saved preference changed.

Updates use `hostInstanceId` and `expectedRevision` from the latest
`settings.get` envelope and a named `settings` patch, for example:

```json
{"jsonrpc":"2.0","id":"settings-2","method":"settings.update","params":{"hostInstanceId":"00000000-0000-0000-0000-000000000001","expectedRevision":"7","settings":{"receiveDetailedConversations":false}}}
```

The IDs above are synthetic. Use the actual host/revision from the current
connection; a mismatch is a stale-revision error, not permission to overwrite.

## MSI-owned firewall and servicing

There is no `firewall.*` RPC namespace or RpcHost elevation/helper mode. The
dedicated MSI always provisions an enabled inbound TCP rule for its exact EXE,
receiver port and Private profile only, even in Internet-sharing mode. It never
includes RPC port, Public/Domain profiles, edge traversal, wildcard programs or
all ports. A firewall rule cannot make a loopback-bound listener reachable.

`RECEIVERPORT` selects an explicitly validated value, otherwise the installing
user's valid default saved port, otherwise 51820 only if settings are absent.
Malformed existing settings require an explicit property. Reject nonprivileged
range violations and known RPC collisions. Capture installing identity/paths
before elevation. MSI never persists application settings or launches RpcHost.

Provisioned port metadata is installer-owned, not proof Windows Firewall permits
traffic. `receiver.getStatus` reports effective/provisioned mismatch and advises
MSI maintenance. Custom data-directory settings do not silently alter the rule.
Standalone EXEs report manual management: arrange exact receiver EXE/port Private
access through normal administrator policy, never open the control port. In
Internet mode no LAN rule is needed for current operation.

The stable RpcHost UpgradeCode is
`{A8D1F2A9-762B-4B2C-A5A3-451463D743B2}`. The installer-owned HKCU key
`Software\AgentSignaler\Installer\RpcHost` records `UpgradeCode`, `UserSid`,
`InstallDirectory` and DWORD `ReceiverPort`. RpcHost treats it as provisioned
only when identity and exact installed executable match. Protected machine-level
ownership/transaction records belong to servicing, not to runtime settings.

MSI servicing requires explicit elevation and fails/rolls back when unavailable
or denied. Silent servicing must be appropriately elevated for the intended user.
Ownership validation protects foreign or externally edited rules; repair restores
only proven-owned state, upgrade/maintenance replaces transactionally and uninstall
removes only proven-owned rules. Cleanup failure is a servicing failure.

Stop the exact installed host before install/update/uninstall. Servicing does not
trust an unauthenticated port for identity, send shutdown, force-kill, automatically
restart or clean up cloud resources. Shared application data survives servicing.
Updater handles only installed MSI inventory, skips absent/equal/newer products
and preserves provisioned port during upgrade. `-WhatIf` must not mutate; standalone
copies are not discovered or replaced.

## Privacy and threat model

Wire DTOs explicitly allow effective machine/session status/source descriptors,
display names, notes, timestamps, GUIDs, Dev Box/project/endpoint/account/tenant
mapping identity, catalog metadata, effective report URL, saved/effective CLI
paths, validated summaries and operation state. This information is exposed to
the unauthenticated controller by decision; it must not be logged.

Never serialize operational CLR records directly. Exclude cached `ms-cloudpc`
URIs, credentials, raw CLI stdout/stderr, arbitrary exceptions, environment
dumps, enumerated window titles and message bodies from diagnostics.

| Threat | Boundary and residual risk |
| --- | --- |
| DNS rebinding | Exact Host/Origin and loopback remote checks reject foreign names; not identity. |
| Hostile localhost pages | Explicitly accepted control risk, including destructive commands and executable paths. |
| Native Origin spoofing | Any local process/user can supply Origin; no owning-user authentication. |
| Port squatting | Bind fails with fixed exit code/instructions; clients cannot authenticate a squatting server. |
| Stale/replaced clients | Host GUID and revision checks reject stale edits; reconnect refetches without replay. |
| Denial of service | Bounded parser/work/output/time budgets and single-controller lease; local peers can still monopolize admission. |
| Sensitive responses | Explicit DTO allowlist and no body logging; trusted client must protect what it receives. |
| Destructive commands | Explicit confirmations prevent mistakes, not malicious authorized-by-origin callers. |
| CLI execution | Unsaved diagnostic and saved executable paths can execute as RpcHost user; accepted local-control consequence. |
| Installer boundary | Elevated MSI-owned receiver provisioning, no runtime RPC elevation; actual servicing remains VM acceptance. |

## Validation and release acceptance

The executable client example and synthetic Chromium harness live under
`tests\AgentSignaler.RpcHost.Web.Tests`. Run pinned dependencies with `npm ci`,
`npm run typecheck`, and `npm test` in that directory after preparing the host
fixture specified by its test configuration. Native tests use the deployed Core
assembly and real isolated SQLite/Kestrel/WebSocket fixtures, not linked copies.

### JavaScript connection example

Serve this example from a synthetic localhost HTTP/HTTPS page, not `file:` or a
non-local page. A browser supplies Origin automatically. Do not put response
bodies in console logs, telemetry or HTML. This intentionally demonstrates only
transport correlation; use the
[TypeScript client](../tests/AgentSignaler.RpcHost.Web.Tests/client/rpc-client.ts)
and [domain reconciliation example](../tests/AgentSignaler.RpcHost.Web.Tests/client/state.ts)
for reconnect, invalidations and paginated views.

```javascript
const socket = new WebSocket("ws://localhost:51821/rpc");
const requestId = crypto.randomUUID();
let completed = false;
const watchdog = setTimeout(() => {
  if (!completed) socket.close(1000, "Example query timed out");
}, 10_000);

socket.addEventListener("open", () => {
  socket.send(JSON.stringify({
    jsonrpc: "2.0", id: requestId, method: "system.getStatus", params: {}
  }));
});
socket.addEventListener("message", ({ data }) => {
  const messages = JSON.parse(data);
  for (const message of Array.isArray(messages) ? messages : [messages]) {
    if (message.id !== requestId) continue;
    completed = true;
    clearTimeout(watchdog);
    // Render through textContent; do not log machine/account/path data.
    document.getElementById("status").textContent =
      message.error ? `RPC failed (${message.error.code})` : "Status received";
  }
});
socket.addEventListener("error", () => {
  document.getElementById("status").textContent = "RPC connection failed";
});
socket.addEventListener("close", () => clearTimeout(watchdog));
```

The example needs an element with ID `status`. Closing the socket does not stop
RpcHost. Use an ID-bearing `system.shutdown` call when an explicit host shutdown
is intended and wait for its acknowledgment. Never automatically retry a mutation
after connection loss; refetch affected state to determine whether it committed.

Required automated validation includes exact numerical bounds, concurrent
batches, cancellations after commit, initial degraded recovery, independent
domain races, pagination churn, localhost/receiver/LAN/tunnel separation,
process ownership/aliases, parent-exit/force-kill guarantees, EXE-only SQLite
execution, MSI table/payload/rollback fixtures and non-mutating updater fixtures.
A blocked required gate means incomplete implementation; a queued workflow is
not a published release.

By explicit user decision, RpcHost MSI ICE validation is disabled after machine
policy blocked it (`WIX1105`): build-time ICE is suppressed and the inspector no
longer invokes or requires ICE. This is not a passed validation result. MSI
identity, elevation, ownership, sequencing, decompile and full-payload checks
remain required, together with all other automated and manual acceptance checks.

| Release acceptance check | Status |
| --- | --- |
| Supported production WebView/browser shell and policies | Not run - deferred release acceptance |
| Live Azure login/permissions, real Dev Tunnel lifecycle | Not run - deferred release acceptance |
| Windows App real sign-in/session/focus success and denied foreground | Not run - deferred release acceptance |
| Dashboard tray/visual parity and second interactive Windows session | Not run - deferred release acceptance |
| Clean Windows image without .NET/Windows App SDK | Not run - deferred release acceptance |
| MSI install/upgrade/repair/uninstall/rollback on disposable VM | Not run - deferred release acceptance |
| UAC approve/deny, silent insufficient privilege, exact owned firewall lifecycle | Not run - deferred release acceptance |
| Parent-exit/termination/port-conflict repetition in actual production launcher | Not run - deferred release acceptance |

### Consequential implementation choices

- The protocol is repository-owned JSON-RPC, not reflection over CLR members.
  New operational properties must not expand the public data surface.
- The control server fixes the ASP.NET hosting environment to Production and
  retains only its owned bootstrap configuration. Inherited development settings
  must not enable diagnostic exception pages or configure additional listeners.
- Each domain/entity owns publication independently; no cross-domain barrier or
  historical snapshot cache is allowed, including as a pagination optimization.
- Client examples prefer string IDs and decimal-string revisions throughout.
- Test fixtures use isolated data and explicit disabled sharing; configuration
  checks alone do not count as real networking/publishing verification.
- Firewall provisioning belongs exclusively to the dedicated MSI. Existing
  Dashboard actions remain visual-host behavior, not shared headless elevation.
- Embedded native MSI actions implement exact rule ownership, protected journals
  and compensating rollback together. The per-user package explicitly requests
  elevated execution for mandatory firewall policy while retaining per-user
  application scope. Installer paths involving redirected LocalAppData or
  reparse points are conservatively rejected rather than trusted across elevation;
  standalone/manual deployments remain an option.
- Test-only TypeScript/browser dependencies are excluded from product payloads.
- Test runs capable of Windows network-access prompts are reserved for the final
  automated stage. Approval requires exact checkout executable/process identity;
  unverified dialogs block publication, and only proven test-owned rules may be
  removed afterward.

### Manual release matrix

Run these on disposable Windows 11 x64 environments with synthetic machines and
explicitly authorized disposable accounts/resources, not a shared development
desktop. Record EXE/MSI hash, product/OS/browser versions, source SHA and actual
outcome per row. All rows below currently have status
**Not run - deferred release acceptance**. Their deterministic synthetic
counterparts are required automated gates, not substitutes for these live checks.

| ID | Procedure | Expected outcome |
| --- | --- | --- |
| R01 | Launch the released EXE alone on a clean Windows image; use a writable isolated data/extraction directory. | One readiness JSON line; SQLite persistence and RPC work without adjacent libraries, .NET or Windows App SDK. |
| R02 | Load the example in supported localhost WebView/browser shells on HTTP and HTTPS, IPv4 and IPv6 origins. Repeat from a non-local page, `null` origin and native client without Origin. | Accepted exact localhost origins; 403 for invalid origins. No claim of owning-user authentication. |
| R03 | Connect two clients; start a cancellable action in the first and disconnect it. Retry the second during and after cleanup. | 409 while active/draining; completed cleanup before replacement admission; receiver and established sharing continue. |
| R04 | Change separate displayed domains during query responses and paginated reads; reconnect before/after restarting host. | Independent watermarks, no late older replacement, stale-revision restart, no retained global capture; fresh GUID after process restart. |
| R05 | Start Dashboard then RpcHost against one directory, then reverse order; repeat with a junction and a second Windows session. | One owner only; equivalent actionable Dashboard error/RpcHost code 3. Dashboard activation never forwards to RpcHost/across sessions. |
| R06 | With an old Dashboard running, follow coexistence guidance rather than opening the same database. Stop/upgrade it, then alternate hosts. | Explicit upgrade requirement; existing settings/database/tunnel identity/mappings retained without migration of a live database. |
| R07 | Occupy the RPC port and configure a receiver/RPC collision separately. Start with an explicit non-conflicting override, edit saved port, restart. | Stable failure/instructions before receiver/sharing startup; no silent fallback or implicit override persistence. |
| R08 | In Internet mode start a disposable tunnel; request `/rpc` through report listener and public endpoint, then test from a second LAN machine. | Control remains loopback-only on separate listener; reports retain supported protocols; no control ingress through tunnel/LAN receiver. |
| R09 | Use discovery/check-all/sign-in with disposable CLI accounts, cancel at progress stages and disconnect. | Captured tested paths, bounded cancellation, sanitized failures and no raw streams; no success reported for cancelled work. |
| R10 | Map synthetic machine to authorized Dev Box; refresh/open/reuse, deny foreground transfer and try last-known open. | Explicit native outcome, correct matching window, no duplicate launch on focus denial and no cached connection URI in RPC. |
| R11 | Edit a note/settings concurrently from stale views; confirm machine removal and mapping clear. Cancel immediately after persistence. | Stale host/revision rejected; confirmations required; committed changes not reported rolled back; removal replay-history warning preserved. |
| R12 | Close the launcher, then force-terminate a test host with a synthetic CLI child. Separately invoke explicit shutdown during a mutation. | Parent exit survival except external job policy; only owned child cleanup; shutdown reply precedes close and bounded exit releases lease. |
| R13 | Install MSI in each connection mode and inspect exact EXE/receiver port/Private rule. Deny UAC and run silent without privilege. | Mandatory receiver-only owned rule; no RPC/Public/Domain/edge allowance; denied/unavailable elevation fails and rolls back. |
| R14 | Upgrade/repair/maintain `RECEIVERPORT`/uninstall with owned, foreign and externally edited rules; inject rollback faults. | Proven ownership only, no duplicates/foreign edits, prior rule/metadata restored on failure, cleanup failure visible. |
| R15 | Try updater/service operations while exact installed host runs, then after explicitly stopping it. Test absent/equal/newer installed versions and standalone copy. | Actionable in-use failure; installed-only upgrade/no-op semantics; no port-based shutdown/force-kill/standalone replacement. |
| R16 | Exercise Dashboard settings/details/catalog/tunnel/Windows App/compact/tray startup and Exit after alternating with RpcHost. | Existing visual workflow, startup registration and firewall UI preserved through the shared operational runtime. |
