# Agent Signaler repository instructions

Agent Signaler is a Windows-only .NET 10 system that reports sanitized agent
state from development machines to a WinUI dashboard. Preserve privacy,
explicit ownership, bounded resource use, cancellation, and rollback across
every change.

## Architecture

- `AgentSignaler.Contracts` owns wire models, protocol versions, validation,
  source identity, and state-reducer semantics. Treat protocol fields and enum
  values as compatibility surfaces.
- `AgentSignaler.Relay` is the short-lived hook executable. It parses bounded
  hook input and forwards sanitized events to the persistent Client.
- `AgentSignaler.Client` is the current-user tray process and the sole managed
  network reporter. It owns local session state, heartbeats, and shutdown.
- `AgentSignaler.Remote` contains hook adapters, discovery, configuration,
  transactional integration management, named-pipe IPC, Client coordination,
  and HTTP transport.
- `AgentSignaler.Service` hosts the bounded Kestrel receiver and persists
  dashboard machine state in SQLite.
- `AgentSignaler.Dashboard` is an unpackaged, self-contained WinUI 3 x64 app. It
  owns display, tray, compact navigation and visual workflow adapters.
- `AgentSignaler.Dashboard.Core` owns the shared operational runtime, canonical
  data resource lease, settings, receiver, local state, CLI workflows and
  independent domain revisions for Dashboard and RpcHost.
- `AgentSignaler.RpcHost` is an explicitly launched self-contained console host
  with a separate loopback-only JSON-RPC listener. Preserve the reviewed
  unauthenticated localhost policy, bounded protocol, single-controller drain,
  explicit DTO privacy boundary and stateless revision-checked pagination.
  Never tunnel its control port or add runtime firewall/elevation helpers.
- `AgentSignaler.Configurator` is an unpackaged, self-contained WinUI 3 x64 app
  for discovering, previewing, applying, repairing, and removing user-level
  integrations.
- `AgentSignaler.Tunneling` isolates Dev Tunnels CLI discovery, validation,
  process control, and diagnostics.
- Tests are separated by service, remote/integration ownership, end-to-end
  integration, and tunneling behavior.

The required reporting path is:

`verified hook -> Relay -> current-user named-pipe IPC -> Client -> Dashboard`

Do not add direct hook-to-HTTP reporting, a second reporting daemon, or a hook
that starts the Client. Tray Exit must stop managed reporting.

## Engineering rules

- Target Windows 11 x64 and the SDK selected by `global.json`. Preserve
  `Platform=x64`, nullable reference types, analyzers, and existing project
  target-framework choices.
- Prefer immutable records, explicit validation, bounded collections and
  payloads, UTC `DateTimeOffset`, cancellation tokens, and narrow expected
  exception filters.
- Never log or persist prompts, responses, source code, tool arguments, raw
  hook payloads, tokens, connection URIs, account identifiers, or raw CLI
  output. Diagnostics use application-defined category codes.
- Preserve strict JSON handling, protocol-version checks, duplicate-property
  rejection, body/message limits, ordering, deduplication, and replay
  protection.
- Configuration changes must be previewable and transactional. Modify or
  remove only app-owned artifacts after exact ownership verification; preserve
  unrelated user configuration and support rollback/recovery.
- Do not kill processes by image name. Scope IPC, startup, process, and file
  ownership to the current user and exact Agent Signaler identity.
- Anonymous Dev Tunnel mode encrypts transport but does not authenticate
  senders. Keep its receiver loopback-only and preserve rate/concurrency limits.
- Keep UI event handlers thin where practical and move testable behavior into
  non-UI classes. Marshal UI changes through the WinUI dispatcher when work
  originates off the UI thread.
- Do not hand-edit generated `bin`, `obj`, `artifacts`, XBF, PRI, or generated
  WiX harvest output.

## Build and verification

Use PowerShell on Windows:

```powershell
dotnet restore AgentSignaler.slnx -p:Platform=x64
dotnet build AgentSignaler.slnx --no-restore --configuration Release -p:Platform=x64
```

Run the smallest affected test project, then the full affected project:

```powershell
dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj --no-build --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Remote.Tests\AgentSignaler.Remote.Tests.csproj --no-build --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj --no-build --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Tunneling.Tests\AgentSignaler.Tunneling.Tests.csproj --no-build --configuration Release -p:Platform=x64
```

Never set `AGENT_SIGNALER_LIVE_TUNNEL_TEST=1` without explicit authorization and
an approved disposable environment. Installer validation is build-and-inspect
only; do not execute generated installers during ordinary development.
