using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Service.Tests;

public class StateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentEvent.SessionStart, AgentState.Waiting)]
    [InlineData(AgentEvent.UserPromptSubmitted, AgentState.Executing)]
    [InlineData(AgentEvent.PreToolUse, AgentState.Executing)]
    [InlineData(AgentEvent.PostToolUse, AgentState.Executing)]
    [InlineData(AgentEvent.PermissionRequest, AgentState.Waiting)]
    [InlineData(AgentEvent.AgentStop, AgentState.Succeeded)]
    [InlineData(AgentEvent.ErrorOccurred, AgentState.Failed)]
    [InlineData(AgentEvent.PostToolUseFailure, AgentState.Failed)]
    [InlineData(AgentEvent.SessionEnd, AgentState.Idle)]
    public void MapsEvents(AgentEvent kind, AgentState expected)
    {
        var session = StateReducer.Apply(null, "session", kind, Now, Now);
        Assert.Equal(expected, StateReducer.Effective(session, Now));
    }

    [Fact]
    public void FailedToolMapsToFailure()
    {
        var session = StateReducer.Apply(null, "session", AgentEvent.PostToolUse, Now, Now, true);
        Assert.Equal(AgentState.Failed, StateReducer.Effective(session, Now));
        Assert.Equal(AgentState.Waiting, StateReducer.Effective(session, Now.AddSeconds(60)));
    }

    [Fact]
    public void UserInputToolBlocksUntilItsOwnCompletion()
    {
        var pending = StateReducer.Apply(null, "session", AgentEvent.PreToolUse, Now, Now,
            toolRequiresUserInput: true);
        Assert.True(pending.AwaitingUserInput);
        Assert.Equal(AgentState.Waiting, StateReducer.Effective(pending, Now.AddMinutes(10)));
        var timestamp = Now;
        foreach (var kind in new[] { AgentEvent.PreToolUse, AgentEvent.PostToolUse, AgentEvent.UserPromptSubmitted })
        {
            timestamp = timestamp.AddSeconds(1);
            pending = StateReducer.Apply(pending, "session", kind, timestamp, timestamp);
            Assert.True(pending.AwaitingUserInput);
            Assert.Equal(AgentState.Waiting, StateReducer.Effective(pending, timestamp));
        }
        Assert.Same(pending, StateReducer.Apply(pending, "session", AgentEvent.PostToolUse, Now, timestamp,
            toolRequiresUserInput: true));
        timestamp = timestamp.AddSeconds(1);
        var completed = StateReducer.Apply(pending, "session", AgentEvent.PostToolUse, timestamp, timestamp,
            toolRequiresUserInput: true);
        Assert.False(completed.AwaitingUserInput);
        Assert.Equal(AgentState.Executing, StateReducer.Effective(completed, timestamp));
    }

    [Theory]
    [InlineData(AgentEvent.PostToolUse, true, true, AgentState.Failed)]
    [InlineData(AgentEvent.PostToolUseFailure, false, true, AgentState.Failed)]
    [InlineData(AgentEvent.SessionEnd, false, false, AgentState.Idle)]
    [InlineData(AgentEvent.AgentStop, false, false, AgentState.Succeeded)]
    [InlineData(AgentEvent.ErrorOccurred, false, false, AgentState.Failed)]
    [InlineData(AgentEvent.SessionStart, false, false, AgentState.Waiting)]
    public void UserInputWaitClearsOnCompletionOrSessionTermination(
        AgentEvent kind, bool failed, bool requiresUserInput, AgentState expected)
    {
        var pending = StateReducer.Apply(null, "session", AgentEvent.PreToolUse, Now, Now,
            toolRequiresUserInput: true);
        var completed = StateReducer.Apply(pending, "session", kind, Now.AddSeconds(1), Now.AddSeconds(1),
            failed, requiresUserInput);
        Assert.False(completed.AwaitingUserInput);
        Assert.Equal(expected, StateReducer.Effective(completed, Now.AddSeconds(1)));
    }

    [Theory]
    [InlineData(AgentEvent.AgentStop, AgentState.Waiting)]
    [InlineData(AgentEvent.ErrorOccurred, AgentState.Failed)]
    public void UserInputWaitTakesPriorityOverSuccessButNotFailure(AgentEvent result, AgentState expected)
    {
        var previous = StateReducer.Apply(null, "session", result, Now, Now);
        var pending = StateReducer.Apply(previous, "session", AgentEvent.PreToolUse, Now.AddSeconds(1),
            Now.AddSeconds(1), toolRequiresUserInput: true);
        Assert.Equal(expected, StateReducer.Effective(pending, Now.AddSeconds(1)));
        Assert.Equal(previous.ResultUntilUtc, pending.ResultUntilUtc);
        Assert.Equal(AgentState.Waiting, StateReducer.Effective(pending, Now.AddSeconds(60)));
    }

    [Theory]
    [InlineData(AgentEvent.PreToolUse, true)]
    [InlineData(AgentEvent.PostToolUse, true)]
    [InlineData(AgentEvent.PostToolUseFailure, true)]
    [InlineData(AgentEvent.UserPromptSubmitted, false)]
    [InlineData(AgentEvent.SessionEnd, false)]
    public void UserInputToolMetadataIsValidatedAndRoundTrips(AgentEvent kind, bool valid)
    {
        var request = Request(kind) with { ToolRequiresUserInput = true };
        Assert.Equal(valid, Protocol.Validate(request).Count == 0);
        var restored = JsonSerializer.Deserialize<StatusRequest>(JsonSerializer.Serialize(request, Protocol.Json), Protocol.Json);
        Assert.Equal(request, restored);
    }

    [Fact]
    public void LegacyPayloadsDefaultToNotAwaitingUserInput()
    {
        var request = Request(AgentEvent.PreToolUse);
        var json = JsonSerializer.Serialize(request, Protocol.Json);
        Assert.DoesNotContain("toolRequiresUserInput", json);
        Assert.False(JsonSerializer.Deserialize<StatusRequest>(json, Protocol.Json)!.ToolRequiresUserInput);
        var snapshot = JsonSerializer.Deserialize<SessionSnapshot>("{}", Protocol.Json)!;
        Assert.False(snapshot.AwaitingUserInput);
    }

    [Theory]
    [InlineData(AgentEvent.AgentStop, AgentState.Succeeded)]
    [InlineData(AgentEvent.ErrorOccurred, AgentState.Failed)]
    public void ResultsHoldExactlySixtySeconds(AgentEvent kind, AgentState expected)
    {
        var session = StateReducer.Apply(null, "session", kind, Now, Now);
        Assert.Equal(expected, StateReducer.Effective(session, Now.AddSeconds(59.999)));
        Assert.Equal(AgentState.Waiting, StateReducer.Effective(session, Now.AddSeconds(60)));
        var ended = StateReducer.Apply(session, "session", AgentEvent.SessionEnd, Now.AddSeconds(1), Now.AddSeconds(1));
        Assert.Equal(expected, StateReducer.Effective(ended, Now.AddSeconds(59)));
        Assert.Equal(AgentState.Idle, StateReducer.Effective(ended, Now.AddSeconds(60)));
    }

    [Fact]
    public void OutOfOrderDoesNotReplaceNewerState()
    {
        var session = StateReducer.Apply(null, "session", AgentEvent.PermissionRequest, Now, Now);
        Assert.Same(session, StateReducer.Apply(session, "session", AgentEvent.PreToolUse, Now.AddSeconds(-1), Now));
        Assert.Same(session, StateReducer.Apply(session, "session", AgentEvent.PreToolUse, Now, Now));
    }

    [Fact]
    public void AggregationUsesSpecifiedPriorityNotEnumOrder()
    {
        var sessions = new[]
        {
            StateReducer.Apply(null, "success", AgentEvent.AgentStop, Now, Now),
            StateReducer.Apply(null, "executing", AgentEvent.PreToolUse, Now, Now),
            StateReducer.Apply(null, "waiting", AgentEvent.PermissionRequest, Now, Now),
            StateReducer.Apply(null, "failed", AgentEvent.ErrorOccurred, Now, Now)
        };
        Assert.Equal(AgentState.Failed, StateReducer.Aggregate(sessions, Now));
        Assert.Equal(AgentState.Waiting, StateReducer.Aggregate(sessions.Take(3), Now));
        Assert.Equal(AgentState.Executing, StateReducer.Aggregate(sessions.Take(2), Now));
        Assert.Equal(AgentState.Succeeded, StateReducer.Aggregate(sessions.Take(1), Now));
        Assert.Equal(AgentState.Idle, StateReducer.Aggregate([], Now));
    }

    [Fact]
    public void RequiredFieldsAndUnknownPayloadPropertiesAreRejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StatusRequest>("{}", Protocol.Json));
        var json = JsonSerializer.Serialize(Request(AgentEvent.SessionStart), Protocol.Json);
        json = json.Insert(1, "\"prompt\":\"must not enter protocol\",");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StatusRequest>(json, Protocol.Json));
    }

    [Fact]
    public void ValidationRejectsInvalidFields()
    {
        var invalid = Request(AgentEvent.SessionStart) with
        {
            ProtocolVersion = 2, MachineId = Guid.Empty, EventId = Guid.Empty,
            MachineName = "bad\nname", ClientVersion = "", SessionId = null
        };
        Assert.Equal(6, Protocol.Validate(invalid).Count);
        Assert.NotEmpty(Protocol.Validate(Request((AgentEvent)0)));
    }

    [Theory]
    [InlineData("\"event\":\"heartbeat\",")]
    [InlineData("\"state\":\"idle\",")]
    [InlineData("\"sessions\":[],")]
    public void RemovedHeartbeatAndSnapshotPayloadsAreRejected(string property)
    {
        var json = JsonSerializer.Serialize(Request(AgentEvent.SessionStart), Protocol.Json).Insert(1, property);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StatusRequest>(json, Protocol.Json));
    }

    internal static StatusRequest Request(AgentEvent kind, Guid? machineId = null, DateTimeOffset? timestamp = null) => new()
    {
        EventId = Guid.NewGuid(), MachineId = machineId ?? Guid.NewGuid(),
        MachineName = "TEST-PC", ClientVersion = "1.0.0", SessionId = "session",
        Event = kind, ReportedAtUtc = timestamp ?? Now
    };
}
