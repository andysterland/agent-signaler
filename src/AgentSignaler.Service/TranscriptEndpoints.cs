using System.Text.Json;
using AgentSignaler.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Service;

/// <summary>Transcript-only admission; buckets exist only for bounded known managed machines.</summary>
internal sealed class TranscriptIngress(TranscriptStore store, MachineStore machines, TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<Guid, Bucket> _machines = [];
    private readonly HashSet<Guid> _activeMachines = [];
    private Bucket? _global;
    private int _active;
    private sealed class Bucket(DateTimeOffset now, double tokens)
    {
        public DateTimeOffset Updated = now;
        public double Tokens = tokens;
        public bool Active;
    }

    public bool TryEnter()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _global ??= new(now, 40);
            if (_active >= 4 || !Take(_global, now, 20, 40)) return false;
            _active++;
            return true;
        }
    }

    public async ValueTask<bool> TryEnterMachineAsync(Guid id, CancellationToken cancellationToken)
    {
        var managed = (await machines.GetMachinesAsync(cancellationToken).ConfigureAwait(false))
            .Where(m => m.PresenceMode == PresenceMode.Managed).Select(m => m.MachineId).ToHashSet();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_activeMachines.Contains(id)) return false;
            foreach (var stale in _machines.Where(p => !p.Value.Active && !managed.Contains(p.Key)).Select(p => p.Key).ToArray())
                _machines.Remove(stale);
            // Unknown IDs get no attacker-controlled rate-bucket allocation.
            if (!managed.Contains(id))
            {
                _activeMachines.Add(id);
                return true;
            }
            if (!_machines.TryGetValue(id, out var bucket))
            {
                if (_machines.Count >= 25) return false;
                bucket = new(_time.GetUtcNow(), 10);
                _machines.Add(id, bucket);
            }
            if (bucket.Active || !Take(bucket, _time.GetUtcNow(), 5, 10)) return false;
            bucket.Active = true;
            _activeMachines.Add(id);
            return true;
        }
    }

    public void Exit(Guid? machine)
    {
        lock (_gate)
        {
            _active--;
            if (machine is { } id)
            {
                _activeMachines.Remove(id);
                if (_machines.TryGetValue(id, out var bucket)) bucket.Active = false;
            }
        }
    }

    private static bool Take(Bucket bucket, DateTimeOffset now, int rate, int burst)
    {
        bucket.Tokens = Math.Min(burst, bucket.Tokens + Math.Max(0, (now - bucket.Updated).TotalSeconds) * rate);
        bucket.Updated = now;
        if (bucket.Tokens < 1) return false;
        bucket.Tokens--;
        return true;
    }

    public async Task<IResult> HandleAsync(HttpContext context, string operation, CancellationToken cancellationToken)
    {
        if (!store.Enabled || !store.Ready)
            return Error(!store.Enabled ? TranscriptRejection.Disabled : TranscriptRejection.Unavailable);
        if (!TryEnter()) return Busy(context);
        Guid? enteredMachine = null;
        try
        {
            if (!context.Request.HasJsonContentType()) return Error(TranscriptRejection.ContentType);
            var maximum = operation == "events" ? TranscriptProtocol.MaxEventBytes : TranscriptProtocol.MaxControlBytes;
            if (context.Request.ContentLength > maximum) return Error(TranscriptRejection.Oversized);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
            // Four pre-admitted readers own fixed bounded scratch; no content-bearing pool.
            var buffer = new byte[maximum + 1];
            var length = 0;
            while (length <= maximum)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (length > maximum) return Error(TranscriptRejection.Oversized);
            }
            var body = buffer.AsMemory(0, length);
            object request;
            Guid machine;
            switch (operation)
            {
                case "capabilities":
                    var capabilities = JsonSerializer.Deserialize<TranscriptCapabilitiesRequest>(body.Span, TranscriptProtocol.Json);
                    if (capabilities is null || !TranscriptProtocol.Validate(capabilities)) return Error(TranscriptRejection.InvalidSchema);
                    request = capabilities; machine = capabilities.MachineId; break;
                case "open":
                    var open = JsonSerializer.Deserialize<TranscriptOpenRequest>(body.Span, TranscriptProtocol.Json);
                    if (open is null || !TranscriptProtocol.Validate(open)) return Error(TranscriptRejection.InvalidSchema);
                    request = open; machine = open.MachineId; break;
                case "close":
                    var close = JsonSerializer.Deserialize<TranscriptCloseRequest>(body.Span, TranscriptProtocol.Json);
                    if (close is null || !TranscriptProtocol.Validate(close)) return Error(TranscriptRejection.InvalidSchema);
                    request = close; machine = close.MachineId; break;
                default:
                    var value = JsonSerializer.Deserialize<TranscriptEvent>(body.Span, TranscriptProtocol.Json);
                    if (value is null || !TranscriptProtocol.Validate(value)) return Error(TranscriptRejection.InvalidSchema);
                    request = value; machine = value.MachineId; break;
            }
            if (!await TryEnterMachineAsync(machine, timeout.Token).ConfigureAwait(false)) return Busy(context);
            enteredMachine = machine;
            timeout.Token.ThrowIfCancellationRequested();
            if (!store.Enabled || !store.Ready)
                return Error(!store.Enabled ? TranscriptRejection.Disabled : TranscriptRejection.Unavailable);
            return request switch
            {
                TranscriptCapabilitiesRequest => TypedResults.Json(store.GetCapabilities(), TranscriptProtocol.Json),
                TranscriptOpenRequest open => TypedResults.Json(await store.OpenAsync(open, timeout.Token).ConfigureAwait(false), TranscriptProtocol.Json),
                TranscriptCloseRequest close => TypedResults.Json(store.Close(close), TranscriptProtocol.Json),
                TranscriptEvent value => TypedResults.Json(store.Accept(value), TranscriptProtocol.Json, statusCode: 202),
                _ => Error(TranscriptRejection.InvalidSchema)
            };
        }
        catch (TranscriptRejectedException error)
        {
            if (error.Category == TranscriptRejection.Capacity) context.Response.Headers.RetryAfter = "1";
            return Error(error.Category);
        }
        catch (JsonException) { return Error(TranscriptRejection.InvalidSchema); }
        catch (BadHttpRequestException error)
        {
            return Error(error.StatusCode == 413 ? TranscriptRejection.Oversized : TranscriptRejection.InvalidSchema);
        }
        catch (OperationCanceledException) { return Error(TranscriptRejection.Timeout); }
        catch (IOException) { return Error(TranscriptRejection.Unavailable); }
        catch (SqliteException) { return Error(TranscriptRejection.Unavailable); }
        catch (InvalidDataException) { return Error(TranscriptRejection.Unavailable); }
        finally { Exit(enteredMachine); }
    }

    private IResult Busy(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "1";
        return Error(TranscriptRejection.RateLimited);
    }

    private IResult Error(TranscriptRejection category)
    {
        var status = category switch
        {
            TranscriptRejection.Oversized => 413,
            TranscriptRejection.ContentType => 415,
            TranscriptRejection.Capacity or TranscriptRejection.RateLimited => 429,
            TranscriptRejection.Disabled or TranscriptRejection.Unavailable => 503,
            TranscriptRejection.Timeout => 408,
            TranscriptRejection.InvalidSchema => 400,
            _ => 409
        };
        return TypedResults.Json(new TranscriptError(category, store.ReceiverEpoch, store.OpenRevision),
            TranscriptProtocol.Json, statusCode: status);
    }
}

internal static class TranscriptEndpoints
{
    public static void MapTranscripts(this WebApplication app, TranscriptStore store, MachineStore machines)
    {
        var ingress = new TranscriptIngress(store, machines);
        foreach (var (path, operation) in new[]
        {
            (TranscriptProtocol.CapabilitiesPath, "capabilities"), (TranscriptProtocol.OpenPath, "open"),
            (TranscriptProtocol.EventsPath, "events"), (TranscriptProtocol.ClosePath, "close")
        })
        {
            var endpoint = app.MapPost(path, (HttpContext context, CancellationToken cancellationToken) =>
                ingress.HandleAsync(context, operation, cancellationToken))
                .WithName($"TranscriptV1{operation}")
                .WithSummary("Receive bounded anonymous prototype transcript control or volatile content.")
                .WithDescription("Internet-mode owned-tunnel readiness is required. Machine/source identity is not authenticated. No network transcript reads.")
                .Produces<TranscriptError>(400).Produces<TranscriptError>(409).Produces<TranscriptError>(413)
                .Produces<TranscriptError>(415).Produces<TranscriptError>(429).Produces<TranscriptError>(503);
            switch (operation)
            {
                case "capabilities": endpoint.Produces<TranscriptCapabilitiesResponse>(200); break;
                case "open": endpoint.Produces<TranscriptOpenResponse>(200); break;
                case "close": endpoint.Produces<TranscriptCloseResponse>(200); break;
                default: endpoint.Produces<TranscriptAcknowledgement>(202); break;
            }
        }
    }
}
