# Windows App Existing Session Reuse Implementation Plan

## Implementation and release status

Implemented in the Dashboard launcher/controller and the injectable
`WindowsAppWindowPlatform`, with matcher, platform, orchestration, and presentation
coverage in `WindowsAppLauncherTests` and `WindowsAppConnectionControllerTests`.
The native boundary additionally exposes `IsWindow` to distinguish disappearing
handles from live activation failures. Titles are capped at 1,024 characters.
Accepted separators are whitespace, colon, parentheses, brackets, en/em dashes,
and hyphens only when the outer side is whitespace or a title edge. A hyphen within
a larger resource token is not a boundary.

Manual title-format capture and foreground verification on minimum/current
supported Windows App versions remain **Not run** release gates (E38-E46 in
`docs\MANUAL-TEST-PLAN.md`). Automated fake-backed fixtures do not establish which
title formats those installed versions actually produce.

## Goal

Change both Windows App launch paths so Dashboard first enumerates top-level
desktop windows and looks for an existing Windows App connection whose window
title identifies the mapped Dev Box. If a matching window exists, restore it when
minimized and bring it to the foreground instead of activating the
`ms-cloudpc:connect` URI again.

If no matching window exists, preserve the current behavior:

- a normal launch resolves and atomically persists a fresh validated connection
  URI before shell activation;
- **Open last known connection** validates and activates the saved URI without
  Azure CLI or network access;
- no fallback client is introduced.

This plan supersedes the no-enumeration/no-reuse restriction in
`docs\windows-app-connection-migration-plan.md`. Its validation, persistence,
privacy, cancellation, and explicit cached-launch requirements otherwise remain
in force.

## Required behavior

For a mapped Dev Box named `DevBoxName`:

1. Validate the stored mapping sufficiently to obtain its trusted Dev Box name.
2. Before protocol checks, Azure CLI calls, network access, cached-URI use, or
   `Process.Start`, enumerate the current interactive desktop's
   top-level windows in z-order.
3. Read each candidate's title without logging or displaying unrelated titles.
4. Match the title against the validated `DevBoxName`, case-insensitively, using
   title-token boundaries so names such as `box1` and `box10` cannot collide.
5. Use the first matching window in z-order.
6. If it is minimized, restore it.
7. Bring it to the foreground.
8. Return a reuse result without activating the connection URI.
9. If no window matches, continue the selected fresh or cached launch path exactly
   as today.

Window enumeration is best effort only when no candidate is found. Expected
inspection failures for individual windows, including a disappearing window,
must skip that candidate and continue. A successfully matched window is different:
if Dashboard cannot restore or foreground it, report a sanitized activation
failure and do not launch a duplicate session.

The feature must not:

- terminate, resize, reposition, or otherwise manage Windows App;
- inspect child-window text, automation trees, remote-session contents, command
  lines, or connection URIs;
- persist window handles, process IDs, titles, or reuse state;
- expose enumerated titles in logs, exceptions, diagnostics, or UI;
- infer a Dev Box from the Agent Signaler machine name or custom display name;
- reuse a title that only contains the Dev Box name as part of a larger token;
- search other Windows sessions or desktops.

## Title matching contract

Add a small pure matcher with focused tests rather than embedding string rules in
the Win32 enumeration callback.

The matcher receives a non-empty top-level window title and the already validated
`WindowsAppConnection.DevBoxName`. It uses ordinal case-insensitive comparison and
accepts the Dev Box name only at title boundaries:

- start or end of the title; or
- adjacent to whitespace or title punctuation used by Windows App, such as
  `-`, en dash, em dash, colon, parentheses, or brackets.

Both sides of the match must satisfy a boundary. This permits titles such as
`devbox-name - Windows App` while rejecting `devbox-name-2` and
`prefixdevbox-name`.

Keep the accepted separator set explicit and tested. Before implementation is
released, capture the title formats produced by every supported Windows App
version and update the matcher fixtures if the product uses a narrower stable
format. Do not broaden matching to arbitrary substring or fuzzy comparison.

## Platform design

Extend `IWindowsAppPlatform` so launch orchestration can perform an early reuse
probe and can atomically recheck immediately before shell activation:

```csharp
internal enum WindowsAppActivationDisposition
{
    NoExistingWindow,
    ExistingWindowActivated,
    ConnectionUriActivated
}

internal interface IWindowsAppPlatform
{
    bool IsProtocolAvailable();
    WindowsAppActivationDisposition TryActivateExisting(string devBoxName);
    WindowsAppActivationDisposition Activate(
        Uri connectionUri,
        string devBoxName);
}
```

`TryActivateExisting` enumerates and focuses only; it never invokes the protocol.
Rename the current `Launch` implementation to `Activate`; it repeats the reuse
check while holding the process-wide activation lock and shell-activates the URI
only when no match exists. The second check closes the race between the initial
probe and a potentially slow fresh resolution.

Protocol registration is required only after the early reuse probe reports no
match. An already connected window can therefore be restored while Azure CLI,
the Dev Box API, or the protocol association is unavailable.

Implement the native window work behind an injectable boundary in
`WindowsAppPlatform.cs`, for example:

```csharp
internal interface ITopLevelWindowPlatform
{
    IReadOnlyList<nint> Enumerate();
    string? GetTitle(nint window);
    bool IsMinimized(nint window);
    bool Restore(nint window);
    bool BringToForeground(nint window);
}
```

The production implementation should use bounded User32 calls:

- `EnumWindows`, filtering each top-level window by its owning process via
  `GetWindowThreadProcessId` and accepting only `msrdc.exe`;
- `GetWindowTextLengthW` and `GetWindowTextW`, with a fixed maximum accepted title
  length and no unbounded allocation;
- `IsIconic`;
- `ShowWindowAsync(..., SW_RESTORE)` so Dashboard does not block on another
  process's UI thread;
- `SetForegroundWindow`.

Add the declarations to `WindowsAppPlatform.cs` or a dedicated
`WindowsAppWindowPlatform.cs`; do not mix this external-window behavior into
`NativeWindow`, which currently owns Dashboard's own window and tray integration.
Keep delegates rooted for the duration of `EnumWindows` and contain exceptions at
the managed callback boundary.

`EnumWindows` already returns windows in top-to-bottom z-order. Stop after the
first valid title match. Ignore untitled windows and handles that become invalid
during inspection.

Run enumeration, restoration, foreground activation, and shell activation on the
existing background launch path, never on the WinUI thread.

## Launcher and controller integration

Update `WindowsAppLauncher` to pass `mapping.DevBoxName` to the platform for both
launch variants.

Normal launch ordering becomes:

1. validate the mapping;
2. try to restore and foreground an existing matching window;
3. if reused, return success without checking the protocol, resolving, or
   persisting;
4. otherwise require the `ms-cloudpc` protocol;
5. resolve a fresh connection;
6. validate identity and URI;
7. atomically persist the refreshed mapping;
8. recheck for a matching window and otherwise shell-activate the URI.

Cached launch ordering becomes:

1. validate the mapping and cached URI;
2. try to restore and foreground an existing matching window;
3. if reused, return success without checking the protocol;
4. otherwise require the protocol;
5. recheck for a matching window and otherwise shell-activate the cached URI.

Return the activation disposition from the launcher so
`WindowsAppConnectionController` can present accurate success text:

- `Brought the existing Windows App connection to the foreground.`
- `Opened in Windows App.`
- `Brought the existing Windows App connection to the foreground without refreshing.`
- `Opened the last known connection in Windows App without refreshing.`

Do not change busy-state, cancellation, compact-window restoration, or error
presentation. A reused compact launch is a success and must not restore the
Dashboard.

## Concurrency and race handling

Keep the existing per-machine `WindowsAppOperationGate`. Add one process-wide
activation lock around enumeration plus either foreground activation or
`Process.Start`. This prevents two Dashboard operations for different local
machines mapped to the same Dev Box from simultaneously observing no window and
both launching.

Do not hold the process-wide lock across Azure CLI, network, or persistence work.
The early probe provides the fast path; `Activate` acquires the lock and repeats
enumeration immediately before `Process.Start`. Do not wait for a newly launched
Windows App window to appear. The lock protects only decisions made inside this
Dashboard process; Windows App remains responsible for handling repeated protocol
activation from other processes.

If the selected handle disappears after matching, continue enumeration from the
remaining captured candidates. If every matched candidate disappears, perform the
normal URI activation. If a live matched window rejects restore or foreground
activation, return `WindowsAppFailure.ActivationFailed` rather than launching a
second connection.

## Failure and privacy rules

Map expected Win32 failures from enumeration, title retrieval, restoration,
foreground activation, and shell activation to existing sanitized product
failures. Preserve programming errors rather than broadly catching every
exception.

No error may contain:

- another application's window title;
- the Dev Box name;
- a process ID or window handle;
- the connection URI;
- raw Win32 exception text.

Failure must continue to release the operation gate and activation lock. A fresh
normal launch that persisted its refreshed URI before activation failure retains
that committed refresh, matching current behavior.

## Automated tests

Extend `WindowsAppLauncherTests.cs` and the linked Dashboard source list in
`AgentSignaler.Integration.Tests.csproj` if a new source file is added.

Cover the pure title matcher:

- exact title and supported prefix/suffix title formats;
- ordinal case-insensitive matching;
- every accepted boundary character;
- leading/trailing whitespace;
- empty and over-limit titles;
- partial-prefix, partial-suffix, and larger-token rejection;
- `box1` versus `box10`;
- punctuation within a valid Dev Box resource name.

Cover the platform with an injected fake top-level-window boundary:

- matching existing window prevents `Process.Start`;
- first matching z-order window wins;
- minimized match is restored before foreground activation;
- non-minimized match is not restored;
- untitled, nonmatching, inaccessible, and disappearing windows are skipped;
- a disappearing matched handle falls through to another match or URI activation;
- live matched-window restore/foreground failure is sanitized and never launches;
- no match preserves the exact original URI shell activation and immediate process
  disposal;
- expected enumeration failures do not prevent URI activation when no candidate
  was identified;
- activation lock serializes concurrent decisions.

Extend launcher/controller tests:

- normal reuse performs no protocol probe, resolution, persistence, or shell
  activation;
- cached reuse never resolves or persists;
- cached reuse performs no protocol probe or shell activation;
- both paths pass the mapped Dev Box name, not machine/display names;
- a no-match normal launch preserves resolve, persist, then activation ordering;
- the pre-shell recheck can reuse a window that appeared during resolution;
- success messages distinguish reuse from URI activation;
- compact reuse remains hidden and does not restore details;
- all failure paths release per-machine and process-wide gates;
- no title, Dev Box name, handle, PID, or URI appears in exceptions or state text.

Targeted verification:

```powershell
dotnet test tests\AgentSignaler.Integration.Tests\AgentSignaler.Integration.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~WindowsAppLauncherTests|FullyQualifiedName~WindowsAppConnectionControllerTests"
dotnet build src\AgentSignaler.Dashboard\AgentSignaler.Dashboard.csproj -p:Platform=x64
```

## Manual acceptance

Add cases to `docs\MANUAL-TEST-PLAN.md` using a disposable mapped Dev Box:

1. With no connection window open, launch from details and compact mode; Windows
   App opens normally.
2. With a connected non-minimized Dev Box window behind other applications,
   launch again; the existing window comes to the foreground and no duplicate
   connection opens.
3. Minimize the connected window and launch again; it restores and comes to the
   foreground.
4. Open different Dev Boxes whose names share prefixes; each action selects only
   the exact mapped Dev Box.
5. Open multiple matching windows and confirm the topmost matching window is used.
6. Repeat through **Open last known connection** while offline; reuse occurs
   without Azure CLI or network access.
7. Verify normal reuse does not invoke Azure CLI or change the saved connection
   retrieval time.
8. Force a foreground-activation failure with a controlled fake; Dashboard reports
   a sanitized failure and does not open a duplicate.
9. Inspect logs and UI to confirm unrelated window titles and connection data are
   absent.

## Documentation updates during implementation

Update:

- `README.md` to replace “Existing-session behavior is delegated to Windows App;
  Dashboard does not inspect or focus its windows” with the bounded title-based
  reuse behavior and its exact-match limitation;
- `docs\MANUAL-TEST-PLAN.md` with the acceptance cases above;
- `docs\windows-app-connection-migration-plan.md` with a short pointer stating
  that this plan supersedes its no-enumeration/no-focus restriction.

## Execution phases

### Phase 1 - matching and native abstraction

1. Add the pure title matcher.
2. Add the injectable top-level-window platform and bounded User32 implementation.
3. Add matcher and native-boundary tests.

### Phase 2 - activation behavior

1. Add `TryActivateExisting` and change `IWindowsAppPlatform.Launch` to
   disposition-returning `Activate`.
2. Add process-wide serialization.
3. Reuse, restore, and foreground matching windows before expensive launch work,
   then recheck before shell activation.
4. Preserve sanitized failure handling and exact URI activation.
5. Run targeted launcher tests.

### Phase 3 - orchestration and presentation

1. Pass the validated mapped Dev Box name through fresh and cached launch paths.
2. Return activation disposition through `WindowsAppLauncher`.
3. Update controller success messages without changing error or compact behavior.
4. Extend controller and concurrency tests.

### Phase 4 - documentation and release validation

1. Update README and the superseded migration contract.
2. Add manual acceptance cases.
3. Run targeted integration tests and the Dashboard x64 build.
4. Manually validate supported Windows App title formats and foreground behavior
   on the minimum and current supported Windows App versions.
