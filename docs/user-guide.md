# Agent Signaler User Guide

A Windows 11 x64 dashboard for Copilot CLI and explicitly verified local IDE agent status through anonymous Dev Tunnels
HTTPS sharing by default, or on an explicitly selected trusted LAN/VPN.
LAN mode requires no cloud account or service.

## Read-only conversation prototype

**External informed consent is a deployment prerequisite.** Before distributing
or using this prototype, the operator must obtain permission for the content,
configured destination, and retention policy. Agent Signaler neither collects nor
verifies that permission. Installation, Apply confirmation, and the informational
notice are not evidence of consent; no in-app consent, pairing, enrollment, bearer
token, or credential-store workflow is added.

New configuration **v5**, and deliberate migrations to v5, default **Share detailed
conversations** on. Use this Configurator control to opt out, then apply the
previewed settings. Valid v5 defaults to true when the field is omitted; a saved
false must survive repair and recovery. Merely opening a newer binary does not
rewrite versions 1–4: they remain status-only. HTTP/LAN is also status-only,
regardless of the flag. Default-on is not auto-start: existing integration
eligibility, explicit configuration/startup, a running Client, and a compatible
receiver are still required; a hook never starts Client.

Details use only the canonical configured **HTTPS Dev Tunnel**, normal certificate
validation, and the Dashboard's loopback Internet listener while its owned tunnel
is running. There is no direct HTTPS-hosting or certificate-setup workflow.
Stopping/changing the tunnel disables transcript ingest and clears volatile state.
Anonymous senders are not authenticated: anyone reaching the endpoint can spoof
machine/source IDs, inject activity, request purges, and consume capacity.
Loopback is not proof that a request came through the tunnel. Trust the configured
destination, Windows certificate trust, and tunnel service/operator accordingly.

### What is captured, and what remains unavailable

- User messages and tool names/observed status come only from supported hooks.
  Permitted message text, including any PII it contains, is not classified or
  redacted. Do not treat this as a mechanism for safely sharing secrets.
- Main-assistant replies require an independently verified, host/version-specific
  file format, path/session binding, and completed-message definition. Only Client
  may read the exact local stop reference, and only for completed user-facing
  assistant text. **No production file profiles are currently verified for Copilot
  CLI, VS Code, or Visual Studio.** Readers therefore remain unavailable; supported
  prompt/activity capture is partial. A documented path field, bundled CLI, or
  synthetic JSON fixture is not evidence of host transcript compatibility.
  The [P0 evidence record](transcript-capability-evidence.md) documents the
  reviewed public fields and missing proof; the production adapter registry is
  intentionally empty. The invented test-only profile is not shipped support.
- Tool arguments/results, raw errors, hidden reasoning, system/developer messages,
  attachments, subagent replies, arbitrary metadata, and file-sourced user messages
  are excluded. Missing assistant text is not an empty successful reply.
- Local stop references never enter HTTP, persistent settings/state, logs,
  diagnostics, or viewer models. A capture-source label identifies a verified
  profile, not a path. Reading an existing host transcript grants no ownership:
  installation, repair, rollback, opt-out, clear, and uninstall must not alter it.
  Relay does not read files or send HTTP.

### Stop, clear, and retention

Client opt-out stops admission and cancels reads/sends before waiting on network
work, clears queued text/references/cursors, and attempts one bounded best-effort
remote purge. An unreachable Dashboard may retain earlier text until its TTL or a
local clear/disable; the UI cannot prove an offline Client acknowledged a change.
The single close/purge request identifies one stream, not every source on the
machine. Other streams' previously received text may remain until TTL or local
clear/disable even when the Dashboard is reachable.
If opt-out cannot be saved, this Client run stays suspended with a save failure,
not a success indication or automatic resumption. Re-enabling begins fresh without
backfill. Tray **Exit** stops all managed reporting.
Transcript-only reload does not wait for presence/network reload. A one-second
configuration revision check suspends detail for deleted, invalid, or stale
configuration; it is not a filesystem watcher. New admission, file access/output,
and sends must recheck the enabled revision.

Dashboard's **Receive detailed conversations** preference defaults on independently
of the Client flag. Find it under **Settings > Internet sharing**. It applies and
saves immediately, independently of the dialog's Save/Cancel; disabling purges
before saving, and a save failure keeps reception disabled for this run with an
error. Disable it to reject further details, purge retained text, and invalidate
views without stopping status. In computer **Settings**, **Clear
transcript** clears that computer's retained content without disabling future
reception. Removal, receiver restart, or listener teardown also invalidate affected
transcript state; restarting does not recover conversation history.

Agent Signaler retains conversation content only in bounded memory: no SQLite
conversation history, spool, export, saved cursor, content-derived recovery hash,
or replay archive. Diagnostics are category-only. This does **not** mean
conversation data never exists on disk: the agent host's own transcript input,
OS paging/hibernation, and external crash capture are outside this storage policy.
Do not paste transcript content into persistent notes or diagnostic evidence.

### Computer details: Settings and Transcript

Computer details has fixed **Settings** and **Transcript** tabs. Settings opens
first and preserves display name/note, status/sessions, and Dev Box mapping,
sign-in, refresh, launch, copy-URI, and cancellation workflows. Tab switches retain
unsaved edits without saving or starting connection work. Save details/Remove
belong only to Settings; Close and ongoing connection cancellation remain reachable
from either tab.

Transcript selects one reported machine/source/scope/session and stream, including
ended sessions whose content has not expired. Identity labels are not authenticated
identities. Text is inert, wrapped plain text with role/time/source and delivery
order; missing host IDs mean arrival order, not invented causal correlation.
Capabilities and availability distinguish disabled, HTTPS/receiver required,
partial/unverified format, baseline, waiting for stop, file unavailable, budget
exceeded, empty/loading/read failure, offline, gaps, truncation, expiry, and reset.
Assistant provenance, when supported, is **Transcript file, read after stop**,
never a path.

There is no composer, Send, approval, agent-action retry, tool execution,
transcript clipboard/export, active links, or remote images. Opening the tab does
not enable reporting, start Client, or read a host file. It reads only the
in-process volatile API; no browser/network-facing transcript reader exists.
Returning to Settings or closing releases transcript text; expiry, eviction,
clear/disable, removal, and reset invalidate visible content as well as caches.
Refreshes are cancelled on selection/tab changes and stale completions rejected.

### Fixed prototype bounds

These are feature budgets, not guarantees about total CLR/WinUI process RSS.
Limits are internal; only the enabled flags are user settings.

| Boundary | Limit / behavior |
| --- | --- |
| Hook / IPC | 64 KiB raw input; 2.2-second Relay work / three-second host timeout; status/control 4 KiB; detail event 32 KiB serialized, Unicode-safe truncation marked explicitly |
| Client | 4 MiB including scratch/metadata/in-flight ownership; 256 events / 16 streams; retries at most eight attempts / two minutes; no disk spool |
| Local reader | One active pass, 16 queued sessions, two-second pending expiry, 32 contexts / eight per source, 30-minute inactivity expiry |
| Each accepted stop | At most 750 ms, 2 MiB inspected, 128 records, 16 replies / 256 KiB output; at most three attempts at 0/100/300 ms, sharing the same budget |
| Reader input | Exact profile-bound current-user regular file; 512-character reference, 64 MiB file ceiling, 16 KiB chunks, 64 KiB records, JSON depth 16; no relative/UNC/device/ADS/reparse paths |
| No backfill | First unbound read establishes EOF baseline, potentially omitting the first reply; replacement/truncation/reset rebaseline; no scans, watchers, continuous polling, or whole-file replay |
| Receiver | 64 MiB including 8 MiB reader/viewer reserve; 25 machines, 64 streams, 128 sessions, 2,048 events; per machine 8 MiB / 256 events; per session 2 MiB / 128 events |
| Ingest | Four concurrent requests globally / one per reported machine, no wait queue; 20 requests/second burst 40 globally, 5/second burst 10 per machine |
| Retention | 30 minutes from receiver receipt; reads/duplicates do not extend it; overflow, loss, restart, or exhaustion is partial capture, not guaranteed delivery |
| Viewer | Session pages of 16, at most 32 entries per machine; event page 32 events / 128 KiB; visible window 64 events / 256 KiB plus one pending page |
| Refresh / cursor | One viewer/read; one-second visible-tab polling; five-minute, at-most-512-byte selection/epoch-bound cursor; appends preserve older pages, destructive changes return resets |

Older-page navigation stays bounded and preserves scroll intent; new activity
does not force a jump while reading history. Manual keyboard, focus, screen-reader,
high-contrast, DPI, and installed-UI acceptance remain **not performed** for this
prototype. Synthetic tests do not verify actual hosts or live tunnel deployment.

## HTTPS client support and Dev Tunnels status

Remote Configurator and Relay now accept a root HTTPS dashboard URL, including default
port 443 and explicit ports 1-65535. HTTP LAN URLs retain the 1024-65535 restriction.
Dashboard defaults to its dashboard-managed **devtunnel CLI fallback**. Internet
mode uses a loopback-only receiver; never expose the all-interface LAN HTTP listener
directly to the Internet. Live release acceptance remains incomplete (see below).

### Enable Internet sharing

1. On the dashboard computer only, explicitly install `Microsoft.devtunnel` with
   `winget install Microsoft.devtunnel`. The qualified CLI version is
   **1.0.2030+fc9273aa0f**; unsupported versions fail visibly. The executable must
   have a valid Microsoft signature. No CLI is downloaded by Dashboard or its MSI.
2. Sign in explicitly with `devtunnel user login`. The CLI manages credentials;
   Dashboard never extracts tokens. Its per-user credential cache may also be used
   by your other devtunnel sessions.
3. Open Dashboard. **Internet HTTPS (Dev Tunnels CLI)** is the default for new
   settings and settings without a connection mode. Explicitly saved LAN mode is
   preserved. The Internet receiver binds only to loopback.
4. Sharing starts automatically using your existing CLI sign-in, with no additional
   consent checkbox or enable switch. A dedicated persistent tunnel is created or reused, with anonymous **connect**
   permission on exactly one HTTP port. No anonymous management access is granted.
5. After public JSON health verification succeeds, **Copy URL** copies exactly the
   displayed public HTTPS base URL. Paste it unchanged into Remote Configurator.
   Copying is disabled when sharing is unavailable; it never falls back to a LAN URL.

**Stop sharing / Cancel** stops the owned host, retains its resource identity, and
persists `AutoStartSharing: false` so it stays stopped across restarts.
**Enable sharing / Retry** starts it again and restores automatic startup.
Confirmed deletion removes the cloud resource; failed deletion retains its identity
for cleanup. The service's host count can briefly lag after Stop; retry deletion
after it clears. Dashboard never evicts a reported connected host.
Sign-out stops hosting before clearing the CLI cache and warns about
other CLI sessions. Delete and Sign out also disable automatic startup.
Closing to tray continues hosting; Exit stops it without disabling sharing on the
next launch. Missing/expired CLI sign-in or failed startup shows an actionable error,
not an automatic sign-in prompt or an HTTP fallback. Invalid settings disable
automatic sharing until repaired. Port, mode, or CLI path changes require restart;
the displayed/copied URL always reflects the effective running configuration.
Public health is rechecked every 20 seconds with a 15-second deadline; a failed
check disables copying until verification succeeds again. Detection is not instant.
If changing the local port, explicitly delete the old tunnel before enabling
sharing on the new port; port or ACL drift is not silently rewritten.

The dashboard shows a progress bar during startup. Computer tiles (including the
minimized compact view) stay hidden until startup finishes, ending with public
HTTPS verification when automatic sharing is enabled. LAN mode and disabled
sharing do not wait for a public endpoint. Settings is disabled during startup;
use the notification-area **Exit** command to cancel and close.
If startup fails, the progress bar stops, an actionable error remains visible,
and available machine data is shown so you can recover in Settings.
The URL sits beneath the machine-count subtitle, with an icon-only **Copy URL**
button immediately to its right (hover for its tooltip). Long URLs are shortened
visually in narrow windows; copying still uses the complete URL. **Compact View**
and **Settings** include glyphs and move below the header when space is limited.
The dashboard retains the URL and **Copy URL**, without infrastructure status
labels; sharing details remain in **Settings > Internet sharing**. Machine
activity and connectivity indicators are unchanged. Prerequisite checks and Dev
Box discovery are explicitly run from Settings, not automatically at startup.

Dashboard settings are grouped into five tabs:
**General** (appearance, compact views, and Windows startup),
**Network** (receiver mode, port, and firewall),
**Internet sharing** (start/stop/retry, delete tunnel, and explicit sign-out),
**Dev Box** (discovery targets, account/catalog results, and refresh/cancel), and
**Prerequisite** (component paths, readiness checks, and setup guidance).
**Save** applies ordinary settings across all tabs; switching tabs preserves edits.
Invalid CLI paths select **Prerequisite** and focus the affected field, with feedback
below the tabs. Firewall and
sharing actions still take effect immediately rather than waiting for Save.
Use Tab/Shift+Tab to move between controls and the tab strip, arrow keys to move
between headers and Enter/Space to select, and Ctrl+Tab/Ctrl+Shift+Tab to cycle tabs. Headers scroll when the window is
too narrow to display all five; each tab's content scrolls independently.

### Prerequisite checks

Open **Settings > Prerequisite** even before enabling Internet mode, or while
sharing is active. Each component has its own **Check** and **Cancel** controls:

- **Dev Tunnels CLI:** tests the entered `devtunnel.exe` path (blank uses the
  WinGet/prerequisite installation), Microsoft signature, supported version, and
  account status. Install separately with `winget install Microsoft.devtunnel`
  and explicitly sign in with `devtunnel user login`, using the selected executable
  when a custom path is configured. This is a separate CLI, not an Azure extension.
- **Azure CLI:** tests installation, version, and saved-account status using the
  entered `az.exe` or official MSI `az.cmd` path. Install Azure CLI 2.90.0 or later
  separately and use `az login` yourself if needed.
- **Azure CLI devcenter extension:** uses that same entered Azure CLI path,
  checks its version, and runs only `az extension list`. Required **only** for
  discovery by Dev Center name, not automatic or subscription-ID discovery.
  Install it separately with `az extension add --name devcenter` if missing.
  Availability does not prove service compatibility or Dev Box permissions.
- **Windows App:** checks the current user's `ms-cloudpc` association without
  activation. Protocol availability is **not verified app/version readiness**.
  Install/update Windows App separately from Microsoft Store and verify version
  2.0.804.0 or later in the app.

**Check all** at the top starts all four diagnostics using a snapshot of the
currently entered paths. The top summary has one line per check: a progress
indicator while queued/running, a green checkmark for Passed, or a red cross for
Failed. Cancelled and not-yet-checked results are labelled separately. Detailed
results and their limitations remain below; Passed means only that the specific
check succeeded, not that every prerequisite capability has been verified.
Azure CLI status and devcenter checks run one after the other to preserve their
existing mutual exclusion; the other checks can run concurrently. **Cancel all
checks** cancels queued and running diagnostics without cancelling sharing or
catalog operations. Individual checks and cancellation remain available.

Checks do not install components/extensions, initiate sign-in, launch connections,
save paths, or change sharing. Dev Tunnels diagnostics use independent, bounded
CLI processes, never the live sharing controller. Failure, cancellation, and closing
Settings leave an active host, public URL, and saved tunnel identity alone.
Runtime sharing/connection safety validation and installer prerequisite detection
remain in place.

Results identify the tested path/version where available and stay visible across
tab switches. Editing a path does not retest it: the previous result still identifies
the old tested path. Effective running paths are displayed separately.
Path changes keep the existing settings fields and require **Save**, Exit, and
reopen to affect sharing or Dev Box operations. Save is disabled during checks;
cancel or wait before saving. Azure checks and Dev Box discovery are mutually
exclusive. Closing Settings cancels **all** its diagnostics and awaits cleanup
before dismissing, without stopping unrelated sharing.

Each computer tile shows a top-left connectivity icon: a green check for online
or a gray cross for offline. Hover for its label; the centered icon and text still
show the agent's activity status.
Full-size tiles also show `Dev Box: <name>` or `Dev Box: Not mapped` with a
mapping status independent of color; selecting one opens machine details.
Tile backgrounds also follow that activity status: blue for executing, amber for
waiting, green for succeeded, red for failed, and neutral gray for idle/offline.

**Compact view when minimized** is enabled by default in Settings, independently
of the main dashboard's **Compact view** density setting. Minimizing shows only a
vertical list of 64 x 64 tiles (Windows logical pixels), always on top at the
top-right of the dashboard's monitor work area, without a title bar or toolbar.
Tiles retain their status colors, connectivity icons, and computer names; hover
for the full name/status and saved note. To add or edit a note, open the computer
tile in the main dashboard, enter **Note/description**, and select **Save details**.
Notes are saved locally across restarts; clear the field and save to remove a note.
Scroll with the mouse wheel or touch when the list
exceeds the screen height. Select a tile to **Open in Windows App** using its
explicit Dev Box mapping. Missing mappings and connection failures restore the
dashboard and open that computer's details. Dashboard first checks top-level window
titles on the current desktop for the mapped Dev Box name, using ordinal
case-insensitive, boundary-safe matching. The first matching window in z-order is
restored if minimized and brought to the foreground without opening another
connection or restoring Dashboard. This is title-based matching, not verification
of the remote connection: identical Dev Box names cannot distinguish projects or
centers, and unsupported title formats are not reused.
Right-click a compact tile for **Connect** (the same action as clicking the tile),
**Show full dashboard**, and **Exit**. Exit shuts down Dashboard and its receiver,
not just the compact window. While a connection is busy, Connect is disabled;
Show full dashboard and Exit remain available.
Use Show full dashboard, the tray icon, or launch Agent Signaler again to restore the dashboard.
The details dialog uses the same reuse-first launch path. Errors appear in
the dialog without discarding mapping, display-name, or note edits.
With no computers, no compact window is shown; the first received
computer appears automatically while minimized. Closing to tray and background
startup do not show the compact window. Disable the setting to retain minimize-to-tray
behavior. The receiver and status refresh continue in either mode.

### Windows App and Dev Box connections

Requires Windows 11 x64, Windows App **2.0.804.0** or later with `ms-cloudpc`
registered, and Azure CLI **2.90.0** or later for fresh connections. Reusing an
already open connection needs neither the protocol association nor Azure CLI/network access.
Dashboard checks the current user's Windows protocol association, including packaged
Windows App and delegated activation; a classic `shell\open\command` registry entry
is not required. Successful shell activation may reuse an existing app without
creating a new process.
Configure Azure CLI globally under **Dashboard settings > Prerequisite**. Enter an
absolute path to a trusted `az.exe` or `az.cmd`, or leave it blank for the default
Azure CLI installation in Program Files. **Save**, Exit and reopen Dashboard to
use a changed path for all Dev Box connections.

Dashboard runs the CLI and validates its reported version and signed-in user,
without inspecting PE headers, executable architecture, or Authenticode signatures.
`az.cmd` must be the official MSI launcher in its `wbin` directory, with the adjacent
`python.exe`. Dashboard runs that runtime directly without a command shell. Keep the entire
installation, including CLI modules and dependencies, protected from untrusted writes.
An explicitly selected missing or unsupported installation never falls back to another path.

**Check Azure CLI status** tests the path currently entered, including unsaved
changes. It checks installation, version, and current sign-in status and shows
selectable, stage-specific diagnostics. The account check validates the CLI's saved
user, tenant, and enabled subscription, not live token validity or Dev Box permissions.
Testing does not sign in, save the path,
change machine mappings, or open a Dev Box. Cancel the test, close Settings, or
Exit to cancel and await cleanup of its owned work. Raw CLI output, tokens, and connection URIs are
not displayed. Only select installations you trust: testing executes the selected
CLI as your Windows account.

Prerequisite installation remains an explicit administrator/user task, not a
connection action. The setup bundle's separate prerequisite detection policy is
documented in `installers\README.md`.

In **Settings > Dev Box**, select **Refresh Dev Boxes**. Dashboard uses Azure CLI
to discover Dev Centers, then lists the Dev Boxes assigned to the signed-in user.
Leave both optional search fields blank to search enabled public-Azure subscriptions available to the current
CLI user in the current tenant. It never changes the selected account, subscription,
tenant, or cloud, and does not install an Azure CLI extension.

To narrow the search, supply **either**:

- **Subscription ID:** a nonempty GUID. Dashboard validates the current CLI account,
  skips `az account list`, and searches Dev Centers only in that subscription.
- **Dev Center name:** for example, `devcenter-tfotz75rskxty-dc`. Dashboard validates
  the current CLI account and runs
  `az devcenter dev dev-box list --dev-center-name <name> --user-id me --output json`.
  This bypasses Dashboard's subscription and ARM enumeration. The `devcenter`
  extension must already be installed (`az extension add --name devcenter`).
  The CLI aggregates its pages into an array; Dashboard validates each item's URI
  before using it for a mapping. An empty list does not invent an endpoint.

**Refresh Dev Boxes** and Settings **Save** persist the validated search fields in
`%LOCALAPPDATA%\AgentSignaler\dashboard-settings.json` and restore them when Settings
opens, including after restarting Dashboard. Machine-details discovery also uses
this saved target. Clear both fields and refresh or save to return to automatic
discovery. Refresh saves the target before discovery, even if discovery later fails
or is cancelled; unrelated unsaved settings are not saved by Refresh. Results remain
session-only. There is no endpoint editor or Add/Remove workflow.

Dev Center discovery uses Azure Resource Manager and requires permission to read
Dev Centers in the searched subscriptions. **Dev Box User access alone may not
allow enumeration.** If discovery is denied, check the CLI sign-in/tenant and ask
an Azure administrator for appropriate Dev Center read access. Existing saved
machine mappings remain launchable under their normal rules even when discovery
is unavailable. Discovery cannot enumerate centers hidden from the CLI account.

Catalog refresh and connection operations use the running CLI path until restart,
even if a different path is entered or saved; the Azure CLI status and extension
checks in **Prerequisite** test the entered path. Legacy `DevCenterEndpoints` settings are ignored, never used as
a fallback or seed, and omitted the next time Settings is saved. Loading an older
settings file does not rewrite it or discard unrelated preferences.

The Settings lists are informational: discovered Dev Center hosts, Dev Box names,
projects, pools, power states, counts, and the last successful refresh time. Validated UPN and
tenant are displayed in this local **Dev Box** tab, not in machine details,
tiles, or global errors. Full CLI arguments/responses are intentionally sent to an
attached debugger in both Debug and Release builds; redact them before sharing.
The catalog is in memory only and is never refreshed
periodically or as part of launch. Only an explicit **Refresh Dev Boxes** or
**Refresh Dev Box list** starts discovery. The Settings button honors the optional
target; the machine-details button uses automatic discovery.
Automatic and subscription-targeted discovery are GET-only using ARM and data-plane API version `2025-02-01`. Limits are
1,000 CLI subscription records and up to 1,000 eligible subscriptions, 20 ARM pages and 250
Dev Center resources per subscription, 20 distinct Dev Centers, and 20 pages and
250 Dev Boxes per center. Requests are bounded to
30 seconds per service request, 15 seconds for the account check, and 1 MiB per CLI
output stream. ARM pagination stays on the validated management origin and subscription
scope; Dev Box pagination stays on its validated Dev Center origin. Named-center
search uses the extension's list API and is bounded to 30 seconds, 250 returned
Dev Boxes, and 1 MiB per output stream for the combined CLI response.
If an individual subscription search fails, Dashboard records a warning, discards
that subscription's incomplete results, and continues with the next subscription.
Successful scopes can produce a **Partial results** catalog; failed subscriptions
and their classified errors are shown in Settings. If every subscription fails,
the refresh fails. Cancellation, account/list validation failures, global limits,
and Dev Box data-plane failures still stop the refresh.
Failed, cancelled, or empty refreshes retain the previous catalog and all mappings;
errors distinguish subscription discovery, Dev Center access, sign-in, malformed responses, and
timeouts without raw output.

In machine details, use **Refresh Dev Box list** explicitly, then choose a
**Mapped Dev Box** and select **Save mapping**. This searches Dev Centers directly,
even on a clean profile; it does not send you to Settings to enter endpoints.
No manual mapping fields are needed;
the summary refers to identity in Settings without exposing UPN or tenant.
Picker identity is endpoint, project, and Dev Box name, never hostname, display
name, note, pool, or list order. Selecting an item alone changes nothing persisted.
**Save mapping** validates the selected catalog identity against a fresh account
check, resolves a fresh connection, and atomically saves the complete mapping only
after success. Validation, resolution, cancellation, or persistence failure preserves
the old mapping. A saved mapping absent from the catalog is shown as
`Unavailable: <name>`, retained and still launchable/refreshable under the normal rules.
Compact tiles remain launch-only; missing mappings open the details picker rather
than choosing a catalog item.

**Sign in with Azure CLI** only signs in and validates identity, or refreshes an
already stored mapping. It never saves an unsaved picker selection; only
**Save mapping** replaces the mapping. Dashboard does not collect passwords or read
tokens. **Refresh connection** resolves and saves the stored mapping without
launching. No Dev Box lifecycle mutations occur.

**Open in Windows App** validates the stored mapping and tries existing-window reuse
before protocol checks, Azure CLI, or network access. Reuse leaves the saved URI and
retrieval time unchanged. With no match, it resolves and atomically saves a fresh
service-issued connection before shell activation. **Open last known connection**
validates the mapping/cache and also tries reuse first; otherwise it activates the
unchanged saved URI. It is an explicit details-only action, visible with its local
retrieval time when a cache exists, and performs no Azure CLI or network request.
Cached data never launches automatically or from compact mode, and is not deleted
based on age. There is no fallback client.

Title inspection is limited to 1,024 characters and accepts the exact name at title
edges, whitespace, colon, parentheses, brackets, en/em dashes, or standalone hyphen
separators. Hyphens next to resource-name text are not boundaries: `box1` does not
match `box10` or `box1-2`. No child text, session contents, process identities, or
connection URIs are inspected, and enumerated titles/handles are never logged,
displayed, or persisted. Disappearing/inaccessible candidates are skipped; a live
match that rejects restoration or foreground activation reports a sanitized error
instead of opening a duplicate. A process-wide lock serializes window checks and
activation, including a second check immediately before shell activation. It is
not held during resolution or persistence and does not wait for new windows to
appear or coordinate other applications.

Status is **Not configured**, **Ready**, **Sign-in required**, or **Unavailable**;
raw CLI output is never displayed. Machine details show the **Windows App launch URI
(last retrieved)** for the saved Dev Box mapping as selectable text with a **Copy URI**
button. Save a mapping or use **Refresh connection** to retrieve it without launching.
The displayed URI updates after successful connection operations and is removed when
the mapping is cleared. Copying uses the last retrieved URI without a network request;
check the last successful refresh time if it may be stale. The URI contains account
information, so share it only with people you trust.

All conflicting connection actions, selection, and the machine's compact tile are
disabled during that machine's mapping, sign-in, connection refresh, launch, or clearing. **Cancel connection
operation**, closing details, or Exit cancels owned work; closing the sign-in browser
does not cancel it. Cancellation and dialog closure await cleanup.
Only one catalog refresh runs at a time; Azure CLI path editing, Azure prerequisite checks, Settings
Save, and additional refresh requests are disabled while it runs. **Save mapping**
is disabled while that machine or the catalog is busy. An already mapped machine
can launch during catalog refresh using its stored mapping. **Cancel refresh**,
closing Settings, and Exit cancel and await catalog cleanup; Dashboard waits for
owned operations before disposing shared services and local storage.
Account checks time out after 15 seconds, service requests after 30 seconds, and
interactive login after 10 minutes.

Prerequisite UI smoke checks (Windows, including compact mode and high DPI):
confirm all five tabs are reachable with keyboard and overflow navigation; enter
unsaved CLI paths, switch tabs, and verify edits/results remain; Save an invalid
CLI path and verify **Prerequisite** is selected. During active sharing, run and
cancel each check (including a failing path). Use **Check all** with mixed passing
and failing prerequisites; verify all four summary rows, queued Azure work,
progress indicators, green/red results, and labelled cancellation. Switch tabs
during the run, then return and use **Cancel all checks**; retry and verify stale
success icons are cleared while rechecking. Close Settings while a check is
pending; the existing URL must remain usable. Repeat with missing/signed-out
components and confirm guidance, no login/install/connection prompts, and that
Azure checks block discovery until cleanup completes.

Mappings and cached connections remain in the per-user database across repair,
upgrade, and uninstall; reinstall reuses them. **Clear connection mapping**
requires destructive confirmation and retains machine history/display name.
Only that action or removing the machine deletes its mapping.

Remote configuration v1–v4 continues to load status-only without rewriting files.
Approved multi-target Apply explicitly migrates to v5 with `integrations`,
`dashboardBaseUrl`, `detailedReportingEnabled` and `heartbeatIntervalSeconds`
(default **300**). **Heartbeat interval (minutes)** accepts whole numbers **1–60**;
zero does not disable reporting. UUID and metadata are retained.
Upgrade **Dashboard first**, then **Client, Configurator, and Relay together**.
New multi-target integration requires the `/api/v3/health` capabilities check; an older
dashboard is rejected rather than silently falling back to v1 reporting.
Old Clients cannot read v5. Explicit downgrade requires stopping the exact owned
Client, clearing volatile detail, transactionally writing validated status-only
v4, and servicing compatible binaries; normal MSIs block older versions. A later
v5 migration must preview default-on again: v4 cannot preserve a v5 opt-out flag.
Failed migration rollback restores prior readable configuration/runtime without
starting a stopped reporter. Successfully saved false must not be replaced by
older true during recovery; report a conflict instead. See `installers\README.md`.

Newly applied `remote.json` files also include `relayPath`, the absolute path to
`AgentSignaler.Relay.exe`. Set **Remote relay location** on the **Hooks** tab to
select a local remote installation; the default is beside Configurator. The saved
path is restored on reopening. Preview and connection testing validate the entered
path; **Apply settings** saves it and updates enabled hooks and owned Client startup
after confirmation, rechecking that the relay is still present.
Older configurations without `relayPath` still load unchanged; the field is added
only through approved apply. `AgentSignaler.Client.exe` must be beside the selected
relay, and remote binaries must have compatible versions; older binaries reject
the new configuration fields. **Start client** uses the saved location, not unsaved edits.
Remote MSI repair/upgrade and the updater use bounded
`Relay --stop-client-for-update` IPC to stop the exact owned current-user Client
before file replacement, including across Windows sessions. Old releases without
Client skip the helper. Servicing never automatically resumes reporting; use the
**Start Client** shortcut afterward. Install alone does not enable sign-in startup.

Requests are anonymous, use `Accept: application/json`, and never use tunnel tokens,
user cookies or interactive sign-in. Redirects and invalid TLS certificates are
rejected. HTTPS uses the system proxy without default proxy credentials; HTTP
continues to bypass proxies. Interactive connectivity tests can be cancelled and
allow 10 seconds; Relay retains its 2.2-second total work budget and 3-second hook
timeout. System proxy discovery and real Internet latency still require release
measurement on supported networks.

HTTPS encrypts reports, but clients are anonymous. Anyone who can reach the URL can
submit or spoof status and consume capacity. In Internet mode, stopping
sharing disables access; HTTPS is not sender authentication.

### Backend choice and release gates

The preferred MSAL.NET sign-in + C# SDK backend remains unimplemented because
supported delegated scopes, consent, and account eligibility for an independent
Entra public client remain unestablished.
[The public SDK authentication question remains unanswered](https://github.com/microsoft/dev-tunnels/issues/557).
The owner selected the documented CLI fallback; no SDK/MSAL packages or CLI-token
extraction were introduced. Do not substitute guessed scopes, first-party client IDs,
or embedded client secrets.

On September 14, 2026, an explicitly approved disposable CLI tunnel passed anonymous
HTTPS GET 200 and synthetic POST 202 checks using normal TLS verification, without
cookies or authentication headers. The implemented controller subsequently passed
an opt-in test using real loopback Kestrel and the actual Relay executable within
the 3-second hook budget, including confirmed cloud deletion. This is not proof of
installed WinUI behavior, real Relay timing from another Internet connection, automatic refresh, revoked
credentials, sleep/reconnect, or MSI servicing. Those remain required release checks
in `docs\ACCEPTANCE.md`; this build is not a production-readiness claim.

Dev Tunnels is an external Microsoft-hosted preview service intended for development
and testing, with no SLA; adopting it for monitoring requires an explicit suitability
decision. Recheck current quotas, organizational outbound policy, inactivity expiry
and token lifetime before implementation/release:
[service overview](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/overview),
[security](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security),
[FAQ](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/faq).

## Build

Open `AgentSignaler.slnx` in Visual Studio 2026 with the .NET desktop/WinUI
development tools and the Windows SDK installed. Use the **x64** platform.
The applications target .NET 10 and use WinUI 3 / Windows App SDK.

From a developer PowerShell:

```powershell
dotnet build AgentSignaler.slnx -p:Platform=x64
dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Remote.Tests\AgentSignaler.Remote.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName!~WindowsTaskSchedulerTests"
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64
dotnet test tests\AgentSignaler.Tunneling.Tests\AgentSignaler.Tunneling.Tests.csproj -p:Platform=x64
```

The applications are unpackaged, self-contained x64 executables. See
`installers\README.md` for building the WiX MSIs and the preferred Dashboard entry
point, `AgentSignaler.Dashboard.Setup.exe`. Build and inspect local installer
artifacts without installation or redirected-drive copying with:

```powershell
.\scripts\Build-Installers.ps1 -Version 1.0.14
```

## Use

1. Install and launch the dashboard on the monitoring computer.
2. In network settings, select a port (default **51820**). Copy the displayed URL.
3. In LAN mode only, if required, explicitly add the inbound firewall rule from the dashboard.
   This requests elevation and opens only the **Private** network profile.
4. On each remote computer, install and open the remote configurator.
5. In **Connection**, paste the dashboard's **Copy URL** value into **Dashboard URL**, test connectivity,
   and set the heartbeat interval. In **Hooks**, tick **Enable** for the desired
   discovered or custom locations. Select **Apply settings** and confirm the exact
   hook, profile, Client/startup and connection changes; no manual hook setup is required.
6. Successful Configurator Apply with selected supported targets for first-time setup or legacy migration starts
   **AgentSignaler.Client.exe** in the tray.
   Its acknowledged startup report registers the computer immediately as Idle;
   no Copilot hook is required. Connection tests and preview remain non-mutating.
   An empty-target Apply saves configuration/startup but never starts a stopped
   Client; use **Start client** explicitly.
7. Run the selected CLI or IDE/profile. Relay sanitizes hooks and forwards them through bounded,
   current-user local IPC; the persistent Client is the sole managed HTTP reporter.
   Updated Relay never falls back to direct HTTP, even when loading legacy v1/v2
   configuration. Legacy direct HTTP compatibility is a receiver feature, not a
   bypass for a stopped coordinator.

Closing/minimizing the dashboard hides it in the notification area without stopping
the server. Use its explicit **Exit** action to stop it. Remote reporting does not
depend on the configurator remaining open. The remote Client's tray menu offers
**Open Configurator** and **Exit**. Approved setup registers its exact quoted
command under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`: startup is
at **this user's sign-in**, not pre-login, and requires no service or elevation.
One reporting owner is enforced per user/data directory across Windows sessions.
Launching Client again asks the existing owner to open Configurator, providing a
foreground control window in that owner's Windows sign-in session. If it cannot
respond, another launch may report that its active icon lives in another session.
Junction or symbolic-link config aliases are rejected. Persisted run-generation
state must not be deleted or reset to work around errors: corrupt/nonpositive state
fails visibly.

Client **Exit** rejects new work, stops timers/retries, and attempts a terminal
offline report within a bounded shutdown budget (at most five seconds for normal
Exit). Acknowledgement makes Dashboard Offline immediately, overriding activity.
If delivery cannot be confirmed, Client still exits and the dashboard ages out.
Hooks never restart Client, send HTTP as fallback, or replay missed stopped-period
events. Use the **Start Client** shortcut or Configurator's explicit start action
to resume; ordinary Configurator launch, testing, and settings-only Apply after
Exit do not resume reporting. Startup registration remains for next sign-in.

Each new Client run starts Idle until fresh hooks arrive: hooks missed while
stopped cannot be reconstructed, including for CLI sessions spanning a restart.
This differs from transient network loss while Client stays alive: local session
updates continue, and a later heartbeat snapshot reconciles missed delivery.
Saved settings and effective runtime settings are distinct; a failed reload must
show saved-but-not-applied status, not claim the running interval changed.
Configurator's **Start client** uses the saved configuration only, never unsaved
edits. A running Client confirms the effective revision after Apply; recover a
saved-but-not-applied result with explicit Start client or retry Apply as appropriate.
The displayed effective revision/interval changes only after Dashboard acknowledges
it. While acknowledgement is pending, Client uses the shorter of the old and new
intervals so an interval increase cannot silently cause a false timeout.

The configurator's **Test connection** displays a dismissible **Test connection**
message inside the receiving dashboard. It does not create a machine card or change
session status. If the dashboard is hidden, open it to see the message; no Windows
notification is sent. Ordinary health checks and relay reports do not show it.

Relay `test --config <path>` uses `DashboardConnection.TestAsync` for read-only
`GET /api/v3/health` with multi-target v4/v5 configuration,
`GET /api/v2/health` with managed v3 configuration, or `GET /health` with legacy
v1/v2 configuration. It does not post status, register a machine, change
session state, or refresh machine liveness. Like Configurator's test, it includes
the connection-test header and shows the dashboard's **Test connection** message.
The relay `heartbeat` command is removed.

After a successful connection test or applied install/repair/update, the
configurator remembers the dashboard URL in `configurator-settings.json` in its
per-user data directory and reloads it when reopened. Failed or cancelled
operations do not replace it. Testing does not change the active integration's
`remote.json`; without a remembered URL, the existing integration URL is loaded.

Standalone CLI discovery is independent of Visual Studio: its owned hook stays under
`%COPILOT_HOME%\hooks\`, or `%USERPROFILE%\.copilot\hooks\` when unset. An IDE-bundled
CLI executable is not evidence that the IDE loads that hook format or directory.
Existing unrelated hooks are not rewritten or removed. Restart existing CLI
sessions after installing or changing hooks; reload the selected IDE/profile or start
a new session as required by that host.

New integrations create no scheduled task. Remote MSI install/repair/update removes
only an ownership-verified legacy `AgentSignaler-Heartbeat-<UUID>` task through a
task-only transaction after file installation, even before Configurator Apply.
It requires no Client and changes no settings, hooks or startup registration.
Rollback restores the exact owned task. Published-folder setup retains Configurator
Apply's owned-task cleanup with backup and rollback protection. Modified/unowned
tasks are never silently removed; loading configuration or previewing alone remains
non-mutating.

Use standalone Copilot CLI **1.0.80 or later** on PATH. Client sends immediate startup and periodic sanitized snapshots,
even without Copilot activity; no scheduled task performs reporting.
The relay has a 2.2-second work budget and hooks have a
3-second host timeout: reporting is best-effort, not zero-latency. Malformed hook
payloads, inputs above 64 KiB, and delivery failures are diagnosed without returning
a hook failure. An IPC acknowledgement means accepted locally, not delivered.
Abrupt CLI termination without `sessionEnd` cannot reliably be distinguished from
a quiet open session: Client presence stays online, but activity can remain stale.
Managed failure detection is **two intervals plus one minute** after last accepted
contact: **11 minutes** at the five-minute default. Quiet clients stay online while
heartbeats arrive. Legacy v1-only machines retain the five-minute hook-only timeout.

### Configurator tabs

Configurator has three fixed, non-closable, non-reorderable tabs:

| Tab | Controls and workflow |
| --- | --- |
| **Connection** | Dashboard URL, heartbeat, Share detailed conversations with its external-consent/PII notice, machine UUID, saved/effective runtime status, Test connection / Cancel test, and Start client. |
| **Hooks** | Editable remote relay executable location, saved as `relayPath` in `remote.json` when applying settings. Discovered locations with Enable checkboxes and pending-change feedback. Refresh / Cancel discovery and Add custom location. Custom type and path editors appear only for explicitly added rows. |
| **Review & maintenance** | Optional settings preview, integration transaction recovery, cleanup of legacy diagnostic hooks, and full Uninstall integration. |

Each tab has a **?** help icon at its top right. Hover to read the view's
instructions. Setup guidance
and the privacy summary live in these tooltips rather than inline paragraphs;
field labels, current status, pending changes and errors remain visible.

The branded header, version, global status, and configuration/Relay
paths and **Apply settings** stay visible outside the tabs. Long status messages scroll within the global
bar; shortened footer paths remain available in full through tooltips and copying.
Each tab scrolls independently. Normal startup
always selects **Connection**; the last tab is not remembered, and opening, editing,
testing, or switching tabs never starts the reporter.

Tab switches and discovery refresh preserve unsaved selections and custom rows.
Editing does not write hook files. Tabs remain navigable during work; conflicting
actions are disabled. Apply builds a fresh complete preview and asks for confirmation,
without requiring a separate Preview or Verification step. Validation returns to
**Connection** for connection fields or **Hooks** for the offending location.

### Independent CLI / Visual Studio / VS Code integration

Upgrade Dashboard first, then co-install matching Client, Relay and Configurator.
New previews save configuration **v5** with the complete selected target collection
and require the source-aware Dashboard capability; there is no older-protocol fallback.
Older configurations remain readable and migration retains machine identity,
heartbeat, startup ownership and existing owned hooks.

1. Open **Hooks**. Discovery is read-only, bounded and cancellable. Each discovered
   CLI installation, Visual Studio instance or local VS Code profile has a row.
   Saved integrations are ticked; unavailable saved locations remain visible.
2. Tick **Enable** in the first column for each desired integration. For a new
   location, select **Add custom location**, choose its integration type and enter an absolute
   local path: the hook directory for CLI/Visual Studio, or the profile directory
   containing `settings.json` for VS Code, then tick **Enable**.
   The selected count and row status show pending changes; ticking does not save yet.
   Applied custom locations persist across refresh and restart. UNC, WSL, SSH,
   containers, browser and remote extension hosts are excluded.
3. Select **Apply settings** at the bottom right, review the complete changes and confirm. The
   configurator creates the hook files and registers VS Code profile hook locations
   automatically, together with connection and Client startup settings. No manual
   JSON editing, executable binding, diagnostic exercise or confirmation checklist
   is required. Merely selecting a row changes no files.
4. Untick a saved row and apply to remove its owned integration while retaining
   other selected integrations and shared reporter settings. Shared Visual Studio
   locations install one artifact and cannot isolate individual instances.
   Full **Uninstall integration** also stops the reporter; machine identity remains.

Known policy blocks, discovery failures and recorded verification failures still
block the complete selection rather than silently skipping it. Ownership hashes,
JSONC comments/unrelated entries and concurrent-edit checks are preserved. Overlapping
CLI/IDE loaders remain blocked; unrelated hooks are never disabled to force coexistence.
For interrupted transactions use **Recover interrupted integration transaction**.
For probes left by an older configurator, use **Clean up legacy diagnostic hooks**.
Changed user files are preserved, not overwritten.

**Test connection is not hook verification.** It proves only Dashboard connectivity
and protocol support. Automatic setup is saved as **Configured**, not **Verified**,
using the adapter's event set; existing verified event subsets remain supported.
Discovery/configuration never proves an IDE emits those events.
VS Code `Stop` returns to Waiting: it proves neither success nor
session end. Missing stable session IDs reduce capability. Permission waits, failures,
success and close cannot be promised where events do not supply them.
Workspace hooks may shadow user hooks; Settings Sync/profile overrides can change
effective behavior. Inspect the active Hooks UI rather than assuming universal coverage.
Session state is **last observed**; the single machine heartbeat does not prove any
IDE session remains open. All sources use Relay → local IPC → the one tray Client →
Dashboard. Opening/editing Configurator never auto-starts a stopped Client; tray Exit
stops every source until explicit Start or the next registered user sign-in.

VS Code's Windows command encoder supports the tested spaces, Unicode, `&`, `(`,
`)` and `^` path cases. It intentionally **rejects paths/arguments containing quotes,
`%` or `!`** rather than risk shell expansion; this is not universal shell/path
support. An incompatible Relay/config/scope path fails closed during preview.
Synthetic Windows command tests do not establish that an installed IDE/profile
uses that shell or loads the hook: real host testing is still a release validation
requirement, not a manual configurator setup step.

#### IDE compatibility evidence (September 15, 2026)

No real IDE workflow was verified during automated implementation. The inventory
below is observational, **not a supported-version certification**:

| Target | Observed version / candidate scope | Live status |
| --- | --- | --- |
| Standalone CLI | Requires 1.0.80+; independent effective `COPILOT_HOME` | Existing documented adapter; live release checks still required |
| Visual Studio Main | IDE `18.12.12211.431`; bundled CLI `1.0.83`; candidate `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli` | **Verification required**; loader, exact experience, home and events unverified |
| Visual Studio IntPreview | IDE `18.12.12211.348`; bundled CLI `1.0.83`; same candidate home | **Verification required independently**; possible shared scope, no instance attribution promised |
| VS Code local default profile | VS Code `1.137.0`; installed `GitHub.copilot` files `1.388.0` | **Verification required**; activation, Hooks UI, policy/trust, schema and shell unverified |
| Other/custom local profiles | User-selected exact root and installed build | **Verification required per profile**; no coverage inferred |

Unverified discovered candidates can now be configured automatically without claiming
live verification. Known policy blocks, discovery failures and recorded verification
failures remain unavailable with a reason. A verified partial result retains its
observed event subset.
See `docs\MANUAL-TEST-PLAN.md` and `docs\ACCEPTANCE.md` for outstanding release gates.

## Trust boundary

**The HTTP endpoint has no authentication or encryption. Anyone who can reach it
can submit or spoof status reports. Use it only on a trusted LAN or VPN.**

- LAN mode listens on all interfaces; Internet mode listens only on loopback.
- Do not port-forward it, publish it to the Internet, or open the Public firewall profile.
- Default anonymous Internet sharing deliberately does not authenticate senders.
  Its warning is informational, not a consent gate; the release checks above still apply.
- A Private-profile firewall rule is a convenience, not authentication.
- The server validates a fixed protocol and never executes incoming values.
- Status/presence remains content-free. V5 detailed reporting uses the fixed
  message/tool-metadata allowlist and externally consented HTTPS prototype policy
  above; allowed text can contain PII. Excluded tool bodies, reasoning, raw errors,
  attachments, and local transcript references never enter the network stream.
  Conversation text and local references are not persisted by Agent Signaler;
  diagnostic logs contain category codes, not payloads.
- The raw hook payload remains transient in the relay process.

## Projects

| Project | Responsibility |
| --- | --- |
| `AgentSignaler.Contracts` | Versioned wire model, validation, pure session reducer |
| `AgentSignaler.Service` | Testable Kestrel host and SQLite machine store |
| `AgentSignaler.Dashboard` | WinUI cards/details/settings, tray, startup, firewall |
| `AgentSignaler.Tunneling` | Dashboard-only CLI process ownership, tunnel lifecycle, ACL and public URL verification |
| `AgentSignaler.Remote` | Shared remote configuration, delivery, and integration logic |
| `AgentSignaler.Client` | Persistent tray coordinator, hook IPC, heartbeat and terminal offline reporting |
| `AgentSignaler.Relay` | Short-lived sanitized hook IPC forwarder and read-only health test |
| `AgentSignaler.Configurator` | Remote client discovery and explicit hook configuration |
| `tests` | Unit and loopback HTTP integration tests |
| `installers` | Separate dashboard and remote-component MSI packages |

## Protocol and state

`GET /health` retains its strict legacy v1 version/status response.
Configuration v4/v5 verifies `GET /api/v3/health`, returning exactly
`{"protocolVersion":3,"status":"ok"}`, and uses `POST /api/v3/reports`.
Sessions carry source kind, integration scope and opaque host session identity;
reporter identity/heartbeat remains machine-wide. Identical session IDs across
CLI, IDEs and profiles do not share state. Dashboard displays per-session sources.
Older managed configuration v3 verifies `GET /api/v2/health`, returning exactly
`{"protocolVersion":2,"status":"ok"}`. `POST /api/v2/reports` accepts managed `started`, `heartbeat`, `hook`,
and terminal `offline` envelopes; these are not extra Copilot hook events.
Started/heartbeat snapshots carry bounded sanitized sessions and the interval.
Generation/sequence ordering is persisted with acceptance; only an acknowledged
newer `started` activates a new run. Duplicate/stale sequences and old generations
cannot renew contact, and nothing in a terminal run can undo its offline report.
Ordering metadata is not authentication.

Detailed content uses separate transcript protocol v1 POST routes:
`/api/transcripts/v1/capabilities`, `/api/transcripts/v1/streams/open`,
`/api/transcripts/v1/events`, and `/api/transcripts/v1/streams/close`.
Control bodies are at most 4 KiB; events at most 32 KiB. These negotiate or ingest,
not read history or control an agent. Acknowledgement is volatile acceptance,
not persistence. Shared IPC v3 adds transcript operations while preserving exact
v2 status/control compatibility where the configuration is readable. Unsupported
receivers or old Clients do not receive conversation text.

`POST /api/v1/status` remains available to legacy machines. After a valid v2 start
activates managed mode, v1 reports for that UUID return **409**, even duplicates and after
Dashboard restart. To deliberately downgrade in a disposable/recovery environment,
stop Client and remove its owned startup/hooks through normal Configurator actions,
restore matching old
binaries/configuration/integration backups, then explicitly remove the machine
locally in Dashboard to reset managed/replay history. Removal loses its saved
display name, notes and mapping; preserve needed information manually first.
Incoming traffic never resets managed mode; there is no automatic downgrade endpoint.
Configurator and Relay tests include `X-AgentSignaler-Connection-Test: 1` to request the
in-dashboard message. This marker is informational, not proof of sender identity.
`POST /api/v1/status` returns **202** only after validation and a committed SQLite
transaction. Malformed JSON or invalid fields return **400**, unsupported content
types **415**, bodies over **32 KiB** **413**, and machine/session capacity conflicts
**409**. Internet mode has a global 200-request burst, replenishes 100 requests/second,
and permits at most 100 concurrent requests with no queue; rejected requests return
**429** with `Retry-After: 1`. These controls do not identify or authenticate senders.
Storage failure or exhausted receipt capacity returns **503**. Error responses contain an `errors` array
without echoing payloads.

The required event fields are `protocolVersion`, `eventId`, `machineId`, `machineName`,
`client`, `clientVersion`, `event`, and `reportedAtUtc`. Hook reports also require
`sessionId`. Enum values use camel case. The only client identifier in v1 is
`copilot-cli`. A failed `postToolUse` may set `toolFailed: true`; the dedicated
`postToolUseFailure` event also maps to Failed.

For `ask_user`, the relay reads `toolName` from tool hooks and sends only
`toolRequiresUserInput: true`, never the question, choices, or answer.
`preToolUse` sets Waiting until that tool's `postToolUse` resumes Executing or
its failure reports Failed. Other tool activity does not clear the pending wait.
Session start/end, agent stop, or an error clears the pending input marker.
The marker persists across relay invocations and dashboard restarts. Existing
hook configuration already covers these events; no new hook is required.
Upgrade Dashboard before the remote package: older receivers reject the new metadata.

On `/api/v1/status`, only hook events are accepted; `heartbeat` and legacy snapshot
fields `state` and `sessions` are rejected. Managed snapshots belong only to v2.
`relayVersion` identifies the reporting relay without
replacing the discovered CLI version. At most **25 computers** and
**64 tracked sessions per computer** are accepted.

Ended sessions remain as ordering tombstones until capacity is needed. A hook for
a new session can evict the oldest ended entry whose result hold has expired;
active sessions and unexpired results are never evicted. A persisted retired-through
timestamp prevents stale hooks from resurrecting evicted sessions, including after restart.

Each session tracks an underlying Waiting, Executing, or Idle state and a 60-second
Succeeded/Failed overlay. Subsequent ordinary hooks update the underlying state
without cancelling that overlay. A new result replaces the preceding result for
that session. Pending `ask_user` input takes precedence over a Succeeded overlay,
but not a Failed overlay; overlay expiration times are unchanged.
Concurrent session priority is:

**Failed > Waiting > Executing > Succeeded > Idle > Offline**.

Managed Offline overrides the aggregate immediately on accepted terminal offline,
or at **last accepted report receipt + two heartbeat intervals + one minute**
(online at 659 seconds, Offline at 660 with the default interval). Details distinguish
last contact from latest Copilot event, interval, and explicit versus timed-out Offline.
Legacy v1 machines use five minutes since their last unique hook receipt.
Health checks and duplicate/stale managed reports do not extend liveness.
Client timestamps determine session ordering, not liveness. Older or equal-timestamp updates
cannot replace a newer session state; equal timestamps use the first accepted state.
Managed heartbeats reconcile session snapshots without extending result holds or
clearing pending questions. Remote clocks should still be synchronized for meaningful
session ordering.

SQLite retains the latest machine/session snapshot, custom display name, and note, not an
activity timeline. On upgrade, a legacy heartbeat `latestEvent` becomes null
(displayed as **No hook received**); machine identities and sessions are preserved.
Managed v2 uses bounded per-machine generation/sequence watermarks, not one
permanent receipt row per heartbeat. Legacy v1 opaque event UUID receipts are retained as idempotency metadata
so duplicate events remain harmless across restarts; they are removed with their
computer. A duplicate does not refresh liveness. The global receipt index is capped
at **1,000,000** entries, with no automatic age-based eviction. At capacity, new
events fail with 503 without changing state or liveness, while existing duplicates
remain harmless. Explicit local computer removal releases its receipts and resets
its replay protection; old reports for that removed computer can be accepted again.
Existing over-limit databases are preserved but cannot accept new event IDs.
SQLite reuses freed pages; removal does not promise to shrink the database file.

Settings and state live under `%LOCALAPPDATA%\AgentSignaler`. Keep this directory
private to the Windows user. Back up the SQLite database only while the dashboard is
stopped, or use a SQLite-aware backup process. `tunnel-state.json` retains only
non-secret resource/owner identity, including pending-create recovery information.
Do not delete it before cleaning up the corresponding cloud tunnel.

## Verification boundaries

The managed tray/heartbeat behavior above is implemented, but installed-UI and
servicing acceptance remain outstanding.
The tray, Exit races, sign-in, interval changes, snapshot recovery, managed protocol
compatibility, and upgrade/rollback cases in the manual plans remain outstanding.
Earlier Relay/tunnel development evidence describes the earlier implementation,
not proof of the managed coordinator path.

Automated tests exercise the reducer, protocol validation, SQLite restart recovery,
deduplication, ordering, concurrency, capacity, exact timeout boundaries, rejection
of legacy v1 heartbeat/snapshot inputs, and real loopback Kestrel requests including chunked size limits.
Remote tests use isolated temporary directories and test doubles; they must not
change a developer's real hooks, scheduled tasks, or firewall.
Managed cross-component qualification must launch the actual Relay through a
running local coordinator and real loopback Kestrel, including concurrent processes,
state errors, privacy checks, and unavailable-network behavior. Direct legacy
HTTP fixtures alone do not establish managed reporting.
Real loopback TLS tests use an isolated, short-lived test CA (not installed in a
certificate store) with per-handler custom root trust to exercise HTTPS health and
status, cookie rejection, redirects and bounded responses. The actual Relay executable
also rejects that untrusted certificate with no plaintext downgrade. These tests do
not prove live Dev Tunnels operation or successful public-certificate delivery from
another Internet connection.

The following require a disposable Windows 11 test environment and are not implied
by a successful build:

- Two-computer LAN/VPN connectivity and network-profile/firewall behavior.
- Actual Copilot CLI lifecycle events, permission waits, tool failure, concurrent
  sessions, and behavior with hooks disabled or an older CLI version.
- Interactive tray, Mica/theme, accessibility, startup, and multi-monitor behavior.
- MSI install, repair, major upgrade, uninstall and forced rollback under both
  normal and interrupted sessions.

Use `docs\ACCEPTANCE.md` to record those checks before distributing a release.

For a repeatable loopback test on one Windows machine, use
[the single-machine manual test plan](MANUAL-TEST-PLAN.md). It includes
synthetic status/relay exercises, optional live CLI and installer checks, expected
results, and cleanup; it does not replace two-computer release acceptance.

## References

- [Copilot hooks reference](https://docs.github.com/en/copilot/reference/hooks-reference)
- [Using Copilot CLI hooks](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks)
- [Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)
