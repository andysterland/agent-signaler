# Third-Party Components

Agent Signaler depends on third-party software distributed under its own terms.
This file is an inventory aid, not a substitute for the upstream license text.
Review the exact restored dependency graph and redistributed payloads before each
public binary release.

## Build and runtime dependencies

| Component | Use | Upstream information |
| --- | --- | --- |
| .NET and ASP.NET Core | Application runtime and local HTTP service | <https://github.com/dotnet/runtime> and <https://github.com/dotnet/aspnetcore> |
| Microsoft Windows App SDK | WinUI desktop applications | <https://github.com/microsoft/WindowsAppSDK> |
| Microsoft Windows SDK Build Tools | Windows API build references | <https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools> |
| CommunityToolkit WinUI DataGrid | Configurator UI | <https://www.nuget.org/packages/CommunityToolkit.WinUI.UI.Controls.DataGrid> |
| Microsoft.Data.Sqlite | Local dashboard persistence | <https://www.nuget.org/packages/Microsoft.Data.Sqlite> |
| SQLitePCLRaw.bundle_e_sqlite3 | Native SQLite bundle | <https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3> |
| WiX Toolset SDK and Heat | MSI and Burn bundle builds | <https://github.com/wixtoolset/wix> |
| xUnit and Microsoft.NET.Test.Sdk | Automated tests | <https://xunit.net/> and <https://www.nuget.org/packages/Microsoft.NET.Test.Sdk> |

Transitive dependencies are governed by their own licenses. Generate and review
the restored package inventory rather than relying only on this direct-dependency
list.

## Installer prerequisite payloads

The optional Dashboard bundle can embed pinned Microsoft prerequisite payloads:

| Component | Source and terms |
| --- | --- |
| Microsoft Azure CLI | Versioned payload metadata is in `installers\AgentSignaler.Dashboard.Bundle\Prerequisites.props`; license link: <https://github.com/Azure/azure-cli/blob/azure-cli-2.90.0/LICENSE> |
| Microsoft Dev Tunnels CLI | Versioned payload metadata is in `installers\AgentSignaler.Dashboard.Bundle\Prerequisites.props`; terms link: <https://aka.ms/devtunnels/tos> |
| Windows App | Detected or linked through Microsoft Store; it is not redistributed by Agent Signaler |

Prerequisite inclusion is not approval to redistribute a binary publicly. Before
attaching an installer to a GitHub release, verify the exact payload's license,
redistribution permission, signer, version, identity, and hash.

## Build-time source material

The native bootstrapper build retrieves SHA-256-pinned WiX 4.0.6 native API
headers from the upstream WiX repository. See:

<https://github.com/wixtoolset/wix/blob/v4.0.6/LICENSE.TXT>

## Project assets

`src\AgentSignaler.Configurator\AgentSignaler.ico` is an Agent Signaler source
asset. Its authorship and redistribution approval remain a public-release gate.
