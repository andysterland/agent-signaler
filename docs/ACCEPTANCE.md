# Release acceptance checks

Run against two disposable Windows 11 x64 computers, with a current Copilot CLI.
Do not run installer/firewall tests on a shared development machine.
Record the product, CLI and OS versions and outcomes with the release evidence.
The managed tray/heartbeat behavior is implemented. The checks below remain
outstanding manual acceptance, not assertions that installed/manual tests passed.

For local testing without a second computer, follow
[the single-machine manual test plan](MANUAL-TEST-PLAN.md). Record its results
separately; loopback tests do not establish LAN/VPN or remote firewall behavior.

## Automated implementation validation (September 15, 2026)

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

New Configurator previews write configuration v4 and require source-aware Dashboard
`GET /api/v3/health` returning exactly `{"protocolVersion":3,"status":"ok"}` and
`POST /api/v3/reports`. Retain the v1/v3 configuration tests below as compatibility checks; they do not certify
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
- Inspect network requests and local files using synthetic prompts only. Confirm
  they contain no prompt, source, tool arguments/output, transcript path or error text.
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
  session end -> Idle after any result hold.
- For `toolName: "ask_user"`, pre-tool -> Waiting until the matching post-tool
  resumes Executing (or tool failure -> Failed). Unrelated tools must not clear
  the wait. Verify with a real CLI question left unanswered, then answer it.
  Verify Waiting survives dashboard restart and supersedes a prior Succeeded
  overlay. Heartbeats preserve the wait without extending result-hold expiry.
- Verify Succeeded and Failed at 59 seconds and underlying state at 60 seconds.
- Run concurrent sessions and verify Failed > Waiting > Executing > Succeeded > Idle.
- End one session without affecting the other.
- At 64 tracked sessions, preserve active sessions and unexpired result holds.
  A new-session hook can evict only the oldest ended entry with an expired hold.
  Restart and verify stale hooks cannot resurrect it through the persisted
  retired-through timestamp.
- Load a legacy database with heartbeat as its latest event: show **No hook received**
  for that null migrated event while preserving machine identities and sessions.
- Leave Client running without Copilot activity beyond eleven minutes: five-minute
  heartbeats keep it online/Idle. Disconnect B or kill only its exact test Client PID:
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
  Verify exact owned HKCU Run registration starts Client only at user sign-in,
  never pre-login; deliberate Exit retains registration for next sign-in.
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
