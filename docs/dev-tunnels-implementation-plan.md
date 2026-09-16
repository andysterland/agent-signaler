# Dev Tunnels HTTPS implementation plan

## Goal and scope

Make anonymous Internet HTTPS sharing the default connection mode for Agent Signaler using Microsoft Dev Tunnels, with hosting owned by the dashboard. No additional sharing consent checkbox or enable switch is required. The preferred implementation is explicit dashboard-user sign-in through MSAL.NET, followed by authenticated tunnel management and in-process hosting through the Dev Tunnels C# SDK. Qualify this path first; retain the supported `devtunnel` CLI as an explicitly selected fallback only if the preferred path cannot be qualified.

- The dashboard user explicitly signs in with a Microsoft account through MSAL.NET before creating or managing a tunnel. MSAL supplies a Dev Tunnels management access token to the C# SDK; the SDK does not perform interactive sign-in itself.
- Remote Configurator and Relay remain anonymous: no account, interactive sign-in, access token, Dev Tunnels SDK, or Dev Tunnels CLI is required.
- Remote apps **do not create tunnels**. They consume the dashboard's public HTTPS URL.
- Preserve hook-only status reporting, machine identity, hook installation, and local dashboard state. There is no scheduled periodic heartbeat or snapshot reconciliation.
- Retain explicitly saved LAN/VPN mode for compatibility. New settings and settings without a connection mode default to Internet. Start sharing automatically using the existing signed-in account unless the user has stopped sharing.
- The CLI is an approved dashboard-only fallback, not a remote-machine dependency. Do not extract CLI credentials for use with the SDK.

This is a plan, not an implementation. It supplements `docs\implementation-plan.md`; its Internet-mode requirements intentionally replace that document's LAN-only assumptions for the new mode.

**Important trust boundary:** HTTPS encrypts transport; it does not authenticate status senders. Anyone able to reach the anonymous endpoint can submit or spoof reports and consume capacity. Anonymous access is an explicit product requirement, not a claim that Internet exposure is equivalent to the existing trusted-network deployment.

## Current implementation and required changes

Paths below are relative to the repository's `src` directory.

| Surface | Current behavior | Planned change |
| --- | --- | --- |
| `src\AgentSignaler.Dashboard\MainWindow.cs` | Owns Kestrel startup/shutdown; constructs an HTTP hostname URL; shows LAN-only warnings and firewall controls | Coordinate tunnel lifecycle, account controls, connection state, and mode-specific warnings; replace the displayed/copyable connection URL with the verified anonymous tunnel's public HTTPS base URL in Internet mode |
| `src\AgentSignaler.Dashboard\DashboardSettings.cs` | Persists port and appearance | Persist connection mode, resume preference, and non-secret tunnel identity; keep tokens out of JSON |
| `src\AgentSignaler.Service\DashboardServer.cs` | `ListenAnyIP(port)`; anonymous health and status endpoints | Explicit loopback binding for Internet mode; retain any-interface binding only for LAN mode; apply bounded Internet-facing request controls |
| `src\AgentSignaler.Remote\RemoteConfiguration.cs` | Configuration version 1; HTTP-only `Host`/`Port`; rejects HTTPS and port 443 | Add versioned HTTPS endpoint representation and compatible version-1 loading |
| `src\AgentSignaler.Remote\DashboardConnection.cs` | Bounded JSON `/health` check | Anonymous HTTPS health check with explicit JSON negotiation and tunnel interstitial handling |
| `src\AgentSignaler.Remote\RelayEngine.cs` | Posts to configured endpoint; bounded retries; 2.2-second total work budget | Preserve anonymous delivery and timing; use the new endpoint representation |
| `src\AgentSignaler.Configurator\MainWindow.xaml` and `.xaml.cs` | HTTP example and LAN-only warnings; short HTTP client timeouts | Accept copied HTTPS URL; explain anonymous trust model; improve connectivity diagnostics |
| `src\AgentSignaler.Relay\Program.cs` | Separate HTTP handler configuration, redirects/proxy disabled | Share deliberate HTTP transport policy with Configurator; never disable certificate validation |
| `src\AgentSignaler.Remote\IntegrationManager.cs` | Serializes configuration into preview/apply/rollback ownership workflow | Migrate endpoint settings only through this workflow, preserving its ownership hashes and backups |
| Existing test projects and installer publish trees | LAN/loopback coverage and self-contained x64 deployment | Extend coverage for HTTPS, migration, tunnel lifecycle, and the selected SDK/identity or dashboard CLI dependencies |

## Target architecture

```text
Dashboard user
  -> preferred: explicit Microsoft sign-in through MSAL.NET
  -> MSAL acquires a Dev Tunnels management access token for the signed-in account
  -> TunnelManagementClient userTokenCallback supplies the authentication header
  -> create/reuse a dedicated persistent tunnel
  -> TunnelRelayTunnelHost connects using a service-issued host-scoped token

Fallback only if the preferred route cannot be qualified:
  -> explicit CLI selection and devtunnel user login
  -> dashboard-managed devtunnel management and host commands

Remote Configurator / Relay (no account or token)
  -> https://<service-returned-host>/health or /api/v1/status
  -> Dev Tunnels HTTPS ingress (service-managed certificate)
  -> encrypted outbound relay connection hosted by SDK or owned CLI process
  -> http://localhost:<receiver-port>
  -> existing Kestrel endpoints and SQLite store
```

For the preferred SDK backend, use MSAL.NET (`Microsoft.Identity.Client`) for user sign-in and management-token acquisition, and `Microsoft.DevTunnels.Management` and `Microsoft.DevTunnels.Connections`, with compatible `Microsoft.DevTunnels.Contracts` types, for tunnel management and hosting. Pin verified published NuGet versions rather than assuming repository `main` matches a package release. Keep MSAL and SDK dependencies confined to Dashboard/Tunneling.

For the fallback CLI backend, let the CLI own authentication, management credentials, and the relay connection. Use the documented Windows installation and login flow. Do not require a separate Entra app registration for this route. Investigate and qualify MSAL + C# SDK first, recording any concrete blocker before choosing CLI hosting. Select and qualify one backend for the first release; do not build both unnecessarily or silently switch backends/accounts after an error. A documented SDK identity blocker does not prevent a successfully qualified, explicitly selected CLI fallback.

Recommended organization: add a small `AgentSignaler.Tunneling` class library for backend orchestration and testable interfaces, referenced only by Dashboard. Keep WinUI-specific interaction in Dashboard. Add `AgentSignaler.Tunneling.Tests`; do not introduce SDK, identity, or CLI dependencies into Relay, Configurator, Remote, Contracts, or the webhook service.

Suggested application-owned abstractions:

- `IDashboardIdentityProvider`: account status, explicit interactive sign-in, noninteractive readiness, and backend-appropriate sign-out. CLI implementations never return raw credentials to the app.
- `IDashboardTunnelHost`: create/reuse, connect, stop, delete, and immutable status snapshots.
- A narrow SDK management adapter or CLI process runner for deterministic tests; do not mock network behavior through WinUI.

These names are proposed application APIs, not SDK classes.

## Phase 0: qualify the hosting backend

**Blocking gate:** validate supported sign-in, lifecycle control, and anonymous HTTPS delivery for the selected backend before building the complete UX or enabling Internet mode. SDK-specific identity blockers can be avoided with the approved CLI route, but do not waive listener, process-lifetime, anonymous-access, or release acceptance gates.

### Preferred route: explicit MSAL sign-in + C# SDK

The SDK's `TunnelManagementClient` accepts a product/user-agent identifier and a `userTokenCallback` returning `AuthenticationHeaderValue`; it does not implement interactive sign-in. User management credentials and service-issued, tunnel-scoped host tokens are different credentials.

Use Microsoft/Entra sign-in through an MSAL.NET public-client application as the preferred identity design, with Windows Web Account Manager if supported and appropriate for unpackaged WinUI. Confirm the supported account types, app registration, delegated permissions, consent requirements, redirect configuration, and broker/browser behavior with the Dev Tunnels service. GitHub sign-in is not part of this MSAL path and is not an assumed additional first-release requirement.

The preferred user and token flow is:

1. The user selects **Sign in** in Dashboard. Configure the MSAL public client with the approved application client ID, authority, redirect URI, and Dev Tunnels delegated scopes; never embed a client secret.
2. On that explicit user action, use MSAL `AcquireTokenInteractive` to sign in and obtain a Dev Tunnels management access token. Bind the selected account to subsequent tunnel operations. Cancellation leaves the dashboard local and unshared; successful sign-in permits sharing according to the saved mode/start preference without another sharing consent prompt.
3. Supply the resulting access token through `TunnelManagementClient.userTokenCallback` as an `AuthenticationHeaderValue` using the SDK-supported Microsoft authentication scheme (`aad`, verified against the selected package/service). Do not pass an ID token, a Microsoft Graph token, or a refresh token. The SDK then performs authenticated create/get/update/delete operations.
4. For subsequent callbacks and approved background resume, use MSAL `AcquireTokenSilent` for the same account and scopes. Map `MsalUiRequiredException` to **Sign in to reconnect**; interactive acquisition is allowed only after another explicit user action, never inside a background SDK token callback.
5. Obtain the separate, service-issued host-scoped tunnel token through the management SDK and connect using `TunnelRelayTunnelHost`. Preserve SDK host-token renewal through the authenticated management client; MSAL renews the user management credential, not the tunnel-scoped host token.
6. Keep MSAL caching Windows-protected and app-scoped where supported, with test isolation. On sign-out, stop hosting before removing the app's cached account credentials and disabling resume; do not sign Windows out.

The [DeepWiki C# SDK overview](https://deepwiki.com/microsoft/dev-tunnels/2.1-c-sdk) describes the management token callback and the separate management/connection layers. Use it as architecture guidance and verify APIs against its linked source and the selected published packages. It does not establish an independent app's MSAL registration, delegated scope strings, or consent requirements; those must still be validated rather than inferred from tunnel access scopes.

Verified SDK source exposes the `aad` and `github` authentication schemes and a Dev Tunnels resource application ID. This does **not** establish the delegated OAuth scope or permission for a third-party registration. Do not substitute Microsoft Graph scopes, invent `/.default` or `user_impersonation`, copy a first-party client ID without documented permission, or require users to extract CLI tokens. The public authentication question in [issue #557](https://github.com/microsoft/dev-tunnels/issues/557) is relevant to this gate.

The proof of concept must establish:

1. A clean Windows user can explicitly sign in and create a tunnel using supported application credentials; a desktop client has no embedded client secret.
2. Silent credential refresh works, and revoked consent/expired sessions produce an actionable sign-in-required state.
3. The selected published packages work with this project's .NET 10, WinUI, self-contained x64 build and installer.
4. A dashboard-hosted local HTTP endpoint is reachable from a different Internet connection using valid public HTTPS and no client credentials.
5. Tenant policy, provider restrictions, and cancellation are reported clearly. An unsupported SDK identity flow blocks that backend. Select the CLI route explicitly instead; never silently change authentication providers or attempt anonymous tunnel creation.

Use a disposable test tunnel and remove it after the experiment. Record the approved registration/scopes, package versions, and provider limitations in implementation documentation; never record tokens.

### CLI route (approved fallback when SDK identity is blocked)

Microsoft documents Windows installation with `winget install Microsoft.devtunnel`, or an official x64 download, and login with `devtunnel user login`. Microsoft organizational/personal accounts are supported; `-g` selects GitHub and `-d` selects device-code login. Hosting still requires dashboard-user authentication; only client access is anonymous.

1. Treat the CLI as an explicitly installed dashboard prerequisite initially. Provide the official installation link and actionable missing/unsupported-version diagnostics; do not silently download or execute a binary. Record a tested version/support range using `devtunnel --version`. Recheck after CLI upgrades because the CLI is preview and commands/output can change. Bundling requires a separate redistribution, servicing, and provenance decision.
2. Resolve and validate a trusted absolute executable path; do not execute a `devtunnel.exe` from the current working directory merely because it shadows PATH. Use `ProcessStartInfo.ArgumentList` with no shell interpolation. Validate tunnel IDs and ports, always pass explicit resource IDs after creation, and never rely on the CLI's shared last-used tunnel.
3. Prove explicit `devtunnel user login`, cancellation, `devtunnel user show`, cached-login reuse, expiry/revocation, and actionable reauthentication on a clean Windows account. Do not assume the CLI has MSAL-like silent-refresh APIs or app-scoped cache isolation. Automatic startup checks the existing account and uses bounded noninteractive commands; missing credentials must fail visibly, never invoke login. Longer-lived expiry/revocation behavior remains a release test.
4. The CLI documentation says login credentials are cached in the system secure key chain and `devtunnel user logout` clears that cache, not browser cookies. Treat it as potentially shared with other CLI use by the same Windows user. Verify any supported isolation mechanism before claiming app-scoped sign-out or isolated credentials in tests. Never read, copy, export, or serialize CLI tokens, and do not invoke `devtunnel token` or pass `--access-token`.
5. Qualify persistent create/show, port and ACL inspection, host, and delete commands against the installed version's help. Prefer documented structured output if that version provides it; do not assume a JSON switch. Otherwise implement bounded, version-tested text parsers with fixtures. Unknown or ambiguous output must fail visibly, not invent an ID, URL, account, successful deletion, or safe ACL. If required ACL/account verification is unavailable, CLI Internet enablement remains blocked.
6. Prove an owned long-running host process can be started, monitored, stopped, and cleaned up from unpackaged WinUI, including app crashes, shutdown during login/create, and descendants. Verify public URL extraction and real anonymous HTTPS health/status delivery from another Internet connection, without a CLI or credentials on the remote machine.
7. Record the tested CLI version, executable provenance, command/output contracts, account/cache limitations, and cleanup procedure. Delete disposable tunnels explicitly. CLI selection removes the independent-app OAuth registration prerequisite, not the need for this live proof.

#### CLI qualification evidence (September 14, 2026; release acceptance incomplete)

- The owner selected CLI fallback qualification because independent-app MSAL registration guidance was unavailable, and explicitly approved installation through WinGet.
- Installed `Microsoft.devtunnel` version `1.0.2030+fc9273aa0f` from the WinGet source. WinGet verified the installer hash; Windows Authenticode validation returned `Valid`, signed by Microsoft Corporation. The per-user executable alias is `%LOCALAPPDATA%\Microsoft\WinGet\Links\devtunnel.exe`.
- Installed command help confirms `--json` for `user login`, `user show`, `create`, `show`, `access list`, `access create`, `port show`, and `delete`. This establishes option availability, not the actual response schemas.
- `access create` supports explicit `--port-number` and `--scopes`; use `--scopes connect` rather than relying on its documented default. `host` accepts an explicit persistent tunnel ID but advertises no JSON option, so readiness/URL output still requires a qualified parser or a verified management-query alternative.
- Login help exposes browser, device-code, Microsoft/Entra, and GitHub options. Inspected help does not establish a noninteractive-only hosting option, cache isolation, renewal behavior, or process containment.
- After the owner completed CLI sign-in independently, `user show --json` returned `status: "Logged in"` and `provider: "microsoft"` with username/tenant/object identity fields. No credentials were extracted. The earlier tool-launched browser login required a parent window handle and fell back to device-code authentication; it was cancelled at the owner's request.
- With explicit owner approval, a disposable persistent tunnel was created with a one-hour expiration and exactly one HTTP port forwarding a synthetic loopback fixture. Tunnel ACLs were empty; port ACLs were exactly `Anonymous`, empty subjects, and scope `connect`. `show`, `port show`, and `access list` JSON contracts were inspected.
- Actual host output in this version is `Hosting port: PORT`, followed by `Connect via browser: HTTPS_URL`, a separate inspection URL line, and `Ready to accept connections for tunnel: FULL_ID`. The public host can differ from the custom tunnel ID. Do not use the older documentation's single-line output example as the only parser fixture.
- An ordinary TLS-validating HTTP client with redirects/cookies disabled and `Accept: application/json` received public HTTPS `/health` 200 with protocol 1 JSON and synthetic `/api/v1/status` 202, without authentication headers. This was from the development machine, not an independent Internet connection or a real Relay process.
- The synthetic receiver and CLI host were stopped. `delete FULL_ID --force --json` returned `deletedTunnel: FULL_ID`; a subsequent lookup reported not found with exit code 2. The disposable resource was deleted. No persistent public exposure remains from qualification.
- Noninteractive refresh, account-cache isolation, long-lived reconnect/token renewal, independent-network delivery, and installed WinUI lifecycle acceptance remain release gates. Do not claim this short synthetic probe establishes them.

## Phase 1: implement tunnel ownership and lifecycle

### Create or resume: SDK backend

1. Use the saved connection mode and automatic-start preference, defaulting to Internet sharing without an additional consent gate. Start and verify the local Kestrel receiver bound to loopback before exposing it.
2. Acquire the explicitly signed-in dashboard user's Dev Tunnels management access token through the MSAL-backed identity provider, using silent acquisition for the selected account. If interaction is required, request an explicit sign-in action rather than prompting from the SDK callback.
3. Create `TunnelManagementClient` using the credential callback. Keep service configuration in the SDK; do not hard-code a region or compose relay endpoints.
4. If saved tunnel and cluster IDs exist, call `GetTunnelAsync` with `IncludePorts = true` and `TokenScopes` containing `TunnelAccessScopes.Host`. Distinguish a missing tunnel from forbidden access, network failure, and quota failure.
5. Create a dedicated application-owned tunnel with `CreateTunnelAsync` when no tunnel exists. Persist returned identifiers immediately so partial failures can be recovered without leaking duplicate resources.
6. Configure exactly one receiver port with `TunnelPort.PortNumber` and `Protocol = TunnelProtocol.Http`. The standard host forwards to localhost at the same port number. Do not configure local HTTPS merely because the public URL is HTTPS.
7. Set a **port-level** `TunnelAccessControl` with an allow entry: `Type = TunnelAccessControlEntryType.Anonymous`, `Scopes = [TunnelAccessScopes.Connect]`, empty subjects, no provider, and neither deny nor inverse flags. Never grant anonymous host, manage, or manage-ports scopes.
8. Refetch and verify effective port/protocol/ACL and host token before connecting. Tunnel-level permissions are inherited; detect unexpected broad anonymous permissions, deny rules, and extra ports. Reconcile only resources proven to belong to this app; fail visibly rather than rewriting unrelated user tunnels.
9. Construct `TunnelRelayTunnelHost` with the management client, subscribe to connection state, and call `ConnectAsync`. Do not use obsolete `StartAsync`.
10. Obtain the target port's `PortForwardingUris` from the service, refetching if needed. Select and validate an absolute HTTPS base URI. Do not synthesize `<id>-<port>.devtunnels.ms`, use an inspection URL, or present `HostRelayUri` to clients.
11. Probe the returned HTTPS `/health` anonymously and validate its JSON before marking the public connection verified. If the relay connects but that probe fails, report the distinction and allow an explicit retry; never substitute an HTTP URL.

Serialize start/stop/reconfigure operations and cancel outstanding work on exit. Protect against double-clicks, stale callbacks, and initialization completing after shutdown.

### Create or resume: CLI backend

Apply the same connection-mode/start-preference, loopback-first, app-ownership, single-port, least-privilege ACL, and verified-HTTPS requirements as the SDK backend:

1. Verify the local receiver and CLI availability/version/account readiness before creating or hosting anything. Launch login only from an explicit user action.
2. Create a dedicated persistent tunnel with `devtunnel create`, without tunnel-wide `--allow-anonymous`. Prefer a unique app-generated custom ID supported by the qualified version, recording pending-create intent before invocation. Persist the returned resource identity immediately. A cancelled/timed-out create may have succeeded remotely: inspect the exact intended resource and verify ownership before retrying; never blindly generate another tunnel or adopt a conflicting resource.
3. Use explicit IDs with `devtunnel show TUNNELID` and all port/access/host/delete commands. Persist the full service-returned identity needed for unambiguous lookup, including cluster information where required. Do not use `devtunnel set`, `unset`, or `delete-all`; create/host may themselves affect the CLI's last-used state, so document that shared-state impact.
4. Configure exactly one local HTTP receiver port using the qualified syntax for `devtunnel port create TUNNELID -p PORT --protocol http`. Grant anonymous client access only on that port with the documented `devtunnel access create TUNNELID --port PORT --anonymous`. Verify the resulting ACL grants only connect, with no anonymous host/manage/manage-ports access, inherited broad access, deny drift, or extra ports. Inspect both tunnel and port ACLs using the qualified CLI commands; do not treat exit code zero as proof of correct effective permissions.
5. Start `devtunnel host TUNNELID` as the owned long-running process. Do not use the quickstart's ID-less temporary hosting command in the product: persistent resources are required for URL reuse and deterministic cleanup.
6. Read stdout and stderr concurrently into bounded buffers. Extract only the selected receiver port's actual HTTPS web-forwarding URL, rejecting inspection and relay URLs. Validate it as an absolute root HTTPS URL using the same endpoint rules as remote clients; never synthesize a hostname. Treat CLI output as untrusted data, not commands or markup.
7. Distinguish process-running, relay-ready, and publicly verified states. Probe anonymous JSON `/health` before showing a connected URL. Unsupported output, process exit, authentication failure, quota failure, and health failure must have visible bounded diagnostics. A running process or printed URL alone is not proof of connectivity.
8. Serialize all lifecycle operations and correlate callbacks with the active process/generation. Bound short-lived command execution, output size, cancellation, and shutdown. Use a Windows Job Object or equivalent verified containment so only this dashboard's child processes and descendants are terminated on stop/crash; establish containment before hosting can begin. Prefer verified graceful termination, then bounded termination of the exact owned process tree. Never kill by executable name or stop unrelated CLI sessions.

The CLI manages credentials and relay reconnection internally. Do not combine it with SDK token extraction or an independent SDK relay host.

### State and recovery

Expose separate local-receiver and tunnel state. Suggested tunnel states: `Disabled`, `SignInRequired`, `Creating`, `Connecting`, `Connected`, `Reconnecting`, `Stopped`, and `Error`.

Use the selected backend's retry/reconnect behavior before adding an application retry policy. For the SDK, observe connection-status events; for the CLI, qualify available readiness/reconnection output and process-exit behavior rather than inventing equivalent events. Do not start a competing host while the existing backend is reconnecting. After terminal failure, offer a bounded restart/retry with backoff and a clear failure category.

For the preferred SDK backend, the MSAL-backed identity provider uses `AcquireTokenSilent` to renew the user management credential and surfaces `MsalUiRequiredException` as sign-in required. The connection SDK can refresh host-scoped tokens using its management client; preserve its default refresh behavior unless there is a tested reason to override `RefreshingTunnelAccessToken`. For the CLI backend, verify credential/connection renewal through supported CLI behavior without accessing tokens. Do not assume tokens are permanent or that an already-issued management tunnel token can refresh itself.

On startup, including background startup, automatically share in Internet mode using the existing account unless the saved automatic-start preference is false. No sharing checkbox or extra consent is required. Never invoke login from startup or a background token callback. If authentication is unavailable, retain the local receiver and surface an actionable sign-in/retry error rather than hiding a failed background start. Explicit Stop, Delete, and Sign out persistently disable automatic startup; ordinary Exit does not. Corrupt settings recover with automatic sharing disabled.

Reuse the saved tunnel to preserve the URL across ordinary restarts. A deleted or expired tunnel may require recreation and a new URL: notify the user that remote settings must be updated. Do not recreate on every transient lookup failure. A conflict with another host must not cause the dashboard to evict it or silently create repeated replacement tunnels.

### Stop, sign-out, delete, and exit

| Action | Behavior |
| --- | --- |
| Close/minimize to tray | Continue hosting, as the current receiver does |
| Stop sharing | Disable automatic startup durably, dispose the SDK relay host or stop the exact owned CLI host process tree; keep tunnel identity for reuse; mark URL offline |
| Sign out | Stop hosting and disable automatic resume first. SDK: clear app credentials. CLI: warn that logout may affect other CLI sessions, then invoke `devtunnel user logout` only with explicit confirmation. Do not sign Windows out or claim browser cookies are cleared |
| Delete tunnel | Confirm impact, disable automatic startup, stop host, call `DeleteTunnelAsync` or `devtunnel delete TUNNELID` using the selected backend; clear saved identity only after confirmed deletion/absence |
| Exit | Cancel initialization/reconnect and management commands, dispose the SDK host before its management client or terminate owned CLI processes, then stop Kestrel and release the store using bounded shutdown |

Stopping hosting is not cloud resource deletion. Offline deletion failures must remain visible and retain enough non-secret identity for later cleanup. Account switching must never blindly reuse a previous account's tunnel. Store a minimal non-secret owner/backend binding with each resource; if the CLI cannot establish account continuity, require explicit revalidation and disable automatic reuse. Ownership markers alone are not authorization.

## Phase 2: settings, UI, and listener boundaries

Persist `Lan`/`DevTunnel` connection mode and `AutoStartSharing`. New or missing fields default to `DevTunnel` and `true`; an explicit saved `Lan` or `AutoStartSharing: false` is preserved. There is no separate sharing consent checkbox or automatic-start switch. Enable sharing / Retry restores automatic startup; Stop sharing disables it, even across restarts.

Persist only non-secret resource IDs, minimal owner/backend binding, port, selected mode, resume preference, and validated CLI path/version metadata when applicable. SDK user/refresh/host tokens remain in memory or the identity library's supported Windows-protected, per-user cache; CLI credentials remain exclusively in its supported cache. Do not serialize SDK `Tunnel` objects wholesale: they can contain access tokens. Respect isolated data directories in tests. Use a process-runner fake in ordinary CLI tests and a disposable Windows account for live tests if CLI credential-cache isolation is unproven.

Dashboard settings should provide sign in/account status, enable/stop sharing, retry, sign out, and confirmed tunnel deletion. In CLI mode, show prerequisite/version status and explain shared CLI login/logout effects before user action. Show the local port separately from the public HTTPS URL.

### Displayed URL and clipboard contract

- In Internet mode, replace the existing hostname/local HTTP connection URL in the dashboard's connection display and **Copy URL** action with the anonymous tunnel's verified public HTTPS base URL. Source it from the selected port's service-returned `PortForwardingUris` (SDK), or the qualified CLI's web-forwarding output. Display and clipboard must use the same effective connection snapshot.
- Copy only the canonical root HTTPS URL that Remote Configurator accepts as `DashboardBaseUrl`, with no credentials, tokens, query, fragment, `/health`, or `/api/v1/status` suffix. Never copy the loopback listener, LAN hostname, inspection URL, relay URL, or a synthesized tunnel hostname in Internet mode.
- Enable **Copy URL** only after anonymous-connect-only access has been verified and the public anonymous JSON health probe succeeds. While starting, unverified, reconnecting, stopped, signed out, deleted, or failed, disable copying and label any retained public address as unavailable. Do not substitute a LAN URL when Internet hosting fails.
- When recreation changes the public URL, atomically update the displayed/copyable URL after verification and notify the user to update remote configurations. Ignore stale callbacks from previous tunnels/accounts.
- Preserve the existing LAN URL and copy behavior in effective LAN mode. If a saved mode/port change requires restart, keep display and clipboard aligned with the effective running mode rather than the pending settings.

Internet-mode warning: "HTTPS encrypts reports, but clients are anonymous. Anyone who can reach this URL can submit status. Stop sharing to disable access."

Keep the LAN warning for LAN mode. Disable adding an inbound firewall rule in Internet mode; outbound Dev Tunnels connections need no inbound opening, router port forwarding, local certificate installation, or URL ACL. Permit explicit removal of an old application-owned rule without silently elevating.

Change `DashboardServer` to take an explicit listener mode/options value. Internet mode binds loopback only; LAN mode preserves current binding. Verify IPv4/IPv6 localhost behavior with the selected SDK or CLI host. A port/mode change can initially require restart, matching current UX; until then, display the effective running configuration, not just saved settings.

Do not expose account, management, database browsing, or administrative HTTP endpoints through the tunnel. Keep the existing health and status API only.

## Phase 3: anonymous HTTPS remote clients

### Configuration migration

Introduce remote configuration version 2 with a canonical absolute `DashboardBaseUrl`, while keeping machine identity and client metadata unchanged. Load version 1 by mapping its `Host` and `Port` to the original HTTP URL in memory. Save version 2 only when the user applies an approved configuration preview.

Centralize validation and endpoint construction:

- HTTPS accepts its normal default port 443 and valid explicit TCP ports; do not apply the local listener's non-privileged-port restriction to a public HTTPS URL.
- Continue accepting existing HTTP LAN URLs with their current port restrictions.
- Require a root/base URL without credentials, non-root path, query, fragment, backslashes, or control characters; preserve IPv6 handling.
- Construct `/health` and `/api/v1/status` from the validated base URL without changing its scheme or authority.
- Reject unsupported configuration versions and ambiguous mixed legacy/new fields. Keep the wire protocol version at 1; configuration versioning is separate.

Update preview, apply, repair, rollback, and ownership hashing through `IntegrationManager`, rather than rewriting `remote.json` on load. Preserve machine UUID, session state, and hook paths. Approved apply removes an ownership-verified legacy heartbeat task with timestamped XML backup and rollback restoration; new installations create no task. Refuse to overwrite or delete modified/unowned tasks. Existing remote binaries cannot consume version 2: upgrade Configurator and Relay together before switching to HTTPS. Rollback to an older release requires restoring the backed-up version-1 configuration and matching integration backups.

### HTTP behavior

Use a shared remote HTTP transport factory/policy instead of drifting implementations in Configurator and Relay.

- Send ordinary HTTPS requests without `Authorization`, `X-Tunnel-Authorization`, user cookies, or tunnel access tokens.
- Set `Accept: application/json`. Dev Tunnels' anti-phishing interstitial is skipped when Accept does not contain `text/html`; an explicit `X-Tunnel-Skip-AntiPhishing-Page` header is another documented option, not authentication.
- Keep normal TLS certificate and hostname validation. Reject redirects rather than following login pages or downgrading to HTTP.
- Preserve `/health` response size and schema validation. HTML, malformed JSON, login redirects, and incompatible protocol versions are failures, not successful connectivity.
- Distinguish unauthorized/forbidden tunnel access, stopped tunnel, DNS/TLS/proxy failure, rate limiting, and incompatible endpoint in Configurator. Never launch sign-in on a remote machine.
- Review existing `UseProxy = false` deliberately: retain LAN behavior; support the system proxy for Internet mode where required, with bounded discovery/connect time and no credential prompt in a hook.
- Keep the Relay's 2.2-second total budget and 3-second hook timeout. Measure DNS, TLS, and relay latency within that budget; do not simply lengthen hook timeouts to accommodate Internet requests. A longer, cancellable interactive Configurator test can be configured separately.
- Preserve transient-only bounded retries and respect `Retry-After` only when it fits the remaining budget. Never retry authentication/validation failures indefinitely.

Keep hook reports best-effort, with no periodic heartbeat, snapshot reconciliation,
or automatic repair of missed delivery after reconnect. Do not introduce an
unbounded offline queue or send raw hook data. Five-minute Offline detection uses
received unique hooks, not health checks or duplicates: quiet active CLI sessions
can go Offline, and the next accepted unique hook restores online status. The
relay `heartbeat` command is removed. Relay `test` uses the existing
`DashboardConnection.TestAsync` read-only `GET /health`, with no status POST,
machine registration, session-state mutation, or liveness refresh.

## Phase 4: bounded public endpoint and service constraints

Preserve current 32 KiB body limit, content-type/protocol validation, request timeouts, 100-connection limit, machine/session limits, safe errors, and privacy restrictions.

Before enabling Internet mode, add a bounded global request-rate/concurrency policy with an explicit `429` response and no unbounded request queue. Select and document limits using a burst test with 25 machines and concurrent hooks. Machine IDs are caller supplied and cannot be trusted as an abuse-control identity; forwarded IP headers are likewise not a safe identity without a verified proxy trust configuration.

Account for SQLite receipt-index growth under sustained valid anonymous traffic. Define and test a bounded retention/storage policy, including replay/liveness semantics, before changing deduplication behavior. Do not silently discard persistence errors or claim rate limits eliminate spoofing and resource exhaustion.

Use bounded diagnostic categories and backend tracing that excludes credentials, authorization headers, raw requests, and private hook data. Do not persist raw CLI stdout/stderr or enable verbose CLI logging by default; allowlist sanitized diagnostics and display device codes only transiently in explicit sign-in UI, never logs. Keep `/health` minimal. Do not enable a permissive forwarded-header policy or unnecessary CORS.

Dev Tunnels introduces an external Microsoft-hosted service dependency even though the user need not provision an Azure resource. Microsoft documents it for development/testing rather than production, with no SLA. Treat that as an explicit suitability decision before release, particularly if Agent Signaler is expected to be an always-available monitoring system.

Verify current quotas and organizational outbound-domain policy during implementation. Handle tunnel count limits, bandwidth/request throttling, and inactivity expiration. Current documentation describes a 30-day default inactivity expiry and approximately 24-hour tunnel tokens; these are service policies to recheck, not permanent application guarantees.

## Phase 5: verification and release

### Automated coverage

| Area | Required cases |
| --- | --- |
| Identity/tunnel unit tests | No creation before sign-in; cancellation; authentication renewal; interaction required; exact anonymous-connect-only port ACL; extra ports/ACL drift; partial-create recovery; missing vs forbidden; missing HTTPS URI; reconnect; conflicting host; stop/delete/account switch; shutdown races; SDK-specific host-token/refresh tests when that backend is selected |
| Preferred MSAL + SDK tests | Interactive acquisition only from explicit user action; startup respects the saved sharing preference without an additional consent gate; management callback uses the selected account/scopes and access token; silent acquisition on subsequent callbacks; UI-required state without background prompts; separate user/host-token renewal; protected cache isolation and app-scoped sign-out |
| Dashboard URL/copy tests | Verified anonymous public HTTPS base URL is identical in display and clipboard and accepted by Remote Configurator; never copy local/inspection/relay/token-bearing URLs in Internet mode; copying disabled for every unavailable state; replacement URL updates atomically; stale callbacks ignored; effective LAN and restart-pending behavior retained |
| CLI backend tests (when selected) | Missing/untrusted executable; supported/unsupported versions; argument safety; explicit resource IDs; bounded stdout/stderr and command timeout; malformed/version-changed output; create success followed by cancellation before persistence; shared login/logout consent; no background prompts; account mismatch; URL/port selection excluding inspection URLs; process exit/reconnect; graceful stop and forced owned-tree cleanup; crash containment; no unrelated-process termination; no token extraction |
| Remote configuration tests | Existing v1 files; v2 round trip; HTTPS default 443; explicit ports; IPv4/IPv6; invalid schemes/credentials/path/query/fragment; mixed-schema rejection; unchanged UUID; exact serialized field expectations |
| HTTP tests | Anonymous JSON GET and POST; no auth/cookies; interstitial/HTML and redirects rejected; TLS failure; no downgrade; 401/403/429/5xx; proxy behavior; retry timing and cancellation |
| Service tests | Loopback-only Internet binding; retained LAN binding; existing status semantics; rate/concurrency boundaries; bounded storage policy; body/timeout/error privacy |
| Integration tests | Actual Relay executable to Kestrel for LAN regression; HTTPS delivery through an isolated trusted test fixture; configuration apply/rollback ownership and legacy-task removal; hook timing, missed-delivery limitations, read-only health tests, removed heartbeat/snapshot rejection, and identity preservation |

Extend `tests\AgentSignaler.Remote.Tests\RemoteTests.cs`, `tests\AgentSignaler.Service.Tests\WebhookTests.cs`, and `tests\AgentSignaler.Integration.Tests\RelayToDashboardTests.cs`. Keep ordinary CI independent of accounts and the Dev Tunnels cloud. Mock HTTP handlers alone do not prove TLS or live tunnel functionality.

Run the solution build and existing test projects, plus the new tunneling tests, from the solution directory. Use the repository's current x64 build settings. Live tests are a separate opt-in suite with disposable resources and guaranteed cleanup.

### Manual release acceptance

1. On a clean dashboard account, qualify the selected backend (including explicit CLI prerequisite installation/version checks if applicable), cancel sign-in, then sign in successfully and enable anonymous sharing. Confirm the displayed URL is a real, certificate-valid HTTPS endpoint.
2. Select **Copy URL** in Dashboard and confirm the clipboard exactly matches the displayed anonymous tunnel's public HTTPS base URL, not its local listener or an inspection URL. Paste it unchanged into Remote Configurator on another Internet connection with no LAN/VPN route and no Dev Tunnels account/session/cookies; run both `/health` and actual Relay reports successfully. Do not install the CLI on the remote machine; it is permitted only on the dashboard for the CLI backend. Verify copying is disabled while sharing is unavailable and that a recreated tunnel updates the copy target only after verification.
3. Confirm GET returns compatible JSON, POST returns 202, the dashboard updates, and captures show no authentication headers or sensitive hook payload fields.
4. Confirm the local HTTP port is unreachable from another LAN machine in Internet mode and no inbound firewall rule is necessary.
5. Exercise all hook states, 25-machine bursts, concurrent sessions, result expiry, and five-minute offline behavior including quiet active CLI sessions. Verify no automatic missed-delivery repair, next-hook online recovery, read-only test mode, and rejection of heartbeat events/snapshot fields. Verify Relay retains its internal 2.2-second budget and does not overrun the configured 3-second hook timeout in the supported environment.
6. Minimize to tray; restart with verified noninteractive authentication; disconnect/reconnect the network; sleep/resume; exercise token expiry/revocation and hosting beyond a token lifetime. CLI mode must not open a background login prompt; test supported-version updates and actionable unsupported-version failure.
7. Stop sharing, sign out, exit, and delete the tunnel; verify anonymous delivery stops for each applicable action. Test expired/deleted resources, changed URLs, account switching, and another active host. In CLI mode, also crash the dashboard and verify owned host descendants exit while unrelated CLI sessions remain running; confirm the shared-cache logout warning and actual effects.
8. Upgrade an existing LAN installation without changing its endpoint, network mode, or identity. Then upgrade the remote pair and explicitly migrate to HTTPS; verify backups and rollback.
9. Publish self-contained dashboard and remote MSIs. Verify SDK/identity dependencies, if selected, are present only in the dashboard payload and WinUI resources remain intact. In CLI mode, test explicit dashboard prerequisite detection/install guidance and missing/removed/updated CLI behavior; never require the CLI for LAN mode, MSI servicing, or remote clients. Verify install/repair/upgrade/uninstall for both backend-specific prerequisites and existing LAN deployments.

Do not add interactive sign-in, implicit CLI installation, or required cloud deletion to MSI custom actions. Document explicit tunnel/account cleanup before uninstall and the offline cleanup path; preserve current servicing rollback behavior. SDK cache removal is app-specific and separate from Windows credentials. In CLI mode, do not remove a shared CLI installation or credential cache on uninstall; explain confirmed `devtunnel delete TUNNELID` and optional `devtunnel user logout`, including effects on other CLI users/sessions in that Windows account.

### Documentation and rollout

Update `README.md`, `docs\MANUAL-TEST-PLAN.md`, `docs\ACCEPTANCE.md`, and `installers\README.md` in the solution tree when implementing. Update the original planning document's security/scope statements to distinguish LAN mode from the opt-in anonymous HTTPS mode.

Ship endpoint/configuration support before asking users to migrate. Retain the selected backend's identity/lifecycle verification and listener isolation. Display the anonymous-access/no-SLA warning without an extra consent gate. Document whether the release uses SDK hosting or a dashboard-only CLI prerequisite; do not describe the CLI route as in-process or dependency-free. A failed tunnel connection must not silently fall back to another backend or an Internet-exposed HTTP listener.

## Definition of done

The dashboard can sign in, create or resume its own tunnel through the qualified C# SDK or dashboard-managed CLI backend, publish a verified Internet-accessible HTTPS URL, and reliably stop hosting. A remote user can paste that URL and deliver status without any sign-in, tokens, tunnel creation, SDK, or CLI installation. Explicitly configured LAN installations and hook timing/privacy guarantees remain intact. Documentation clearly describes the selected backend and its prerequisites, anonymous spoofing risk, cloud dependency, expiry, recovery, credential-cache behavior, and cleanup.

## Implementation checkpoint (September 14, 2026)

The selected implementation is the dashboard-only CLI fallback; MSAL + SDK remains
the preferred future backend pending approved registration guidance. Remote v2
HTTPS configuration/migration was already present. Implementation now includes
the Tunneling library and deterministic tests, explicit Internet listener mode,
bounded global admission controls, bounded fail-closed receipt storage, persisted
non-secret tunnel identity, opt-in dashboard controls, and a single effective
public display/clipboard URL.

The receiver permits a 200-request initial burst, replenishes 100 requests/second,
and bounds active requests at 100 with no queue. Receipt history is capped at
1,000,000 entries, with no eviction: new IDs fail with 503 at capacity without
changing liveness; explicit local computer removal releases its history and resets
that computer's replay protection. Existing over-limit stores are retained.

CLI sign-in is explicit and its cache remains CLI-owned. The owner's subsequent
default-sharing requirement enables automatic startup with an existing account
without an extra consent checkbox. Stop/Delete/Sign out disable future startup,
and missing credentials fail visibly without invoking login. The
development Release WinUI binary has been launched with isolated data: Internet
mode bound both loopback families, health succeeded, copying was disabled before
sharing, the earlier consent-gated enabling flow, and a disabled inbound firewall rule.
This is not installed-MSI or independent-network acceptance.

An opt-in integration test exercises actual Relay delivery through the controller
and a disposable real tunnel, with explicit cleanup and retained recovery identity
on cleanup failure. This test passed with the actual Relay within its 3-second
hook budget and confirmed resource deletion. A subsequent isolated Release WinUI
run exercised the earlier consent flow, enable sharing, verified public URL/copy readiness, stop,
and confirmed deletion. A temporarily stale cloud host count blocked immediate
deletion safely; retry after convergence succeeded without evicting a host.
Copying was disabled again after stop/delete. All disposable resources were removed.

Public health monitoring runs every 20 seconds with a 15-second deadline, disabling
copying on detected failure and re-verifying before recovery; detection is not
instantaneous. It does not start a competing host or change CLI credentials.
Ordinary tests do not require accounts or the cloud. Release
sign-off still requires the independent-network, long-duration, account-revocation,
sleep/reconnect, accessibility, and installer checks above.

The subsequent default-sharing update was verified in an isolated Release WinUI
run: settings containing only a local port selected Internet automatically, created
and verified a public tunnel without any consent/enable switch, and reused the
same resource after process restart. Stop persisted `AutoStartSharing: false`;
a further restart stayed offline. Confirmed deletion cleared the disposable
resource and local test artifacts were removed. Targeted settings/account/resume
tests and the Dashboard build passed. This does not establish long-duration
credential renewal or independent-network release acceptance.

## Sources and verification notes

Reviewed September 14, 2026. SDK source inspected at commit `16d8ed6e3c0131a25362d537e12fe3293e96c80f`; confirm equivalent APIs in the selected published packages. Independent-app identity registration/scopes remain a Phase 0 validation item for the SDK route, not a solved prerequisite. The project owner approved a CLI fallback on September 14, 2026. Microsoft Learn documents CLI installation, supported account login, persistent hosting, and port-scoped anonymous access; exact automation/output contracts, cache isolation, crash cleanup, and live acceptance remain to be qualified rather than assumed.

- [Windows CLI installation and first tunnel](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/get-started?tabs=windows)
- [CLI commands: login/cache, persistent tunnels, ports, access, and hosting](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/cli-commands)
- [Dev Tunnels SDK and feature matrix](https://github.com/microsoft/dev-tunnels)
- [DeepWiki C# SDK architecture and authentication callback overview (secondary source)](https://deepwiki.com/microsoft/dev-tunnels/2.1-c-sdk)
- [Management API contracts](https://github.com/microsoft/dev-tunnels/blob/16d8ed6e3c0131a25362d537e12fe3293e96c80f/cs/src/Management/ITunnelManagementClient.cs)
- [Management client authentication callback](https://github.com/microsoft/dev-tunnels/blob/16d8ed6e3c0131a25362d537e12fe3293e96c80f/cs/src/Management/TunnelManagementClient.cs)
- [Port forwarding URI and port ACL contracts](https://github.com/microsoft/dev-tunnels/blob/16d8ed6e3c0131a25362d537e12fe3293e96c80f/cs/src/Contracts/TunnelPort.cs)
- [Anonymous access-control examples](https://github.com/microsoft/dev-tunnels/blob/16d8ed6e3c0131a25362d537e12fe3293e96c80f/cs/test/TunnelsSDK.Test/TunnelAccessTests.cs)
- [Relay host connection and disposal](https://github.com/microsoft/dev-tunnels/blob/16d8ed6e3c0131a25362d537e12fe3293e96c80f/cs/src/Connections/TunnelRelayTunnelHost.cs)
- [Connection token refresh](https://github.com/microsoft/dev-tunnels/blob/16d8ed6e3c0131a25362d537e12fe3293e96c80f/cs/src/Connections/TunnelConnection.cs)
- [Security, TLS termination, anonymous access, and anti-phishing behavior](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/security)
- [Service suitability and preview status](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/overview)
- [Tunnel persistence and expiration](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/faq)
- [Service limits](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits#dev-tunnels-limits)
- [WinUI Web Account Manager prerequisites](https://learn.microsoft.com/en-us/windows/apps/develop/security/web-account-manager)
