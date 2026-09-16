# Configurator tabbed view implementation plan

## Goal

Replace Configurator's single long scrolling view with a fixed, non-closable
tabbed interface that groups related tasks while preserving the current
multi-target discovery, IDE verification, transactional preview/apply, runtime,
recovery, and cancellation behavior.

This is an information-architecture and UI refactor. It must not change
configuration formats, integration ownership, hook generation, verification
requirements, reporter startup behavior, privacy guarantees, or dashboard
protocol compatibility.

Paths below are relative to the repository's `src` directory.

## Current baseline

`src\AgentSignaler.Configurator\MainWindow.xaml` currently places every control
inside one window-level `ScrollViewer` and `StackPanel`. The page mixes:

- product guidance and discovery controls;
- dynamically generated integration target cards;
- dashboard URL, heartbeat, runtime, and identity settings;
- the consented IDE verification workflow;
- preview/apply, connectivity tests, startup, recovery, and uninstall actions;
- global status feedback and the exact change preview.

`MainWindow.xaml.cs` keeps one shared state model for all of these workflows:
`targets`, `selections`, `plan`, `verification`, discovery and connection-test
cancellation sources, runtime state, and the `busy` flag. Moving controls must
not recreate them on tab changes or split these fields into independent copies.

The Dashboard already uses a WinUI `TabView` with fixed, non-reorderable,
non-closable tabs and a separate scrolling region per tab in
`src\AgentSignaler.Dashboard\MainWindow.cs`. Configurator should follow that
established interaction pattern.

## Proposed tab structure

Keep the branded header, version, short privacy statement, global `InfoBar`, and
configuration/Relay paths outside the tabs. These identify the application and
provide feedback that must remain visible regardless of the selected task.

Use a `TabView` with `IsAddTabButtonVisible="False"`, `CanDragTabs="False"`,
`CanReorderTabs="False"`, and `TabWidthMode="SizeToContent"`. Each tab owns one
vertical `ScrollViewer`; remove the window-level `ScrollViewer`.

| Tab | Purpose | Existing controls/content |
| --- | --- | --- |
| **Connection** | Configure and operate the shared reporter connection. | Dashboard URL, heartbeat interval, runtime status, machine UUID, Test/Cancel test, Save initial reporter connection, Start client, and concise explanations of saved versus effective settings. |
| **Integrations** | Discover, inspect, select, and remove integration targets. | Discovery summary, local-only/shared-scope guidance, Refresh/Cancel detection, custom VS Code profile root, dynamically generated `TargetsPanel`, selected-set explanation, and per-target Verify/Remove/override actions. |
| **Verification** | Run and complete the explicit actual-IDE verification workflow. | Verification guidance, evidence/status text, experience and reload fields, four confirmation checkboxes, Read evidence, Complete, Cancel, Record limitation, and Recover interrupted verification. |
| **Review & maintenance** | Review and consent to persistent integration changes or recovery/removal. | Preview install/repair/update, Apply, exact preview text, Recover interrupted integration transaction, and Uninstall entire integration. |

Do not duplicate controls between tabs. In particular, keep **Test connection**
under Connection and **Verify integration** under Integrations/Verification so
the UI continues to distinguish dashboard reachability from proof that an IDE
loaded a hook.

## Layout and navigation behavior

1. Use a root `Grid` with rows for the persistent header, `TabView`, global
   `InfoBar`, and optional persistent path/footer details. Give the tab content
   the remaining window height so each tab scrolls independently.
2. Preserve the current maximum readable content width, margins, and spacing.
   Target cards and preview text must stretch horizontally without introducing
   a window-level horizontal scrollbar.
3. Default to **Connection** on normal startup. Do not persist the last selected
   tab in this change.
4. When the user selects **Verify integration** or retries verification from an
   integration target, switch to **Verification** before presenting or
   activating the diagnostic workflow. If consent is cancelled, retain the
   selected target context and leave the user on Verification with clear status.
5. When a preview is created, switch to **Review & maintenance** and bring the
   exact preview into view. Per-target removal previews should use the same tab
   before their consent dialog.
6. Route validation and workflow errors to the tab containing the relevant
   control:
   - missing/invalid URL or heartbeat -> Connection;
   - unavailable selected target or invalid custom profile/override ->
     Integrations;
   - incomplete verification evidence/confirmations -> Verification;
   - stale or failed preview/apply/recovery -> Review & maintenance.
   Focus the actionable control when one is known.
7. Preserve normal `Tab`/`Shift+Tab` focus movement, arrow-key tab-header
   navigation, and `Ctrl+Tab`/`Ctrl+Shift+Tab` cycling supplied by `TabView`.
   Do not add custom keyboard handling unless WinUI behavior is insufficient.
8. Tab headers must remain usable at the current 1050-pixel window width and at
   practical narrower widths. Rely on `TabView` header scrolling/overflow rather
   than truncating labels into ambiguous text.

## State and lifecycle requirements

### Shared state

- Retain one `MainWindow` instance and the existing fields in
  `MainWindow.xaml.cs`; changing tabs must not reload configuration, rerun
  discovery, rebuild targets, discard text edits, clear checkboxes, or invalidate
  a preview.
- Continue invalidating `plan` only for the existing semantic changes:
  URL/heartbeat edits, target selection changes, target overrides, discovery
  refresh, verification changes, and integration mutations. Selecting a tab
  alone must not invalidate a preview.
- Preserve dynamically created target controls in `TargetsPanel` across tab
  switches. `RenderTargets` should still rebuild only after discovery or a
  state-changing operation.
- Keep the active `HookVerificationPlan` and its entered confirmation text intact
  when navigating away from Verification.

### Busy and cancellation handling

The current `SetBusy` implementation only walks direct children of
`RootPanel` and one nested `StackPanel` level. That will not correctly update
controls nested under `TabViewItem`, `ScrollViewer`, `Grid`, `Border`, and
`Expander`.

Refactor busy-state handling before or with the XAML move:

- Prefer named action controls and explicit state updates for workflow-critical
  buttons over a shallow visual-tree scan.
- If a shared recursive helper is used, handle `Panel`, `ContentControl`,
  `Border`, `ScrollViewer`, `TabView`, and `TabViewItem` safely without disabling
  the global status display.
- Keep Cancel detection and Cancel test enabled only while their corresponding
  operation owns a cancellation source.
- Keep Read evidence, Complete verification, Cancel verification, and Record
  limitation enabled only while a verification is active.
- Keep Apply enabled only when a current preview exists, no operation is busy,
  and no verification is active.
- Disable target selection and target-card actions during busy work or active
  verification exactly as today.
- Do not disable tab switching solely because an operation is running; users
  must be able to reach the active operation's Cancel control. Prevent starting
  conflicting work through the individual action states.

Closing the window must continue to cancel connection testing and discovery and
attempt verification cleanup. Tab changes must never cancel work.

### Global feedback

Keep `StatusBar` outside the selected tab so success, warning, and error messages
remain visible after automatic navigation or user tab changes. Replace its
current assumption that `StartBringIntoView` should scroll the single root page;
focus or announce the global bar without unexpectedly scrolling a tab away from
the control the user is editing.

The exact change preview belongs in Review & maintenance and remains selectable,
read-only, monospaced, and vertically bounded. Verification previews may continue
to populate the same box, but beginning verification must navigate there for
review before the consent dialog and then return to Verification after consent.

## Implementation steps

### 1. Introduce the tab shell

Update `src\AgentSignaler.Configurator\MainWindow.xaml`:

- replace the outer `ScrollViewer` with a root `Grid`;
- retain a compact persistent header and privacy summary;
- add named `TabView` and `TabViewItem` elements for the four tabs;
- add one scrolling `StackPanel` content region per tab;
- move existing controls without renaming event handlers or changing user-facing
  safety text unless needed to remove repetition;
- keep `StatusBar`, `ConfigPathText`, and `RelayPathText` globally visible.

Use named tab items such as `ConnectionTab`, `IntegrationsTab`,
`VerificationTab`, and `ReviewTab` so code-behind can navigate directly without
depending on numeric indexes.

### 2. Reorganize controls without changing workflows

Move controls according to the table above. Preserve every current action and
warning, including:

- no automatic reporter start during ordinary open/edit/test;
- no IDE support claim from discovery or dashboard connectivity;
- explicit consent for diagnostic installation and production apply;
- exact saved/proposed/ownership and shared-scope target details;
- explicit recovery paths and full versus per-target removal;
- privacy exclusions and unsupported remote-host guidance.

Shorten repeated explanatory paragraphs only where a persistent overview plus
tab-local detail conveys the same requirements. Do not remove safeguards to make
the tabs appear simpler.

### 3. Add explicit tab-routing helpers

In `src\AgentSignaler.Configurator\MainWindow.xaml.cs`, add a small helper that
selects a named tab and optionally focuses or brings a specific control into
view after layout.

Invoke it from:

- `BeginVerificationAsync`, `RetryVerificationAsync`, and active-verification
  recovery/result paths;
- `Preview_Click`, `Apply_Click`, `RemoveTargetAsync`, and integration recovery;
- validation failures in `ReadConfiguration`, `SelectedTargets`,
  `AddProfile_Click`, `ChangeHookDirectory`, and `ChangeCodeExecutable`;
- startup warnings for pending diagnostic or integration recovery, selecting
  the corresponding tab only when the user activates the recovery action rather
  than unexpectedly changing the initial tab.

Avoid routing based on exception-message parsing. Validate fields near their
own handlers or use typed/local error handling to choose the destination.

### 4. Make control-state updates tab-safe

Replace the shallow `RootPanel.Children` traversal in `SetBusy` with explicit
workflow state application. Group named controls by responsibility where useful,
for example:

- discovery actions;
- connection actions;
- target selection/actions;
- verification actions;
- preview/apply/maintenance actions.

Because target-row buttons are created dynamically, retain references to their
interactive controls or recursively update only `TargetsPanel`. Reapply the
state after `RenderTargets`, starting/cancelling verification, creating or
invalidating a preview, and completing asynchronous work.

Ensure the selected tab itself remains navigable while controls are disabled.

### 5. Preserve accessibility and responsive behavior

- Give each tab a concise accessible name matching its visible header.
- Keep labels as real control headers rather than replacing them with placeholder
  text.
- Ensure status and verification result text remains selectable and wraps.
- Verify focus moves to the first invalid field or relevant workflow control
  after automatic navigation.
- Ensure expanders and dynamically generated controls remain reachable by
  keyboard and screen readers.
- Check high-contrast and light/dark themes using existing resources; do not
  introduce fixed background colors.
- Confirm the four tabs and their content remain usable at 200% text scaling and
  when the window is shorter than its initial 960-pixel height.

### 6. Update documentation and acceptance coverage

After implementation:

- update `README.md` to describe the four Configurator tabs and where connection
  testing, target discovery, verification, preview/apply, and recovery now live;
- update `docs\MANUAL-TEST-PLAN.md` multi-target procedures to name the new tabs
  and add tab-navigation/state-preservation checks;
- update `docs\ACCEPTANCE.md` only where release instructions refer to controls
  whose location changed.

Do not rewrite the existing architectural plans; they remain the source for
behavioral and security requirements.

## Validation plan

### Build and automated checks

1. Build Configurator and the existing integration-test project for x64 Debug.
2. Run the existing `ConfiguratorSettingsTests` and targeted Remote integration,
   verification, preview/apply, and runtime tests to confirm the UI refactor did
   not alter domain behavior.
3. If UI-state logic is extracted into testable helpers, add focused tests for
   action enablement and tab destinations. Do not add brittle tests that assert
   visual-tree child indexes.

### Manual UI checks

1. Launch with no saved configuration: Connection is selected, machine identity
   and paths are shown, and saving the initial reporter still installs no hooks
   or startup entry.
2. Enter unsaved URL/heartbeat values, select targets, expand target details, and
   enter verification text; switch through every tab and confirm all state is
   preserved.
3. Refresh and cancel discovery from Integrations. Confirm other tabs remain
   reachable and no integration state changes.
4. Start Test connection, navigate away, return, and cancel it. Confirm no
   machine status or integration configuration changes.
5. Select Verify on a target. Confirm navigation to Verification, diagnostic
   consent, evidence reading, incomplete-confirmation errors, cancellation,
   completion, limitation recording, and interrupted recovery.
6. Create a production preview. Confirm navigation to Review & maintenance,
   exact preview visibility, Apply enablement, and invalidation after changing
   URL, heartbeat, target selection, discovery result, or verified scope.
7. Exercise per-target removal, full uninstall, and integration recovery.
   Confirm the correct tab remains selected and global status remains visible.
8. While discovery, connection testing, verification, preview/apply, or recovery
   is active, confirm only compatible actions are enabled and the relevant Cancel
   action can always be reached.
9. Close Configurator during active discovery, testing, and verification in
   separate runs. Confirm bounded cancellation and existing probe-cleanup
   behavior.
10. Verify keyboard tab-header navigation, focus routing for invalid inputs,
    screen-reader labels, high contrast, light/dark themes, narrow width, short
    height, and 200% text scaling.

## Acceptance criteria

- Configurator uses exactly four fixed tabs named **Connection**,
  **Integrations**, **Verification**, and **Review & maintenance**.
- The header and global status feedback remain visible independently of the
  selected tab; each tab scrolls independently.
- Every control and safety statement from the current single view remains
  available in the appropriate logical group without duplicated state.
- Switching tabs does not trigger discovery, cancel work, discard edits, rebuild
  targets, clear verification input, or invalidate an approved preview.
- Verify and Preview actions navigate to the relevant tab, and validation errors
  route to and focus the relevant field or workflow.
- Busy-state handling works for controls nested inside tabs and always leaves the
  owner-specific Cancel action reachable.
- Dashboard connectivity testing remains distinct from actual IDE hook
  verification.
- Existing configuration, integration ownership, verification, runtime,
  recovery, removal, privacy, and explicit-start behavior is unchanged.
- The interface is keyboard accessible and usable at narrow/short sizes, high
  contrast, and 200% text scaling.
- README and manual acceptance documentation identify the new control locations.

## Scope boundary

This change does not redesign target cards, add a setup wizard, persist tab
selection, alter configuration schemas, change integration discovery or
verification policy, add new installation capabilities, or modify Dashboard
settings tabs. Those changes require separate product decisions.
