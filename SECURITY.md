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
  senders. Anyone able to reach the public URL can submit reports and consume
  capacity.
- LAN mode is intended only for explicitly trusted private networks or VPNs.
- Agent Signaler should receive state metadata, not prompts, responses, source
  code, credentials, tokens, or connection URIs.
- CLI paths, IDE hooks, startup registration, firewall changes, Windows App
  activation, and installer payloads cross important trust boundaries. Use only
  trusted installations and review requested changes before applying them.
- Generated installers are not official trusted releases unless their publisher,
  signature, provenance, and checksums are explicitly documented.
