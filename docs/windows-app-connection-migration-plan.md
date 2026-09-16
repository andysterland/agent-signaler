# Windows App Connection Migration Plan

## Superseding existing-session reuse

The [existing-session reuse plan](windows-app-existing-session-reuse-plan.md)
supersedes this document's no-enumeration/no-focus/no-reuse restriction and
refresh-before-every-launch ordering. Both launch actions first try bounded,
boundary-safe title matching on the current desktop; a matching window is restored
and foregrounded without protocol checks, CLI/network access, or persistence.
With no match, the fresh/explicit-cache flows below remain in force, with a
serialized recheck immediately before shell activation. Validation, privacy,
cancellation, atomic persistence, and no-fallback requirements are unchanged.

## Superseding discovery and mapping workflow

The [Dev Box discovery and machine mapping plan](devbox-discovery-and-machine-mapping-plan.md)
supersedes this document's manual mapping fields and **Save and discover** workflow.
Use **Settings > Dev Box** (including all former Azure CLI controls) for explicitly
refreshed, in-memory discovery of accessible Dev Centers through Azure CLI and
Azure Resource Manager, then discovery of their assigned Dev Boxes and the machine
details picker and **Save mapping** for freshly validated, atomic mapping.
Sign-in never saves an unsaved picker selection. UPN and tenant are displayed only
in the local Dev Box Settings tab, not machine details. Existing mappings, fresh
normal launch, explicit cached launch, cancellation, installer, and security
requirements remain in force except where the discovery plan explicitly supersedes
the old flow. See `src\README.md` and `src\docs\MANUAL-TEST-PLAN.md` for current
operation and verification procedures. No Dev Center endpoints are entered or
managed by the user; ARM Dev Center read permissions are needed for enumeration.
This pointer records no new live validation.

## Implementation contract

This document is an execution specification, not a proposal. An implementation agent
must complete it without requesting design decisions, credentials, Azure access,
installer approval, or manual test results.

Use these rules:

- Implement phases in order and continue automatically when the preceding automated
  checks pass.
- Preserve unrelated working-tree changes.
- Do not perform live Azure mutations, interactive sign-in, package installation,
  MSI installation, protocol registration changes, or Dev Box lifecycle operations
  while implementing or testing.
- Use fakes for Azure CLI, protocol activation, time, and process execution.
- Build artifacts, but do not install or publish them.
- Treat manual and live-environment checks as release gates. Their absence does not
  block completion of the code change.
- If an external package cannot be downloaded during implementation, keep its
  versioned metadata in source and validate the bundle structure with a local fake
  payload. Do not weaken signature, hash, publisher, architecture, or version checks.
- Do not introduce placeholders, TODOs, optional design alternatives, or branches
  that require a later human choice.

The fixed decisions in this plan are:

| Decision | Required value |
| --- | --- |
| Target OS/architecture | Windows 11 x64 |
| Target framework | Existing `net10.0-windows10.0.22621.0` |
| Dev Center API version | `2025-02-01` |
| Azure cloud | Public Azure only |
| Azure CLI minimum | `2.90.0`, 64-bit Microsoft-signed MSI installation |
| Azure CLI executable | `%ProgramFiles%\Microsoft SDKs\Azure\CLI2\wbin\az.exe` |
| Windows App minimum | `2.0.804.0` |
| Dev Tunnels CLI | Existing exact version in `TunnelValidation.SupportedCliVersion` |
| Normal launch behavior | Refresh through Dev Center before every launch |
| Cached URI behavior | Never automatic; explicit details-dialog action only |
| Cached URI expiry | No time-based deletion; always display retrieval time |
| Mapping retention | Retain across repair/upgrade/uninstall |
| Installer entry point | `AgentSignaler.Dashboard.Setup.exe` |
| Direct MSI behavior | Detect and warn only; never install prerequisites |
| Authentication | Existing Azure CLI session or explicit `az login` button |
| Azure operations | `az login`, `az account show`, and GET-only `az rest` |

## Goal

Replace classic Remote Desktop Connection integration with Microsoft Dev Box
connection through Windows App. Selecting a configured computer must resolve a fresh,
service-issued `ms-cloudpc:connect` URI for its explicit Dev Box mapping and pass that
URI unchanged to Windows shell activation.

The reported guest hostname and editable display name are never Dev Box identifiers.
There is no `mstsc.exe` fallback.

## Existing implementation to replace

- `src\src\AgentSignaler.Dashboard\RemoteDesktopLauncher.cs` validates a hostname,
  enumerates `mstsc.exe` windows, activates a title match, or starts
  `%SystemRoot%\System32\mstsc.exe`.
- `src\src\AgentSignaler.Dashboard\MainWindow.cs` owns a synchronous
  `RemoteDesktopLauncher` and calls it from compact tiles and machine details.
- `src\src\AgentSignaler.Dashboard\CompactWindow.cs` accepts a synchronous connect
  callback.
- `src\src\AgentSignaler.Service\MachineStore.cs` persists a JSON machine snapshot
  without Dev Box metadata.
- `src\src\AgentSignaler.Service\MachineView.cs` exposes no connection mapping.
- `src\tests\AgentSignaler.Integration.Tests\RemoteDesktopLauncherTests.cs` tests the
  classic RDP implementation.
- `src\scripts\Build-Installers.ps1` and `src\scripts\Test-Installers.ps1` build and
  inspect two MSI packages but no bootstrapper.

## Qualified service contract

Qualification on September 14, 2026 used Azure CLI 2.90.0 and the Dev Center
developer API. Repeated read-only requests for four assigned Dev Boxes returned
`cloudPcConnectionUrl` values shaped as:

```text
ms-cloudpc:connect?cpcid=...&username=...&environment=...&version=...&source=...
```

Use only:

```text
GET {endpoint}/projects/{project}/users/me/devboxes/{devBox}/remoteConnection?api-version=2025-02-01
```

The implementation must not synthesize a connection URI, derive a Cloud PC ID from
a hostname, scrape the portal, call a management-plane API, or read an Azure CLI
access token.

## Security boundary

The only permitted Azure CLI command models are:

```text
az login [--tenant <tenant-guid>]
az account show --output json
az rest --method get --resource https://devcenter.azure.com --url <validated-url>
```

Implement a typed command factory with one method per command. It must not accept an
arbitrary command name, HTTP method, request body, extra argument collection, or raw
URL. Tests must prove that POST, PUT, PATCH, DELETE, action endpoints, request bodies,
and arbitrary Azure CLI commands cannot be represented.

All processes must:

- use `ProcessStartInfo.ArgumentList`;
- use `UseShellExecute = false`;
- redirect standard output and standard error;
- create no window;
- receive a bounded timeout and cancellation token;
- terminate only the process tree started by Dashboard;
- cap each output stream at 1 MiB and fail when the cap is exceeded;
- omit command output, account JSON, and connection URIs from logs and UI errors.

`az.exe` is the only supported Azure CLI entry point. Do not invoke `az.cmd`,
`cmd.exe`, PowerShell, `explorer.exe`, or a PATH-resolved executable. Require the
fixed 64-bit installation path, file version `>= 2.90.0`, and a valid Authenticode
signature whose signer chains to Microsoft. Unsupported installations produce an
actionable error.

Timeouts:

| Operation | Timeout |
| --- | --- |
| `az account show` | 15 seconds |
| `az rest` | 30 seconds |
| interactive `az login` | 10 minutes |
| shell protocol activation | synchronous `Process.Start`; no wait for app exit |

## Data model and persistence

Add `src\src\AgentSignaler.Service\WindowsAppConnection.cs`:

```csharp
public sealed record WindowsAppConnection(
    Uri DevCenterEndpoint,
    string ProjectName,
    string DevBoxName,
    string AzureAccountUpn,
    Guid AzureTenantId,
    string? LastKnownConnectionUri,
    DateTimeOffset? ConnectionUriRetrievedAtUtc);
```

Add `WindowsAppConnection? WindowsAppConnection` to `MachineView` and to
`MachineStore.StoredMachine`.

Persist it as the existing snapshot's nested camel-case JSON object:

```json
{
  "windowsAppConnection": {
    "devCenterEndpoint": "https://example.region.devcenter.azure.com/",
    "projectName": "project",
    "devBoxName": "example-agent-01",
    "azureAccountUpn": "user@example.com",
    "azureTenantId": "00000000-0000-0000-0000-000000000000",
    "lastKnownConnectionUri": "ms-cloudpc:connect?cpcid=...",
    "connectionUriRetrievedAtUtc": "2026-09-15T02:45:00Z"
  }
}
```

Add these `MachineStore` operations under `_gate`, each using one transaction:

```csharp
Task SetWindowsAppConnectionAsync(
    Guid machineId,
    WindowsAppConnection connection,
    CancellationToken cancellationToken = default);

Task ClearWindowsAppConnectionAsync(
    Guid machineId,
    CancellationToken cancellationToken = default);
```

`SetWindowsAppConnectionAsync` replaces the complete nested object.
`ClearWindowsAppConnectionAsync` sets it to null without removing the machine.
`AcceptAsync`, rename, restart, and status changes preserve it. `RemoveAsync`
removes it with the machine.

Validate on write and deserialize:

- endpoint is absolute HTTPS, has no user info, query, or fragment, has path `/`,
  uses the default port, and its IDN host matches
  `^[a-z0-9-]+\.[a-z0-9-]+\.devcenter\.azure\.com$`;
- project and Dev Box names match
  `^[A-Za-z0-9][A-Za-z0-9._-]{2,62}$`;
- UPN is trimmed, 1-320 characters, contains exactly one non-edge `@`, and has no
  whitespace or control characters;
- tenant ID is non-empty;
- URI and timestamp are both null or both non-null;
- a stored URI passes the connection URI validator below.

Legacy snapshots without `windowsAppConnection` deserialize with null. Invalid
persisted mapping data throws `InvalidDataException` with a recovery message and
must not be silently discarded. Remote status payloads and contracts remain
unchanged.

## Dashboard connection components

Delete `RemoteDesktopLauncher.cs` after moving no reusable code from it. Add:

```text
src\src\AgentSignaler.Dashboard\AzureCliProcess.cs
src\src\AgentSignaler.Dashboard\DevBoxConnectionResolver.cs
src\src\AgentSignaler.Dashboard\WindowsAppLauncher.cs
src\src\AgentSignaler.Dashboard\WindowsAppPlatform.cs
src\src\AgentSignaler.Dashboard\WindowsAppConnectionValidator.cs
```

Use these interfaces:

```csharp
internal interface IAzureCliProcess
{
    Task<AzureCliResult> RunAsync(
        AzureCliCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface IDevBoxConnectionResolver
{
    Task<ResolvedDevBoxConnection> ResolveAsync(
        WindowsAppConnection mapping,
        CancellationToken cancellationToken);
}

internal interface IWindowsAppPlatform
{
    bool IsProtocolAvailable();
    void Launch(Uri connectionUri);
}
```

`ResolvedDevBoxConnection` contains the validated URI, normalized account UPN,
tenant ID, subscription ID, and retrieval time.

### Account validation

Deserialize only the required `az account show` properties:

```json
{
  "id": "<subscription-guid>",
  "state": "Enabled",
  "tenantId": "<tenant-guid>",
  "user": {
    "name": "<upn>",
    "type": "user"
  }
}
```

Require:

- exit code zero;
- valid bounded JSON with no trailing content;
- `state` equal to `Enabled`, case-insensitively;
- non-empty subscription and tenant GUIDs;
- user type `user`;
- UPN equal to the mapping UPN using `OrdinalIgnoreCase`;
- tenant equal to the mapping tenant.

Classify a nonzero result containing Azure CLI's login-required codes/messages as
`SignInRequired`; classify all other failures as `Unavailable`. Do not display raw
output.

### Dev Center request construction

Build the URL from validated typed fields only:

```text
{endpoint}projects/{escaped-project}/users/me/devboxes/{escaped-devbox}/remoteConnection?api-version=2025-02-01
```

Use `Uri.EscapeDataString` for each path segment. Require the final URI to remain
HTTPS on the same host as the configured endpoint and to have no user information
or fragment.

Deserialize a response object containing exactly one required string property,
`cloudPcConnectionUrl`. Ignore no unknown top-level properties: reject them so a
contract change fails visibly. Cap the response at 1 MiB.

### Connection URI validation

Parse with `UriKind.Absolute` and launch the original validated string unchanged.
Require:

- scheme exactly `ms-cloudpc`, case-insensitively;
- URI command/path exactly `connect`;
- no authority, user information, port, or fragment;
- total length from 1 through 4096 characters;
- no control characters or invalid percent escapes;
- exactly one occurrence each of `cpcid`, `username`, `environment`, `version`, and
  `source`;
- no unknown query parameters;
- `cpcid` parses as a non-empty GUID;
- decoded `username` equals the configured Azure account UPN,
  case-insensitively;
- every decoded value is non-empty, at most 512 characters, and contains no control
  characters.

Do not normalize, sort, decode/re-encode, or reconstruct the URI after validation.

### Launch behavior

`WindowsAppPlatform.IsProtocolAvailable` checks merged `HKCR\ms-cloudpc` for the
`URL Protocol` value and a non-empty `shell\open\command`. It must not parse,
execute, trust, or expose the registered command path.

`Launch` uses:

```csharp
Process.Start(new ProcessStartInfo(connectionUri.OriginalString)
{
    UseShellExecute = true
});
```

Treat a null process or `Win32Exception` as activation failure. Dispose a returned
process immediately. Do not wait for Windows App, enumerate its windows, inspect
processes, or attempt focus/reuse behavior.

`WindowsAppLauncher.OpenAsync`:

1. rejects a missing mapping;
2. verifies protocol registration;
3. resolves a fresh URI;
4. atomically persists the refreshed URI, account identity, and retrieval time;
5. launches it;
6. returns success only after `Process.Start` succeeds.

There is no automatic cached fallback. A separate
`OpenLastKnownAsync` validates and launches the stored URI without Azure CLI or
network access and is callable only from the explicit details-dialog button.

## Authentication and details UI

Extend the existing machine-details dialog; do not create a separate settings page.
Add:

- Dev Center endpoint;
- project name;
- Dev Box name;
- Azure account UPN;
- tenant ID;
- connection status;
- last successful refresh time;
- **Save and discover**;
- **Sign in with Azure CLI**;
- **Refresh connection**;
- **Open in Windows App**;
- **Open last known connection**;
- **Clear connection mapping**.

Behavior:

- **Save and discover** validates fields, calls account validation and resolution,
  and saves only after both succeed.
- For the first mapping, save the verified UPN and tenant automatically when they
  exactly match the entered values. Do not add a second confirmation dialog.
- **Sign in with Azure CLI** runs `az login`; include `--tenant` when a valid tenant
  is entered or stored. Do not use `--allow-no-subscriptions`.
- After exit code zero, run `az account show`, require the entered/stored UPN and
  tenant, then automatically retry discovery.
- **Refresh connection** resolves and saves without launching.
- **Open in Windows App** uses the normal fresh-resolution path.
- **Open last known connection** is visible only when a cached URI exists; show its
  local retrieval time beside the button.
- **Clear connection mapping** requires the existing `ContentDialog` destructive
  secondary-button confirmation pattern and clears only the mapping.

While any connection operation runs:

- disable all connection actions and mapping fields;
- show one operation-specific progress message;
- allow cancellation;
- keep the WinUI thread responsive;
- cancel when the details dialog closes or the application exits;
- prevent more than one operation per machine with a keyed in-memory gate.

Closing the sign-in browser does not imply cancellation; cancellation is controlled
by Dashboard's button, dialog closure, timeout, or application exit.

Status values are exactly:

```text
Not configured
Ready
Sign-in required
Unavailable
```

Never show raw Azure CLI output or the connection URI.

## Main window and compact-window behavior

Replace `OpenRemoteDesktop` with one async `OpenWindowsAppAsync` path used by both
the details button and compact tiles.

- Rename **Remote Desktop** to **Open in Windows App**.
- Change compact-tile tooltips and automation names to “Open {machine name} in
  Windows App”.
- Disable a card's launch action while its machine has an active request.
- If a compact tile has no mapping, restore the main window and open that machine's
  details dialog with “Configure a Dev Box connection to continue.”
- If compact launch fails, restore the dashboard and show the classified error.
- If a cached URI exists after a refresh failure, restore/open details so the user
  can explicitly choose **Open last known connection**.
- Never launch cached data directly from compact mode.

Use these user-facing error categories:

- Windows App is not installed or `ms-cloudpc` is not registered.
- Dev Box mapping is missing or invalid.
- Azure CLI is missing, unsupported, signed out, timed out, or using a different
  account or tenant.
- The Dev Box was not found, is unavailable, or access was denied.
- The Dev Center API was unavailable or returned malformed data.
- The service returned an unsafe or unsupported connection URI.
- Windows rejected protocol activation.

## Installer implementation

Add:

```text
src\installers\AgentSignaler.Dashboard.Bundle\AgentSignaler.Dashboard.Bundle.wixproj
src\installers\AgentSignaler.Dashboard.Bundle\Bundle.wxs
src\installers\AgentSignaler.Dashboard.Bundle\Prerequisites.props
src\installers\AgentSignaler.DevTunnelsPrerequisite\AgentSignaler.DevTunnelsPrerequisite.wixproj
src\installers\AgentSignaler.DevTunnelsPrerequisite\Package.wxs
src\scripts\Update-PrerequisiteMetadata.ps1
```

Use WiX Burn 4.0.6 and add the bundle project to `AgentSignaler.slnx` with ordinary
solution build disabled, matching the MSI projects.

`Prerequisites.props` is the single source of truth for:

- Azure CLI version `2.90.0`;
- versioned URL
  `https://azcliprod.blob.core.windows.net/msi/azure-cli-2.90.0-x64.msi`;
- Azure CLI SHA-256;
- Azure CLI product code and Microsoft signer requirement;
- Dev Tunnels version equal to `TunnelValidation.SupportedCliVersion`;
- versioned Microsoft download URL resolved from the official
  `https://aka.ms/TunnelsCliDownload/win-x64` redirect;
- Dev Tunnels SHA-256 and Microsoft signer requirement;
- Windows App minimum version `2.0.804.0`;
- Microsoft Store URI for Windows App.

`Update-PrerequisiteMetadata.ps1` downloads to a temporary directory, follows only
HTTPS redirects whose final host is Microsoft-owned, verifies Authenticode before
hashing, writes exact version/URL/hash values to `Prerequisites.props`, and always
deletes temporary files. The normal build never updates this file automatically.

Because Microsoft distributes the Windows Dev Tunnels CLI as a signed executable
rather than an MSI, build a per-user prerequisite MSI containing only the pinned
`devtunnel.exe`. Install it to
`%LOCALAPPDATA%\Programs\Microsoft Dev Tunnels CLI\devtunnel.exe`, add that exact
path to `CliTunnelController` discovery after its existing explicit-path and WinGet
locations, and mark the package permanent in the Burn chain. The prerequisite MSI
has its own stable upgrade code, supports major upgrades, and never removes the CLI
during Dashboard bundle uninstall. It must not create credentials, run
`devtunnel user login`, or modify PATH.

The bundle:

- detects the 64-bit Azure CLI by MSI product metadata and then verifies
  `%ProgramFiles%\Microsoft SDKs\Azure\CLI2\wbin\az.exe`;
- detects Dev Tunnels through the existing WinGet link path or the prerequisite-MSI
  path and validates it with the existing `TunnelValidation` rules;
- detects Windows App package version and `ms-cloudpc` registration;
- shows Installed, Update required, or Missing;
- preselects Azure CLI and Dev Tunnels installation when missing/outdated;
- allows the user to decline either and continue after one explicit warning;
- provides a Microsoft Store action for Windows App instead of chaining Store
  installation;
- chains prerequisites before the existing per-user Dashboard MSI;
- re-detects accepted prerequisites before MSI execution;
- never signs in, signs out, removes, or downgrades shared prerequisites;
- propagates `0`, `3010`, cancellation, download, verification, and package failure
  outcomes deterministically;
- logs product, version, and result only.

Quiet mode installs the pinned Azure CLI and Dev Tunnels packages and the Dashboard
MSI without prompts. Supplying `ACCEPT_PREREQUISITE_LICENSES=1` is mandatory in
quiet mode; otherwise exit before download with a documented nonzero code. Passive
mode shows progress and uses the same property requirement. Interactive mode shows
license links and records acceptance in Burn variables only for the current run.

Keep `AgentSignaler.Dashboard.msi` independently buildable. Add read-only detection
properties and warnings to its log; do not add nested installers or prerequisite
custom actions.

Update `Build-Installers.ps1` to produce:

```text
artifacts\msi\AgentSignaler.Dashboard.msi
artifacts\msi\AgentSignaler.Remote.msi
artifacts\bundle\AgentSignaler.Dashboard.Setup.exe
```

Build the bundle after both MSIs pass existing validation. Do not install or copy
the bundle during tests. Add `Test-DashboardBundle.ps1` to inspect bundle metadata
without executing it.

## Automated test requirements

### Service tests

Extend `src\tests\AgentSignaler.Service.Tests\StoreTests.cs`:

- legacy snapshot loads with null mapping;
- set, replace, clear, rename, status update, restart, and remove have the specified
  behavior;
- delayed status cannot overwrite the mapping;
- malformed endpoint, names, UPN, tenant, timestamp/URI pair, or cached URI throws;
- persisted malformed metadata fails explicitly.

### Dashboard integration tests

Replace `RemoteDesktopLauncherTests.cs` with:

```text
AzureCliProcessTests.cs
DevBoxConnectionResolverTests.cs
WindowsAppConnectionValidatorTests.cs
WindowsAppLauncherTests.cs
```

Link the new dashboard source files in
`AgentSignaler.Integration.Tests.csproj`, replacing the old launcher link.

Cover:

- exact supported command construction;
- rejection of all unsupported command and HTTP method shapes;
- executable path, signer, architecture, and version checks;
- output caps, timeout, cancellation, process-tree termination, and nonzero exit;
- account JSON and account/tenant/subscription mismatch cases;
- URL escaping and same-origin Dev Center request construction;
- 200, 401, 403, 404, 408, 429, 5xx, malformed JSON, oversized JSON, missing
  property, and unknown property responses;
- every connection URI rule;
- launch unchanged after validation;
- missing protocol and shell activation failures;
- fresh resolution and persistence before launch;
- no launch or mapping mutation after any failure;
- explicit cached launch without resolver access;
- keyed duplicate suppression;
- absence of URI and raw CLI output in exceptions and presentation strings.

Use an injected process runner; no test may use the developer's Azure CLI session.

### UI tests

Keep UI logic testable through extracted presentation/state helpers and fakes:

- details and compact actions use the same async launch method;
- missing mapping restores Dashboard and opens the correct details dialog;
- busy state disables all conflicting controls;
- close/exit cancels work;
- sign-in success verifies the account and retries discovery;
- failed/cancelled/mismatched sign-in never saves or launches;
- cached launch is explicit and unavailable from compact mode;
- labels, tooltips, and automation names mention Windows App.

### Installer tests

Verify without installing:

- bundle is x64 and chains Azure CLI, Dev Tunnels, then Dashboard MSI;
- pinned versions, URLs, hashes, signer constraints, and detection conditions match
  `Prerequisites.props`;
- quiet/passive license property behavior is encoded;
- Windows App is detect/store-link only;
- direct MSI contains detection/warning logic and no nested installation action;
- repair/upgrade cannot downgrade newer prerequisites;
- uninstall does not remove prerequisites;
- bundle logs exclude account, token, URI, and credential fields.

## Documentation changes

Update during the same implementation:

- `src\README.md`: replace mstsc behavior with explicit Dev Box mapping, supported
  versions, Azure CLI sign-in, fresh resolution, explicit cached launch, and no
  fallback.
- `src\docs\MANUAL-TEST-PLAN.md`: replace the mstsc case with Windows App cases.
- `src\installers\README.md`: make the bundle the preferred entry point; document
  direct-MSI behavior, quiet/passive license property, prerequisite retention, and
  bundle output.

The documented servicing policy is fixed: mappings and cached URIs remain in the
per-user database across repair, upgrade, and uninstall. A later reinstall reuses
them. Removing a machine or selecting **Clear connection mapping** is the only
product action that deletes a mapping.

## Execution phases

### Phase 1 - persistence

1. Add `WindowsAppConnection`.
2. Add validation shared by store writes and reads.
3. Extend `StoredMachine` and `MachineView`.
4. Add set/clear operations.
5. Add and run targeted service tests.

Exit command:

```powershell
dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj -p:Platform=x64 --filter FullyQualifiedName~StoreTests
```

### Phase 2 - resolver and launcher

1. Add typed Azure CLI commands and bounded process execution.
2. Add account parsing and validation.
3. Add Dev Center URL construction and response parsing.
4. Add strict connection URI validation.
5. Add protocol detection and shell launch.
6. Add launcher orchestration and cached-launch method.
7. Replace launcher tests and update test project links.

Exit command:

```powershell
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~AzureCliProcessTests|FullyQualifiedName~DevBoxConnectionResolverTests|FullyQualifiedName~WindowsAppConnectionValidatorTests|FullyQualifiedName~WindowsAppLauncherTests"
```

### Phase 3 - dashboard UX

1. Replace synchronous RDP fields and methods in `MainWindow`.
2. Make compact callbacks asynchronous and duplicate-safe.
3. Extend details UI with mapping, authentication, refresh, launch, cached launch,
   clear, busy, cancellation, and errors.
4. Remove all mstsc/window-enumeration code and text.
5. Add presentation/state tests.

Exit commands:

```powershell
dotnet build src\AgentSignaler.Dashboard\AgentSignaler.Dashboard.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64
```

### Phase 4 - installer

1. Add prerequisite metadata and update script.
2. Add Burn bundle project and source.
3. Add direct-MSI detection/warnings.
4. Extend build and static installer validation scripts.
5. Update installer documentation.
6. Make local build and validation the default; copy artifacts only when an
   explicit trusted destination is supplied.

Exit command:

```powershell
.\scripts\Build-Installers.ps1 -Version 1.0.5
```

The command must build and inspect artifacts only and must not require a redirected
drive.

### Phase 5 - complete verification

Run:

```powershell
dotnet build AgentSignaler.slnx -p:Platform=x64
dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Remote.Tests\AgentSignaler.Remote.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Tunneling.Tests\AgentSignaler.Tunneling.Tests.csproj -p:Platform=x64
.\scripts\Build-Installers.ps1 -Version 1.0.5
```

Search the source, tests, and documentation and require zero remaining product
references to `mstsc.exe`, `RemoteDesktopLauncher`, `IRemoteDesktopPlatform`,
`RemoteDesktopWindow`, `MatchesHost`, or user-facing “Remote Desktop” text, except
historical release notes if any.

## Non-blocking release acceptance

After automated implementation is complete, release owners may validate on a
disposable Windows 11 x64 machine with Windows App 2.0.804.0 or later and Azure CLI
2.90.0 or later:

1. configure a card whose hostname differs from its Dev Box name;
2. launch from details and compact views;
3. verify existing-session behavior is delegated to Windows App;
4. exercise successful, cancelled, timed-out, expired, and wrong-account sign-in;
5. exercise missing protocol, offline network, API failures, malformed responses,
   and explicit cached launch;
6. review captured process arguments and confirm GET-only Azure requests;
7. install, repair, upgrade, and uninstall the bundle and direct MSI;
8. confirm mappings persist and shared prerequisites/credentials remain untouched;
9. confirm no `mstsc.exe` process starts.

These checks may block release, but they do not block autonomous implementation.
Do not modify a Dev Box or Azure resource to create a test condition.

## Completion criteria

- A configured card opens its explicit Dev Box through a freshly resolved,
  validated `ms-cloudpc:connect` URI.
- Missing configuration opens an actionable details flow.
- Cached launch is explicit, timestamped, and never automatic.
- Existing snapshots remain compatible and remote reports cannot overwrite mapping
  data.
- Azure authentication remains inside Microsoft Azure CLI.
- Every Dev Center request is GET-only and targets the configured public-Azure
  developer endpoint.
- Unsafe responses, missing prerequisites, authentication failures, and activation
  failures are explicit and never success-shaped.
- No passwords, tokens, cookies, authorization headers, raw CLI JSON, or connection
  URIs are logged or included in hook traffic.
- The bundle and direct MSI follow the fixed prerequisite and servicing rules.
- No product path invokes or falls back to classic Remote Desktop Connection.

## References

- [Connect to a Dev Box with Windows App](https://github.com/MicrosoftDocs/azure-docs/blob/main/articles/dev-box/includes/connect-with-windows-app.md)
- [Dev Boxes - Get Remote Connection](https://learn.microsoft.com/en-us/rest/api/devcenter/developer/dev-boxes/get-remote-connection?view=rest-devcenter-developer-2025-02-01)
- [Install Azure CLI on Windows](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli-windows)
- [Azure Virtual Desktop URI schemes](https://learn.microsoft.com/en-us/azure/virtual-desktop/uri-scheme)
- [Windows App overview](https://learn.microsoft.com/en-us/windows-app/overview)
