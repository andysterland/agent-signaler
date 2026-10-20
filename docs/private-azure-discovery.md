# Private Azure Blob discovery

Private discovery is optional. Direct Dashboard URLs remain supported. The
reporting path is unchanged: verified hook → Relay → current-user pipe → Client →
Dashboard. Azure stores only a signed connection-control document, never machine
identifiers, hooks, sessions, prompts, responses or conversation text.

**Current production blocker:** Azure setup and stored-policy mutation are
deliberately disabled. Set Container ACL does not expose an ETag compare-and-set,
and a container lease does not exclude other ACL writers. The documented time
condition cannot establish the exact concurrency guarantee required to preserve
foreign policy edits. Preview reports this limitation before creating a key,
journal or cloud resource. No sign-in, retry, live test or confirmation bypasses
it. The lifecycle below describes the implemented controller contract exercised
with atomic fake backends, not completed live Azure provisioning support.

## Dashboard setup

1. Select Internet mode and restart Dashboard if necessary. Verify Dev Tunnel
   sharing through the existing controls. Azure authentication is independent of
   Dev Tunnels authentication.
2. Sign in explicitly using the existing Azure CLI prerequisite workflow. Setup
   never opens sign-in automatically. The publisher uses `AzureCliCredential`
   pinned to the entered tenant and subscription; there is no default credential
   chain, account-key retrieval, or authentication fallback.
3. In Settings → Internet sharing → Private Azure Blob discovery, enter the tenant
   GUID, subscription GUID, existing resource group, and existing storage account.
   This version supports the public Azure `blob.core.windows.net` service.
4. Select **Preview setup**. Review the exact account endpoint, networking and SAS
   policy summary, generated dedicated private container, fixed `endpoint.json`
   blob, reader policy, owner marker, local protected state and costs. No Azure
   mutation occurs during preview. Preview expires after ten minutes.
5. Confirm the preview and select **Create discovery**. Dashboard creates only the
   dedicated private container/blob/policy. It does not create accounts/resource
   groups, modify networking/security policy, or assign Azure roles. Interrupted
   creation remains explicitly recoverable, not falsely rolled back.
6. Review status, select **Test reader access**, then explicitly copy the Client
   connection package into Configurator. Copy excludes clipboard history and cloud
   roaming and does not flush clipboard content into persistent storage. The
   clipboard can still expose credentials to other software. No automatic delayed
   clearing is scheduled. Never put the package into source control or support logs.

The package is `agentsignaler-discovery:v1:` plus base64url JSON, at most 16 KiB.
It contains one HTTPS single-blob stored-policy SAS, discovery identity and P-256
publisher public key. Remote machines require no Azure account, CLI or sign-in.
An unavailable signed document means the package is provisioned but not presently
connectable; it is not a verified reporting endpoint.

## Permissions and account constraints

Use an existing account whose Blob endpoint is reachable from Dashboard and the
remote Clients. “Private” means **no anonymous Blob read or container listing**,
not an Azure private network endpoint. SAS does not bypass storage firewall,
private endpoint, public-network, perimeter, or organization restrictions.

Dashboard needs these permissions on the selected resources:

| Operation / documented API | Role field | Exact permission |
| --- | --- | --- |
| ARM account inspection ([provider permissions](https://learn.microsoft.com/en-us/azure/role-based-access-control/permissions/storage#microsoftstorage)) | `Actions` | `Microsoft.Storage/storageAccounts/read` |
| ARM policy-bound SAS signing ([List Service SAS, 2025-06-01](https://learn.microsoft.com/en-us/rest/api/storagerp/storage-accounts/list-service-sas?view=rest-storagerp-2025-06-01)) | `Actions` | `Microsoft.Storage/storageAccounts/listServiceSas/action` |
| Create dedicated private container with ownership metadata ([Create Container](https://learn.microsoft.com/en-us/rest/api/storageservices/create-container#permissions)) | `Actions` | `Microsoft.Storage/storageAccounts/blobServices/containers/write` |
| Read container properties, metadata and ownership ETag ([Get Container Properties](https://learn.microsoft.com/en-us/rest/api/storageservices/get-container-properties#permissions)) | `Actions` | `Microsoft.Storage/storageAccounts/blobServices/containers/read` |
| Read stored policy identifiers/public access ([Get Container ACL](https://learn.microsoft.com/en-us/rest/api/storageservices/get-container-acl#permissions)) | `Actions` | `Microsoft.Storage/storageAccounts/blobServices/containers/getAcl/action` |
| Set/renew/rotate/revoke stored policies; currently concurrency-blocked ([Set Container ACL](https://learn.microsoft.com/en-us/rest/api/storageservices/set-container-acl#permissions)) | `Actions` | `Microsoft.Storage/storageAccounts/blobServices/containers/setAcl/action` |
| Acquire/release the container lease using the Blob data endpoint ([Lease Container](https://learn.microsoft.com/en-us/rest/api/storageservices/lease-container#permissions)) | `Actions` | `Microsoft.Storage/storageAccounts/blobServices/containers/write` |
| Read/list blobs and read blob properties/metadata ([provider DataActions](https://learn.microsoft.com/en-us/azure/role-based-access-control/permissions/storage#microsoftstorage)) | `DataActions` | `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read` |
| Create/conditionally overwrite `endpoint.json`, including its metadata ([Put Blob](https://learn.microsoft.com/en-us/rest/api/storageservices/put-blob#permissions)) | `DataActions` | `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write` |
| Acquire/release the blob lease ([Lease Blob](https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob#permissions)) | `DataActions` | `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write` |
| Delete only the verified owned discovery blob ([Delete Blob](https://learn.microsoft.com/en-us/rest/api/storageservices/delete-blob#permissions)) | `DataActions` | `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete` |

Account inspection and SAS signing are granted at the selected account scope.
Container creation requires a grant at an existing parent scope (normally that
account); container/blob operations can be narrowed to the dedicated container
once it exists. Dashboard does not create or change role assignments. The table
distinguishes role `Actions` from `DataActions` even though the container
operations themselves use the Blob data endpoint. In particular, the documented
Blob **Lease Container** permission is `containers/write`, not the separately
listed ARM container operation `containers/lease/action`; Dashboard does not call
that ARM lease API. `blobs/write` covers both initial creation and subsequent
overwrite; the create-only `blobs/add/action` alternative is insufficient for
renewable publication. No container-delete permission is needed because the
implementation deliberately retains the container. No `listkeys/action`,
`listAccountSas/action`, `generateUserDelegationKey/action`, account-write,
resource-group-write or role-assignment-write permission is requested or used.

**Storage Blob Data Owner** is the least-privileged built-in role documented for
Set Container ACL. Data Contributor alone is insufficient for that operation.
A custom role can grant the exact required actions. Management-plane SAS signing
is separately authorized: `listServiceSas/action` is powerful account-scoped
signing authority, not a narrowly blob-scoped publisher permission. Scope and
approve it accordingly. Do not grant it to remote Clients.

Shared Key authorization must be allowed on the selected account. ARM's omitted
or null `allowSharedKeyAccess` means the service default (enabled); an explicit
false is refused without changing the account.
The selected renewable service SAS cannot be substituted with a user-delegation
SAS (its delegation key lifetime is at most seven days). Account/organization
incompatibility is a setup failure; Dashboard never enables Shared Key, shortens
the agreed policy lifetime, retrieves keys, or rotates account keys.

The ARM request uses API **2025-06-01**, canonicalized resource
`/blob/<account>/<container>/endpoint.json`, `signedResource=b`,
`signedProtocol=https`, and the stored-policy identifier. Read permission, start
and expiry are exclusively in the policy. Returned SAS values containing fixed
expiry/start/permissions, IP restrictions, duplicate or additional fields are
rejected. ARM responses are bounded to 64 KiB, depth 16, with duplicate rejection;
raw Azure response bodies are never displayed or logged. SDK content diagnostics
and automatic retries are disabled; external operations have cancellation and
explicit deadlines.

Azure SAS-expiration-policy documentation currently says expiration action is not
supported for service SAS associated with a stored access policy. Dashboard parses
and displays the configured Log/Block action and maximum interval regardless;
conservatively, a Block interval shorter than 90 days plus five minutes of start
backdating refuses setup rather than disregarding an organization restriction.
Log-only shorter intervals are disclosed, not silently changed. Effective policy
dates are re-read and compared after ACL mutation, and scoped SAS reads plus
anonymous read/list denial must succeed. Any actual organization/storage refusal
blocks setup and leaves recovery state. No success-shaped fallback is available.

## Lifecycle and controls

- Reader policy: 90 days, renewed to 90 days when **fewer than 30 days** remain.
  Dashboard checks on startup and daily while running. Renewal preserves the
  identifier and copied package, including restoration after natural expiry.
- Document: strict versioned JSON, maximum 8 KiB, ECDSA P-256/SHA-256 over exact
  UTF-8 payload bytes, IEEE P1363 signature, base64 envelope fields. The signing
  public key is pinned in Configurator. An unauthorized storage writer cannot
  manufacture another trusted endpoint without the local signing key.
- Publication: only the current health-verified owned Dev Tunnel endpoint;
  unavailable states omit the endpoint. Intent/revision is flushed locally before
  upload; revisions never reset or overflow. Documents expire after 24 hours.
- Refresh: immediately on endpoint availability/replacement and every six hours
  while verified sharing is active, not on every health heartbeat. Failed updates
  are distinct from successful sharing and retry with bounded exponential jitter,
  ordinarily 30 seconds to five minutes, honoring server Retry-After guidance.
- **Publish current state** retries publication. **Test reader access** verifies the
  scoped SAS against the exact owned signed blob, not receiver availability.
- **Rotate package** creates a fresh random reader-policy ID and new package while
  preserving the previous reader group. Distribute the new package first, then
  explicitly **Retire previous reader group**. A further rotation is blocked until
  the previous group has been retired. Retired IDs are durably retained and never
  re-created; the bounded retirement ledger fails closed at capacity.
- **Revoke discovery** invalidates both current and overlapping reader groups.
  Revoked IDs are not renewed or silently replaced. SAS-policy changes can take
  up to 30 seconds to propagate. All Clients sharing that group are affected.
- **Disable publishing** is independent of revocation/deletion and explicitly
  retains existing reader access. The last document can remain usable until its
  signed expiry. Re-enabling publishes the currently verified state.
- Stop/Delete/Logout invalidate prior publisher generations. Superseded network
  writes are canceled, serialized and followed by unavailable publication. Exit
  allows at most three seconds for best-effort unavailable publication within the
  normal shutdown budget. Azure failure cannot stop local shutdown.

A six-hour available refresh does not trust the previous Connected flag: it
rechecks the canonical resource lease, current signed-CLI account/tunnel ownership,
receiver port and anonymous connect-only ACL, and reruns the existing anonymous
public HTTPS `/health` protocol negotiation. Available publication performs that
check again immediately before conditional upload, then checks the captured
generation. Stop/Delete/Logout cancel an in-flight check; health/ownership failure
cannot produce a newly committed available document. Azure work never holds the
tunnel-control gate.

A crash, offline machine or unreachable Azure endpoint can leave a stale signed
document until expiry. Discovery is not proof that Dashboard is currently running.
A closed settings view cancels only view-owned commands; the publisher belongs to
the runtime and canonical data-resource lease, across processes/Windows sessions.

## Protected ownership, concurrency and recovery

`private-discovery.dpapi` contains the ownership checkpoints, revision/retirement
state and protected signing/SAS state as **current-user DPAPI ciphertext**. It has
an explicit protected current-user-only ACL, bounded size/depth, duplicate and
unknown-field rejection, durable atomic replacement, and ciphertext fingerprint
checks against external edits. Ordinary Dashboard settings, previews and runtime
status never contain signing keys or SAS/package values. Preserve this file when
repairing a partial operation. Never replace a missing/decryption-failed trust
anchor silently: another Windows user or restored unreadable state is blocked.

Each Azure mutation rechecks exact generated container identity, ownership
metadata, blob metadata, private ACL, expected policy IDs/permissions/validity,
allowed blob inventory, and the existing document signature/identity/revision.
Foreign policy/blob/metadata edits block the operation; they are not overwritten.
Initial blob creation is create-only. Existing blob writes/deletes use an actual
blob lease plus exact ETag conditions. Container and blob leases are finite,
never broken or stolen, and mutations are bounded below their lease duration.

**Important Azure concurrency distinction:** Set Container ACL and Lease Container
do not support `If-Match`, despite Blob writes supporting it. Set Container ACL
supports `If-Unmodified-Since`; a container lease alone is enforced only for
container deletion, not foreign ACL writes. HTTP dates have second precision.
An ETag re-read followed by a time-conditioned write is not an atomic ETag
comparison, and the documented service contracts do not establish the clock
ordering needed to prove that waiting for a later server Date eliminates every
same-second race. Consequently the production adapter refuses every ACL write
with `discovery_acl_atomic_concurrency_unsupported`, and account inspection marks
atomic policy updates unsupported so setup is refused before cloud creation.
Actual SDK transport tests verify no ACL mutation request is submitted even with
a competing same-second edit, and verify the SDK rejects an `If-Match` condition
locally. This is an explicit platform blocker, not a deferred live-test claim.
Provisioning, renewal requiring a policy write, rotation, retirement and
revocation/removal cannot be claimed operational against Azure until a documented
atomic primitive or an explicitly approved change to the concurrency requirement
is available. Existing policies retain their existing expiry; cleanup requires
authorized manual action. Blob publication retains native lease/ETag conditions.

Recovery resumes an exactly owned interrupted container/blob/policy transaction.
Before upload, the exact signed envelope (including publication timestamps and
revision) and expected blob ETag are protected and flushed locally. After an
uncertain response or restart, an exact remote-envelope match reconciles that
same intent without re-upload or revision change; an unchanged remote ETag permits
retry of the identical bytes. A changed ETag with different content, or different
signed content at the intended revision, fails closed. Stop or a different verified
endpoint supersedes obsolete intent with a new monotonic revision rather than
replaying an old available endpoint. In-flight mutations report unknown commit where cloud response loss
prevents certainty; no fictitious rollback is reported. Preserve the recovery
journal until the operation succeeds or explicit owned removal completes.

**Remove discovery** first revokes owned reader policies and deletes only the
verified owned discovery blob. It retains an empty owned container as a visible
tombstone. This is deliberate: container leases do not prevent concurrent foreign
blob creation, so “enumerate empty, then delete container” cannot atomically prove
that a container delete would preserve unrelated data. No recursive deletion or
account/resource-group deletion is attempted. If desired, the owner can inspect
and manually remove the empty container in Azure. MSI uninstall never signs in or
changes Azure. Its integration-removal helper returns before Dashboard runtime/UI
initialization and removes only the exact owned startup registration. It retains
`private-discovery.dpapi`, publisher ownership/signing state and cloud resources;
the state lives outside the MSI installation directory. In this build, the atomic
policy blocker also prevents automated revoke/remove; use authorized manual Azure
cleanup instead. The reader policy and document retain their existing expiries,
and storage/transaction costs may continue until the owner performs cleanup.

Losing the signing key requires explicit new discovery provisioning and package
redistribution. Rotating an Azure account key can invalidate issued service SAS;
recover by explicitly rotating/reissuing packages as appropriate, not by silently
rotating account keys. Network/firewall or policy failures require the owner to
repair the account/environment outside Dashboard.

## Local verification and deferred release acceptance

### Repository implementation verification

The following local commands passed using disposable fixtures and fake Azure
adapters. These results do not remove the production stored-policy concurrency
block described above and do not constitute live Azure or manual UI acceptance.

| Command | Result |
| --- | --- |
| `dotnet build AgentSignaler.slnx --no-restore --configuration Release -p:Platform=x64` | Passed; zero errors and one existing xUnit2013 warning |
| `dotnet test tests\AgentSignaler.Remote.Tests\AgentSignaler.Remote.Tests.csproj --no-build --configuration Release -p:Platform=x64` | 833 passed |
| `dotnet test tests\AgentSignaler.Dashboard.Core.Tests\AgentSignaler.Dashboard.Core.Tests.csproj --no-restore --configuration Release -p:Platform=x64` | 165 passed |
| `dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj --no-build --configuration Release -p:Platform=x64` | 1,380 passed; one explicitly disabled live-tunnel test skipped |
| `dotnet test tests\AgentSignaler.Service.Tests\AgentSignaler.Service.Tests.csproj --no-build --configuration Release -p:Platform=x64` | 473 passed |
| `dotnet test tests\AgentSignaler.Tunneling.Tests\AgentSignaler.Tunneling.Tests.csproj --no-build --configuration Release -p:Platform=x64` | 214 passed |
| `.\scripts\Build-Installers.ps1 -Configuration Release -NoRestore` | Passed self-contained x64 publish, MSI/bundle builds and read-only inspection; 933 native policy tests, 42 bundle checks and 22 prerequisite-security tests passed |

The native Visual Studio workspace build also passed. Live tunnel tests remained
disabled. Installer inspection verified the discovery assembly in both payloads
and the Azure SDK dependencies in Dashboard. No generated installer was executed,
no installed integration/startup state was changed, and no Azure resources were
provisioned or mutated. No commit, push or deployment was performed.

Packaging still reports the existing release gates: WiX ICE validation is blocked
by local system policy, and the pinned Azure CLI MSI supplies `az.cmd` rather than
the bootstrapper's mandated `az.exe` detection target. These warnings were not
suppressed or treated as live installation acceptance.

### Deferred acceptance

Automated tests use fake Azure identities, in-memory journals and controlled HTTP
transports, not a live subscription, tunnel or installed user configuration.
Dashboard Core and WinUI Release x64 builds validate compilation; a build is not
manual UI acceptance. Do not enable `AGENT_SIGNALER_LIVE_TUNNEL_TEST` implicitly.

First resolve the documented atomic-policy blocker; live tests alone do not
resolve it. Then, before release, separately authorize a disposable Azure
environment and verify:

1. Exact least-privilege RBAC, Shared Key disabled failure, firewall/perimeter
   failure, SAS-policy compatibility and absence of key-retrieval calls.
2. Anonymous blob read/list denied; issued SAS can read only `endpoint.json`, cannot
   list/write/delete; policy propagation and successful scoped reads.
3. Concurrent ACL/metadata edits in the same and following server seconds,
   unchanged-SAS renewal, 412 conflicts, lease loss and partial-commit recovery.
4. Rotation overlap, explicit previous-group retirement, revocation propagation,
   natural expiry restoration, permanent retired-ID non-reuse, and manual cleanup.
5. Verified tunnel replacement, stale/canceled publisher results, stalled Azure,
   closed settings, process crash and three-second shutdown behavior.
6. Manual unpackaged Windows UI navigation/scaling/accessibility, confirmation
   states, masked import, clipboard history/cloud exclusion, and signed handover.

Neither live Azure acceptance nor manual Windows UI acceptance has been performed
by the repository implementation work.

## Microsoft references

- [Storage Accounts – List Service SAS, 2025-06-01](https://learn.microsoft.com/en-us/rest/api/storagerp/storage-accounts/list-service-sas?view=rest-storagerp-2025-06-01)
- [Set Container ACL: permissions, lease header and propagation](https://learn.microsoft.com/en-us/rest/api/storageservices/set-container-acl)
- [Supported conditional headers by operation](https://learn.microsoft.com/en-us/rest/api/storageservices/specifying-conditional-headers-for-blob-service-operations)
- [Blob and container concurrency/lease guarantees](https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage)
- [SAS expiration policies and stored-policy limitations](https://learn.microsoft.com/en-us/azure/storage/common/sas-expiration-policy)
- [AzureCliCredentialOptions: tenant/subscription pinning](https://learn.microsoft.com/en-us/dotnet/api/azure.identity.azureclicredentialoptions?view=azure-dotnet)
