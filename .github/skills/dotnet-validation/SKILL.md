---
name: dotnet-validation
description: Build and test Agent Signaler correctly on Windows. Use after changing C#, project files, protocols, persistence, IPC, networking, or installer inputs.
---

# Validate .NET changes

Use the SDK from `global.json` and always build x64.

Restore only when needed:

```powershell
dotnet restore AgentSignaler.slnx -p:Platform=x64
```

Build product projects:

```powershell
dotnet build AgentSignaler.slnx --no-restore --configuration Release -p:Platform=x64
```

Choose the smallest relevant test project:

- Service HTTP, protocol acceptance, SQLite, aggregation:
  `AgentSignaler.Service.Tests`
- Remote configuration, IPC, hooks, ownership, rollback, Client behavior:
  `AgentSignaler.Remote.Tests`
- Executable/process/loopback reporting paths:
  `AgentSignaler.Integration.Tests`
- Dev Tunnels parsing, validation, process control, diagnostics:
  `AgentSignaler.Tunneling.Tests`

Example:

```powershell
dotnet test tests\AgentSignaler.Remote.Tests\AgentSignaler.Remote.Tests.csproj --no-build --configuration Release -p:Platform=x64
```

Keep `AGENT_SIGNALER_LIVE_TUNNEL_TEST=0`. Do not run generated installers.
