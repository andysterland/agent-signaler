<div align="center">

# Agent Signaler

**A Windows dashboard for seeing what Copilot CLI and verified IDE agents are doing across your development machines.**

[Use cases](#use-cases) · [How it works](#how-it-works) · [Get started](#get-started) · [Security](SECURITY.md) · [Contributing](CONTRIBUTING.md)

</div>

![Synthetic Agent Signaler dashboard preview showing several development machines in different agent states](docs/images/dashboard-overview.svg)

> [!IMPORTANT]
> Agent Signaler is an early Windows-only project. Automated coverage is extensive,
> but live cloud, installer lifecycle, IDE compatibility, and release-signing
> acceptance are still incomplete. Review the [acceptance record](docs/ACCEPTANCE.md)
> before relying on it for important workflows.

## Why Agent Signaler?

- **See agent state at a glance:** executing, waiting for input, succeeded, failed,
  idle, or offline.
- **Monitor multiple machines:** identify each computer by name, optional display
  name, note, source, and latest activity.
- **Keep status visible:** switch to compact always-on-top tiles while working in
  another application. Compact tiles show only the state glyph, using the same
  colors and symbols as the full dashboard; hover for machine details.
- **Open the mapped Dev Box:** refresh a validated Windows App connection or focus
  an existing matching window. Launches from compact view show a cancellable progress
  dialog while searching local windows, refreshing the connection, and launching Windows App.
- **Choose the network boundary:** use a trusted private LAN/VPN or an explicitly
  configured anonymous Dev Tunnel backed by a loopback-only listener.
- **Configure integrations deliberately:** preview and apply supported user-level
  CLI and IDE hooks with ownership and rollback checks.

## Use cases

| Use case | Workflow and result |
| --- | --- |
| **Monitor long-running Copilot CLI work** | Run the managed Client on a development machine. The dashboard shows live execution, waiting, result, idle, and offline transitions without displaying prompt or response content. |
| **Track several development machines** | Connect multiple Windows environments and give them clear display names or notes. Each tile keeps machine identity and activity separate. |
| **Keep status visible while multitasking** | Minimize the dashboard into compact always-on-top tiles. Status remains visible without keeping the full window open. |
| **Open the correct Dev Box** | Explicitly map a machine to an Azure Dev Box. Agent Signaler refreshes the validated connection or restores and focuses a matching Windows App window. |
| **Share status across networks** | Use an anonymous Dev Tunnel to expose only the loopback dashboard endpoint instead of opening the LAN listener directly to the Internet. |
| **Integrate supported IDE agents** | Configurator discovers candidate integrations, previews exact changes, verifies supported hook behavior, and applies owned user-level configuration transactionally. |

## Product tour

| Dashboard | Compact monitoring |
| --- | --- |
| ![Synthetic dashboard overview with build, documentation, and test agents](docs/images/dashboard-overview.svg) | ![Synthetic compact view with vertically stacked machine status tiles](docs/images/compact-view.svg) |

| Dev Box workflow | Sharing and diagnostics |
| --- | --- |
| ![Synthetic Dev Box mapping and Windows App connection workflow](docs/images/dev-box-mapping.svg) | ![Synthetic Internet sharing and prerequisite diagnostic status](docs/images/sharing-diagnostics.svg) |

All previews use fictional machines, accounts, URLs, and resources.

## How it works

```mermaid
flowchart LR
    A[Copilot CLI or verified IDE hooks] --> B[Relay]
    B --> C[Current-user Client]
    C -->|Trusted LAN/VPN| D[Dashboard]
    C -->|Anonymous HTTPS Dev Tunnel| D
    D --> E[Status tiles and local history]
    D --> F[Optional Windows App Dev Box launch]
```

Relay accepts bounded hook events and sends sanitized state through local
current-user IPC. The persistent Client owns network reporting and periodic
presence snapshots. Dashboard stores local machine state in SQLite and never needs
prompt text, response text, source code, or credentials.

## Get started

### Requirements

- Windows 11 x64
- .NET SDK `10.0.100` or a compatible feature-band selected by `global.json`
- Visual Studio Windows/WinUI tools for application development
- Visual Studio x64 C++ tools for the native installer bootstrapper
- Optional: Microsoft Dev Tunnels CLI for Internet sharing
- Optional: Azure CLI and Windows App for Dev Box connections

### Build and test

```powershell
dotnet restore AgentSignaler.slnx -p:Platform=x64
dotnet build AgentSignaler.slnx --no-restore --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Remote.Tests\AgentSignaler.Remote.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Tunneling.Tests\AgentSignaler.Tunneling.Tests.csproj -p:Platform=x64
```

The live tunnel test is opt-in and skipped unless
`AGENT_SIGNALER_LIVE_TUNNEL_TEST=1`. Do not enable it without an approved disposable
environment and explicit anonymous-exposure authorization.

Build and inspect installers locally without installing or publishing them:

```powershell
.\scripts\Build-Installers.ps1 -Version 1.0.14
```

See the [user guide](docs/user-guide.md) for setup and operation,
[installer guide](installers/README.md) for packaging details, and
[contribution guide](CONTRIBUTING.md) for development safety requirements.

## Security and privacy

Agent Signaler intentionally transports state, identity, timing, and bounded
integration metadata—not prompts, responses, source code, tokens, or connection
URIs. Anonymous Dev Tunnel mode encrypts transport but does **not** authenticate
status senders; anyone who can reach the URL can submit reports and consume
capacity.

Read [SECURITY.md](SECURITY.md) before deployment. Report vulnerabilities
privately rather than opening a public issue.

## Project status

- Source builds and automated non-live tests are the current supported
  development path.
- Installer, upgrade, rollback, Windows App, Azure, Dev Tunnel, and IDE workflows
  still have documented manual release gates.
- GitHub Releases may contain explicitly labeled unsigned development MSIs with
  published SHA-256 checksums. No signed release is currently promised.
- Source code is available under the [MIT License](LICENSE).

Detailed status is tracked in [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md) and
[docs/MANUAL-TEST-PLAN.md](docs/MANUAL-TEST-PLAN.md).

## Community

- [Contributing](CONTRIBUTING.md)
- [Support](SUPPORT.md)
- [Security policy](SECURITY.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)

Licensed under the [MIT License](LICENSE).
