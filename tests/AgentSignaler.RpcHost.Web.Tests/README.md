# Test-only RPC browser contract harness

This is not a production web UI. `client\rpc-client.ts` is a browser-native
WebSocket example with unique string request IDs, 32 application and four reserved
control request slots, notification handling, batches, sanitized errors, and
explicit reconnect/disposal. Deadlines match the host method profile: ten seconds
for local operations, three minutes for sign-in, and five minutes for CLI work.
`notify` throws on local serialization/send failure; silence is not delivery proof.
`client\state.ts` provides independent domain/entity views and decimal-string
revision comparisons using `BigInt`.

Views subscribe before their initial query, refetch on every reconnect, reset
watermarks on host-instance changes, and never replace a newer complete view with
an older response. Pagination sends `expectedRevision` after the first page and
publishes only complete assemblies. After three churn failures, the previous
complete view stays stale; no background poll runs. Demand or invalidation waits
out the one-second cooldown before one retry. Consumers must display `stale`,
including when disconnected or when the server sets `isStale`.

The browser fixture serves these modules as JavaScript without a bundler. A
TypeScript consumer can display a complete machine view as follows:

```typescript
import { RpcClient } from "./client/rpc-client.ts";
import { paginatedView } from "./client/state.ts";
import type { Page, Snapshot } from "./client/state.ts";

const client = new RpcClient();
const machines = client.watch(paginatedView("machines", params =>
  client.request<Snapshot<Page<unknown>>>("machines.list", { ...params })));
await client.connect("ws://localhost:51821/rpc");
await machines.refresh();
// Render machines.data together with machines.stale; this is not a global snapshot.
// Explicitly reconnect after transport loss; never automatically retry mutations.
client.dispose();
```

## Prerequisites and listener-free checks

Use Windows x64, Node.js 22.18 or later (native TypeScript type stripping), and
the repository-selected .NET SDK. Versions are pinned in this private manifest
and lockfile; no npm dependencies are added to production.

```powershell
# From the repository root:
Set-Location tests\AgentSignaler.RpcHost.Web.Tests
npm ci --ignore-scripts
npm run typecheck
npm run test:unit
npx playwright test --list
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path (Get-Location) '.browsers'
npx playwright install chromium
```

The unit runner uses in-memory transports and a fake clock: it starts no HTTP,
WebSocket, receiver, or browser listener. Test discovery also starts no listeners.

## Actual-host Chromium tests — final network-test phase only

Publish the real host first; from the repository root an explicit prerequisite
command is:

```powershell
dotnet publish src\AgentSignaler.RpcHost\AgentSignaler.RpcHost.csproj --configuration Release -p:Platform=x64 --runtime win-x64 --self-contained true --output artifacts\publish\RpcHost
```

The default executable is
`artifacts\publish\RpcHost\AgentSignaler.RpcHost.exe`, relative to the repository root.
Override it with an absolute `AGENT_SIGNALER_RPC_HOST_EXE` if the packaging script
uses a different publish directory. Run only after implementation, build,
packaging, and other non-network checks have finished:

```powershell
# From the repository root:
$env:AGENT_SIGNALER_RPC_HOST_EXE = [IO.Path]::GetFullPath(
    (Join-Path (Get-Location) 'artifacts\publish\RpcHost\AgentSignaler.RpcHost.exe'))
Set-Location tests\AgentSignaler.RpcHost.Web.Tests
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path (Get-Location) '.browsers'
$env:AGENT_SIGNALER_RPC_WEB_NETWORK_TESTS = '1'
npm test
Remove-Item Env:\AGENT_SIGNALER_RPC_WEB_NETWORK_TESTS
```

Use `npm run test:browser` instead to run only Chromium tests. Without the explicit
network gate, fixture setup fails rather than silently skipping required tests.

Each test owns a new localhost HTML server, a specific RpcHost child process, and
an isolated `.fixtures\<guid>` settings/database/extraction directory. Receiver
and RPC ports are reserved independently and never use configured production
ports. Settings select loopback Dev Tunnel mode with automatic sharing disabled
and nonexistent, isolated Azure/Dev Tunnel executable paths. No account, tunnel,
Windows App, firewall, startup, installer, or remote-session action is requested.
Process cleanup targets only the exact subprocess created by that fixture.
Tests do not capture raw host output, screenshots, videos, or traces.

## Evidence boundaries

The real-host tests exercise Chromium's actual WebSocket Origin and transport,
domain envelopes, isolated SQLite-backed machine queries and settings writes,
settings invalidation convergence, mixed batches, errors, explicit-null/string
IDs, notification response omission, single-controller rejection, same/new-host
reconnect, and acknowledged shutdown.

The deterministic race, 64-bit revision, domain/entity isolation, multi-page
assembly, and three-restart churn tests use synthetic in-memory responses. They
are not evidence of live maximum-size server pagination, live ordering latency,
CLI cancellation, LAN/tunnel isolation, or supported WebView policy behavior.
Those require the host/integration suites and deferred release acceptance.

**Unauthenticated control warning:** any accepted localhost origin or native
client capable of supplying such an Origin can control RpcHost. Loopback binding,
Origin checks, and a single-controller lease are not authentication.
