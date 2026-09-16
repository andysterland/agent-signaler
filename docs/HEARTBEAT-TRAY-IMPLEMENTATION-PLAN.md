# Remote tray client and heartbeat implementation plan

## Goal

Add a persistent remote-client executable that runs in the Windows notification area, starts automatically at user sign-in, and owns all reporting to the dashboard. Replace scheduled execution with an in-process heartbeat timer configured in Configurator, defaulting to **5 minutes**.

Confirmed behavior:

- The tray menu offers **Open Configurator** and **Exit**.
- Exit stops **all** reporting, including Copilot hook delivery, until the tray client is explicitly started again or starts at the next Windows sign-in.
- Exit sends a best-effort offline report before closing.
- A crash or lost connection causes the dashboard to mark the client offline after **two heartbeat intervals plus one minute**: **11 minutes** at the default interval.
- No scheduled task is created or used for normal operation.

This document plans implementation only. All project paths below are relative to
the repository's `src` directory.

## Current code baseline

The current implementation differs from the original project plan:

| Surface | Current behavior |
| --- | --- |
| `AgentSignaler.Contracts\Protocol.cs` | Wire protocol v1 contains only Copilot hook events; every report requires a session ID. Offline timeout is fixed at five minutes. |
| `AgentSignaler.Remote\RelayEngine.cs` | Short-lived `hook` and `test` modes. Hooks update bounded local session state and send HTTP directly; no heartbeat mode remains. |
| `AgentSignaler.Remote\IntegrationManager.cs` | Removes owned legacy heartbeat tasks during apply; retains task ownership/rollback support. Generates hooks by enumerating every `AgentEvent`. |
| `AgentSignaler.Remote\RemoteConfiguration.cs` | Strict custom JSON converter for configuration v1/v2; v2 supports HTTP/HTTPS URLs and the installed Relay path. |
| `AgentSignaler.Service\MachineStore.cs` | Derives offline state from last receipt time; persists session state and bounded event receipts. No current presence lifecycle. |
| Dashboard `NativeWindow.cs`, `Program.cs`, `DashboardSettings.cs` | Existing notification-area, single-instance/IPC, and per-user Run-key patterns to reuse or narrowly extract. |
| Configurator | Previews and transactionally applies integration changes; health testing does not change machine status. |

Implementation must add heartbeat support, not merely reactivate old scheduled-task behavior. Preserve existing HTTPS transport, anonymous remote access, state reducer semantics, privacy restrictions, and installer ownership rules.

## Architecture

Add `src\AgentSignaler.Client`, producing **AgentSignaler.Client.exe**, as a self-contained Windows x64 tray application alongside Configurator and Relay.

```text
Windows user sign-in
  -> AgentSignaler.Client.exe --background --config <absolute path>
     -> single reporting coordinator
     -> heartbeat timer + HTTP delivery
     -> tray menu: Open Configurator / Exit

Copilot hook
  -> short-lived AgentSignaler.Relay.exe
     -> validate and sanitize hook
     -> bounded, current-user local IPC to tray client
     -> return quickly; no direct HTTP fallback

Configurator
  -> preview/apply persisted settings and startup registration
  -> ask running tray client to reload committed settings
```

The tray process is the **only network reporter** for upgraded clients. This ownership is necessary to make Exit reliable: a marker-file check followed by independent Relay HTTP delivery would still allow an in-flight hook to resurrect a machine after Exit.

Keep domain logic, local state, configuration, IPC contracts, and transport coordination testable in `AgentSignaler.Remote`. Keep the message loop, icon, process activation, and Windows integration in the new executable. Reuse the dashboard's Win32 notification-area mechanism with a hidden window and functioning message loop; do not depend on a visible Configurator window or reference the dashboard executable.

No always-open server connection is required: existing communication is HTTP. "Stop the connection" means stop accepting reporting work, stop timers/retries, send the terminal offline report when possible, and dispose transports. Do not create remote Dev Tunnels clients or require remote sign-in.

## Phase 1: define presence and heartbeat protocol

### Keep lifecycle separate from Copilot events

Do not append heartbeat/offline values to `AgentEvent`: `IntegrationManager` currently enumerates it to generate Copilot hooks, and the state reducer treats these as session events.

Introduce a separate v2 reporting envelope and `/api/v2/reports` endpoint with explicit kinds:

| Kind | Meaning |
| --- | --- |
| `started` | A new tray reporting run is active; announces interval and current sanitized session snapshot |
| `heartbeat` | Renews liveness and reconciles current session state, even without Copilot activity |
| `hook` | Carries an existing validated Copilot hook event, inside the tray-managed lifecycle |
| `offline` | Terminal report for the current run; overrides session display state immediately |

Common fields should include protocol version, event ID, machine identity/metadata, client version, run generation, sequence, and UTC report timestamp. Started/heartbeat reports include the validated heartbeat interval and bounded session snapshot. Require a session ID only for hook reports. Specify strict per-kind allowed fields and safe error responses.

Preserve existing request-size, JSON-depth, session/machine-count, anonymous transport, rate-limit, and privacy boundaries. Never include prompts, raw hook payloads, tool arguments/results, or secrets.

### Durable ordering and Exit races

Use a persisted, monotonically increasing **run generation** per machine/configuration, allocated atomically when the tray starts, plus increasing report sequence numbers within the run.

- Allocate generation under the single-owner lock before sending `started`; fail visibly on corrupt generation state rather than resetting to a lower value.
- The dashboard persists the active generation, last accepted sequence, and terminal-offline flag with the machine snapshot in the same transaction as report acceptance.
- A higher generation begins only with a valid `started` report. Older generations cannot renew liveness or change state.
- Duplicate or stale sequences never refresh liveness. Retry a report with the same sequence/event ID.
- Once offline is accepted, no report from that generation can bring the client online again. Only a newer `started` can do so.
- A heartbeat or hook delivered after the final offline report is therefore harmless, even if cancellation failed to prevent a request reaching the server.
- Report ordering uses generation/sequence, not wall-clock time; keep existing source timestamps for per-session late-hook handling.

Start must be acknowledged before subsequent reports from that generation are sent. Use serialized delivery, bounded pending work, and a full state snapshot to recover from dropped/coalesced reports. Do not advance the server sequence watermark for malformed or uncommitted requests.

These values are ordering metadata, **not authentication**. Anonymous senders can still spoof machine reports; retain the existing trust warning.

### Compatibility

Keep `/api/v1/status` and the current strict `/health` response for existing clients. Add a separate versioned capability/health endpoint that updated Configurator and Client use to confirm presence-v2 support; do not break old clients by silently changing the existing health JSON shape.

Upgrade Dashboard first, then the remote package. Refuse to enable the new tray integration against an incompatible dashboard with a clear upgrade message; do not silently fall back to direct v1 hook delivery.

Existing machines remain in legacy mode until a valid v2 `started` activates managed reporting. Once activated, reject v1 reports for that machine so a leftover old Relay cannot undo offline state. Document a deliberate local reset/downgrade procedure; do not automatically switch back based on incoming traffic.

## Phase 2: dashboard presence and storage

Extend persisted machine snapshots and `MachineView` with presence mode, heartbeat interval, last accepted presence/report receipt, run ordering metadata, and explicit offline state. Keep latest Copilot event/time separate from latest contact so a heartbeat does not replace useful activity with "heartbeat."

For managed clients:

```text
offline deadline = last accepted report receipt + (2 * heartbeat interval) + 1 minute
offline          = explicit offline OR server now >= offline deadline
```

At five minutes, the deadline is eleven minutes after the last accepted report. A valid current-generation hook can renew contact as well as a heartbeat. At the exact deadline the machine becomes Offline. Old v1 clients retain the existing five-minute policy.

An acknowledged offline report immediately overrides Failed/Waiting/Executing/etc. without deleting machine name, notes, Windows App mappings, or session information. Restart the tray to begin a new run and become online. A machine can first appear as Idle from its startup/heartbeat report without ever starting Copilot.

Heartbeats should reconcile the bounded local session snapshot, not fabricate `sessionStart`/`sessionEnd` events or reset active sessions to Idle. Preserve result expiration, `AwaitingUserInput`, timestamp ordering, and bounded retirement/tombstone behavior. Do not extend a Succeeded/Failed overlay every time a snapshot is resent.

Handle heartbeat reception, local database errors, validation failures, and protocol incompatibility explicitly. No acknowledgement until persistence commits.

**Storage growth:** do not insert one permanent receipt row for every recurring heartbeat. For v2, use bounded per-machine generation/sequence watermarks for idempotency. Keep v1 receipt behavior intact unless deliberately migrated. Add backward-compatible snapshot migration and cover the current deserializer, which already handles older heartbeat-related fields.

Show last contact, configured interval, and whether Offline was explicitly reported or inferred by timeout in machine details. Preserve the existing offline icon/color and aggregation for online clients.

## Phase 3: persistent tray runtime

### Startup and lifetime

1. Acquire one reporting-owner lock per Windows user and canonical configuration path, with data-directory isolation for tests.
2. Validate configuration and installed executable paths, initialize the tray icon and local IPC, and load local session state.
3. Allocate the new generation and send an immediate `started` snapshot; do not wait five minutes to appear.
4. Start the heartbeat scheduler independently of hook activity. Send even when no sessions exist.
5. Continue running after Configurator closes; show no main window on normal sign-in startup.

Use a cancelable `PeriodicTimer` or equivalent monotonic scheduler with injectable `TimeProvider`; prevent overlapping heartbeat callbacks. Reuse HTTP connections through the existing `RemoteHttpTransport` policy. Use bounded retries/backoff and bounded/coalesced pending snapshots, never an unlimited queue.

On sleep/resume or restored connectivity, send an immediate current snapshot rather than replaying a burst of missed heartbeats. Keep periodic scheduling independent of successful hook traffic. On failure, retain the icon and show disconnected/retrying state with rate-limited diagnostics.

Plan explicitly for simultaneous Windows sessions: the reporter lock must enforce one owner for the same per-user data directory across sessions, rather than blindly copying Dashboard's session-scoped mutex. Secondary launches must not create a second timer or issue offline for the primary instance. Explain where the active icon lives if another session owns it.

### Menu and user feedback

- **Open Configurator:** launch/activate the co-installed executable using an exact trusted path and safe argument handling. Use the same configuration/data-directory context. Do not stop reporting while settings are open.
- **Exit:** perform the controlled shutdown below. Startup registration remains enabled for the next sign-in.
- Tooltip/status should distinguish running/connected, retrying, unconfigured, and stopping. Do not describe an unacknowledged report as delivered.

Handle Explorer restart by recreating the icon. If icon initialization/recovery fails, surface an actionable error rather than leaving a silently running, uncontrollable process. Reuse existing icon resource disposal and window-message patterns; keep UI thread work separate from asynchronous networking.

### Hook IPC

Use bounded, versioned named-pipe messages restricted to the current Windows user, with endpoint names scoped to the canonical configuration. Apply short connection/read/write deadlines and payload/count limits. Do not accept arbitrary launch paths or executable commands through IPC.

Relay continues validating/sanitizing the hook, but forwards only the sanitized event to the tray. The tray serializes session-state mutation, snapshot creation, and report sequencing. An IPC acceptance acknowledgement means accepted locally, not necessarily delivered to Dashboard.

Preserve the current Relay's 2.2-second total budget and 3-second configured hook timeout. If the tray is absent, stopping, incompatible, or its queue is full, write a bounded diagnostic and exit successfully without failing Copilot. Never auto-start the tray from a hook, use direct HTTP fallback, or queue old hooks for replay after deliberate Exit.

While the tray is stopped, reporting is paused and those hook events are not captured by the tray. At a new run, clear prior-run active session assumptions and start Idle until new hooks arrive; otherwise a session ended while stopped could be advertised forever. Document this limitation for Copilot sessions that span a tray restart. Keep this behavior distinct from transient network loss while the tray remains alive, where local state continues updating and the next snapshot repairs delivery.

### Controlled Exit

1. Atomically enter Stopping, reject new hook/control work, stop the timer, and disable duplicate Exit requests.
2. Cancel/drain bounded in-flight delivery; discard queued activity that would otherwise be sent after shutdown.
3. Allocate a final sequence and attempt the terminal offline report with a fresh, bounded cancellation budget, not the already-canceled lifetime token.
4. On acknowledgement, Dashboard shows Offline immediately. On failure, retain a diagnostic and briefly notify the user that offline delivery could not be confirmed; still exit within a fixed shutdown bound.
5. Dispose HTTP/IPC resources, remove the icon, and release the owner lock last.

Use a proposed maximum five-second normal Exit budget. OS shutdown/logoff is best-effort within Windows' available time; never block system shutdown indefinitely. Process kill, power loss, or unreachable Dashboard cannot guarantee an immediate offline report: the agreed server deadline is the fallback.

No post-exit retry daemon, scheduled task, or watchdog may restart the client after deliberate Exit.

## Phase 4: Configurator settings and startup registration

Add **Heartbeat interval (minutes)** with default **5** to Configurator. Recommended initial allowed range: whole numbers **1 through 60**; document these bounds as an implementation recommendation to confirm before coding. Zero does not disable the timer. Validate in UI, configuration converter, IPC, and server using shared bounds.

Use remote configuration version 3 for managed reporting and persist `heartbeatIntervalSeconds` (default 300). Continue loading v1/v2 without rewriting files on read; migration happens only during approved Apply. Update the strict converter's field allowlist and exact serialization tests. Keep machine UUID, URL, and metadata intact.

Display effective runtime state as well as saved interval. Editing a value or testing connectivity must not change the running client. The preview must show:

- Configuration and hook changes.
- Exact Client/Relay paths.
- Start-at-sign-in registration and immediate first launch.
- Heartbeat interval and corresponding offline deadline.
- Removal of an owned legacy heartbeat task, if present.

On successful Apply, ask an already-running client to reload committed settings and return its effective configuration revision; start it when applying first-time setup. After deliberate Exit, settings-only actions must not silently resume reporting: offer an explicit **Start client** action. Ordinary Configurator launch and Test Connection must remain side-effect-free with respect to presence.

Rebuild the timer and immediately announce an accepted interval change. Until Dashboard acknowledges the new interval, schedule at the smaller of old and new intervals to avoid false offline transitions when increasing the interval. Do not claim runtime settings changed if IPC fails; show saved-but-not-applied status and provide retry/restart.

For URL changes, pause old-endpoint work, best-effort report offline there, dispose the old transport, and start a new generation against the validated new endpoint. If the old dashboard is unreachable, warn that it will age out. Coordinate this with transactional Apply/rollback rather than having workers independently notice partial files.

Register startup using an app-owned value under:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

The quoted command launches the installed Client executable with an absolute config path. This means **at user sign-in**, not before login; a system-tray app cannot provide pre-login monitoring. No elevation or Windows service is needed.

Use exact command ownership checks, idempotent registration, and rollback snapshots modeled on Dashboard startup handling. Do not overwrite an unrelated value. Detect disabled startup registration where practical and make it visible in Configurator.

## Phase 5: integration migration and installers

Extend `IntegrationPlan`, `IntegrationManifest`, and removal journals with tray/startup ownership and compatible journal/schema handling.

Apply should stage and validate files, verify dashboard v2 capabilities, commit the configuration/startup changes transactionally, then coordinate runtime activation. Distinguish disk-commit success from runtime launch/reload failure and provide explicit recovery; never report full installation success when the tray could not start. Preserve the prior runtime configuration on failed updates where possible.

Retain Task Scheduler access only to recognize/remove app-owned legacy tasks and restore a pre-existing task if a migration/uninstall transaction rolls back. Successful install/repair/update leaves **no owned heartbeat scheduled task**. Never remove unrelated tasks; remove any remaining new-task creation paths and scheduled-heartbeat documentation.

Package Client, Configurator, and Relay together in the remote MSI's existing stable per-user installation directory. Update:

- `AgentSignaler.slnx` and project references.
- `scripts\Build-Installers.ps1`: publish Client and merge it with Configurator/Relay using existing hash-collision checks.
- `scripts\Test-Installers.ps1` and WiX payload/shortcut checks.
- `scripts\Update-AgentSignaler.ps1`: account for the persistent Client process when validating upgrades.
- Remote uninstall helpers and rollback journals: stop the exact owned client gracefully, remove only the owned startup registration, and preserve identity/settings according to existing policy.

Do not kill processes by image name or other users' clients. Installer helpers must use bounded current-user IPC and explicit errors. No GUI, sign-in, required cloud connectivity, or scheduled task should be introduced into MSI custom actions. A failed offline notification must not prevent uninstall; inability to safely stop an owned process/remove owned integration needs separate, actionable handling.

Repair/upgrade must not create duplicate startup entries, timers, or icons. Update all three remote binaries as one versioned unit. Add a Start menu shortcut for explicitly restarting Client after Exit.

## Phase 6: tests and acceptance

### Automated tests

Use fake time, fake network/IPC, isolated files/registry abstractions, and actual loopback integration tests. Do not change a developer's real startup entries or tasks.

| Area | Required cases |
| --- | --- |
| Configuration | Missing interval defaults to 300 seconds; v1/v2 migration; v3 exact JSON; min/max and invalid values; unchanged UUID; preview/apply/rollback |
| Timer | Immediate startup; heartbeat at five minutes; no overlap; live interval changes; no timer after Exit; resume without catch-up bursts |
| Offline boundaries | With interval 300 seconds, online at 659 seconds and offline at 660; same formula at other intervals; legacy five-minute behavior |
| Presence ordering | Explicit offline overrides active state; duplicate/stale reports do not renew contact; delayed heartbeat/hook/start from old run rejected; newer start revives; dashboard restart preserves ordering |
| Activity | Heartbeats without sessions show Idle; activity snapshots preserve waiting/question/result semantics; result expiry is not extended; disconnected state reconciles |
| Exit races | Hook arrives during Exit; response lost after commit; request completes after offline; network unavailable; shutdown within bound; hooks cannot restart tray or use HTTP fallback |
| Storage | Old snapshot migration; bounded v2 metadata over thousands of heartbeats; malformed reports do not advance sequence or liveness; state errors are visible |
| IPC/process | One owner, duplicate launches, cross-session collision, wrong-user denial, oversized/partial input, no tray, full queue, hanging peer, Configurator launch failure |
| Servicing | Owned-only startup/task migration; foreign entries preserved; transactional rollback; partial runtime activation; upgrade/uninstall process coordination |

Extend current Contracts/Service/Remote/integration tests and add tray coordinator tests without requiring interactive UI. Exercise the real Relay executable against real local IPC and Dashboard in integration coverage, not just independent mocks. Keep `/health` probes non-mutating.

### Manual acceptance

1. Configure an existing or new client with the five-minute default, apply, and confirm exactly one icon appears and the machine registers immediately.
2. Close Configurator and run no Copilot activity; verify periodic reports keep the machine online beyond eleven minutes.
3. Change the interval, reload, and verify the exact cadence and advertised dashboard deadline.
4. Launch Configurator from the tray, confirm saved settings, and verify closing it leaves reporting running.
5. With an Executing session, select Exit. Confirm immediate Offline when acknowledged, no remaining Client process, and subsequent hooks neither send nor restart Client.
6. Start Client explicitly, then sign out/in to test automatic startup. Check Explorer restart, sleep/resume, duplicate launch, and disconnected networks.
7. Kill only the test client process or block its network; verify Offline at the agreed deadline, not at the heartbeat interval.
8. Verify anonymous HTTPS and LAN operation, with no new Dev Tunnels dependency on remote machines.
9. Upgrade from installations with and without legacy tasks; confirm no owned scheduled task remains after success, and unrelated tasks/startup entries are preserved.
10. Install, repair, upgrade, rollback, and uninstall the remote package; verify correct startup removal, bounded shutdown, and retained machine identity.

Update `README.md`, `docs\MANUAL-TEST-PLAN.md`, `docs\ACCEPTANCE.md`, and `installers\README.md` to describe sign-in startup, timer settings, eleven-minute default failure detection, Exit semantics, restart behavior, and best-effort offline delivery.

## Completion criteria

Client runs persistently in the tray at sign-in, owns all hook and heartbeat reporting, and needs no scheduled task. Configurator persists a variable heartbeat interval with a five-minute default. Exit stops all reporting and immediately marks Offline when reachable; unclean failures age out after two intervals plus one minute. Delayed traffic cannot undo Exit, remote clients remain anonymous, and upgrade/rollback/uninstall preserve ownership and identity.
