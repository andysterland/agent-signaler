using System.Text.Json;
using System.Threading.RateLimiting;
using AgentSignaler.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Service;

public sealed class DashboardServer : IAsyncDisposable
{
    private readonly WebApplication _application;

    public DashboardServer(MachineStore store, int port, Action? onConfiguratorTestConnection = null,
        DashboardServerOptions? options = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Choose a port between 1024 and 65535.");
        options ??= new DashboardServerOptions();
        options.Validate();
        ListenerMode = options.ListenerMode;
        Transcripts = new TranscriptStore(async (machineId, generation, cancellationToken) =>
            (await store.GetMachinesAsync(cancellationToken).ConfigureAwait(false)).Any(machine =>
                machine.MachineId == machineId && machine.PresenceMode == PresenceMode.Managed &&
                machine.Generation == generation && !machine.ExplicitOffline));
        Transcripts.SetEnabled(options.ReceiveDetailedConversations);
        Transcripts.SetReadiness(ListenerMode == DashboardListenerMode.Internet && options.TranscriptTunnelReady);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // Request logging is intentionally disabled: no payload, endpoint names or peer data are retained.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            if (ListenerMode == DashboardListenerMode.Internet) options.ListenLocalhost(port);
            else options.ListenAnyIP(port);
            options.Limits.MaxRequestBodySize = Protocol.MaxBodyBytes;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            options.Limits.MaxConcurrentConnections = 100;
        });
        if (ListenerMode == DashboardListenerMode.Internet)
        {
            builder.Services.AddRateLimiter(limits =>
            {
                limits.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                    PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                        RateLimitPartition.GetConcurrencyLimiter("global", _ => new ConcurrencyLimiterOptions
                        {
                            PermitLimit = options.ConcurrentRequestLimit, QueueLimit = 0
                        })),
                    PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                        RateLimitPartition.GetTokenBucketLimiter("global", _ => new TokenBucketRateLimiterOptions
                        {
                            TokenLimit = options.RequestBurstLimit,
                            TokensPerPeriod = options.RequestsPerSecond,
                            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                            AutoReplenishment = true,
                            QueueLimit = 0
                        })));
                limits.OnRejected = async (rejected, _) =>
                {
                    rejected.HttpContext.Response.Headers.RetryAfter = "1";
                    await Error(429, "Receiver is busy. Retry later.").ExecuteAsync(rejected.HttpContext);
                };
            });
        }
        _application = builder.Build();
        if (ListenerMode == DashboardListenerMode.Internet) _application.UseRateLimiter();
        if (ListenerMode == DashboardListenerMode.Internet) _application.MapTranscripts(Transcripts, store);
        _application.MapGet("/health", (HttpContext context) =>
        {
            var connectionTest = context.Request.Headers[Protocol.ConnectionTestHeader];
            if (connectionTest.Count == 1 && connectionTest[0] == "1")
                onConfiguratorTestConnection?.Invoke();
            return Results.Json(new HealthResponse(Protocol.Version, "ok"), Protocol.Json);
        });
        foreach (var version in new[] { PresenceProtocol.Version, PresenceProtocol.SourceVersion })
        {
        _application.MapGet($"/api/v{version}/health", (HttpContext context, CancellationToken cancellationToken) =>
        {
            var connectionTest = context.Request.Headers[Protocol.ConnectionTestHeader];
            if (connectionTest.Count == 1 && connectionTest[0] == "1")
                onConfiguratorTestConnection?.Invoke();
            return TypedResults.Json(new PresenceHealthResponse(version, "ok"), Protocol.Json);
        }).WithName($"PresenceHealthV{version}").WithSummary("Check managed presence protocol support.");
        _application.MapPost($"/api/v{version}/reports", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!context.Request.HasJsonContentType()) return Error(415, "Content-Type must be application/json.");
            if (context.Request.ContentLength > Protocol.MaxBodyBytes) return Error(413, "Request body is too large.");
            PresenceReport? report;
            try
            {
                using var body = new MemoryStream();
                var buffer = new byte[4096];
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var read = await context.Request.Body.ReadAsync(buffer, budget.Token);
                    if (read == 0) break;
                    if (body.Length + read > Protocol.MaxBodyBytes) return Error(413, "Request body is too large.");
                    body.Write(buffer, 0, read);
                }
                report = JsonSerializer.Deserialize<PresenceReport>(body.GetBuffer().AsSpan(0, (int)body.Length), PresenceProtocol.Json);
            }
            catch (BadHttpRequestException error) { return Error(error.StatusCode, "Invalid or oversized HTTP request."); }
            catch (JsonException) { return Error(400, "Malformed JSON, missing required fields or unsupported property values."); }
            catch (OperationCanceledException) { return Error(408, "Request body was not received in time."); }
            if (report is null) return Error(400, "A presence report object is required.");
            if (report.ProtocolVersion != version) return Error(400, "Protocol version must match the endpoint.");
            var errors = PresenceProtocol.Validate(report);
            if (errors.Count > 0) return Results.Json(new ValidationResponse(errors), Protocol.Json, statusCode: 400);
            try
            {
                var result = await store.AcceptAsync(report, cancellationToken);
                return Results.Json(result, Protocol.Json, statusCode: 202);
            }
            catch (PresenceConflictException error) { return Error(409, error.Message); }
            catch (CapacityException error) { return Error(409, error.Message); }
            catch (SqliteException) { return Error(503, "State could not be stored. Check the dashboard database and retry."); }
            catch (InvalidDataException) { return Error(503, "Stored state needs local repair before reporting can resume."); }
        }).WithName($"AcceptPresenceReportV{version}").WithSummary("Commit an ordered managed presence report.")
            .Produces<PresenceResponse>(202).Produces<ValidationResponse>(400).Produces<ValidationResponse>(409)
            .Produces<ValidationResponse>(413).Produces<ValidationResponse>(415).Produces<ValidationResponse>(503);
        }
        _application.MapPost("/api/v1/status", async (HttpContext context) =>
        {
            if (!context.Request.HasJsonContentType())
                return Error(415, "Content-Type must be application/json.");
            if (context.Request.ContentLength > Protocol.MaxBodyBytes)
                return Error(413, $"Request exceeds {Protocol.MaxBodyBytes} bytes.");
            StatusRequest? request;
            try
            {
                // The limit also applies to chunked requests with no Content-Length.
                using var body = new MemoryStream();
                var buffer = new byte[4096];
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                budget.CancelAfter(TimeSpan.FromSeconds(5));
                while (true)
                {
                    var read = await context.Request.Body.ReadAsync(buffer, budget.Token);
                    if (read == 0) break;
                    if (body.Length + read > Protocol.MaxBodyBytes) return Error(413, "Request body is too large.");
                    body.Write(buffer, 0, read);
                }
                using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.EnumerateObject().Any(p => p.Name.Equals("source", StringComparison.OrdinalIgnoreCase)))
                    return Error(400, "Source fields require presence v3.");
                request = document.RootElement.Deserialize<StatusRequest>(Protocol.Json);
            }
            catch (BadHttpRequestException error) { return Error(error.StatusCode, "Invalid or oversized HTTP request."); }
            catch (JsonException) { return Error(400, "Malformed JSON, missing required fields or unsupported property values."); }
            catch (OperationCanceledException) { return Error(408, "Request body was not received in time."); }
            if (request is null) return Error(400, "A status request object is required.");
            var errors = Protocol.Validate(request);
            if (errors.Count > 0) return Results.Json(new ValidationResponse(errors), Protocol.Json, statusCode: 400);
            try
            {
                var result = await store.AcceptAsync(request, context.RequestAborted);
                return Results.Json(result, Protocol.Json, statusCode: 202);
            }
            catch (CapacityException error) { return Error(409, error.Message); }
            catch (PresenceConflictException error) { return Error(409, error.Message); }
            catch (ReceiptCapacityException)
            {
                context.Response.Headers.RetryAfter = "60";
                return Error(503, "Receipt storage is full. Remove a computer locally to release its replay history before retrying.");
            }
            catch (SqliteException) { return Error(503, "State could not be stored. Check the dashboard database and retry."); }
        });
    }

    public DashboardListenerMode ListenerMode { get; }
    public TranscriptStore Transcripts { get; }
    public void SetTranscriptReadiness(bool ready) =>
        Transcripts.SetReadiness(ListenerMode == DashboardListenerMode.Internet && ready);

    private static IResult Error(int status, string message) =>
        Results.Json(new ValidationResponse([message]), Protocol.Json, statusCode: status);

    public Task StartAsync(CancellationToken cancellationToken = default) => _application.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Transcripts.SetReadiness(false);
        return _application.StopAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync()
    {
        Transcripts.Dispose();
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
