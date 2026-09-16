---
applyTo: "tests/**/*.cs"
---

# Test instructions

- Use the existing xUnit patterns and test project matching the production
  component.
- Prefer deterministic fakes, loopback endpoints, temporary directories,
  controlled process fixtures, and `TimeProvider` over sleeps or live services.
- Cover success, malformed input, cancellation/timeout, ownership mismatch,
  rollback, duplicate/replay, ordering, capacity, and exact size boundaries
  when applicable.
- Tests must not use production Azure subscriptions, Dev Boxes, tunnels, IDE
  profiles, firewall rules, startup entries, scheduled tasks, or installed
  applications as fixtures.
- Keep `AGENT_SIGNALER_LIVE_TUNNEL_TEST` disabled unless the user explicitly
  authorizes the documented disposable-environment test.
- Assert externally meaningful behavior rather than private implementation
  details. Avoid weakening security or validation checks merely to simplify a
  test.
