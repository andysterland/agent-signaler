# Contributing to Agent Signaler

Thanks for helping improve Agent Signaler. This project interacts with Windows
startup, IDE configuration, local IPC, network listeners, cloud tunnels, Azure
CLI, Windows App, and installers, so changes must preserve explicit ownership,
privacy, cancellation, and rollback boundaries.

## Development environment

Use Windows 11 x64 with:

- the .NET SDK selected by `global.json`;
- Visual Studio Windows application/WinUI development tools;
- Windows SDK build tools;
- Visual Studio x64 C++ tools for the native bootstrapper;
- PowerShell 5.1 or later.

WiX is restored through NuGet. Azure CLI, Dev Tunnels CLI, Windows App, and live
cloud accounts are not required for default builds or tests.

## Build and test

```powershell
dotnet restore AgentSignaler.slnx -p:Platform=x64
dotnet build AgentSignaler.slnx --no-restore --configuration Release -p:Platform=x64
dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Remote.Tests\AgentSignaler.Remote.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Tunneling.Tests\AgentSignaler.Tunneling.Tests.csproj -p:Platform=x64
```

Build and inspect installers without installing or publishing them:

```powershell
.\scripts\Build-Installers.ps1 -Version 1.0.14
```

Run the smallest test selection that covers a change, then run the full affected
project before submitting. Keep analyzers enabled and do not suppress warnings
without a documented correctness reason.

## Safety rules

- Never enable `AGENT_SIGNALER_LIVE_TUNNEL_TEST=1` without an approved disposable
  environment and explicit authorization for anonymous Internet exposure.
- Do not use production Azure subscriptions, Dev Boxes, tunnels, IDE profiles,
  firewall rules, startup entries, scheduled tasks, or installed applications as
  test fixtures.
- Installer validation must remain read-only. Do not execute generated installers
  during ordinary development or CI.
- Do not commit credentials, tokens, connection URIs, account identifiers,
  databases, logs, generated output, signing material, or real environment
  screenshots.
- Preserve existing user configuration and unrelated integrations. Changes to
  owned files or registrations require preview, exact ownership checks, rollback,
  and explicit failure reporting.
- Exceptions, logs, tests, and screenshots must not expose prompts, responses,
  source code, credentials, raw CLI output, or private environment data.

## Pull requests

Describe:

- the user-visible behavior and reason for the change;
- affected trust and persistence boundaries;
- tests and manual checks performed;
- any behavior intentionally left unverified;
- documentation updated with the implementation.

Keep changes focused. Do not combine unrelated cleanup with behavior changes.
Security-sensitive findings should follow [SECURITY.md](SECURITY.md), not a public
issue or pull request.

By submitting a contribution, you agree that it may be distributed under the
repository's [MIT License](LICENSE).
