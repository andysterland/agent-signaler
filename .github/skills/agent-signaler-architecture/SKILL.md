---
name: agent-signaler-architecture
description: Trace Agent Signaler features across processes and projects. Use when planning or implementing cross-cutting protocol, reporting, configuration, dashboard, or lifecycle changes.
---

# Agent Signaler architecture tracing

Before changing a cross-cutting feature:

1. Start with the relevant model and validation in
   `src/AgentSignaler.Contracts`.
2. Trace hook ingestion through `AgentSignaler.Relay` and
   `AgentSignaler.Remote`.
3. Trace current-user IPC and persistent reporting through
   `ClientIpc`, `ClientCoordinator`, and `AgentSignaler.Client`.
4. Trace receiver and persistence behavior through `DashboardServer`,
   `MachineStore`, and `AgentSignaler.Service`.
5. Trace presentation in `AgentSignaler.Dashboard`, or configuration ownership
   in `AgentSignaler.Configurator` and Remote integration managers.
6. Identify installer and migration consequences.
7. Locate tests for each affected boundary before editing.

Preserve the reporting chain:

`hook -> Relay -> named pipe -> Client -> HTTP(S) -> DashboardServer -> SQLite -> WinUI`

Protocol or persistence changes are incomplete unless every producer, consumer,
validator, compatibility path, and affected test is addressed.
