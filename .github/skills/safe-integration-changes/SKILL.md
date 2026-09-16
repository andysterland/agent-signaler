---
name: safe-integration-changes
description: Safely modify IDE hooks, startup entries, configuration files, Client lifecycle, or installers. Use whenever a change writes to user configuration or persistent Windows resources.
---

# Make ownership-safe integration changes

Treat user configuration and Windows registrations as foreign unless Agent
Signaler ownership is proven.

For any mutation:

1. Discover without modifying state.
2. Produce an exact preview of files, settings, registrations, and processes.
3. Validate paths, schema versions, expected hashes/revisions, and current-user
   ownership.
4. Back up owned state and create or preserve the recovery journal.
5. Apply the complete transaction; do not silently skip selected targets.
6. Verify effective behavior, not only file existence.
7. Roll back completed steps on failure and surface unresolved recovery work.
8. On remove or uninstall, leave foreign, edited, or ownership-mismatched
   artifacts untouched and report them.

Do not kill by image name, overwrite concurrent user edits, start the Client
from hooks, or create an alternate reporting path. Keep diagnostics to bounded
application-defined codes.
