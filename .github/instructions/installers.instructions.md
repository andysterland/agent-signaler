---
applyTo: "installers/**/*,scripts/Build-Installers.ps1,scripts/Build-DashboardBootstrapper.ps1,scripts/Test-Installers.ps1,scripts/Test-DashboardBundle.ps1"
---

# Installer instructions

- Installer work is Windows x64 and uses WiX plus a native dashboard
  bootstrapper. Preserve upgrade, rollback, retained-data, and prerequisite
  behavior.
- Build and inspect installers; do not install or execute generated packages
  during routine validation.
- Remove only ownership-verified Agent Signaler hooks, startup entries,
  scheduled tasks, files, and processes. Never remove foreign or modified
  artifacts.
- Preserve user settings, identity, backups, recovery journals, and unrelated
  integrations across upgrade and uninstall unless product behavior explicitly
  requires otherwise.
- Do not hand-edit generated harvest output. Use the repository scripts and
  deterministic component-GUID workflow.
- Never introduce signing secrets, private URLs, credentials, or machine-local
  paths into installer sources.
