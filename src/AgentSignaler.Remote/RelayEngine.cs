using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public sealed record HookData(string SessionId, DateTimeOffset Timestamp, bool ToolFailed,
    bool ToolRequiresUserInput = false, SourceDescriptor? Source = null, string? InvocationId = null);

public static class HookParser
{
    public static HookData Parse(ReadOnlyMemory<byte> input, AgentEvent kind, DateTimeOffset now)
    {
        if (input.Length > 65536 || HookPayloadAdapters.EventName("copilot-cli", kind) is null)
            throw new InvalidDataException("Invalid hook.");
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sessionId", out var idProperty) ||
            idProperty.ValueKind != JsonValueKind.String || !RemoteConfiguration.ValidText(idProperty.GetString(), 128))
            throw new MissingHookSessionException();
        var id = idProperty.GetString();
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("timestamp").GetInt64());
        if (timestamp > now) timestamp = now;
        if (timestamp.Year is < 1970 or > 9998) throw new InvalidDataException("Invalid timestamp.");
        var failed = kind == AgentEvent.PostToolUse &&
            root.TryGetProperty("toolResult", out var result) && result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("resultType", out var status) && status.ValueKind == JsonValueKind.String &&
            status.GetString() == "failure";
        var requiresUserInput = kind is (AgentEvent.PreToolUse or AgentEvent.PostToolUse or AgentEvent.PostToolUseFailure) &&
            root.TryGetProperty("toolName", out var toolName) && toolName.ValueKind == JsonValueKind.String &&
            toolName.GetString() == "ask_user";
        return new HookData(id!, timestamp, failed, requiresUserInput);
    }
}

public sealed class DiagnosticLog(string path)
{
    public void Write(string code)
    {
        // Codes are defined by this application, never exception messages or input data.
        if (string.IsNullOrEmpty(code) || code.Length > 64 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Diagnostics require an application-defined category code.", nameof(code));
        try
        {
            using var held = AtomicFile.Acquire(path + ".lock", TimeSpan.FromMilliseconds(30));
            if (File.Exists(path) && new FileInfo(path).Length >= 32768) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {code}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Agent Signaler diagnostics could not be written.");
        }
    }
}

public sealed record StoredRemoteSession(SessionSnapshot Snapshot, DateTimeOffset SourceTimestamp);
public sealed record StoredRemoteState(int Version, DateTimeOffset LastReportedAtUtc, List<StoredRemoteSession> Sessions);
public sealed record LocalReport(List<SessionSnapshot> Sessions, DateTimeOffset ReportedAtUtc, bool Accepted,
    bool CapacityExceeded = false);
internal sealed record EnrichedRemoteState(int Version, string LegacyHash, StoredRemoteState State,
    Dictionary<string, DateTimeOffset> RetiredSources);

public sealed class SessionStore(string path, DiagnosticLog log)
{
    public List<SessionSnapshot> Update(AgentEvent? kind, HookData? hook, DateTimeOffset now) =>
        UpdateReport(kind, hook, now).Sessions;

    public LocalReport UpdateReport(AgentEvent? kind, HookData? hook, DateTimeOffset now)
    {
        using var held = AtomicFile.Acquire(path + ".lock", TimeSpan.FromMilliseconds(180));
        StoredRemoteState stored;
        var retired = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        byte[]? legacyBytes = null;
        try
        {
            var bytes = AtomicFile.ReadBounded(path, 65536);
            legacyBytes = bytes;
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var legacy = JsonSerializer.Deserialize<List<SessionSnapshot>>(bytes, Protocol.Json) ?? throw new InvalidDataException();
                if (legacy.Any(s => s is null)) throw new InvalidDataException();
                stored = new StoredRemoteState(1, legacy.Select(s => s.UpdatedAtUtc).DefaultIfEmpty(now).Max(),
                    legacy.Select(s => new StoredRemoteSession(s, s.UpdatedAtUtc)).ToList());
            }
            else stored = JsonSerializer.Deserialize<StoredRemoteState>(bytes, Protocol.Json) ?? throw new InvalidDataException();
            if (stored.Version is not (1 or 2) || stored.LastReportedAtUtc.Offset != TimeSpan.Zero ||
                stored.LastReportedAtUtc.Year is < 1970 or > 9998 || stored.Sessions is null ||
                stored.Sessions.Any(s => s is null || s.Snapshot is null || s.SourceTimestamp.Offset != TimeSpan.Zero ||
                    s.SourceTimestamp.Year is < 1970 or > 9998 || s.SourceTimestamp > s.Snapshot.UpdatedAtUtc))
                throw new InvalidDataException();
            var sessions = stored.Sessions.Select(s => s.Snapshot).ToList();
            if (sessions.Count > Protocol.MaxSessions ||
                sessions.Select(s => SourceIdentity.SessionKey(s.Source, s.SessionId)).Distinct(StringComparer.Ordinal).Count() != sessions.Count ||
                sessions.Any(s => !RemoteConfiguration.ValidText(s.SessionId, 128) ||
                    !PresenceProtocol.ValidLatestEvent(s) ||
                    s.Source is { IsValid: false } ||
                    s.UnderlyingState is not (AgentState.Waiting or AgentState.Executing or AgentState.Idle) ||
                    (s.AwaitingUserInput && s.UnderlyingState != AgentState.Waiting) ||
                    s.ResultState is not (null or AgentState.Succeeded or AgentState.Failed) ||
                    (s.ResultState is null) != (s.ResultUntilUtc is null) ||
                    s.UpdatedAtUtc.Year is < 1970 or > 9998 || s.UpdatedAtUtc.Offset != TimeSpan.Zero ||
                    s.UpdatedAtUtc > stored.LastReportedAtUtc ||
                    (s.ResultUntilUtc is { } expiry &&
                        (expiry.Offset != TimeSpan.Zero || expiry - s.UpdatedAtUtc > Protocol.ResultDuration))))
                throw new InvalidDataException();
            stored = stored with
            {
                Version = 2,
                Sessions = stored.Sessions.Select(s => s with
                {
                    Snapshot = s.Snapshot with { Source = s.Snapshot.Source ?? SourceDescriptor.LegacyCli }
                }).ToList()
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            legacyBytes = null;
            stored = new StoredRemoteState(2, now.AddTicks(-1), []);
            log.Write("state-missing-or-invalid-idle");
        }
        // The base file remains readable by older Clients. Only matching sidecars may enrich it.
        byte[]? matchingEnrichment = null;
        if (legacyBytes is not null)
        {
            foreach (var candidate in new[] { path + ".events-v1", path + ".events-v1.previous" })
            {
                if (!File.Exists(candidate)) continue;
                var enrichment = AtomicFile.ReadBounded(candidate, 131072);
                var enriched = JsonSerializer.Deserialize<EnrichedRemoteState>(enrichment, PresenceProtocol.Json)
                    ?? throw new InvalidDataException("Invalid enriched session state. Restore both state files from backup.");
                if (enriched.Version != 3)
                    throw new InvalidDataException("Incompatible session state. Restore a compatible backup before restarting.");
                if (enriched.LegacyHash == Convert.ToHexString(SHA256.HashData(legacyBytes)))
                {
                    if (enriched.State?.Sessions is null || enriched.RetiredSources is null ||
                        enriched.State.Sessions.Count > Protocol.MaxSessions || enriched.RetiredSources.Count > Protocol.MaxSessions ||
                        enriched.RetiredSources.Any(p => p.Key.Length != 64 || p.Key.Any(c => !char.IsAsciiHexDigit(c)) ||
                            p.Value.Offset != TimeSpan.Zero || p.Value.Year is < 1970 or > 9998) ||
                        enriched.State.Sessions.Any(s => s?.Snapshot is null || !PresenceProtocol.ValidLatestEvent(s.Snapshot)) ||
                        !JsonSerializer.SerializeToUtf8Bytes(LegacyState(enriched.State), Protocol.Json).AsSpan().SequenceEqual(legacyBytes))
                        throw new InvalidDataException("Invalid enriched session state. Restore both state files from backup.");
                    stored = enriched.State;
                    retired = enriched.RetiredSources;
                    matchingEnrichment = enrichment;
                    break;
                }
            }
        }
        var reportedAt = now > stored.LastReportedAtUtc ? now : stored.LastReportedAtUtc.AddTicks(1);
        var entries = stored.Sessions.ToList();
        var originalRetired = new Dictionary<string, DateTimeOffset>(retired, StringComparer.Ordinal);
        var accepted = true;
        var capacityExceeded = false;
        if (kind is { } eventKind && hook is not null)
        {
            var key = SourceIdentity.SessionKey(hook.Source, hook.SessionId);
            var previous = entries.Find(s => SourceIdentity.SessionKey(s.Snapshot.Source, s.Snapshot.SessionId) == key);
            var scope = SourceIdentity.SessionKey(hook.Source, "");
            accepted = previous is null ? !retired.TryGetValue(scope, out var watermark) || hook.Timestamp > watermark
                : hook.Timestamp >= previous.SourceTimestamp;
            if (accepted)
            {
                // Local source timestamps reject late session events; the global clock orders wire reports.
                var next = StateReducer.Apply(previous?.Snapshot, hook.SessionId, eventKind, reportedAt, reportedAt,
                    hook.ToolFailed, hook.ToolRequiresUserInput) with { Source = hook.Source ?? SourceDescriptor.LegacyCli };
                entries.RemoveAll(s => SourceIdentity.SessionKey(s.Snapshot.Source, s.Snapshot.SessionId) == key);
                entries.Add(new StoredRemoteSession(next, hook.Timestamp));
            }
        }
        while (entries.Count > Protocol.MaxSessions ||
            !PresenceProtocol.FitsSnapshot(entries.Select(s => s.Snapshot)))
        {
            var ended = entries.Where(s => s.Snapshot.UnderlyingState == AgentState.Idle &&
                (kind == AgentEvent.SessionEnd || hook is null || SourceIdentity.SessionKey(s.Snapshot.Source, s.Snapshot.SessionId) !=
                    SourceIdentity.SessionKey(hook.Source, hook.SessionId)))
                .OrderBy(s => s.SourceTimestamp).FirstOrDefault(s =>
                    retired.ContainsKey(SourceIdentity.SessionKey(s.Snapshot.Source, "")) || retired.Count < Protocol.MaxSessions);
            if (ended is null)
            {
                entries = stored.Sessions.ToList();
                retired = originalRetired;
                accepted = false;
                capacityExceeded = true;
                log.Write("session-capacity-rejected");
                break;
            }
            var scope = SourceIdentity.SessionKey(ended.Snapshot.Source, "");
            retired[scope] = retired.TryGetValue(scope, out var previous) && previous > ended.SourceTimestamp
                ? previous : ended.SourceTimestamp;
            entries.Remove(ended);
        }
        var state = new StoredRemoteState(2, reportedAt, entries);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(LegacyState(state), Protocol.Json);
        var sidecar = JsonSerializer.SerializeToUtf8Bytes(
            new EnrichedRemoteState(3, Convert.ToHexString(SHA256.HashData(serialized)), state, retired), PresenceProtocol.Json);
        // Write the enrichment first: a crash leaves either an old coherent base or a matching pair.
        if (matchingEnrichment is not null) AtomicFile.Write(path + ".events-v1.previous", matchingEnrichment);
        AtomicFile.Write(path + ".events-v1", sidecar);
        AtomicFile.Write(path, serialized);
        return new LocalReport(entries.Select(s => s.Snapshot).ToList(), reportedAt, accepted, capacityExceeded);
    }

    private static StoredRemoteState LegacyState(StoredRemoteState state) => state with
    {
        Version = 2,
        Sessions = state.Sessions.Select(s => s with { Snapshot = PresenceProtocol.ProjectStoredSession(s.Snapshot) }).ToList()
    };
}

public sealed class RelayEngine(HttpClient? client = null)
{
    public async Task<bool> SendAsync(RemoteConfiguration config, StatusRequest request, CancellationToken cancellationToken)
    {
        if (Protocol.Validate(request).Count != 0) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Protocol.Json);
        if (bytes.Length > Protocol.MaxBodyBytes) return false;
        using var ownedClient = client is null ? RemoteHttpTransport.CreateClient(config) : null;
        var transport = client ?? ownedClient!;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(2200));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var delay = TimeSpan.FromMilliseconds(80);
            try
            {
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new("application/json");
                using var message = new HttpRequestMessage(HttpMethod.Post, config.Endpoint) { Content = content };
                message.Headers.Accept.Add(new("application/json"));
                using var response = await transport.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                if (response.StatusCode == HttpStatusCode.Accepted) return true;
                if (attempt != 0 || response.StatusCode is not (HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
                    HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)) return false;
                var retryAfter = response.Headers.RetryAfter;
                var requestedDelay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
                if (requestedDelay > delay) delay = requestedDelay.Value;
            }
            catch (HttpRequestException ex) when (attempt == 0 &&
                ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded) { }
            if (delay >= TimeSpan.FromMilliseconds(2200) - elapsed.Elapsed) return false;
            var retryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var remaining = delay;
            do
            {
                // Windows timers can complete early; Retry-After is a minimum, not an approximate delay.
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(1)
                    ? TimeSpan.FromMilliseconds(1) : remaining, budget.Token);
                remaining = delay - System.Diagnostics.Stopwatch.GetElapsedTime(retryStarted);
            } while (remaining > TimeSpan.Zero);
        }
        return false;
    }

    public async Task<int> RunAsync(string[] args, Stream input, CancellationToken cancellationToken = default)
    {
        var mode = args.FirstOrDefault();
        var forgiving = mode == "hook";
        DiagnosticLog? log = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(2200));
        try
        {
            if (mode is not ("hook" or "test")) return 2;
            var configPath = RemotePaths.DefaultConfig;
            for (var i = 1; i + 1 < args.Length; i += 2)
                if (args[i] == "--config") configPath = args[i + 1];
            if (!Path.IsPathFullyQualified(configPath)) throw new InvalidDataException();
            log = new DiagnosticLog(RemotePaths.Log(configPath));
            string? eventName = null, sourceKind = null, scope = null, sourceVersion = null, adapter = null;
            Guid? probeId = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !seen.Add(args[i])) throw new InvalidDataException();
                switch (args[i])
                {
                    case "--config": configPath = args[i + 1]; break;
                    case "--event" when mode == "hook":
                        eventName = args[i + 1];
                        break;
                    case "--source" when mode == "hook": sourceKind = args[i + 1]; break;
                    case "--scope" when mode == "hook": scope = args[i + 1]; break;
                    case "--source-version" when mode == "hook": sourceVersion = args[i + 1]; break;
                    case "--adapter" when mode == "hook": adapter = args[i + 1]; break;
                    case "--probe" when mode == "hook":
                        if (!Guid.TryParseExact(args[i + 1], "D", out var probe) || probe == Guid.Empty)
                            throw new InvalidDataException();
                        probeId = probe;
                        break;
                    default: throw new InvalidDataException();
                }
            }
            if (!Path.IsPathFullyQualified(configPath)) throw new InvalidDataException();
            var config = RemoteConfiguration.Load(configPath);
            if (mode == "test")
            {
                using var ownedClient = client is null ? RemoteHttpTransport.CreateClient(config) : null;
                await DashboardConnection.TestAsync(config, client ?? ownedClient!, budget.Token);
                log.Write("connection-test-succeeded");
                return 0;
            }
            if (eventName is null) throw new InvalidDataException();
            SourceDescriptor? source = null;
            if (sourceKind is not null || scope is not null || sourceVersion is not null || adapter is not null)
            {
                if (sourceKind is null || scope is null || adapter != sourceKind) throw new InvalidDataException();
                source = new SourceDescriptor(sourceKind, scope, sourceVersion ?? "unknown");
                if (!source.IsValid) throw new InvalidDataException();
            }
            adapter ??= "copilot-cli";
            var kind = HookPayloadAdapters.Event(adapter, eventName);
            if (probeId is null && !HookPayloadAdapters.IsAllowed(config, kind, source)) throw new InvalidDataException();
            if (probeId is not null && source is null) throw new InvalidDataException();
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await input.ReadAsync(buffer, budget.Token);
                if (count == 0) break;
                if (bytes.Length + count > 65536) throw new InvalidDataException();
                bytes.Write(buffer, 0, count);
            }
            var payload = bytes.ToArray();
            var hook = HookPayloadAdapters.Parse(adapter, eventName, payload, DateTimeOffset.UtcNow, source);
            var response = await ClientIpc.SendAsync(configPath,
                new(ClientIpc.LegacyVersion, probeId is null ? "hook" : "probe", kind, hook, ProbeId: probeId), budget.Token);
            log.Write(response.Accepted ? "hook-accepted-locally" : "client-not-accepting");
            if (probeId is null && response.Accepted && config.Version >= 5 && config.DetailedReportingEnabled &&
                config.BaseUri.Scheme == Uri.UriSchemeHttps && !budget.IsCancellationRequested)
            {
                using var detailBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                detailBudget.CancelAfter(TimeSpan.FromMilliseconds(200));
                var negotiation = await ClientIpc.SendAsync(configPath,
                    new(ClientIpc.Version, "transcript-negotiate", TranscriptSource: hook.Source ?? SourceDescriptor.LegacyCli),
                    detailBudget.Token);
                if (negotiation is { Accepted: true, Transcript: { Enabled: true } capability } &&
                    !detailBudget.IsCancellationRequested)
                {
                    var observation = TranscriptHookProjection.Parse(adapter, eventName, payload, hook, capability.SettingsRevision);
                    if (observation is not null)
                    {
                        var reference = TranscriptHookProjection.ReadReference(adapter, eventName, payload,
                            observation, capability.AssistantFile);
                        var request = reference is null
                            ? new ClientIpcRequest(ClientIpc.Version, "transcript-hook", Transcript: observation)
                            : new ClientIpcRequest(ClientIpc.Version, "transcript-read", TranscriptRead: reference);
                        var detail = await ClientIpc.SendAsync(configPath, request, detailBudget.Token);
                        if (!detail.Accepted) log.Write("transcript-not-accepted");
                    }
                }
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or
            HttpRequestException or OperationCanceledException or System.Security.SecurityException or FormatException)
        {
            log?.Write(ex is MissingHookSessionException ? "hook-missing-stable-session" : "operation-failed");
            return forgiving ? 0 : 1;
        }
    }
}
