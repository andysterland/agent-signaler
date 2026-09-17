# Release acceptance checks

Run against two disposable Windows 11 x64 computers, with a current Copilot CLI.
Do not run installer/firewall tests on a shared development machine.
Record the product, CLI and OS versions and outcomes with the release evidence.
The managed tray/heartbeat behavior is implemented. The checks below remain
outstanding manual acceptance, not assertions that installed/manual tests passed.

For local testing without a second computer, follow
[the single-machine manual test plan](MANUAL-TEST-PLAN.md). Record its results
separately; loopback tests do not establish LAN/VPN or remote firewall behavior.

## Multi-Copilot session release gates

Implementation uses the existing sole current-user Client and source/session
identity. The following manual gates are **Not run**, not claims established by
offline tests. Use MC01-MC09 in `MANUAL-TEST-PLAN.md`:

- Interleave two CLI conversations in one scope with Visual Studio and VS Code;
  waiting wins over other sessions' resumes, success and failure. Leave a question
  quiet beyond the heartbeat deadline while Client remains online.
- Verify session-specific accepted event/timestamp labels and unknown legacy
  metadata, with no prompt/response content in status persistence or RPC v1.
- Verify source/session/stream transcript drill-in, retained-ended access, stable
  list selection, Back, tab hiding and immediate text invalidation.
- Inspect 0/1/10/11/64 compact indicators at supported DPI and small work areas;
  all 4 x 4 logical-pixel squares remain visible through wrapping/scrolling.
- Verify receiver-first matching-binary deployment and the documented stopped
  backup/restore rollback procedure in a disposable environment.

Network/listener, actual host, interactive WinUI, tunnel and installed servicing
coverage is deliberately excluded from the current offline-only validation.

The focused offline UI/runtime validation includes:

- `SessionPresentationTests`: identity/order, accepted event timestamps, legacy
  unknown metadata, offline/ended indicators, and session-only revision changes.
- `RpcSessionProjectionTests` and `RpcProtocolTests`: direct in-process RPC
  execution, strict existing DTO allowlist, waiting and stale pagination.
- `CopilotsControllerTests`, `CompactSessionPresentationTests`,
  `TranscriptViewControllerTests`, `TranscriptDetailsSourceTests`,
  `MachineCardPresentationTests`, and `MachineCardAppearanceTests`: metadata-only
  rows, retained streams, cancellation/invalidation and complete indicator layout.

Run only these inspected offline selectors rather than entire runtime/RPC or
integration suites that initialize real listeners. Native rendering and
production host capture remain separate release gates even when these pass.

### Offline implementation verification (September 17, 2026)

- Release/x64 solution and Visual Studio builds passed. Dashboard XBF and PRI
  generation passed. The existing `PrerequisiteSettingsTests` xUnit2013 analyzer
  warning remains; no compiler errors were reported.
- Service: 374 selected tests passed; Remote: 45 selected tests passed.
- Dashboard.Core: 6 session projection/revision tests passed.
- RpcHost: 43 in-process protocol/projection tests passed.
- Integration: 111 selected Copilots, transcript-controller and compact-card
  tests passed, including unrelated-stream/partial-expiry selection regressions.
- No network listeners, external-service tests, live tunnel tests, application
  launches or installer executions were run for this validation. Full suites
  containing those behaviors were intentionally not selected.
- Legacy Idle tombstones and enriched session-end observations are excluded
  from connected indicators. No silence-based session expiry was introduced.

## RpcHost release acceptance

The [RPC protocol](rpc-host-protocol.md) separates required deterministic
automated gates from interactive release acceptance. Do not treat configuration
assertions, fake CLI/native adapters or a queued workflow as live verification.
The execution handoff records actual automated results, artifact hashes, source
SHA and publication status separately from this checklist.

| Check | Status |
| --- | --- |
| Supported WebView/browser shell and real origin policy | Not run - deferred release acceptance |
| Real Azure account login/permissions and Dev Tunnel lifecycle | Not run - deferred release acceptance |
| Real Windows App mapping, reuse, sign-in and foreground denial | Not run - deferred release acceptance |
| Dashboard visual/tray parity and second interactive Windows session | Not run - deferred release acceptance |
| Clean supported Windows image without .NET/Windows App SDK | Not run - deferred release acceptance |
| Disposable-VM MSI install/upgrade/repair/uninstall/rollback | Not run - deferred release acceptance |
| UAC approve/deny, silent insufficient privilege and intended-user elevation | Not run - deferred release acceptance |
| Mandatory exact Private receiver rule, port maintenance, foreign-rule preservation and rollback | Not run - deferred release acceptance |
| Parent-exit/forced termination/port collision repeated in production launcher | Not run - deferred release acceptance |

RpcHost intentionally accepts unauthenticated control from unrelated localhost
pages and native clients, including other local users supplying Origin. This
accepted risk includes executable-path changes, diagnostics and destructive
operations. Neither Origin nor the single-controller lease authenticates a user.
Do not use the endpoint across a trust boundary.

## Detailed-conversation prototype release gates — pending

This is the resolved externally consented prototype policy, **not a release
validation result**. Historical automated/live evidence below predates this work
and does not validate the transcript stream, file reader, viewer, or v5 servicing.
Record fresh Release/x64 build, targeted/full affected synthetic test results,
resource measurements and packaging inspection separately after integration.
No new manual UI, actual-host, live tunnel, certificate/trust, or installer
execution acceptance is claimed here; all remain **Not run** for this feature.

- External informed permission for content, destination, and retention must be
  obtained before distribution/use. The app does not collect/verify it;
  installation and notices do not establish consent. No pairing, enrollment,
  bearer-token or certificate-provisioning gate is added.
- New v5 and explicit v5 migrations default detail on; explicit false survives
  repair/recovery. Legacy v1–v4/HTTP/LAN stay status-only without silent rewrites.
  Upgrade Dashboard first, matching Relay/Client/Configurator together. Verify
  old/new IPC compatibility with readable configurations and visible v5 rejection
  by old Clients. Downgrade is an explicit stop/purge/validated-v4 transaction with
  compatible servicing; v4 cannot retain a v5 flag and later v5 migration previews
  default-on again. Failed opt-out save suspends this run; rollback must not
  replace a saved false with older true or start a previously stopped reporter.
- Exercise actual Relay/current-user IPC/Client/isolated HTTPS test
  transport/volatile store/in-process reader/view model with synthetic user text,
  fictional PII, tool names and ordering. For assistant-file coverage use only
  independently evidenced profiles, or an explicitly test-only adapter.
  **No production Copilot CLI, VS Code, or Visual Studio file profile is currently
  verified.** The [P0 evidence record](transcript-capability-evidence.md) records
  the reviewed sources and deliberately empty production registry.
  Hook fields/stop paths, bundled CLI and invented fixtures are not
  format/path/session/completion evidence. Partial capture must remain explicit.
- Verify hook-only user/tool provenance and completed user-facing assistant-only
  file extraction. Exclude tools bodies/results, errors, reasoning, attachments,
  subagents and arbitrary payloads. Local stop references never reach HTTP,
  persistent state, diagnostics, or UI. No watcher/scanner, path guessing,
  whole-history replay, viewer/network-triggered read, or host-file mutation.
- Details require canonical configured HTTPS, ordinary TLS validation, compatible
  receiver and its owned running tunnel on loopback Internet mode. Anonymous
  callers can spoof IDs, inject/purge data and exhaust bounded capacity; loopback
  does not authenticate tunnel provenance. Disable/incompatibility/TLS/redirect/
  endpoint changes stop detail without suppressing status or rerouting text.
- Measure 32 KiB events, Client 4 MiB and receiver 64 MiB total feature budgets
  including scratch/index/reader/UI copies, fixed quotas and retry limits.
  Exercise bounded stop reads (750 ms/2 MiB/128 records/16 replies), admission,
  baseline/reset/no-backfill, coalescing and cancellation. Verify gaps, duplicate
  ordering, restart epochs and 30-minute receipt-based TTL with no read extension.
- Agent Signaler persistent config/state/SQLite/WAL/log/recovery artifacts must
  contain no conversation output or local read references/cursors. Test-owned
  host input files are distinguished from output and unchanged by every lifecycle
  operation. Memory-only does not promise absence from host history, OS
  paging/hibernation, or external crash capture.
- Settings opens first and retains all editing/Dev Box actions/drafts. Copilots
  opens session-specific transcripts with bounded selection/paging/visible text
  and only in-process reads. Verify
  partial/empty/loading/error/expiry/reset states, inert rendering, no
  copy/export/actions, correct scroll intent and accessibility. Clear, disable,
  expiry, eviction, removal, restart, close and Exit invalidate visible/cached
  content and reject stale completions; tab switches never start reporting.
- Verify opt-out/Exit immediately stop admission, cancel reads/sends, drop queues
  and references within the existing shutdown budget, and do not restart Client
  from hooks. Unreachable remote purge is best-effort; TTL/local clear/disable
  bounds earlier retention. The single close/purge identifies one stream, so
  other sources' retained text may remain even when reachable; test and disclose
  this rather than claiming machine-wide remote deletion. Re-enable starts fresh,
  not from host history.
- Inspect fresh managed payloads, matching remote binary versions and WinUI
  XBF/PRI packaging without executing packages. Ship no transcript database,
  host transcript inputs, credentials/pairing or certificates. Preserve exact
  app ownership and explicit opt-out during servicing. Source inspection is not
  installed rollback/upgrade/uninstall evidence.

Use T01–T18 in `MANUAL-TEST-PLAN.md` for separately authorized acceptance. Missing
production profile evidence does not block the reader framework/partial viewer,
but must never be reported as complete production assistant capture.

### Synthetic implementation verification

The transcript-only staged tree was validated separately from pre-existing local
edits:

- Release/x64 solution build passed; the existing `PrerequisiteSettingsTests`
  collection-count analyzer warning remains.
- Remote: 526 passed; Service: 441 passed; Integration: 1,294 passed and the
  opt-in live-tunnel test skipped; Tunneling: 192 passed.
- Coverage includes actual Relay/local IPC/isolated TLS/store/viewer-controller
  integration, test-only file adapters, managed allocation bounds, and retry,
  cancellation, configuration and invalidation regressions.
- Fresh self-contained Dashboard and Remote MSI builds and read-only inspection
  passed (672 and 532 payload files). WiX ICE validation remained blocked by
  system policy (`WIX1105`); no elevation or suppression was attempted.
- Production host formats, native WinUI allocation/DPI/accessibility behavior,
  live networks and installed servicing remain unverified. No real hooks,
  conversations, tunnels, trust changes or installer executions were used.

## Historical automated implementation validation (September 15, 2026)

- On-disk solution Debug/x64 build and Visual Studio build succeeded with no compiler warnings or errors.
- Service: 398 tests passed. Remote: 330 tests passed, excluding the two tests that call the real Windows Task Scheduler.
- Targeted IPC, connectivity, shutdown, source presentation and Windows shell integration: 113 passed; one opt-in live tunnel test skipped.
- Synthetic Windows `cmd.exe` tests execute the built Relay through real local IPC using space, Unicode and shell-metacharacter fixture paths, with empty stdout and a measured duration below the three-second hook timeout.
- All hook/profile/state fixtures are isolated. No real IDE settings, sign-in startup entries, scheduled tasks or cloud resources were changed during these runs.

These results validate application behavior, not installed IDE hook support or live
MSI servicing. WiX ICE validation is blocked by local system policy; complete ICE,
Dashboard/Bundle packaging and actual upgrade/rollback/uninstall acceptance on an
authorized disposable machine before release.

## Multi-target IDE release gate — not verified by implementation

New Configurator previews write configuration v5 and prefer enriched Dashboard
`GET /api/v4/health` returning exactly `{"protocolVersion":4,"status":"ok"}` and
`POST /api/v4/reports`, with source-aware v3 fallback only on v4 HTTP 404.
Retain the v1/v3 configuration tests below as compatibility checks; they do not certify
native Visual Studio or VS Code hooks. Upgrade Dashboard first and deploy matching
Client/Relay/Configurator binaries together.

**No real IDE workflow was verified during automated implementation.** Inventory
observed September 15, 2026:

| Installation/profile | Observed build | Release evidence status |
| --- | --- | --- |
| Visual Studio Main | `18.12.12211.431`; bundled CLI `1.0.83` | **Not run / verification required**; effective IDE loader/home/experience/events unproved |
| Visual Studio IntPreview | `18.12.12211.348`; bundled CLI `1.0.83` | **Not run / verification required independently**; shared candidate home is not isolation |
| VS Code default local profile | `1.137.0`; installed `GitHub.copilot` files `1.388.0` | **Not run / verification required**; activation, profile hooks, shell, trust and policy unproved |
| Custom local Windows profiles | Record exact root and build during acceptance | **Not run / verification required per profile** |

The Visual Studio home `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli` is a
candidate only. Binary/version/directory discovery, extension files, synthetic
events, dashboard connectivity and manually invoking a bundled CLI are not actual
IDE verification.

Execute M01–M15 in `MANUAL-TEST-PLAN.md` using disposable profiles.
Configurator opens on **Connection** (URL/heartbeat, Test/Cancel test, initial reporter
Save and explicit Start); **Integrations** contains discovery, target selection and
Verify/Remove. Evidence, confirmations, diagnostic cancellation and interrupted
verification recovery are in **Verification**. Preview/Apply, integration transaction
recovery and full uninstall are in **Review & maintenance**. Run M16–M21 for these
control locations, tab state/cancellation, validation focus and accessibility; these
remain manual gates, not results established by the automated checks above.

Record:

- Verify/Retry from Integrations selects Verification, shows the exact diagnostic
  preview in Review & maintenance before explicit consent, then returns to
  Verification even if cancelled, retaining target context/status. Existing saved configuration and explicitly
  started Client; actual identified IDE/profile experience with other hosts excluded.
  Completion confirmations must be unchecked by default and supported by evidence.
- Observed event/schema/timeout contract, stable session IDs, required reload steps;
  for VS Code, the effective Hooks UI, policy/trust, workspace/profile overrides,
  actual Windows `cmd` command execution and loader isolation.
  For a Visual Studio home override, verify the exact user-selected hook directory
  with its new physical scope ID; changing a candidate must not change environment
  variables or IDE binaries, or reuse evidence for another scope.
- Cancel/close/interrupted recovery from Verification, hash conflicts and user-edit preservation.
  No probe or profile setting changes during ordinary read-only discovery.
- Complete selected-set preview in Review & maintenance without silent skips, transactional rollback,
  JSONC preservation/concurrent-edit rejection, saved/proposed state, legacy
  manifest migration, shared-scope references and target-only/full removal.
  Per-target Remove starts in Integrations and shows its exact preview in
  Review & maintenance before consent; full uninstall and integration transaction
  recovery remain in Review & maintenance.
- Concurrent CLI/VS/VS Code source attribution and collision isolation, exactly
  one report per host invocation and one persistent machine heartbeat/tray owner.
  Shared Visual Studio scope must not falsely identify a particular instance.
  Verified isolated VS Code/custom CLI-home/VS scopes may coexist; default CLI
  observer and explicit cross-scope profile-loader conflicts must remain blocked.
  Automatic exact-file loader exclusions are unsupported, and unrelated hooks or
  settings must never be disabled to obtain a passing result.
- VS Code Stop means Waiting, neither success nor session end. Missing permission,
  tool-failure, success and close signals remain explicit limitations. Heartbeat
  proves reporter availability, not an open IDE session; no fabricated session expiry.
- Exit stops every source; opening/editing/testing Configurator and IDE hooks do not
  restart it or bypass IPC. Explicit Start resumes using saved settings only.
- Blocked policy, unsupported/remote hosts and invalidated versions remain disabled
  or require verification, never fake Verified. Do not claim coverage for inline
  completion, ordinary chat or another agent experience from one passing workflow.

Record successful compatibility keyed by installation/profile, exact host and
adapter versions, physical scope, tested experience and observed events. A partial
result is not full support. All entries above remain open until actual live evidence
is recorded; builds and fixture tests alone cannot close these gates.

## Connection and privacy

- Install the dashboard on A and remote components on B using the MSIs.
- Test `/health` from B and configure hooks with the UI preview: neither operation
  registers a machine or refreshes machine liveness. Approved Configurator Apply
  for first-time setup or legacy v1/v2-to-v3 migration starts
  Client; its acknowledged v2 start registers an Idle card without a hook. Close
  Configurator and confirm periodic reporting continues. Send a real hook through
  Relay's bounded current-user IPC and verify Client is the sole managed HTTP reporter.
- Verify Relay `test` uses read-only `DashboardConnection.TestAsync`: managed v3
  config probes `/api/v2/health`; legacy v1/v2 config probes `/health`,
  with no status POST or local session-state mutation; the removed `heartbeat`
  command must fail without sending a report.
- Inspect CLI/verified CLI-backed owned hook JSON: absolute `exec` and `args` array,
  no shell wrapper. VS Code instead uses its verified shell schema and trusted
  Windows command encoding: spaces, Unicode, `&`, `(`, `)` and `^` are tested cases.
  Quotes, `%` and `!` paths/arguments must fail closed at preview, not expand or be
  treated as universally supported. Synthetic cmd-to-Relay/IPC tests are not proof
  that the actual IDE uses this execution contract.
- Verify the unrelated hook fixture has unchanged contents after install, repair,
  endpoint update and uninstall; verify timestamped backups exist.
- Inspect status network requests and application-created local files using
  synthetic prompts only: no message text, tool arguments/output, local transcript
  reference or error text. Separately test v5 detail requests against the allowlist
  above; permitted message text/fictional PII is expected there, not in status or
  persistent storage. Do not confuse test-owned transcript inputs with app output.
- Verify the endpoint rejects malformed bodies, unknown properties and oversized
  fixed-length and chunked bodies.

## HTTPS and CLI-hosted Internet release

The dashboard now defaults to automatic CLI sharing without a consent checkbox;
explicit LAN mode and explicit Stop preferences are retained. The preferred MSAL + SDK identity
route remains blocked on approved registration/scopes. Do not sign off Internet
mode based on client tests, an ordinary build, or the short synthetic CLI proof.

- Without running MSI servicing, load/preview an existing v1/v2 installation: config bytes, UUID, hooks,
  session state and any legacy task must be unchanged. Verify legacy v1 direct HTTP
  compatibility separately; it is not a managed Relay fixture.
- Upgrade Dashboard first, then Client/Configurator/Relay together. Preview an HTTPS
  endpoint and approve v3 migration only after checking its exact fields,
  `heartbeatIntervalSeconds: 300`, Client/Relay paths, sign-in command, and warning.
  Refuse managed setup against a dashboard lacking `/api/v2/health` capabilities
  (exactly `{"protocolVersion":2,"status":"ok"}`);
  preserve the strict legacy `/health` shape, with no direct-v1 fallback.
  Successful Apply preserves identity and removes any remaining ownership-verified
  legacy task for published-folder setups. MSI servicing removes it earlier through
  the separate task-only migration; no operation creates a new task.
  Configurator transaction failure restores previous
  config, owned hook, manifest, startup value and legacy task. Distinguish committed
  disk settings from runtime launch/reload failure with explicit recovery.
  Verify timestamped file/task backups for
  old-release recovery, and refusal to delete a modified or unowned task.
- With an isolated trusted HTTPS fixture, verify GET JSON health and POST 202,
  no Authorization/X-Tunnel-Authorization/Cookie, no TLS bypass and no redirects
  (including HTTPS-to-HTTP). HTML/interstitials are failures.
- Exercise DNS, TLS, restricted access (401/403), unavailable tunnel, proxy 407,
  throttling (429/Retry-After), cancellation and incompatible protocol diagnostics.
  Test system-proxy/PAC behavior without credential prompts on supported networks.
- Measure actual Relay processes against running IPC with DNS/TLS/Client latency: internal work remains
  bounded to 2.2 seconds and the supported hook environment must complete within
  its 3-second timeout. A cancellable 10-second UI probe is not timing proof.

Additional CLI-host acceptance:

- Qualify the pinned CLI installation/signature/version and explicit account login
  in an unpackaged WinUI deployment. No remote or LAN-mode CLI prerequisite.
- Verify loopback-only Internet binding from a second LAN computer; no inbound
  firewall rule, local certificate, or URL ACL is needed.
- Check exact anonymous-connect-only port ACLs, ownership/account mismatch,
  interrupted creation, unavailable/deleted resources, conflicting hosts, and
  preservation of unrelated resources.
- Verify the copied public HTTPS base URL exactly matches the display, is disabled
  while unavailable, and works unchanged on an independent Internet connection.
- Test owned-process cleanup on stop, cancellation, exit, and dashboard crash;
  unrelated CLI processes must survive. Test explicit delete and shared-cache logout.
- Test 25-machine hook bursts and rate/concurrency rejection. Legacy v1 receipt
  capacity is 1,000,000 without eviction; at capacity new v1 events return 503 without
  refreshing liveness. Explicit machine removal resets that machine's replay history.
  Thousands of managed heartbeats must retain bounded per-machine generation/sequence
  watermarks, not append permanent receipt rows.
- Exercise actual Relay timing, credential expiry/revocation, sleep/reconnect and
  hosting beyond a token lifetime. Verify automatic resume with an existing CLI
  account, no extra consent gate, and no automatic login prompt. Stop/Delete/Sign out
  must persistently disable automatic resume; ordinary Exit must not.
- Publish self-contained x64 MSIs; verify Tunneling appears only in Dashboard,
  CLI remains an explicit prerequisite, and no login/install/cloud cleanup runs
  inside MSI custom actions. Test repair, upgrade, uninstall and rollback.

**Not yet release-verified:** the installed-UI and independent-network cases above,
long-duration authentication/reconnect behavior, and MSI acceptance. The approved
development-machine synthetic tunnel returned HTTPS GET 200 and POST 202, then was
stopped and deleted; it does not replace these checks. Obtain explicit anonymous
spoofing-risk and no-SLA suitability acceptance for release. Saved LAN mode stays
LAN; missing connection mode now defaults to Internet. Malformed settings must
recover with automatic sharing disabled.

Development verification on September 14, 2026 additionally exercised the real
controller with an isolated Kestrel store and actual Relay process through a
disposable public tunnel, meeting the 3-second hook budget and confirming deletion.
Before the default-sharing change, an isolated Release WinUI run enabled sharing through the consent-gated UI,
displayed the service-returned HTTPS URL with copying enabled, then stopped and
deleted it with copying disabled. Immediate deletion first encountered a stale
service host count; identity was retained and a later explicit retry confirmed
deletion. No other host was evicted. Test receivers/processes and local artifacts
were cleaned up. These are development-machine checks, not MSI or second-network
sign-off.

The default-sharing update was also verified in an isolated Release WinUI run:
no consent or automatic-start switch was present, a public HTTPS URL became
copyable automatically using the existing CLI account, and restart reused the same
tunnel. Stop persisted the off preference and a subsequent restart remained
stopped. The disposable tunnel was then deleted and test artifacts cleaned up.
Long-duration credentials and independent-network/MSI acceptance remain outstanding.

## State

- `sessionStart` -> Waiting; prompt/pre-tool/successful post-tool -> Executing;
  permission request -> Waiting; agent stop -> Succeeded; error/tool failure -> Failed;
  session end -> no active contribution, even during a result hold.
- For `toolName: "ask_user"`, pre-tool -> Waiting until the matching post-tool
  resumes Executing (or tool failure -> Failed). Unrelated tools must not clear
  the wait. Verify with a real CLI question left unanswered, then answer it.
  Verify Waiting survives dashboard restart and supersedes prior Succeeded and
  Failed overlays. Heartbeats preserve the wait without extending result-hold expiry.
- Verify Succeeded and Failed at 59 seconds and underlying state at 60 seconds.
- Run concurrent sessions and verify Waiting > Failed > Executing > Succeeded > Idle.
- End one session without affecting the other.
- At 64 tracked sessions, preserve active sessions and unexpired result holds.
  A new-session hook can evict only the oldest ended entry with an expired hold.
  Restart and verify stale hooks cannot resurrect it through the persisted
  retired-through timestamp.
- Load a legacy database with heartbeat as its latest event: show **No hook received**
  for that null migrated event while preserving machine identities and sessions.
- Leave Client running without Copilot activity beyond eleven minutes: five-minute
  heartbeats keep it online and preserve observed waits (Idle only with no active
  sessions). Disconnect B or kill only its exact test Client PID:
  online at 659 seconds and Offline at 660 since last accepted contact by default.
  Validate two intervals plus one minute at other supported intervals.
- With Client alive during a network outage, send hooks and reconnect without more
  hooks: the next current snapshot repairs delivery without extending expired
  results or clearing pending questions. Resume must not send a catch-up burst.
- Use Client Exit while Executing: accepted terminal offline immediately overrides
  activity. Lost/offline acknowledgement still permits bounded shutdown (normal
  Exit at most five seconds), with an honest diagnostic and timeout fallback.
  Inject delayed hooks/heartbeats, duplicate/stale sequences and old generations
  before/after Dashboard restart: none may revive a terminal run or renew contact.
- Run hooks after Exit: successful fail-open return, no HTTP fallback, restart,
  queued replay, or post-exit timer. Settings-only Apply, launch, and Test Connection
  do not resume an already-managed stopped Client. First Configurator v3 migration
  is a first-launch operation, unlike MSI task-only migration.
  Explicit Start Client or next sign-in begins a newer run,
  initially Idle until fresh hooks; events missed while stopped are unrecoverable.
  Test a Copilot session spanning restart separately from transient network loss.
- Keep a separate legacy v1-only fixture: Offline at five minutes without unique
  hooks and no snapshot recovery. After managed activation, v1 for that UUID returns
  409 persistently, even duplicates. Deliberate downgrade requires stopping Client,
  removing owned startup/hooks through normal Configurator actions, manually
  preserving display name/notes/mappings, and explicitly removing the computer in
  Dashboard before legacy setup. Removal deletes snapshots/replay history and local
  details/mappings; there is no automatic downgrade endpoint.
- On `/api/v1/status`, reject `heartbeat` events and legacy `state`/`sessions` fields with 400,
  without registering a machine or changing state/liveness.
- Exercise invalid managed per-kind fields and corrupt isolated generation/session
  state: visible bounded diagnostics, no acknowledgement before persistence, and no
  generation reset or malformed-request advancement of watermarks/liveness.
- Replay the same event UUID before and after restarting A: no state/liveness change.
- Deliver older reports after newer ones: no state regression or resurrected session.
- Rename/remove a computer, restart A, and verify persisted state and display names.

## UI and Windows lifecycle

- Validate interval default 5, whole-number range 1–60, rejected zero/fraction/outside
  bounds, persisted v3 seconds, unchanged UUID, and preview of exact deadline.
  Edit/Test without Apply leaves the timer unchanged. Accepted reload announces
  interval immediately; failed reload shows saved-but-not-applied with recovery.
  Effective revision/interval changes only after Dashboard acknowledgement; while
  pending, use the shorter old/new cadence. Exercise increases and decreases during
  an outage without claiming unacknowledged settings became effective.
- Verify one Client icon/timer per user/data directory across duplicate launches and Windows
  sessions; secondary launch cannot stop the owner. Test Explorer restart, sleep/resume,
  absent/incompatible/full/hung IPC and wrong-user denial.
  Reject junction/symlink config aliases and corrupt/nonpositive persisted generation
  state; diagnostics must identify cross-session ownership without resetting history.
  Updated Relay with v1/v2 configuration still has no direct HTTP fallback.
- Client Open Configurator uses co-installed trusted path/context and keeps reporting.
  Verify the Configurator checkbox creates/removes only its owned current-user
  Startup `.lnk`, targeting the adjacent Client with canonical background/config
  arguments. Startup occurs only at user sign-in, never pre-login; deliberate
  Exit retains registration for next sign-in. Repair preserves opt-out; owned Run
  migration avoids duplicate launches and preserves Windows Startup Apps disabled state.
- Resize through compact/wide layouts and verify square readable cards.
- Check high contrast, light/dark themes, keyboard navigation and screen-reader labels.
- Toggle always-on-top; restart and verify the choice persists.
- Minimize and close the window: tray remains and `/health` responds.
- Launch a second dashboard: the existing window is activated; no second host starts.
- Use tray Exit: port closes and SQLite flushes.
- Opt into startup, sign out/in and verify behavior; opt out and verify removal.
- Cancel firewall elevation: actionable feedback, no rule change.
- Add firewall rule: Private only. Remove rule: only Agent Signaler's rule is removed.
- On a Public network verify no Agent Signaler Public rule exists.

## Failure and deployment

- Stop A or use an unreachable endpoint; verify relay hook invocations exit normally
  within their strict timeout and never alter Copilot permission decisions.
- Change B's endpoint; preview, apply, verify read-only health, then send a hook to the new host.
- Repair remote MSI/configuration with existing sessions and unrelated hooks present.
- During MSI repair/upgrade and updater runs, verify bounded
  `Relay --stop-client-for-update` stops the exact owned current-user coordinator
  across Windows sessions before file replacement, never image-name killing.
  Old releases without Client skip the helper. No automatic Client restart follows;
  explicitly use Start Client. Ordinary install does not create startup until Apply.
- Upgrade Dashboard MSI first, then all three remote binaries in the stable Remote
  directory; verify the Start Client shortcut explicitly restarts after Exit.
  Hook paths remain stable and UUID is unchanged. After InstallFiles, verify MSI
  task-only `--migrate-legacy-heartbeat` / `--rollback-legacy-heartbeat` /
  `--commit-legacy-heartbeat` actions leave no owned legacy task on successful
  install/repair/update, even before Apply. Test upgrading a legacy pair without
  Client: migration requires neither Client, GUI nor cloud connectivity, changes no
  settings/hooks/startup, preserves foreign tasks, and restores exact owned task
  on rollback. Published-folder Apply independently retains safe owned-task cleanup.
- Force a failed remote configuration operation: previous hook/config/manifest,
  exact owned startup value and any removed ownership-verified legacy task restored;
  unrelated or modified entries remain untouched.
- Uninstall remote MSI with Client running: stop only the exact owned current-user
  coordinator using bounded IPC, never image-name kills. Failed offline notification
  must not block removal; unsafe stop/ownership failures require actionable errors.
  Owned startup/hooks and any verified legacy task removed or previous owned contents restored;
  unrelated files and retained identity/backups remain intact.
- Force MSI rollback: previous functioning components/integration remain consistent.
- Uninstall dashboard: no running server; inspect explicitly created firewall/startup
  settings according to installer documentation and remove any retained opt-in rules.
