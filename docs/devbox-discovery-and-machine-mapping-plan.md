# Dev Box Discovery and Machine Mapping Implementation Plan

The [existing-session reuse plan](windows-app-existing-session-reuse-plan.md)
supersedes the fresh-resolution requirement below only when a boundary-safe title
match can be restored and foregrounded. With no match, normal launch still resolves
and saves fresh data; catalog discovery and mapping rules are unchanged.

## Automatic discovery supersedes endpoint management

The current implementation follows the September 15, 2026 request to discover
both Dev Centers and Dev Boxes through Azure CLI, without asking users to add or
manage Dev Centers. The manual-endpoint design below is historical, not the current
Settings or refresh contract.

- Settings and machine details invoke the same parameterless, explicitly requested
  catalog refresh. There is no endpoint editor, endpoint seeding, or Settings detour.
- Refresh validates the current CLI user and tenant, enumerates the user's enabled
  public-Azure subscriptions in that tenant, and uses GET-only Azure Resource
  Manager discovery to obtain validated Dev Center endpoints. It then lists the
  assigned Dev Boxes using the existing bounded Dev Center data-plane requests.
- The CLI account/subscription/tenant/cloud is never switched by Dashboard.
  No CLI extension is installed. Hidden resources cannot be discovered.
- ARM Dev Center read access is required. Dev Box User access alone may be
  insufficient; permission failures are explicit rather than falling back to
  manual endpoints, silently skipping failed scopes, or publishing partial results.
- Subscription, resource, page, endpoint, item, time, and output limits are enforced.
  Pagination is origin-validated before every request; ARM pagination also stays
  inside the searched subscription/resource-list scope.
- Discovered centers and Dev Boxes remain in memory. Legacy `DevCenterEndpoints`
  settings are ignored and omitted on the next explicit Settings Save; other
  preferences and persisted machine mappings remain compatible.
- Existing picker identity, explicit atomic Save mapping, fresh launch, optional
  explicit cached launch, cancellation/cleanup, account privacy, and snapshot
  retention rules remain in force. No periodic or launch-triggered catalog refresh
  is introduced.

See `src\README.md` and `src\docs\MANUAL-TEST-PLAN.md` for the current user workflow
and acceptance cases. The following records describe the preceding implementation
and its validation at that time.

## Implementation and validation status

All five implementation phases are complete: normalized endpoint settings, shared
bounded discovery, the Dev Box Settings tab and cancellation lifecycle, explicit
machine picker/mapping and tile presentation, compatibility tests, and documentation.
The README and manual test plan describe the implemented workflow; the Windows App
migration plan points here instead of its former manual mapping flow.

Automated verification on September 15, 2026:

- Every phase's targeted test command passed.
- Full Service tests: 352 passed.
- Full Remote tests: 152 passed.
- Full Integration tests: 810 passed, one opt-in live-tunnel test skipped.
- Full Tunneling tests: 145 passed.
- Dashboard and solution Release x64 builds passed. The solution reports one
  existing xUnit2031 analyzer warning in `RemoteTests.cs`.
- Both requested default Debug builds were attempted, but copying Dashboard
  dependencies was blocked by the running Dashboard/debugger holding DLLs open
  (MSB3021/MSB3027). The Visual Studio build tool also reported the active debugging
  session. No user processes were stopped and the running app was not updated;
  end debugging and rebuild/restart to load the changes.

The live Azure, Windows App, installer, accessibility, and manual release checks
below remain not run. Automated tests use isolated fakes; no Azure account was
accessed and no cloud resources were changed.

## Goal

Replace manual entry of Dev Box mapping fields with a shared catalog of Dev Boxes
available to the Azure CLI account. The dashboard must let the user refresh that
catalog from Settings and choose a catalog entry for each machine tile.

The existing Windows App launch contract remains unchanged: selecting a Dev Box
stores an explicit machine-to-Dev-Box mapping, and every normal launch resolves a
fresh, validated `ms-cloudpc:connect` URI before activating Windows App.

## Scope and discovery boundary

The Dev Center developer API lists Dev Boxes within a specific Dev Center. A user
with only Dev Box User access might not have Azure Resource Manager permission to
enumerate every Dev Center in a tenant or subscription. Therefore, "all Dev Boxes"
means the union of Dev Boxes returned by every Dev Center endpoint configured in
Dashboard Settings.

Support one or more Dev Center endpoints. This handles users assigned to multiple
Dev Centers without relying on subscription-level resource discovery.

Do not:

- require Azure subscription Reader access;
- infer a Dev Box from a reported hostname or display name;
- automatically change an existing machine mapping based on matching names;
- create, start, stop, restart, delete, or otherwise mutate a Dev Box;
- persist remote-connection URIs in the shared catalog;
- log Azure CLI output, account identifiers, Dev Box URIs, or connection URIs.

## Existing implementation to extend

- `src\src\AgentSignaler.Dashboard\DashboardSettings.cs` persists the Azure CLI
  path but has no Dev Center catalog settings.
- `src\src\AgentSignaler.Dashboard\AzureCliProcess.cs` provides bounded,
  cancellable Azure CLI execution and typed `az account show` and `az rest`
  commands.
- `src\src\AgentSignaler.Dashboard\DevBoxConnectionResolver.cs` validates the
  active Azure account and resolves one saved mapping.
- `src\src\AgentSignaler.Service\WindowsAppConnection.cs` stores an explicit Dev
  Center endpoint, project, Dev Box name, UPN, tenant, and last known connection.
- `src\src\AgentSignaler.Dashboard\WindowsAppConnectionController.cs` owns
  per-machine mapping, refresh, launch, cancellation, and busy state.
- `src\src\AgentSignaler.Dashboard\MainWindow.cs` contains a tabbed Settings
  dialog and manual Dev Box fields in the machine-details dialog.
- Main dashboard tiles open machine details. Compact tiles launch the configured
  mapping and restore machine details when no valid mapping exists.

This plan supersedes the manual mapping-field workflow in
`docs\windows-app-connection-migration-plan.md`; its validation, fresh-resolution,
explicit cached-launch, cancellation, installer, and security requirements remain
in force.

## Settings model

Extend `DashboardSettings` with:

```csharp
public IReadOnlyList<Uri> DevCenterEndpoints { get; init; } = [];
```

Persist endpoints as normalized absolute HTTPS root URIs. Reuse the endpoint
validation rules from `WindowsAppConnection`:

- HTTPS only;
- default port;
- no user information, query, or fragment;
- path exactly `/`;
- public Azure Dev Center hostname;
- no whitespace or control characters;
- no duplicates after IDN-host and URI normalization.

The Azure CLI path is a Dev Box dependency. Move its controls from the standalone
**Azure CLI** Settings tab into a new **Dev Box** tab. The new tab owns:

- optional trusted Azure CLI path;
- Azure CLI installation/version/sign-in status;
- editable list of Dev Center endpoints;
- **Add Dev Center** and **Remove** actions;
- **Refresh Dev Boxes** and **Cancel refresh** actions;
- last successful refresh time;
- account display showing the validated UPN and tenant;
- refresh status and classified error text;
- the discovered Dev Box list.

Changing the Azure CLI path still requires an application restart because the
running connection services capture the path during initialization. Editing Dev
Center endpoints does not require restart. Save valid endpoint changes when the
Settings dialog is saved; refreshing uses the endpoints currently entered in the
dialog so the user can validate changes before saving. Catalog refresh uses the
running CLI path; only the CLI status test uses the currently entered path.

If endpoints are absent, seed the Settings editor from distinct valid endpoints in
existing machine mappings. Do not silently save seeded values until the user
selects **Save**.

## Catalog model

Add dashboard-only immutable models:

```csharp
internal sealed record DevBoxCatalogItem(
    Uri DevCenterEndpoint,
    string ProjectName,
    string PoolName,
    string DevBoxName,
    string PowerState,
    string ProvisioningState,
    string? OperatingSystem,
    int? VCpus,
    int? MemoryGb,
    Guid? UniqueId);

internal sealed record DevBoxCatalogSnapshot(
    IReadOnlyList<DevBoxCatalogItem> Items,
    string AzureAccountUpn,
    Guid AzureTenantId,
    DateTimeOffset RetrievedAtUtc);
```

Catalog identity is the tuple of normalized endpoint, project name, and Dev Box
name. Sort display entries by Dev Box name, project, then endpoint host using
case-insensitive ordinal comparison. Reject duplicate identities in one endpoint
response. Merge different endpoints without collapsing entries that happen to
share names.

Keep the catalog in memory for the running dashboard process. Machine mappings are
already persisted independently, so catalog failure or expiration must not remove
or rewrite mappings. Do not persist the complete CLI response.

## Azure CLI discovery

Add:

```text
src\src\AgentSignaler.Dashboard\DevBoxCatalogService.cs
src\src\AgentSignaler.Dashboard\DevBoxCatalogController.cs
```

Use `IAzureCliProcess`; do not add a second process runner.

`DevBoxCatalogService.RefreshAsync`:

1. Validate and normalize all entered Dev Center endpoints before invoking Azure
   CLI.
2. Run the existing bounded `az account show --output json` command once.
3. Require an enabled interactive user account, non-empty subscription and tenant
   IDs, and a valid UPN. Subscription ID is evidence that the selected CLI context
   is usable, not the scope used to discover Dev Centers.
4. For each configured endpoint, issue a GET-only command:

   ```text
   az rest --method get
     --resource https://devcenter.azure.com
     --url {endpoint}devboxes?api-version=2025-02-01
   ```

5. Follow service-provided pagination only when `nextLink` is an absolute HTTPS
   URI on the same validated endpoint host. Cap pages and total items to prevent
   unbounded work.
6. Parse only the fields needed by `DevBoxCatalogItem`. Accept documented
   additional properties, but require the response envelope and required identity
   fields to have the expected types.
7. Validate each item URI, when present, against its configured endpoint and
   require its project and Dev Box path segments to agree with the parsed fields.
8. Return one atomic snapshot only after every configured endpoint succeeds.

Use at most 20 endpoints, a 30-second timeout per service request, the existing
15-second account-check timeout, a maximum of 20 pages per endpoint, 250 items
per endpoint, and the existing 1 MiB limit per CLI output stream.

Do not return a partial catalog as a successful refresh. On failure or an empty
result, retain the previous in-memory snapshot and all saved mappings. Distinguish
an empty result from a failure; show which configured endpoint failed without
including raw CLI output or account identifiers.

## Discovery state and lifecycle

`DevBoxCatalogController` owns:

- the current successful snapshot;
- refresh operation state;
- cancellation;
- status and safe error presentation;
- change notifications consumed by Settings and machine details.

Allow only one catalog refresh at a time. Disable endpoint editing, Azure CLI
testing, Settings Save, and additional refresh requests while refresh is running.
Cancel refresh and closing Settings cancel and await refresh cleanup. Dashboard exit cancels and awaits
catalog refresh before disposing shared services.

Never refresh automatically or periodically in the background. Refresh only when
the user selects **Refresh Dev Boxes** in Settings or **Refresh Dev Box list** in
machine details. Opening a picker or launching a mapped machine never refreshes
the catalog. The details refresh action navigates to **Settings > Dev Box** when
no endpoints are configured.

## Dev Box Settings experience

The **Dev Box** Settings tab shows:

1. **Azure CLI**
   - path;
   - current installation and sign-in test;
   - existing trusted-installation warning.
2. **Dev Centers**
   - endpoint editor;
   - add/remove controls;
   - explanation that all configured centers are queried and subscription Reader
     permission is not required.
3. **Available Dev Boxes**
   - **Refresh Dev Boxes** button;
   - last successful refresh in local time;
   - account and tenant used for the successful refresh;
   - count and list of discovered Dev Boxes;
   - name, project, pool, power state, and endpoint host for each entry.

The list is informational in Settings. Machine mapping is performed from machine
details so the target machine is unambiguous.

Empty and error states must distinguish:

- no Dev Center endpoints configured;
- Azure CLI unavailable or unsupported;
- sign-in required;
- endpoint access denied or Dev Center unavailable;
- successful refresh with no assigned Dev Boxes;
- malformed or unsafe service response;
- cancelled or timed-out refresh.

## Machine tile and mapping experience

Keep the existing main tile activation behavior: selecting a tile opens that
machine's details. Update each full-size tile to show:

- `Dev Box: <name>` when mapped;
- `Dev Box: Not mapped` when no mapping exists;
- a visible mapping status icon or text independent of color.

In machine details, replace the five manual mapping text boxes with:

- a **Mapped Dev Box** ComboBox populated from the current catalog;
- a read-only summary of endpoint host, project, pool, and last connection refresh,
  referring the user to Settings for identity without exposing UPN or tenant;
- **Refresh Dev Box list**, which invokes the shared catalog controller;
- **Save mapping**;
- existing **Sign in with Azure CLI**, **Refresh connection**, **Open in Windows
  App**, **Open last known connection**, and **Clear connection mapping** actions.

Picker behavior:

- identify the current selection by endpoint, project, and Dev Box name;
- retain and display a saved mapping that is absent from the current catalog as
  **Unavailable: `<name>`**, without clearing it;
- require an explicit **Save mapping** action to replace a mapping;
- create the new `WindowsAppConnection` from the selected catalog identity and the
  catalog's validated UPN and tenant;
- resolve the selected Dev Box and persist the mapping only after remote connection
  discovery succeeds;
- preserve the old mapping if validation, resolution, or persistence fails;
- never select or save a mapping automatically based on machine hostname, display
  name, note, pool, or list order.

**Sign in with Azure CLI** only signs in and validates identity, or refreshes an
already stored mapping. It never saves an unsaved picker selection. Only explicit
**Save mapping** replaces a mapping, after fresh account validation and connection
resolution succeed. Cancel/close/exit await owned connection-operation cleanup.

If the catalog has not been refreshed, show an empty picker and an actionable
**Refresh Dev Box list** button. If Settings has no Dev Center endpoint, navigate
the user to the **Dev Box** Settings tab rather than showing manual fields.

Compact tiles remain launch-only. A compact tile with no mapping restores the main
window and opens machine details at the Dev Box picker. Compact tiles do not show a
picker and never launch the first or name-matched catalog item automatically.

## Mapping controller changes

Replace `WindowsAppMappingFields` input for new mappings with a selected
`DevBoxCatalogItem` plus the current catalog identity:

```csharp
internal sealed record DevBoxMappingSelection(
    DevBoxCatalogItem DevBox,
    string AzureAccountUpn,
    Guid AzureTenantId);
```

Add a `Map` operation to `WindowsAppOperation`. The operation:

1. validates the catalog item and catalog identity;
2. creates a candidate mapping without cached connection data;
3. resolves a fresh connection through `IDevBoxConnectionResolver`;
4. verifies returned account and tenant;
5. persists the complete refreshed mapping atomically;
6. updates UI state only after persistence succeeds.

Retain the existing keyed per-machine operation gate. Catalog refresh has a
separate global gate. A catalog refresh may run while an already mapped machine is
opened because launch reads only its persisted mapping. Disable **Save mapping**
while either that machine or the global catalog is busy.

## Compatibility and migration

- Existing `WindowsAppConnection` records remain valid without database migration.
- Existing mappings appear as selected when their identity exists in the catalog.
- Existing mappings absent from the catalog remain launchable and refreshable
  under current rules until the user changes or clears them.
- Remove manual endpoint, project, Dev Box, UPN, and tenant editors only after the
  picker flow is complete.
- Keep the current explicit last-known connection behavior and timestamp.
- Remote webhook contracts, machine IDs, status reports, and compact layout
  persistence are unchanged.

## Security and privacy requirements

- Every discovery request is GET-only and uses the Dev Center data-plane resource.
- Validate endpoint origins and pagination links before invoking Azure CLI.
- Pass CLI arguments through `ProcessStartInfo.ArgumentList`; never use a shell.
- Bound process time, output size, page count, endpoint count, and item count.
- Never display or log raw JSON, bearer tokens, subscription IDs, UPNs in global
  error banners, Dev Box item URIs, or any `ms-cloudpc`, `ms-avd`, or web URL.
- Settings may display the validated signed-in UPN and tenant only inside the local
  Dev Box tab because the user explicitly requested account discovery.
- Do not write catalog data into webhook traffic or expose it through the local
  dashboard receiver.
- Preserve the existing trusted Azure CLI path validation and version check.

## File-level implementation outline

### Dashboard

- `DashboardSettings.cs`
  - add and validate Dev Center endpoints;
  - preserve legacy settings compatibility.
- `AzureCliProcess.cs`
  - add typed list and paginated-list GET commands;
  - keep arguments and output withheld from `ToString`.
- `DevBoxCatalogService.cs`
  - add strict account, list envelope, item, URI, and pagination parsing.
- `DevBoxCatalogController.cs`
  - add shared snapshot, busy state, cancellation, errors, and notifications.
- `DevBoxConnectionResolver.cs`
  - extract reusable account parsing/validation to avoid duplicate rules.
- `WindowsAppConnectionController.cs`
  - add selection-based `Map`;
  - remove manual field dependency after migration.
- `MainWindow.cs`
  - initialize and stop the catalog controller;
  - add the Dev Box Settings tab;
  - add tile mapping labels;
  - replace manual details fields with the picker and mapping summary.
- `MainWindow.AzureCli.cs`
  - move Azure CLI controls into the Dev Box section;
  - coordinate test and catalog-refresh busy states.
- `CompactWindow.cs`
  - preserve launch behavior and route missing mappings to the picker.

### Service

No schema change is required. Add shared validation helpers to
`WindowsAppConnection.cs` only if needed by catalog parsing; keep public persisted
shape compatibility.

### Tests

Add:

```text
tests\AgentSignaler.Integration.Tests\DevBoxCatalogServiceTests.cs
tests\AgentSignaler.Integration.Tests\DevBoxCatalogControllerTests.cs
tests\AgentSignaler.Integration.Tests\DevBoxMappingPresentationTests.cs
```

Extend:

```text
tests\AgentSignaler.Integration.Tests\AzureCliProcessTests.cs
tests\AgentSignaler.Integration.Tests\WindowsAppConnectionControllerTests.cs
tests\AgentSignaler.Service.Tests\StoreTests.cs
```

## Automated test requirements

Cover:

- settings round-trip, legacy files, endpoint normalization, duplicate rejection,
  and invalid endpoint recovery;
- exact GET-only Azure CLI commands and argument boundaries;
- one and multiple endpoints;
- pagination success and same-origin enforcement;
- page, item, time, and output limits;
- malformed envelopes and fields;
- duplicate identities;
- account sign-in, account type, and tenant validation;
- cancellation before, between, and during CLI requests;
- atomic snapshot replacement and retention after failed refresh;
- safe error messages with no raw output or connection URLs;
- picker sorting and identity matching;
- existing mapping present and absent from the catalog;
- explicit mapping replacement with rollback on resolution or persistence failure;
- no hostname-based automatic mapping;
- machine and global busy-state interaction;
- compact missing-mapping navigation;
- shutdown cancellation and cleanup;
- persistence compatibility for existing mappings.

## Implementation phases

### Phase 1 - settings and catalog contracts

1. Add Dev Center endpoint settings and validation.
2. Add catalog records and presentation helpers.
3. Add settings and validation tests.

Exit command:

```powershell
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~DashboardSettings|FullyQualifiedName~DevBoxCatalog"
```

### Phase 2 - discovery service

1. Extract shared Azure account validation.
2. Add list and pagination commands.
3. Implement bounded multi-endpoint catalog refresh.
4. Add parser, command, failure, and cancellation tests.

Exit command:

```powershell
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~AzureCliProcessTests|FullyQualifiedName~DevBoxCatalogServiceTests"
```

### Phase 3 - shared catalog controller and Settings

1. Add controller state, cancellation, and notifications.
2. Add the Dev Box Settings tab.
3. Move Azure CLI settings into that tab.
4. Add endpoint editing, refresh, list, and error states.
5. Ensure dialog closure and application exit await cancellation.

Exit command:

```powershell
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~DevBoxCatalogControllerTests|FullyQualifiedName~AzureCliDiagnosticsTests"
dotnet build src\AgentSignaler.Dashboard\AgentSignaler.Dashboard.csproj -p:Platform=x64
```

### Phase 4 - machine picker and tiles

1. Add tile mapping presentation.
2. Replace manual mapping fields with the catalog picker.
3. Add selection-based atomic mapping.
4. Preserve unavailable saved mappings and compact navigation behavior.
5. Add mapping, presentation, and controller tests.

Exit command:

```powershell
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~WindowsAppConnectionControllerTests|FullyQualifiedName~DevBoxMappingPresentationTests"
dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj -p:Platform=x64 --filter FullyQualifiedName~StoreTests
```

### Phase 5 - documentation and complete verification

Update:

- `src\README.md`;
- `src\docs\MANUAL-TEST-PLAN.md`;
- `docs\windows-app-connection-migration-plan.md` with a pointer to the completed
  discovery flow.

Run:

```powershell
dotnet build AgentSignaler.slnx -p:Platform=x64
dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Remote.Tests\AgentSignaler.Remote.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Tunneling.Tests\AgentSignaler.Tunneling.Tests.csproj -p:Platform=x64
```

## Manual verification

On a disposable Windows 11 x64 machine with supported Windows App and Azure CLI:

1. configure one Dev Center and refresh a multi-item catalog;
2. configure two Dev Centers and verify merged, deterministic ordering;
3. map multiple dashboard machines to different Dev Boxes;
4. map two machines to the same Dev Box and verify the explicit choice is
   preserved;
5. verify a machine hostname different from its Dev Box name is never
   auto-matched;
6. launch mapped machines from details and compact tiles;
7. remove catalog access and verify mappings remain intact and visible as
   unavailable;
8. exercise wrong account, expired sign-in, access denied, offline, malformed,
   timeout, cancellation, and pagination failures;
9. close Settings and exit Dashboard during refresh and verify owned CLI
   processes are stopped;
10. inspect logs and UI errors for account, URI, token, and raw JSON leakage.

## Completion criteria

- Settings has one Dev Box section containing every Azure CLI and Dev Box catalog
  setting.
- The user can explicitly refresh the union of assigned Dev Boxes across all
  configured Dev Centers.
- Each full-size machine tile shows its mapping state and opens a picker capable
  of selecting one discovered Dev Box.
- Mapping replacement is explicit, validated, freshly resolved, and atomic.
- No hostname or display-name heuristic chooses a Dev Box.
- Failed or empty refreshes never clear existing mappings or a previous successful
  catalog.
- Existing mappings and cached explicit-launch behavior remain compatible.
- Compact tiles remain safe launch-only controls.
- Discovery is bounded, cancellable, GET-only, origin-validated, and free of
  sensitive logging.
