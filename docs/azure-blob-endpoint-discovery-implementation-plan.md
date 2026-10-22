# Private Azure Blob endpoint discovery implementation plan

## Goal and agreed scope

Let each remote Client find the Dashboard's current verified Dev Tunnel endpoint
without reconfiguring remote machines when a tunnel expires or is replaced.
Dashboard publishes a small discovery document into private Azure Blob Storage.
Dashboard UI provisions the document and a scoped read credential; the owner
copies a connection package into the remote Configurator UI once.

No Azure Function, hosted API, custom cloud process, DNS record, or replacement
receiver is required. Azure Storage provides authorization and static storage.
Dashboard remains the sole receiver host and publisher; Client remains the sole
managed network reporter.

Decisions confirmed for this plan:

- Use an existing Azure Storage account. Dashboard creates a dedicated private
  container, discovery blob, and stored access policy, not an account or resource
  group. Do not automatically change account security, networking, or role assignments.
- Issue an HTTPS-only, single-blob, read-only **service SAS associated with a
  stored access policy**. Dashboard can extend policy expiry without clients
  copying a new credential, and revoke that policy centrally.
- Remote machines do not need Azure CLI, an Azure account, or Azure sign-in.
  Dashboard needs Azure sign-in and explicit provisioning permissions.
- Preserve direct URL configuration and existing installations. Discovery is an
  opt-in connection mode, not a silent migration.

This document is a plan only. It does not provision Azure resources or change
application behavior.

## Unattended implementation contract

Implement the complete feature end-to-end without waiting for human design
approval, stage sign-off, Azure credentials, interactive sign-in, or live resource
provisioning. The decisions and defaults below are implementation requirements,
not questions to return to the owner. Proceed through all implementation stages
using isolated local fixtures and fake Azure adapters.

This authorization covers repository implementation and local automated
verification only. It does not authorize Azure mutations, paid resources, account
or role changes, live Dev Tunnels, generated installer execution, changes to the
developer's installed integrations/startup configuration, deployment, commits or
pushes. Do not request those permissions as a prerequisite for completing the
local implementation. Keep application tests in explicitly owned disposable
fixtures, not the user's actual configuration.

Preserve the product's explicit sign-in, preview/apply, provisioning, rotation,
revocation and removal confirmations. These are shipped application behavior,
not implementation-time approval gates. Exercise the underlying workflows with
fake identities and explicit test confirmations.

Use repository conventions for file layout, naming and error presentation.
Resolve routine implementation details autonomously within this plan's security,
compatibility and resource bounds. If a genuine platform limitation makes a
requirement impossible, continue independent work, document the exact limitation
and leave the affected operation explicitly blocked. Do not weaken authorization,
ownership, TLS or secret handling to obtain a success-shaped result. Missing Azure
credentials or live acceptance evidence is not such an implementation blocker.

### Fixed implementation choices

| Decision | Requirement |
| --- | --- |
| Shared assembly | Add `AgentSignaler.Discovery` for shared non-UI signing/package and protected-store behavior; preserve existing project target-framework/platform conventions. |
| Azure authentication | Use `Azure.Identity` with explicit `AzureCliCredential` and pinned tenant context. Interactive sign-in remains an explicit Dashboard UI action. |
| Azure operations | Use `Azure.Storage.Blobs` for data-plane operations and a typed, bounded ARM HTTP adapter for `listServiceSas`, API version `2025-06-01`; do not retrieve account keys. |
| Reader policy | 90-day validity; automatically extend to 90 days when fewer than 30 days remain, under the initial setup authorization. Check at startup and at least daily while Dashboard is running. |
| Document signing | ECDSA P-256/SHA-256, IEEE P1363 fixed-width signature encoding, base64 envelope fields, and a SubjectPublicKeyInfo public key in the package. Sign the exact payload bytes. |
| Connection package | `agentsignaler-discovery:v1:` followed by base64url-encoded UTF-8 JSON containing the blob SAS URI, discovery identity and signing public key. Maximum total encoded package: 16 KiB. |
| Discovery identity/revision | Random nonempty GUID identity and positive signed 64-bit monotonic publication revision; reject overflow rather than reset. |
| Document validity | 24 hours from publication; refresh every six hours while verified sharing is active. Allow publication timestamps at most five minutes in the future; never extend expiry with clock-skew tolerance. |
| Client discovery | Five-second request deadline; one in-flight request; five-minute periodic refresh with up to 10% jitter; at least 30 seconds between failure-triggered refreshes. |
| Retry/cache | Exponential retry starting at 30 seconds and capped at five minutes, with jitter respecting those bounds and server throttling; verified endpoint cache at most ten minutes and never beyond signed expiry. |
| Shutdown publication | At most three seconds for best-effort unavailable publication, within the existing overall shutdown budget; failure must not prevent local shutdown. |
| Clipboard | Explicit copy only, excluding history/cloud roaming where supported; no automatic delayed clipboard clearing in v1. |
| Release gates | Live Azure and manual Windows UI acceptance are separate, deferred release requirements, not prerequisites for implementation completion. |

Account or organization policy incompatibility is an actionable runtime setup
failure, not permission to silently shorten validity, enable Shared Key or
substitute a different authentication scheme.

## 1. Security and Azure constraints

### Private storage, not a private network endpoint

"Private" means the container has no anonymous access: neither the document nor
container listing is publicly readable. Its normal HTTPS Blob service endpoint
must be reachable from the remote machines. An account restricted to private
endpoints or selected networks only works if those machines have suitable
connectivity. A SAS does not bypass the storage firewall.

Azure encrypts stored blobs at rest, and clients fetch over TLS. Application-level
encryption of the discovery payload is unnecessary for this scope: the SAS controls
read access. Do not put the endpoint into public metadata, DNS, tags, or a public
static website.

### SAS choice and its implications

Microsoft recommends user-delegation SAS where possible. Its delegation key has a
maximum seven-day lifetime, which does not meet the selected copy-once experience.
The selected service SAS requires Shared Key authorization to be allowed by the
account and organization. Fail with setup guidance if it is disabled; never enable
it automatically or silently substitute a different credential.

Use Azure Resource Manager's `listServiceSas` operation with an explicit blob
canonicalized resource and stored-policy identifier. This can issue a service SAS
without retrieving or persisting account keys. The permission is nevertheless
powerful account-scoped signing authority, not a blob-scoped publisher permission.
Make that distinction clear in setup guidance. Never distribute signing authority,
an account key, a write SAS, or an account SAS to remote machines.

Use one dedicated container per discovery identity and one shared reader group in
the first release. Azure permits only five stored access policies per container.
Do not promise unlimited individually revocable client credentials. Multiple
clients using the same connection package are revoked together.

Put read permission and validity in the stored policy; put the blob scope,
HTTPS-only restriction, and policy identifier in the SAS. Do not duplicate policy
permission/start/expiry fields into the SAS: a fixed SAS expiry would defeat renewal.
Do not pin roaming clients to an IP address.

Policy defaults: valid for 90 days; renew to 90 days when less than
30 days remain, after an explicit initial authorization to maintain this policy.
Apply organization restrictions rather than claiming these defaults work under
every account policy. Show the actual effective expiry.

An expired policy can be extended on the same identifier to restore its existing
credentials. A **revoked** identifier must never be reused: reusing it could
reactivate previously revoked credentials. Rotation creates a fresh random policy
identifier and a new package. Account-key rotation also invalidates credentials;
explain that recovery requires new packages rather than silently rotating keys.

### Credential confidentiality and endpoint authenticity

Treat the copied SAS URI and complete connection package as bearer secrets.
Persist them only in a current-user protected credential store, using Windows
DPAPI and ownership-checked files with restrictive ACLs. Ordinary settings contain
an opaque credential reference, not the SAS query or plaintext connection package.
Do not put secrets into hook files, command lines, previews, diagnostics, IPC
responses, exception messages, recovery journals, ordinary settings backups, or
telemetry. Protected credential files may be journaled as opaque ciphertext.

Provide a single copyable package that includes the read SAS URI, discovery
identity, and publisher verification key. Sign documents locally in Dashboard and
pin that public key during Configurator import. This prevents a storage writer
without the Dashboard signing key from substituting a different receiver for
sensitive reports. It does not protect against a compromised Dashboard or Client.

Generate the signing key locally, protect the private key for the current Windows
user, and never upload it. Use the standard ECDSA P-256/SHA-256 implementation
with the fixed encoding and signed-byte format above; do not invent
cryptography. Losing the signing key requires explicit reprovisioning and new
client packages. Never silently replace the trust anchor.

Private discovery is **not receiver authentication**. Dev Tunnel access remains
anonymous under the current design. Anyone who otherwise learns the tunnel URL
can still reach the receiver. Adding authenticated reporting is a separate
protocol/security feature and is not implicitly delivered by this plan.

## 2. Architecture and repository impact

Preserve the reporting path:

```text
verified hook -> Relay -> current-user pipe -> Client -> Dev Tunnel -> Dashboard
```

Add a separate bounded discovery path:

```text
Dashboard.Core -> private discovery blob
Configurator -> read/verify/test during explicit setup
Client -> read/verify/cache -> effective endpoint for all managed transports
```

Azure Storage never receives hook payloads, machine identities, sessions,
conversation text, or reporting credentials. Discovery contains only connection
control data. The read SAS is sent only to the configured Azure Blob endpoint,
never to the tunnel.

| Surface | Planned responsibility |
| --- | --- |
| `AgentSignaler.Contracts` | Versioned discovery document/package models, strict parsing, size limits and endpoint-validation rules; no Azure SDK or secret persistence. |
| `AgentSignaler.Discovery` | Signature/package codec and current-user protected credential storage, shared by Dashboard.Core and Remote without a Dashboard-to-Remote project dependency. Retain repository target-framework conventions. |
| `AgentSignaler.Dashboard.Core` | Azure provisioning adapter, ownership manifest, publisher lifecycle, renewal, rotation, cleanup, runtime domain and commands. |
| `AgentSignaler.Dashboard` | Settings UI for provisioning, publish state, package copy, renewal, revocation, and removal. |
| `AgentSignaler.Remote` | Configuration schema, discovery HTTP resolver, effective-endpoint coordination, secure credential references, transactional Configurator apply, transport integration. |
| `AgentSignaler.Configurator` | Connection-mode selector, masked package import, read/test UI, preview/apply and repair guidance. |
| `AgentSignaler.Client` | Existing tray/runtime status presentation for discovery health; no new reporting process. |
| `AgentSignaler.Tunneling` | Retain verified tunnel lifecycle; expose or consume status without putting Azure provisioning into the CLI controller. |
| `AgentSignaler.Service` | No new cloud listener or discovery endpoint. Existing receiver contracts remain unchanged. |

Specific existing integration points:

- `DashboardSettings.cs`, `DashboardRuntime.cs`,
  `DashboardRuntime.Operations.cs`, and `RuntimeContracts.cs` own saved/effective
  settings, resource gates, command outcomes, lifecycle and domain revisions.
- `MainWindow.Tunneling.cs` owns Internet sharing presentation; extend its settings
  flow rather than creating an unrelated provisioning window/process.
- Existing Azure prerequisite/sign-in flows and `AzureCliProcess.cs` supply
  neighboring patterns. Do not reuse a command wrapper without auditing its
  secret handling and cancellation.
- `RemoteConfiguration.cs` currently accepts versions 1-5 and assumes a concrete
  `BaseUri`; its strict converter must explicitly support discovery.
- `PresenceTransport.cs`, `DashboardConnection.cs`, `RemoteHttpTransport.cs`,
  `ClientCoordinator.cs`, `ClientCoordinator.Transcripts.cs`,
  `TranscriptDeliveryCoordinator.cs`, and `TranscriptTransport.cs` all need a
  coherent effective endpoint.
- `MultiTargetIntegrationManager.cs` currently requires configuration v5 in
  important paths and renders changed file contents in previews. Its schema,
  preview, transactional credential handling, repair and downgrade paths must be
  updated together; secret redaction cannot be just a UI concern.
- Configurator's `MainWindow.xaml`, `MainWindow.xaml.cs`, and
  `ConfiguratorSettings.cs` currently assume a directly entered URL and persist a
  last-successful URL. Do not let that preference override discovery or store a
  credential-bearing URI.

## 3. Discovery document, package, and configuration

Define discovery document v1 independently of the reporting protocol. Required
signed payload fields:

| Field | Purpose |
| --- | --- |
| `version` | Discovery schema version. |
| `discoveryId` | Random stable identity, unrelated to machine/account identifiers. |
| `revision` | Persisted monotonic publication number. |
| `publishedAtUtc`, `expiresAtUtc` | UTC freshness bounds with a defined clock-skew allowance. |
| `state` | Explicit `available` or `unavailable`. |
| `dashboardBaseUrl` | Present only for an available, publicly verified endpoint. |

Use a bounded envelope containing the exact signed UTF-8 payload bytes encoded as
base64 and a signature. Verify the signature before parsing the payload; this
avoids depending on JSON property ordering/canonicalization. Reject duplicate,
unknown, oversized, unsupported or malformed fields, including in the envelope.
Maximum document size: 8 KiB; maximum nesting depth: 8. Bound every decoded
allocation and timestamp range.

An available endpoint must be canonical HTTPS on port 443, root path only, with
no credentials, query or fragment. Validate the actual supported Dev Tunnels
hostname grammar, with an exact `.devtunnels.ms` suffix boundary; reject arbitrary
hosts, suffix lookalikes, IP literals, local addresses and redirects. Do not weaken
certificate validation or health negotiation.

Persist the highest accepted revision and payload fingerprint per discovery
identity. Accept an identical same-revision document for revalidation, reject
changed contents at the same revision, and reject lower revisions. Expiry limits
replay for a newly provisioned Client without prior state; it is not a trusted
global clock. Pin the identity/key as well as requiring a valid signature.

Define a bounded, versioned, ASCII copy/paste package with the blob SAS URI,
discovery identity and signing public key. The package is an encoding, not
encryption. Import validates one exact blob URI, HTTPS, known Azure public-cloud
Blob host syntax, SAS shape, blob scope and HTTPS restriction. Do not accept
custom domains, redirects, alternate cloud endpoints, container/account scope, or
arbitrary URLs in v1. A policy-backed SAS need not expose expiry/permission in its
query: verify access through Azure and Dashboard's provisioning checks rather
than requiring an `se`/`sp` parameter that should be in the policy.

Add remote configuration v6 with an explicit connection discriminator:

- Direct URL: retains the existing URL behavior.
- Private blob discovery: stores only a protected credential reference and
  non-secret connection identity/trust metadata; not a placeholder/fallback URL.

Keep versions 1-5 loadable with unchanged meaning. Preserve detailed-sharing and
source-identity preferences during explicit v6 upgrade. Old binaries must fail
clearly on v6 rather than misinterpret it. Update coordinated upgrade/repair and
installer documentation. Do not allow a v5 downgrade to discard discovery
credentials or silently retain a stale endpoint; require explicit conversion to a
verified direct URL or block the downgrade.

Separate saved connection configuration from a resolved endpoint snapshot.
`BaseUri` access must not perform network I/O or silently return a dummy URL.
Resolve explicitly and pass an immutable snapshot with its own endpoint revision
to transports. Do not rewrite `remote.json` each time the tunnel changes.

## 4. Dashboard UI and Azure provisioning

Add **Stable connection discovery** under Settings > Internet sharing.

### Setup flow

1. Explain private storage, Azure charges, Shared Key requirements, reader-group
   revocation and anonymous receiver access. Show that direct URL mode is unchanged.
2. Use explicit Azure sign-in/check controls, reusing the existing prerequisite
   conventions. Pin tenant/subscription/account context; background operations
   must never trigger sign-in or switch to an unrelated identity.
3. Select an existing subscription, resource group and storage account. Show its
   Blob endpoint, network restrictions, Shared Key policy and required permissions.
   Suggest a generated dedicated container name and fixed `endpoint.json` blob.
4. **Preview setup** lists the exact new container/blob/policy, local protected
   files, owner identity, validity/renewal behavior and costs. It must not contain
   the SAS, signing key, package, or raw Azure output. Provision only after confirmation.
5. Create a new private container with an unguessable ownership marker. Refuse
   name collisions rather than adopting an unrelated existing container. Persist
   exact resource IDs, marker and transaction checkpoints locally.
6. Generate/protect the signing key. Publish a signed unavailable document if
   sharing is not currently verified; otherwise publish the verified endpoint.
   Use create-only writes initially, then ETag-conditional replacement.
7. Create the read-only stored policy and issue a blob-only service SAS through
   the authenticated ARM adapter. Preserve all other ACL entries. Re-read and
   verify resource scope, policy, anonymous denial and successful SAS reads.
8. After verification, expose **Copy agent connection package**. If sharing is
   unavailable, label the package as provisioned but not currently connectable;
   Configurator may import/test it but cannot complete apply until health succeeds.

Required persistent UI status: setup state, last verified publication time,
pending publication, discovery availability, policy expiry, renewal warning and
reader-group identity. Never expose secrets in ordinary selectable status text.

### Ongoing controls

Provide **Publish now / Retry**, **Test reader access**, **Copy connection
package**, **Renew read access**, **Rotate credential**, **Revoke read access**,
**Disable publishing**, and **Remove owned discovery resources**.

Renewal extends the existing policy and preserves the package. Rotation creates a
new identifier/package, temporarily permits old and new readers after explicit
confirmation, and requires a separate action to retire the old identifier.
Revocation invalidates the complete reader group; never display it as merely a
single-machine action.

Use masked secret controls. Clipboard copy is explicit, excludes cloud/history
roaming where the Windows API supports it, and explains that the clipboard can
still expose credentials. Do not schedule automatic clipboard clearing or
overwrite newer user content.

All controls use thin handlers, dispatcher-safe state updates, cancellation,
bounded operations and accessible labels. A closed settings view must not destroy
the runtime publisher; exiting Dashboard must cancel and drain it.

## 5. Publisher lifecycle, ownership and recovery

Run under Dashboard.Core's canonical data-resource lease, not a view lifetime.
Add a separately revisioned discovery domain and explicit resource gates; integrate
with sharing/settings without holding locks across network calls. Another process
or Windows session must not become a second publisher for the same canonical data.

Publish only after the current tunnel has passed the existing anonymous public
HTTPS health verification. Capture its generation/endpoint, then check again
before committing publication so a delayed operation cannot republish an old
tunnel after Stop, Delete or replacement.

Freshness policy: publish a document valid for 24 hours, refresh every
six hours while verified sharing is active, and publish immediately after an
endpoint change. Recheck current ownership and endpoint health before refreshing.
Do not publish on every heartbeat. Use bounded exponential retry with jitter,
honor throttling, and expose a distinct "sharing works; discovery update failed"
state instead of claiming full setup success.

On explicit Stop/Delete/Logout/Exit, stop forwarding promptly. A signed unavailable
publication is best effort within a separate short budget; Azure failure cannot
prevent shutdown. Invalidate/cancel older publish work first and serialize final
publication so it cannot race a late available write. Crash or loss of networking
can leave a stale document until expiry; UI and clients must not mistake it for
proof that Dashboard is running.

Persist publication intent/revision before upload and reconcile uncertain writes
using ETag, identity, signature and intended revision. Retry an uncertain commit
idempotently instead of creating new resources. Maintain a bounded recovery
journal that contains resource ownership and ciphertext references, never plaintext
credentials. Cancellation after a cloud mutation must record the committed or
unknown outcome rather than report rollback without evidence.

Every mutation/removal verifies exact resource identity, ownership marker and
expected state. Conditional writes and an appropriate Azure concurrency primitive
must protect blob and ACL changes; establish the selected API's concurrency
guarantees from its documented contract and automated adapter tests, then confirm
them during deferred live acceptance. The dedicated container reduces ACL sharing, but does
not justify overwriting concurrent foreign policy changes.

Rollback deletes only resources conclusively created by the transaction and still
unchanged/owned. Otherwise retain a recovery record and show the required action.
Never delete or reconfigure the existing account, resource group, foreign blob,
foreign policy, network rules or role assignments.

Disable publishing does not imply revocation or deletion; state that explicitly.
Removal previews reader impact, revokes only owned policy identifiers and deletes
only verified owned objects. Preserve foreign contents and do not delete a container
with unrelated data. Normal MSI uninstall must not sign in to Azure or delete cloud
resources. Retained resources have visible expiry/manual cleanup guidance.

Protect local credentials/signing state for the current user even though the
resource lease spans Windows sessions. Another Windows user or a restored data
directory without decryptable keys must get an actionable blocked state, not a new
publisher identity or plaintext fallback.

## 6. Configurator UI, transactional settings and Client behavior

### Configurator connection UI

Add a **Connection method** selector:

- **Direct Dashboard URL**: existing controls and semantics.
- **Private Azure discovery**: masked connection-package input, explicit import,
  safe identity summary, reader-access status and last successful resolution time.

Provide **Test discovery and connection**, cancellation, **Replace credential** and
**Remove saved credential**. Do not require an agent-side Azure sign-in. Test
performs a bounded authenticated blob GET, verifies signature/identity/freshness,
validates the endpoint, then runs existing receiver capability negotiation. Distinguish
storage authorization failure from tunnel/receiver unavailability; the existing
"enable anonymous tunnel access" message is inappropriate for Blob 403 responses.

Import and tests do not write settings/hooks, start Client, or modify Azure.
Use Preview/Apply to commit the protected credential and configuration alongside
the existing integration/startup transaction. Stage an immutable credential slot,
include its identity/ciphertext hash in revision checks, and atomically switch the
settings reference. On failed apply, preserve the previous credential and config;
remove only owned unreferenced staged slots. A running Client must acknowledge
the effective configuration/credential revision before claiming success.

Show only redacted credential actions in preview/recovery UI. Update
`ConfiguratorSettings` so a last-successful direct URL cannot replace discovery.
Keep a persistent selected mode and explicit saved-versus-effective status.
Applying an incomplete, expired, revoked or unverified setup is blocked without
changing hooks. Expiry is obtained from verified policy/status where available;
do not invent an expiry by parsing absent SAS query fields.

### Runtime resolution

Client owns one shared resolver used by presence and detailed-reporting flows.
Configurator uses the same resolver logic for read-only tests. Relay/hooks perform
no storage access, do not receive the credential and never start Client.

Required bounds: 8 KiB document; 16 KiB connection package; five-second discovery
request deadline; one in-flight resolution; refresh every five minutes with jitter;
minimum 30 seconds between failure-triggered refreshes; retry backoff capped at
five minutes. All budgets must compose with existing connection-test, delivery and
shutdown deadlines, not expand hook timing budgets.

Use conditional GETs/ETags and set `Cache-Control: no-store` on publication. A 304
does not extend the signed document expiry. Do not use mutable unbounded lists of
endpoints or trust browser/proxy redirects. Reject unexpected media types, oversized
streamed responses, malformed packages and TLS failures.

The last verified endpoint is an in-memory, bounded-age cache, not a saved
configuration override. Define a maximum ten-minute verified-cache age, capped by
document expiry. Continue using that endpoint through transient storage outages
only within these bounds. After bounds expire, suspend sends and report discovery
unavailable; no stale direct-URL fallback. Re-resolve on startup and after connection
failures, subject to rate limits. Fail closed and invalidate the cached endpoint on
an explicit unavailable document, invalid signature/identity, revocation/Blob 403,
or missing discovery blob. Never bypass auth/TLS on retry.

Endpoint handover must occur once under Client's delivery coordination:

1. Suspend transcript admission/delivery and invalidate the old endpoint snapshot.
2. Cancel/drain old discovery and transport work; discard results from stale
   configuration or endpoint revisions.
3. Negotiate health/capabilities with the new endpoint before sending reports.
4. Recreate endpoint-bound transports and clear cached protocol/capability state.
   Allocate the appropriate reporting generation and preserve ordering, source
   identity, replay protection and bounded session semantics.
5. Require a fresh presence Started acknowledgement before enabling transcripts.
   Retire old receiver stream/delivery state; do not replay queued conversation
   content into the replacement receiver. Surface gaps using existing semantics.
6. Resume normal current-state reporting without resubmitting old hooks or
   extending transcript retry/retention/result-hold budgets.

Automatic endpoint changes must not mutate saved settings or restart the process.
Do not blindly call the current Configurator reload path: it assumes static URLs,
can attempt old-endpoint offline delivery, and changes hook/pending-work state.
Extract a deliberate handover primitive and test it. A stale old endpoint must
not receive even an Offline report after its lease/trust expires.

Tray Exit cancels discovery, retry timers and both transports. Background
discovery must not resurrect reporting. Preserve startup opt-out, coordinated
reload, expected-revision checks and verified hook ownership.

## 7. Unattended implementation sequence

Run stages 1-7 consecutively without waiting for human approval. There is no
live-Azure prerequisite. Implement capability checks and failure states even when
their live acceptance cannot be exercised in the implementation environment.

1. **Documented Azure contract and threat model.** Establish the exact required
   control/data-plane RBAC for private container creation, blob writes,
   stored-policy ACL updates and `listServiceSas` from current Microsoft
   documentation. Encode the request/response contracts, conditional-write or lease
   behavior and permission failures in fake-backed tests. Cover policy-bound SAS
   issuance without account-key retrieval, unchanged-SAS renewal, revocation,
   propagation and SAS-expiration-policy incompatibility. Record any outstanding
   live confirmation as a deferred release item; do not wait for credentials,
   provision resources or ask a human to approve the next stage.
2. **Shared contracts and protected state.** Implement document/package limits,
   signature verification, URI validation, credential storage, publication/cache
   revisions and remote v6 migration. Add pure unit tests before UI.
3. **Client discovery and handover.** Implement a fakeable resolver and common
   effective-endpoint lifecycle for presence and transcripts. Preserve all direct
   URL paths. Exercise cancellation, stalled storage, endpoint replacement and Exit.
4. **Dashboard provisioning and publisher.** Implement typed Azure adapters,
   preview/commit/recovery, ownership checks, policy lifecycle, runtime domain and
   publish lifecycle. Use the selected Azure Identity/Blob SDKs with explicit Azure
   CLI-backed user authentication and a bounded ARM SAS adapter. Never use a broad credential
   chain that silently selects a different identity. Audit SDK logging/retries and
   disable secret-bearing request/response diagnostics.
5. **Dashboard UI.** Wire setup, status, package copy, renewal, rotation, revocation,
   disabling and ownership-safe removal to the runtime commands.
6. **Configurator UI and integration transactions.** Wire import/test/preview/
   apply, protected revision acknowledgment and recovery. Update Client tray status
   and all v5-only assumptions together.
7. **Servicing, documentation and local completion.** Include any new shared
   assemblies/dependencies in existing self-contained x64 publish/installer inputs.
   Document permissions, expiry, recovery, offline behavior, copy safety, costs and
   anonymous-receiver limitations. Build/inspect installers; do not execute them.
   Record deferred manual/live acceptance and its exact steps separately from
   completed automated coverage. Do not claim those exercises have passed.

Each stage must land with its affected tests. Feature-enable only when both ends
are complete; incomplete discovery must never replace working direct URL behavior.
No change to the existing one-day tunnel expiration is required for this feature.
Expose the completed discovery workflow as an opt-in application feature, without
changing existing installations or requiring a developer-side Azure setup first.

## 8. Local completion and deferred release acceptance

Use existing test projects and fake Azure/HTTP/time/credential-store adapters.
Ordinary automated tests must not create billable resources or start live tunnels.
Automated local verification is required for implementation completion. Manual UI
and live Azure exercises remain required before claiming release acceptance, but
their pending status does not block finishing and handing off the implementation.

| Test surface | Required coverage |
| --- | --- |
| Shared discovery contracts | Strict schema/duplicate handling, all size/depth limits, malformed base64/signatures, hostname suffix attacks, package parsing, expired/future documents, replay and same-revision conflicts. |
| `AgentSignaler.Remote.Tests` | v1-v5 compatibility, v6 direct/discovery discrimination, no plaintext secrets in previews/journals/settings/logs, protected-slot rollback, wrong-user access, resolver single-flight, cache/304/expiry, redirects, Blob 401/403/404, throttling, cancellation and Exit. |
| Remote reporting/transcript tests | One coherent endpoint snapshot, fresh capability negotiation and Started acknowledgment, endpoint changes during sends, stale-result rejection, no cross-endpoint transcript replay, bounded queues, unchanged direct URL semantics. |
| `AgentSignaler.Dashboard.Core.Tests` | Lease ownership across processes/sessions, runtime revisions/gates, verified-only publication, publish/Stop races, partial commits, ETag/ACL conflicts, foreign-resource refusal, renewal, rotation and permanent revoked-identifier retirement. |
| `AgentSignaler.Tunneling.Tests` | Recreated-tunnel status is consumed correctly; public-health failure never publishes a usable endpoint. No live tunnel test unless explicitly authorized. |
| `AgentSignaler.Integration.Tests` | Full fake-backed provisioning/package import/apply/start/discovery/replacement flow, recovery and servicing of protected references, no SAS in hooks or IPC. |
| Deferred manual Windows UI release gate | Accessibility, masked fields, safe copy, busy/cancel behavior, view close versus application Exit, saved/effective status, diagnostics with no secret leakage. Cover testable behavior locally without waiting for a human UI walkthrough. |
| Deferred live Azure release gate | Anonymous blob reads fail; read SAS GET succeeds; write/delete/list/other-blob reads fail; policy extension preserves the exact copied package; old policy revocation takes effect; firewall and Shared Key restrictions fail visibly. Requires separately authorized disposable resources; do not run during unattended implementation. |

Use the repository's Windows SDK and Release x64 commands. Run the smallest new/
affected selectors first, then the affected projects; build both WinUI applications
when wiring UI and inspect publish/installer outputs. Recheck current Azure policy
limits, exact RBAC and concurrency behavior during the deferred live release gate.

### Unattended implementation completion criteria

Implementation is complete when all of the following are satisfied:

- Both UIs, provisioning adapters, protected storage, discovery resolver and
  coherent status/transcript handover are implemented and wired end-to-end.
- The fake-backed acceptance flow provisions a private discovery resource, imports
  its package, applies configuration and replaces the endpoint without editing
  agent settings. It covers renewal, rotation, revocation and failure recovery.
- Affected automated tests and Release x64 builds pass, and required
  publish/installer build-and-inspect work is complete. Missing credentials do not
  cause these tests to skip the core behavior: inject fakes instead.
- Direct URL compatibility, secret redaction, ownership, cancellation, rollback
  and reporting privacy boundaries remain intact.
- Implementation changes and directly related documentation are saved in the
  repository. The handoff distinguishes completed local work from deferred live
  Azure/manual UI release acceptance and any genuine unresolved technical blockers.

Do not pause between stages to obtain sign-off. Do not run the following real-user
release scenario automatically, remove its confirmations, or describe a
fake-backed result as live Azure proof.

### Deferred real-user release acceptance scenario

1. Owner chooses an existing account and explicitly provisions a private discovery
   container/document from Dashboard UI.
2. Owner copies one connection package into Configurator, tests, previews and
   applies it. No agent-side Azure sign-in or extra reporting process is required.
3. Dashboard replaces a confirmed-absent tunnel and publishes the new verified URL.
   Running Clients recover automatically within the configured refresh/retry
   bounds without settings edits or restarts.
4. Status and conversation transports switch together, with fresh negotiation and
   no content delivered to an expired or stale endpoint.
5. Policy renewal leaves existing packages working. Rotation requires a new package;
   revocation blocks the reader group and is never silently undone.
6. Storage outages, expiry, interrupted provisioning and lost protected keys produce
   explicit actionable states without exposing credentials or mutating foreign data.
7. Dashboard shutdown and Client tray Exit promptly stop their respective work even
   if Azure is unreachable. All existing direct URL installations keep working.

## References

- [Azure Storage SAS overview](https://learn.microsoft.com/en-us/azure/storage/common/storage-sas-overview)
- [Stored access policies, limits and propagation](https://learn.microsoft.com/en-us/rest/api/storageservices/define-stored-access-policy)
- [ARM List Service SAS](https://learn.microsoft.com/en-us/rest/api/storagerp/storage-accounts/list-service-sas?view=rest-storagerp-2025-06-01)
- [User-delegation SAS](https://learn.microsoft.com/en-us/rest/api/storageservices/create-user-delegation-sas)
- [Dev Tunnels inactivity expiration](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/faq#when-are-unused-dev-tunnels-deleted)
