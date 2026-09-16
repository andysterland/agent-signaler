using System.Net;
using System.Text;
using AgentSignaler.Tunneling;
using Xunit;

namespace AgentSignaler.Tunneling.Tests;

public sealed class HealthProbeTests
{
    private static readonly Uri Endpoint = new("https://sample-51839.usw2.devtunnels.ms/");

    [Fact]
    public void PublicTransportUsesCredentiallessSystemProxyAndNormalTls()
    {
        using var handler = AnonymousHealthProbe.CreateHandler(useProxy: true);
        Assert.True(handler.UseProxy);
        Assert.NotNull(handler.Proxy);
        Assert.Null(handler.Proxy.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.Null(handler.Credentials);
        Assert.False(handler.UseDefaultCredentials);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void LoopbackTransportNeverUsesProxy()
    {
        using var handler = AnonymousHealthProbe.CreateHandler(useProxy: false);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Null(handler.Credentials);
    }

    [Fact]
    public void SystemProxyRoutingIsPreservedWithoutInheritingCredentials()
    {
        var configured = new WebProxy("http://proxy.example:8080")
        {
            Credentials = new NetworkCredential("test-user", "test-only")
        };
        var anonymous = new AnonymousHealthProbe.CredentiallessSystemProxy(configured);
        Assert.Equal(configured.GetProxy(Endpoint), anonymous.GetProxy(Endpoint));
        Assert.Equal(configured.IsBypassed(Endpoint), anonymous.IsBypassed(Endpoint));
        Assert.Null(anonymous.Credentials);
        Assert.NotNull(configured.Credentials);
        Assert.Throws<NotSupportedException>(() => anonymous.Credentials = CredentialCache.DefaultCredentials);
    }

    [Fact]
    public async Task LocalAndPublicProbesUseSeparateTransports()
    {
        using var publicHandler = new FakeHandler(_ => Response("""{"protocolVersion":1,"status":"ok"}"""));
        using var localHandler = new FakeHandler(_ => Response("""{"protocolVersion":1,"status":"ok"}"""));
        using var probe = new AnonymousHealthProbe(publicHandler, localHandler);
        await probe.VerifyAsync(new Uri("http://127.0.0.1:51839/"), CancellationToken.None);
        Assert.Equal(1, localHandler.Requests);
        Assert.Equal(0, publicHandler.Requests);
        await probe.VerifyAsync(Endpoint, CancellationToken.None);
        Assert.Equal(1, localHandler.Requests);
        Assert.Equal(1, publicHandler.Requests);
    }

    [Fact]
    public async Task ProbeUsesAnonymousJsonGetOnHealth()
    {
        using var handler = new FakeHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri(Endpoint, "health"), request.RequestUri);
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Response("""{"protocolVersion":1,"status":"ok"}""");
        });
        using var probe = new AnonymousHealthProbe(handler);
        await probe.VerifyAsync(Endpoint, CancellationToken.None);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("""{"protocolVersion":2,"status":"ok"}""")]
    [InlineData("""{"protocolVersion":1,"status":"error"}""")]
    [InlineData("""{"status":"ok"}""")]
    [InlineData("""{"protocolVersion":"1","status":"ok"}""")]
    [InlineData("""{"protocolVersion":1,"status":"ok","status":"error"}""")]
    [InlineData("""{"protocolVersion":1,"status":"ok","unexpected":"incompatible"}""")]
    [InlineData("""<html>login</html>""")]
    [InlineData("null")]
    public async Task InvalidHealthFailsClosed(string body)
    {
        using var probe = new AnonymousHealthProbe(new FakeHandler(_ => Response(body)));
        await Assert.ThrowsAsync<TunnelException>(() => probe.VerifyAsync(Endpoint, CancellationToken.None));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task RedirectAndAuthResponsesAreNotConnected(int code)
    {
        using var handler = new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)code)
        {
            Content = new StringContent("""{"protocolVersion":1,"status":"ok"}""", Encoding.UTF8, "application/json")
        });
        using var probe = new AnonymousHealthProbe(handler);
        await Assert.ThrowsAsync<TunnelException>(() => probe.VerifyAsync(Endpoint, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var probe = new AnonymousHealthProbe(new FakeHandler(_ => Response(new string(' ', 4097))));
        await Assert.ThrowsAsync<TunnelException>(() => probe.VerifyAsync(Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task WrongContentTypeIsRejected()
    {
        using var probe = new AnonymousHealthProbe(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"protocolVersion":1,"status":"ok"}""", Encoding.UTF8, "text/html")
        }));
        await Assert.ThrowsAsync<TunnelException>(() => probe.VerifyAsync(Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task UnsafeUriIsRejectedBeforeRequest()
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("Must not send"));
        using var probe = new AnonymousHealthProbe(handler);
        await Assert.ThrowsAsync<TunnelException>(() => probe.VerifyAsync(new("http://example.com/"), CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(response(request));
        }
    }
}
