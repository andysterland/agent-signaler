using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignaler.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Service;

public sealed class CapacityException(string message) : Exception(message);
public sealed class PresenceConflictException(string message) : Exception(message);

public sealed class MachineStore : IDisposable
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly int _receiptLimit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int MaximumMachines = 25;

    private sealed record StoredMachine
    {
        public int SchemaVersion { get; init; } = 1;
        public Guid MachineId { get; init; }
        public string MachineName { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? Note { get; set; }
        public WindowsAppConnection? WindowsAppConnection { get; set; }
        public string Client { get; set; } = "";
        public string ClientVersion { get; set; } = "";
        public AgentEvent? LatestEvent { get; set; }
        public DateTimeOffset LatestReportUtc { get; set; }
        public DateTimeOffset LastContactUtc { get; set; }
        public PresenceMode PresenceMode { get; set; }
        public int? HeartbeatIntervalSeconds { get; set; }
        public long Generation { get; set; }
        public long Sequence { get; set; }
        public bool ExplicitOffline { get; set; }
        public DateTimeOffset? RetiredThroughUtc { get; set; }
        public Dictionary<string, DateTimeOffset> RetiredSources { get; set; } = new(StringComparer.Ordinal);
        public List<SessionSnapshot> Sessions { get; set; } = [];
    }

    public MachineStore(string databasePath, TimeProvider? timeProvider = null, MachineStoreOptions? options = null)
    {
        options ??= new MachineStoreOptions();
        options.Validate();
        _receiptLimit = options.ReceiptLimit;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 10
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            BEGIN IMMEDIATE;
            CREATE TABLE IF NOT EXISTS Machines (Id TEXT PRIMARY KEY, Snapshot TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Receipts (
                EventId TEXT PRIMARY KEY, MachineId TEXT NOT NULL,
                FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_Receipts_MachineId ON Receipts(MachineId);
            CREATE TABLE IF NOT EXISTS ReceiptCount (Id INTEGER PRIMARY KEY CHECK(Id=1), Total INTEGER NOT NULL);
            INSERT OR IGNORE INTO ReceiptCount(Id, Total) SELECT 1, COUNT(*) FROM Receipts;
            CREATE TRIGGER IF NOT EXISTS Receipts_Inserted AFTER INSERT ON Receipts
                BEGIN UPDATE ReceiptCount SET Total=Total+1 WHERE Id=1; END;
            CREATE TRIGGER IF NOT EXISTS Receipts_Deleted AFTER DELETE ON Receipts
                BEGIN UPDATE ReceiptCount SET Total=Total-1 WHERE Id=1; END;
            COMMIT;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        return connection;
    }

    public async Task<StatusResponse> AcceptAsync(StatusRequest request, CancellationToken cancellationToken = default)
    {
        var errors = Protocol.Validate(request);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(request));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var machine = Load(connection, transaction, request.MachineId);
            if (machine?.PresenceMode == PresenceMode.Managed)
                throw new PresenceConflictException("This computer uses managed presence. Restart the tray client to report.");
            using var duplicate = connection.CreateCommand();
            duplicate.Transaction = transaction;
            duplicate.CommandText = "SELECT EXISTS(SELECT 1 FROM Receipts WHERE EventId=$id)";
            duplicate.Parameters.AddWithValue("$id", request.EventId.ToString());
            if ((long)duplicate.ExecuteScalar()! != 0) return new StatusResponse(true);
            using var receiptCount = connection.CreateCommand();
            receiptCount.Transaction = transaction;
            receiptCount.CommandText = "SELECT Total FROM ReceiptCount WHERE Id=1";
            if ((long)receiptCount.ExecuteScalar()! >= _receiptLimit) throw new ReceiptCapacityException();

            if (machine is null)
            {
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM Machines";
                if ((long)count.ExecuteScalar()! >= MaximumMachines)
                    throw new CapacityException("The dashboard supports at most 25 computers. Remove one before registering another.");
                machine = new StoredMachine { MachineId = request.MachineId };
            }

            var now = _timeProvider.GetUtcNow();
            machine.LastContactUtc = now;
            if (request.ReportedAtUtc >= machine.LatestReportUtc)
            {
                machine.LatestReportUtc = request.ReportedAtUtc;
                machine.MachineName = request.MachineName;
                machine.Client = request.Client;
                machine.ClientVersion = request.ClientVersion;
                machine.LatestEvent = request.Event;
            }

            ApplyHook(machine, request, now);
            Save(connection, transaction, machine);
            using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = "INSERT INTO Receipts(EventId, MachineId) VALUES ($event, $machine)";
            receipt.Parameters.AddWithValue("$event", request.EventId.ToString());
            receipt.Parameters.AddWithValue("$machine", request.MachineId.ToString());
            receipt.ExecuteNonQuery();
            transaction.Commit();
            return new StatusResponse(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<PresenceResponse> AcceptAsync(PresenceReport report, CancellationToken cancellationToken = default)
    {
        var errors = PresenceProtocol.Validate(report);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(report));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var machine = Load(connection, transaction, report.MachineId);
            var managed = machine?.PresenceMode == PresenceMode.Managed;
            if (managed && (report.Generation < machine!.Generation ||
                report.Generation == machine.Generation && report.Sequence <= machine.Sequence))
                return new PresenceResponse(true);
            var newRun = !managed || report.Generation > machine!.Generation;
            if (newRun && report.Kind != PresenceKind.Started)
                throw new PresenceConflictException("A new generation must begin with started.");
            if (!newRun && (machine!.ExplicitOffline || report.Kind == PresenceKind.Started))
                throw new PresenceConflictException("This generation is already started or terminal. Start a newer generation.");
            if (machine is null)
            {
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM Machines";
                if ((long)count.ExecuteScalar()! >= MaximumMachines)
                    throw new CapacityException("The dashboard supports at most 25 computers. Remove one before registering another.");
                machine = new StoredMachine { MachineId = report.MachineId };
            }
            var now = _timeProvider.GetUtcNow();
            if (newRun)
            {
                machine.Sessions.Clear();
                machine.RetiredThroughUtc = null;
                machine.RetiredSources.Clear();
                machine.ExplicitOffline = false;
            }
            machine.PresenceMode = PresenceMode.Managed;
            machine.Generation = report.Generation;
            machine.Sequence = report.Sequence;
            machine.LastContactUtc = now;
            machine.MachineName = report.MachineName;
            machine.Client = report.Client;
            machine.ClientVersion = report.ClientVersion;
            if (report.Kind is PresenceKind.Started or PresenceKind.Heartbeat)
            {
                machine.HeartbeatIntervalSeconds = report.HeartbeatIntervalSeconds;
                ReconcileSessions(machine, report.Sessions!, now);
            }
            else if (report.Kind == PresenceKind.Hook)
            {
                var hook = report.Hook!;
                if (hook.ReportedAtUtc >= machine.LatestReportUtc)
                {
                    machine.LatestReportUtc = hook.ReportedAtUtc;
                    machine.LatestEvent = hook.Event;
                }
                ApplyHook(machine, hook, now);
            }
            else machine.ExplicitOffline = true;
            Save(connection, transaction, machine);
            // The persisted run/sequence watermark replaces per-report receipt rows.
            transaction.Commit();
            return new PresenceResponse(false);
        }
        finally { _gate.Release(); }
    }

    private static void ApplyHook(StoredMachine machine, StatusRequest request, DateTimeOffset now)
    {
        var key = SourceIdentity.SessionKey(request.Source, request.SessionId!);
        var index = machine.Sessions.FindIndex(s => SessionKey(s) == key);
        if (index < 0 && IsRetired(machine, request.Source, request.ReportedAtUtc)) return;
        if (index < 0 && machine.Sessions.Count >= Protocol.MaxSessions)
        {
            var incomingScope = SourceIdentity.SessionKey(request.Source, "");
            var retired = machine.Sessions.Where(s => s.UnderlyingState == AgentState.Idle &&
                (s.ResultUntilUtc is null || s.ResultUntilUtc <= now) &&
                (SourceIdentity.SessionKey(s.Source, "") != incomingScope ||
                 s.UpdatedAtUtc < request.ReportedAtUtc)).MinBy(s => s.UpdatedAtUtc);
            if (retired is null)
                throw new CapacityException("Maximum tracked sessions reached; end an active session before starting another.");
            machine.Sessions.Remove(retired);
            Retire(machine, retired.Source, retired.UpdatedAtUtc);
        }
        var previous = index < 0 ? null : machine.Sessions[index];
        var next = StateReducer.Apply(previous, request.SessionId!, request.Event,
            request.ReportedAtUtc, now, request.ToolFailed, request.ToolRequiresUserInput, request.Source);
        if (index < 0) machine.Sessions.Add(next);
        else machine.Sessions[index] = next;
    }

    private static string SessionKey(SessionSnapshot session) =>
        SourceIdentity.SessionKey(session.Source, session.SessionId);

    private static bool IsRetired(StoredMachine machine, SourceDescriptor? source, DateTimeOffset timestamp) =>
        machine.RetiredSources.TryGetValue(SourceIdentity.SessionKey(source, ""), out var watermark) &&
        timestamp <= watermark;

    private static void Retire(StoredMachine machine, SourceDescriptor? source, DateTimeOffset timestamp)
    {
        var scope = SourceIdentity.SessionKey(source, "");
        if (!machine.RetiredSources.TryGetValue(scope, out var watermark))
        {
            if (machine.RetiredSources.Count >= Protocol.MaxSessions)
                throw new CapacityException("Maximum retired source scopes reached; start a new reporting generation.");
            machine.RetiredSources.Add(scope, timestamp);
        }
        else if (timestamp > watermark) machine.RetiredSources[scope] = timestamp;
    }

    private static void ReconcileSessions(StoredMachine machine, IReadOnlyList<SessionSnapshot> snapshot, DateTimeOffset now)
    {
        var ids = snapshot.Select(SessionKey).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in machine.Sessions.Where(s => !ids.Contains(SessionKey(s))))
            Retire(machine, removed.Source, removed.UpdatedAtUtc);
        var previous = machine.Sessions.ToDictionary(SessionKey, StringComparer.Ordinal);
        machine.Sessions = [];
        foreach (var incoming in snapshot)
        {
            if (previous.TryGetValue(SessionKey(incoming), out var existing) && existing.UpdatedAtUtc >= incoming.UpdatedAtUtc)
            {
                machine.Sessions.Add(existing);
                continue;
            }
            if (existing is null && IsRetired(machine, incoming.Source, incoming.UpdatedAtUtc)) continue;
            // Expiry is absolute: repeated snapshots cannot renew a result overlay.
            var until = incoming.ResultUntilUtc;
            if (until > now + Protocol.ResultDuration) until = now + Protocol.ResultDuration;
            machine.Sessions.Add(incoming with
            {
                ResultUntilUtc = until > now ? until : null,
                ResultState = until > now ? incoming.ResultState : null
            });
        }
    }

    public async Task<IReadOnlyList<MachineView>> GetMachinesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Snapshot FROM Machines";
            using var reader = command.ExecuteReader();
            var now = _timeProvider.GetUtcNow();
            var machines = new List<MachineView>();
            while (reader.Read())
            {
                var machine = Deserialize(reader.GetString(0));
                var timeout = machine.PresenceMode == PresenceMode.Managed
                    ? PresenceProtocol.OfflineAfter(machine.HeartbeatIntervalSeconds!.Value) : Protocol.OfflineAfter;
                var deadline = machine.LastContactUtc + timeout;
                var state = machine.ExplicitOffline || now >= deadline ? AgentState.Offline :
                    StateReducer.Aggregate(machine.Sessions, now);
                machines.Add(new MachineView(machine.MachineId, machine.MachineName, machine.DisplayName,
                    machine.Client, machine.ClientVersion, state, machine.LatestEvent,
                    machine.LastContactUtc, machine.Sessions.AsReadOnly(), machine.WindowsAppConnection)
                {
                    Note = machine.Note,
                    PresenceMode = machine.PresenceMode,
                    HeartbeatIntervalSeconds = machine.HeartbeatIntervalSeconds,
                    Generation = machine.Generation,
                    Sequence = machine.Sequence,
                    ExplicitOffline = machine.ExplicitOffline,
                    OfflineDeadlineUtc = deadline,
                    LatestEventUtc = machine.LatestEvent is null ? null : machine.LatestReportUtc
                });
            }
            return machines.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally { _gate.Release(); }
    }

    public Task RenameAsync(Guid id, string? displayName, CancellationToken cancellationToken = default) =>
        UpdateDetailsCoreAsync(id, displayName, null, updateNote: false, cancellationToken);

    public Task UpdateDetailsAsync(Guid id, string? displayName, string? note,
        CancellationToken cancellationToken = default) =>
        UpdateDetailsCoreAsync(id, displayName, note, updateNote: true, cancellationToken);

    private async Task UpdateDetailsCoreAsync(Guid id, string? displayName, string? note,
        bool updateNote, CancellationToken cancellationToken)
    {
        displayName = displayName?.Trim();
        if (displayName is { Length: > 128 } || displayName?.Any(char.IsControl) == true)
            throw new ArgumentException("Display name must be at most 128 characters without control characters.", nameof(displayName));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var machine = Load(connection, transaction, id) ?? throw new KeyNotFoundException("Computer was removed.");
            machine.DisplayName = string.IsNullOrEmpty(displayName) ? null : displayName;
            if (updateNote) machine.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            Save(connection, transaction, machine);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task SetWindowsAppConnectionAsync(Guid machineId, WindowsAppConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            connection.Validate();
            using var database = Open();
            using var transaction = database.BeginTransaction();
            var machine = Load(database, transaction, machineId) ?? throw new KeyNotFoundException("Computer was removed.");
            machine.WindowsAppConnection = connection;
            Save(database, transaction, machine);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task ClearWindowsAppConnectionAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var machine = Load(connection, transaction, machineId) ?? throw new KeyNotFoundException("Computer was removed.");
            machine.WindowsAppConnection = null;
            Save(connection, transaction, machine);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Machines WHERE Id=$id";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    private static StoredMachine? Load(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Snapshot FROM Machines WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() is string json ? Deserialize(json) : null;
    }

    private static StoredMachine Deserialize(string json)
    {
        try
        {
            var snapshot = JsonNode.Parse(json)?.AsObject() ??
                throw new InvalidDataException("The machine database contains an invalid snapshot.");
            // Upgrade persisted snapshots without discarding identity, sessions, or ordering history.
            if (snapshot.Remove("heartbeatWatermarkUtc", out var watermark) && snapshot["retiredThroughUtc"] is null)
                snapshot["retiredThroughUtc"] = watermark;
            if (snapshot["latestEvent"]?.GetValue<string>() == "heartbeat")
                snapshot["latestEvent"] = null;
            var machine = snapshot.Deserialize<StoredMachine>(Protocol.Json) ??
                throw new InvalidDataException("The machine database contains an invalid snapshot.");
            if (machine.SchemaVersion is < 1 or > 2 || machine.Sessions is null ||
                machine.Sessions.Count > Protocol.MaxSessions || machine.RetiredSources is null ||
                machine.RetiredSources.Count > Protocol.MaxSessions ||
                machine.Sessions.Any(s => s is null || s.Source is { IsValid: false } ||
                    string.IsNullOrWhiteSpace(s.SessionId) || s.SessionId.Length > 128 || s.SessionId.Any(char.IsControl)) ||
                machine.Sessions.Select(SessionKey).Distinct(StringComparer.Ordinal).Count() != machine.Sessions.Count)
                throw new InvalidDataException("The machine database contains invalid source identity metadata.");
            // Missing source remains the wire-compatible representation of the legacy CLI scope.
            if (machine.RetiredThroughUtc is { } retired)
            {
                Retire(machine, SourceDescriptor.LegacyCli, retired);
                machine.RetiredThroughUtc = null;
            }
            if (!Enum.IsDefined(machine.PresenceMode) ||
                machine.PresenceMode == PresenceMode.Managed &&
                (machine.Generation <= 0 || machine.Sequence <= 0 ||
                 machine.HeartbeatIntervalSeconds is not { } interval || !PresenceProtocol.IsValidHeartbeatInterval(interval)))
                throw new InvalidDataException("The machine database contains invalid presence ordering metadata.");
            machine.WindowsAppConnection?.Validate();
            return machine;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The machine database contains an invalid snapshot or Windows App connection mapping. " +
                "Back up the database, then restore a valid snapshot or remove the affected computer and register it again.");
        }
    }

    private static void Save(SqliteConnection connection, SqliteTransaction transaction, StoredMachine machine)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Machines(Id, Snapshot) VALUES ($id, $snapshot)
            ON CONFLICT(Id) DO UPDATE SET Snapshot=excluded.Snapshot
            """;
        command.Parameters.AddWithValue("$id", machine.MachineId.ToString());
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(machine with { SchemaVersion = 2 }, Protocol.Json));
        command.ExecuteNonQuery();
    }

    public void Dispose() => _gate.Dispose();
}
