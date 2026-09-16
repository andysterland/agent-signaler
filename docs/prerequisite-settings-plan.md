# Prerequisite Settings Tab Plan

## Goal

Add a fifth Settings tab named **Prerequisite** to centralize required-component
setup and readiness checks, while keeping operational controls in their existing
tabs.

Dev Tunnels CLI is separate from Azure CLI. The Azure CLI extension used for
Dev Box discovery by Dev Center name is `devcenter`, not Dev Tunnels.

## Tab contents

| Component | Prerequisite tab contents |
|---|---|
| Dev Tunnels CLI | Executable path, installation/version/signature checks, account status, and setup guidance |
| Azure CLI | Executable path, existing installation/version/sign-in test, and cancellation |
| Azure CLI `devcenter` extension | Explicit availability check; required only for discovery by Dev Center name |
| Windows App | `ms-cloudpc` registration check and installation guidance; distinguish protocol availability from verified app version |

## Implementation steps

1. **Separate prerequisite UI from feature controls.**
   Extract Azure CLI setup from `src\AgentSignaler.Dashboard\MainWindow.DevBox.cs`
   and `MainWindow.AzureCli.cs`, and Dev Tunnels setup/checks from
   `MainWindow.Tunneling.cs`, into a new `MainWindow.Prerequisites.cs`.
   Add the tab in `MainWindow.cs`. Keep sharing start/stop/delete/sign-out under
   **Internet sharing**, discovery under **Dev Box**, and firewall controls under
   **Network**.

2. **Reuse existing diagnostics safely.**
   Retain `AzureCliDiagnostics` and reuse the Windows App association probe in
   `WindowsAppPlatform`. Add extension detection without installing anything.
   Separate Dev Tunnels diagnostics from the live sharing controller:
   `CliTunnelController.CheckAccountAsync` can stop hosting on failure, so it must
   not become an unrestricted prerequisite check.

3. **Provide consistent results.**
   Show each component's status, checked path/version where available, actionable
   failure details, and individual Check/Cancel controls. Checks must not install
   components, sign in, launch Windows App, or change sharing. Allow inspection
   before Internet mode is enabled. Do not present Azure CLI saved-account
   validation as proof of live token validity or Dev Box permissions.

4. **Preserve Settings behavior.**
   Keep existing saved fields and restart requirements. Route invalid CLI paths
   to **Prerequisite**. Clearly distinguish paths being tested from effective
   running paths. Preserve edits/results across tab switches and cancel and await
   diagnostic work when Settings closes. Preserve coordination with Dev Box
   discovery and the existing Save behavior.

5. **Verify and document.**
   Cover missing/unsupported components, signed-out accounts, missing
   extension/protocol, cancellation, unsaved paths, and checks during active
   sharing. Verify five-tab layout and keyboard navigation. Update README
   navigation and prerequisite guidance.

## Acceptance criteria

- Settings contains a fifth tab with the exact label **Prerequisite**.
- Component paths, explicit readiness checks, and setup guidance are centralized
  there rather than duplicated across feature tabs.
- Sharing operations, Dev Box discovery, and firewall controls remain in their
  existing tabs.
- Checks provide actionable results without installing software, initiating
  sign-in, launching connections, or interrupting active sharing.
- Invalid CLI paths select the new tab; switching tabs preserves unsaved edits.
- Closing Settings cancels and awaits diagnostic work without stopping unrelated
  ongoing sharing.
- Existing settings remain compatible, including restart-required path changes.
- Results distinguish protocol availability from Windows App version verification,
  and saved Azure CLI account status from live access validation.

## Scope boundary

Move user-facing checks, not runtime safety validation or installer prerequisite
detection. Windows App and extension checks are additions to Settings, rather than
existing buttons being relocated.
