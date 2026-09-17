using System.Collections.Immutable;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class CopilotsControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid MachineId = Guid.Parse("ab555555-1111-2222-3333-444444444444");
    private static readonly SourceDescriptor Cli = new("copilot-cli", "scope", "1");

    [Fact]
    public async Task RenamingKeepsSelectionAndTranscriptIdentityAndRetainedRowsFallBackToIds()
    {
        var reader = new MetadataReader();
        var selection = reader.Add(Cli, "active");
        reader.Add(Cli, "retained", closed: true);
        using var controller = new CopilotsController(reader);
        var session = Session(Cli, "active", AgentState.Waiting, AgentEvent.PermissionRequest);
        controller.SetMachine(Machine(session));
        await controller.ShowAsync();
        var original = controller.State.Rows.Single(row => row.SessionId == "active");
        controller.Select(original.Key);
        controller.SetMachine(Machine(session with { DisplayName = "Friendly name" }));
        var renamed = controller.State.Rows.Single(row => row.SessionId == "active");
        Assert.Equal(original.Key, renamed.Key);
        Assert.Equal(original.Key, controller.State.SelectedKey);
        Assert.Equal("Friendly name", renamed.DisplayName);
        Assert.Contains("session Friendly name", renamed.Heading);
        Assert.Equal("Session ID: active", renamed.Identity);
        Assert.Equal(selection, Assert.Single(renamed.Streams).Selection);
        Assert.Equal("retained", controller.State.Rows.Single(row => row.SessionId == "retained").DisplayName);
        Assert.Equal(0, reader.EventReads);
        await controller.RefreshAsync();
    }

    [Fact]
    public async Task DisabledTranscriptsLeaveDistinctStatusRowsAndWaitingCountAvailableWithoutBodyReads()
    {
        var reader = new MetadataReader { Availability = TranscriptAvailability.Disabled };
        reader.Add(Cli, "old", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine(
            Session(Cli, "same", AgentState.Waiting, AgentEvent.PermissionRequest),
            Session(new("vscode", "scope"), "same", AgentState.Executing, AgentEvent.PreToolUse),
            Session(new("copilot-cli", "other"), "same", AgentState.Failed, AgentEvent.ErrorOccurred)));
        await controller.ShowAsync();
        Assert.Equal(3, controller.State.Rows.Length);
        Assert.Equal(3, controller.State.Rows.Select(row => row.Key).Distinct().Count());
        Assert.Contains("1 waiting for input", controller.State.Message);
        Assert.Contains("disabled", controller.State.Message);
        var waiting = Assert.Single(controller.State.Rows, row => row.Session?.State == AgentState.Waiting);
        Assert.Equal("Permission requested", waiting.LastEvent);
        Assert.Contains(Now.ToString("O"), waiting.EventTime);
        Assert.Equal(0, reader.EventReads);
    }

    [Fact]
    public async Task WaitingRowRetainsSeparateResultInformationAndDoesNotBorrowMachineEvent()
    {
        using var controller = new CopilotsController(null);
        controller.SetMachine(Machine(Session(Cli, "waiting", AgentState.Waiting, null) with
        {
            AwaitingUserInput = true, ResultState = AgentState.Failed, ResultUntilUtc = DateTimeOffset.MaxValue
        }) with { LatestEvent = AgentEvent.PreToolUse, LatestEventUtc = Now });
        await controller.ShowAsync();
        var row = Assert.Single(controller.State.Rows);
        Assert.Contains("Waiting for input", row.Status);
        Assert.Contains("last result Failed", row.Status);
        Assert.Equal("Last event unavailable", row.LastEvent);
        Assert.Equal("Event time unavailable", row.EventTime);
    }

    [Fact]
    public async Task JoinsSourceSessionIgnoringVersionAndKeepsStreamsSeparateIncludingRetainedOnlyRows()
    {
        var reader = new MetadataReader();
        reader.Add(Cli with { Version = "2" }, "same");
        reader.Add(Cli, "same", closed: true);
        reader.Add(new("vscode", "scope"), "same", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine(Session(Cli, "same", AgentState.Waiting, AgentEvent.PermissionRequest)));
        await controller.ShowAsync();
        Assert.Equal(2, controller.State.Rows.Length);
        var active = Assert.Single(controller.State.Rows, row => !row.RetainedOnly);
        Assert.Equal(2, active.Streams.Length);
        Assert.Equal(2, active.Streams.Select(stream => stream.Selection.StreamId).Distinct().Count());
        var retained = Assert.Single(controller.State.Rows, row => row.RetainedOnly);
        Assert.Equal("Last event unavailable", retained.LastEvent);
        Assert.Contains("not connected", retained.Status);
        Assert.Contains("ended/closed", retained.TranscriptStatus);
        Assert.Equal(0, reader.EventReads);
    }

    [Fact]
    public async Task VersionAndAnotherSessionsStateChangesPreserveStableSelectionAndSortOrder()
    {
        using var controller = new CopilotsController(null);
        var a = Session(Cli, "A", AgentState.Waiting, AgentEvent.PermissionRequest);
        var b = Session(Cli, "B", AgentState.Waiting, AgentEvent.PermissionRequest);
        controller.SetMachine(Machine(b, a));
        await controller.ShowAsync();
        var order = controller.State.Rows.Select(row => row.Key).ToArray();
        controller.Select(order[1]);
        controller.SetMachine(Machine(a with { Source = Cli with { Version = "2" } },
            b with { UnderlyingState = AgentState.Executing, LatestEvent = AgentEvent.PreToolUse }));
        Assert.Equal(order, controller.State.Rows.Select(row => row.Key));
        Assert.Equal(order[1], controller.State.SelectedKey);
        Assert.Contains("1 waiting for input", controller.State.Message);
        Assert.Equal("Tool started", controller.State.Rows[1].LastEvent);
        controller.Hide();
        await controller.ShowAsync();
        Assert.Equal(order[1], controller.State.SelectedKey);
    }

    [Fact]
    public async Task OfflineAndEndedRowsDoNotCountAsConnectedAndLegacyEventIsUnknown()
    {
        using var controller = new CopilotsController(null);
        controller.SetMachine(Machine(
            Session(Cli, "old", AgentState.Waiting, null),
            Session(Cli, "ended", AgentState.Idle, AgentEvent.SessionEnd)) with { State = AgentState.Offline });
        await controller.ShowAsync();
        Assert.Contains("0 connected", controller.State.Message);
        Assert.Contains("0 waiting", controller.State.Message);
        Assert.All(controller.State.Rows, row => Assert.False(row.Session!.IsConnected));
        Assert.Equal("Last event unavailable", controller.State.Rows.Single(row => row.SessionId == "old").LastEvent);
        Assert.True(controller.State.Rows.Single(row => row.SessionId == "ended").RetainedOnly);
    }

    [Fact]
    public async Task MetadataIncludesTheSecondBoundedPageWithoutAnyTranscriptReads()
    {
        var reader = new MetadataReader();
        for (var i = 0; i < 32; i++) reader.Add(Cli, $"session-{i:00}", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine());
        await controller.ShowAsync();
        Assert.Equal(32, controller.State.Rows.Length);
        Assert.Equal(2, reader.ListReads);
        Assert.Equal(0, reader.EventReads);
        Assert.All(controller.State.Rows, row => Assert.True(row.RetainedOnly));
    }

    [Theory]
    [InlineData(TranscriptResetReason.Cleared)]
    [InlineData(TranscriptResetReason.Disabled)]
    [InlineData(TranscriptResetReason.Expired)]
    [InlineData(TranscriptResetReason.Removed)]
    public async Task InvalidationImmediatelyRemovesRetainedMetadataWithoutRemovingStatus(TranscriptResetReason reason)
    {
        var reader = new MetadataReader();
        reader.Add(Cli, "retained", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine(Session(Cli, "active", AgentState.Waiting, AgentEvent.PermissionRequest)));
        await controller.ShowAsync();
        Assert.Equal(2, controller.State.Rows.Length);
        reader.Invalidate(reason);
        Assert.DoesNotContain(controller.State.Rows, row => row.SessionId == "retained");
        Assert.Equal(reason == TranscriptResetReason.Removed ? 0 : 1, controller.State.Rows.Length);
    }

    [Theory]
    [InlineData(TranscriptResetReason.Expired)]
    [InlineData(TranscriptResetReason.Evicted)]
    public async Task ScopedInvalidationPreservesUnrelatedRowsStreamsAndRetainedSelection(TranscriptResetReason reason)
    {
        var reader = new MetadataReader();
        var affected = reader.Add(Cli, "same", closed: true);
        var sameSessionStream = reader.Add(Cli, "same", closed: true);
        reader.Add(Cli with { ScopeId = "other-scope" }, "same", closed: true);
        reader.Add(new("vscode", Cli.ScopeId), "same", closed: true);
        reader.Add(Cli, "B", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine());
        await controller.ShowAsync();
        var selectedKey = controller.State.Rows.Single(row => row.SessionId == "B").Key;
        controller.Select(selectedKey);
        var states = new System.Collections.Concurrent.ConcurrentQueue<CopilotsState>();
        controller.Changed += () => states.Enqueue(controller.State);

        reader.Invalidate(reason, affected with { Source = Cli with { Version = "2" } });
        Assert.Equal(selectedKey, controller.State.SelectedKey);
        await controller.RefreshAsync();

        Assert.Equal(4, controller.State.Rows.Length);
        var survivingStream = Assert.Single(controller.State.Rows.Single(row => row.Key ==
            SourceIdentity.SessionKey(Cli, "same")).Streams);
        Assert.Equal(sameSessionStream.StreamId, survivingStream.Selection.StreamId);
        Assert.All(states, state =>
        {
            Assert.Equal(selectedKey, state.SelectedKey);
            Assert.Equal(4, state.Rows.Length);
        });
        Assert.DoesNotContain(controller.State.Rows.SelectMany(row => row.Streams),
            info => info.Selection.StreamId == affected.StreamId);
        Assert.True(reader.ListReads >= 2);
        Assert.Equal(0, reader.EventReads);
    }

    [Fact]
    public async Task ScopedInvalidationCancelsLateMetadataWithoutLosingBackNavigationSelection()
    {
        var reader = new MetadataReader();
        var affected = reader.Add(Cli, "A", closed: true);
        reader.Add(Cli, "B", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine());
        await controller.ShowAsync();
        var selectedKey = controller.State.Rows.Single(row => row.SessionId == "B").Key;
        controller.Select(selectedKey);
        reader.Hold = true;
        var blocked = controller.RefreshAsync();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        reader.Invalidate(TranscriptResetReason.Expired, affected);
        Assert.True(reader.HeldToken.IsCancellationRequested);
        Assert.Equal(selectedKey, controller.State.SelectedKey);
        Assert.Contains(controller.State.Rows, row => row.SessionId == "B");
        reader.Release.TrySetResult();
        await Task.WhenAll(blocked, controller.RefreshAsync());

        Assert.Equal(selectedKey, controller.State.SelectedKey);
        Assert.Equal("B", Assert.Single(controller.State.Rows).SessionId);
        Assert.Equal(0, reader.EventReads);
    }

    [Fact]
    public async Task FailedScopedRefreshPreservesUnaffectedRetainedSelection()
    {
        var reader = new MetadataReader();
        var affected = reader.Add(Cli, "A", closed: true);
        reader.Add(Cli, "B", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine());
        await controller.ShowAsync();
        var selectedKey = controller.State.Rows.Single(row => row.SessionId == "B").Key;
        controller.Select(selectedKey);
        reader.Fail = true;
        reader.Invalidate(TranscriptResetReason.Evicted, affected);
        await controller.RefreshAsync();
        Assert.Equal(selectedKey, controller.State.SelectedKey);
        Assert.Contains(controller.State.Rows, row => row.SessionId == "B");
        Assert.Contains("last-known metadata", controller.State.Message);
        Assert.Equal(0, reader.EventReads);
    }

    [Theory]
    [InlineData(TranscriptResetReason.Expired)]
    [InlineData(TranscriptResetReason.Evicted)]
    public async Task PartialSelectedStreamInvalidationPreservesSelectionUntilMetadataConfirmsRetention(TranscriptResetReason reason)
    {
        var reader = new MetadataReader();
        var selected = reader.Add(Cli, "selected", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine());
        await controller.ShowAsync();
        var selectedKey = Assert.Single(controller.State.Rows).Key;
        controller.Select(selectedKey);
        var states = new System.Collections.Concurrent.ConcurrentQueue<CopilotsState>();
        controller.Changed += () => states.Enqueue(controller.State);
        reader.Hold = true;
        reader.Invalidate(reason, selected, stillRetained: true);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(selectedKey, controller.State.SelectedKey);
        Assert.Single(controller.State.Rows);
        reader.Release.TrySetResult();
        await controller.RefreshAsync();
        Assert.All(states, state =>
        {
            Assert.Equal(selectedKey, state.SelectedKey);
            Assert.Single(state.Rows);
        });
        var stream = Assert.Single(Assert.Single(controller.State.Rows).Streams);
        Assert.Equal(selected.StreamId, stream.Selection.StreamId);
        Assert.Equal(new TranscriptRetainedRange(2, 3), stream.RetainedRange);
        Assert.Equal(0, reader.EventReads);
    }

    [Theory]
    [InlineData(TranscriptResetReason.Expired)]
    [InlineData(TranscriptResetReason.Evicted)]
    public async Task SelectedStreamIsRemovedOnlyAfterMetadataConfirmsNothingRemains(TranscriptResetReason reason)
    {
        var reader = new MetadataReader();
        var selected = reader.Add(Cli, "selected", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine());
        await controller.ShowAsync();
        var key = Assert.Single(controller.State.Rows).Key;
        controller.Select(key);
        reader.Hold = true;
        reader.Invalidate(reason, selected);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(key, controller.State.SelectedKey);
        reader.Release.TrySetResult();
        await controller.RefreshAsync();
        Assert.Empty(controller.State.Rows);
        Assert.Null(controller.State.SelectedKey);
    }

    [Theory]
    [InlineData(TranscriptResetReason.Cleared)]
    [InlineData(TranscriptResetReason.Disabled)]
    [InlineData(TranscriptResetReason.Removed)]
    public async Task ExplicitSelectedStreamPurgeImmediatelyRemovesMetadataAndSelection(TranscriptResetReason reason)
    {
        var reader = new MetadataReader();
        var selected = reader.Add(Cli, "selected", closed: true);
        reader.Add(Cli, "unaffected", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine());
        await controller.ShowAsync();
        controller.Select(controller.State.Rows.Single(row => row.SessionId == "selected").Key);
        reader.Hold = true;
        reader.Invalidate(reason, selected);
        Assert.Null(controller.State.SelectedKey);
        Assert.Equal("unaffected", Assert.Single(controller.State.Rows).SessionId);
        reader.Release.TrySetResult();
        await controller.RefreshAsync();
        Assert.Equal("unaffected", Assert.Single(controller.State.Rows).SessionId);
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("remove")]
    [InlineData("dispose")]
    [InlineData("invalidate")]
    public async Task LifecycleCancelsPendingMetadataAndRejectsLateCompletion(string action)
    {
        var reader = new MetadataReader { Hold = true };
        reader.Add(Cli, "retained", closed: true);
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine());
        var pending = controller.ShowAsync();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (action)
        {
            case "hide": controller.Hide(); break;
            case "remove": controller.SetMachine(null); break;
            case "dispose": controller.Dispose(); break;
            default: reader.Invalidate(TranscriptResetReason.Cleared); break;
        }
        Assert.True(reader.HeldToken.IsCancellationRequested);
        reader.Release.TrySetResult();
        await pending;
        Assert.Empty(controller.State.Rows);
        Assert.Equal(0, reader.EventReads);
    }

    [Fact]
    public async Task FailedMetadataIsSanitizedAndDoesNotHideLiveStatus()
    {
        var reader = new MetadataReader { Fail = true };
        using var controller = new CopilotsController(reader);
        controller.SetMachine(Machine(Session(Cli, "active", AgentState.Waiting, AgentEvent.PermissionRequest)));
        await controller.ShowAsync();
        Assert.Single(controller.State.Rows);
        Assert.Contains("metadata unavailable", controller.State.Message);
        Assert.DoesNotContain("FORBIDDEN", controller.State.Message);
    }

    private static SessionSnapshot Session(SourceDescriptor source, string id, AgentState state, AgentEvent? latest) => new()
    {
        Source = source, SessionId = id, UnderlyingState = state, LatestEvent = latest,
        LatestEventAtUtc = latest is null ? null : Now, UpdatedAtUtc = Now
    };
    private static MachineView Machine(params SessionSnapshot[] sessions) =>
        new(MachineId, "synthetic", null, "copilot-cli", "1", AgentState.Waiting, null, Now, sessions);

    private sealed class MetadataReader : ITranscriptReader
    {
        private readonly List<TranscriptSessionInfo> _sessions = [];
        private readonly Guid _epoch = Guid.NewGuid();
        public TranscriptAvailability Availability { get; init; } = TranscriptAvailability.Partial;
        public bool Hold { get; set; }
        public bool Fail { get; set; }
        public int ListReads { get; private set; }
        public int EventReads { get; private set; }
        public CancellationToken HeldToken { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<TranscriptInvalidation>? Invalidated;
        public TranscriptSelection Add(SourceDescriptor source, string session, bool closed = false)
        {
            var selection = new TranscriptSelection(MachineId, source, session, Guid.NewGuid());
            _sessions.Add(new(selection, Now, closed, new(1, 3), false, false));
            return selection;
        }
        public void Invalidate(TranscriptResetReason reason, TranscriptSelection? selection = null, bool stillRetained = false)
        {
            if (selection is null) _sessions.Clear();
            else if (stillRetained)
            {
                var index = _sessions.FindIndex(info => TranscriptViewController.SameSelection(info.Selection, selection));
                _sessions[index] = _sessions[index] with { RetainedRange = new(2, 3) };
            }
            else _sessions.RemoveAll(info => TranscriptViewController.SameSelection(info.Selection, selection));
            Invalidated?.Invoke(this, new(_epoch, MachineId, selection, 1, reason));
        }
        public async ValueTask<TranscriptSessionsPage> ListSessionsAsync(Guid machineId, string? cursor = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListReads++;
            if (Fail) throw new IOException("FORBIDDEN private diagnostic");
            var skip = cursor is null ? 0 : 16;
            var rows = _sessions.Skip(skip).Take(16).ToImmutableArray();
            var next = skip + 16 < _sessions.Count ? "next" : null;
            if (Hold)
            {
                HeldToken = cancellationToken;
                Started.TrySetResult();
                await Release.Task;
            }
            return new(_epoch, 1, 0, rows, next, Availability, TranscriptResetReason.None, TranscriptProtocol.ImplementedCapabilities);
        }
        public bool IsCurrent(TranscriptEventsPage page) => false;
        public bool IsCurrent(TranscriptSelection selection, Guid receiverEpoch, long invalidationGeneration, TranscriptRetainedRange displayedRange) => false;
        public ValueTask<TranscriptEventsPage> ReadEventsAsync(TranscriptSelection selection, string? cursor = null, CancellationToken cancellationToken = default)
        {
            EventReads++;
            throw new InvalidOperationException("Metadata rows must never read transcript bodies.");
        }
        public ValueTask<TranscriptEventsPage> ReadLatestEventsAsync(TranscriptSelection selection, CancellationToken cancellationToken = default) =>
            ReadEventsAsync(selection, cancellationToken: cancellationToken);
        public ValueTask<TranscriptEventsPage> ReadEventsBeforeAsync(TranscriptSelection selection, string cursor, CancellationToken cancellationToken = default) =>
            ReadEventsAsync(selection, cursor, cancellationToken);
    }
}
