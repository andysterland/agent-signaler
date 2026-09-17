using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AgentSignaler.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentSignaler.Integration.Tests;

internal sealed class LoopbackTlsDashboard : IAsyncDisposable
{
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _leaf;
    private readonly WebApplication _application;
    private readonly HttpClient _upstream = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false
    });
    private int _plaintextConnections;

    public ConcurrentQueue<ObservedRequest> Requests { get; } = new();
    public Uri HttpsAddress { get; private set; } = null!;
    private Uri HttpAddress { get; set; } = null!;
    public int PlaintextConnections => Volatile.Read(ref _plaintextConnections);
    public int? RedirectStatus { get; init; }
    public int? TranscriptRedirectStatus { get; init; }
    public string? TranscriptCapabilitiesBody { get; init; }
    public string? HealthBody { get; init; }
    public string HealthContentType { get; init; } = "application/json";
    public bool ChunkedHealth { get; init; }

    public LoopbackTlsDashboard(Uri dashboard)
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=AgentSignaler isolated integration CA",
            rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        _root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=localhost", leafKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        leafRequest.CertificateExtensions.Add(names.Build());
        using var publicLeaf = leafRequest.Create(_root, now.AddMinutes(-1),
            now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var privateLeaf = publicLeaf.CopyWithPrivateKey(leafKey);
        // Schannel needs an imported server key. Without PersistKeySet it is deleted on disposal;
        // neither this short-lived certificate nor its CA is installed in a certificate store.
        _leaf = X509CertificateLoader.LoadPkcs12(privateLeaf.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.DefaultKeySet);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listener =>
            {
                listener.Use(async (connection, next) =>
                {
                    var input = connection.Transport.Input;
                    var read = await input.ReadAsync(connection.ConnectionClosed);
                    if (!read.Buffer.IsEmpty && read.Buffer.FirstSpan[0] != 0x16)
                        Interlocked.Increment(ref _plaintextConnections);
                    input.AdvanceTo(read.Buffer.Start, read.Buffer.Start);
                    await next(connection);
                });
                listener.UseHttps(_leaf);
            });
            options.Listen(IPAddress.Loopback, 0, listener =>
                listener.Use(async (connection, next) =>
                {
                    Interlocked.Increment(ref _plaintextConnections);
                    await next(connection);
                }));
        });
        _application = builder.Build();
        _application.Run(async context =>
        {
            Requests.Enqueue(new(context.Request.Method, context.Request.Path.Value!,
                context.Request.Headers.Cookie.ToString(), context.Request.Headers.Authorization.ToString(),
                context.Request.Headers["X-Tunnel-Authorization"].ToString(), context.Request.Headers.Accept.ToString()));
            context.Response.Headers.SetCookie = "test-session=must-not-be-replayed; Path=/; Secure";
            if (context.Request.Path == "/api/transcripts/v1/capabilities" && TranscriptCapabilitiesBody is { } capabilities)
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(capabilities, context.RequestAborted);
                return;
            }
            if (context.Request.Path == "/health" && HealthBody is { } body)
            {
                context.Response.ContentType = HealthContentType;
                if (ChunkedHealth)
                    await context.Response.StartAsync(context.RequestAborted);
                else
                    context.Response.ContentLength = Encoding.UTF8.GetByteCount(body);
                await context.Response.WriteAsync(body, context.RequestAborted);
                return;
            }
            if ((context.Request.Path.StartsWithSegments("/api/transcripts")
                    ? TranscriptRedirectStatus ?? RedirectStatus : RedirectStatus) is { } status)
            {
                context.Response.StatusCode = status;
                context.Response.Headers.Location = new Uri(HttpAddress, context.Request.Path.Value!).AbsoluteUri;
                return;
            }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                new Uri(dashboard, context.Request.Path.Value!));
            if (context.Request.ContentType is { } contentType)
            {
                request.Content = new StreamContent(context.Request.Body);
                request.Content.Headers.ContentType = new(contentType);
            }
            using var response = await _upstream.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
    }

    public async Task StartAsync()
    {
        await _application.StartAsync();
        var addresses = _application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Select(address => new Uri(address)).ToArray();
        HttpsAddress = addresses.Single(address => address.Scheme == Uri.UriSchemeHttps);
        HttpAddress = addresses.Single(address => address.Scheme == Uri.UriSchemeHttp);
    }

    public HttpClient CreateTrustedClient(RemoteConfiguration configuration)
        => new(CreateTrustedHandler(configuration)) { Timeout = TimeSpan.FromSeconds(10) };

    public SocketsHttpHandler CreateTrustedHandler(RemoteConfiguration configuration)
    {
        var handler = RemoteHttpTransport.CreateHandler(configuration, interactive: true);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.CertificateChainPolicy);
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            CustomTrustStore = { _root },
            ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") },
            RevocationMode = X509RevocationMode.NoCheck
        };
        return handler;
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync();
        await _application.DisposeAsync();
        _upstream.Dispose();
        _leaf.Dispose();
        _root.Dispose();
    }

    internal sealed record ObservedRequest(string Method, string Path, string Cookie, string Authorization,
        string TunnelAuthorization, string Accept);
}
