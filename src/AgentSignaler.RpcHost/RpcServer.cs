using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSignaler.RpcHost;

internal sealed class RpcServer(IRpcApplication application, int port) : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private WebApplication? server;
    private int controller;
    private int stopAdmission;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        server = CreateBuilder(port).Build();
        server.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            KeepAliveTimeout = TimeSpan.FromSeconds(60)
        });
        server.Run(HandleAsync);
        await server.StartAsync(cancellationToken);
    }

    internal static WebApplicationBuilder CreateBuilder(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(RpcServer).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Configuration.Sources.Clear();
        // Hosting setters require a writable provider; retain only the captured bootstrap values.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHostDefaults.ApplicationKey] = builder.Environment.ApplicationName,
            [WebHostDefaults.EnvironmentKey] = builder.Environment.EnvironmentName,
            [WebHostDefaults.ContentRootKey] = builder.Environment.ContentRootPath
        });
        builder.WebHost.UseUrls([]);
        builder.WebHost.PreferHostingUrls(false);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxRequestHeadersTotalSize = 8192;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.MaxConcurrentUpgradedConnections = 1;
            options.Listen(IPAddress.Loopback, port);
            options.Listen(IPAddress.IPv6Loopback, port);
        });
        return builder;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var isRpc = context.Request.Path == "/rpc";
        if (!RpcEndpointPolicy.IsRequestAllowed(context, port, isRpc))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (context.Request.Path == "/health" && HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"status\":\"transportReady\",\"protocolVersion\":1}", context.RequestAborted);
            return;
        }
        if (!isRpc)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (Volatile.Read(ref stopAdmission) != 0 || Interlocked.CompareExchange(ref controller, 1, 0) != 0)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }
        var deferredRelease = false;
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var connection = new RpcConnection(socket, application, stopping.Token);
            await connection.RunAsync();
            var drain = connection.Drain;
            if (!drain.IsCompleted)
            {
                deferredRelease = true;
                _ = ReleaseAfterDrainAsync(drain);
                return;
            }
        }
        finally
        {
            if (!deferredRelease) Interlocked.Exchange(ref controller, 0);
        }
    }

    private async Task ReleaseAfterDrainAsync(Task pending)
    {
        try { await pending; }
        finally
        {
            application.ReportDrainCompleted();
            Interlocked.Exchange(ref controller, 0);
        }
    }

    public void StopAdmission(bool cancelConnections = true)
    {
        Interlocked.Exchange(ref stopAdmission, 1);
        if (cancelConnections) stopping.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        StopAdmission();
        if (server is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await server.StopAsync(deadline.Token); }
            finally { await server.DisposeAsync(); }
        }
        stopping.Dispose();
    }
}
