# Repeatable local network tests

Build the Release/x64 test projects and publish RpcHost before setup. Run from
the repository root in an ordinary, unelevated PowerShell terminal:

```powershell
.\scripts\Set-TestFirewall.ps1 -WhatIf
.\scripts\Set-TestFirewall.ps1
```

The second command requests one UAC approval if rules need to change. Only the
firewall helper runs elevated, never the tests. On machines with multiple active
network categories, explicitly select one with `-Profile Public`, `Private`, or
`Domain`. The default is the single active category, not all profiles.

Eight exact executable paths receive inbound **TCP from 127.0.0.1 only**.
Ports are unrestricted because fixtures allocate ephemeral ports. The rules do
not grant LAN/Internet ingress, UDP, edge traversal, or access to other programs.
IPv6 loopback remains covered by Windows' normal loopback handling; `::1` is not
accepted as a Windows Firewall rule address and is not added to these rules.
No global firewall/notification settings, installed-product rules, startup
settings, or persistent environment variables are changed.

Paths are scoped to this checkout and configuration:

- Service, Integration, Dashboard.Core and RpcHost `testhost.exe` build outputs.
- The built RpcHost application and published RpcHost EXE.
- `.rpc-test-work\published-exe\exe-only\AgentSignaler.RpcHost.exe`
- `artifacts\rpchost-publish-tests\firewall-prepared\exe-only\AgentSignaler.RpcHost.exe`

The last two paths are static by default. Their fixtures take exclusive file
leases, refuse unexpected existing directories, and clean up their own copies.
Concurrent runs of the same EXE-only fixture in one checkout fail instead of
overwriting each other; serialize those runs or use separate checkouts.
Other per-test state remains isolated. Browser tests already use the static
published EXE, so they do not need another copy or rule.

Rules are path-based, not binary-hash-pinned: subsequent builds at these exact
paths retain the same loopback permission. Changing checkout, configuration,
target framework or active network profile requires matching setup. Run with
`-Configuration Debug` for Debug outputs.

Setup verifies existing owned-rule properties and is idempotent. Rerunning it
with unchanged rules does not request elevation. It also sets
`AGENT_SIGNALER_RPC_TEST_EXE` and `AGENT_SIGNALER_RPC_HOST_EXE` in the current
terminal only. In a new terminal, rerun setup or set those variables explicitly
to the published EXE.

```powershell
.\scripts\Set-TestFirewall.ps1 -Action Status
.\scripts\Test-RpcHostPublish.ps1 -Version 1.0.19
dotnet test tests\AgentSignaler.RpcHost.Tests\AgentSignaler.RpcHost.Tests.csproj --no-build --configuration Release -p:Platform=x64 --blame-hang --blame-hang-timeout 3m --blame-hang-dump-type none
```

Run potentially blocking network tests last, after other builds and checks.
Machine/group policy may still override local rules; a successful setup is not
proof that tests passed or that a policy-controlled machine will never prompt.
Verify actual test results and use bounded timeouts.

## Ownership and removal

Rules have deterministic checkout/path/profile identities and a dedicated group.
Setup/removal refuses an owned-name rule whose checked properties differ.
Unrelated rules, including existing Windows application-consent rules, are not
rewritten. Each modifying helper run records exact created/removed identities
and completion status in `artifacts\test-firewall\last-operation.clixml`.

To remove only rules created by this script (one UAC approval if necessary):

```powershell
.\scripts\Set-TestFirewall.ps1 -Action Remove -Profile Public
```

An optional `-OwnedBlockRulesCsv <absolute-path>` can remove previously verified
test-created blocks during Enable, in the same elevation. The CSV must contain
`Name,Program,Protocol,Profile,Action`, with exact unique rule identities, TCP/UDP,
Public and Block. Only the narrowly recognized legacy test paths are eligible;
current rule/application/port/address properties must still match. A changed
record is an error, not permission to delete it. Never reuse an obsolete
manifest after someone has changed its rules.

This is developer-only setup. It is not shipped as a RpcHost runtime elevation
helper and does not replace MSI-owned production receiver provisioning.
