# Multi-Copilot Session Implementation Plan

## Status and agreed scope

Implemented in the repository. The behavioral contract and sequence below are
retained as the acceptance specification, not a claim of completed manual release
validation. See `ACCEPTANCE.md` and MC01-MC09 in `MANUAL-TEST-PLAN.md` for the
remaining actual-host, WinUI/DPI/accessibility, network and rollback checks.

The implementation reuses the existing session identity and sole current-user
Client. Presence v4 carries per-session status-event metadata; explicit v2/v3
projections preserve compatibility. Rollback-readable primary state plus
hash-bound enrichment replaces an incompatible primary-format rewrite. Copilots
provides metadata-only session rows and scoped transcript drill-in, and compact
tiles wrap every connected-session indicator without dropping sessions.

The dashboard must represent concurrent Copilot CLI, Visual Studio, and VS Code
sessions on each machine without one session overwriting another's state.

Confirmed product decisions:

- One Copilot row and compact indicator represents one conversation/session,
  not a CLI process, IDE window, installed integration, or reporting Client.
- "Last sent message" means the latest received, accepted status event only.
  It does not mean prompt, response, transcript text, or raw hook payload.
- Compact indicators are 4 x 4 logical pixels, wrapping into additional bottom
  rows when necessary rather than dropping sessions or replacing them with a count.
- Hook silence does not expire a session or clear its waiting state. Display
  the time of its last status event instead.

Keep one current-user Agent Signaler Client as the sole managed reporter:

`verified hook -> Relay -> current-user pipe -> Client -> HTTP(S) -> receiver -> SQLite -> Dashboard`

Do not introduce per-Copilot reporting daemons, process scanning, per-process
network heartbeats, or hooks that start the Client.

## Existing foundation and actual gaps

| Surface | Existing behavior | Required change |
| --- | --- | --- |
| `Contracts\SourceIdentity.cs` | `SessionKey` combines source kind, scope, and host session ID; product version is not identity. | Reuse this identity throughout presentation and selection. No new process ID is needed. |
| `Remote\HookPayloadAdapters.cs`, `RelayEngine.cs` | Adapters require stable host session IDs; `SessionStore` reduces sessions independently. | Exercise concurrent sources and preserve each session's latest event in snapshots. |
| `Remote\ClientCoordinator.cs` | One owner serializes hook delivery and sends full session snapshots with generation/sequence watermarks. | Preserve that ownership and delivery model; carry the new session metadata through snapshot recovery. |
| `Contracts\StateReducer.cs` | Waiting already outranks executing, but failed outranks waiting. Result overlays can also mask an input wait. | Make waiting the highest online priority, with input waits protected from result overlays. |
| `Service\MachineStore.cs` | Stores source-scoped sessions, but `LatestEvent` is machine-wide. | Persist and expose per-session latest events without borrowing the machine-wide event. |
| `Dashboard\MainWindow.cs` | Machine details has Settings and Transcript tabs; Settings contains a plain session listing. | Replace the standalone Transcript tab and plain listing with a Copilots list and session-specific transcript drill-in. |
| `Dashboard\MainWindow.cs`, `CompactWindow.cs` | Compact cards have a machine name and aggregate icon in a 64 x 64 tile. | Add a bounded bottom grid of per-session status squares. |

Paths in this table are relative to `src`.

The existing code is already multi-session capable. This is an extension and
correctness change, not a replacement of the reporting architecture.

## Behavioral contract

### Identity and lifecycle

Use `(MachineId, SourceIdentity.SessionKey(Source, SessionId))` as the session
identity. Never key by product name, display order, source scope alone, reporter
generation alone, or product version. Retain the legacy source normalization.

Only supported, verified hooks with a stable session ID create or update a row.
Missing IDs continue to produce sanitized diagnostics; do not invent a shared
"unknown session" that could merge independent conversations.

"Connected sessions" means observed, non-ended sessions while their reporting
Client is online. It is not a claim that the dashboard has checked host-process
liveness. A host without a session-end hook can remain observed indefinitely;
explain this limitation in the empty/help text and user guide.

An accepted session-end ends only that session. Ended sessions do not contribute
to aggregate status or compact squares, even if terminal metadata remains
retained. Preserve bounded tombstones and replay protection. Do not equate
ordinary waiting, completion of a turn, or an idle state with a proven host exit.

Client Exit or the existing machine heartbeat deadline makes the machine
Offline, overriding online-state aggregation. Keep last-known rows clearly
labeled offline, but do not display them as connected squares. A new reporting
generation preserves today's clean-start behavior; do not resurrect waits from
before intentional Client Exit. A network retry within the same run must
reconcile the complete session set without losing a quiet waiting session.

### Waiting priority

For an online machine, aggregate active sessions using:

`Waiting > Failed > Executing > Succeeded > Idle`

This must be a shared Contracts rule, not a dashboard-only color or label rule.
No sessions means Idle. Machine connectivity is evaluated separately.

Maintain input-wait state per session. A resume from session B cannot clear a
wait in session A, including when both use the same product and integration
scope. If several sessions are waiting, the machine stays Waiting until every
such session has resumed or ended.

Preserve the existing distinction between a pending `ask_user` operation and
ordinary execution: unrelated tool activity, including unrelated activity in
the same session, is not proof that the pending question was answered. Use the
adapter's established completion/resume signals; do not invent unsupported host
events or treat every prompt/tool event as an answer.

Update `Apply`, `Effective`, and `Aggregate` together:

- Explicit input waits, including permission requests and pending questions,
  must not be hidden by a previous success/failure overlay.
- A matching completion/resume changes only that session. Terminal lifecycle
  events use their documented per-session reducer behavior.
- Preserve ordinary result overlays and their absolute expiry when no explicit
  input wait takes priority. Turn completion is not itself a session-end.
- Neither heartbeat receipt, result expiry, elapsed hook silence, nor another
  session's failure/success clears a pending input wait.

Keep result information available in the session row even when Waiting wins
the main status. A successful turn may retain its existing temporary success
presentation when there is no pending input request.

### Latest status event

Add nullable, validated latest-event metadata to `SessionSnapshot`, using an
`AgentEvent` value and an explicitly named UTC event timestamp. Preserve
`UpdatedAtUtc` as the existing ordering field rather than changing its meaning.
The row timestamp is the accepted reporting timestamp, not a heartbeat receipt.

Update this metadata only when a hook is accepted for that identity. Carry it
in full snapshots, local state, receiver persistence, and runtime projections.
Duplicates, stale reports, heartbeats, and result expiry must not replace it.
Legacy snapshots without metadata display "Last event unavailable"; never
guess from the current state or assign the machine's latest event to every row.

Render application-defined event labels and relative/absolute timestamps.
Reuse or extract event formatting from `MachineCardPresentation.Activity`.
No raw payloads, tool arguments, prompts, responses, or transcript excerpts
are added to status storage, logging, list rows, tooltips, or RPC.

## Implementation sequence

### 1. Contracts, reducer, and compatibility

First add reducer and identity regression cases, then implement the behavioral
contract in `StateReducer.cs` and the new snapshot metadata.

Introduce a new managed-presence version for enriched snapshots. The current
strict JSON readers reject unknown properties: adding optional fields to v3
payloads is not a backward-compatible rollout. Update health/capability
selection, converters, validators, producers, endpoints, and transport tests
together. Preserve v1 status and v2/v3 presence acceptance with explicit legacy
projections; do not silently strip source identity to reach an older receiver.
If a receiver cannot support the configured source-aware reporting mode, show
an actionable compatibility error rather than merging sessions.

Do not renumber existing events or states. Reporter generation/sequence remains
machine-reporting ownership, not Copilot session identity. Existing hook IPC
already carries the event and identity, so no IPC version bump is needed solely
for metadata derived by the Client. Transcript wire identity need not change.

### 2. Client state, delivery, and receiver persistence

Update `SessionStore`, `ClientCoordinator`, `PresenceTransport`, `DashboardServer`,
and `MachineStore` so incremental hooks and full snapshots produce equivalent
per-session state and latest-event metadata.

Audit capacity behavior as part of waiting-state correctness. `SessionStore`
currently trims oldest entries to fit count/byte limits; a quiet waiting session
must not disappear when other sessions are busy. Keep the 64-session and message
byte bounds. Reclaim eligible ended/tombstone entries first, preserve replay
watermarks, and reject admissions that cannot fit without dropping a live
session. Return an explicit bounded-capacity outcome and sanitized diagnostic;
do not acknowledge and then silently discard a session. Apply equivalent
admission rules at the receiver, including direct/legacy ingestion.

Reserve enough space for required bounded metadata updates to admitted sessions,
not only their initial start events. Cover both the count limit and worst-case
serialized payload size.

Version the local persisted state as needed, and update SQLite snapshot
deserialization/migration without destroying unrelated machine settings. Missing
legacy metadata stays unknown. Define and exercise rollback before writing an
enriched persisted format: an older binary must not treat new fields as corrupt
state and silently erase the session set. Document a supported backup/restore
or explicit incompatible-format path.

No new installer resources, startup registrations, firewall rules, or integration
configuration rewrites are expected. Package compatible producer/receiver
versions and document receiver-first upgrade and rollback requirements.

### 3. Shared runtime and RPC

Use `Dashboard.Core\RuntimeContracts.cs` and
`DashboardRuntime.Machines.cs` to provide one immutable session presentation
projection for full details and compact cards. Include stable identity,
source label, effective state, latest-event metadata, and lifecycle/connectivity
presentation. Use deterministic ordering by source kind, scope, and session ID,
not state, last activity, or `Source.ToString()` including product version.

A session change must invalidate the machine/session revision even if the
aggregate machine state remains Waiting. Review existing structural equality
and revision-checked pagination so a resumed B is visible while A still waits.

Audit `RpcHost\RuntimeRpcApplication.cs` and the explicit `RpcSession` DTO.
Existing session-state results must reflect shared waiting semantics. The new
dashboard event field does not require widening RPC v1: retain its existing DTO
allowlist unless a separately versioned API addition is deliberately introduced.
Never serialize internal session models or transcript contents wholesale.
Preserve independent runtime domain revisions and loopback-only RPC policy.

### 4. Copilots tab and transcript drill-in

Replace the machine-details Transcript tab with a **Copilots** tab. Leave
Settings and its connection/configuration actions intact; remove its duplicate
plain session listing. Keep existing dialog default-selection behavior.

Each row shows product, distinguishable scope/session label, current status,
latest accepted status event, and its timestamp. Show the waiting count in the
machine summary so recent activity from a different Copilot does not imply the
machine has resumed.

Use a virtualized list and a non-UI controller/presentation model. Keep stable
selection and scroll position during refresh; update through the dispatcher and
cancel work when the tab/dialog closes or the machine disappears.

Opening a row's **View transcript** action replaces the list content inside
Copilots with that session's read-only transcript and a **Back to Copilots**
action. Reuse `TranscriptDetailsView` and `TranscriptViewController`, but pass an
explicit selection instead of auto-selecting another machine-wide session.

Join status rows with content-free `ITranscriptReader.ListSessionsAsync`
metadata by machine/source/session identity. Preserve access to ended sessions
with retained transcripts in a clearly labeled retained/ended section or filter;
they do not contribute to connected counts or status squares. A retained-only
row's last status event is unavailable unless actually known.

Keep `StreamId` in the full `TranscriptSelection`. If a session has multiple
retained streams, scope any stream selector to that session; never merge them
or silently switch to a different Copilot. Respect bounded selector pagination.

Preserve existing transcript opt-in, transport policy, memory-only retention,
read-only paging, gap/truncation markers, and immediate invalidation. Show
disabled, unavailable, expired, or no-retained-events states explicitly.
Back navigation, tab hiding, clearing, disablement, removal, and disposal must
cancel pending reads and release rendered text. The list does not load message
bodies merely to render its rows.

### 5. Compact session indicators

Add a bottom indicator region in `MachineCard.CreateCompactContent`, updated by
`MachineCard.Update` from the same active-session projection as the Copilots tab.
Retain the machine's aggregate icon/status and existing connection click action.

Each session gets a 4 x 4 WinUI logical-pixel square with its own effective-state
color, reusing the existing state palette with adequate contrast against the
machine tile. Logical pixels follow normal Windows DPI scaling.

Use deterministic row-major ordering matching the session list. With 4px
squares, 1px spacing, and a 50px indicator region, ten squares fit per row and
64 sessions require seven rows (34px high). Do not clip or omit overflow:
retain the 64px tile width and grow compact tile height only when needed.

Update `CompactWindow.Update` to sum actual tile heights rather than assuming
`ordered.Length * TileSize`. Preserve work-area bounds, vertical scrolling,
monitor/DPI handling, hover-preview placement, and hit targets. Zero connected
sessions collapses the indicator region.

Squares are indicators, not 4px buttons. Expose session labels/statuses in the
accessible card/hover summary and the full Copilots list; do not rely only on
color or tiny pointer targets.

## Acceptance and validation plan

| Area | Required scenarios |
| --- | --- |
| Identity | Concurrent CLI sessions in one scope; CLI/VS/VS Code together; identical session IDs in different scopes/products; version change without a new identity; missing-ID rejection. |
| Waiting | A waits, B resumes repeatedly: machine stays Waiting; A and B wait, only A resumes: still Waiting; last waiter resumes: remaining state wins; B fails while A waits: Waiting; pending question plus result overlay: Waiting; no silence timeout. |
| Ordering | Duplicate/stale hooks cannot change state or latest event; out-of-order heartbeat does not overwrite newer state; full-snapshot recovery preserves every admitted session and last event. |
| Lifecycle | Session-end affects only its owner; retained transcripts do not imply connected status; Client Exit and deadline show Offline; new generation does not resurrect prior waits; retry within a run preserves them. |
| Bounds | 0, 1, 10, 11, and 64 sessions; 65th admission; maximum-length source/session fields; byte-bound pressure; quiet waiter cannot be evicted by busy peers; replay after retirement. |
| Compatibility | Legacy status/presence and stored snapshots; enriched-version negotiation; strict unknown/duplicate-property rejection; source-safe incompatibility; upgrade and supported rollback. |
| Runtime/RPC | B's status/event changes while aggregate Waiting is unchanged; session revision advances; stale pagination is rejected; DTO privacy boundary remains intact. |
| Details | Copilots replaces Transcript tab; distinct event labels/times; status-only operation with transcripts disabled; correct source/session/stream drill-in; Back restores selection; retained-ended access; clear/expiry/removal cancels reads and clears text. |
| Compact | One square per active session; matching colors/stable order; complete wrapping at capacity; no clipping; empty/offline behavior; keyboard/screen-reader summaries; DPI and small-work-area layout. |

Extend existing suites rather than adding a new runner:

- Service: `StateTests`, `PresenceTests`, `SourcePresenceTests`, and affected
  transcript/store/endpoint cases.
- Remote: `RemoteTests`, `ClientRuntimeTests`, `ClientDeliveryOrderingTests`,
  `ClientIpcTests`, `SourceTransportTests`, and source-adapter/verification cases.
- Dashboard.Core/RpcHost: `DashboardRuntimeTests` and `RpcRuntimeTests`.
- Integration: `MachineCardPresentationTests`, `MachineCardAppearanceTests`,
  `TranscriptViewControllerTests`, `TranscriptDetailsSourceTests`, and
  `TranscriptEndToEndTests`; add focused session-list/indicator-layout tests
  within the existing projects.

During implementation, use the repository's Windows/x64 build and validation
skills, build affected projects before `--no-build` test runs, and start with
targeted selectors before affected-project coverage. Add manual WinUI cases to
`docs\MANUAL-TEST-PLAN.md` and update `docs\user-guide.md` and `docs\ACCEPTANCE.md`.
Do not enable live tunnel tests or run generated installers.

Deliver in dependency order: reducer/contracts, producer/receiver compatibility,
runtime projection, Copilots drill-in, then compact layout. Release only when
interleaved sessions demonstrate the agreed waiting behavior end to end.
