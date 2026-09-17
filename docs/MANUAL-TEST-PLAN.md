# Single-machine manual test plan

## Purpose and scope

Run Dashboard, persistent Client, Configurator, Relay, and optionally Copilot CLI on one
Windows 11 x64 machine. Use `127.0.0.1` as the dashboard host; no second computer,
cloud dashboard, inbound firewall rule, or URL ACL is needed for the core tests.

This is a test procedure, not a record of tests already passed. Record each case
as **Pass**, **Fail**, **Blocked**, or **Not run**, with evidence and defect links.
Managed tray/heartbeat behavior is implemented; its manual acceptance remains
outstanding. Sections B/D use **legacy direct HTTP v1 fixtures** with five-minute
hook-only liveness. Section C instead requires a **running managed Client coordinator**
and v3 configuration; a Relay process alone is not a network reporting fixture.
Synthetic events exercise the application but do not prove that Copilot CLI emits
the corresponding hooks. Loopback does not prove LAN/VPN reachability or firewall
enforcement. Retain the two-computer checks in `ACCEPTANCE.md` for release sign-off.

## Prerequisites and safety

- Use a disposable Windows user account, preferably in a local Windows 11 x64 VM
  with a checkpoint. One VM on one physical machine is sufficient. Do not run
  installer, hook, startup, corruption, or firewall mutation tests in a real
  development profile. Firewall changes are machine-wide even with a test account.
- Obtain both MSIs or complete published application folders for the same build.
  See `..\installers\README.md` for the existing build procedure. Published apps
  are self-contained; PowerShell **7** is required for the HTTP examples below.
  Building from source additionally requires the documented .NET 10/WinUI tools.
- Optional: Copilot CLI **1.0.80 or later** on PATH, signed in with an authorized
  account, and a disposable empty working directory. Live CLI tests may require
  Internet access and consume usage. They are not required for synthetic tests.
- Leave `AGENT_SIGNALER_DATA_DIR` unset for this procedure. Use the disposable
  user's default `%LOCALAPPDATA%\AgentSignaler` data directory. Also leave
  `COPILOT_HOME` unset unless specifically testing its override.
  Process-only data overrides do not automatically carry into sign-in launches
  or installer cleanup; do not mix those modes during lifecycle testing.
- Keep the machine on a trusted network. In explicitly selected LAN mode the receiver binds all interfaces and
  has no authentication or encryption, even when tests use loopback. Never
  port-forward it or disable Windows Firewall.
- Record product/build, Windows, PowerShell, CLI versions, execution date, tester,
  installation mode, and selected ports. Allow about 60-90 minutes for core
  checks, plus live CLI and servicing checks. Keep the machine awake during timers.
- Use only synthetic prompts, outputs, and error text. Do not attach credentials,
  actual prompts, source code, or user-profile data to test evidence.

## Detailed-conversation prototype acceptance — Not run

This section specifies expected outcomes, not passed tests. Existing HTTP/LAN and
v1–v4 fixture sections remain **status-only** compatibility checks. The historical
automated counts in `ACCEPTANCE.md` do not validate these new gates.
Do not execute live hooks, installers, tunnels, certificate/trust changes, or
installed-UI acceptance as part of unattended implementation. Obtain separate
authorization and an approved disposable environment first. Use only synthetic
messages (including fictional PII) and independently marked prohibited fields;
keep evidence category-only, without messages, local transcript paths, or raw
payloads. No new certificate provisioning is part of the product.

External informed permission for content, destination, and retention must exist
before prototype distribution/use. The app does not verify it. Apply confirmation,
installation, and notices are not consent evidence. New v5/default-on tests must
use isolated configuration, never a real operator endpoint.

**Production assistant readers are unavailable:** no Copilot CLI, VS Code, or
Visual Studio transcript format/path/session/completion profile is independently
verified. Do not promote a synthetic normalized/test-only adapter to production
support. Published hook fields, installed binaries, and actual-host acceptance are
distinct evidence; a stop path alone is insufficient. Test partial prompt/activity
capture independently of assistant availability.

| ID | Procedure after separate authorization | Expected result |
| --- | --- | --- |
| T01 | Preview a new v5 setup, omitted v5 flag, explicit true/false, and v1–v4 loads. Open new binaries without Apply. | V5 defaults Share detailed conversations on; explicit false remains false. Legacy stays status-only without rewrite. Notice states external consent and unredacted message PII; no consent/enrollment/token workflow. |
| T02 | Upgrade Dashboard first and matching Relay/Client/Configurator together; explicitly migrate. Test old Relay/new Client and new Relay/old Client with readable legacy configuration. | Status routes and exact v2 IPC remain compatible; v3 negotiation gates content before extraction/send. Old Clients do not receive text and visibly reject unreadable v5, not reinterpret it. |
| T03 | Repair/recover saved false; inject opt-out save failure or rollback toward older true. Preview an explicit coordinated v5-to-v4 downgrade. | False preserved or recovery conflict; failed save leaves this run suspended. Downgrade stops exact owned Client, clears detail, writes validated status-only v4 and uses compatible servicing. V4 retains no v5 flag; subsequent v5 upgrade previews default-on again. Stopped reporter is not restarted by rollback. |
| T04 | Exercise HTTP/LAN, incompatible receiver, disabled receiver, TLS failure, redirect, stopped/changed tunnel, and endpoint/source changes with controlled fixtures. | Status remains independent. Details use only configured HTTPS with normal validation and owned-running-tunnel readiness on loopback Internet mode; no alternate endpoint, stale queue, or local file open while unavailable. |
| T05 | Send synthetic allowed hook user text/fictional PII/tool names plus unique markers in arguments/results, errors, reasoning, attachments, and arbitrary fields. | Only permitted fields survive; PII within allowed text remains unredacted. Status/SQLite contain no messages; local stop reference never enters HTTP, state, logs, diagnostics, or viewer models. |
| T06 | Use an explicitly test-only verified-format fixture and accepted exact stop reference through actual Relay/IPC/Client/HTTPS harness/store/view model. Also exercise each unavailable production profile. | Assistant-only completed top-level text; no file-sourced user prompts, tools, subagents, deltas, or hidden reasoning. One stop observation, partial fallback, provenance without a path. Fixture success proves no production schema. |
| T07 | In test-owned files exercise first stop, repeated stops, partial trailing frame, delayed flush, replacement, deletion, truncation, format/encoding/session mismatch. | Baseline excludes history unless exact current-turn proof exists; possible first reply omission is visible. No duplicates, guessed boundaries, whole-file replay, watcher, or scan. Changes invalidate cursors and rebaseline with a gap. Input bytes are unchanged. |
| T08 | Reject relative/UNC/device/ADS/reparse inputs and wrong root/session/user/file identity using isolated fixtures. | Profile-specific exact binding is rechecked on the opened read-only handle; unsupported binding stays unavailable. No path in error output; no file ownership transferred. |
| T09 | Exercise reader limits with synthetic boundary fixtures and a controllable clock. | One active read, 16 queued sessions, two-second queue expiry, 32 contexts/eight per source, 30-minute inactivity expiry. Each stop shares 750 ms / 2 MiB / 128 records / 16 replies / 256 KiB output across at most 0/100/300 ms attempts; 64 MiB file, 16 KiB chunk, 64 KiB record/depth-16 limits. Capacity/budget gaps visible, IPC/status not blocked. |
| T10 | Opt out or Exit during reads, retries, IPC negotiation, and slow sends; re-enable, restart, or remove/change endpoint/source. Make receiver unreachable during opt-out. | Admission stops immediately; operations cancel within existing four-second shutdown budget. Text/references/cursors dropped; no backfill/resurrection. Unreachable remote purge is best-effort, not acknowledged deletion; local TTL/clear/disable still applies. Hooks never start Client or issue HTTP/control responses. |
| T11 | Toggle Receive detailed conversations under Settings > Internet sharing, cancel the dialog, and inject a preference-save failure. Clear one computer, remove it, stop listener/tunnel, and restart receiver while viewing. | Toggle applies/saves immediately, independent of dialog Save/Cancel. Disable purges before save and a failed save leaves this run disabled with an error, without disabling status. Clear permits only fresh future capture. Affected streams/views invalidate immediately, including late dispatcher completions. Restart has empty history/new epoch; managed status re-registration remains possible. |
| T12 | Use Unicode/escaping-heavy content, slow senders/readers, concurrent sources, dropped acknowledgements, retry exhaustion, overflow, and TTL expiry. | 32 KiB serialized events; total Client 4 MiB and receiver 64 MiB including scratch/index/view copies, not extra caches. Bounded duplicate/order/gap metadata, quotas and throttling; no duplicate TTL extension, silent complete-history claim, or disk spool. Record measurements, not just configured constants. |
| T13 | Open computer details through normal and connection-error paths; edit Settings then switch tabs during mapping/launch/cancel work. | Settings first, all former controls and drafts intact. No save/connection on switch; Save details/Remove only on Settings with handler guards, Close and cancellation reachable on both. Existing Settings copy-URI unaffected. |
| T14 | Select same session names in distinct machines/sources/scopes/streams, retained ended sessions, and empty/partial/offline/error states. Delay completions while changing selection or closing. | No merged identities or stale cross-selection text; missing assistant reply is not success. Baseline, unverified/changed format, waiting for stop, budget, gaps, truncation, expiry and reset are explicit; arrival order is labelled where native correlation is absent. |
| T15 | Page older content and receive new activity; repeatedly switch tabs/close/reopen. Expire or evict content while visible. | Session page 16 / maximum 32; event page 32 / 128 KiB; window 64 / 256 KiB plus one pending page. One viewer/read, one-second visible-only refresh, bounded cursor lifetime, append-safe older pages, scroll intent/new-activity indicator, deadline-based invalidation and text-reference release. |
| T16 | Render synthetic HTML, Markdown, links and image syntax; inspect keyboard, screen reader, focus, high contrast, DPI and narrow layouts. | Inert wrapped plain text, no navigation/images/execution, composer, approval, transcript copy/export, or accessibility leakage of excluded fields. Virtualization preserved; no file read or sharing enabled by opening Transcript. |
| T17 | Inspect test-owned configuration/state/SQLite/WAL/log/backup/recovery artifacts for distinct allowed and prohibited markers, and compare host-input bytes before/after lifecycle operations. | No application-persisted conversation text, paths/cursors or recovery fingerprints. Known synthetic host-input fixtures are excluded from output scans and remain unchanged. Memory-only is not a claim about host history, OS paging/hibernation or external crash capture. |
| T18 | Build/inspect fresh x64 packages before separately authorized installer lifecycle tests. Test clean install, repair, upgrade, interrupted rollback and uninstall only in the approved VM. | Matching managed assemblies plus XBF/PRI preserved; no transcript database, host inputs, credentials/pairing or certificates shipped. No capture/tunnel/consent established by installation. Only exact app-owned artifacts changed; host transcripts always untouched. Source inspection alone does not pass installed lifecycle acceptance. |

Keep all T01–T18 **Not run** until their own execution evidence is recorded.
Automated fixture results may support individual boundaries, but do not close
manual UI, actual-host, live-cloud, or installer-execution gates.

## Dashboard startup presentation (Not run)

- With previously reported machines, start Dashboard in Internet mode in an
  approved disposable live-tunnel environment. Confirm a progress bar appears and
  no computer tiles, empty-state message, or compact tiles appear before public
  HTTPS verification completes. Minimize/restore during loading and check again.
- Confirm Settings and Compact View are disabled while loading. After startup, the
  bar disappears, tiles appear, machine status is unchanged, and Copy URL copies
  the verified displayed URL. No Service/Dev Tunnels status labels remain.
- Repeat in LAN mode and with automatic sharing disabled: startup completes
  without waiting for a public endpoint. With no machines, the waiting message
  appears only after startup.
- In a disposable profile, test an occupied receiver port, unavailable CLI, and
  failed public health verification. The bar must stop and an actionable error
  remain visible; Settings is available for recovery and copying is disabled
  without a valid endpoint.
- Exit from the notification area during loading. Startup cancels, shutdown
  completes, and no late continuation shows dashboard or compact tiles. Closing
  to an available tray icon still hides rather than cancels startup.
- Check keyboard navigation, progress accessibility name, light/dark themes,
  and increased display scaling.
- Confirm the URL sits below the machine-count subtitle, with an icon-only Copy
  URL button immediately to its right. Check its tooltip and accessible name,
  and use both mouse and keyboard to copy the complete URL.
- Check the smaller default window (1040 x 640 logical pixels, or 900 x 600 with
  compact density). Resize narrower and test a long URL at 100%, 150%, and 200%
  scaling: the URL ellipsizes without displacing Copy URL; header actions move
  below the brand at narrow widths, and machine cards remain scrollable.
- Confirm Compact View and Settings have glyphs and their exact text labels.
  With machines loaded and compact-on-minimize enabled, Compact View still
  minimizes to the compact tiles; Settings still opens the settings dialog.

## Multi-target IDE verification and configuration v5

These are **Not run** live release gates, not automated implementation results.
The older v1/v3 fixture sections below remain compatibility tests; new Configurator
previews use v5 and require an upgraded source-aware Dashboard. Disable detailed
reporting for these status-only cases; T01–T18 cover the separate detail stream.

Inventory observed September 15, 2026 (not hook verification):

| Candidate | Observed versions | Effective scope / status |
| --- | --- | --- |
| Visual Studio Main | IDE `18.12.12211.431`, bundled CLI `1.0.83` | `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli` is only a candidate; **Verification required** |
| Visual Studio IntPreview | IDE `18.12.12211.348`, bundled CLI `1.0.83` | Same candidate home; **Verification required independently**, possibly shared |
| VS Code default local profile | Host `1.137.0`, installed `GitHub.copilot` files `1.388.0` | Activation, effective profile hooks, policy/trust and Windows shell **unverified** |
| Custom local profile | Record actual host/adapter versions and selected root | **Verification required**; remote hosts excluded |

No actual IDE workflow was verified during automated implementation. Do not promote
these entries to Verified from binary presence, a synthetic payload, a health request,
or a manually run bundled CLI. Record exact instance/profile, agent experience,
host/adapter version, physical scope, observed events and reload instructions per
successful live test, without prompt/tool/transcript content.

| ID | Procedure | Expected result |
| --- | --- | --- |
| M01 | In Hooks, refresh with standalone CLI plus both VS instances. Toggle Enable on/off, then refresh with an unsaved selection. Cancel a refresh; simulate inaccessible installation metadata in a disposable fixture. | Discovered rows show Enable, name, read-only path and status, without empty dropdowns or an automatic blank row. Toggling updates the selected count and pending row status immediately; refresh preserves pending selections. Failures are visible; discovery and toggles make no hook/settings changes. |
| M01a | Inspect the Enable column before/after clicking a checkbox and toggling with Space. Repeat on a custom row, in light/dark/high-contrast themes and at increased display scaling. | The unchecked outline and checked fill/checkmark are visibly distinct and fully inside the cell, not clipped. Keyboard focus remains visible. The checkmark and selection count agree; no settings are written until Apply is confirmed. |
| M01b | Open each Configurator tab, hover its top-right help icon, then move away or switch tabs. Scroll the view and repeat at increased display scaling. | Descriptive help appears in a wrapped tooltip, not in inline paragraphs, and dismisses when leaving. Each view's icon stays at the top right outside scrolling content. Connection help includes privacy and startup guidance; Hooks help includes relay, Apply and custom-path guidance; Review help describes preview and maintenance. Labels, live status, pending counts and errors remain visible. Opening help changes no settings. |
| M02 | Select Add custom location, enter custom CLI/Visual Studio hook directories and a VS Code profile directory, and tick Enable. Try relative and UNC paths. Add another row, refresh and reopen before/after Apply. | Type and path editors appear only on requested custom rows. Invalid paths fail visibly. Refresh retains draft paths/types/ticks; applied custom locations survive reopening. Unticked blank rows are ignored. |
| M03 | With no saved configuration and Client stopped, enter connection settings, disable Share detailed conversations for this status-only case, tick one location and select Apply settings without using Preview first. Cancel, then repeat and approve. | Apply shows exact combined changes for confirmation, not external-consent verification. Cancellation writes nothing. Approved first setup saves v5 settings with explicit false, installs hooks/startup and starts Client without a reporter-only Save or diagnostic workflow. |
| M03a | Apply against a compatible dashboard whose health response takes more than 3 but less than 10 seconds after connecting. Repeat with an unresponsive dashboard and an unresponsive local Client. | Apply uses the interactive 3-second connection timeout within a 10-second capability-check budget. A slow healthy response can succeed. Dashboard timeouts identify the URL and Test connection action without changing existing settings/hooks/startup. Local Client timeouts identify IPC rather than connectivity. If a later refresh fails after saving, the UI says settings were saved. |
| M04 | Apply one VS instance, exclude other loaders and perform harmless actual IDE agent activity. Separately run its bundled CLI as a negative attribution check. | Configuration is labelled Configured, not Verified. Native host testing remains a release check, not a setup prerequisite. Bundled CLI execution is not proof of IDE support; no prompt/tool data is sent. |
| M05 | Apply a disposable VS Code profile and inspect its settings and hook file. Exercise host policy/trust, workspace overrides and reload behavior. Load a legacy recorded policy-block fixture and try Apply. | Only owned hook location entries change; comments/unrelated settings and policy remain untouched. Known blocks are not silently overridden. Automatic configuration does not promise universal loader support. |
| M06 | After automatic setup, send synthetic valid events and events with absent stable session IDs or unsupported names. Inspect Client and Dashboard. | Configured sources report without compatibility records; invalid payloads remain rejected. Stop means Waiting, not success or session end. |
| M07 | Open a disposable configuration with a pending legacy diagnostic; use Clean up legacy diagnostic hooks in Review & maintenance. Repeat after editing its owned bytes. | No new verification workflow is exposed. Recovery removes only unchanged owned probe artifacts; conflicting user edits survive with a visible error. |
| M08 | Select one available and one unavailable location, then Apply. Revise the selection and cancel/approve in separate runs. | The complete selected set is validated; nothing is silently skipped. Exact files/settings/startup changes are confirmed together. |
| M09 | Enable both VS instances sharing a home and Apply, then untick one and Apply again. | One artifact per physical scope. The remaining selected integration retains its hook; instance-specific isolation is not promised. |
| M10 | Configure verified isolated VS Code profile, custom CLI home and isolated VS scope together; cause identical host session IDs in different sources using fixtures, then exercise real hosts one at a time. Separately retain a default .copilot/hooks observer or explicit profile location loading another selected scope. | Verified isolated coexistence succeeds with distinct sessions, exactly one event per invocation and one machine UUID/tray/timer/heartbeat. Actual default/explicit loader conflicts remain blocked. No automatic exact-file exclusions or disabling unrelated hooks; remove an existing owned default CLI integration separately if required before custom-home migration. |
| M11 | Inspect VS Code Stop and absent session-end events; wait through heartbeats and test workspace hooks shadowing user hooks. | Stop becomes Waiting, never Succeeded or SessionEnd. Last-observed activity is distinct from machine availability; no invented expiry/end. Effective hook limitations remain visible. |
| M12 | Edit JSONC settings between Preview/Apply; inject partial write failure in fixtures, then an interrupted rollback. In Review & maintenance, resolve owned-artifact conflicts and consent to Recover interrupted integration transaction. Repair and remove one target after unrelated comments/settings edits. | Stale preview rejected; rollback/recovery preserves concurrent user edits and retains a journal/backups on conflicts. Recovery never starts a stopped Client; inspect saved versus effective runtime in Connection afterward. Per-target removal retains other hooks, config/startup and reporter. |
| M13 | Exit Client during IDE activity; open/edit/Test in Connection and Refresh in Hooks, then explicitly Start client. | Exit stops reporting; no hook HTTP fallback, resurrection or stopped-period replay. Explicit Start restores the saved reporter. |
| M14 | Test old Dashboard, legacy owned manifest migration, unticking saved rows, empty selection, full Uninstall integration and packaged repair/uninstall. | Old Dashboard is refused; migration retains identity and owned artifacts. Unticked targets are removed on Apply, with shared reporter settings retained. Full uninstall stops the reporter; foreign changes survive. |
| M15 | In disposable fixtures, exercise generated VS Code Windows cmd commands using spaces, Unicode, ampersand, parentheses and caret paths; separately preview quote, percent and exclamation-mark arguments/paths. Verify the actual selected IDE shell separately under M05. | Supported fixture commands reach exact Relay/IPC with empty stdout and the existing three-second hook budget. Quotes, `%` and `!` fail closed instead of expanding. No universal shell/path support claim; synthetic cmd execution does not verify an IDE loader. |

### Configurator tab navigation and accessibility

These checks also remain **Not run** until exercised in the installed UI.

| ID | Procedure | Expected result |
| --- | --- | --- |
| M16 | Launch fresh, switch tabs and reopen. Repeat with pending diagnostic/integration recovery. | Exactly three fixed tabs: Connection / Hooks / Review & maintenance. Connection is initially selected; Apply settings, header, status and paths remain outside the tabs. No automatic reporting starts. |
| M17 | Edit URL/heartbeat, tick locations and enter custom paths/types. Switch tabs, refresh and scroll rows out of view and back. | Draft fields and selections survive navigation, refresh and DataGrid virtualization. Editing invalidates the displayed preview, without writing configuration. |
| M18 | Start discovery or connection testing, switch tabs, return and cancel. Also switch during Apply/recovery. | Cancellation remains reachable only for its operation. Conflicting controls are disabled. Apply needs no manual preview/verification step and is disabled while busy. |
| M19 | Apply invalid connection fields, an unavailable row, invalid/duplicate custom paths, and fixtures with conflicting file edits. Cancel the final consent dialog. | Validation selects Connection or the offending Hooks row; transaction errors appear in Review & maintenance. Every Apply presents a fresh complete preview; cancellation changes no hooks/settings. |
| M19a | In Hooks, edit Remote relay location to another local installation containing Relay and Client. Preview, cancel Apply, then Apply and reopen. Try blank, relative, wrong executable and missing paths. Refresh locations with an unsaved relay edit. | Cancel leaves JSON and hooks unchanged. Apply saves `relayPath` in `remote.json`, updates enabled hooks and owned startup, and reopening restores it. Start client uses the saved installation. Invalid paths focus the relay field on Hooks. Editing clears the preview; discovery preserves the unsaved edit. |
| M20 | Navigate the three tabs and grid by keyboard and screen reader. Tick the first-column checkbox, choose custom type, enter/copy paths and invoke Apply. | Named tabs, checkboxes, path controls and grid column headers are accessible. Discovered paths are read-only and copyable. No model type name appears as a text-box header. |
| M21 | Test light/dark/high contrast, narrow/short windows and 200% text scaling. Scroll all grid rows and columns; show a long error. | Grid scrolling exposes every location and custom row. Controls remain reachable; global status is bounded, paths have tooltips, and Apply settings remains visible. |

### HTTPS client-only checks

The tests in this section isolate HTTPS client behavior from the opt-in CLI host.
Explicitly select LAN mode for the LAN tests below. Never port-forward
the HTTP receiver as a substitute for loopback-only Internet mode.

In a separate disposable, trusted HTTPS test fixture:

1. Paste a root HTTPS URL in Configurator (443 default or explicit port 1-65535).
   Verify IPv4/IPv6, optional trailing slash, and rejection of credentials, paths
   (including normalized dot segments), queries, fragments and control characters.
2. Test connectivity without applying: existing v1/v2 `remote.json` bytes and identity
   must not change. Cancel a slow test with **Cancel test**; otherwise it ends within
   the interactive 10-second limit.
3. Return compatible JSON with `Content-Type: application/json`, then HTML,
   malformed JSON, incompatible versions and redirects. Only compatible 200 JSON
   health succeeds. Invalid certificates must fail; never disable TLS validation.
4. Inspect synthetic GET and POST: `Accept: application/json`, no authorization or
   cookies. Verify system proxy use for HTTPS, no credential prompt, and HTTP proxy
   bypass. Simulate 401/403, 407, 429 and unavailable-host responses.
5. Upgrade Dashboard first, then Client/Configurator/Relay together before Apply.
   Check `/api/v2/health` capabilities and unchanged legacy `/health`; incompatible
   Dashboard blocks managed integration without direct-v1 fallback. Inspect v3
   config including `heartbeatIntervalSeconds: 300`, exact Client/Relay paths,
   unchanged UUID, whole 1–60 minute bounds, sign-in registration and first launch.
   For published-folder setup, approved Apply removes any remaining
   ownership-verified legacy heartbeat task; MSI servicing removes it beforehand
   through a task-only transaction. Neither creates a replacement. Force a
   failed health check during apply: the previous integration, including any removed
   legacy task and owned startup value, must be restored. Preserve matching config,
   integration, startup and task backups. Runtime launch/reload failure after disk
   commit must show saved-but-not-applied and explicit recovery, not full success.

These checks do not establish real Dev Tunnels hosting, relay latency, certificate
provisioning or release suitability. Record the Internet acceptance gates in
`ACCEPTANCE.md` as blocked until independently verified.

### Opt-in CLI hosting checks

Use a disposable signed-in Windows account, the qualified Microsoft-signed CLI
version documented in `..\README.md`, and synthetic status data only. Obtain
explicit approval before creating a public endpoint.

1. Install/sign in to the CLI explicitly. Verify missing, invalid-signature, and
   unsupported-version diagnostics; no app/MSI download or automatic login occurs.
2. With new settings or no saved connection mode, confirm Internet mode is selected
   and starts sharing automatically without a consent checkbox or enable switch.
   Explicitly saved LAN mode must remain LAN. Confirm IPv4/IPv6 loopback listeners and absence
   of a wildcard listener. Adding an inbound firewall rule must be disabled.
3. Keep the informational risk warning visible, with no consent gate. Inspect the dedicated tunnel:
   exactly one HTTP port, empty tunnel ACL, anonymous connect-only port ACL.
4. Confirm **Copy URL** matches the displayed verified public HTTPS URL, paste it
   unchanged into Configurator, apply, and send actual Relay hooks through the
   running Client coordinator. No remote client account,
   CLI, cookies, or tokens are required.
5. Stop/cancel sharing, sign out with shared-cache confirmation, and Exit. Copying
   must be disabled and hosted access must stop. Tray close must continue hosting.
6. After Stop/Delete/Sign out, reopen: sharing must remain stopped. Enable manually
   to restore automatic startup. After ordinary Exit or a crash while sharing,
   reopen: the saved tunnel must resume without prompting. Missing credentials must
   produce an actionable visible failure while the local receiver still works.
   Change port/mode/path and verify the old effective URL remains until restart.
7. Confirm resource deletion, failure retention, partial-create recovery, account
   mismatch, extra ports/ACL drift, and competing-host rejection. Do not silently
   create replacements or rewrite unrelated tunnels.
8. Confirm dashboard crash terminates its owned host process tree but not unrelated
   devtunnel sessions. Explicitly delete disposable resources before uninstalling;
   retain failed-cleanup IDs for a later authenticated `devtunnel delete`.

Record installed-UI, independent-network, >token-lifetime, and MSI outcomes in
`ACCEPTANCE.md`. A successful synthetic development-machine tunnel probe does not
establish those outcomes.

An explicit opt-in integration test creates a disposable tunnel around an isolated
loopback Kestrel store, checks anonymous HTTPS, runs the actual Relay executable,
then stops/deletes the resource. It never signs in or installs the CLI. Run only
after approval for public synthetic traffic and completing CLI sign-in:

```powershell
$env:AGENT_SIGNALER_LIVE_TUNNEL_TEST = '1'
try {
    dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64 `
        --filter FullyQualifiedName~ActualRelayReachesAnonymousCliTunnelAndResourceIsDeleted
}
finally {
    Remove-Item Env:\AGENT_SIGNALER_LIVE_TUNNEL_TEST
}
```

If cleanup is not confirmed, the failure reports a retained non-secret
`tunnel-state.json` under the temporary `AgentSignaler-live` directory. Use the
recorded exact resource ID for authenticated cleanup; do not delete that file first.
Ordinary CI skips this cloud test.

## Setup

1. For an MSI run, install both packages as the same ordinary test user and launch
   their Start menu shortcuts. Install must not create hooks, a heartbeat task,
   startup registration, or a firewall rule by itself.
   Remote MSI does remove an existing ownership-verified legacy heartbeat task
   after InstallFiles without changing settings/hooks/startup; verify before Apply.
2. For a published-folder run, use the complete folders, not copied executables.
   Skip MSI-specific cases and keep the folders in place until integration cleanup.
3. Check port **51820** is unused before starting. If occupied, choose another
   unused port in **1024-65535**; do not terminate the unrelated listener.
4. For the LAN tests, explicitly select **Trusted LAN / VPN** in **Settings**.
   Change its port if necessary, then use
   **Exit** and reopen it. Closing the window is not Exit.
5. Open PowerShell 7 as the test user. Define the following once. Adjust executable
   paths for published-folder runs and `$Port` if changed:

```powershell
$Port = 51820
$BaseUrl = "http://127.0.0.1:$Port"
$Dashboard = "$env:LOCALAPPDATA\Programs\AgentSignaler\Dashboard\AgentSignaler.Dashboard.exe"
$Configurator = "$env:LOCALAPPDATA\Programs\AgentSignaler\Remote\AgentSignaler.Configurator.exe"
$Relay = "$env:LOCALAPPDATA\Programs\AgentSignaler\Remote\AgentSignaler.Relay.exe"
$Client = "$env:LOCALAPPDATA\Programs\AgentSignaler\Remote\AgentSignaler.Client.exe"
$Data = "$env:LOCALAPPDATA\AgentSignaler"
$TestRoot = Join-Path (Get-Location) ("AgentSignaler-Manual-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $TestRoot | Out-Null
$ManualMachine = [guid]::NewGuid().ToString()

function New-ManualStatus {
    param(
        [string]$Event = 'sessionStart',
        [string]$Session = 'manual-session-1',
        [string]$Machine = $ManualMachine,
        [string]$Name = 'Manual HTTP'
    )
    @{
        protocolVersion = 1
        eventId = [guid]::NewGuid().ToString()
        machineId = $Machine
        machineName = $Name
        client = 'copilot-cli'
        clientVersion = 'manual-test'
        event = $Event
        sessionId = $Session
        reportedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
}

function Send-ManualStatus {
    param([System.Collections.IDictionary]$Body)
    $response = Invoke-WebRequest -Uri "$BaseUrl/api/v1/status" -Method Post `
        -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 10 -Compress) `
        -SkipHttpErrorCheck -TimeoutSec 10 -NoProxy
    [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $response.Content }
}

Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 5 -NoProxy
```

Expected health: `protocolVersion = 1`, `status = ok`. A browser pointed at `/`
is not the dashboard UI; use the desktop application and `/health`.
That legacy health response alone does not confirm managed v2 capabilities:
`GET /api/v2/health` must return exactly `{"protocolVersion":2,"status":"ok"}`.
Keep `$ManualMachine` separate from managed UUIDs: once a v2 start activates a
machine, Dashboard returns 409 for direct v1 reports (even duplicates), including
after restart.

## A. Dashboard and configuration

| ID | Steps | Expected result |
| --- | --- | --- |
| A01 | Launch on a clean test profile; inspect the empty screen and warning. Click **Copy URL** and paste into a text editor. | "Waiting for machines"; trusted-network/no-authentication warning; copied URL contains this PC's hostname and the running port. It need not display `127.0.0.1`. `/health` succeeds. |
| A02 | Open **Settings**. Try blank, fractional, 1023, and 65536 ports, then a valid port. Cancel a separate edit. | Invalid values cannot be saved; valid range is 1024-65535. Cancel does not save ordinary settings. Firewall buttons perform immediate actions, not deferred Save actions. |
| A03 | Open configurator; record UUID, detected CLI version and effective home. Click **Refresh detection**. | UUID stays stable. Supported CLI is recognized. VS/VS Code information is explicitly unsupported/informational; no IDE settings are modified. |
| A04 | Paste the dashboard's **Copy URL** value into **Dashboard URL**; click **Test connection**. Reopen, then test an unused port, invalid URL `http://127.0.0.1:51820/path`, and cancel a pending test. Reopen again. Repeat after a successful applied update. | Last successful test/update URL reloads; failed, cancelled, or merely edited/previewed URLs never replace it. Successful tests save only `configurator-settings.json`, not active `remote.json`, hooks, or tasks. Without remembered settings, existing host/port settings load as a complete URL. Invalid input gives actionable feedback; UI remains usable. |
| A05 | With supported CLI, click **Preview install / repair / update**; inspect before consenting. Check consent, then edit URL/interval/startup checkbox or refresh detection. | Preview names config/hooks, exact Client/Relay paths, interval/deadline, owned current-user Startup `.lnk` creation/removal, immediate first launch and owned legacy-task removal. Preview does not start reporting. Apply requires current consent; edits/refresh invalidate it. |
| A06 | In the disposable Copilot home, create `hooks\manual-unrelated.json` containing `{"version":1,"hooks":{}}` in a text editor. Record SHA-256. Generate a valid preview, consent and apply. | Read-only health/capabilities succeed before managed setup. First-time Apply starts one Client icon; acknowledged `started` registers Idle without a hook. UUID and unrelated hook hash are unchanged; owned hook is `hooks\agent-signaler.json`. Disk commit and runtime activation outcomes are reported separately. |
| A07 | Inspect owned hook JSON and Task Scheduler (enable **View > Show Hidden Tasks**). | Hooks use absolute relay `exec`, separate `args`, and `timeoutSec: 3`, not a command-shell wrapper. New integration creates no scheduled task; `AgentSignaler-Heartbeat-<UUID>` is absent after successful migration. |
| A08 | Close Configurator; keep Client and Dashboard running without CLI activity for over eleven minutes. Then generate a hook. | Five-minute heartbeats advance contact and keep Idle/quiet activity online. Latest Copilot event is separate from contact; snapshots do not extend result holds or clear pending questions. |
| A09 | Reopen Configurator and apply a repair preview. Compare UUID, unrelated hash, startup and task list; repeat after Client Exit. | Same UUID, one exact owned startup value, no task/duplicate icon/timer, no unrelated changes; timestamped backups retained. Running Client reloads committed settings. After deliberate Exit, settings-only Apply does not restart reporting; explicit Start Client is required. |
| A10 | Optional, in a separate clean fixture: launch configurator without a discoverable CLI, or with an available older CLI build. | Discovery reports not found/unsupported; preview is blocked with CLI 1.0.80-or-later guidance. No integration is silently installed. Do not uninstall a real CLI to arrange this. |
| A11 | From a checkpoint with genuine older integration, record owned `AgentSignaler-Heartbeat-<UUID>` task XML and integration hashes. First use published folders (no MSI): load/preview, then Apply. Restore checkpoint; separately upgrade Remote MSI and inspect before opening Configurator. Include a legacy pair without Client. | Load/preview alone never changes task/files. Published-folder Apply backs up/removes owned task, migrates v3 and launches Client. MSI instead removes owned task after InstallFiles, before Apply, without requiring Client or changing settings/hooks/startup. Later first managed Apply launches Client; acknowledged startup reports Idle. No replacement task. Use E09 for Configurator rollback and G11 for separate MSI task rollback. |
| A12 | From that checkpoint, alter legacy task action/XML or place an unrelated task at expected name. Separately attempt MSI servicing, published-folder Apply and removal. | Ownership mismatch is reported; modified/unowned task is never overwritten/deleted. Restore checkpoint before normal cleanup. Mark unavailable fixtures Blocked; do not invent ownership manifests. |
| A13 | Inspect default interval 5; preview 1 and 60, then reject zero, negative, fractional and 61. Edit/Test without Apply; apply a valid change with Client running and restart Configurator. | Whole 1–60 minutes persists as v3 `heartbeatIntervalSeconds` (60–3600); preview gives two intervals plus one minute (default 11). Editing/testing does not change timer. Accepted reload announces the new interval immediately; failed IPC shows saved-but-not-applied with retry/start guidance. |

After Exit, edit URL/interval without applying and select **Start client**: it must
use saved configuration only. An already-managed stopped integration's Apply commits
settings without launching; first installation and first legacy v1/v2 integration
migration launch Client. For a running Client,
confirm its effective revision after reload. Start client or retry Apply must
recover a saved-but-not-applied result without reporting false activation success.
With an interval increase/decrease pending Dashboard acknowledgement, verify the
shorter old/new cadence is used while the displayed effective revision/interval
remains the acknowledged one. Repeat during network failure.

If CLI is unavailable, mark A05-A09 blocked and continue with B-F: neither HTTP
fixtures nor the isolated relay fixture below depends on configured CLI hooks.

## B. Legacy direct HTTP status and session tests

Use **Manual HTTP**, not the real PC card. Live CLI hooks use a different UUID,
so they cannot overwrite these tests. This v1-only fixture has no Client and no
periodic status reporting; do not apply its five-minute policy to managed clients.
Allow approximately two seconds after each send for the one-second UI refresh.
Every valid new request below should return **202**, `duplicate: false`.

```powershell
Send-ManualStatus (New-ManualStatus 'sessionStart')
```

Open the new card: details should show its hostname, client/version, UUID, latest
event, server last contact, and `manual-session-1`. "Waiting" in protocol terms is
displayed as **Waiting for input**.

### B01: ordinary transitions

Send each event separately and inspect the card and open details after each:

```powershell
Send-ManualStatus (New-ManualStatus 'userPromptSubmitted')
Send-ManualStatus (New-ManualStatus 'preToolUse')
Send-ManualStatus (New-ManualStatus 'postToolUse')
Send-ManualStatus (New-ManualStatus 'permissionRequest')
Send-ManualStatus (New-ManualStatus 'sessionEnd')
```

Expected in order: Executing, Executing, Executing, Waiting for input, Idle.
Do not paste the whole sequence if observing each intermediate state.

### B02: result holds and replacements

1. Send `agentStop`: Succeeded for 60 seconds, then Waiting for input.
2. Repeat `agentStop`, then immediately send `preToolUse`: Succeeded remains until
   the original hold expires, then Executing. The ordinary event does not reset
   or cancel the hold.
3. Send `errorOccurred`, then `sessionEnd`: Failed remains for the hold, then Idle.
4. Repeat separately with `postToolUseFailure`, and with:

   ```powershell
   $failed = New-ManualStatus 'postToolUse'
   $failed.toolFailed = $true
   Send-ManualStatus $failed
   ```

   Each produces Failed, then Waiting for input unless a later event changes the
   underlying state.
5. Send `errorOccurred`, then `agentStop` before expiry: the newer Succeeded result
   replaces Failed and starts a new hold.

Use a stopwatch from receipt, observe around 59 seconds and again after 61-62
seconds. Record actual elapsed times. The polling UI cannot establish an exact
subsecond boundary; exact 60-second boundary coverage belongs to automated tests.
Finish by sending `sessionEnd` and letting all overlays expire.

### B03: multiple sessions on one machine

Use `New-ManualStatus <event> <session>` to address separate sessions. On the
same UUID create these states, inspecting after each step:

| Steps | Expected aggregate |
| --- | --- |
| Session `a`: `agentStop`; other sessions Idle | Succeeded |
| Session `b`: `preToolUse` while `a` still Succeeded | Executing |
| Session `c`: `permissionRequest` | Waiting for input |
| Session `d`: `errorOccurred` | Waiting for input from `c`; `d` retains its Failed result |
| End `d`, then wait for its hold to expire | Waiting for input from `c` |
| End `c` | Executing from `b` |
| End `b` and `a`; wait out any remaining holds | Idle |

Details list distinct sessions; ending one does not end others. Priority is
**Waiting > Failed > Executing > Succeeded > Idle**; Offline overrides all when
contact expires. Ended entries remain as ordering tombstones until capacity is
needed. New-session hooks can evict the oldest ended entry only after its result
hold expires; active sessions and unexpired results are never evicted. There is
no periodic snapshot retirement. Session end removes only that session from
aggregation immediately, even when terminal result metadata remains retained.

### B04: duplicate and out-of-order requests

```powershell
$first = New-ManualStatus 'preToolUse' 'ordering'
Send-ManualStatus $first
Start-Sleep -Seconds 2
$newer = New-ManualStatus 'permissionRequest' 'ordering'
Send-ManualStatus $newer
Send-ManualStatus $first
$older = $first.Clone()
$older.eventId = [guid]::NewGuid().ToString()
Send-ManualStatus $older
$equal = $newer.Clone()
$equal.eventId = [guid]::NewGuid().ToString()
$equal.event = 'preToolUse'
Send-ManualStatus $equal
```

Expect replay of `$first`: 202 with `duplicate: true`. Older and equal-timestamp
requests with new IDs can be accepted but must not replace the session's newer
Waiting state. For the duplicate alone, verify server last contact does not move.
Do not assume the same liveness rule for a new event UUID.

Exit/relaunch the dashboard and replay `$first`: still duplicate; session state
and any saved display name survive. No activity timeline is expected.

### B05: session end and delayed hooks

```powershell
Start-Sleep -Seconds 2
$ended = New-ManualStatus 'sessionEnd' 'ordering'
Send-ManualStatus $ended
$delayed = $newer.Clone()
$delayed.eventId = [guid]::NewGuid().ToString()
Send-ManualStatus $delayed
```

Expected: `ordering` remains Idle after `sessionEnd`; the delayed older hook cannot
replace its newer end state. Other sessions are unaffected. A fresh
`New-ManualStatus 'sessionStart' 'ordering'` can start it again. No snapshot is sent.

### B06: Offline and recovery without disconnecting the PC

Save and send one fresh request for Manual HTTP:

```powershell
$quiet = New-ManualStatus 'preToolUse'
Send-ManualStatus $quiet
```

Record **Server last contact**, then
send nothing for that UUID for five minutes. Keep dashboard running and PC awake.
At about 4:59 it should remain online; after 5:01-5:02 expect Offline. Other
machines' hooks and repeated `GET /health` checks must not keep this card online.
Replay `Send-ManualStatus $quiet`:
still Offline and duplicate. Send a fresh event: online again within a UI refresh.
Do not change the system clock or stop the dashboard to simulate this case.

## C. Managed Client/Relay, recovery, and privacy without a live CLI

Create a separate v3 fixture and explicitly start Client with its exact absolute
config path. This does not register hooks, startup, or a task and does not touch
Configurator's state. Use the complete co-installed remote publish directory;
keep the fixture coordinator running for hook-delivery cases (pause it for C03/C07's
read-only assertions). A legacy v1 config passed to
updated Relay is not a substitute for a running managed coordinator.

```powershell
$RelayConfig = Join-Path $TestRoot 'remote.json'
$RelayMachine = [guid]::NewGuid().ToString()
@{
    version = 3; dashboardBaseUrl = $BaseUrl; heartbeatIntervalSeconds = 300
    machineId = $RelayMachine; machineName = 'Manual relay'; clientVersion = 'manual-test'
    relayPath = $Relay
} | ConvertTo-Json | Set-Content -LiteralPath $RelayConfig -Encoding utf8

Start-Process -FilePath $Client -ArgumentList @('--background', '--config', ('"{0}"' -f $RelayConfig))
# Confirm exactly one fixture Client icon and an acknowledged Idle card before sending hooks.

function Send-ManualHook {
    param([string]$Event, [string]$Session = 'relay-session')
    @{
        sessionId = $Session
        timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        prompt = 'MANUAL-PRIVATE-PROMPT'
        toolArgs = @{ text = 'MANUAL-PRIVATE-ARGS' }
        toolResult = @{ resultType = 'success'; textResultForLlm = 'MANUAL-PRIVATE-OUTPUT' }
        error = @{ message = 'MANUAL-PRIVATE-ERROR' }
    } | ConvertTo-Json -Depth 6 -Compress | & $Relay hook --event $Event --config $RelayConfig
    "Relay exit code: $LASTEXITCODE"
}

Send-ManualHook 'sessionStart'
```

Raw hooks use Unix-millisecond `timestamp`, unlike HTTP `reportedAtUtc`.
Never post raw hook JSON to either HTTP endpoint. Relay sends only sanitized IPC;
Client serializes local state and managed v2 HTTP. An IPC acknowledgement proves
local acceptance, not remote delivery. Identify the fixture by config/UUID before
using its tray Exit; do not stop the separately configured Client.

| ID | Steps | Expected result |
| --- | --- | --- |
| C01 | Send `sessionStart`, `preToolUse`, `permissionRequest`, and `sessionEnd` separately with the helper. | Exit 0; Manual relay card follows Waiting, Executing, Waiting, Idle after delivery. Sanitized state is scoped to `$TestRoot`; diagnostics, when emitted, contain no payload. |
| C02 | Keep fixture Client alive; Exit Dashboard. Time `Send-ManualHook 'preToolUse'`; reopen Dashboard without more hooks and wait for retry/heartbeat. Then send `permissionRequest`. | Relay exits 0 within its budget. Client retains sanitized local state; a current snapshot repairs missed delivery without more hooks, same UUID, no replay burst. Failed network delivery is not reported as delivered. |
| C03 | Exit fixture Client first so periodic traffic cannot confound the probe. Exit Dashboard, run `& $Relay test --config $RelayConfig`, record exit code; reopen and repeat. Compare state hashes/contact; also test a fresh config UUID without starting its Client. Explicitly restart original fixture for C04. | Test returns nonzero on health failure and 0 on compatible health. `DashboardConnection.TestAsync` uses read-only `GET /api/v2/health` for this v3 fixture; legacy v1/v2 configs retain `/health`. No status POST, registration, state mutation, liveness refresh or Client auto-start. Hook exit 0 alone is not delivery proof. |
| C04 | Pipe `'{'` to `& $Relay hook --event sessionStart --config $RelayConfig`; repeat with `('x' * 65537)`. | Exit 0, bounded category diagnostic, no new session or raw payload in logs. Hook input limit is 64 KiB, distinct from the HTTP 32 KiB limit. |
| C05 | Exit fixture Client and wait for Relay invocations to finish. Back up only fixture state, corrupt or move its session file, and explicitly restart Client; separately test corrupt generation state using an approved fixture. | New runs start Idle until fresh hooks; prior-run active assumptions are not reused. State errors are visible and bounded. Corrupt generation state must not silently reset ordering. Never corrupt real-user state or an open database; restore backups before continuing. |
| C06 | Inspect fixture JSON and `relay.log`/`relay.log.1` if present. Search for all `MANUAL-PRIVATE-` markers. Inspect only Agent Signaler-owned files, not the CLI's own logs. | No private markers persisted. Stored information is bounded session/status metadata; logs contain timestamps/category codes, not payloads/error text. Missing/rotated logs are not evidence of payload transmission safety. |
| C07 | Exit fixture Client to exclude periodic traffic; run `& $Relay heartbeat --config $RelayConfig`, record exit code and compare state/contact. Explicitly restart for C08. | Removed command returns nonzero with no report, state/liveness mutation or Client restart. Periodic heartbeats belong to Client, not this command. |
| C08 | Send Executing, then Exit the fixture Client; measure shutdown and send more hooks after it stops. Open/Test Configurator without explicitly starting Client. Repeat with Dashboard unavailable. | Acknowledged terminal offline immediately overrides activity. Exit completes within five seconds normally; unreachable/unacknowledged offline gives a diagnostic and timeout fallback, not a delivery claim. No hook HTTP fallback, auto-restart, replay queue, timer or post-exit retries. |
| C09 | Explicitly restart fixture Client using the command above, then send a new hook. For installed context, test Start Client shortcut separately. | New acknowledged generation initially reports Idle; new hook updates activity. Hooks missed while stopped cannot be reconstructed, including a CLI session spanning restart. This is not C02's transient-network recovery. |
| C10 | With default interval, keep the coordinator quiet beyond eleven minutes. Then stop only its exact recorded test PID uncleanly, or block its network with an approved fixture; keep Dashboard awake. | Quiet heartbeats keep online. After last accepted contact, online at 659 seconds and Offline at 660 (allow UI polling slack); deadline is two intervals plus one minute, not one interval. Details identify interval and timeout versus explicit offline. |
| C11 | With controlled v2 protocol fixtures, replay stale/duplicate sequences, prior generations, and delayed hook/heartbeat after terminal offline; restart Dashboard and repeat. Try malformed per-kind fields, then a valid newer start; inspect metadata after thousands of heartbeats. Mark Blocked if no controlled fixture exists. | Stale/terminal traffic cannot renew liveness or revive activity; only a newer valid start begins a run. Invalid/uncommitted reports do not advance watermarks. Once managed, v1 for that UUID is rejected; bounded per-machine watermarks survive restart without a receipt row for each heartbeat. |

Deliberate legacy downgrade is a separate destructive recovery test, not automatic
fallback: stop Client and remove owned startup/hooks through normal Configurator
actions, restore
matching old binaries/config/integration backups, preserve needed details/mapping,
then explicitly remove the machine locally in Dashboard. This resets managed mode
and replay history and loses saved display name, notes and mapping; preserve these
manually. There is no automatic downgrade endpoint. Only then verify legacy v1
delivery for the retained UUID. Never lower/delete generation or identity files
to bypass ordering protection.

The relay work budget is 2.2 seconds; the generated hook host timeout is 3 seconds.
Measure several warm runs for C02/C04. Record full process elapsed time separately
from the internal work budget; investigate runs exceeding the host timeout.
Loopback refusal is only one failure mode, not proof of timeout behavior on a
black-holed network. A successful marker search is a local-persistence check, not
a packet-capture verification of all outbound traffic.

## D. Legacy v1 HTTP validation and capacity

For each invalid request, expect the indicated status, an `errors` array without
echoed input, no new card/session, and an operational `/health` afterward.
These requests target `/api/v1/status` only. Its heartbeat/snapshot rejection is
not rejection of valid managed `/api/v2/reports` envelopes (covered by C11).

```powershell
# Malformed JSON: 400
Invoke-WebRequest "$BaseUrl/api/v1/status" -Method Post -ContentType 'application/json' `
    -Body '{' -SkipHttpErrorCheck -NoProxy

# Wrong media type: 415
Invoke-WebRequest "$BaseUrl/api/v1/status" -Method Post -ContentType 'text/plain' `
    -Body '{}' -SkipHttpErrorCheck -NoProxy

# Body over 32768 bytes: 413 (ASCII makes byte size unambiguous)
Invoke-WebRequest "$BaseUrl/api/v1/status" -Method Post -ContentType 'application/json' `
    -Body ('x' * 32769) -SkipHttpErrorCheck -NoProxy
```

| ID | Steps | Expected result |
| --- | --- | --- |
| D01 | Run the three requests above. | 400, 415, 413 respectively. |
| D02 | Start with a fresh `New-ManualStatus` for each mutation: remove `eventId`; set `protocolVersion` to 2; set `machineId` to the empty UUID; set `client` to `visual-studio`; set `event` to an unknown string or integer; remove `sessionId`; add `prompt = 'MANUAL-PRIVATE-REJECT'`. Send each. | Each returns 400. Required fields, enum values and unknown-property rejection are enforced. |
| D03 | Fresh requests: set `machineName` to 129 ASCII characters; `sessionId` to 129; `clientVersion` to 65; set `toolFailed = $true` on `sessionStart`. | Each returns 400. |
| D04 | Run `Send-ManualStatus (New-ManualStatus 'heartbeat')`. Separately add `state = 'idle'` or `sessions = @()` to otherwise valid fresh hook requests, then try both fields together. | Each returns 400: heartbeat events and legacy snapshot fields are rejected, even when empty. No state/liveness mutation or machine registration. |
| D05 | On a new UUID, send `sessionStart` for `capacity-1` through `capacity-64`; then `capacity-65`. | First 64 return 202; the 65th returns 409. Existing state survives. Remove this machine afterward. |
| D06 | Optional capacity run: close live CLI sessions and Exit every owned test Client; wait for in-flight Relay to exit, then remove cards in the disposable dashboard. Send 25 distinct UUIDs/names; send a 26th. | 25 accepted, 26th 409; resize/scroll usable. Remove one and retry: 202. Stopped test coordinators cannot repopulate cards; leave unrelated clients untouched. |
| D07 | Repeat D05's full 64-session fixture before removing it. Save an old hook for `capacity-1`, then send `agentStop` and `sessionEnd` for it. Try a new-session hook during the result hold, then a fresh one after expiry. Restart dashboard and send the saved old hook with a new event UUID. | During the hold, capacity still returns 409. After expiry, the new session is accepted by evicting the ended entry; the other active sessions remain. The persisted retired-through timestamp prevents the stale hook from recreating the evicted session. Remove the fixture afterward. |

Example for D05 (reserve a free machine slot first):

```powershell
$CapacityMachine = [guid]::NewGuid().ToString()
1..65 | ForEach-Object {
    $result = Send-ManualStatus (New-ManualStatus 'sessionStart' "capacity-$_" $CapacityMachine 'Manual capacity')
    [pscustomobject]@{ Session = $_; Status = $result.Status; Body = $result.Body }
}
```

For D06, use `[guid]::NewGuid().ToString()` per machine and names such as
`Manual capacity PC 01`. Synthetic cards are not separate physical clients.
Do not create more than the documented bounded fixtures. Chunked-body limits,
exact boundary values, storage-failure 503 responses, and high-concurrency behavior
are covered by the existing automated suites; this manual section does not claim
to have verified them.

## E. UI, persistence, and endpoint changes

Dev Box cases below are procedures, not completed checks. Use an approved disposable
Azure session for read-only live discovery/launch and existing isolated fakes for
malformed responses, exact limits, process failures, and persistence failures.
Do not mutate Dev Boxes or corrupt a real user's database. Record unavailable
fixtures/prerequisites as **Blocked** or **Not run**, not Pass. CLI/path tests
execute the selected installation as the current user; use only trusted fixtures.

| ID | Steps | Expected result |
| --- | --- | --- |
| E01 | Open Manual HTTP details; save `AAA Manual workstation`, close/reopen details, then Exit/relaunch dashboard. Clear the name and save. | Display name persists across restart, cards reorder by name, and clearing the name restores hostname. UUID and session data persist. |
| E02 | Remove a synthetic card, cancel confirmation first, then confirm. Restart before sending another event. Finally send a fresh event for its same UUID. | Cancel preserves it; confirmed removal persists through restart. New report recreates the card without its removed custom name. Removal does not block future reporting. |
| E03 | Toggle **Compact view**, resize narrow/wide, test System/Light/Dark appearances and Windows contrast mode. | Main cards remain square/readable, scrolling works, text and symbols convey state without relying solely on color. The main dashboard is not always on top. |
| E04 | Use Tab, Shift+Tab, Enter, Space and Escape in cards, settings and configurator. Enable Narrator; inspect card/control names. Test increased display/text scaling on the same display. | Focus is visible; primary actions are reachable; labels/status are announced; dialogs do not trap focus or hide required actions. Record clipping/accessibility defects. |
| E05 | Minimize, restore using tray; close window, restore again. Test `/health` while hidden. Launch the same dashboard executable again. | Tray remains, host stays responsive; existing instance/window is activated rather than a second receiver. |
| E06 | Use explicit **Exit**, then test `/health`; reopen. | Request fails because port closed; no stale tray icon remains; reopening restores persisted cards/settings. |
| E07 | Save a different unused port. Check Copy URL and both health URLs before restart; then Exit/reopen and retest. Update `$Port`/`$BaseUrl`. | Saved port is marked restart-required; old port remains active until restart. Afterward only new port responds. |
| E08 | With Client running, update Configurator to E07's new endpoint; preview, consent, Apply. Check UUID, paths and unrelated hash. For C fixture changes, Exit it, update `dashboardBaseUrl`, explicitly restart. | Probes alone do not mutate presence. Committed endpoint change pauses old work, best-effort reports offline there, disposes old transport and starts a new generation at the new endpoint. Old unreachable Dashboard ages out with warning. Identity persists; a new run is Idle until new hooks. |
| E09 | Record working config/hook/manifest hashes, exact startup value and any legacy task XML. Apply an unused port; repeat A11's published-folder branch from its pre-migration checkpoint, without MSI servicing. Separately inject runtime reload/launch failure after commit using a controlled fixture. | Configurator transaction failure preserves/restores owned files/startup/task and working runtime config, retaining backups. An MSI-migrated task-free baseline stays task-free: Configurator does not restore a task removed by an earlier committed MSI. Foreign values survive. Post-commit runtime failure is saved-but-not-applied with explicit recovery. Reopen to load saved URL; no assumed UI auto-reset. |
| E10 | With existing settings lacking the new property, minimize with two or more computers. Cover the compact window with another app; send new states, rename/add/remove computers. Restore via tray or a second launch. | **Compact view when minimized** defaults on. Only a sorted vertical list of 64 x 64 logical-pixel tiles appears, with no title bar or toolbar, at the monitor's top-right work area. It stays on top, updates live, and disappears when the dashboard is restored. |
| E11 | Disable **Compact view when minimized**, Save, minimize, restore, Exit/relaunch and minimize again. Re-enable it, then close to tray and launch with `--background`. Exit while compact tiles are visible. | Disabled preference persists independently of main card density; minimizing only hides to tray. Closing/background startup show no compact window. Exit closes both windows and the receiver. |
| E11a | Right-click different compact tiles and use Connect, Show full dashboard, and Exit in separate runs. Open the menu with Shift+F10 on a focused tile. Repeat a right-click while that machine is connecting, and after tiles are renamed/reordered. | Menu entries appear in that order. Connect runs the clicked tile's normal connection action, preserving missing-mapping/error navigation. Show full dashboard restores the main window and hides compact view. During a connection, Connect is disabled but the other menu actions remain available. Exit cancels owned work and shuts down both windows and the receiver rather than hiding to tray. |
| E12 | Minimize with no computers, then send the first report. Test the maximum 25 computers, scrolling to the last tile with wheel/touch/keyboard. Repeat on secondary monitors at 100%, 150%, and 200% DPI and with long names. | Empty lists show no extra UI. First report creates the compact view. Tiles stay 64 x 64 logical pixels, list height stays within the work area, names truncate with full-name/status tooltips, and every tile is accessible. |
| E13 | On a disposable Windows 11 x64 machine with Windows App 2.0.804.0+ and trusted Azure CLI 2.90.0+, sign in separately to Azure CLI with Dev Center read and Dev Box access. Select Settings > Dev Box > Refresh Dev Boxes without configuring endpoints. Open a full-size machine tile, choose a Mapped Dev Box different from the reported hostname, and select Save mapping. With no matching connection window, launch from details and compact mode; rename the display name and repeat. | Dev Centers and assigned Dev Boxes are discovered automatically through CLI requests. Full-size tiles open details and show mapping status independent of color. Save mapping resolves fresh validated data before atomic persistence, without launching. With no matching window, both normal launch actions refresh the saved connection and save before Windows App activation. No hostname heuristic, fallback client, URI display, or credential collection occurs. |
| E14 | On a clean profile with no catalog or endpoint settings, open unmapped machine details and select an unmapped compact tile. Select Refresh Dev Box list directly in details. | Opening details alone performs no discovery. The explicit refresh searches Dev Centers and Dev Boxes without a Settings detour or manual endpoint/project/name/UPN/tenant editors. Compact mode restores the correct picker and never chooses or launches a catalog item. Connection statuses remain Not configured, Ready, Sign-in required, or Unavailable. |
| E15 | Select an unsaved picker item on both an unmapped machine and a machine already mapped elsewhere. Exercise Sign in with Azure CLI success, nonzero exit, wrong account/tenant, cancellation, expired session, and timeout using fakes or an approved disposable session. Close the browser separately from cancelling Dashboard. | Sign-in only signs in/validates identity or refreshes the already stored mapping, using its tenant when applicable. It never persists the unsaved picker selection or launches. Only Save mapping replaces the mapping. Failed/cancelled/mismatched sign-in leaves it unchanged. Browser closure alone is not cancellation; no raw output is shown. |
| E16 | During Save mapping, sign-in, connection refresh, launch, and clear, try repeated clicks/Enter, picker changes, and the same compact tile. Cancel, close details, and Exit in separate runs. | Operation-specific progress stays responsive; conflicting actions/selection and that tile are disabled. One operation per machine runs. Cancel, close, and Exit terminate only owned work and await cleanup; Exit waits before disposing storage. An interrupted candidate mapping never replaces the saved one. |
| E17 | Refresh connection without launching. With no matching window, simulate missing protocol, unsupported CLI, offline API, denied/missing Dev Box, malformed/unsafe responses, and rejected activation through fakes. Repeat with a saved cached connection. | Refresh saves only verified data, without launching. Failures are classified and contain no raw CLI output or connection URI. Compact failure restores details, including when a cache exists; cache never launches automatically. |
| E18 | With cached data and no matching window, inspect its local retrieval time and select Open last known connection explicitly in details. Repeat offline and with an old retrieval timestamp. | The unchanged validated cached connection launches with no CLI/network call and no expiry deletion. The action and its timestamp are hidden without a cache. Compact mode reuses an existing window or resolves fresh data, never the cache. |
| E19 | Cancel then confirm Clear connection mapping using its destructive secondary button. Rename/report/restart; later repair, upgrade, uninstall, and reinstall on a disposable system. | Cancel retains mapping; confirmation clears only mapping/cache, not machine history/name. Reports and renames preserve mappings. Repair/upgrade/uninstall retain per-user mapping/cache; reinstall reuses them. Only explicit clear or machine removal deletes a mapping. |
| E20 | Open Settings > Dev Box. Test a blank path, a custom trusted az.exe, and the official az.cmd installation. Enter a different trusted path, run Test Azure CLI status, then refresh the catalog before and after Settings Save without restarting. Cancel/reopen in a separate run; finally Exit/reopen. | All former Azure CLI controls and trusted-installation warning remain in Dev Box. Test uses the entered path, including unsaved edits, and reports path/version/sign-in without login, mapping changes, or activation. Catalog/connection operations use the running path until restart, even after Save. Cancel leaves saved settings unchanged. Save persists independently of devtunnel.exe; after restart Dev Box operations use the new path. Official az.cmd uses adjacent Python directly without a shell; installations/dependencies must be trusted and protected from writes. |
| E21 | Test relative paths, missing files, unsupported launcher names, malformed installations, unsupported versions, process-start failures, nonzero exits, and malformed JSON using isolated fixtures/fakes. Repeat with no Azure sign-in and with a trusted 32-bit or unsigned CLI. | Status is validated by running version and account commands, not by inspecting PE headers, architecture, or signatures. Detailed, selectable diagnostics identify the failed stage and give actionable recovery steps, without exposing raw CLI output, tokens or connection URIs. No missing or invalid selection silently falls back to another CLI. |
| E22 | Start a slow CLI status test; cancel it, close Settings, and Exit in separate runs. Try repeated clicks and path edits while testing. | Dashboard stays responsive; duplicate tests and path edits are disabled during the test. Cancellation terminates only owned work and is awaited before shutdown. Reopening Settings allows a fresh test. |
| E23 | Open Settings and visit all four tabs using mouse and keyboard. Edit ordinary settings, switch tabs, and Save or Cancel. Save an invalid port or CLI path while viewing another tab. Repeat at increased display/text scaling with scrolling and Narrator. | No standalone Azure CLI tab or Dev Center Add/Remove/endpoint editor remains. Discovered centers and boxes are read-only. Save validates ordinary settings with visible feedback. Cancel discards ordinary edits, not an already refreshed in-memory catalog. Lists, picker, diagnostics, and Save/Cancel remain accessible. |
| E24 | Use CLI fakes returning one and multiple enabled public-Azure subscriptions, disabled subscriptions, other tenants, and other users. Inspect all request arguments and the current CLI selection before/after refresh. Include a malformed subscription list and over-limit lists. | Automatic discovery searches only subscriptions belonging to the validated current user/tenant. Up to 1,000 eligible subscriptions are supported; limits and invalid subscription-list data fail explicitly without truncation. Requests are typed and bounded; no account set, tenant/cloud switch, shell, or extension installation occurs. Subscription identifiers appear only in local Settings and explicitly enabled debugger output. |
| E25 | Use ARM fixtures returning public Dev Center endpoint metadata, duplicate/malformed resources, HTTP or foreign-host endpoints, credentials, nondefault ports, paths, query/fragment, and inconsistent subscription/resource identities. Include safe and unsafe ARM pagination links. | Discovered endpoints are normalized and validated before data-plane requests. Pagination is restricted to the public management origin and searched subscription/resource-list scope. Unsafe or inconsistent metadata discards that subscription's results and reports a warning before continuing with other subscriptions. All-subscription failure and global endpoint-limit violations still fail the refresh. |
| E26 | Load older settings containing valid, duplicate, invalid, null, or obsolete-shaped DevCenterEndpoints values alongside valid theme/port/CLI preferences. Open/refresh/cancel, then explicitly Save and restart. | Legacy endpoint values are ignored, never seeded or queried, and do not discard unrelated valid preferences. Load/refresh/cancel does not rewrite the file; Save omits the retired setting. Existing machine mappings remain intact without a database migration. |
| E27 | Discover centers in one and multiple subscriptions, including equal Dev Box names across projects/centers. Fail one subscription on its first or a later ARM page, then succeed in another subscription. Repeat with all subscriptions failing and with cancellation after a failure. | Successful subscription results merge and sort deterministically. An incomplete subscription contributes no endpoints. Each failed subscription is listed with a safe warning; processing continues unless cancelled. A nonempty partial catalog is marked Partial results; all-subscription failure or cancellation retains the previous catalog. |
| E28 | With a successful catalog and saved mappings, simulate one center failing after another succeeds, then a fully empty result. Also test empty/failure with no prior snapshot. | No partial successful catalog replaces the snapshot. Failure and empty-result messages are distinct; both retain the previous catalog and its successful timestamp, plus all mappings. Without a prior snapshot, the list remains empty with actionable status. |
| E29 | Use fixtures for both ARM and data-plane pagination, foreign-origin/HTTP/relative/credential-bearing nextLink, duplicate identities, malformed envelopes/field types, and item URI endpoint/project/name mismatches. Include documented extra properties and equal names on distinct identities. | Valid pagination and extra properties work; unsafe links are rejected before invocation. ARM failures are isolated to their subscription. Dev Box data-plane failures still fail the refresh, preserving the previous snapshot. Different endpoint/project identities are not collapsed. UI errors never echo raw responses. |
| E30 | Exercise subscription-record/subscription/ARM-page/resource/center limits, plus 20 pages and 250 items per center, 1 MiB per output stream, 30-second discovery requests and 15-second current-account timeout. Include nonterminating/repeated ARM and data-plane pagination. | Boundary-valid results succeed; overflow, excessive pagination, and timeout fail boundedly without publishing partial results or silently limiting the search. Only owned process trees stop; the previous catalog and mappings remain unchanged. Use isolated fixture evidence, not live resource mutations. |
| E31 | Test no discovered centers, discovered centers with no assigned boxes, missing/unsupported CLI, no sign-in, disabled/noninteractive account, invalid context, ARM/data-plane access denial, offline, malformed responses, cancellation, and timeout. | Empty-center and empty-box results are distinct. Errors identify subscription/ARM discovery or Dev Box access failures safely and explain required permissions. No error requests manual endpoint management or leaks account/subscription identifiers or raw URLs. |
| E32 | Open/close Settings and details, open a picker, wait through normal dashboard refresh intervals, launch an already mapped machine, then restart Dashboard. Observe isolated CLI invocation records. | No automatic, periodic, picker-open, or launch-triggered catalog discovery occurs. Only explicit catalog refresh buttons discover. Normal launch reuses a matching window or resolves only the stored connection. The catalog is in memory and empty after restart until explicit refresh; saved endpoints and mappings survive. |
| E33 | During slow subscription, ARM, and data-plane discovery, try CLI path edits/testing, Settings Save, repeated refreshes, and Save mapping. Launch a different mapped machine. Cancel refresh, close Settings/details, and Exit in separate runs; cancel before/between/during requests. | One global refresh runs. CLI editing/testing, Settings Save, extra refreshes, and Save mapping are disabled while busy. Stored-mapping launch remains independent. Cancel/close/Exit await owned CLI cleanup before disposal, retain the last snapshot, and permit a later explicit refresh. No unrelated process is terminated. |
| E34 | Match a saved mapping by endpoint/project/name after catalog reorder. Explicitly map different machines to different entries, then two machines to the same entry. Select another entry without saving, close/reopen, and change hostname/display name/note. | Exact catalog identity restores selection; names, note, pool, or list order never choose or save a mapping. Duplicate explicit choices across machines are permitted. Unsaved selection never changes the stored mapping or launch target. |
| E35 | With an existing mapping, choose another item and Save mapping. Switch CLI account/tenant after catalog refresh, and inject resolution, cancellation, and atomic persistence failures in separate runs. Repeat successfully. | Save mapping freshly validates the account against the catalog identity, resolves the chosen connection, and atomically persists the complete mapping/cache before updating saved UI state. Every failure preserves the old mapping/cache with no launch. Only a successful explicit Save mapping replaces it. |
| E36 | Use fixtures returning a successful nonempty automatic catalog that excludes a saved mapping's center or Dev Box. Open its details and launch/refresh it. Separately fail ARM or data-plane discovery, or return an empty catalog as in E28. | Missing saved identity is retained and displayed as Unavailable: name, never cleared or automatically replaced. It remains launchable/refreshable under normal account, validation, and fresh-resolution rules. Failed/empty catalog refresh removes neither mappings nor prior catalog entries. |
| E37 | Inspect successful discovery, CLI diagnostics, details summary, full/compact tiles, global errors, logs, and receiver/webhook traffic using approved fixtures with recognizable identity/URI markers. | Validated UPN, tenant, and selected subscription appear in the local Settings Dev Box tab, separately from retained catalog identity. Details show endpoint host/project/pool/refresh information without exposing account identity. Both Debug and Release builds intentionally write full arguments, stdout, and stderr to an attached debugger as described below. Catalog data never enters webhook traffic or the local receiver. Redact sensitive evidence; local log inspection alone is not packet-level proof. |

### Windows App existing-session reuse

Use a disposable mapped Dev Box. These live checks are **Not run** until recorded
on the minimum supported Windows App version (2.0.804.0) and every supported/current
version. Capture only approved disposable Dev Box title formats in the release
record, never unrelated titles or connection data. Update the pure matcher fixtures
if those versions use a narrower stable format; do not substitute fuzzy matching.

| Case | Procedure | Expected result |
| --- | --- | --- |
| E38 | With no connection window open, launch from details and compact mode. | Fresh resolution and atomic save precede normal shell activation. Compact success leaves Dashboard hidden. |
| E39 | Put a connected, non-minimized window behind another application and launch again from details and compact mode. | The existing mapped window comes to the foreground without another connection; compact mode does not restore Dashboard. |
| E40 | Minimize that connection window and launch again. | Only the matching window is restored and foregrounded; no duplicate connection opens. |
| E41 | Open mapped Dev Boxes whose names share prefixes or contain resource punctuation, such as `box1`, `box10`, and `box1-2`; rename the local machine/display name. | Only the exact mapped name at accepted title boundaries matches. Machine/display names never select a connection. |
| E42 | Open multiple matching windows and arrange their z-order. | The topmost matching window wins; lower matches are not managed. |
| E43 | Select **Open last known connection** with an existing window while offline. Use a controlled fake to make the protocol association unavailable. | Reuse needs no CLI/network/protocol probe or shell activation. URI and retrieval time stay unchanged; success says "without refreshing". |
| E44 | Repeat normal launch with an existing window while CLI/API/protocol access is unavailable through controlled fakes. Compare the saved retrieval time. | Reuse occurs before those dependencies and makes no CLI call or persistence change. |
| E45 | Force restore/foreground failure on a live matching window with a controlled fake; retry after clearing the fault. Also simulate disappearing handles and a match appearing during resolution. | Live failure is sanitized, never launches a duplicate, and releases busy/activation gates. Disappearing matches fall through. A late match is reused after the fresh mapping is committed. |
| E46 | Inspect logs, debugger output, state text, and errors during reuse with recognizable private title/handle/URI markers in controlled fixtures. | Enumeration emits no titles, Dev Box names, handles, PIDs, or URIs. No child text, remote contents, cross-desktop/session search, or persisted window state exists. Existing CLI debugger behavior is unchanged when fresh resolution is actually needed. |

In the Dev Box tab, verify optional targeted searches:

- Supply a subscription GUID; the app validates the account, skips `account list`,
  and issues ARM requests only for that GUID. Blank fields restore automatic discovery.
- Supply a Dev Center name instead; the app calls `devcenter dev dev-box list`
  with that name, `--user-id me`, and `--output json`. It does not issue Dashboard
  subscription-list/ARM enumeration commands. Verify JSON arrays, empty arrays,
  unsafe/missing item URIs, duplicate items, size limits, and cancellation.
- Invalid GUIDs, empty GUIDs, invalid names, and specifying both targets disable
  refresh with a validation message. Inputs are disabled while refreshing and are
  not persisted. A missing extension must not trigger automatic installation.

In the Dev Box tab, verify that a refresh shows the current discovery stage, subscriptions
and Dev Centers searched, failed subscriptions, current subscription/host and page,
responses read (REST pages or a combined named-center CLI response), and Dev Boxes
found so far. The busy indicator stops on completion, cancellation, or failure. Counts
describe the latest attempt, not a partial replacement of the catalog. A failure after
account validation retains that attempt's user, tenant, subscription, and validation time;
a subsequent failed account check must not present the previous identity as current.

**Azure CLI debugger logging (Debug and Release):** Visual Studio's Debug Output receives a correlated
`[AzureCLI #...]` invocation and response for every CLI child operation, including the
version probe. Responses include exit code, elapsed milliseconds, and both output streams.
Long streams are split into offset-labeled, JSON-escaped chunks without truncation.
Startup failures, cancellation, and timeouts report that no complete response was received.
Use approved fixtures to verify nonzero exit codes, pagination, and long/multiline output.
Both build configurations use `System.Diagnostics.Debug.WriteLine`. Its calls are retained
in Release by a file-local `DEBUG` definition in `AzureCliProcess.cs`, without changing other
Release build behavior. Without an attached debugger, logging is skipped. `Debug.WriteLine`
also safely handles the debugger detaching before a write.
Verify a refresh both with and without a debugger attached in Debug and Release builds.

**Sensitive data warning:** Debug output is intentionally unredacted and can contain
account identities, subscription IDs, connection URLs, or credentials. Do not attach it
to issues or share it without redaction. No new log files or telemetry are created.

## F. Optional live Copilot CLI end-to-end

Run after A06 with the correct endpoint. Close old CLI sessions and start fresh
ones so hooks are loaded. Observe the actual PC card, not synthetic fixtures.
Use only harmless actions in the disposable directory; do not grant broad tool
permissions just to induce states.

| ID | Steps | Expected result |
| --- | --- | --- |
| F01 | Start CLI, submit a harmless prompt requiring a tool, observe tool execution and completion. | SessionStart -> Waiting; prompt/pre-tool/successful post-tool -> Executing; agentStop -> Succeeded, then Waiting after hold. Rapid intermediate states may require inspecting details; do not infer missing hooks solely from UI polling. |
| F02 | Request a harmless action requiring explicit approval; leave permission dialog pending, then cancel/deny or approve normally. | PermissionRequest -> Waiting. Reporting does not make or change the permission decision. Subsequent state follows actual emitted hooks. |
| F03 | Request a harmless failing command, such as reading a nonexistent file in the test directory. | If CLI emits tool failure/error, Failed appears for its hold. Record emitted event and CLI version; a tool failure and overall agent completion can replace one another's result. |
| F04 | Open two CLI sessions; leave one waiting and run work in the other. End one normally, then the other. | Separate session IDs; documented aggregate priority. Ending one does not end the other; Idle follows final ends/result holds and remains online while Client heartbeats arrive. |
| F05 | Close Configurator; keep Client alive and repeat F01. Exit Dashboard, run a harmless CLI action, then reopen without further hooks. | Configurator is not required; unavailable receiver does not block CLI. Current Client snapshot repairs transient lost delivery without requiring another hook. |
| F06 | Leave CLI quiet beyond eleven minutes with Client alive; then abruptly terminate only that CLI session without an end hook. Separately Exit/restart Client while a session spans the restart. | Quiet Client stays online. Abrupt CLI termination can leave stale activity: presence is not proof CLI is alive. New Client run starts Idle until fresh hooks; stopped-period events are unrecoverable. Client crash/network failure instead ages out two intervals plus one minute after last contact. |
| F07 | If CLI exposes a documented hooks-disabled mode, start a fresh session with it while Client runs. | No hooks from that session; Client still sends periodic presence/snapshots, keeping machine online without inventing activity. Record exact CLI option/version or mark blocked. |

## G. Optional Windows integration and MSI servicing

Run only in the disposable account/VM. These checks can be done on one machine
but mutate OS integration. Use default data paths and installed shortcuts for
startup/MSI cases. Preserve a VM checkpoint before servicing.

| ID | Steps | Expected result |
| --- | --- | --- |
| G01 | Enable **Start at Windows sign-in (this user)**, save, sign out/in. Verify tray and `/health`. Disable, save, sign out/in again. | Opt-in starts dashboard in background; opt-out removes its startup registration. Dashboard startup does not generate remote status or refresh machine liveness. |
| G02 | Snapshot existing firewall rules. Click **Add Private firewall rule...**, cancel elevation, then repeat and approve if authorized. Inspect Windows Defender Firewall advanced properties. | Cancellation gives feedback with no rule change. Approved rule allows the current receiver TCP port on Private only, not Public/Domain; no global firewall disable or URL ACL. |
| G03 | Use **Remove firewall rule...**. Compare rules to baseline. | Only Agent Signaler's owned rule is removed. Loopback still works; this is not a test of remote ingress enforcement. |
| G04 | Exit Dashboard, close Configurator and pause CLI; leave owned Client running. Repair both same-version MSIs using maintenance or `msiexec /fa "<absolute MSI path>" /l*v "<absolute log path>"`. Inspect tasks before Apply, explicitly Start Client and test. | Bounded `--stop-client-for-update` stops exact owned Client before replacement, no auto-restart. After InstallFiles, task-only migration removes any owned legacy task without config/hook/startup changes. Same UUID/data/three-binary paths, no duplicates; foreign hooks/tasks survive and clean repair does not opt into integration. |
| G05 | Exit Dashboard/Configurator and pause CLI; leave owned Client running. Upgrade Dashboard first, then Remote; separately run updater with Client in another session for same user. Record logs; inspect task/config/hooks/startup before Apply. Explicitly Start Client for managed config; migrate legacy config per A11. | Bounded `--stop-client-for-update` IPC stops owned Client across sessions, never image-name killing; old releases without Client skip only this stop helper. New Relay task-only migration still runs after InstallFiles, removing owned legacy task before Apply. No automatic Client restart or config/hook/startup changes. Stable paths/shortcut/UUID, no duplicates; lower version blocked. Missing builds mean Blocked. |
| G06 | Choose Uninstall integration; cancel, then confirm while owned Client runs. Inspect preview/startup/task. Reapply for G07. | Cancel unchanged. Bounded current-user IPC stops only exact owned Client; removes owned startup/hook/config/legacy task. Identity/diagnostics/backups and foreign entries remain. Reapply retains UUID, creates no task; first-launch versus explicit-start outcomes are visible. |
| G07 | Close CLI; uninstall Remote with integration and Client running, then Dashboard with startup enabled. Repeat with Dashboard unreachable. Remove any explicitly added firewall rule first. | Exact owned Client stops within bound; failed offline notification alone does not block uninstall. Unsafe stop/ownership failure gives actionable error, never an image-name kill. Owned startup/hooks/legacy task removed, no server; foreign processes/entries survive. No firewall elevation; retained settings/identity/backups/journals survive. |
| G08 | Reinstall both; launch and inspect identity/data; explicitly configure integration again. | Retained identity is reused, not silently replaced. Retained dashboard data may reappear. Reinstallation alone does not recreate remote hooks; even approved apply creates no scheduled task. |
| G09 | After approved remote setup inspect the owned `AgentSignaler-Client-*.lnk` in `shell:startup`: exact adjacent Client target and canonical `--background --config` arguments. Toggle Configurator's sign-in checkbox and apply while Client is running and stopped; repair, uninstall/rollback and sign out/in. In an approved disposable environment also migrate owned legacy Run entries, Windows-disabled entries and conflicting foreign entries. Exit Client, run hooks (also with legacy v1/v2 config), then use Start Client shortcut. Repeat duplicate launch, Explorer restart, sleep/resume, another Windows session sharing the user's data directory, and isolated junction/symlink config aliases. | Unchecked removes only owned startup and does not stop a running Client; repair/rollback preserve the choice. Legacy migration does not leave duplicate launches or reset Windows Startup Apps disabled state; foreign entries remain untouched. Sign-in (not pre-login) starts one reporting owner/icon/timer per user/data directory across sessions. Exit retains registration but hooks never restart/fallback, regardless of config version. Explicit start resumes a newer Idle run. Secondary launch identifies cross-session ownership without stopping owner or creating a timer; aliases are rejected. Resume sends current snapshot without catch-up burst. |
| G10 | At a checkpoint substitute a foreign/modified startup value; attempt Apply/removal. With approved failure fixtures test transaction/MSI rollback after owned startup/task cleanup. | Foreign entry is preserved with ownership feedback. Rollback restores exact journaled owned values/task/files, not unrelated entries; identity persists. No GUI, sign-in, required cloud connectivity or new scheduled task in MSI custom actions. Mark unavailable failure fixtures Blocked. |
| G11 | From a checkpoint with an ownership-verified legacy task, exercise successful Remote MSI install/repair/update and an approved failure after task migration. Inspect logs for Relay `--migrate-legacy-heartbeat`, `--rollback-legacy-heartbeat`, and `--commit-legacy-heartbeat` after InstallFiles, each with required `--transaction-id` (optional `--config`). Repeat upgrading legacy pair without Client/Dashboard connectivity, missing task/manifest, and foreign replacement before rollback. | Success leaves no owned heartbeat task before Apply. Task-only journal and timestamped XML backup enable exact rollback, but never overwrite a foreign replacement; commit finalizes migration. Missing task/manifest is no-op. No Client/GUI/cloud dependency or settings/hooks/startup changes. Helpers are MSI-orchestrated, not manual setup commands; missing controlled failure fixture means Blocked, not Pass. |

Integration ownership protection can also be checked at a checkpoint: save the
exact owned hook bytes, add harmless whitespace to that file in a text editor,
then try repair/removal. Expect refusal to overwrite/delete the modified file.
Restore the exact bytes before continuing normal cleanup; JSON-equivalent text
with a different hash is still considered modified.

Forced MSI rollback/interrupted servicing needs a controlled failure fixture and
recovery procedure; the repository does not provide a turnkey manual failure
switch. Mark that release-acceptance item **Not run/Blocked** unless an approved
fixture is available. Configurator rollback in E09 is not proof of MSI rollback.
Do not terminate arbitrary installer processes or corrupt installed binaries to
simulate rollback.

## Multi-Copilot session acceptance

These cases require a separately authorized disposable environment. They are
**Not run** during offline-only automated validation; do not enable
`AGENT_SIGNALER_LIVE_TUNNEL_TEST` to execute them.

| ID | Steps | Expected result |
| --- | --- | --- |
| MC01 | Observe two CLI conversations in one scope plus verified VS and VS Code conversations. Reuse host session IDs across different scopes/products; change only a product version. | One row per machine/source/scope/session, no collision, and no new identity solely for a version change. Missing stable IDs produce only sanitized diagnostics. |
| MC02 | Leave A asking for input. Repeatedly resume B, succeed B and fail B. Then make B wait, resume only A, and finally resume B. Include unrelated tools in A while its question is pending. | Waiting remains until the last pending input resumes or ends. Other activity does not answer A's question. Failure/result information remains visible without hiding the wait. |
| MC03 | Leave a waiter silent for at least 15 minutes with Client online; interrupt/recover the network within the same run. End B only, then Exit Client; restart explicitly. | Silence and retry retain A and its event timestamp. End affects B only. Exit/deadline shows last-known rows Offline and no connected squares. A new run does not resurrect prior waits. |
| MC04 | Open details with transcripts disabled; switch Settings/Copilots while editing settings. Interleave events from different rows, duplicate one and resend an older event. | Settings opens first and drafts/actions remain. Copilots works status-only; each row keeps only its own latest accepted event/time. Duplicate/stale/heartbeat traffic cannot replace it. Unknown legacy metadata is explicitly unavailable. Waiting count stays accurate. |
| MC05 | Retain synthetic history for several source/session identities and multiple streams of one session. Open View transcript on each, choose a stream, Back, and reopen. Include an ended/retained-only session. | No body reads for list rows. Read-only drill-in stays inside Copilots and never selects a different session. Streams remain separate, ended history remains accessible, Back restores selection/scroll. |
| MC06 | While a transcript read is pending, Back, hide tab, clear, disable, remove machine, close dialog, or let history expire. | Pending reads cancel; rendered text and cached entries release immediately; stale completions never restore text. Disabled/unavailable/expired/empty states are explicit. |
| MC07 | Observe 0, 1, 10, 11 and 64 connected sessions. Resize/work at 100%, 150% and 200% DPI on small and multiple monitors. | Exact indicator count; 4 x 4 logical pixels, 1-pixel spacing, deterministic row-major order. At 64, seven rows/34-pixel indicator height with no clipping. Tile stays 64 pixels wide, grows in height, and compact window scrolls within work-area bounds. |
| MC08 | Navigate compact and full cards/list with keyboard and screen reader; exercise hover and existing connection action. | Aggregate icon/action preserved; text summary exposes session labels/statuses and wait count. Squares are not tiny interactive hit targets. Hover preview remains correctly placed. |
| MC09 | On a stopped backup checkpoint, upgrade receiver first then matching producer binaries; exercise legacy/enriched traffic, 65th session and worst-case identifiers. Restore backups with matching old binaries as documented. | Strict versions reject unsupported fields without source merging. Capacity rejection is explicit and never evicts a quiet live waiter. Settings/mappings survive upgrade. Supported rollback restores the backed-up session set instead of treating enriched state as corrupt. |

## Cleanup

1. Close disposable CLI sessions, Exit each exact test Client (including C's isolated
   fixture), and wait for in-flight Relay to exit. Confirm no test reporting resumes.
2. Remove integration through the configurator (or exercise Remote MSI uninstall).
   Confirm the exact owned Client startup value, owned hook and any ownership-verified legacy
   `AgentSignaler-Heartbeat-<recorded UUID>` task are gone; compare unrelated hook
   hashes. Never delete unrelated or ownership-mismatched startup/tasks/hooks.
3. Remove the firewall rule through Dashboard Settings if created. Disable startup
   unless it is being removed by the MSI case. Use explicit dashboard **Exit** and
   confirm `/health` no longer responds.
4. Save sanitized screenshots, request outcomes, hashes, timing notes, installer
   logs and defects outside the disposable profile. Do not copy an open SQLite
   database; stop dashboard first or use a SQLite-aware backup.
5. Remove synthetic cards through the UI if retaining the installation. Delete
   only the recorded `$TestRoot` and the unrelated fixture file you created after
   preserving evidence. Do not broadly purge `%LOCALAPPDATA%` or `.copilot`.
6. Uninstall test packages if desired; retained per-user state is intentional.
   Revert the VM checkpoint or remove the disposable account when finished.
   Confirm no test-created startup/firewall/task entries remain.

## Results and exit criteria

Copy this table into the release/test record and add one row per case, including
each B02/D02 variation. Nothing starts as Pass.

| Case | Build/environment | Actual result and timing | Outcome | Evidence/defect |
| --- | --- | --- | --- | --- |
| A01 | | | Not run | |

- Core loopback qualification: A01-A02, B-D, E01-E12, and cleanup pass; explicitly
  record optional capacity runs and any blocked prerequisites.
- Dev Box qualification additionally requires applicable E13-E46 cases. Separate
  fake-backed evidence from approved live Azure/Windows App results; neither
  documentation nor core loopback success substitutes for those checks.
- Remote integration qualification additionally requires A03-A13, C01-C11, and F01-F07
  with a real supported CLI; synthetic tests cannot substitute for those.
- Installation qualification requires applicable G cases for the tested versions.
- Any crash, incorrect accepted state, lost identity/data, private-payload leak,
  unrelated-file mutation, silent failed setup, or persistent hook blocking is a
  failure requiring investigation.
- Release sign-off still requires outstanding `ACCEPTANCE.md` checks, especially
  two-machine LAN/VPN behavior, remote firewall enforcement, multi-monitor behavior
  when unavailable locally, packet-level privacy verification, and MSI rollback.
