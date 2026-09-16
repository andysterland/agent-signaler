---
applyTo: "**/*.cs,**/*.csproj,**/*.props,**/*.targets"
---

# .NET implementation instructions

- Use C# with nullable reference types and implicit usings enabled. Follow the
  existing compact style and avoid speculative abstractions.
- Preserve `net10.0` and the Windows target-framework variants already selected
  by each project. All product builds are x64.
- Pass `CancellationToken` through asynchronous I/O and process operations.
  Apply explicit time budgets at external, IPC, and network boundaries.
- Validate at trust boundaries before mutating state. Keep payload, session,
  concurrency, retry, and storage limits explicit and tested at their exact
  boundary values.
- Catch only expected exception types and return actionable failures. Never
  convert a failed mutation into a success-shaped result.
- Keep protocol serialization strict: reject unknown or duplicate properties
  where the existing protocol does, require UTC timestamps, and update
  validation and compatibility tests with any wire-model change.
- Use `TimeProvider` for behavior that tests need to control.
- Preserve deterministic persistence and ordering. SQLite writes that update
  related state must remain transactional.
