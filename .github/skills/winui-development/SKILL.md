---
name: winui-development
description: Implement and validate Agent Signaler WinUI 3 Dashboard or Configurator changes. Use for XAML, windows, controls, async UI workflows, resources, startup, or publishing.
---

# Develop WinUI features

1. Determine whether the feature belongs to Dashboard display/monitoring or
   Configurator discovery/integration management.
2. Find a neighboring UI workflow and reuse its busy-state, cancellation,
   InfoBar/error, dispatcher, and lifecycle patterns.
3. Keep platform/process/network/database logic outside XAML event handlers
   where it can be tested independently.
4. Preserve unpackaged application assumptions; do not require package identity.
5. Ensure window-owned cancellation is triggered on close and no continuation
   updates disposed UI.
6. Reuse `BrandResources.xaml` and standard WinUI controls before adding custom
   styling or interaction behavior.
7. For XAML/resource/startup changes, build the affected project in Release x64
   and ensure generated XBF and PRI resources remain publishable.

Dashboard targets Windows build 22621; Configurator targets build 19041. Do not
unify target frameworks without an explicit compatibility decision.
