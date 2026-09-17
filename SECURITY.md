# Security Policy

## Reporting a vulnerability

Do not open a public issue, discussion, or pull request for a suspected
vulnerability.

Use GitHub's private vulnerability reporting for this repository:

<https://github.com/andysterland/agent-signaler/security/advisories/new>

Include the affected version or commit, reproduction steps, impact, and any
suggested mitigation. Remove credentials, tokens, connection URIs, account IDs,
machine names, private URLs, source code from unrelated projects, and personal
data before submitting.

No response-time or remediation SLA is currently offered. Reports will be
acknowledged and assessed as maintainer availability permits.

## Supported versions

No signed public release is currently designated as supported. Security fixes are
developed on `main` until a versioned support policy is published.

## Important security boundaries

- Anonymous Dev Tunnel mode encrypts transport but does not authenticate report
  senders. Anyone able to reach the public URL can spoof machine/source identities,
  inject reports, request transcript purges, and consume bounded capacity.
- LAN mode is intended only for explicitly trusted private networks or VPNs.
- This detailed-conversation prototype requires externally obtained informed
  permission for content, destination, and retention **before distribution/use**.
  The operator owns that prerequisite; the app neither collects nor verifies it.
  Installation, a notice, and prerequisite-license acceptance do not prove consent.
- New configuration v5 and explicit migrations default detailed reporting on;
  **Share detailed conversations** provides an opt-out. Legacy v1–v4 and HTTP/LAN
  remain status-only. Coordinate Dashboard-first and matching remote-binary
  upgrades; preserve explicit false through repair/recovery. A downgrade is an
  explicit status-only transaction, not an edit of the schema version.
- Allowed content is hook-sourced user text and tool names/observed status, plus
  completed user-facing assistant text from independently verified stop-triggered
  Client file adapters. This text can contain PII, source snippets, or secrets
  users put in messages; there is no PII detector or redaction promise. The app
  does not separately collect source files, credentials, account/environment
  records, tool arguments/results, raw errors, reasoning, attachments, or arbitrary
  hook payloads. No production assistant-file profile is currently verified for
  Copilot CLI, VS Code, or Visual Studio; synthetic fixtures do not establish one.
- Stop references are untrusted, local-only IPC input. Paths must never reach
  HTTP, settings/state, diagnostics, or UI models. Only the existing Client may
  read a profile-bound current-user file after an accepted stop; no scanning,
  watchers, whole-history replay, or file access initiated by the viewer/network.
  Reading grants no ownership: repair, clear, opt-out, rollback, and uninstall
  must leave host transcript inputs unchanged.
- Detailed transport uses only the configured HTTPS Dev Tunnel with normal
  certificate/hostname validation, no redirects/cookies/default credentials, and
  no alternative endpoint on retry. Ingest requires the owned tunnel to be running
  for the loopback Internet listener. Loopback does not prove tunnel provenance.
  Trust includes the configured destination, Windows trust store, tunnel
  service/operator, and externally controlled distribution; there is no
  application-level sender authentication or production authenticity guarantee.
- Transcript events, queues, reads, and UI copies have fixed memory/resource
  limits; receiver content expires after 30 minutes. Agent Signaler creates no
  conversation SQLite history, spool, export, recovery copy, or content logs.
  Memory-only is an application storage policy, **not** a no-disk guarantee:
  the host's existing history, OS paging/hibernation, and external crash capture
  are outside it. Do not include conversation content in diagnostic submissions.
- Opt-out cancels local reads/sends and drops pending detail while status remains
  independent. Remote purge is bounded best-effort: an unreachable receiver may
  retain earlier text until TTL or local clear/disable. The single close/purge
  identifies one stream; other streams may retain earlier text even when the
  receiver is reachable. Receiver disable purges
  content; clear does not disable future reception. Exit stops managed reporting.
- The viewer is inert plain text and read-only, without export/copy, commands,
  approvals, links, or remote images. No browser/network-facing transcript read
  API, pairing workflow, credential store, or certificate provisioning is added.
- CLI paths, IDE hooks, startup registration, firewall changes, Windows App
  activation, and installer payloads cross important trust boundaries. Use only
  trusted installations and review requested changes before applying them.
- Generated installers are not official trusted releases unless their publisher,
  signature, provenance, and checksums are explicitly documented.
