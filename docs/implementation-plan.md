# Agent Signaler Implementation Plan

## Goal

Build a Windows application that displays the status of GitHub Copilot running on remote computers. Each remote computer is represented by a square status card in a modern, always-on-top WinUI dashboard.

The dashboard also hosts an HTTP webhook server. Remote computers report Copilot status through Copilot CLI hooks installed by a separate WinUI configuration application.

## Opt-in HTTPS extension and implementation status

The Dev Tunnels extension intentionally supersedes the LAN-only assumptions below
for Internet mode, which now defaults to automatic sharing without an additional
consent checkbox. Explicitly saved LAN mode is preserved; absent mode uses Internet.
The preferred backend is MSAL sign-in plus the C# SDK; the owner-approved
CLI fallback is used while independent-app registration guidance remains unavailable.
In Internet mode the dashboard must authenticate hosting, bind the
local receiver only to loopback, and share the actual service-returned HTTPS URL.
Remote Configurator/Relay remain anonymous and never create tunnels or require
accounts, tokens, an SDK, or a CLI.

HTTPS encrypts transport but does not authenticate senders. Anyone who can reach
the anonymous endpoint can spoof reports and consume capacity. An informational risk warning,
bounded request/storage controls and approval of the external development/testing
service with no SLA are release requirements, not optional LAN protections.

Remote prerequisites implemented as of September 14, 2026 include:
canonical v2 HTTPS endpoint configuration with v1 read compatibility, preview/apply
migration and rollback, shared anonymous JSON transport, normal TLS validation,
redirect/cookie rejection, bounded retries and cancellable connectivity diagnostics.
The wire protocol remains v1. The CLI implementation
adds explicit loopback Internet mode, bounded global request controls, fail-closed
receipt capacity, owned tunnel lifecycle, and verified public-URL copying. Sharing
resumes automatically unless explicitly stopped; no extra consent is required.
See the extension plan for qualification evidence.

The preferred SDK identity route remains **blocked**: published Management/Connections/Contracts 1.3.56 metadata
targets .NET 10, but public documentation does not establish independent Entra
public-client eligibility, delegated scopes, consent procedure or account matrix.
No independent-app registration/scopes have been approved. The owner separately
approved CLI fallback qualification, completed CLI login, and authorized a disposable
synthetic tunnel: anonymous HTTPS health/status passed and the resource was deleted.
The relevant [SDK authentication question](https://github.com/microsoft/dev-tunnels/issues/557)
remains open without an answer. Do not guess scopes or reuse first-party client IDs.
Metadata is not a substitute for unpackaged WinUI/self-contained x64 validation.

After authoritative provider/registration approval, qualify the preferred MSAL/SDK
route separately; do not extract CLI tokens for SDK use. The implemented CLI route
still requires installed WinUI, independent-network, token-lifetime, and MSI release
acceptance. Do not substitute a publicly exposed LAN HTTP listener. Current scope
and verification procedures are in
`..\src\README.md`, `..\src\docs\ACCEPTANCE.md` and `..\src\installers\README.md`.

## Version 1 scope

- Windows 11 x64.
- Visual Studio 2026-compatible project system.
- C#, .NET 10, WinUI 3, and the Windows App SDK.
- Direct communication over a trusted LAN or VPN.
- HTTP with a configurable port.
- No authentication.
- No Azure or other cloud resources.
- Up to 25 remote computers.
- Full hook integration for Copilot CLI.
- Detect Visual Studio Code and Visual Studio Copilot installations, but identify their hook integration as unsupported until those clients expose equivalent configurable hooks.
- Self-contained x64 deployment through WiX/MSI installers.

## Solution structure

Create an SDK-style `.slnx` solution containing:

- `AgentSignaler.Contracts`
  - Webhook request and response contracts.
  - Status and event enumerations.
  - Protocol-version and validation logic.
- `AgentSignaler.Dashboard`
  - WinUI dashboard.
  - Embedded ASP.NET Core Kestrel server.
  - Machine-state aggregation and persistence.
  - Notification-area and firewall integration.
- `AgentSignaler.Configurator`
  - WinUI application for discovering clients and configuring Copilot CLI hooks.
  - Dashboard connection settings.
  - Hook preview, installation, repair, and removal.
- `AgentSignaler.Relay`
  - Lightweight console executable invoked directly by Copilot hooks.
  - Local session-state tracking and webhook delivery.
  - Read-only `test` mode using `DashboardConnection.TestAsync` and `GET /health`.
- Unit and integration test projects.
- WiX installer projects for the dashboard and remote client components.

Both WinUI applications will be unpackaged, self-contained x64 applications.

## Status model

| State | Suggested color | Meaning |
|---|---|---|
| Offline | Gray | No accepted unique hook event has been received for five minutes |
| Idle | Blue | The computer is reachable with no active Copilot session |
| Executing | Purple | Copilot is processing a prompt or using tools |
| Waiting | Amber | Copilot is waiting for permission or another user prompt |
| Succeeded | Green | The latest operation completed successfully |
| Failed | Red | The latest operation reported an error |

Status cards must also show an icon and accessible text so that state is not communicated through color alone.

When multiple Copilot sessions are active on one computer, calculate the displayed state using this priority:

1. Failed
2. Waiting
3. Executing
4. Succeeded
5. Idle
6. Offline

Succeeded and failed remain visible for 60 seconds before returning to the underlying waiting or idle state.

## Webhook protocol

Host Kestrel inside the dashboard process using the .NET Generic Host.

### Endpoints

- `POST /api/v1/status`
  - Accepts hook events only; rejects heartbeat events and legacy snapshot fields.
  - Returns `202 Accepted` after successful validation and storage.
- `GET /health`
  - Used by the remote configurator and Relay test mode to test connectivity without registering a machine or changing session state/liveness.

Kestrel listens on all network interfaces using a configurable, non-privileged HTTP port. The proposed default is `51820`.

### Status request

```json
{
  "protocolVersion": 1,
  "eventId": "uuid",
  "machineId": "persistent-uuid",
  "machineName": "DEV-PC-01",
  "client": "copilot-cli",
  "clientVersion": "string",
  "sessionId": "required-session-id",
  "event": "agentStop",
  "reportedAtUtc": "2026-09-05T13:40:00Z"
}
```

The contract must not include prompts, source code, tool arguments, tool output, or other potentially sensitive hook content.

The server must:

- Validate required fields and the protocol version.
- Enforce a small request-body size limit.
- Use server receipt time when calculating liveness.
- Safely ignore duplicate event IDs.
- Handle delayed or out-of-order reports without replacing newer session state.
- Return explicit validation errors for malformed requests.

## Dashboard application

### User interface

- Responsive grid of square remote-computer cards.
- Modern Fluent design with Mica, rounded cards, and light/dark theme support.
- Always-on-top enabled by default, with a persisted toggle.
- Compact layout suitable for remaining visible beside development tools.
- Card content includes display name, state icon, state label, and last-contact indicator.
- Selecting a card opens details for hostname, custom display name, client, current state, latest event, latest contact, and session information.
- Allow computers to be renamed or removed locally.

### Lifecycle

- Enforce a single dashboard instance.
- Minimize to the Windows notification area while keeping the webhook server running.
- An explicit **Exit** action stops both the UI and server.
- Optionally start with Windows through a user-controlled setting.
- Shut down Kestrel and flush persistence cleanly on exit.

### Persistence

Use SQLite to store:

- Persistent machine ID.
- Reported hostname.
- User-defined display name.
- Latest state.
- Latest event and client information.
- Latest server receipt time.

Only the latest state is retained in version 1; an event history or activity timeline is out of scope.

### Network configuration

- Display the effective webhook URL that users should copy to remote clients.
- Clearly warn that the endpoint is unauthenticated and suitable only for a trusted LAN or VPN.
- Provide an explicit elevated action to add or remove a Windows Firewall inbound rule for Private networks.
- Use the Windows Firewall COM API rather than parsing command output.
- Do not require an HTTP URL reservation because Kestrel listens directly through sockets.

## Remote configurator

### Client discovery

Detect:

- Copilot CLI and its effective home directory.
- Visual Studio Code and the GitHub Copilot extension.
- Visual Studio and its GitHub Copilot installation.

Only Copilot CLI hook configuration is enabled in version 1. Visual Studio Code and Visual Studio are shown as detected but unsupported, with an explanation that they do not currently expose equivalent user-configurable Copilot hooks.

### Configuration workflow

1. Collect the dashboard hostname or IP address and port.
2. Generate or load a persistent machine UUID.
3. Test the dashboard `/health` endpoint.
4. Discover the Copilot CLI hook location.
5. Generate a preview of the proposed hook/relay changes and any legacy heartbeat-task removal.
6. Back up files that will be changed.
7. Apply the configuration after explicit confirmation.
8. Verify that the hook file is valid JSON and that a read-only health check succeeds.

The configuration app must support install, repair, update, and uninstall operations. These operations must be idempotent and must not modify unrelated hook files.

New installations create no scheduled task. Approved integration apply removes
only an ownership-verified legacy `AgentSignaler-Heartbeat-<UUID>` task, backing up
its XML and restoring it with the previous files/manifest on rollback. Modified or
unowned tasks must not be silently deleted. Loading or previewing configuration
does not perform this migration. Health verification does not register a machine;
the first accepted hook does.

### Hook location

Use the effective Copilot home:

```text
%COPILOT_HOME%\hooks\
```

When `COPILOT_HOME` is not set, use:

```text
%USERPROFILE%\.copilot\hooks\
```

Install an application-owned hook file with a unique, stable name. Keep timestamped backups and restore only files owned or previously modified by Agent Signaler.

## Relay executable

Configure Copilot CLI hooks using their supported `exec` and `args` fields so that the relay executable is launched directly without PowerShell or command-shell quoting.

For each invocation, the relay:

1. Reads the hook payload from standard input.
2. Extracts only the event and identifiers needed for status reporting.
3. Updates an atomic per-user local session-state file.
4. Sends the minimal status request to the dashboard.
5. Exits quickly without interfering with Copilot.

Network failure must never block or fail a Copilot operation. Use short connection and request timeouts, retry only transient errors within a strict time budget, and maintain bounded diagnostic logs that contain no prompt or source content.

### Hook mapping

| Copilot event | Agent Signaler state |
|---|---|
| `sessionStart` | Waiting |
| `userPromptSubmitted` | Executing |
| `preToolUse` | Executing |
| Successful `postToolUse` | Executing |
| `permissionRequest` | Waiting |
| `agentStop` | Succeeded, then Waiting after 60 seconds |
| `errorOccurred` or failed tool result | Failed |
| `sessionEnd` | Idle |

The relay maintains state per Copilot session so concurrent sessions can be aggregated correctly.

## Hook-only reporting and offline detection

There is no scheduled periodic heartbeat or snapshot reconciliation. The relay
`heartbeat` command is removed; `test` uses the existing
`DashboardConnection.TestAsync` read-only `GET /health` path, without posting
status, registering a machine, or mutating local session state.

The dashboard marks a computer Offline five minutes after the last successfully
received unique hook event. Duplicate reports and health checks do not refresh
liveness. A quiet but active CLI can go Offline; the next accepted unique hook
restores online status. Abrupt termination cannot be distinguished from a quiet
session by this timeout.

Missed deliveries are not automatically repaired when connectivity returns.
Subsequent hooks update their own sessions, not a machine-wide snapshot; stale
session state may remain when an end event was lost.

## Security boundaries

Version 1 intentionally has no authentication or transport encryption.

Mitigations still required:

- Display a permanent trusted-network warning in network settings.
- Default the firewall rule to the Private profile only.
- Do not open the Public firewall profile.
- Validate all input and enforce request-size limits.
- Never execute values received through the webhook.
- Do not persist or transmit original Copilot hook payloads.
- Avoid logging machine data beyond identifiers required for diagnostics.
- For existing LAN mode, do not expose the HTTP receiver outside a trusted network.
  The opt-in Dev Tunnels extension instead explicitly accepts anonymous senders over
  HTTPS; require its identity, isolation, capacity, consent and release gates above.

## Implementation phases

### Phase 1: Foundation

- Create the `.slnx` solution and SDK-style projects.
- Configure WinUI 3, Windows App SDK, .NET 10, x64, nullable reference types, analyzers, and shared build properties.
- Define the protocol contracts, state reducer, validation rules, and test fixtures.

### Phase 2: Dashboard service layer

- Add in-process Generic Host and Kestrel startup/shutdown.
- Implement webhook and health endpoints.
- Add SQLite persistence, machine registration, event deduplication, ordering, aggregation, and timeout transitions.
- Add webhook integration tests.

### Phase 3: Dashboard UI

- Build the status grid and machine details experience.
- Add accessible visual states, theme support, Mica, and responsive sizing.
- Implement always-on-top, settings, single-instance behavior, and notification-area lifecycle.

### Phase 4: Relay

- Implement hook payload parsing and event mapping.
- Add atomic per-session state persistence.
- Implement hook-only status delivery, bounded retries, diagnostic logging, and read-only health test mode.
- Test simultaneous sessions, network failures, malformed input, and timeout behavior.

### Phase 5: Remote configurator

- Implement client and hook-directory discovery.
- Add endpoint configuration and connectivity testing.
- Add change preview, backups, hook installation, repair, and removal.
- Remove ownership-verified legacy heartbeat tasks on apply, with backup/rollback; create no new task.
- Display unsupported status for Visual Studio Code and Visual Studio.

### Phase 6: Windows integration and deployment

- Add explicit elevated firewall-rule management.
- Build WiX MSI installers for dashboard and remote components.
- Verify upgrade, repair, uninstall, and rollback behavior.
- Ensure hook paths remain valid after product upgrades and legacy-task migration preserves ownership and rollback.

### Phase 7: End-to-end validation

- Test across at least two Windows 11 computers over a LAN or VPN.
- Verify every supported Copilot CLI hook transition.
- Verify concurrent sessions and priority aggregation.
- Verify succeeded/failed expiration and offline detection.
- Verify dashboard restart and persisted-state recovery.
- Verify remote endpoint changes, unavailable networks, firewall rules, upgrades, and uninstall restoration.

## Version 1 acceptance criteria

- A newly configured remote computer appears automatically after its first accepted hook, not a health test or apply.
- The dashboard accurately represents all six states.
- Multiple sessions on one computer aggregate according to the defined priority.
- Remote reporting continues when the configurator is closed.
- The dashboard server continues when its window is minimized to the notification area.
- Five minutes without a received unique hook marks a computer Offline, including a quiet active CLI.
- A fresh hook restores online status; missed delivery is not automatically reconciled.
- Relay test mode leaves machine/session state and liveness unchanged; heartbeat commands/events and snapshot fields are rejected.
- A completed or failed operation remains visible for 60 seconds.
- Hook network failures do not delay or fail Copilot operations.
- Existing unrelated Copilot hook configuration remains intact.
- MSI repair, upgrade, and uninstall leave the machine in a consistent state.
- No Azure resources or external services are required.

## References

- [GitHub Copilot hooks reference](https://docs.github.com/en/copilot/reference/hooks-reference)
- [Using hooks with GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks)
- [Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)
- [Configure endpoints for Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints)
