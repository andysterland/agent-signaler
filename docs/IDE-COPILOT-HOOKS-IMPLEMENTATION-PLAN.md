# Visual Studio and VS Code Copilot hooks implementation plan

## Goal and agreed scope

Extend Configurator to configure and verify Copilot reporting independently for:

- Standalone Copilot CLI, preserving existing support.
- Visual Studio instances installed on this machine, enabling only instances whose actual IDE hook integration has been verified.
- VS Code Copilot agent sessions, using per-user settings for selected local Windows profiles rather than modifying workspace repositories.

All hooks must feed the existing Relay -> local IPC -> persistent tray Client -> Dashboard pipeline. Preserve anonymous HTTPS/LAN access, five-minute default heartbeats, configurable intervals, and Exit stopping all reporting. Do not add scheduled tasks, a second reporting daemon, or IDE-to-dashboard HTTP shortcuts.

This is an implementation plan, not a claim that hook execution has already been
verified in the installed IDEs. Paths below are relative to the repository's
`src` directory unless explicitly marked as synthetic examples.

## 1. Findings and verification status

### Project findings

`src\AgentSignaler.Remote\Discovery.cs` currently hard-codes:

```text
%ProgramFiles%\Microsoft Visual Studio\18\Main\Common7\IDE\CopilotCli\copilot.exe
```

It prefers that binary over standalone CLI and switches the discovered home to:

```text
%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli
```

That couples distinct products and treats a bundled executable as sufficient proof of IDE support. Configurator still exposes one effective CLI/home, and `IntegrationManifest` tracks only one hook file.

The current CLI parser expects `sessionId`, a numeric Unix-millisecond `timestamp`, and CLI-specific tool fields. Current status and presence validators require `client = copilot-cli`; session identities have no IDE/profile namespace. None of these assumptions is sufficient for simultaneous CLI, Visual Studio, and VS Code reporting.

### Historical local inventory, inspected September 15, 2026

Read-only checks used Visual Studio Installer's `vswhere`, executable version commands, extension metadata, and directory existence. No IDE hook configuration, session database, transcript, or user credential file was modified or read.

| Installation | Observation | What remains unverified |
| --- | --- | --- |
| Visual Studio stable fixture | A Visual Studio 18 installation with a bundled Copilot CLI was observed | Whether that IDE's active Copilot harness loads filesystem hooks, and its effective hook home |
| Visual Studio preview fixture | A separate Visual Studio 18 preview installation with a bundled Copilot CLI was observed | Same; each installation must be verified independently |
| Proposed Visual Studio user home | The standard `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli` directory existed, without a verified `hooks` consumer | Directory existence does not prove that `<home>\hooks` is consumed by either IDE |
| VS Code fixture | A current VS Code installation was present | Effective hook implementation, enabled extensions/features, and policies in the selected profile |
| VS Code extension files | GitHub Copilot extension files were present in the default extension directory | Installed files do not prove the extension is enabled or that this profile uses its agent hook engine; CLI extension listing did not establish activation |
| Default VS Code user data | The default user directory existed without a discovered `profiles` directory | Custom user-data directories, portable installations, and profiles elsewhere are not ruled out |

Do not hard-code the example username. Resolve the Visual Studio candidate home using `Environment.SpecialFolder.LocalApplicationData`. Do not infer support from arbitrary GUID-named child directories or scan session/history databases for evidence.

### Public documentation

VS Code documents agent hooks in Preview, with PascalCase events such as `SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, and `Stop`. It documents shell command fields, `timeout`, snake_case input fields, and ISO-8601 timestamps.

The GitHub CLI documentation separately documents user hooks under `COPILOT_HOME\hooks`, camelCase events, and direct `exec`/`args` support. CLI support is not proof that a Visual Studio SDK-hosted session enables the same filesystem loader: an SDK host can configure hooks differently or not enable them.

No authoritative Visual Studio-specific evidence obtained during planning establishes the proposed hook home for these installed builds. That is a required Phase 0 verification gate, not a reason to report them as unsupported forever or automatically supported now.

## 2. Phase 0: verify real IDE behavior

Build an explicit, consented **Verify hook integration** workflow before offering normal installation for an unverified target. Passive discovery may identify candidates, but only an actual IDE-originated hook proves the integration.

### Visual Studio verification

1. Enumerate instances with the Visual Studio Setup Configuration API or bundled `vswhere -all -prerelease`, recording instance ID, installation path, version, and launchability. Avoid recursive scans of Program Files.
2. Locate each instance's Copilot host using known relative locations and installation metadata. A CLI binary, SDK package, or extension is a candidate capability, not a success result. Version probes must have bounded execution/output and explicit failure diagnostics.
3. Establish which Copilot experience is being tested: native agent chat, delegated/background CLI, or another harness. Do not claim support for inline completions, ordinary chat, or every agent mode from one successful test.
4. Start with `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli` as the candidate home. Verify whether the running IDE overrides `COPILOT_HOME`, chooses another user-data root, or registers SDK callbacks instead of loading hook files. Do not modify global environment variables or installed IDE binaries to force the desired result.
5. After consent, temporarily install a uniquely named, app-owned diagnostic hook only in the candidate location. Use the same bounded Relay/IPC path with a probe identifier; record event type, schema/version facts, and success/failure only. No prompt, tool arguments, transcript path, or raw payload logging.
6. Have the user perform a harmless agent action inside one identified Visual Studio instance, with the other instance closed or otherwise excluded from the test. Require a matching probe acknowledgement from that IDE workflow. Running the bundled CLI manually is not an equivalent test.
7. Verify event names, stdin shape, direct-exec support, timeout behavior, and required reload/new-session steps. A startup event alone proves neither tool events nor session termination.
8. Remove the diagnostic hook and restore only unchanged app-owned probe changes in a finally/recovery path. Record pending cleanup if interrupted.

Maintain a local compatibility record keyed by installation identity, IDE/host version, hook scope/home, and adapter schema. Invalidate or require re-verification when relevant components change. Report statuses such as **Not installed**, **Detected / verification required**, **Verified**, **Partially supported**, **Blocked by policy**, and **Verification failed**, with reasons.

If the IDE cannot expose supported filesystem hooks, leave that target disabled with an explanation. Do not patch an SDK, inspect private session storage, scrape logs, or claim CLI installation resolves it.

### Shared Visual Studio home

Both instances may use the same user hook directory. Canonicalize destinations and install one owned file per effective shared scope, not one duplicate file per instance.

If the hook payload supplies no trustworthy instance discriminator, report the source as **Visual Studio, shared hook scope**, not falsely attribute it to Main or IntPreview. Explain that enabling/disabling a shared scope affects all IDEs that load it; selecting one row cannot promise instance-level isolation that the host does not provide.

### VS Code verification

Identify installation/channel, selected profile/user-data root, enabled Copilot agent implementation, hook support, workspace trust, and applicable policy. Do not equate the presence of a `github.copilot*` directory with functional hooks.

Verify the effective `chat.hookFilesLocations` setting and actual supported schema against the installed build. Public docs list user locations but their shown default setting does not enumerate every listed location; treat runtime behavior as something to check, not resolve by assumption.

Use an explicit app-owned per-profile hook location and a consented profile settings entry where supported. Verify discovery through VS Code's Hooks customization UI and a harmless agent action. If organization policy disables hooks, report that state without changing policy.

## 3. Independent discovery and capability adapters

Replace the single `DiscoveryResult` CLI/home selection with a collection of target descriptors. Each should include:

- Stable target/scope ID and kind: CLI, Visual Studio, or VS Code.
- Display name, installation/profile identity, host version, and hook adapter version.
- Effective configuration scope/path, with provenance: documented, user-selected, or verified.
- Capability state, supported event set, and reasons/limitations.
- Shared-path grouping and current installed/owned state.

Keep standalone CLI discovery independent of Visual Studio. Installing Visual Studio must not silently redirect standalone CLI hooks away from its effective `COPILOT_HOME`.

Introduce testable provider adapters for discovery, capability verification, hook generation, and payload normalization. Share state and delivery code, but not an assumed universal IDE hook JSON schema.

Refresh should be read-only and bounded, honoring cancellation. Surface inaccessible/failed discovery separately from absence. Cache expensive version checks and invalidate on installation changes.

For VS Code, enumerate supported local profiles without rewriting profile registries. Allow explicit selection of a custom user-data/profile root when it cannot be discovered safely, then verify it. Initial scope excludes WSL, SSH, containers, browser sessions, and remote extension hosts: a local Windows Relay/pipe cannot execute inside those environments. Show that limitation prominently.

## 4. Hook configuration and payload adapters

### CLI and verified Visual Studio CLI-backed adapter

Preserve the working standalone format: app-owned JSON, supported camelCase event names, direct `exec` plus argument array, and bounded timeout. Reuse it for Visual Studio only after the live test proves that exact format and location.

Generate only explicitly supported events. Do not continue blindly enumerating `AgentEvent` for every host.

If a verified Visual Studio SDK-hosted format differs, add a dedicated adapter for that format rather than renaming the installation "CLI" and assuming equivalence.

### VS Code adapter

Use the documented `hooks` object and supported command properties, including `windows`/`command` and `timeout`. Do not emit CLI-only `exec`/`args` unless that installed VS Code implementation explicitly supports them.

Keep Relay the receiver. Use a tested Windows command-encoding helper that invokes the exact co-installed executable with trusted arguments for source adapter, target scope, event, and config path. Verify the actual shell selected by this VS Code build. Cover spaces, Unicode, quotes, and shell metacharacters in paths; never interpolate hook payload values into a command.

Use a wrapper only if required by the verified execution contract. Prefer no additional process beyond the unavoidable host shell and Relay; preserve the existing 2.2-second Relay budget and three-second hook timeout. Do not silently raise hook latency to accommodate an unnecessarily expensive wrapper.

Normalize `session_id`, ISO-8601 `timestamp`, `hook_event_name`, and documented per-event metadata separately from the CLI parser. Do not fall back to broad guessed aliases or parse text output to infer success/failure.

The public schema makes `session_id` optional. Require a stable ID for session reporting; if missing, safely skip with a bounded diagnostic and mark reduced capability. Do not invent a random ID on every event or use a transcript path as a surrogate. Enable full session support only once the targeted harness demonstrably supplies stable IDs.

### Initial event mapping

| VS Code event | Proposed normalized behavior | Limitation |
| --- | --- | --- |
| `SessionStart` | Session started; Waiting until prompt processing | Verify ordering with the first `UserPromptSubmit` |
| `UserPromptSubmit` | Executing | Ignore prompt content |
| `PreToolUse` | Executing | Does not independently prove the user is waiting for permission |
| `PostToolUse` | Continue Executing | Documented for successful completion; not a generic failure event |
| `Stop` | Execution stopped; return underlying state to Waiting | Not `SessionEnd`; not proof that the operation succeeded |
| `PreCompact` | No status transition initially | Compaction is not failure or session termination |
| `SubagentStart` / `SubagentStop` | Excluded from initial top-level tracking | Must not overwrite or end the parent's state |

Add an explicit normalized execution-stopped event/state transition if needed instead of mapping VS Code `Stop` to existing `AgentStop`, which currently produces a Succeeded overlay. The detailed VS Code reference explicitly says Stop does not mean the session ended or became inactive.

Do not promise exact permission-wait, tool-failure, success, or session-close detection where the verified event set cannot supply it. Show per-target capability limitations in Configurator and documentation. Do not infer failure from tool response strings, read transcripts, or assume CLI's `ask_user` tool name applies to VS Code.

A tray heartbeat indicates machine/reporting availability, not that an IDE session is still open. Without a supported session-end signal, preserve honest last-observed state and identify it as such; do not fabricate SessionEnd from elapsed time. Add no activity-expiry heuristic as part of this change without a separate product decision.

### Non-interference and privacy

Every observer hook must return promptly with success and no stdout control response. Never emit permission decisions, `continue: false`, stop-blocking output, or input modifications. Unknown/unsupported events should be diagnosed without blocking Copilot.

Keep raw stdin transient. Extract only stable identity, event, time, and allowlisted boolean/status facts. Never transmit or persist prompts, source, tool input/output, transcript paths, workspace paths, or raw error messages.

## 5. Avoid duplicate and cross-product execution

VS Code and CLI can discover overlapping user directories. A dedicated VS Code file alone does not ensure isolation if VS Code also loads the existing CLI-owned file.

Before enabling multiple targets:

1. Resolve effective loaders and canonical physical destinations, including settings inheritance and shared VS homes.
2. Prefer isolated app-owned files selected through supported host configuration. If needed, explicitly preview a supported exclusion of the conflicting app integration location; never disable unrelated user hooks or whole shared directories without identifying the impact.
3. If isolation requires changes that would disable unrelated hooks, block that configuration and explain the conflict, or implement a verified harness-aware routing adapter. Do not guess a harness from the parent executable or working directory.
4. Verify one IDE event produces exactly one normalized event with the correct source while CLI, both Visual Studio instances, and VS Code are configured.

Do not rely on new random event IDs for deduplication: duplicate hook invocations generate different IDs. Where the host provides a stable invocation ID, preserve a bounded deduplication key. Without one, fix duplicate registration/loading rather than deduplicating by timestamp and accidentally dropping legitimate events.

## 6. Source identity across IPC, state, protocol, and UI

Preserve one machine UUID, one tray process, and one machine heartbeat across all IDEs. Heartbeats belong to the reporter, not whichever IDE was most recently discovered.

Carry a source descriptor on normalized hooks and session snapshots:

- Source kind (`copilot-cli`, `visual-studio`, `vscode`).
- Stable local integration scope ID, with IDE/profile version as bounded metadata.
- Opaque host session ID.

Use a composite session identity `(source kind, scope ID, host session ID)` so identical session IDs across products/profiles cannot collide. Do not concatenate unbounded strings into the existing 128-character limit; use explicit validated fields or a deterministic bounded internal key while preserving safe display metadata.

Update all affected surfaces:

- Relay arguments and parser selection.
- `HookData`, `ClientIpcRequest`, IPC versioning/validation, and the tray coordinator's active integration allowlist.
- `SessionStore`, `SessionSnapshot`, reducer ordering, retirement, and heartbeat snapshots.
- Status/presence validation and Dashboard machine/session views.
- Integration/configuration persistence and migration.

Current v1 status and v2 presence enforce `client = copilot-cli` and match hook identity against the presence envelope. Separate reporting-agent metadata from originating IDE metadata in the new contract. Do not masquerade IDE events as CLI to satisfy old validation.

Recommended compatibility boundary: introduce source-aware presence v3 endpoints and capability detection, while retaining legacy v1/v2 readers/routes. Upgrade Dashboard before enabling multi-IDE reporting. Require the new capability in Configurator; do not silently send incompatible source fields to an older strict JSON receiver.

Map existing sessions to a legacy CLI scope during storage migration, preserving names, notes, Windows App mappings, receipt/generation ordering, result state, and machine identity. Version IPC and deploy Client/Relay/Configurator together. Older installed hooks without source arguments may remain supported as legacy CLI until their owned files are migrated.

In Dashboard, show the source per session and display mixed-client activity truthfully instead of letting the last report relabel the whole machine. Retain current cross-session priority and explicit/offline timeout behavior.

## 7. Multi-target Configurator and ownership

Replace the single detected-client summary with selectable target rows and expandable details showing:

- Installation/profile, exact hook scope, support/verification state, and event coverage.
- Shared-scope warnings, workspace override/policy limitations, and reload requirements.
- Installed versus proposed state and the exact files/settings to change.

Offer **Refresh**, **Verify integration**, **Preview**, **Apply/Repair**, and per-target **Remove**. Keep dashboard connectivity testing distinct from hook verification: a successful health request does not prove an IDE loaded a hook.

Use configuration v4 for a selected integration collection, with compatible v1-v3 reading. Preserve the current heartbeat setting, URL, startup ownership, and explicit Start Client behavior. Do not automatically start a stopped tray merely because Configurator was opened or a settings field was edited.

Replace the single-file manifest with a versioned collection of owned artifacts and target references. Migrate the prior `HookPath` record rather than orphaning it. Track shared artifact ownership once with references from each selected target.

For VS Code per-profile settings:

- Prefer an app-owned hook file outside the shared default CLI directory, referenced by a supported `chat.hookFilesLocations` entry.
- Preserve JSONC comments, formatting, unrelated properties, and inherited values using a real JSONC-aware editing mechanism; do not deserialize/rewrite the entire settings file as ordinary JSON.
- Do not change enterprise policies, workspace trust, repository hook files, or arbitrary extension enablement.
- Detect settings changes since preview; re-plan instead of overwriting concurrent VS Code edits.
- Removal restores only the owned location entry if it still matches the installed value. Keep subsequent user edits intact.
- Explain that Settings Sync/custom user-data/profile behavior can change the effective setting; verify on the selected profile rather than promising universal coverage.

Public docs state workspace hooks can take precedence over user hooks for an event. Per-user installation therefore cannot promise delivery in every workspace. Detect/report shadowing where possible and document how users can inspect effective hooks; do not modify repositories to bypass it.

Preview all file and settings operations together. Apply transactionally with backups and ownership/hash checks; failures roll back newly applied artifacts without changing unrelated hooks. If multi-target verification is incomplete, show per-target results and require a revised preview for a supported subset rather than silently skipping selected targets.

Removing one integration must retain shared tray startup, configuration, and other targets. Only full remote-integration removal should stop the reporter/remove its startup entry under existing ownership rules.

## 8. Servicing, documentation, and validation

Update installer/uninstall helpers to understand manifest collections and shared artifacts. Keep installed executable paths stable and include adapter/wrapper assets if required. An IDE upgrade does not authorize rewriting its installation directory. Discovery/repair should surface invalidated verification and removed profiles.

### Automated coverage

| Area | Cases |
| --- | --- |
| Discovery | Multiple VS instances; no bundled CLI; CLI present but hooks unsupported; custom install roots; failed version probe; standalone CLI remains independent |
| Profiles/scopes | Default/custom VS Code profile roots; disabled extensions; policy-disabled hooks; remote host rejected; shared VS home; overlapping CLI/VS Code loaders |
| Schema adapters | Exact CLI and VS Code fixtures; timestamp shapes; optional/missing IDs; malformed/oversized input; unsupported events; matching event declarations |
| Status semantics | VS Code Stop does not mean success/session close; session-ID collision across sources; heartbeat snapshots preserve source and state; mixed IDE sessions aggregate |
| Hook safety | Empty stdout; exit success on report failure; special-character path quoting; bounded execution; no prompt/transcript/tool data persisted |
| Integration | Multiple targets; shared artifacts installed once; old manifest migration; JSONC preservation; concurrent edits; partial failure rollback; per-target removal |
| Compatibility | Older dashboard refused for new integration; old hook arguments migrated safely; source-aware IPC version mismatch; old state migration |
| Lifecycle | Tray Exit stops all IDE reporting; hooks never restart it or bypass IPC; heartbeat cadence remains one per machine |
| Packaging | Client/Relay/Configurator versions match; assets installed; repair/upgrade/uninstall preserve unrelated IDE configuration |

Use synthetic payload fixtures and temporary profile/home directories. Do not copy real chat payloads into tests or invoke a user's actual hooks/settings as part of ordinary test execution.

### Manual release gates

1. Record exact installed Visual Studio instance and bundled-host versions and verify each candidate from inside its actual Copilot experience.
2. Confirm the effective Visual Studio hook home, not merely existence of the suggested directory. Record unsupported/partial instances accurately.
3. Verify VS Code hooks in the selected local profile with the documented configuration UI and harmless agent activity; confirm reload requirements.
4. Verify first prompt, tool execution, stop, and any other claimed supported event, including differences between ordinary agent execution and subagents.
5. Run CLI, Visual Studio, and VS Code simultaneously. Verify source attribution, distinct sessions, no duplicate reports, and one persistent tray process.
6. Test profile/workspace overrides, disabled hooks, and shared Visual Studio homes. Unsupported environments must produce an explanation rather than a false success.
7. Exit the tray during IDE activity; verify Offline and no resurrection from any IDE hook. Restart explicitly and verify normal reporting.
8. Remove one target; confirm other IDE hooks and heartbeat reporting remain. Test full uninstall/rollback without deleting unrelated hook files/settings.

Update `README.md`, `docs\MANUAL-TEST-PLAN.md`, `docs\ACCEPTANCE.md`, and `installers\README.md` with the verified version/scope matrix and event limitations. Replace unconditional Visual Studio support claims only when backed by the new verification evidence.

## Completion criteria

Configurator can independently discover, verify, preview, install, repair, and remove supported IDE hook integrations. Visual Studio support is enabled only after proving the actual IDE loader and location. Selected local VS Code profiles report through their correct schema. Sources coexist without duplicate hooks or colliding sessions, and all delivery remains controlled by the existing tray client.

Unverified or unsupported instances remain visibly disabled/limited; no code or documentation claims universal Visual Studio/VS Code hook coverage.

## References

Reviewed September 15, 2026; recheck preview documentation against the installed versions during implementation.

- [VS Code agent hook configuration, locations, policies, and common input](https://code.visualstudio.com/docs/agent-customization/hooks)
- [VS Code detailed hooks reference, including Stop semantics](https://code.visualstudio.com/docs/agents/reference/hooks-reference)
- [GitHub Copilot CLI hook formats, locations, and direct execution](https://docs.github.com/en/copilot/reference/hooks-reference)
- [Visual Studio installation discovery with vswhere](https://github.com/microsoft/vswhere)
- Local project evidence: `Discovery.cs`, `IntegrationManager.cs`, `RelayEngine.cs`, `ClientIpc.cs`, `ClientCoordinator.cs`, `Protocol.cs`, and `PresenceProtocol.cs`.
