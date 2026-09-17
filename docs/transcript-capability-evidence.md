# Stop-triggered transcript capability evidence (P0)

Reviewed 2026-09-16 using published web documentation only. No installed host,
private storage, real hook, conversation, SDK bridge, or live service was used.
This records documentation evidence, **not installed-host acceptance**.

## Production disposition

`VerifiedTranscriptFileAdapterRegistry.Production` is intentionally empty.
Every host currently returns `format-unverified`. Hook prompt/activity support
is independent and must remain available where already supported. No production
schema, storage root, filename alias, or format version is inferred from examples
or an extension such as `.jsonl`.

| Host/profile | Exact verified hook evidence | Reader disposition and missing proof |
| --- | --- | --- |
| Copilot CLI; no eligible host/format version | [GitHub hooks reference](https://docs.github.com/en/copilot/reference/hooks-reference), `agentStop` section: `sessionId: string`, `timestamp: number`, `cwd: string`, `transcriptPath: string`, `stopReason: "end_turn"`, `stop_hook_active: boolean`. `userPromptSubmitted.prompt` is hook-sourced text. | Unavailable. This hook reference does not establish a version-pinned UTF-8 independently framed append schema, complete main-assistant message fields, record/session binding, exact Windows current-user transcript root/filename mapping, or replacement/reset semantics. A supplied path is not that proof. |
| VS Code; no eligible host/format version | [Hooks reference](https://code.visualstudio.com/docs/agents/reference/hooks-reference), `Stop` section: `stop_hook_active: boolean`; [agent hooks common fields](https://code.visualstudio.com/docs/agent-customization/hooks): optional `session_id` and `transcript_path`, ISO timestamp and `hook_event_name`; `UserPromptSubmit.prompt`. | Unavailable. The common-field documentation explicitly says the transcript format is **not a stable hook API** and may change. No exact version, framing/encoding, completed assistant field set, native record/session identity, current-user root/file mapping, or append/reset guarantee is established by these pages. CLI compatibility is not assumed. |
| Visual Studio; no eligible host/format version | [Visual Studio agent mode](https://learn.microsoft.com/en-us/visualstudio/ide/copilot-agent-mode?view=visualstudio) describes agent mode (prerequisite VS 2022 17.14+), not a passive transcript-file contract. Page metadata identifies commit `76f5eb0389edceb290d49c99dead3f5101415664`, updated 2026-07-30. | Unavailable. Independently documented IDE-originated stop reference, format version, framing, session/completion fields, exact path mapping and reset behavior are all unverified. Repository CLI-shaped adapters or a bundled CLI are not evidence of IDE equivalence. |

The two evolving hook-reference sites do not identify an immutable host build or
file-format schema version in the reviewed sections. Therefore **none is labeled
a verified production file profile**. This is a bounded review of the cited public
evidence, not a claim that no additional implementation evidence could exist.
A future profile needs authoritative version-pinned implementation/schema evidence
for every missing property above before registration, with independent fixtures.

## Important event distinctions

- CLI `subagentStop.response` is the full **subagent** final response; it is not a
  main-assistant reply. The documented subagent hook also supplies a path, which
  must not be substituted for `agentStop.transcriptPath`.
- CLI's PascalCase compatibility payload uses `Stop.transcript_path`; that is
  not proof of VS Code file-format compatibility.
- VS Code's reference explicitly says `Stop` means the current execution stopped,
  not that the session ended or became inactive. Custom-agent-scoped Stop may be
  treated as SubagentStop. Do not claim final-turn/session-complete semantics.
- Tool input/results, raw errors, reasoning and arbitrary text-bearing fields are
  excluded, regardless of their presence in public examples.
- Observer hooks must return success without stdout decisions or control output.
  No new real hook registrations were performed.

## Synthetic framework profile

Only the test assembly contains `SyntheticTranscriptFileAdapter`. Its profile is
`test-only-utf8-lf-v1`, its source version is `test-only-1`, and its root is an
isolated test-owned directory under the repository's test output. Files are named
exactly `<session>.synthetic`, UTF-8 without BOM, one independently complete JSON
object followed by LF. The test grammar has exact `session`, `kind`, `complete`,
`text`, and `id` properties; no aliases or unknown/duplicate properties.
Only `kind: "assistant"` with `complete: true` yields text. User, tool, reasoning,
subagent, system, attachment and delta test records are ignored.

This invented grammar intentionally verifies the framework, **never a host**.
The production framework selects adapters from source kind/scope/exact version,
validates the supplied path and opened Windows handle, establishes an EOF baseline,
then consumes only appended complete frames. It has no first-turn native-ID proof
adapter, so first-stop history is always skipped with `baseline-established`.
An EOF inside a frame causes that pre-baseline frame to be discarded on completion.
Adapter code is responsible for strict schema/depth-16/session/completion checking;
the framework independently enforces encoding, framing, file/record/output/byte/time
limits, cursor identity and cancellation. Changed schemas rebaseline with a gap.

No host profile currently meets the production end-to-end file-capture gate.
Synthetic tests may verify the reader and its integration, but cannot upgrade
that capability statement. Actual-host, live-tunnel, installer-execution and
manual-UI acceptance remain unperformed.

## Automated evidence

Release x64 synthetic reader/profile tests cover Windows relative-handle path
validation, ancestor junction and hard-link rejection, current-user-owned inputs,
read-only sharing/deletion, first/partial baselines, append-only extraction and
offset deduplication, schema/session/encoding gaps, replacement/truncation,
all read/output/record/context/queue limits, fixed retry scheduling, delayed
flush, shared scratch admission/release, expiry, reset/revision/readiness and Exit.
These tests create and remove only their own synthetic inputs; they do not
change another user's file owner or exercise installed-host directories.
