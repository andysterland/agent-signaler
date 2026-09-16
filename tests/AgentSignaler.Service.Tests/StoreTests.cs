using AgentSignaler.Contracts;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSignaler.Service.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan value) => Now += value;
}

public sealed class StoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), $"AgentSignaler-tests-{Guid.NewGuid()}");
    private readonly ManualTimeProvider _clock = new();
    private readonly MachineStore _store;
    public StoreTests() => _store = new MachineStore(Path.Combine(_directory, "state.db"), _clock);

    private const string CachedUri = "ms-cloudpc:connect?cpcid=11111111-2222-3333-4444-555555555555&username=user%40example.com&environment=public&version=2&source=dashboard";

    private static WindowsAppConnection Mapping() => new(
        new Uri("https://example.region.devcenter.azure.com/"), "project", "dev-box",
        "user@example.com", Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        CachedUri, new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task MultipleMachineMappingsCanShareDevBoxWithoutCatalogDataOrNameInference()
    {
        var first = StateTests.Request(AgentEvent.SessionStart, timestamp: _clock.Now) with
        {
            MachineName = "UNRELATED-HOST-ONE"
        };
        var second = first with
        {
            EventId = Guid.NewGuid(), MachineId = Guid.NewGuid(), MachineName = "UNRELATED-HOST-TWO"
        };
        var unmapped = first with
        {
            EventId = Guid.NewGuid(), MachineId = Guid.NewGuid(), MachineName = Mapping().DevBoxName
        };
        foreach (var request in new[] { first, second, unmapped }) await _store.AcceptAsync(request);
        await _store.SetWindowsAppConnectionAsync(first.MachineId, Mapping());
        await _store.SetWindowsAppConnectionAsync(second.MachineId, Mapping());
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        var machines = (await reopened.GetMachinesAsync()).ToDictionary(machine => machine.MachineId);
        Assert.Equal(Mapping(), machines[first.MachineId].WindowsAppConnection);
        Assert.Equal(Mapping(), machines[second.MachineId].WindowsAppConnection);
        Assert.Null(machines[unmapped.MachineId].WindowsAppConnection);
        await reopened.ClearWindowsAppConnectionAsync(first.MachineId);
        machines = (await reopened.GetMachinesAsync()).ToDictionary(machine => machine.MachineId);
        Assert.Null(machines[first.MachineId].WindowsAppConnection);
        Assert.Equal(Mapping(), machines[second.MachineId].WindowsAppConnection);
        Assert.Null(machines[unmapped.MachineId].WindowsAppConnection);
    }

    [Fact]
    public async Task ConnectionMappingSetReplaceClearAndRemovePreserveMachineState()
    {
        var request = StateTests.Request(AgentEvent.PreToolUse, timestamp: _clock.Now);
        await _store.AcceptAsync(request);
        var original = Assert.Single(await _store.GetMachinesAsync());
        Assert.Null(original.WindowsAppConnection);
        await _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping());
        var stored = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(original.MachineId, stored.MachineId);
        Assert.Equal(original.Sessions, stored.Sessions);
        Assert.Equal(original.LastContactUtc, stored.LastContactUtc);
        Assert.Equal(Mapping(), stored.WindowsAppConnection);
        var json = ReadSnapshot();
        Assert.Equal(JsonSerializer.SerializeToNode(Mapping(), Protocol.Json)!.ToJsonString(),
            json["windowsAppConnection"]!.ToJsonString());

        var replacement = Mapping() with
        {
            DevBoxName = "replacement-box", LastKnownConnectionUri = null, ConnectionUriRetrievedAtUtc = null
        };
        await _store.SetWindowsAppConnectionAsync(request.MachineId, replacement);
        using (var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock))
        {
            Assert.Equal(replacement, Assert.Single(await reopened.GetMachinesAsync()).WindowsAppConnection);
            await reopened.ClearWindowsAppConnectionAsync(request.MachineId);
            await reopened.ClearWindowsAppConnectionAsync(request.MachineId);
            var cleared = Assert.Single(await reopened.GetMachinesAsync());
            Assert.Null(cleared.WindowsAppConnection);
            Assert.Equal(original.Sessions, cleared.Sessions);
            Assert.Equal(original.State, cleared.State);
            Assert.Equal(original.LastContactUtc, cleared.LastContactUtc);
            Assert.Null(ReadSnapshot()["windowsAppConnection"]);
        }
        Assert.Null(Assert.Single(await _store.GetMachinesAsync()).WindowsAppConnection);
        await _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping());
        await _store.RemoveAsync(request.MachineId);
        Assert.Empty(await _store.GetMachinesAsync());
        Assert.False((await _store.AcceptAsync(request)).Duplicate);
        Assert.Null(Assert.Single(await _store.GetMachinesAsync()).WindowsAppConnection);
    }

    [Fact]
    public async Task ConnectionMappingSurvivesRenameStatusDelayedStatusAndRestartWithoutExpiry()
    {
        var request = StateTests.Request(AgentEvent.SessionStart, timestamp: _clock.Now);
        await _store.AcceptAsync(request);
        await _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping());
        await _store.RenameAsync(request.MachineId, "A display name is not a Dev Box");
        _clock.Advance(TimeSpan.FromDays(365));
        await _store.AcceptAsync(request with
        {
            EventId = Guid.NewGuid(), Event = AgentEvent.PreToolUse, ReportedAtUtc = _clock.Now,
            MachineName = "NEW-HOST"
        });
        await _store.AcceptAsync(request with { EventId = Guid.NewGuid(), MachineName = "OLD-HOST" });
        Assert.True((await _store.AcceptAsync(request)).Duplicate);
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        var saved = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(Mapping(), saved.WindowsAppConnection);
        Assert.Equal("NEW-HOST", saved.MachineName);
        Assert.Equal("A display name is not a Dev Box", saved.Name);
        Assert.Equal(AgentState.Executing, saved.State);
        _clock.Advance(Protocol.OfflineAfter);
        Assert.Equal(AgentState.Offline, Assert.Single(await reopened.GetMachinesAsync()).State);
        Assert.Equal(Mapping(), Assert.Single(await reopened.GetMachinesAsync()).WindowsAppConnection);
    }

    [Fact]
    public async Task LegacySnapshotWithoutConnectionMappingLoadsAndUpdates()
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        var snapshot = ReadSnapshot();
        snapshot.Remove("windowsAppConnection");
        WriteSnapshot(snapshot);
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        Assert.Null(Assert.Single(await reopened.GetMachinesAsync()).WindowsAppConnection);
        await reopened.RenameAsync(request.MachineId, "Legacy");
        Assert.Null(Assert.Single(await reopened.GetMachinesAsync()).WindowsAppConnection);
    }

    [Fact]
    public async Task MissingMachineAndCancellationCannotCreateOrChangeConnectionMappings()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.SetWindowsAppConnectionAsync(Guid.NewGuid(), Mapping()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.ClearWindowsAppConnectionAsync(Guid.NewGuid()));
        Assert.Empty(await _store.GetMachinesAsync());
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        await _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping());
        var before = ReadSnapshot().ToJsonString();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping() with { ProjectName = "other" }, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.ClearWindowsAppConnectionAsync(request.MachineId, cancellation.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _store.SetWindowsAppConnectionAsync(request.MachineId, null!));
        Assert.Equal(before, ReadSnapshot().ToJsonString());
    }

    public static IEnumerable<object[]> InvalidMappings()
    {
        var mapping = Mapping();
        yield return [mapping with { DevCenterEndpoint = null! }];
        foreach (var endpoint in new[]
        {
            "relative", "http://example.region.devcenter.azure.com/",
            "https://user@example.region.devcenter.azure.com/", "https://example.region.devcenter.azure.com/?x=1",
            "https://example.region.devcenter.azure.com/#fragment", "https://example.region.devcenter.azure.com/projects",
            "https://example.region.devcenter.azure.com:444/", "https://example.devcenter.azure.com/",
            "https://example.region.devcenter.azure.com.evil.test/", "https://example.region.devcenter.azure.cn/",
            "https://example.region.devcenter.azure.com./", "https://example.region.extra.devcenter.azure.com/",
            "https://exam_ple.region.devcenter.azure.com/", "https://127.0.0.1/",
            "https://\u200D.region.devcenter.azure.com/", "https://\uFFFD.region.devcenter.azure.com/"
        })
            yield return [mapping with { DevCenterEndpoint = new Uri(endpoint, UriKind.RelativeOrAbsolute) }];
        foreach (var name in new[] { null, "", "ab", "_abc", "abc/def", "abc def", "abc\n", "abc?", "ébc", new string('a', 64) })
        {
            yield return [mapping with { ProjectName = name! }];
            yield return [mapping with { DevBoxName = name! }];
        }
        foreach (var upn in new[]
        {
            null, "", "user", "@example.com", "user@", "user@@example.com", " user@example.com",
            "user@example.com ", "user @example.com", "user\t@example.com", "user\u0000@example.com",
            "user\u00a0@example.com", new string('a', 319) + "@b"
        })
            yield return [mapping with { AzureAccountUpn = upn! }];
        yield return [mapping with { AzureTenantId = Guid.Empty }];
        yield return [mapping with { LastKnownConnectionUri = null }];
        yield return [mapping with { ConnectionUriRetrievedAtUtc = null }];
        foreach (var uri in InvalidConnectionUris())
            yield return [mapping with { LastKnownConnectionUri = (string)uri[0] }];
    }

    [Theory]
    [MemberData(nameof(InvalidMappings))]
    public async Task InvalidConnectionMappingWriteIsRejectedWithoutChangingSnapshot(WindowsAppConnection mapping)
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        await _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping());
        var before = ReadSnapshot().ToJsonString();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _store.SetWindowsAppConnectionAsync(request.MachineId, mapping));
        Assert.Equal(before, ReadSnapshot().ToJsonString());
    }

    [Theory]
    [MemberData(nameof(InvalidMappings))]
    public async Task InvalidPersistedConnectionMappingFailsExplicitlyWithoutMutation(WindowsAppConnection mapping)
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        var snapshot = ReadSnapshot();
        snapshot["windowsAppConnection"] = JsonSerializer.SerializeToNode(mapping, Protocol.Json);
        WriteSnapshot(snapshot);
        var before = ReadSnapshot().ToJsonString();
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => reopened.GetMachinesAsync());
        Assert.Contains("Back up the database", error.Message);
        Assert.DoesNotContain("ms-cloudpc:", error.ToString());
        Assert.DoesNotContain("user@example.com", error.ToString());
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.RenameAsync(request.MachineId, "Changed"));
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.SetWindowsAppConnectionAsync(request.MachineId, Mapping()));
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ClearWindowsAppConnectionAsync(request.MachineId));
        var next = request with { EventId = Guid.NewGuid(), ReportedAtUtc = request.ReportedAtUtc.AddSeconds(1) };
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.AcceptAsync(next));
        Assert.Equal(before, ReadSnapshot().ToJsonString());
        snapshot["windowsAppConnection"] = JsonSerializer.SerializeToNode(Mapping(), Protocol.Json);
        WriteSnapshot(snapshot);
        Assert.False((await reopened.AcceptAsync(next)).Duplicate);
    }

    [Theory]
    [InlineData("\"not an object\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"devCenterEndpoint\":42}")]
    public async Task MalformedPersistedMappingShapeHasRecoveryMessage(string mappingJson)
    {
        await _store.AcceptAsync(StateTests.Request(AgentEvent.SessionStart));
        var snapshot = ReadSnapshot();
        snapshot["windowsAppConnection"] = JsonNode.Parse(mappingJson);
        WriteSnapshot(snapshot);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _store.GetMachinesAsync());
        Assert.Contains("restore a valid snapshot", error.Message);
        Assert.Equal(snapshot.ToJsonString(), ReadSnapshot().ToJsonString());
    }

    [Theory]
    [InlineData("devCenterEndpoint", "\"https://[invalid\"")]
    [InlineData("azureTenantId", "\"not-a-guid\"")]
    [InlineData("connectionUriRetrievedAtUtc", "\"not-a-timestamp\"")]
    [InlineData("lastKnownConnectionUri", "42")]
    [InlineData("projectName", "[]")]
    public async Task MalformedPersistedMappingFieldTypesHaveRecoveryMessage(string field, string value)
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        await _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping());
        var snapshot = ReadSnapshot();
        snapshot["windowsAppConnection"]![field] = JsonNode.Parse(value);
        WriteSnapshot(snapshot);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _store.GetMachinesAsync());
        Assert.Contains("Back up the database", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("WindowsAppConnection")]
    [InlineData("WINDOWSAPPCONNECTION")]
    public async Task InvalidPersistedMappingWithAlternatePropertyCasingStillFailsExplicitly(string propertyName)
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        var snapshot = ReadSnapshot();
        snapshot.Remove("windowsAppConnection");
        snapshot[propertyName] = JsonSerializer.SerializeToNode(Mapping() with { AzureTenantId = Guid.Empty }, Protocol.Json);
        WriteSnapshot(snapshot);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _store.GetMachinesAsync());
        Assert.Contains("Back up the database", error.Message);
        await _store.RemoveAsync(request.MachineId);
        Assert.False((await _store.AcceptAsync(request)).Duplicate);
        Assert.Null(Assert.Single(await _store.GetMachinesAsync()).WindowsAppConnection);
    }

    [Fact]
    public async Task ConnectionMappingWriteFailuresRollBackAndReleaseTheGate()
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        await _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping());
        var before = ReadSnapshot().ToJsonString();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "state.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER RejectMappingWrite BEFORE UPDATE ON Machines
            BEGIN SELECT RAISE(ABORT, 'Simulated write failure'); END;
            """;
        command.ExecuteNonQuery();
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() =>
                _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping() with { DevBoxName = "replacement" }));
            Assert.Equal(before, ReadSnapshot().ToJsonString());
            await Assert.ThrowsAsync<SqliteException>(() => _store.ClearWindowsAppConnectionAsync(request.MachineId));
            Assert.Equal(before, ReadSnapshot().ToJsonString());
        }
        finally
        {
            command.CommandText = "DROP TRIGGER RejectMappingWrite";
            command.ExecuteNonQuery();
        }
        await _store.ClearWindowsAppConnectionAsync(request.MachineId);
        Assert.Null(Assert.Single(await _store.GetMachinesAsync()).WindowsAppConnection);
    }

    public static IEnumerable<object[]> InvalidConnectionUris()
    {
        foreach (var value in new[]
        {
            "", "connect?x=1", CachedUri.Replace("ms-cloudpc:", "https:"),
            CachedUri.Replace("connect?", "Connect?"), CachedUri.Replace("connect?", "other?"),
            CachedUri.Replace("connect?", "//connect?"), CachedUri.Replace("connect?", "//host/connect?"),
            CachedUri.Replace("connect?", "//user@host:123/connect?"),
            CachedUri.Replace("connect?", "/connect?"), CachedUri.Replace("connect?", "con%6Eect?"),
            CachedUri.Replace("connect?", "other/../connect?"), " " + CachedUri, CachedUri + "#",
            CachedUri + "#fragment", CachedUri + "\n", CachedUri + "%", CachedUri + "%0", CachedUri + "%GG",
            CachedUri + "&source=duplicate", CachedUri + "&extra=value", CachedUri + "&", CachedUri + "&=value",
            CachedUri.Replace("source=dashboard", "source"),
            CachedUri.Replace("source=dashboard", "Source=dashboard"),
            CachedUri.Replace("source=dashboard", "%73ource=dashboard"),
            CachedUri.Replace("11111111-2222-3333-4444-555555555555", "not-a-guid"),
            CachedUri.Replace("11111111-2222-3333-4444-555555555555", Guid.Empty.ToString()),
            CachedUri.Replace("user%40example.com", "other%40example.com"),
            CachedUri.Replace("source=dashboard", "source=%00"),
            CachedUri.Replace("source=dashboard", "source=%0D%0A"),
            CachedUri.Replace("source=dashboard", "source=%C2%85"),
            CachedUri.Replace("source=dashboard", "source=" + new string('a', 513)),
            CachedUri.Replace("source=dashboard", "source=" + new string('a', 4096))
        })
            yield return [value];
        foreach (var parameter in new[] { "cpcid", "username", "environment", "version", "source" })
        {
            var parts = CachedUri[(CachedUri.IndexOf('?') + 1)..].Split('&');
            var part = parts.Single(p => p.StartsWith(parameter + "=", StringComparison.Ordinal));
            yield return [CachedUri + "&" + part];
            yield return ["ms-cloudpc:connect?" + string.Join("&", parts.Where(p => p != part))];
            yield return [CachedUri.Replace(part, parameter + "=")];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidConnectionUris))]
    public void SharedConnectionUriValidatorRejectsUnsafeValuesWithoutLeakingThem(string uri)
    {
        var error = Assert.ThrowsAny<ArgumentException>(() => WindowsAppConnection.ValidateConnectionUri(uri, "user@example.com"));
        Assert.DoesNotContain("ms-cloudpc:", error.ToString());
        Assert.DoesNotContain("user@example.com", error.ToString());
    }

    [Fact]
    public async Task SharedConnectionUriValidatorPreservesOriginalTextAndAcceptsBoundaries()
    {
        var uri = "MS-CLOUDPC:connect?source=a%2fb+z&version=2&environment=public&username=USER%40example.com&cpcid=%31" +
            "1111111-2222-3333-4444-555555555555";
        Assert.Equal(uri, WindowsAppConnection.ValidateConnectionUri(uri, "user@example.com").OriginalString);
        var prefix = CachedUri[..CachedUri.IndexOf("&environment=", StringComparison.Ordinal)] +
            "&environment=" + string.Concat(Enumerable.Repeat("%41", 440)) +
            "&version=" + string.Concat(Enumerable.Repeat("%41", 440)) + "&source=";
        var remaining = 4096 - prefix.Length;
        var maximum = prefix + string.Concat(Enumerable.Repeat("%41", remaining / 3)) + new string('A', remaining % 3);
        Assert.Equal(4096, maximum.Length);
        Assert.Equal(maximum, WindowsAppConnection.ValidateConnectionUri(maximum, "user@example.com").OriginalString);
        Assert.Throws<ArgumentException>(() => WindowsAppConnection.ValidateConnectionUri(maximum + "A", "user@example.com"));
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        var mapping = Mapping() with { LastKnownConnectionUri = uri };
        await _store.SetWindowsAppConnectionAsync(request.MachineId, mapping);
        Assert.Equal(uri, Assert.Single(await _store.GetMachinesAsync()).WindowsAppConnection!.LastKnownConnectionUri);
        var upn = new string('a', 318) + "@b";
        var boundary = mapping with
        {
            DevCenterEndpoint = new Uri("https://EXAMPLE.REGION.devcenter.azure.com:443/"),
            ProjectName = "a._", DevBoxName = new string('a', 63), AzureAccountUpn = upn,
            LastKnownConnectionUri = CachedUri.Replace("user%40example.com", upn)
                .Replace("source=dashboard", "source=" + new string('a', 512))
        };
        await _store.SetWindowsAppConnectionAsync(request.MachineId, boundary);
        Assert.Equal(boundary, Assert.Single(await _store.GetMachinesAsync()).WindowsAppConnection);
        WindowsAppConnection.ValidateConnectionUri(CachedUri.Replace("user%40example.com", "a+b%40example.com"), "a+b@example.com");
    }

    private JsonObject ReadSnapshot()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "state.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Snapshot FROM Machines";
        return JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
    }

    private void WriteSnapshot(JsonObject snapshot)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "state.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Machines SET Snapshot=$snapshot";
        command.Parameters.AddWithValue("$snapshot", snapshot.ToJsonString());
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task RegistersPersistsRenamesAndRemoves()
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        Assert.False((await _store.AcceptAsync(request)).Duplicate);
        await _store.RenameAsync(request.MachineId, "My workstation");
        using (var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock))
        {
            var saved = Assert.Single(await reopened.GetMachinesAsync());
            Assert.Equal("My workstation", saved.Name);
            Assert.Equal(AgentState.Waiting, saved.State);
            Assert.Equal(_clock.Now, saved.LastContactUtc);
            Assert.Single(saved.Sessions);
        }
        await _store.RemoveAsync(request.MachineId);
        Assert.Empty(await _store.GetMachinesAsync());
        Assert.False((await _store.AcceptAsync(request)).Duplicate);
    }

    [Fact]
    public async Task DetailsPersistAcrossRestartReportsRenameAndConnectionChanges()
    {
        var request = StateTests.Request(AgentEvent.SessionStart, timestamp: _clock.Now);
        await _store.AcceptAsync(request);
        var original = Assert.Single(await _store.GetMachinesAsync());
        const string note = "Build workstation\r\nRuns integration tests.";
        await _store.UpdateDetailsAsync(request.MachineId, " Workstation ", $" {note} ");
        var edited = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal("Workstation", edited.DisplayName);
        Assert.Equal(note, edited.Note);
        Assert.Equal(original.Sessions, edited.Sessions);
        Assert.Equal(original.LastContactUtc, edited.LastContactUtc);
        Assert.Equal(original.State, edited.State);

        await _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping());
        await _store.RenameAsync(request.MachineId, "Renamed");
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _store.AcceptAsync(request with
        {
            EventId = Guid.NewGuid(), Event = AgentEvent.PreToolUse, ReportedAtUtc = _clock.Now
        });
        await _store.AcceptAsync(request with { EventId = Guid.NewGuid() });
        Assert.True((await _store.AcceptAsync(request)).Duplicate);
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        var saved = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal("Renamed", saved.DisplayName);
        Assert.Equal(note, saved.Note);
        Assert.Equal(Mapping(), saved.WindowsAppConnection);
        Assert.Equal(AgentState.Executing, saved.State);
        await reopened.ClearWindowsAppConnectionAsync(request.MachineId);
        Assert.Equal(note, Assert.Single(await reopened.GetMachinesAsync()).Note);
        await reopened.RemoveAsync(request.MachineId);
        await reopened.AcceptAsync(request);
        Assert.Null(Assert.Single(await reopened.GetMachinesAsync()).Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public async Task DetailsCanClearNameAndNote(string? emptyNote)
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        await _store.SetWindowsAppConnectionAsync(request.MachineId, Mapping());
        await _store.UpdateDetailsAsync(request.MachineId, "Custom name", "Original note");
        await _store.UpdateDetailsAsync(request.MachineId, "Custom name", "Replacement note");
        Assert.Equal("Replacement note", Assert.Single(await _store.GetMachinesAsync()).Note);
        await _store.UpdateDetailsAsync(request.MachineId, "", emptyNote);
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        var saved = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Null(saved.DisplayName);
        Assert.Equal(request.MachineName, saved.Name);
        Assert.Null(saved.Note);
        Assert.Equal(Mapping(), saved.WindowsAppConnection);
    }

    [Fact]
    public async Task LegacySnapshotWithoutNoteLoadsAndCanSaveDetails()
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        var snapshot = ReadSnapshot();
        snapshot.Remove("note");
        WriteSnapshot(snapshot);
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        Assert.Null(Assert.Single(await reopened.GetMachinesAsync()).Note);
        await reopened.UpdateDetailsAsync(request.MachineId, "Legacy", "New note");
        var saved = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal("Legacy", saved.DisplayName);
        Assert.Equal("New note", saved.Note);
    }

    [Fact]
    public async Task InvalidNameCancellationAndMissingMachineCannotPartiallySaveDetails()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _store.UpdateDetailsAsync(Guid.NewGuid(), "Missing", "Note"));
        Assert.Empty(await _store.GetMachinesAsync());
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        await _store.UpdateDetailsAsync(request.MachineId, "Original", "Original note");
        var before = ReadSnapshot().ToJsonString();
        foreach (var invalidName in new[] { "Invalid\nname", new string('a', 129) })
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _store.UpdateDetailsAsync(request.MachineId, invalidName, "Changed note"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.UpdateDetailsAsync(request.MachineId, "Changed", "Changed note", cancellation.Token));
        Assert.Equal(before, ReadSnapshot().ToJsonString());
    }

    [Fact]
    public async Task UserInputWaitSurvivesRestartAndOtherSessions()
    {
        var request = StateTests.Request(AgentEvent.PreToolUse) with { ToolRequiresUserInput = true };
        await _store.AcceptAsync(request);
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        Assert.True(Assert.Single(Assert.Single(await reopened.GetMachinesAsync()).Sessions).AwaitingUserInput);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await reopened.AcceptAsync(request with
        {
            EventId = Guid.NewGuid(), SessionId = "other", ToolRequiresUserInput = false,
            ReportedAtUtc = _clock.Now
        });
        Assert.Equal(AgentState.Waiting, Assert.Single(await reopened.GetMachinesAsync()).State);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await reopened.AcceptAsync(request with
        {
            EventId = Guid.NewGuid(), Event = AgentEvent.PostToolUse, ReportedAtUtc = _clock.Now
        });
        var machine = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(AgentState.Executing, machine.State);
        Assert.All(machine.Sessions, session => Assert.False(session.AwaitingUserInput));
    }

    [Fact]
    public async Task DuplicateDoesNotRefreshLivenessEvenAfterRestart()
    {
        var request = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(request);
        _clock.Advance(TimeSpan.FromMinutes(5));
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        Assert.True((await reopened.AcceptAsync(request)).Duplicate);
        Assert.Equal(AgentState.Offline, Assert.Single(await reopened.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task ReceiptTimeControlsOfflineBoundaryRegardlessOfClientClock()
    {
        var request = StateTests.Request(AgentEvent.SessionStart, timestamp: _clock.Now.AddYears(-1));
        await _store.AcceptAsync(request);
        _clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(AgentState.Waiting, Assert.Single(await _store.GetMachinesAsync()).State);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(AgentState.Offline, Assert.Single(await _store.GetMachinesAsync()).State);
        await _store.AcceptAsync(request with { EventId = Guid.NewGuid(), ReportedAtUtc = request.ReportedAtUtc.AddSeconds(1) });
        Assert.Equal(AgentState.Waiting, Assert.Single(await _store.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task LateSessionReportCannotReplaceNewerStateOrMachineMetadata()
    {
        var current = StateTests.Request(AgentEvent.PermissionRequest, timestamp: _clock.Now);
        await _store.AcceptAsync(current);
        await _store.AcceptAsync(current with
        {
            EventId = Guid.NewGuid(), MachineName = "OLD-NAME",
            Event = AgentEvent.PreToolUse, ReportedAtUtc = _clock.Now.AddSeconds(-10)
        });
        var saved = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(AgentState.Waiting, saved.State);
        Assert.Equal("TEST-PC", saved.MachineName);
        Assert.Equal(AgentEvent.PermissionRequest, saved.LatestEvent);
    }

    [Fact]
    public async Task IndependentConcurrentSessionsAreAggregated()
    {
        var first = StateTests.Request(AgentEvent.PreToolUse);
        var second = first with { EventId = Guid.NewGuid(), SessionId = "second", Event = AgentEvent.ErrorOccurred };
        await Task.WhenAll(_store.AcceptAsync(first), _store.AcceptAsync(second));
        Assert.Equal(AgentState.Failed, Assert.Single(await _store.GetMachinesAsync()).State);
        _clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(AgentState.Waiting, Assert.Single(await _store.GetMachinesAsync()).State);
        await _store.AcceptAsync(second with
        {
            EventId = Guid.NewGuid(), ReportedAtUtc = _clock.Now, Event = AgentEvent.SessionEnd
        });
        Assert.Equal(AgentState.Executing, Assert.Single(await _store.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task HooksDoNotExtendExistingResultLifetime()
    {
        var hook = StateTests.Request(AgentEvent.AgentStop);
        await _store.AcceptAsync(hook);
        _clock.Advance(TimeSpan.FromSeconds(20));
        var followup = hook with
        {
            EventId = Guid.NewGuid(), Event = AgentEvent.PermissionRequest,
            ReportedAtUtc = _clock.Now
        };
        await _store.AcceptAsync(followup);
        _clock.Advance(TimeSpan.FromSeconds(39));
        Assert.Equal(AgentState.Succeeded, Assert.Single(await _store.GetMachinesAsync()).State);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AgentState.Waiting, Assert.Single(await _store.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task HookResultExpiryUsesReceiptTimeWithClientClockSkew()
    {
        var clientNow = _clock.Now.AddHours(2);
        var request = StateTests.Request(AgentEvent.AgentStop, timestamp: clientNow);
        await _store.AcceptAsync(request);
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(AgentState.Succeeded, Assert.Single(await _store.GetMachinesAsync()).State);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AgentState.Waiting, Assert.Single(await _store.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task EndedSessionsRejectDelayedHooks()
    {
        var hook = StateTests.Request(AgentEvent.SessionStart);
        await _store.AcceptAsync(hook);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _store.AcceptAsync(hook with
        {
            EventId = Guid.NewGuid(), Event = AgentEvent.SessionEnd,
            ReportedAtUtc = _clock.Now
        });
        await _store.AcceptAsync(hook with { EventId = Guid.NewGuid(), ReportedAtUtc = hook.ReportedAtUtc.AddSeconds(1) });
        var machine = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(AgentState.Idle, machine.State);
        Assert.Equal(AgentState.Idle, Assert.Single(machine.Sessions).UnderlyingState);
    }

    [Fact]
    public async Task EndedSessionsFreeCapacityAndRetiredHooksStayRetiredAfterRestart()
    {
        var hook = StateTests.Request(AgentEvent.SessionEnd, timestamp: _clock.Now);
        for (var i = 0; i < Protocol.MaxSessions; i++)
            await _store.AcceptAsync(hook with { EventId = Guid.NewGuid(), SessionId = $"ended-{i}" });
        _clock.Advance(TimeSpan.FromSeconds(61));
        await _store.AcceptAsync(hook with
        {
            EventId = Guid.NewGuid(), SessionId = "new", Event = AgentEvent.PreToolUse, ReportedAtUtc = _clock.Now
        });
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        await reopened.AcceptAsync(hook with
        {
            EventId = Guid.NewGuid(), SessionId = "ended-0", Event = AgentEvent.PermissionRequest
        });
        var machine = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(Protocol.MaxSessions, machine.Sessions.Count);
        Assert.DoesNotContain(machine.Sessions, s => s.SessionId == "ended-0");
        Assert.Equal(AgentState.Executing, machine.State);
    }

    [Fact]
    public async Task LegacySnapshotPreservesStateAndMigratesOnNextHook()
    {
        var request = StateTests.Request(AgentEvent.PreToolUse, timestamp: _clock.Now);
        await _store.AcceptAsync(request);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "state.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Snapshot FROM Machines";
            var snapshot = JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
            snapshot["latestEvent"] = "heartbeat";
            snapshot.Remove("retiredThroughUtc");
            snapshot["heartbeatWatermarkUtc"] = JsonValue.Create(_clock.Now);
            command.CommandText = "UPDATE Machines SET Snapshot=$snapshot";
            command.Parameters.AddWithValue("$snapshot", snapshot.ToJsonString());
            command.ExecuteNonQuery();
        }
        using var reopened = new MachineStore(Path.Combine(_directory, "state.db"), _clock);
        var legacy = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Null(legacy.LatestEvent);
        Assert.Equal(AgentState.Executing, legacy.State);
        Assert.Equal(request.MachineId, legacy.MachineId);
        await reopened.AcceptAsync(request with
        {
            EventId = Guid.NewGuid(), Event = AgentEvent.SessionStart, SessionId = "retired",
            ReportedAtUtc = _clock.Now.AddSeconds(-1)
        });
        Assert.Single(Assert.Single(await reopened.GetMachinesAsync()).Sessions);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await reopened.AcceptAsync(request with
        {
            EventId = Guid.NewGuid(), Event = AgentEvent.SessionEnd, ReportedAtUtc = _clock.Now
        });
        var updated = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(AgentEvent.SessionEnd, updated.LatestEvent);
        Assert.Equal(AgentState.Idle, updated.State);
    }

    [Fact]
    public async Task ActiveSessionsAndUnexpiredResultsAreNotEvictedAtCapacity()
    {
        var request = StateTests.Request(AgentEvent.PreToolUse, timestamp: _clock.Now);
        for (var i = 0; i < Protocol.MaxSessions; i++)
            await _store.AcceptAsync(request with { EventId = Guid.NewGuid(), SessionId = $"session-{i}" });
        _clock.Advance(TimeSpan.FromSeconds(1));
        var next = request with { EventId = Guid.NewGuid(), SessionId = "new", ReportedAtUtc = _clock.Now };
        await Assert.ThrowsAsync<CapacityException>(() => _store.AcceptAsync(next));
        await _store.AcceptAsync(request with
        {
            EventId = Guid.NewGuid(), SessionId = "session-0", Event = AgentEvent.AgentStop, ReportedAtUtc = _clock.Now
        });
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _store.AcceptAsync(request with
        {
            EventId = Guid.NewGuid(), SessionId = "session-0", Event = AgentEvent.SessionEnd, ReportedAtUtc = _clock.Now
        });
        next = next with { ReportedAtUtc = _clock.Now.AddSeconds(1) };
        await Assert.ThrowsAsync<CapacityException>(() => _store.AcceptAsync(next));
        _clock.Advance(Protocol.ResultDuration);
        Assert.False((await _store.AcceptAsync(next)).Duplicate);
        Assert.Equal(Protocol.MaxSessions, Assert.Single(await _store.GetMachinesAsync()).Sessions.Count);
    }

    [Fact]
    public async Task CapacityFailureDoesNotStoreOrDeduplicateRejectedReport()
    {
        for (var i = 0; i < 25; i++) await _store.AcceptAsync(StateTests.Request(AgentEvent.SessionStart));
        var rejected = StateTests.Request(AgentEvent.SessionStart);
        await Assert.ThrowsAsync<CapacityException>(() => _store.AcceptAsync(rejected));
        var registered = await _store.GetMachinesAsync();
        Assert.Equal(25, registered.Count);
        await _store.RemoveAsync(registered[0].MachineId);
        Assert.False((await _store.AcceptAsync(rejected)).Duplicate);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
