# Read-only hook transcript transport implementation plan

## Implementation boundary and resolved prototype scope

This is the implementation specification for an externally consented
proof-of-concept deployment, not a production telemetry design. The decisions
below supersede the earlier in-app opt-in, pairing, authentication, and strictly
inline-hook-only capture proposals.
Consent is an external deployment prerequisite: the distributing operator must
obtain informed permission for the content, destination, and retention policy
before users receive/use the prototype. The application neither collects nor
verifies that permission; installation itself is not technical evidence of it.

Updating this document does not start implementation. A subsequent instruction
to implement it authorizes repository changes, synthetic tests, builds, and
installer inspection without additional product-design questions. It does not
authorize real hook installation, real conversations as fixtures, certificate or
trust changes, live tunnel creation, installer execution, or actual-host testing.
Those are deployment/acceptance activities, not prerequisites for completing the
code against the fixed contracts and synthetic fixtures below.

## Goal and agreed scope

Extend hook reporting with enough structured conversation information
for a read-only transcript viewer in the Dashboard computer tile details view.
Preserve one-way application data flow:

```text
Verified host hook -> Relay -> current-user named pipe -> Client
    -> bounded local transcript read on stop (verified formats; assistant replies only)
    -> configured HTTPS Dev Tunnel -> DashboardServer -> bounded in-memory transcript store
    -> in-process reader -> Dashboard computer details / Transcript tab
```

Transport acknowledgements and capability negotiation are allowed; remote agent
commands, messages, approvals, terminal input, and hook control responses are not.

Agreed decisions:

- Cover Copilot CLI, Visual Studio, and VS Code, with each host gated independently
  on verified transcript support.
- Detailed reporting is **default-on, with an opt-out control**, for new
  configuration v5 and explicit migrations to v5. No in-app consent prompt,
  pairing, enrollment, bearer token, credential store, or authorization ledger
  is part of this prototype.
- Share user messages and user-facing assistant replies, including PII present
  in those allowed message fields, plus tool names and
  observed status. Exclude tool arguments, tool results, raw errors, hidden
  reasoning, attachments, and arbitrary hook payloads.
- Keep user prompts and tool metadata sourced from hooks. Add bounded,
  stop-triggered reads of the exact host transcript file for **assistant replies
  only**, using independently verified format adapters. File reads belong to
  Client, never Relay. Do not add transcript filesystem watchers, directory
  scans, general log/database readers, or SDK/session-hosting bridges.
- Keep transcript content in bounded memory only: no SQLite conversation history,
  local spool, export, or historical replay. Agent Signaler may read the host's
  existing file as an input; it must not create, modify, copy, or persist that
  transcript or its own extracted conversation history.
- Use the existing HTTPS Dev Tunnel and normal certificate validation. There is
  no application-level sender authentication. Direct HTTPS hosting, certificate
  provisioning, and browser/network-facing transcript reads are deferred.
  Ordinary HTTP/LAN and legacy configurations remain status-only.
- Build contracts, capture, delivery, receiver storage, a testable read API, and
  a WinUI transcript viewer in the computer tile details dialog.
- Make computer details a tabbed view: **Settings** preserves the entire current
  details view; **Transcript** displays the selected computer's available sessions
  and read-only transcript. Settings is the initial tab to preserve existing entry
  behavior. Keep the opt-out setting, capability display, and receiver stop/clear
  controls in scope. A browser/network-facing read API remains later work.
- Use a separately versioned transcript stream alongside unchanged status
  reports. Agreed limits are 32 KiB per serialized event, a 4 MiB Client queue,
  a 64 MiB receiver buffer, and 30-minute receiver retention.

## Capture feasibility and verified-format fallback

Main-assistant replies are not established as inline hook fields, but a stop hook
can supply a local transcript path. This plan now permits bounded file reading
to extract replies for verified formats. That removes the strictly inline-hook
limitation without promising that every host exposes a usable file or stable
schema. It does not authorize inspecting real user transcripts during development.

| Host | Documented usable hook content | Missing or unverified capability | Planned status |
| --- | --- | --- | --- |
| Copilot CLI | `userPromptSubmitted.prompt`, session identity, tool names/lifecycle; `agentStop.transcriptPath` | Main-assistant text is not inline. File format, role/completion fields, session binding, and append behavior need a verified adapter profile. | Read assistant replies on stop only when its exact format/path profile is verified; otherwise retain prompt/activity capture |
| VS Code | `UserPromptSubmit.prompt`, optional `session_id` and `transcript_path`, tool names/IDs, `Stop` | The transcript format is explicitly not a stable hook API. Optional identity/path and host version changes can make reading unavailable. | Same bounded reader for a separately verified profile, never assumed CLI compatibility; otherwise partial capture |
| Visual Studio | Existing repository adapter uses a CLI-shaped hook contract | Actual IDE-originated transcript reference and format are unverified. Bundled CLI/shared SDK infrastructure proves neither. | Reader remains unavailable until independent evidence exists; preserve any supported status/hook capture |

CLI `subagentStop.response` is a subagent reply, not a replacement for the main
assistant's user-facing answer. Do not substitute tool output, reasoning,
summaries, or inferred text for missing assistant messages.

Expose separate capabilities for hook prompts/activity and stop-triggered
assistant-file extraction. The viewer identifies the capture source and displays
partial coverage when a format/path is unsupported, the first read establishes a
baseline, or data is missing/truncated. Even with a reader, this is a bounded
best-effort view of observed activity, not a complete transcript archive.

P0 verifies each profile's schema and path mapping from authoritative published
implementation/schema evidence plus synthetic fixtures. Record exact host/format
versions, framing/encoding, session identity, message/completion fields, expected
file locations, and format-reset behavior. Do not label invented JSON fixtures
as host verification. Actual installed-host acceptance remains separate. If such
evidence is unavailable, implement the reader framework and fixtures but keep that
production adapter disabled with an explicit reason; do not guess aliases, scan
private storage, or block the remaining unattended work.

## Current codebase findings

Paths and line ranges below describe the worktree inspected during planning;
future edits may move line numbers.

| Boundary | Current implementation | Consequence |
| --- | --- | --- |
| Wire contracts | `src\AgentSignaler.Contracts\Protocol.cs:13-63` defines status v1 with identity, event, UTC time, and two tool booleans; 32,768-byte bodies, 64 sessions, strict unknown-field handling | Do not append transcript fields to old wire versions |
| Managed presence | `src\AgentSignaler.Contracts\PresenceProtocol.cs:9-44,93-159` supports presence v2 and source-aware v3 with generation/sequence ordering and bounded snapshots | Status snapshots are not conversation history |
| Source identity | `src\AgentSignaler.Contracts\SourceIdentity.cs:8-32` keys sessions by source kind, scope, and host session ID; version is metadata | Reuse this identity, additionally scoped by reported machine and transcript stream; these IDs are not authentication |
| CLI extraction | `src\AgentSignaler.Remote\RelayEngine.cs:8-34` reduces raw input to `HookData`: session, time, tool booleans, optional source/invocation ID | User text, assistant text, and tool names are intentionally absent |
| IDE extraction | `src\AgentSignaler.Remote\HookPayloadAdapters.cs:10-105` explicitly maps host events and extracts a bounded VS Code tool ID | Tool ID is not a message ID or proof of duplicate callbacks |
| Hook process | `src\AgentSignaler.Remote\RelayEngine.cs:192-272` bounds stdin to 65,536 bytes and execution to 2.2 seconds; hook mode forwards only to IPC and returns success on expected failures | Keep observation non-blocking, with no HTTP from Relay |
| Hook registration | `src\AgentSignaler.Remote\HookAdapters.cs:47-89` emits explicit event sets with three-second timeouts; Visual Studio currently shares the CLI-shaped generator | Add only independently supported hook events; never enumerate new wire events into every host |
| IPC | `src\AgentSignaler.Remote\ClientIpc.cs:17-77,90-135` uses version 2, a 4,096-byte message cap, current-user pipes, four listeners, and deadlines | Current IPC is smaller than the network budget and must be versioned deliberately |
| Client projection | `src\AgentSignaler.Remote\ClientCoordinator.cs:134-161` validates hooks, mutates state, and creates status requests; it does not forward `InvocationId` | Carry transcript-only metadata through a separate projection without persisting it in state |
| Delivery/lifecycle | `src\AgentSignaler.Remote\ClientCoordinator.cs:10-39,239-279,407-518` bounds pending hooks to 64 and clears them on reconnect/reload/stop | Transcript reliability cannot reuse snapshot reconciliation or claim lossless delivery |
| Client process | `src\AgentSignaler.Client\Program.cs:70-99,109-135` owns IPC, network-change notifications, and coordinated tray shutdown | Client remains the only managed reporter; Exit must stop transcript reporting too |
| HTTP transport | `src\AgentSignaler.Remote\PresenceTransport.cs:17-36` posts presence; `RemoteHttpTransport.cs:5-18` disables redirects/cookies/default credentials | Reuse HTTPS protections and add separate bounded transcript delivery without credentials |
| Receiver | `src\AgentSignaler.Service\DashboardServer.cs:25-66,74-121` exposes anonymous status ingest with bounded reads; Internet mode binds loopback | Add independently bounded prototype ingest; retain loopback binding and explicitly document unauthenticated senders |
| Storage | `src\AgentSignaler.Service\MachineStore.cs:19-69,141-199` stores machine snapshots and replay state in SQLite | Keep transcript text completely outside `MachineStore`, receipts, and state snapshots |
| Presentation | `src\AgentSignaler.Service\MachineView.cs:5-19` and `src\AgentSignaler.Dashboard\MainWindow.cs:465-499` expose/poll status state; `MainWindow.cs:565-924` builds the current details dialog and owns its save/remove/connection lifecycle | Preserve existing controls as Settings; add a Transcript tab using a separate volatile read model and controller |
| Configuration | `src\AgentSignaler.Remote\RemoteConfiguration.cs:73-108,126-187` supports strict versions 1-4; `MultiTargetIntegrationManager.cs:22-64,81-108` previews owned changes | Upgrade the shared configuration to v5 and preserve explicit opt-out during repair/recovery |
| Verification | `src\AgentSignaler.Remote\IntegrationTarget.cs:12-50` and `HookVerification.cs:11-34,115-157` track event capability and sanitized probe evidence | Existing status verification is not transcript verification |
| File-reader gap | `src\AgentSignaler.Remote\HookPayloadAdapters.cs:72-105` and `RelayEngine.cs:240-264` currently forward sanitized hook metadata only, with no transcript reference or reader | Add a local-only stop reference and Client-owned reader; do not put paths into existing status or network contracts |

Existing privacy policy explicitly excludes conversation content
(`README.md:112-118`, `SECURITY.md:25-35`). The owner approved changing that
documented policy for this prototype: externally consented, default-on transient
transmission/display of allowed message text, with opt-out. Update these documents
during implementation; do not claim that the application verifies external
consent or provides production-grade confidentiality/authenticity. Content in
diagnostics and persistent application storage remains prohibited.

## Proposed architecture

### 1. Separate contracts and capability gates

Add proposed `TranscriptProtocol.cs` and transcript read-model contracts in
`AgentSignaler.Contracts`. Keep status v1 and presence v2/v3 unchanged.
Use transcript protocol v1 and implement these exact new routes:

| Route | Method and behavior |
| --- | --- |
| `/api/transcripts/v1/capabilities` | POST; content-free protocol, receiver epoch, enabled state, limits, and implemented field capabilities |
| `/api/transcripts/v1/streams/open` | POST; idempotently open/replace one source stream using an expected receiver-wide open revision |
| `/api/transcripts/v1/events` | POST; one typed event or coalesced gap per request |
| `/api/transcripts/v1/streams/close` | POST; close an identified stream and optionally purge its retained content |

Control request/response bodies are at most 4 KiB; event bodies are at most
32 KiB including metadata. These are ingest/control endpoints, never a remote
conversation-read or agent-command API. No authentication endpoint is added.
Return 200 for successful control operations, 202 for accepted/duplicate events,
400 for invalid schemas, 409 for epoch/stream/order conflicts, 413 for size
violations, 415 for content type, 429 with bounded Retry-After for capacity/rate,
and 503 when transcript reception is disabled or unavailable. Error bodies
contain only application-defined categories, never received values.

Advertise exact capabilities: user messages, main-assistant complete messages,
tool names, tool correlation, and particular lifecycle signals. Do not advertise
delta streaming, final-turn semantics, or failure detection without evidence.
Initial delivery uses complete available messages; token streaming is out of scope.
Add `CaptureOrigin` (`hook` or `transcriptFile`), bounded format-profile ID, and
optional triggering capture ID to transcript event provenance. These identify
the extraction method, not a file location. Assistant reads can finish after
the stop marker or a later prompt arrives; retain delivery order and available
native turn/message IDs rather than fabricate chronological/causal ordering.

Add typed, event-specific records rather than a generic JSON/property bag:

| Data | Required semantics |
| --- | --- |
| Envelope | Protocol/schema version, delivery event ID, reported machine ID, Client generation, receiver epoch, independent transcript stream ID and monotonically increasing sequence |
| Session | Source descriptor and opaque host session ID; optional turn/message IDs only when genuinely supplied |
| Provenance | Host event name, adapter/capability version, observed UTC time, Client accepted UTC time, and whether ordering is host-provided or arrival-based |
| User/assistant message | Explicit role, message ID with host/local provenance, plain text, completion/availability state, truncation indicator and category |
| Tool activity | Validated tool name, host invocation ID when supplied, observed phase, and bounded normalized outcome such as requested/completed/failed/unknown |
| Lifecycle | Explicit session/turn signals only when supported; `Stop` is not universally session end or successful completion |
| Gaps | Missing sequence interval where known, otherwise an explicit unknown-range discontinuity with an application-defined reason |

Use a Client-assigned transcript sequence, not timestamps, as delivery order.
Do not fabricate causal ordering across concurrent hooks or pair tools by name.
Locally assigned message IDs identify deliveries, not host-level duplicate
callbacks. Do not extend legacy status/IPC fields just to add correlation.
Status and transcript projections share source/session context, but explicit
cross-projection callback correlation is unavailable in this version.

Validate every boundary: required fields, exact event discriminators, UTC
timestamps, bounded strings/collections, duplicate JSON properties, unknown
fields, illegal combinations, enabled source eligibility, and serialized byte length.
Keep enum compatibility explicit. No raw payload fallback is allowed.

### 2. Default-on prototype, external consent, and opt-out

Add `DetailedReportingEnabled` to shared `RemoteConfiguration` v5, defaulting to
`true` when omitted in a valid v5 document. Versions 1-4 have no such field and
retain status-only behavior. New Configurator saves and explicit upgrades produce
v5; simply running a new binary must not silently rewrite an old configuration.
There is no per-source consent workflow or content-category chooser. Use the
fixed field allowlist and existing selected/eligible integrations.

The Configurator control is labeled **Share detailed conversations** and defaults
on for v5. Explain beside it that message text, including personal information,
is sent to the configured Dashboard, and that consent is handled outside the app.
This notice is informational, not an additional acceptance checkbox/dialog.
Do not add PII classification/redaction of allowed user/assistant text. Do not
separately collect credentials, environment/account records, source files,
tool bodies, raw errors, or reasoning. The only file-path exception is a bounded
local-only transcript reference needed by the reader; never send it over HTTP
or include it in persisted settings, logs, diagnostics, or viewer models. Keep diagnostics
category-only and persist neither messages nor their content-derived hashes.

Default-on does not mean auto-start: Relay never starts Client; installing the
application never starts capture on its own. Existing explicit configuration,
hook ownership, Client startup, and tray Exit rules still apply. Capture requires
v5 with the flag enabled, an existing eligible integration, a running Client, a
compatible receiver, and the configured HTTPS transport. Missing prerequisites
produce a visible unavailable reason; they must not broaden collection or start
new processes. Newly enabled sources inherit the machine-wide flag.

Provide one receiver-side **Receive detailed conversations** setting, default
true when omitted, independent of the Client flag. Changing it to false purges
receiver content, closes streams, invalidates views, and rejects further detail
without stopping status. Store only this non-content preference in existing
Dashboard settings. The computer Settings tab includes **Clear transcript**,
which purges retained text locally without changing future reception.

When Client opt-out is applied, stop admission and cancel file reads and content sends before
awaiting status reload/network work. Increment a local in-memory settings revision,
discard queued text, read requests, file references/cursors, and invalidate
outstanding IPC negotiations. Notify the
receiver with one bounded best-effort close/purge request if the endpoint is still
available. This is not a delivery guarantee: an unreachable receiver retains
existing text only until its TTL or a local clear/disable. Re-enable starts fresh
without backfill. The UI reports last-observed remote state, not a claim that an
offline machine has acknowledged opt-out.

Apply changes transactionally through existing owned configuration paths. If an
attempt to save opt-out fails, keep this Client run suspended and display that the
preference was not saved; do not show success or resume sending automatically.
Preserve an explicit saved false through repair, migration, and recovery. If a
rollback would replace a successfully saved false with an older true, preserve
false and report a recovery conflict rather than silently re-enabling sharing.
No separate permission ledger or credential store is introduced.

### 2a. Fixed prototype network boundary

Use only the existing Dev Tunnel deployment for detailed network reporting.
Client sends to the canonical HTTPS `BaseUri` already configured for status,
with ordinary certificate/hostname validation, redirects/cookies disabled, and
no default or application credentials. It must never send detailed content to a
different endpoint on retry or carry pending content across an endpoint change.
HTTP configurations continue status-only, showing "HTTPS required for details".

Dashboard accepts transcript routes only in Internet listener mode on the
existing loopback listener. Production composition enables them only while its
owned tunnel is running for that listener; stopping/changing the tunnel disables
ingest and purges volatile state. Synthetic tests inject that readiness signal.
Do not add forwarded-header trust, public read routes, direct TLS listeners,
certificate installation, or automatic cloud-resource changes.

HTTPS authenticates the public endpoint, not each reporting Client or the
Dashboard process behind the tunnel. The prototype deliberately trusts the
configured destination, Windows certificate trust, tunnel service/operator, and
externally controlled distribution. Loopback is not proof that an individual
request arrived through the tunnel. Anonymous senders can spoof machine/source
IDs, corrupt the demonstration, request purges, and consume capacity. Document
these limitations prominently; the UI must not label identities authenticated.
No legal or technical verification of external consent is claimed.

### 3. Hook capture and versioned IPC

Extend host adapters with a separate allowlisted transcript projection.
Continue producing status independently when content is disabled, unsupported,
oversized, or rejected. Check the current enabled state before extracting/retaining conversation
fields; raw stdin stays bounded and transient.

Preserve the 64 KiB stdin cap, 2.2-second Relay budget, and three-second configured
hook timeout. Hook observers return success without stdout decisions, context
injection, approvals, or execution changes. No network retries happen in Relay,
and a hook must never start Client.

Upgrade the **shared named-pipe protocol to v3**, not a second pipe. New Client
accepts exact existing v2 status/control requests and returns exact v2 responses.
Keep those commands at 4 KiB. V3 adds `transcript-negotiate` (content-free, 4 KiB),
`transcript-hook` (32 KiB), and `transcript-reload` (content-free, 4 KiB).
Keep status wire requests and their `HookData` shape unchanged.

Also add v3 `transcript-read` (4 KiB): a stop observation with a local-only
`LocalTranscriptReference` containing source/session identity, stop timestamp,
capture ID, enabled-state revision, and the supplied path. A path is at most 512
characters and the whole serialized envelope must fit 4 KiB; reject oversize
references, never truncate paths. Generate references only from a verified
adapter's exact `agentStop.transcriptPath` or `Stop` common `transcript_path`
field. No guessed paths, alias matching, or subagent-stop substitution.
This command replaces the v3 stop observation that would otherwise be sent by
`transcript-hook`; it does not duplicate it. Missing/unusable references still
allow the ordinary stop observation and partial-capture availability.

The command queues bounded Client work and returns promptly; it never waits for
file I/O or HTTP. The status projection is still sent first, and the existing
200 ms transcript IPC/2.2-second Relay budgets cover this command too. Client
creates network provenance from sanitized metadata, not by serializing the local
request. Keep local-reference types in Remote, outside wire contracts and
`HookData`/`SessionStore`; strict network serializers reject path fields.

The framing reader has a hard 32 KiB ceiling, then strictly rejects v2/control
frames exceeding 4 KiB and invalid/duplicate version or command fields before
constructing content models. Retain four current-user listeners and existing
read/work deadlines; allow at most two concurrent transcript content operations
so status/control work retains capacity.

Relay sends the legacy status projection first. It attempts content-free v3
negotiation only with time left in the existing 2.2-second budget, allowing at
most 200 ms for the complete transcript attempt and 50 ms to connect. An old
Client returns incompatible without ever receiving conversation text.
Negotiation returns the source capability and enabled-state revision; Client
rechecks both on `transcript-hook`. No retry occurs in Relay. Reserve 2 KiB of the
32 KiB envelope for Client-added wire metadata and truncate the permitted text
to actual serialized size at a Unicode-safe boundary. Re-measure after projection.
Missing legacy callback correlation remains explicitly unavailable.

Check the current flag, enabled source, current verification, and message validity again
in Client. IPC acceptance means accepted into volatile memory, not delivered or
durably stored. A same-user pipe establishes user scope, not a security boundary
against malicious processes already running as that user.

### 3a. Bounded stop-triggered assistant reader

Add `StopTriggeredTranscriptReader` and `ITranscriptFileAdapter` in
`AgentSignaler.Remote`, owned by the existing Client lifecycle. Only an accepted,
eligible stop reference schedules a pass. Do not use `FileSystemWatcher`, file
polling, a directory scanner, another process, or continuous token streaming.
UI polling remains unrelated to file access. Neither opening the Transcript tab
nor receiving a network request can cause Client to open a file.

**Profiles and extraction.** Select an adapter by verified host/format version
and source scope, not by guessing the contents of arbitrary files. Initial
profiles must support bounded, independently framed append records and explicit
completed user-facing assistant messages. Do not assume a `.jsonl` extension
proves such a format. Snapshot-only/rewritten, unknown, or changed formats are
unsupported until a separate verified adapter exists; do not fall back to
whole-file deserialization. A host-version mismatch disables its reader but
does not suppress supported hook prompts, tool activity, or status.

Parse only structural/session metadata needed to validate the stream and
completed top-level assistant text. Discard file-sourced user messages so the
same prompt is never emitted both from a hook and from the file. Exclude tool
inputs/results, reasoning, system/developer messages, attachments, raw errors,
subagent replies, and arbitrary metadata. Do not concatenate every text-bearing
field or treat streaming deltas as completed messages. Emit only the final
user-facing text form established by that adapter's evidence.

**File boundary.** Treat the hook-supplied path as untrusted local input.
Validate it again in Client against the selected profile's exact current-user
host transcript root, expected file naming/session mapping, and stable source
identity. Roots come from the verified installed integration/profile, never a
network request, file contents, or a broad "anything under AppData" rule.
Require a regular local file; reject relative/UNC/device paths, alternate data
streams, reparse-point traversal, and identity/session mismatches. If the profile
cannot establish this binding, keep file capture unavailable.

Open read-only with sharing compatible with a writing host (`ReadWrite | Delete`),
then validate final resolved location, current-user ownership, and file identity
on the opened handle before reading. Keep path/identity checks tied to that
handle to avoid check/open races. Never create, repair, rename, copy, modify,
or lock the host's transcript against normal writing. Close the handle after
each pass; no handles are retained while waiting for another hook.

**Baseline and no history replay.** Maintain an in-memory cursor keyed by
source/session, file identity, format profile, and capture/settings revision.
On the first stop for an unbaselined file, emit the current reply only if the
verified profile can tie a complete record to the exact native turn/prompt ID
observed during this enabled Client run, within the read budget. Otherwise take
the current EOF as the baseline, emit no pre-existing replies, and show
"Capture started here; earlier replies unavailable". This deliberately may
omit the first observed reply; never label that baseline a complete transcript.
If EOF is inside a frame, discard that pre-baseline frame when it later completes.

Subsequent stops read only appended records after the last completed boundary.
Keep only offsets and bounded identity metadata between passes, not file buffers
or historical message bodies. An incomplete final record remains pending at its
start offset and is re-read only by the bounded retry or a later stop. Count
re-read bytes against the same limits. Commit a completed record's cursor together
with local queue admission or an explicit dropped-content/gap outcome. A failed
HTTP send uses the existing in-memory delivery queue, never a file re-read.

Repeated stop callbacks with unchanged data emit no duplicate replies.
Use file identity/record position and, where verified, native message identity
for extraction deduplication; do not compare prompt text or confuse operation IDs
with message IDs. A profile with ambiguous completion/rewrite semantics is
unsupported, not "best effort" text concatenation.

Deletion, replacement, truncation, encoding/schema change, loss of session
binding, or cursor expiry produces a categorized discontinuity. Rebaseline
without replaying old records; do not carry offsets across file identity changes.
Opt-out, endpoint/source removal, receiver clear/reset, and Client restart discard
reader contexts and pending reads. Resume establishes a new baseline and never
reconstructs dropped history from the host file.

**Scheduling and failure.** Use one active read globally, a FIFO queue of at most
16 session requests, and at most one queued successor per active session.
Coalesce repeated pending stops for the same session into its latest trigger;
the retained cursor still covers intervening appended records. Maximum 32 reader
contexts globally and eight per source, expiring after 30 minutes without a
stop. Reject excess work with an explicit capacity/gap reason instead of blocking
IPC. A pending request expires two seconds after its latest coalesced stop;
discard expired work with a gap reason. Reclaiming a context requires a fresh
baseline next time.

Each stop permits at most 2 MiB total file bytes inspected, 128 records, 16
emitted replies/256 KiB normalized output, and 750 ms from worker admission including all
retries. Read in at most 16 KiB chunks, with a 64 KiB encoded-record limit and
JSON depth 16 for JSON profiles. File size is capped at 64 MiB; oversized files
remain unsupported for this prototype even when a suffix might be usable.
Use at most three attempts, scheduled at 0, 100, and 300 ms from worker admission,
only for sharing/transient availability or an incomplete trailing write.
These attempts do not reset byte/time budgets and are not ongoing polling.
Do not schedule another pass merely because the file might grow later.

On budget exhaustion, report the reason/gap and rebaseline at that pass's
observed EOF, excluding a partial pre-baseline frame; do not process a backlog
indefinitely or scan to find a guessed message boundary. Invalid encoding,
schema/path/identity mismatch, and permanent access failures do not retry.
Missing or delayed replies leave hook prompt/activity display intact.

File work must not hold the status mutation/delivery lock or run on the UI
thread. Recheck enabled/settings revision and receiver readiness before opening
and before queueing replies. Opt-out and Exit cancel active I/O/retry waits,
drop scheduled work, and release contexts within the existing shutdown budget.
Surface only application-defined file-reader categories, never exception text,
file paths, raw records, or excerpts in diagnostics.

### 4. Independent bounded delivery inside Client

Add proposed `TranscriptDeliveryCoordinator` and `TranscriptTransport` components
owned and cancelled by the existing `ClientCoordinator`. They are not a second
daemon, hook-side reporter, or independent process lifetime.

Status heartbeats, reconciliation, and terminal Offline retain their existing
semantics and resource reservation. Transcript events are never embedded into
heartbeat snapshots or passed to `SessionStore.UpdateReport`.

Bound the queue by bytes and entry count, including metadata and in-flight
ownership. Enforce the agreed 4 MiB budget. Use bounded retries only while the
Client is alive and detailed reporting remains enabled; honor backoff and cancellation.
Retain event IDs and sequences across retries. Never retry forever, spool to
disk, or let transcript traffic starve reporting shutdown.

On overflow or expiry, evict the oldest pending content and reserve capacity for
a bounded gap notice. Distinguish known sequence loss from events never accepted
by IPC. A disconnect can therefore yield an incomplete transcript, not a replay
promise. Never silently clear transcript records as status reconnect currently
clears `_hooks`.

Use one transcript stream per machine/source scope/Client generation, spanning
that source's sessions. Assign monotonically increasing sequences under the Client
lock; use random opaque delivery IDs and stream IDs. Keep one outbound transcript
request in flight globally, independently bounded from status delivery.
Start only after managed Started is acknowledged and content-free transcript
negotiation succeeds. Cache receiver capability for 60 seconds; stop new capture
on an explicit incompatible/disabled response, and suspend it when that cache
expires without successful refresh.

Retries preserve sequence, event identity, and original acceptance time. Open
uses an expected receiver-wide monotonic open revision and an idempotency ID.
Return that revision in content-free negotiation and advance it on every open,
replacement, retirement, and reclamation. It survives individual stream-slot
reclamation and resets only with a new receiver epoch. A mismatched open revision
returns a conflict; Client renegotiates and retries within its existing budget.
An exact committed open retry is acknowledged while its bounded current-stream
receipt remains available; otherwise it is stale, never accepted as a new open.
This prevents delayed packets from reopening forgotten streams without keeping
unbounded tombstones. Receiver accepts the next sequence, acknowledges
an exact retry of the latest event as duplicate, and rejects conflicting reuse.
Older sequence numbers never append. Keep only high-water marks and the latest
receipt fingerprint in memory, not an ever-growing event-ID set.

When dropping a pending prefix, coalesce it into one gap range per stream.
Receiver advances only through the unreceived suffix of that gap, so an event
whose acknowledgement was lost is not erased. Rejected/unobserved IPC callbacks
have no assigned sequence: report unknown-range loss only when observed; always
label capture as incomplete before stream start. Do not promise detection of a
hook that never reached Client.

### 5. Receiver and memory-only read API

Add a dedicated ingest handler and proposed `TranscriptStore` in
`AgentSignaler.Service`. Keep the existing 32 KiB network ceiling, bounded body
reads, cancellation, and Internet-mode loopback binding. Add transcript-specific
rate/concurrency and per-machine limits in addition to existing global controls.

Check receiver readiness and validate before accepting data. Return a bounded acknowledgement
containing receiver epoch, stream identity, acknowledged sequence, and explicit
accepted/duplicate/rejected disposition. Acceptance means **volatile receipt**,
not SQLite commit. Retries must not append duplicate events.

Enforce 64 MiB globally across transcript buffers and associated indexes; add
bounded per-machine/session quotas so one sender cannot consume the entire
receiver. Expire content at 30 minutes from receiver receipt even if a session
stays active. Eviction must update the read model's retained range and gap state.
Bound deduplication, stream, and gap metadata as well as message text.

A receiver restart creates a new epoch and empty history. Client reconciliation
must announce the discontinuity and start a new stream without resending already
acknowledged content. Only still-pending, permitted in-memory data may be retried.
Client restart likewise starts a new stream and does not reconstruct history.
Reject old-epoch requests before mutation. Reassign only still-pending events
to the new stream, preserving delivery IDs and original retry expiry; include a
restart gap. Require the machine's managed presence to exist before stream open;
otherwise return a categorized conflict for Client to retry after Started.
This ordering check is not authentication.

Expose an in-process, cancellation-aware reader interface that the Transcript tab
uses to list sessions and read bounded pages after an opaque cursor. Return
receiver epoch, retained sequence range, continuation cursor, capabilities, and
availability/gap/truncation information. Separate the append/change counter from
a selection-scoped destructive-invalidation generation. Appending new events
does not invalidate a cursor or an older-page read. Clear, removal, and reset
invalidate affected selections; expiry/eviction reset a cursor only when its
retained position is no longer available. Return an explicit reset reason and
the currently retained range, never silently jump to a different session.
Do not return unbounded enumerations or make readers hold the ingest lock.

No network-facing GET, WebSocket, browser UI, export, or public transcript
discovery endpoint is introduced. A later remote-reader API will require its own
reader authentication/authorization and exposure review. The Dashboard is already
the receiver remote from development machines.

Never place text in `MachineView`, SQLite snapshots, receipts, config, diagnostic
probes, crash attachments, or integration backups. Prevent application-generated
content dumps. Explain that memory-only is an application storage policy, not a
guarantee against OS paging, hibernation, external crash capture, or the agent
host's own history. Host transcript files are read-only inputs, not an Agent
Signaler archive; "Agent Signaler does not persist conversations" must not be
presented as "conversation data never exists on disk".

### 6. Tabbed computer details and transcript viewer

Update `src\AgentSignaler.Dashboard\MainWindow.cs` around `ShowDetailsAsync` rather
than introducing a separate transcript window or changing computer tile actions.
Use a standard WinUI `TabView` with fixed, non-closable **Settings** and
**Transcript** tabs, no add/reorder actions, and existing application resources.
Reuse the neighboring Configurator tab-control conventions without changing its
UI. Preserve the existing single-dialog and navigation guards.

The **Settings** tab contains the complete existing details content: display
name, note, machine/reporting status, sessions, and all Dev Box/Windows App
mapping, sign-in, refresh, launch, copy-URI, and cancellation controls.
Keep Save details and Remove available only on Settings, with existing
validation, confirmation, busy-state, and persistence semantics. Close remains
available on both tabs. Tab switching must neither save nor discard draft edits,
trigger connection work, nor rebuild the Settings controls. Preserve the current
connection-message entry path and focus behavior by initially selecting Settings.
Put ongoing connection progress/cancellation in a shared footer accessible from
both tabs. Hide Save details and Remove on Transcript and also guard their
handlers; existing busy-state updates must not re-enable hidden actions.

The **Transcript** tab:

- Uses the selected machine's reported identity plus source/scope/session
  and stream identity. Offer a bounded session/source selector; never merge
  identically named sessions from different products or machines.
- Lists sessions with retained transcript data as well as applicable current
  capability/status information. Do not derive the list solely from active
  `MachineView.Sessions`: ended sessions may still have unexpired content.
- Displays user and available assistant messages in delivery order with role,
  source/session context, timestamps, tool names/observed outcomes, and lifecycle
  markers. Surface local arrival order when host correlation is unavailable.
- Clearly distinguishes sharing disabled, HTTPS/compatible receiver required, unsupported or partial
  host capability, no events received, offline, loading, read failure, expired
  history, gaps, truncated text, and receiver restart. An absent assistant reply
  is not an empty successful message or evidence that the agent has finished.
  Include reader-specific states: format unverified/changed, file unavailable,
  read budget exceeded, baseline established, and waiting for the next stop.
  Show assistant provenance as "Transcript file, read after stop" without a path.
- Is strictly read-only: no composer, Send, approval, retry-agent-action, tool
  execution, export, or transcript clipboard action. Opening the tab never enables
  sharing, starts Client, or triggers host transcript-file reads.
- Renders text as inert, wrapped plain text using standard controls. Do not
  interpret HTML, execute Markdown actions, navigate links, load remote images,
  or attach tool bodies/hidden reasoning to view models or accessibility labels.
  Disable transcript text selection and copy context menus; this does not change
  the existing Settings copy-URI workflow.

Implement testable selection, paging, availability, and refresh behavior in a
proposed `TranscriptViewController` and presentation records outside UI event
handlers. Read the in-process API, not a new HTTP reader endpoint. Use a virtualized
list and a bounded visible page/window, with bounded access to older retained
entries rather than accumulating every page. Preserve the scroll position while
reading older content; follow new content only when already at the latest entry,
otherwise show an explicit new-activity indicator.

Allow one viewer and at most one active read. Poll every one second only while
Transcript is visible; coalesce refreshes and marshal changes through the WinUI dispatcher.
Cancel outstanding reads on tab/session/machine changes and dialog close, and
reject stale completions using the full selection and receiver epoch. Returning
to Settings releases transcript text references; re-entering Transcript reads
only currently retained data. Keep Settings edits intact.

Account for view models, reader copies, rendered text, and outstanding pages
within the agreed receiver memory budget, not as an unbounded extra cache.
TTL expiry, eviction, receiver reset, local clear/disable, and machine removal must also
invalidate visible content; merely cancelling the next poll is insufficient.
Machine removal must purge that computer's volatile transcript state and close
its viewer reads without changing existing status re-registration semantics.
Retire its current streams on removal, but preserve existing status
re-registration semantics. Local clear retires affected streams; subsequent
capture opens a new stream containing only new data, never queued old content.
Use an immediate invalidation notification plus a viewer expiry timer; reject
late dispatcher updates against selection, epoch, invalidation generation, and
the current retained range, not the append counter.
Dialog close, application Exit, and listener teardown must detach notifications,
cancel reads, and release text without continuations touching disposed controls.

Provide keyboard tab navigation, accessible role/time/source labels, visible
focus, high-contrast support, and readable wrapped content at supported DPI and
window sizes. Give each tab a bounded layout/scroll region; avoid a surrounding
ScrollViewer that defeats transcript-list virtualization. Preserve unpackaged
WinUI x64 behavior and generated resource/publishing requirements.

### 7. Fixed resource, retry, and viewer defaults

These are implementation decisions, not questions left for a later approval.
Expose only the enabled flags and existing endpoint/settings UI in this
prototype; keep resource limits as validated internal options for synthetic tests.

| Boundary | Fixed policy |
| --- | --- |
| Hook stdin | Preserve 65,536 bytes, existing parse depth, 2.2-second Relay and three-second host timeout |
| Transcript IPC/event | 32,768 serialized bytes including metadata/escaping; control and legacy messages 4,096 bytes |
| Oversized text | Unicode-safe prefix fitting the serialized cap, explicitly marked truncated |
| Client budget | 4 MiB total: 3 MiB retained/in-flight events, 512 KiB shared IPC/file-reader scratch, 256 KiB transport scratch, 256 KiB control/index/reader-context reserve |
| Client counts | 256 events and 16 source streams globally; at most 64 events/1 MiB per source |
| Local transcript references | At most 512 path characters and 4 KiB serialized local request; no paths on the network or disk |
| Reader scheduling | One active read; 16 queued sessions, two-second queue expiry; at most one queued successor per active session; 32 contexts globally/eight per source, 30-minute inactivity expiry |
| Per-stop reading | 750 ms total, 2 MiB inspected bytes, 128 records, 16 replies/256 KiB normalized output, at most three attempts at 0/100/300 ms |
| Reader input | 64 MiB file ceiling, 16 KiB chunks, 64 KiB encoded record ceiling, JSON depth 16 for JSON profiles |
| Receiver budget | 64 MiB total: 48 MiB retained events, 8 MiB readers/viewer, 4 MiB ingress scratch, 4 MiB control/index reserve |
| Receiver counts | 25 reported machines, 64 retained streams, 128 sessions, 2,048 events globally |
| Per machine | 8 MiB/256 events, 16 streams, 32 sessions, within global limits |
| Per session | 2 MiB/128 events, within machine/global limits |
| Ingress concurrency | Four transcript requests globally, one per reported machine, no wait queue; retain outer Internet limiter |
| Request rate | Global 20 requests/second with burst 40; per reported machine 5/second with burst 10; global limit applies before creating machine buckets |
| Retry lifetime | Two minutes from original Client acceptance, maximum eight attempts |
| Request timeout/backoff | 1.5 seconds; delays 1, 2, 4, 8, then 15 seconds with +/-20% jitter |
| Retention | 30 minutes from receiver receipt; duplicate deliveries and reads never extend TTL |
| Session selector | Pages of 16, at most 32 entries per selected machine; include retained ended sessions |
| Event page | At most 32 events or 128 KiB serialized, whichever is reached first |
| Visible window | At most 64 events or 256 KiB serialized plus one pending page; account decoded/rendered copies against the reader budget |
| Refresh | One-second visible-tab polling, one active read, coalesced refresh; separate deadline-based expiry invalidation |
| Cursor | At most 512 bytes, five-minute life, bound to selection/receiver epoch/invalidation generation/position; appends preserve it; unavailable positions return explicit reset information |
| Shutdown | Stop content admission immediately; cancel/await owned content operations within existing four-second shutdown budget, never prolonging reporting after Exit |

Store retained events in owned immutable UTF-8 buffers. Charge each event
`Align256(serializedBytes + 1024)` and charge transient/decoded copies to their
separate reserved partitions. Reserve capacity before allocating/decoding;
avoid general-purpose content-buffer pools that retain returned text. Event
ownership transfers on delivery rather than creating unaccounted copies.
Reader buffers, decoded records, local references, and cursors share the existing
Client partitions; they are not an extra allocation pool. Reserve scratch before
opening/decoding and release it after each record/pass. If concurrent IPC leaves
insufficient reader scratch, defer only within the current pass budget or report
capacity; never increase the 4 MiB ceiling or retain partial-record buffers across
passes. Stream output into the existing queue with its normal admission checks.
Measure overhead and delayed GC/native rendering in tests. These are accounted
feature budgets, not a promise that total CLR/WinUI process RSS stays below
64 MiB; lower admission/window counts if measurements require it, never increase
the agreed ceilings or silently retain extra pages.

Evict oldest pending/retained content within the violated quota; retain one
coalesced gap record per active stream in reserved control space. Refuse new
streams/sessions once metadata slots are full, with a capacity reason; do not
evict live ordering state and then accept replay as new. Reclaim closed/expired
stream slots after their content expires, advancing the receiver-wide open revision to prevent
late traffic reopening them. Bound receiver rate-limit buckets to known machine
slots; source IDs and remote addresses cannot create an unbounded dictionary.

Retry only network failures, 408, 429, and 5xx while enabled and within lifetime.
Honor Retry-After within the remaining lifetime; expire instead of retrying early.
TLS validation, redirects, malformed protocol, and disabled/incompatible replies
suspend detail and expose a category. A 409 supplies a content-free reason:
epoch reset renegotiates; retired stream discards pending content before reopening;
unknown managed machine waits for presence; sequence conflict drops the affected
stream with an explicit discontinuity. Status remains independent.

On retry exhaustion or reconnect, do not use the host file as a delivery spool or resurrect already
acknowledged content. Missing assistant capability is unavailable/unsupported;
missing IDs mean arrival order and unavailable correlation. Neither case becomes
a successful empty message.

## Implementation work packages

| ID | Work and affected areas | Dependencies | Exit condition |
| --- | --- | --- | --- |
| P0 | Record exact hook fields plus verified transcript format/path/session/completion profiles from published evidence; add synthetic fixtures and explicit unsupported profiles | None | No invented host formats; independently establish file-reader eligibility or mark unavailable without blocking the rest |
| P1 | Implement shared config v5 defaults/opt-out and revision handling, Dashboard receiver preference, transactional recovery, and prototype policy wording | None | Default true and explicit false behave as specified; no in-app consent/authentication gate is introduced |
| P2 | Implement transcript v1 provenance/contracts, strict validation, ordering/gaps, acknowledgements, bounded viewer-read contracts, and IPC v3 with separate local-only file references | None | Synthetic compatibility/bounds/order cases pass; paths cannot serialize into network or persistent state |
| P3 | Implement Internet-mode prototype ingress, readiness gating, volatile store, reader pages, purge/reset, and fixed resource controls | P1, P2 | Valid synthetic events are readable in memory; disabled/invalid/oversized sends fail explicitly; nothing reaches SQLite |
| P4 | Implement Client-owned transcript queue/transport, transcript-only reload, opt-out cancellation, and lifecycle integration | P1, P2 | Fake transport cases cover retries, overflow/gaps, defaults/opt-out, endpoint changes, and Exit; integrate with P3 before P7 |
| P5 | Wire shared IPC v3 hook prompts/tool metadata and stop references while preserving v2 status/control | P0, P4 | Prompts stay hook-sourced; stop hints remain local; missing paths or unsupported hosts retain partial capture |
| P5a | Implement bounded stop-triggered assistant-only file adapters, path/handle validation, baselines/cursors, retries, cancellation, and queue integration | P0, P2, P4, P5 | Verified-format fixtures yield only new completed assistant text; no watchers/backfill; unknown formats and read failures preserve partial capture |
| P6 | Add Configurator default-on Share detailed conversations control and Dashboard receive/clear/capability UI; finish coordinated migration/servicing | P1, P3, P4 | UI shows defaults and applies opt-out without waiting on network; no new consent/pairing flow; recovery preserves false |
| P6a | Convert computer tile details to Settings/Transcript tabs; preserve Settings and implement bounded viewer/controller | P2, P3 | Reader/controller works against synthetic fixtures, including unsupported states; integrate P6 controls before P7 |
| P7 | Complete boundary/E2E, file-reader and viewer coverage, migration and installer inspection, and documentation | P3, P4, P5, P5a, P6, P6a | Release gates below are met without claiming unsupported host capabilities |

Do not add SDK packages, filesystem watchers, or a new reporter process as a
shortcut around P0. Only the verified, stop-triggered Client reader may supplement
hook events; the viewer must never read host files itself. Missing verified file
formats or reply fields do not block unattended completion of the reader framework,
partial capture, and viewer. P7 reports unavailable adapters and coverage gaps
rather than claiming a full transcript. Add tests within each work package; P7
consolidates integration evidence, not a late first testing phase.

## Compatibility, migration, and installer consequences

- Version transcript wire contracts independently. Keep all existing status and
  presence routes unchanged, including source-aware identity and state reduction.
- Upgrade the shared configuration to v5, with default-on detail and explicit
  false preserved. Update converters, validation, preview, reload, servicing,
  and rollback together. Audit every `ToVersion4()` and exact-v4 check so normal
  repairs never silently downgrade v5 or drop its opt-out field.
- Keep reading v1-v4 as status-only. Old Clients cannot read v5: require a
  coordinated Relay/Client/Configurator upgrade before migration. Preserve v2
  IPC control/status compatibility only where the configuration is readable.
  An incompatible configuration must fail visibly, never silently reinterpret.
- Downgrade is an explicit status-only transaction: stop the exact owned Client,
  clear volatile transcript state, write a validated v4 configuration, and
  install compatible binaries through existing servicing. On later upgrade,
  preview default-on behavior again; never describe v4 as preserving a v5 flag.
  A rollback of failed migration restores the exact prior readable config and
  runtime state without starting reporting that was stopped.
- Implement `transcript-reload` independently of network-dependent presence
  reload. A one-second configuration revision check (no filesystem watcher) detects
  invalid/deleted/stale configuration; suspend detail until valid reconciliation.
  Recheck revision before IPC acceptance, file opening/output admission, and
  every network send. Cancel reads and suspend/drop pending detail before
  endpoint/source removal changes are committed.
- Preserve exact artifact ownership, shared-scope deduplication, JSONC settings,
  current-user startup, and interrupted-transaction recovery.
- Upgrade receiver before enabling new Clients; keep Relay/Client/Configurator
  binaries compatible. An unsupported receiver disables content, not status.
  Old Relay/new Client and new Relay/old Client paths need explicit fixtures.
- Ship no transcript database migration, credential store, pairing material, or
  certificate provisioning. Packaging includes any new managed assemblies only;
  installers never establish consent, create tunnels, or start capture on their own.
- Reading host transcripts grants no ownership over those files. Installation,
  repair, rollback, opt-out, clear, and uninstall must never delete or alter them.
  Format-profile updates invalidate reader contexts instead of migrating or
  rewriting host data. Persist no local paths, cursors, extracted text, or
  message fingerprints for reader recovery.
- Preserve unrelated current-user configuration during uninstall/repair and use
  existing exact-ownership checks. Do not hand-edit generated installer harvest,
  XBF, PRI, bin, obj, or artifacts output.
- Update `README.md`, `SECURITY.md`, `docs\user-guide.md`,
  `docs\MANUAL-TEST-PLAN.md`, `docs\ACCEPTANCE.md`, and installer documentation when
  implementation changes their guarantees. Preserve unrelated worktree edits.

## Validation and release gates

Use synthetic conversation fixtures only, with separate markers for permitted
message text (including fictional names/contact details) and prohibited
tool/credential fields. Never collect real conversations or real PII to populate
tests or capability evidence. Default-on production code is exercised in isolated
test configuration, never pointed at a real operator endpoint.
Synthetic host-input transcript files are explicitly test-owned fixtures; their
presence is not a license for Agent Signaler to persist extracted output. Artifact
privacy assertions distinguish those known inputs from application-created
state/log/database/recovery files, all of which must remain content-free.

| Coverage | Existing tests to extend / proposed coverage |
| --- | --- |
| Host schemas and privacy | `SourceTransportTests`, `RemoteTests`, `IdeDiscoveryVerificationTests`: exact fields, missing IDs, unsupported reply hooks, wrong source, no tool bodies/reasoning, fictional PII preserved inside allowed messages, explicit opt-out projection |
| IPC | `ClientIpcTests`, `ClientRelayIpcTests`, `SourceTransportTests`: framing, mixed versions, size boundaries, duplicate properties, timeouts, no Client auto-start, status survives transcript rejection |
| Stop references | Extend adapter/IPC tests: exact stop/path fields, missing/oversized/invalid references, local-only types, no duplicate stop event, prompt provenance, old-Client fallback, and zero network/persistent path leakage |
| File adapters | New `TranscriptFileAdapterTests` in `AgentSignaler.Remote.Tests`: evidence-backed synthetic formats, completed assistant-only extraction, excluded prompts/tools/reasoning/subagents, invalid framing/encoding/schema, message deduplication and unsupported profiles |
| Reader boundaries | New `StopTriggeredTranscriptReaderTests`: current-user root/session/handle binding, traversal/UNC/device/ADS/reparse rejection, first baseline/current-turn proof, partial writes, delayed flush, replacement/truncation/deletion, byte/time/record/context limits, coalescing and scratch accounting |
| Reader lifecycle | Opt-out/Exit/reload/clear/reset during read/retry, no stale output, no filesystem watchers, no history backfill, no file mutation, bounded restart behavior, and unchanged prompt/status reporting on reader failure |
| Delivery/lifecycle | `ClientRuntimeTests`, `ClientDeliveryOrderingTests`, `ManagedDeliveryRaceTests`: deduplication, reconnect gaps, byte/entry bounds, cancellation, endpoint changes, opt-out/reload races, Exit with in-flight sends |
| Receiver | `ReceiverControlTests`, `SourcePresenceTests`, and new transcript store/endpoint tests: configured mode/readiness, reported machine/scope isolation without pretending authentication, invalid TLS, redirects, replay, epochs, TTL, eviction, malformed/chunked bodies and bounded reads |
| Persistence exclusion | `RelayToDashboardTests` and new E2E fixtures: inspect synthetic config/state/log/database/WAL/recovery artifacts for message markers; transcripts must exist only in memory |
| Ownership/settings | `MultiTargetIntegrationTests`, `SettingsServicingTests`, `MultiTargetServicingAuthoringTests`, `HookTargetRowTests`: v5 default-on, preserved false, v1-v4 status-only, coordinated upgrade, shared scopes, policy blocks, rollback and failed opt-out save |
| Viewer/controller | New `TranscriptViewControllerTests` and presentation tests in `AgentSignaler.Integration.Tests`: machine/source/session isolation, retained ended sessions, partial/empty/error states, paging, stale completions, refresh coalescing, scroll intent, and bounded view state |
| Details regression and UI acceptance | Preserve save/remove validation, draft edits, mapping/launch/copy-URI and cancellation workflows; check tab switching, keyboard/focus/high contrast/DPI, dialog navigation, removal, and close/Exit during reads |
| Viewer privacy/lifecycle | Synthetic end-to-end reader-to-view-model checks: no forbidden fields, inert text rendering, no capture on open, and purge of visible/cached text after expiry, eviction, local clear/disable, receiver reset, or removal |
| Network and packaging | `DashboardConnectionTests`, existing loopback TLS harness, `ProbePipelineTests`, tunnel receiver-control tests, build-and-inspect installer checks |

Minimum release outcomes:

1. New v5 and explicitly migrated v5 configurations default to sending allowed
   detailed content without an in-app consent/enrollment step once ordinary
   configuration/startup prerequisites are satisfied. An explicit false suppresses
   detail while preserving status, including after repair and recovery. Legacy
   v1-v4 configurations remain status-only until a deliberate migration.
2. A synthetic supported-hook fixture travels through actual Relay, current-user
   IPC, Client, the HTTPS test transport, the volatile reader, and the Transcript
   view model with user role, identity, tool metadata, and ordering intact.
   For each implemented verified profile, a synthetic stop with its fixture then
   exercises Relay reference ingestion, actual Client file reading, assistant-only
   extraction, transport, store, and viewer. Also test the unverified-format
   fallback. If no production format can be verified, exercise this pipeline with
   an explicitly test-only file adapter and report production readers unavailable.
   Neither that adapter nor a generic normalized producer proves host-format
   compatibility or live reply capability.
3. No unsupported host is labeled complete. Missing assistant text, unavailable
   IDs, truncation, overflow, disconnects, and epoch resets remain observable.
4. Opt-out, disabled/unavailable receiver, TLS failure, redirects, incompatible
   protocol, and endpoint changes cannot continue sending queued content or
   suppress ordinary status reporting. No authentication claim appears in the UI.
5. Measured queue/store behavior satisfies the agreed resource ceilings under
   Unicode/escaping-heavy inputs, slow senders/readers, and concurrent sessions.
6. Application-controlled persistent artifacts contain no conversation text in
   either enabled or opted-out mode. Restart produces empty transcript history.
   Host input files remain unchanged; reader cursors do not survive restart.
7. Exit stops all managed reporting; hooks stay observational and do not change
   permission decisions or wait on network work.
8. Actual host acceptance uses only separately authorized disposable fixtures and
   records capability facts, never fixture text or raw payloads. No live tunnel
   test or installer execution occurs without separate authorization.
9. Opening computer tile details selects Settings and preserves all existing
   content and actions. Switching tabs preserves unsaved edits and does not start
   reporting or connection operations.
10. Transcript renders only the selected computer/source/session's permitted
    retained content, with explicit partial, missing, truncated, and gap states.
    Ended sessions remain viewable until expiry; stale asynchronous reads cannot
    display another selection's content.
11. Viewer memory remains bounded across repeated paging and tab switches.
    Receiver disable/clear, expiry, eviction, removal, reset, close, and Exit clear affected
    displayed/cached content. No transcript text is persisted through UI settings,
    clipboard/export actions, or diagnostics.
12. There is no PII detector, consent dialog, pairing workflow, token lifetime,
    credential provisioning, or new certificate setup to complete. Documentation
    states external consent and the anonymous prototype trust limitations.
13. File reads occur only after accepted stop hints and their fixed bounded
    retries. Prompts come only from hooks. First-read baselines, unavailable
    paths/formats, skipped history, partial writes, and exhausted read budgets
    are visible; none causes whole-file replay or suppresses ordinary status.
14. No local transcript path reaches HTTP, persistent state, diagnostics, or UI.
    File I/O never blocks Relay on disk/network work, mutates host transcripts,
    creates a watcher, or survives Client opt-out/Exit.

During implementation, use the repository's Windows x64 SDK/build conventions.
Run the smallest affected existing test selectors first, then affected projects;
do not run stale binaries with `--no-build`. Restore only when required by changed
dependencies or missing assets. For the tabbed WinUI implementation, build
`AgentSignaler.Dashboard` in Release x64 and check its existing XBF/PRI publishing
path; use authorized synthetic data for manual UI acceptance. No build/test run
is needed to publish or update this plan.

## Public capability references

These references establish documentation findings, not installed-host acceptance.
Recheck them during P0; hooks are evolving and IDE capabilities are independent.

- [GitHub Copilot hooks reference](https://docs.github.com/en/copilot/reference/hooks-reference):
  CLI hook input, prompt field, stop/transcript path, subagent distinction, and
  observer exit behavior. The path field alone is not a verified file schema.
- [VS Code hooks reference](https://code.visualstudio.com/docs/agents/reference/hooks-reference):
  prompt/tool event fields and `Stop` schema.
- [VS Code agent hooks](https://code.visualstudio.com/docs/agent-customization/hooks):
  optional session identity, transcript-format instability, preview/local-host
  constraints, and execution semantics. Its transcript path is provided for
  convenience; the file format is not a stable hook API and may change.
- [Visual Studio agent mode](https://learn.microsoft.com/en-us/visualstudio/ide/copilot-agent-mode?view=visualstudio):
  product behavior, not proof of a supported external reply hook.
- [Copilot SDK streaming events](https://docs.github.com/en/copilot/how-tos/copilot-sdk/features/streaming-events):
  a distinct event API, explicitly excluded from this hook-triggered plan; it does not
  establish passive access to arbitrary existing CLI or IDE sessions.

## Unattended completion and stopping rules

All product choices needed for the prototype are fixed above. Use existing
repository mechanisms and .NET/WinUI APIs; do not introduce a new test framework,
SDK transcript bridge, second reporter, browser viewer, direct HTTPS server,
pairing flow, general private-storage reader, or filesystem watcher. The bounded
assistant-only reader of a verified, hook-supplied transcript is the sole allowed
file-capture extension. Missing field/format evidence disables only that adapter,
not implementation of the rest of the prototype.

Complete code and synthetic automated coverage for P0-P7, including P5a and P6a, after a
separate implementation instruction. Do not wait for a human to configure an IDE,
enter consent, enroll a machine, sign in, create a tunnel, or demonstrate a main
assistant reply. Mark actual-host, cloud, installer execution, and manual UI
acceptance as not performed; never equate fixture success with those outcomes.
If a toolchain is unavailable, report the concrete validation blocker without
adding runtime fallbacks or falsely claiming a successful build.

For ordinary implementation details not changing this specified behavior, choose
the simplest existing repository pattern. Stop only for conflicting concurrent
edits, a required action outside the authorized development boundary, or a
contradiction that cannot be resolved while preserving these fixed decisions.
Document the blocked capability and continue independent work where possible.
