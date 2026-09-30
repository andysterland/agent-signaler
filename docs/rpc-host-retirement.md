# Standalone RpcHost retirement release notes

The first Dashboard/Remote-only release removes the standalone RpcHost, its
JSON-RPC listener, installer, publish assets, updater target and firewall setup
targets. It does not add a replacement control listener or headless receiver.
The supported reporting path remains:

`verified hook -> Relay -> current-user named pipe -> Client -> Dashboard`

**Source updates do not uninstall anything or remove machine firewall rules.**
The repository owner must complete the manual steps below on each affected
machine. No uninstall, install, process stop, or firewall mutation was performed
as part of implementing this retirement.

## Historical release and recovery identity

At retirement preparation on 2026-09-30, the last published release containing
RpcHost was [v1.0.23](https://github.com/andysterland/agent-signaler/releases/tag/v1.0.23),
published at **2026-09-30 13:28:09 UTC**, marked **prerelease**. Its actual release
assets include `AgentSignaler.RpcHost.msi`, `AgentSignaler.RpcHost.exe` and
`AgentSignaler.RpcHost.NOTICES.txt`. This is a published historical release, not
an inferred version or a claim of a stable-channel release.

The stable RpcHost MSI UpgradeCode is
**`{A8D1F2A9-762B-4B2C-A5A3-451463D743B2}`**. It is a product-family identity for
manual inventory/recovery, **not** an installed ProductCode to pass to an
uninstall command. Do not copy it into current runtime or updater code.

Release provenance was checked read-only through the public GitHub Releases
API (`releases` and `releases/tags/v1.0.23`). `gh release list` was attempted but
the local CLI had no authentication; no credentials were requested or changed.
Recheck published releases before shipping if another release is published in
the meantime. Historical assets remain available for manual recovery only.

## Preserved ownership-aware cleanup capability

Cleanup must use the exact previously reviewed helper from the affected
machine, not the reduced current helper. Before editing either helper, exact
byte copies were preserved outside the product in the implementation session:

`af2d5143-d695-4730-8f3c-bdf1b0399503\files\rpc-host-retirement`

This directory is under the owner's Visual Studio Copilot CLI `session-state`
directory. It contains `provenance.json` and these files:

| File | Provenance | SHA-256 |
| --- | --- | --- |
| `Set-TestFirewall.ps1` | Exact in-flight tracked working-tree helper | `54c9f6d399af8f743d711e5955c03ea852c4863d77b7c4109df87e6b0a8f97a9` |
| `Set-ApplicationFirewall.ps1` | Exact in-flight **untracked** helper; no available Git revision | `51fe43b20025103ee8614a06cf9e3c3a09bd365385fea38c0ad3857dd637a50a` |
| `Set-TestFirewall.tracked.ps1` | Unmodified tracked historical helper | `93553537731c14847d627234c5ac2dcc0d807b8d4b172fd855f58d9f9242838a` |

The last commit changing the tracked test helper was
`26873ba7f2c3c99550139ab98a9a36428aaed260`; its repository path is
`scripts\Set-TestFirewall.ps1`. The checkout HEAD at archival time was
`e0ce682b6f2b2e9d67a4aeeb313f18e4c697a3bf`. Neither commit identifies the
in-flight edits or the untracked application helper: use their archived bytes
and hashes. A fresh clone cannot recover the untracked helper.

The owner must retain a protected copy of this session archive and cleanup
receipts until all affected machines are retired; a local session directory is
not a distributed release asset or a permanent backup. For other machines or
checkouts, preserve their actually used helpers and manifests as well. Do not
substitute an unreviewed version when ownership checks fail.

## Manual owner procedure (not an automated migration)

### 1. Inventory and preserve before replacing helpers

- Inventory each installed MSI, separately managed executable, checkout,
  Release/Debug configuration, executable path, and previously used
  Private/Public/Domain profile. Inventory **MSI-owned**, **application-script**,
  **test-script**, and **Windows application-consent block** rules separately.
- Preserve shared Dashboard settings, SQLite state, tunnel identity, unrelated
  integrations, existing cleanup manifests and receipts. Back up the new helper
  bytes separately before temporarily restoring an old helper.
- Retrieve the matching historical revision or hash-verified archive into the
  **original checkout's `scripts` directory** for this manual operation. Both
  helpers derive checkout paths/ownership from their location. Do not execute
  the copies directly in the session archive or a different checkout, and do
  not reset a dirty working tree to obtain them. Restore the new helper files
  afterward; do not commit the old copies or add a retirement helper.
- Verify archived files with `Get-FileHash -Algorithm SHA256` before using them.
  If the archive is absent, recover the tracked helper from the exact commit
  above; recover the untracked helper from the owner's protected copy. Do not
  claim a Git-only recovery path for that untracked file.

All commands in the next two sections refer to the **restored old helpers**,
run in an ordinary PowerShell terminal at the original checkout as the user
who created the rules. `Status` and `-WhatIf` are read-only; the non-WhatIf
commands are **manual owner actions requiring explicit approval** and may
request UAC. Never run them during source validation.

### 2. Remove script-owned rules with exact ownership verification

The archived application helper discovers its entire current-user owned group,
including installed and developer Dashboard/RpcHost paths. `Remove` is not
RpcHost-only and is **not narrowed by `-Scope`**. It needs neither executable
files nor receiver-port arguments. Approve removal of the entire verified group
and plan to reapply Dashboard rules afterward:

```powershell
.\scripts\Set-ApplicationFirewall.ps1 -Action Status
.\scripts\Set-ApplicationFirewall.ps1 -Action Remove -WhatIf
# Owner only, after approving the complete displayed group:
.\scripts\Set-ApplicationFirewall.ps1 -Action Remove
.\scripts\Set-ApplicationFirewall.ps1 -Action Status
```

The final old-helper status must report zero owned application rules. Preserve
`artifacts\application-firewall\last-operation.clixml` before any subsequent
modifying operation.

The archived test helper finds rules by checkout, expected executable paths,
configuration and profile. Its `Remove` includes retained testhost rules along
with built, published and fixed-fixture RpcHost allow rules. It does not require
the executable files to remain on disk. Repeat this explicit sequence for
**each previously used configuration/profile in every original checkout**;
single-profile invocations also work with the tracked historical helper:

```powershell
.\scripts\Set-TestFirewall.ps1 -Action Status -Configuration Release -Profile Public
.\scripts\Set-TestFirewall.ps1 -Action Remove -Configuration Release -Profile Public -WhatIf
# Owner only, after approving the complete displayed scope:
.\scripts\Set-TestFirewall.ps1 -Action Remove -Configuration Release -Profile Public
.\scripts\Set-TestFirewall.ps1 -Action Status -Configuration Release -Profile Public
```

Repeat with `Private`, and with `Domain`/`Debug` where actually used. The
in-flight archived helper defaults to both Private and Public; the tracked
helper selects one profile. Use explicit profiles rather than relying on either
default. The final old-helper status must report zero owned allow rules in each
scope. Preserve `artifacts\test-firewall\last-operation.clixml` after each
operation because the next modifying run overwrites it.

#### Previously recorded test-created block rules

These are separate from allow rules: **old-helper `Remove` does not remove
blocks**. Only old-helper `Enable -OwnedBlockRulesCsv` supports this historical
cleanup. Use only the original reviewed CSV containing
`Name,Program,Protocol,Profile,Action`; its protocol values are `6` (TCP) or
`17` (UDP), with `Public` and `Block`. The old helper recognizes only the exact
built RpcHost path and GUID-named legacy test-copy paths in the original
checkout. It verifies the exact rule identity, application path, ports,
addresses, local policy origin, direction, enabled state and edge policy.

First run the old helper's read-only ownership inspection:

```powershell
$manifest = '<ABSOLUTE_PATH_TO_ORIGINAL_REVIEWED_BLOCK_RULES_CSV>'
.\scripts\Set-TestFirewall.ps1 -Action Status -Configuration Release -Profile Public -OwnedBlockRulesCsv $manifest
```

If it reports verified blocks, the following optional sequence requires the
old helper's build/publish prerequisites (all five mandatory original binaries).
Do it **before removing those copies**. `Enable` temporarily provisions all
seven original allow targets for this profile while removing verified blocks;
the subsequent `Remove` cleans those allow rules up again:

```powershell
.\scripts\Set-TestFirewall.ps1 -Action Enable -Configuration Release -Profile Public -OwnedBlockRulesCsv $manifest -WhatIf
# Owner only, after approving both the allow-rule and block-rule scope:
.\scripts\Set-TestFirewall.ps1 -Action Enable -Configuration Release -Profile Public -OwnedBlockRulesCsv $manifest
# Preserve the Enable receipt before Remove overwrites it.
.\scripts\Set-TestFirewall.ps1 -Action Remove -Configuration Release -Profile Public -WhatIf
.\scripts\Set-TestFirewall.ps1 -Action Remove -Configuration Release -Profile Public
.\scripts\Set-TestFirewall.ps1 -Action Status -Configuration Release -Profile Public -OwnedBlockRulesCsv $manifest
```

Repeat only for originally used configurations/manifests. Final status must
report zero allow rules and zero verified blocks (recorded blocks already
absent). If prerequisites or the reviewed manifest are unavailable, leave
cleanup pending for manual administrator review; do not enable guessed paths,
broaden eligibility, or fabricate a manifest from display names. Never remove
foreign or modified rules. A failed ownership check is a stop condition, not
permission to delete by group/name wildcard.

### 3. Stop and uninstall the installed product separately

After any block cleanup that needs the old binaries, stop the **exact installed
RpcHost** through its owning console/process after verifying its full executable
path and ownership. Never kill by image name. On each machine use **Windows
Settings -> Apps -> Installed apps -> Agent Signaler RpcHost -> Uninstall**,
accepting the MSI's elevation request when required. Do not uninstall Dashboard
or Remote. For a separately managed standalone copy, stop that exact copy and
remove only its verified owned files manually.

MSI uninstall removes **MSI-owned** resources, including its own firewall
provisioning; it does not remove script-created or Windows consent rules.
Inspect the uninstall result separately from the old helpers' status. Preserve
shared Dashboard settings/data and unrelated rules. No automatic uninstaller,
installed-product migration, or replacement servicing helper is provided.

### 4. Restore the reduced helpers and retained access

Restore both new helper files after historical cleanup. Rebuild retained test
projects if needed, then follow [the current firewall procedure](test-firewall.md)
to preview and explicitly reapply the retained testhost and Dashboard rules.
The test helper now covers three executables (six Private/Public rules by
default). The application helper covers Dashboard only, with an explicit
receiver-port **Private** rule and an IPv4-loopback-only Public rule. The
Dashboard's existing explicit Private receiver-rule UI workflow is unchanged.

The reduced test helper cannot see deleted targets, so zero rules in its status
is **not evidence of historical cleanup**. Use the old helper for that evidence.
The reduced application helper fails closed on unsupported executables in its
owned group rather than silently retiring them. Keep old-helper status evidence,
receipts and archive until every manual inventory item has been resolved.

## First-release updater transition

Publish the matching updater source and the first Dashboard/Remote-only release
as **one synchronized rollout**. Before invoking the updater, users must refresh
their checkout to that release revision, including **both**
`scripts\Update-AgentSignaler.ps1` **and** `scripts\UpdaterPolicy.ps1`.
Refreshing only the entry script is insufficient. Preserve local changes while
refreshing; do not blindly reset an in-flight working tree.

Old updater policy requires RpcHost MSI and EXE checksum entries before it
checks selected applications. It therefore rejects a reduced manifest even
after manual uninstall or with `-Apps Remote`. The new policy accepts only the
Dashboard/Remote asset set and rejects historical manifests containing retired
assets. Do not run the new updater while the previous, RpcHost-bearing release
is still the latest; wait for the matching reduced release to be published.
Historical releases are manual recovery artifacts, not new-updater inputs.

Users unable to refresh both scripts can manually install the signed Dashboard
and Remote MSIs from the new release using the existing
[installation guidance](user-guide.md). There is no updater self-update,
automatic RpcHost removal, or transitional migration release. Reduced-manifest
acceptance and legacy-manifest rejection must ship with the matching packaging
and release changes.

## Manual completion remains the owner's responsibility

Pending on every affected machine: preserve/distribute the cleanup archive,
inventory exact ownership, approve and verify script-rule cleanup, stop/uninstall
the exact old product, reapply retained access where needed, and retain receipts.
Before release: recheck the last published RpcHost release and synchronize both
updater scripts with the reduced release. Parser checks, source builds and
read-only firewall previews do not complete any of these manual actions.
