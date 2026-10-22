# In-app GitHub release updates implementation plan

## Goal

Client, Dashboard and Configurator check the releases published to
`andysterland/agent-signaler`. When a newer applicable version is available:

- Client's notification-area icon context menu offers **Update now**.
- Dashboard and Configurator show a non-modal update banner with **Update now**.
- An explicit update action hands off to an independent PowerShell updater,
  gracefully exits the affected apps, downloads and validates the installer,
  runs it without elevation, and restarts the appropriate apps.

Checks are automatic; installation is never automatic. Closing a banner, closing
a window to the tray, signing in, receiving a hook or detecting a release must not
authorize installation or restart an intentionally stopped Client.

This is an implementation plan, not authorization to run installers or modify the
current machine's installed applications.

## 1. Implementation defaults and boundaries

Implement end-to-end without human stage approvals. Use the decisions below and
fake-backed local tests; defer real installer execution, clean-image acceptance
and interactive UI walkthroughs to separately authorized release acceptance.
Do not request production credentials or publish releases as implementation steps.

| Decision | Requirement |
| --- | --- |
| Repository | Fixed `andysterland/agent-signaler`; never an arbitrary repository supplied by a release or handoff file. It is currently public. |
| Release channel | Preserve the current updater's newest-published, non-draft release policy, including prereleases. Clearly label prereleases. Do not silently switch to `/releases/latest`, which excludes prereleases. |
| Version | Canonical `vMAJOR.MINOR.PATCH`, compared numerically within existing MSI bounds. Equal or older versions never cause installation or downgrade. |
| App-to-package mapping | Dashboard updates `AgentSignaler.Dashboard.msi`. Client and Configurator both update `AgentSignaler.Remote.msi`, containing Client, Configurator and Relay together. |
| Package scope | Only already-installed, exact current-user x64 MSI products with the repository's expected UpgradeCodes. No new product installation, prerequisite setup, RpcHost migration or unrelated package update. |
| Privileges | Original installing Windows user, non-elevated. No `RunAs`, UAC request, account switching, machine-wide installer or execution-policy changes. |
| Checks | First check 30 seconds after app startup, then every six hours with up to 10% jitter; manual **Check for updates** bypasses the six-hour interval but respects rate limits. |
| Check limits | One in-flight check per installation; 15-second metadata deadline, 4 MiB metadata bound, maximum 100 release records across five pages of 20 records. |
| Retry | Background failures use 15-minute exponential backoff capped at six hours, honoring GitHub rate-limit/reset and Retry-After headers. No background sign-in or modal dialogs. |
| Check cache | Persist only bounded release/version/asset identity and ETag/check times, never credentials or raw release bodies. Coalesce checks across processes and use a fresh cache for six hours. |
| Handoff | One current-user servicing operation per canonical installation directory, across processes and Windows sessions. Competing clicks join/show the existing operation rather than launch another installer. |
| Restart | Restore affected apps that were running when this explicit update started, in the same session and with their original safe launch context. Never start a previously stopped Client or unrelated Dashboard. |
| Installer UI | `/passive /norestart`, with existing restart suppression and Restart Manager shutdown disabled. Never reboot Windows automatically. |
| Failure | Abort safely on trust, ownership, quiescence or compatibility failure. Surface the failure and preserve an actionable result; never force-kill apps to make installation work. |

The release-enumeration bound must not produce a misleading "up to date" result
when the implementation cannot establish the selected release. Detect pagination
exhaustion/ambiguity and expose "check incomplete" instead of silently selecting a
different channel or accepting an arbitrary partial result.

For GitHub network operations, use managed HTTP in the applications and the
PowerShell script's existing bounded HTTP path for downloads. Do not require
`gh`, Git, a repository checkout, PowerShell 7, or Azure sign-in on target machines.
Public checks must not consult credential helpers. Private-repository access, if
needed later, must be explicit and must not introduce interactive credential lookup
into automatic checks. Preserve existing manual updater credential support without
putting tokens in handoff files, command lines or logs.

## 2. Current repository integration points

Reuse existing update behavior before adding another installer implementation:

- `scripts\Update-AgentSignaler.ps1` already discovers releases, downloads bounded
  assets, reads MSI inventory, validates hashes and MSI identity, stops Client
  through its matching Relay helper, and invokes Windows Installer.
- `scripts\UpdaterPolicy.ps1` defines version bounds, exact release assets,
  UpgradeCodes, per-user/x64/elevation policy, and installer exit-code handling.
- `scripts\Test-Updater.ps1` and `scripts\Test-UpdaterFixture.ps1` exercise the
  updater using non-installable synthetic payloads and non-mutating fixture mode.
- The manual updater currently rejects running Dashboard/Configurator processes
  and asks the user to reopen apps after servicing. Its existing no-restart
  behavior must remain the default outside the new explicit app-handoff mode.
- `src\AgentSignaler.Client\TrayWindow.cs` owns the native tray context menu and
  message loop; `Program.cs` owns Client startup, IPC and graceful shutdown.
- `src\AgentSignaler.Configurator\MainWindow.xaml` and `MainWindow.xaml.cs` own
  connection/integration UI, pending settings, transactions and window cancellation.
  `Program.cs` supports an explicit `--config` launch context.
- `src\AgentSignaler.Dashboard\MainWindow.cs` owns layout and `ExitAsync`.
  `MainWindow.Runtime.cs`, Dashboard.Core's runtime shutdown and canonical resource
  lease own draining receiver, discovery, tunnels and persistence.
- `src\AgentSignaler.Dashboard\Program.cs` supports `--background` and `--exit`
  with current-user local single-instance IPC. Closing to tray is not Exit.
- `ClientIntegrationRuntime`, `ClientCoordinator`, `ClientIpc` and Relay's
  `--stop-client-for-update` already provide exact-configuration ownership and
  shutdown boundaries. Preserve their version checks and reporting semantics.
- `.github\workflows\release-msis.yml` publishes Dashboard MSI, Remote MSI and
  `SHA256SUMS.txt`. It already signs/timestamps MSIs and first-party executables.
  The updater's current unsigned-development warning is not a sufficient trust
  policy for the new unattended download/install handoff.
- `installers\AgentSignaler.Dashboard\Package.wxs`,
  `installers\AgentSignaler.Remote\Package.wxs`, `scripts\Build-Installers.ps1`
  and the existing installer guide own per-user deployment and payload inclusion.

Do not edit unrelated working-tree changes, redesign reporting, add a receiver
process or introduce an Internet-facing update-control endpoint.

## 3. Shared update library and state

Add a non-UI `AgentSignaler.Updates` library referenced by Client, Configurator and
Dashboard/Core as appropriate. Preserve .NET 10, current UI target-framework
choices, nullable analysis and x64 deployment. Keep update models independent of
reporting protocol versions and remote configuration versions.

Define immutable, bounded models for installed-package identity, release
candidate, check result, servicing request, participant identity and durable
result. Expose a testable state machine:

```text
Unknown -> Checking -> UpToDate | Available | CheckFailed
Available -> Preparing -> AwaitingParticipants -> HandingOff
HandingOff -> WaitingForExit -> Downloading -> Validating -> Installing
Installing -> Verifying -> Restarting -> Completed | RebootRequired
Any servicing stage -> Failed | Cancelled
```

Keep `CheckFailed` distinct from `UpToDate`. Distinguish cached availability from
freshly revalidated install eligibility. Bound journals/results and update their
schema explicitly; reject duplicate/unknown fields and unsafe paths.

Use separate current-user, ownership-checked update storage rather than
overloading Dashboard's SQLite/settings or remote integration journals. Use one
per-installation cache/lease and a unique run directory under
`%LOCALAPPDATA%\AgentSignaler\Updates`. Validate canonical paths, ownership, ACLs,
and reparse-point absence. Never recursively clean the whole Updates directory.

Inject HTTP, time, inventory, process/IPC, signature and installer adapters so
normal tests do not download real installers or inspect/change real inventory.

## 4. Release discovery and trust

Check installed MSI inventory rather than assuming an assembly version proves
installation scope or that Client and Configurator are separate products.
Resolve the exact registered installation path and UpgradeCode. A portable/source
build may display release information, but **Update now** must explain that
in-app MSI servicing is unavailable and must not close it or install a new product.
Enumerate only the two expected UpgradeCode families (at most 128 products each),
then query each exact ProductCode in the current user's unmanaged MSI context.
Do not enumerate the unrelated product catalog or open its cached MSI databases.
Only documented absence from that context is excluded; corrupt or unreadable
relevant registrations and cached packages fail closed.

Parse only supported release fields. Bound assets, strings, page count and JSON
depth; never render release bodies as HTML or execute release-provided commands.
Preserve the exact three-asset contract currently enforced by `UpdaterPolicy.ps1`.
Use numeric version comparison, reject unsupported tag formats and verify that
package ProductVersion matches the selected release tag.

Pin the user's selected release ID, tag, asset IDs, expected filenames, sizes and
expected checksums for the handoff. The script must not silently select a newer
release after the user clicks an offer. If the pinned release is deleted, replaced
or changes identity, fail and require a fresh check.

Keep strict HTTPS and bounded approved GitHub redirect handling. Never forward
GitHub Authorization headers to a download CDN. Do not persist redirect URLs,
which can contain temporary signed query parameters, or log raw HTTP errors.

For an installed-app update, require all of:

1. Expected repository/release/asset identity and exact download size bounds.
2. SHA-256 matching the pinned, strictly parsed checksum manifest.
3. Valid timestamped Authenticode MSI signature with the authorized release
   publisher identity and trusted code-signing chain.
4. Expected UpgradeCode, canonical ProductVersion, valid ProductCode, x64 package,
   per-user scope and non-elevated summary flags.

Checksums alone do not authenticate a publisher. Reuse the release pipeline's
publisher policy in a shared testable form, including supported certificate renewal;
do not hard-pin a short-lived leaf certificate or accept any trusted signer.
Unsigned, locally self-signed or mismatched-publisher packages must be rejected
in the production in-app path. Test adapters may simulate trust, never bypass it
for real installers.

Recheck package hashes, signatures and identity while holding read-only file
handles that prevent replacement through servicing. Compare the installed
product again immediately before installation to avoid concurrent downgrade or
double-install decisions.

## 5. UI requirements

### Client notification-area menu

Add **Check for updates** and, only when a newer eligible Remote release is known,
**Update now** to `TrayWindow.ShowMenu`. Show a short disabled version/status row;
disable repeated actions while preparing or handing off.

Do not run network, inventory or PowerShell work in the native window procedure.
Post immutable state updates back to the window thread; keep menu callbacks thin.
Selecting **Update now** explicitly authorizes updating the complete Remote
package and resuming this running Client afterward. Explain that Configurator
also closes if open and reporting pauses during servicing. Use a bounded explicit
confirmation before starting disruptive coordination, not on each subsequent step.

Ordinary **Exit** remains unchanged and does not restart Client. Explorer restart
and tray-icon failure retain existing safety behavior.

### Dashboard and Configurator banners

Use an accessible WinUI InfoBar above the main content, independent of existing
error/connection banners. Example: "Version X is available. Update and restart."
Include **Update now**, **Later**, and a safe release-details link. Mark preview
releases clearly. Provide **Check for updates** and last-check/result information
in settings/help.

Later dismisses this release's banner until the next app launch or manual check;
it does not disable background checks or authorize installation. Do not change
reporting preferences when dismissing it.

Update actions show preparing/progress state and are disabled during conflicting
transactions. Configurator must identify unsaved connection, credential, integration
and startup edits. Ask the user to save through the existing verified apply flow,
explicitly discard, or cancel; never auto-apply edits or silently lose them.
Other affected Configurator windows receive the same cooperative prepare request
and can veto. Dashboard must similarly finish or cancel in-flight edits/workflows
before consenting to exit.

Marshal updates through each UI dispatcher and cancel check subscriptions on
actual app exit. Minimizing Dashboard to tray must not stop its check scheduler.
Do not automatically open/restore a minimized window merely because an update exists.

## 6. Safe handoff and cooperative shutdown

Bundle the updater and its required policy/helper files with each MSI. Do not
download and execute a PowerShell script from GitHub at update time. Include the
scripts in release signing/verification and installation payload validation.

Before shutdown, copy the exact installed, validated updater dependency set into
the unique run directory, pin its identity/hash and launch the staged entry script
using system x64 Windows PowerShell 5.1 with `-NoProfile -NonInteractive -File`.
Respect execution policy; report policy blocking without weakening it. Never
execute an inline command assembled from release text or user input.

The staged script must survive removal/replacement of its original installed
files and exit of its parent. Do not put it into Dashboard's tunnel-process kill
job. A readiness acknowledgement must arrive before the initiating app commits
to exit; launch failure leaves the application running.

Use an authenticated current-user local pipe plus a protected, versioned manifest
for this handoff. Permit only exact app/package actions and validated paths.
Record the installation identity, target release/assets, run nonce, parent PID
and creation time, approved participants, original visibility/launch context,
canonical data/config path references and restart intent. Do not store settings
contents, SAS URLs, tokens, conversation data or arbitrary restart commands.
Bind the pipe connection, nonce and request to the owning user/install operation.

Add a versioned maintenance endpoint/registration for each app instance, distinct
from reporting IPC where necessary. This is local control only, not a new network
listener. Inventory affected processes by exact canonical installed executable
path, user, session and process creation time; do not treat image name as ownership.
Use ephemeral ownership-checked registration plus exact process inventory so an
unregistered or older participant is detected rather than silently ignored.

Use a two-phase participant protocol:

1. **Prepare:** acquire the per-installation servicing lease; resolve every affected
   instance and verify same-user/same-session ownership. Participants drain or
   reject conflicting work and report pending edits without closing.
2. **Commit exit:** after all consent and script readiness, request graceful shutdown.
   Dashboard uses `ExitAsync` and drains receiver, discovery publisher, tunnel,
   SQLite work and canonical lease. Configurator cancels/drains reads and finishes
   or cancels owned transactions. Client uses existing current-user exact-config
   stop/ownership mechanisms, suspends reporting/transcripts and releases IPC.

Updating Remote coordinates every Client/Configurator instance using that installed
binary set, even if different config directories are used. Dashboard-only update
does not stop a Client using the separate Remote installation. Do not update both
products merely because both are installed.

If another affected instance belongs to a different Windows session, cannot prove
ownership, lacks maintenance support, refuses preparation or cannot shut down
cleanly, abort before MSI and show instructions to close that exact instance.
Never close another user's apps, force-kill a process, repeatedly race a new
instance, or accept an unverifiable shutdown acknowledgement.

Hold a maintenance gate from prepare through servicing/restart. Normal app startup
must detect an active, live owning updater and decline activation/startup/reporting
until servicing finishes, without launching additional installers. Detect stale
gates using validated PID/creation time and a bounded operation lease, not age
alone. Recheck inventory after shutdown and before MSI to catch launch races.

During restart health checks, retain the operation gate in `Restarting` together
with its matching restart marker. Admit only an updater-authorized child whose
one-use protected grant matches its exact new PID, creation time, user, session,
executable and captured restart context. The child may wait at most five seconds
for the updater to atomically publish this grant after process launch; registration
consumes it under the maintenance lease. Ordinary startup and competing prepare
requests remain blocked until restart health and durable result recording finish.

The initiating app exits only after readiness and consent; the script waits for
the exact participant PIDs to exit and their owner/lease handles to be released.
Use a 60-second overall quiescence budget and retain existing narrower shutdown
budgets. Failure means no installer invocation, not a forced termination.

Before transferring the servicing gate, reserve commit through each authenticated
maintenance endpoint and recheck the exact participant inventory and live updater.
Client must atomically reserve update shutdown against ordinary tray Exit: Exit
winning this race declines commit and leaves the gate in Preparing for rollback;
an accepted reservation disables ordinary Exit until cancellation or update
shutdown. Missing Client reservation support fails closed. A stopped Client is
absent from the participant manifest and is never added to the restart set.
If a positive reserve or commit acknowledgement cannot be delivered before
shutdown is queued, cancel the local unqueued reservation within five seconds.
Never cancel after shutdown dispatch or clear the servicing gate on this path.
Authenticated repeat cancellation remains idempotent in Preparing, including
after local acknowledgement-failure cleanup; cancellation in Servicing is refused.

## 7. PowerShell download, installation and restart

Extend `Update-AgentSignaler.ps1` with an explicit app-handoff mode that consumes
the protected manifest and validates it before any side effect. Keep ordinary
manual invocation and fixture `-WhatIf` behavior compatible and non-restarting.
Share validation policy rather than duplicating permissive versions in C# and
PowerShell; use common fixtures to prove parity where both must implement it.

After acknowledged shutdown, the independent script:

1. Revalidates pinned release/asset metadata and downloads the checksum manifest
   and selected MSI into its owned run directory. Use existing 64 KiB checksum/
   1 GiB MSI limits and five-minute per-asset deadlines. Honor bounded throttling.
2. Verifies trust, checksum, package identity, installation scope, version and
   file pinning before invoking Windows Installer. Script progress remains visible
   in a non-elevated progress console; do not leave a silent vanished application.
3. Runs the system `msiexec.exe` with `/i`, `/passive`, `/norestart`,
   `REBOOT=ReallySuppress` and `MSIRESTARTMANAGERCONTROL=Disable`, using safe
   argument quoting and an owned per-run log. No credentials go into MSI properties.
4. Handles `0` as success, `3010` as reboot-required, `1618` as another install in
   progress, and cancellation/failure codes distinctly. Never terminate an active
   `msiexec` to enforce a script timeout or retry servicing blindly.
5. Verifies registered installed version, product identity, expected installed
   executable paths and publisher trust before restarting anything.
6. Restarts eligible apps using fixed allowlisted executables and safe structured
   arguments, then writes an authoritative durable result.

Restore Client only if it was running and consented as part of this update;
preserve its exact `--config` and background launch. Restart previously running
Configurator windows with their original config selection, without applying
unsaved work. Restart Dashboard with its original data directory and visible or
background mode; restore user layout through saved settings, not arbitrary script
commands. Preserve disabled sign-in startup and stopped reporting preferences.

A successful restart means more than `Process.Start`: verify exact new process
identity and local maintenance/health readiness within 30 seconds. For Client,
differentiate local runtime readiness from remote Dashboard availability; an offline
receiver must not be misreported as a failed installation. Do not open Configurator
as a side effect of probing Client activation.

For exit `3010`, record reboot required and do not automatically restart apps whose
files may still require replacement. Show the explicit reboot-required result;
never reboot Windows. For installer failure, restart prior apps only if inventory,
binary version/identity and configuration compatibility prove the prior installation
is intact. If that cannot be established, leave them stopped and show recovery
instructions. Never automatically downgrade or claim cross-package rollback.

If download/validation fails before MSI, the unchanged prior installation can be
restored under the captured restart intent after releasing the maintenance lease,
while retaining the restart gate and exact-child admission checks through health.
If Windows Installer is still servicing after a long wait, show a waiting state;
do not release the gate or restart against files being replaced. Process
interruption must leave a journal that can distinguish not-started, running,
completed and unknown MSI outcomes for recovery.

Do not auto-launch the updater on login after failure. Next application launch
may display the bounded durable result and a safe retry/repair action. Provide a
visible result and recovery path when the application cannot restart. Cleanup only
this run's verified files after completion; retain bounded diagnostic results/logs
without sweeping unrelated staging directories.

The result schema has no chronological timestamp. Show all bounded unresolved or
non-successful records rather than naming a last/latest result from GUID order.
Recovery presentation is observation only: it must not clear unknown MSI gates,
retry servicing, terminate an installer or restart an application.

## 8. Packaging, signing and compatibility

- Publish the shared updates assembly and staged-script dependency set in both
  application payloads. Validate matching script/policy versions and hashes.
- Extend release signing/catalog verification to first-party updater scripts as
  well as current executables/MSIs. Do not weaken the exact three release-asset
  contract just to distribute scripts; they travel inside the signed MSIs.
- Preserve per-user MSI scope, existing UpgradeCodes and coordinated Remote
  servicing/rollback. Preserve local config, SQLite data, DPAPI credentials,
  discovery signing identity, tunnel identity and foreign IDE configuration.
- Do not introduce installer custom actions that automatically start Client.
  Restart is an explicitly authorized handoff responsibility, not ordinary MSI
  upgrade behavior, so manual upgrades continue to respect prior Exit.
- The first supporting release must still be installed using the existing manual
  mechanism. Older installations cannot retroactively discover or execute a newly
  added in-app updater. Explain this bootstrap boundary in release notes.
- Update installer, user and contributor documentation, including prerelease
  selection, no-elevation expectations, reporting downtime and failure recovery.

## 9. Implementation stages

1. Add shared update models, release policy, installed-inventory adapters and
   bounded fake-backed discovery/cache scheduler.
2. Refactor and extend existing script/policy handling with pinned-release
   handoff, trust enforcement, journals, result and safe restart. Preserve manual
   and `-WhatIf` contracts.
3. Add app maintenance registration, per-installation lease, startup gate and
   two-phase shutdown adapters for Dashboard, Configurator and Client.
4. Wire Client tray actions and both WinUI banners, including pending-edit consent,
   cancellation, background lifetime and release-specific dismissal.
5. Wire packaging/signing, recovery presentation and documentation.
6. Run targeted existing script and .NET tests, affected full test projects and
   Release x64 UI builds, then build/inspect installers. Do not execute installers.

No stage requires a human sign-off. If a genuine technical blocker is discovered,
preserve the fail-closed boundary, complete independent work and report it
explicitly; do not silently bypass signing, ownership or quiescence requirements.

## 10. Verification and acceptance

Extend existing runners rather than introduce new test tools.

| Boundary | Required coverage |
| --- | --- |
| Release policy | Numeric versions/MSI bounds, prereleases, draft exclusion, newest-published selection, pagination caps, malformed/duplicate fields, renamed/missing/extra assets, older releases and no downgrade. |
| HTTP/cache | Cancellation, size/depth limits, conditional requests/304, stale cache, concurrent checks, offline/proxy failures, GitHub 403/429 reset/backoff, safe redirects and no credential leakage. |
| Script policy | Extend `scripts\Test-Updater.ps1` and fixture support for pinned releases, signature requirements, handoff modes, return codes and policy parity without real installation. `-WhatIf` never closes/restarts apps or invokes MSI. |
| Ownership | Exact user/path/session/PID-creation identity, competing update clicks, unknown/old participants, cross-session refusal, reparse points, manifest tampering, stale gates and startup races. |
| Shutdown | Dashboard receiver/tunnel/discovery lease drained; Configurator unsaved edits/transaction veto; all matching Remote instances coordinated; parent exit leaves updater alive; stalled participants prevent MSI. |
| Installer/restart | Fake installer verifies exact arguments, MSI `0`/`3010`/`1618`/failure/cancellation, corrupt staged files, already-updated inventory, reboot suppression, safe failure restoration and no restart before MSI finishes. |
| Reporting/privacy | Tray Exit stays stopped, stopped Client never starts, existing config/credential paths preserved, no SAS/tokens/settings content in handoff/logs, no hooks start Client, no automatic configuration migration. |
| UI | Banner/actions disabled correctly, no network on tray/UI thread, dispatcher-safe updates, pending-edit choices, Later semantics, closed-window cancellation and visible recovery result. |
| Packaging | Both MSIs remain per-user x64, shared updater/script dependencies shipped and trusted, Remote binary versions stay aligned, XBF/PRI valid, existing asset contract preserved. |

Put shared logic coverage in the smallest appropriate existing Remote,
Dashboard.Core and Integration test projects; add references needed to exercise
the non-UI updates library. Use process fixtures and non-installable fake payloads
for launch/handoff tests. Never use real `msiexec` as the fake installer.

`UpdateWireParityTests` stages actual dependencies through the managed launcher
and runs the non-installable `tests\fixtures\UpdateWireFixture.ps1` entry under
x64 Windows PowerShell 5.1. It calls the production manifest validator, gate and
grant writers, JSON framing and authenticated pipe helpers without overriding
them. Managed strict storage reads and the real maintenance listener verify root
ACL protection, existing-record replacement, default/custom install paths,
one-use restart admission, competing-update refusal and local health transitions.
Only installed-app facts and signing trust are injected; no MSI, installed
inventory, production app activation or network request is executed.

Unattended implementation is complete when the complete check/offer/consent/
shutdown/download/validate/install/restart flow works under injected adapters,
affected tests/builds pass, required installer inspection is complete, and changes
and related documentation are saved. Distinguish this from deferred release proof.

Deferred, separately authorized acceptance on a disposable Windows 11 x64 user:

1. Install the first supporting release as a standard user and publish a newer
   correctly signed release in the intended channel.
2. Verify all three offers, including tray/background lifetime and non-modal UI.
3. Update Dashboard and verify clean receiver/tunnel shutdown and one restart.
4. Update Remote from Client and from Configurator, with multiple Configurator
   windows, pending edits and both running/stopped Client cases.
5. Verify no elevation prompt, no automatic OS reboot, exact selected release,
   restored safe launch context and reporting only when previously running.
6. Exercise real network interruption, rejected signatures, installer failure,
   reboot-required result and manual recovery without data/credential loss.

The release gate may execute installers only with separate explicit authorization.
Ordinary development remains build-and-inspect only.
