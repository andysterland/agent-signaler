using AgentSignaler.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Service.Tests;

public sealed class ReceiptRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), $"AgentSignaler-receipts-{Guid.NewGuid()}");
    private readonly ManualTimeProvider _clock = new();
    private string DatabasePath => Path.Combine(_directory, "state.db");

    private MachineStore Open(int limit) => new(DatabasePath, _clock, new MachineStoreOptions { ReceiptLimit = limit });

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Theory]
    [InlineData(-365)]
    [InlineData(365)]
    public async Task CapacityNeverEvictsReplayHistoryOrRefreshesLivenessEvenAfterRestartAndClockSkew(int clientSkewDays)
    {
        var request = StateTests.Request(AgentEvent.SessionStart, timestamp: _clock.Now.AddDays(clientSkewDays));
        using (var store = Open(2))
        {
            await store.AcceptAsync(request);
            await store.AcceptAsync(request with { EventId = Guid.NewGuid(), Event = AgentEvent.PreToolUse, ReportedAtUtc = request.ReportedAtUtc.AddSeconds(1) });
        }
        _clock.Advance(TimeSpan.FromDays(730));
        using var reopened = Open(2);
        var before = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(AgentState.Offline, before.State);
        Assert.True((await reopened.AcceptAsync(request)).Duplicate);
        await Assert.ThrowsAsync<ReceiptCapacityException>(() => reopened.AcceptAsync(request with
        {
            EventId = Guid.NewGuid(), ReportedAtUtc = _clock.Now, MachineName = "must-not-be-saved"
        }));
        var after = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(before.LastContactUtc, after.LastContactUtc);
        Assert.Equal(before.MachineName, after.MachineName);
        Assert.Equal(before.LatestEvent, after.LatestEvent);
        Assert.Equal(before.Sessions, after.Sessions);
        Assert.Equal(AgentState.Offline, after.State);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM Receipts"));
        Assert.Equal(2L, Scalar("SELECT Total FROM ReceiptCount"));
    }

    [Fact]
    public async Task GlobalLimitAppliesAcrossMachinesAndExplicitRemovalResetsOnlyRemovedHistory()
    {
        using var store = Open(2);
        var first = StateTests.Request(AgentEvent.SessionStart);
        var second = StateTests.Request(AgentEvent.SessionStart);
        var rejected = StateTests.Request(AgentEvent.SessionStart);
        await store.AcceptAsync(first);
        await store.AcceptAsync(second);
        await Assert.ThrowsAsync<ReceiptCapacityException>(() => store.AcceptAsync(rejected));
        Assert.Equal(2, (await store.GetMachinesAsync()).Count);
        await store.RemoveAsync(first.MachineId);
        Assert.Equal(1L, Scalar("SELECT Total FROM ReceiptCount"));
        Assert.True((await store.AcceptAsync(second)).Duplicate);
        Assert.False((await store.AcceptAsync(rejected)).Duplicate);
        await store.RemoveAsync(rejected.MachineId);
        Assert.False((await store.AcceptAsync(first)).Duplicate);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM Receipts"));
    }

    [Fact]
    public async Task ConcurrentUniqueReportsCannotOvershootTheGlobalLimit()
    {
        using var store = Open(10);
        var request = StateTests.Request(AgentEvent.SessionStart);
        var accepted = 0;
        var rejected = 0;
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            try
            {
                await store.AcceptAsync(request with { EventId = Guid.NewGuid() });
                Interlocked.Increment(ref accepted);
            }
            catch (ReceiptCapacityException) { Interlocked.Increment(ref rejected); }
        }));
        Assert.Equal(10, accepted);
        Assert.Equal(40, rejected);
        Assert.Equal(10L, Scalar("SELECT COUNT(*) FROM Receipts"));
        Assert.Equal(10L, Scalar("SELECT Total FROM ReceiptCount"));
    }

    [Fact]
    public async Task LegacyOverLimitMigrationPreservesAllReceiptsAndFailsClosed()
    {
        var requests = Enumerable.Range(0, 3).Select(_ => StateTests.Request(AgentEvent.SessionStart)).ToArray();
        using (var original = Open(3))
            foreach (var request in requests) await original.AcceptAsync(request);
        Scalar("""
            DROP TRIGGER Receipts_Inserted;
            DROP TRIGGER Receipts_Deleted;
            DROP TABLE ReceiptCount;
            """);
        using var migrated = Open(2);
        foreach (var request in requests) Assert.True((await migrated.AcceptAsync(request)).Duplicate);
        await Assert.ThrowsAsync<ReceiptCapacityException>(() => migrated.AcceptAsync(StateTests.Request(AgentEvent.SessionStart)));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM Receipts"));
        Assert.Equal(3L, Scalar("SELECT Total FROM ReceiptCount"));
        await migrated.RemoveAsync(requests[0].MachineId);
        await Assert.ThrowsAsync<ReceiptCapacityException>(() => migrated.AcceptAsync(StateTests.Request(AgentEvent.SessionStart)));
        await migrated.RemoveAsync(requests[1].MachineId);
        Assert.False((await migrated.AcceptAsync(StateTests.Request(AgentEvent.SessionStart))).Duplicate);
        Assert.True((await migrated.AcceptAsync(requests[2])).Duplicate);
    }

    [Fact]
    public async Task PersistenceFailureRollsBackStateAndReceiptBudgetAndIsNotSilenced()
    {
        using var store = Open(1);
        var request = StateTests.Request(AgentEvent.SessionStart);
        Scalar("""
            CREATE TRIGGER RejectReceipt BEFORE INSERT ON Receipts
            BEGIN SELECT RAISE(ABORT, 'test storage failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => store.AcceptAsync(request));
        Assert.Empty(await store.GetMachinesAsync());
        Assert.Equal(0L, Scalar("SELECT Total FROM ReceiptCount"));
        Scalar("DROP TRIGGER RejectReceipt");
        Assert.False((await store.AcceptAsync(request)).Duplicate);
        Assert.Equal(1L, Scalar("SELECT Total FROM ReceiptCount"));
    }

    [Fact]
    public async Task RepeatedFillAndExplicitRemovalReusesBoundedStorage()
    {
        using var store = Open(100);
        long firstPages = 0;
        for (var round = 0; round < 4; round++)
        {
            var request = StateTests.Request(AgentEvent.SessionStart);
            for (var index = 0; index < 100; index++)
                await store.AcceptAsync(request with { EventId = Guid.NewGuid() });
            Assert.Equal(100L, Scalar("SELECT COUNT(*) FROM Receipts"));
            if (round == 0) firstPages = Scalar("PRAGMA page_count");
            else Assert.InRange(Scalar("PRAGMA page_count"), 1L, firstPages + 4);
            await store.RemoveAsync(request.MachineId);
            Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM Receipts"));
            Assert.Equal(0L, Scalar("SELECT Total FROM ReceiptCount"));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000001)]
    public void InvalidReceiptBudgetsAreRejected(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(limit));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
