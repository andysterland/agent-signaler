---
applyTo: "src/AgentSignaler.Dashboard/**/*,src/AgentSignaler.Configurator/**/*"
---

# WinUI implementation instructions

- These are unpackaged, self-contained WinUI 3 x64 applications using the
  Windows App SDK. Preserve the custom startup and publish handling in their
  project files.
- Reuse existing resources and native-window helpers. Keep Dashboard and
  Configurator visual behavior consistent where they share branding or
  interaction patterns.
- Keep long-running discovery, networking, process, database, and integration
  work asynchronous. Disable conflicting commands while operations are active
  and restore UI state in `finally`.
- Show actionable errors through the existing UI notification/error patterns.
  Do not expose exception details that may contain private environment data.
- Cancel window-owned work when the window closes. Never update XAML controls
  from a background thread; use the dispatcher.
- Prefer testable controller/service classes for platform and workflow logic.
  Limit code-behind to view state, event routing, and composition when feasible.
- Do not assume packaged-app APIs or package identity. Validate unpackaged
  publish output when changing XAML resources, startup, icons, or project files.
- Preserve accessibility: clear labels, keyboard behavior supplied by standard
  controls, visible busy/error state, and usable layouts at supported scaling.
