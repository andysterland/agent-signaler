# Repeatable local network tests

Build the Release/x64 test projects before setup. Run from
the repository root in an ordinary, unelevated PowerShell terminal:

```powershell
.\scripts\Set-TestFirewall.ps1 -WhatIf
.\scripts\Set-TestFirewall.ps1
```

The second command requests one UAC approval if rules need to change. Only the
firewall helper runs elevated, never the tests. By default the script creates
matching Private and Public rules. Both remain limited to IPv4 loopback. Use
`-Profile Private`, `Public`, or `Domain` to select a narrower or different
profile explicitly.

Three exact executable paths receive inbound **TCP from 127.0.0.1 only** on
both Private and Public profiles by default (six rules).
Ports are unrestricted because fixtures allocate ephemeral ports. The rules do
not grant LAN/Internet ingress, UDP, edge traversal, or access to other programs.
IPv6 loopback remains covered by Windows' normal loopback handling; `::1` is not
accepted as a Windows Firewall rule address and is not added to these rules.
No global firewall/notification settings, installed-product rules, startup
settings, or persistent environment variables are changed.

Paths are scoped to this checkout and configuration:

- Service, Integration and Dashboard.Core `testhost.exe` build outputs.

Rules are path-based, not binary-hash-pinned: subsequent builds at these exact
paths retain the same loopback permission. Changing checkout, configuration,
target framework or selected network profiles requires matching setup. Run with
`-Configuration Debug` for Debug outputs.

Setup verifies existing owned-rule properties and is idempotent. Rerunning it
with unchanged rules does not request elevation.

```powershell
.\scripts\Set-TestFirewall.ps1 -Action Status
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
.\scripts\Set-TestFirewall.ps1 -Action Status
.\scripts\Set-TestFirewall.ps1 -Action Remove -WhatIf
.\scripts\Set-TestFirewall.ps1 -Action Remove
.\scripts\Set-TestFirewall.ps1 -Action Status
```

These commands cover Private and Public in the current checkout's Release
configuration. Repeat with `-Configuration Debug` or `-Profile Domain` only where
previously used. `-Profile Public` limits an operation to Public rules. The
current helper never deletes Windows application-consent block rules.

## Explicit Dashboard receiver access

Application access is separate from developer test access. The Dashboard's
existing explicit **Private** receiver-rule workflow remains available in its
Network settings. For script-managed installed/developer Dashboard copies,
preview the exact receiver port before approving setup:

```powershell
.\scripts\Set-ApplicationFirewall.ps1 -Action Status
.\scripts\Set-ApplicationFirewall.ps1 -DashboardPort 51820 -WhatIf
.\scripts\Set-ApplicationFirewall.ps1 -DashboardPort 51820
```

Use the port configured in Dashboard, not necessarily the example `51820`.
`-Scope Installed`, `Developer`, or `Both` (default) selects the desired
executable paths; `-Configuration Debug` selects Debug developer output. Only
existing selected executables are provisioned. At most four rules cover two
exact Dashboard paths:

- Private: inbound TCP on the explicit receiver port; LAN senders are allowed.
- Public: inbound TCP on that same port **from 127.0.0.1 only**.

No UDP, edge traversal, Public/LAN access, or Domain rule is added. A Private
rule does not authenticate senders, change Dashboard's bind mode, or make a
loopback-bound receiver reachable from the LAN. Use LAN mode only on a trusted
network. The script neither starts applications nor elevates the Dashboard.

Rules are owned by the invoking user's exact group and verified path/profile,
port, address and other rule properties. `Enable` reconciles the **entire
script-owned application group**, removing stale rules outside the selected
scope/configuration. `Remove` removes that entire verified group, not just the
paths selected by `-Scope`; `Status` inspects the group without requiring
executables or ports. Review this scope before approval:

```powershell
.\scripts\Set-ApplicationFirewall.ps1 -Action Remove -WhatIf
.\scripts\Set-ApplicationFirewall.ps1 -Action Remove
.\scripts\Set-ApplicationFirewall.ps1 -Action Status
```

Modifying runs save receipts in
`artifacts\application-firewall\last-operation.clixml`. Both helpers preserve
unrelated rules, refuse changed owned rules, carry a checked plan through
elevation, and attempt rollback on failure. Preserve receipts before another
operation overwrites them; inspect incomplete operations rather than retrying
blindly. `Status` and `-WhatIf` do not change firewall rules or request elevation.

For earlier installations, follow the [manual retirement release
notes](rpc-host-retirement.md) **before replacing previously used helpers**.
Current helpers deliberately do not provide historical cleanup capability, and
an unsupported executable in the application group fails closed rather than
being automatically removed. This setup does not replace MSI-owned or
Dashboard-managed receiver rules.
