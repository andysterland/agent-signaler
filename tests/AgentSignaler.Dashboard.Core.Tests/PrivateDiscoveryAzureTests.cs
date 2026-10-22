using System.Net;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgentSignaler.Dashboard.Core.Tests;

public sealed class PrivateDiscoveryAzureTests
{
    private static readonly PrivateDiscoveryTarget Target = new(Guid.NewGuid(), Guid.NewGuid(), "owned-group", "ownedaccount");
    private const string Policy = "as-11111111111111111111111111111111";

    [Theory]
    [InlineData("{\"serviceSasToken\":\"secret\",\"serviceSasToken\":\"secret\"}")]
    [InlineData("{\"serviceSasToken\":\"secret\",\"extra\":true}")]
    public async Task ArmRejectsDuplicateOrUnknownSasResponseProperties(string json)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") })));
        using var adapter = new PrivateDiscoveryAzure(Target, new Credential(), http);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => adapter.CreateReadSasAsync("agentsignaler-test", Policy, default));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArmRejectsOversizedStreamedResponseWithoutExposingRawOutput()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('s', 65537)))) })));
        using var adapter = new PrivateDiscoveryAzure(Target, new Credential(), http);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => adapter.CreateReadSasAsync("agentsignaler-test", Policy, default));
        Assert.Equal("discovery_arm_response_size", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountSharedKeyAndExactIdentityAreRequired(bool wrongAccount)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(new
        {
            id = wrongAccount ? Target.ResourceId + "foreign" : Target.ResourceId,
            properties = new { allowSharedKeyAccess = wrongAccount, primaryEndpoints = new { blob = Target.Endpoint.AbsoluteUri } }
        }))));
        using var adapter = new PrivateDiscoveryAzure(Target, new Credential(), http);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => adapter.ValidateAccountAsync(default));
        Assert.Equal(wrongAccount ? "discovery_account_requirements" : "discovery_shared_key_disabled", error.Code);
    }

    [Fact]
    public async Task AccountPreviewIncludesNetworkAndSasPolicyWithoutChangingEither()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(new
            {
                id = Target.ResourceId,
                properties = new { allowSharedKeyAccess = true, primaryEndpoints = new { blob = Target.Endpoint.AbsoluteUri },
                    publicNetworkAccess = "Enabled", networkAcls = new { defaultAction = "Deny" }, sasPolicy = new { expirationAction = "Block", sasExpirationPeriod = "100.00:00:00" } }
            }));
        }));
        using var adapter = new PrivateDiscoveryAzure(Target, new Credential(), http);
        var info = await adapter.ValidateAccountAsync(default);
        Assert.True(info.SharedKeyAllowed);
        Assert.Equal("Deny", info.DefaultNetworkAction);
        Assert.Equal("Block", info.SasExpirationAction);
        Assert.Equal(TimeSpan.FromDays(100), info.SasMaximumInterval);
        Assert.Equal(Target.Endpoint, info.Endpoint);
        Assert.False(info.AtomicPolicyUpdatesSupported);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task AccountDefaultSharedKeyAuthorizationIsRecognized(string value)
    {
        var properties = new Dictionary<string, object?> { ["primaryEndpoints"] = new { blob = Target.Endpoint.AbsoluteUri } };
        if (value != "missing") properties["allowSharedKeyAccess"] = value == "null" ? null : true;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(new { id = Target.ResourceId, properties }))));
        using var adapter = new PrivateDiscoveryAzure(Target, new Credential(), http);
        Assert.True((await adapter.ValidateAccountAsync(default)).SharedKeyAllowed);
    }

    [Theory]
    [InlineData("Block", "89.00:00:00", false)]
    [InlineData("Block", "90.00:00:00", false)]
    [InlineData("Block", "90.00:05:00", true)]
    [InlineData("Log", "1.00:00:00", true)]
    [InlineData("Block", "malformed", false)]
    public async Task AccountSasPolicyIsParsedAndIncompatibleBlockIntervalRefused(string action, string period, bool accepted)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(new
        {
            id = Target.ResourceId,
            properties = new { primaryEndpoints = new { blob = Target.Endpoint.AbsoluteUri }, sasPolicy = new { expirationAction = action, sasExpirationPeriod = period } }
        }))));
        using var adapter = new PrivateDiscoveryAzure(Target, new Credential(), http);
        if (accepted)
        {
            var info = await adapter.ValidateAccountAsync(default);
            Assert.Equal(action, info.SasExpirationAction);
            Assert.NotNull(info.SasMaximumInterval);
        }
        else await Assert.ThrowsAsync<DiscoveryException>(() => adapter.ValidateAccountAsync(default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.Redirect, false)]
    public async Task AnonymousProbeMustDenyBothBlobReadAndContainerListing(HttpStatusCode status, bool accepted)
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.Equal("ownedaccount.blob.core.windows.net", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain("sig=", request.RequestUri.Query, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(status));
        }));
        using var adapter = new PrivateDiscoveryAzure(Target, new Credential(), http);
        if (accepted)
        {
            await adapter.VerifyAnonymousDeniedAsync("agentsignaler-test", default);
            Assert.Equal(2, requests);
        }
        else
        {
            await Assert.ThrowsAsync<DiscoveryException>(() => adapter.VerifyAnonymousDeniedAsync("agentsignaler-test", default));
            Assert.Equal(1, requests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AclMutationIsBlockedWithoutDocumentedAtomicCompareAndSet(bool sameSecondForeignEdit)
    {
        var containerLease = Guid.NewGuid().ToString("D");
        var blobLease = Guid.NewGuid().ToString("D");
        var writes = 0;
        var lastModified = DateTimeOffset.UtcNow.AddSeconds(-10);
        lastModified = lastModified.AddTicks(-(lastModified.Ticks % TimeSpan.TicksPerSecond));
        using var dataHttp = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("ETag", sameSecondForeignEdit ? "\"foreign-etag\"" : "\"expected-etag\"");
                response.Headers.Date = DateTimeOffset.UtcNow;
                response.Content = new ByteArrayContent([]);
                response.Content.Headers.LastModified = lastModified;
                return Task.FromResult(response);
            }
            if (request.RequestUri!.Query.Contains("comp=lease", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                var action = Assert.Single(request.Headers.GetValues("x-ms-lease-action"));
                if (action == "acquire")
                {
                    Assert.Equal("60", Assert.Single(request.Headers.GetValues("x-ms-lease-duration")));
                    var response = new HttpResponseMessage(HttpStatusCode.Created);
                    response.Headers.Add("x-ms-lease-id", request.RequestUri!.AbsolutePath.EndsWith("endpoint.json", StringComparison.Ordinal) ? blobLease : containerLease);
                    response.Headers.Add("ETag", "\"lease-etag\"");
                    return Task.FromResult(response);
                }
                Assert.Equal("release", action);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            writes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        using var arm = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected ARM call")));
        var options = new BlobClientOptions { Transport = new HttpClientTransport(dataHttp) };
        using var adapter = new PrivateDiscoveryAzure(Target, new Credential(), arm, options);
        await using var held = await adapter.AcquireAsync("agentsignaler-test", default);
        var now = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<DiscoveryException>(() =>
            held.WritePoliciesAsync([new(Policy, now, now.AddDays(90), "r")], "\"expected-etag\"", default));
        Assert.Equal("discovery_acl_atomic_concurrency_unsupported", error.Code);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task ActualSdkRejectsAclEtagConditionBeforeSendingAnyRequest()
    {
        var requests = 0;
        using var dataHttp = new HttpClient(new Handler(_ =>
        {
            requests++;
            throw new InvalidOperationException("The SDK must reject the unsupported condition locally.");
        }));
        var options = new BlobClientOptions { Transport = new HttpClientTransport(dataHttp) };
        var container = new BlobContainerClient(new Uri(Target.Endpoint, "agentsignaler-test"), new Credential(), options);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => container.SetAccessPolicyAsync(
            PublicAccessType.None, conditions: new BlobRequestConditions { IfMatch = new ETag("\"expected-etag\"") }));
        Assert.Equal(0, requests);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task BlobRedirectIsRejectedBeforeAnyCredentialCanReachAnotherHost()
    {
        var requests = 0;
        using var dataHttp = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.Equal(Target.Endpoint.Host, request.RequestUri!.Host);
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new("https://foreign.example/credential-capture");
            return Task.FromResult(response);
        }));
        using var arm = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected ARM call")));
        using var adapter = new PrivateDiscoveryAzure(Target, new Credential(), arm,
            new BlobClientOptions { Transport = new HttpClientTransport(dataHttp) });
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => adapter.AcquireAsync("agentsignaler-test", default));
        Assert.Equal("discovery_blob_redirect", error.Code);
        Assert.Equal(1, requests);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Credential : TokenCredential
    {
        public List<string> Scopes { get; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.AddRange(requestContext.Scopes);
            return new("fake-not-a-real-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
