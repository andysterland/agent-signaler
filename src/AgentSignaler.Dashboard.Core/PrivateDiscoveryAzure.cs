using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using AgentSignaler.Contracts;

namespace AgentSignaler.Dashboard;

internal sealed class PrivateDiscoveryAzure : IPrivateDiscoveryAzure, IDisposable
{
    internal const string BlobName = "endpoint.json";
    private readonly PrivateDiscoveryTarget target;
    private readonly TokenCredential credential;
    private readonly HttpClient arm;
    private readonly BlobServiceClient blobs;
    private readonly HttpClient? dataHttp;
    private readonly HttpPipelineTransport blobTransport;
    private const string ApiVersion = "2025-06-01";

    public PrivateDiscoveryAzure(PrivateDiscoveryTarget target) : this(target, CreateCredential(target),
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) }) { }

    internal PrivateDiscoveryAzure(PrivateDiscoveryTarget target, TokenCredential credential, HttpClient arm, BlobClientOptions? blobOptions = null)
    {
        target.Validate();
        this.target = target;
        this.credential = credential;
        this.arm = arm;
        var options = blobOptions ?? new BlobClientOptions();
        if (blobOptions is null)
        {
            dataHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            options.Transport = new HttpClientTransport(dataHttp);
        }
        blobTransport = options.Transport;
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        options.Diagnostics.IsTelemetryEnabled = false;
        options.AddPolicy(new PinnedBlobPolicy(target.Endpoint.Host), HttpPipelinePosition.PerRetry);
        blobs = new(target.Endpoint, credential, options);
    }

    private static AzureCliCredential CreateCredential(PrivateDiscoveryTarget target)
    {
        target.Validate();
        var options = new AzureCliCredentialOptions
        {
            TenantId = target.TenantId.ToString("D"), Subscription = target.SubscriptionId.ToString("D"),
            ProcessTimeout = TimeSpan.FromSeconds(15)
        };
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        options.Diagnostics.IsTelemetryEnabled = false;
        return new(options);
    }

    public async Task<PrivateDiscoveryAccountInfo> ValidateAccountAsync(CancellationToken token)
    {
        using var response = await ArmAsync(HttpMethod.Get, "", null, token).ConfigureAwait(false);
        var root = response.RootElement;
        if (!root.TryGetProperty("id", out var id) || !string.Equals(id.GetString(), Uri.UnescapeDataString(target.ResourceId), StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("properties", out var properties) ||
            !properties.TryGetProperty("primaryEndpoints", out var endpoints) ||
            !endpoints.TryGetProperty("blob", out var blob) || blob.GetString() != target.Endpoint.AbsoluteUri)
            throw new DiscoveryException("discovery_account_requirements");
        if (properties.TryGetProperty("allowSharedKeyAccess", out var keys) &&
            keys.ValueKind is not (JsonValueKind.Null or JsonValueKind.True))
            throw new DiscoveryException("discovery_shared_key_disabled");
        var network = properties.TryGetProperty("publicNetworkAccess", out var publicNetwork) ? publicNetwork.GetString() : "Enabled";
        var defaultAction = properties.TryGetProperty("networkAcls", out var acls) && acls.TryGetProperty("defaultAction", out var action)
            ? action.GetString() : "Allow";
        var sasAction = "None";
        TimeSpan? sasMaximum = null;
        if (properties.TryGetProperty("sasPolicy", out var policy) && policy.ValueKind != JsonValueKind.Null)
        {
            if (policy.ValueKind != JsonValueKind.Object || !policy.TryGetProperty("sasExpirationPeriod", out var period) ||
                period.ValueKind != JsonValueKind.String || period.GetString() is not { Length: > 0 and <= 32 } interval ||
                !TimeSpan.TryParseExact(interval, "c", System.Globalization.CultureInfo.InvariantCulture, out var maximum) || maximum <= TimeSpan.Zero)
                throw new DiscoveryException("discovery_sas_expiration_policy");
            sasAction = policy.TryGetProperty("expirationAction", out var expirationAction) ? expirationAction.GetString() : "Log";
            sasMaximum = maximum;
            if (sasAction == "Block" && maximum < TimeSpan.FromDays(90).Add(TimeSpan.FromMinutes(5)))
                throw new DiscoveryException("discovery_sas_expiration_policy_incompatible");
        }
        if (network is not ("Enabled" or "Disabled" or "SecuredByPerimeter") || defaultAction is not ("Allow" or "Deny") ||
            sasAction is not ("None" or "Log" or "Block")) throw new DiscoveryException("discovery_account_requirements");
        return new(target.Endpoint, network, defaultAction, true, sasAction, sasMaximum, AtomicPolicyUpdatesSupported: false);
    }

    public async Task VerifyAnonymousDeniedAsync(string container, CancellationToken token)
    {
        foreach (var uri in new[] { $"{target.Endpoint.AbsoluteUri}{container}/{BlobName}",
            $"{target.Endpoint.AbsoluteUri}{container}?restype=container&comp=list&maxresults=1" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await arm.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is not (System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound))
                throw new DiscoveryException("discovery_anonymous_access_not_denied");
        }
    }

    public async Task<PrivateDiscoveryRemote?> InspectAsync(string container, CancellationToken token)
    {
        try { return await ReadAsync(blobs.GetBlobContainerClient(container), token).ConfigureAwait(false); }
        catch (RequestFailedException error) when (error.Status == 404 && error.ErrorCode == "ContainerNotFound") { return null; }
    }

    public async Task CreateAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] initialDocument, CancellationToken token)
    {
        var client = blobs.GetBlobContainerClient(container);
        await client.CreateAsync(PublicAccessType.None, metadata.ToDictionary(), cancellationToken: token).ConfigureAwait(false);
        await client.GetBlobClient(BlobName).UploadAsync(BinaryData.FromBytes(initialDocument), new BlobUploadOptions
        {
            Conditions = new() { IfNoneMatch = ETag.All }, Metadata = metadata.ToDictionary(),
            HttpHeaders = new() { ContentType = "application/json", CacheControl = "no-store" }
        }, token).ConfigureAwait(false);
    }

    public async Task InitializeBlobAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] initialDocument,
        string containerETag, CancellationToken token)
    {
        var client = blobs.GetBlobContainerClient(container);
        var lease = client.GetBlobLeaseClient();
        await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: token).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(client, token).ConfigureAwait(false);
            if (current.ContainerETag != containerETag || !current.IsPrivate || current.Blobs.Count != 0 || current.Policies.Count != 0 ||
                current.Metadata.Count != metadata.Count || metadata.Any(p => !current.Metadata.TryGetValue(p.Key, out var value) || value != p.Value))
                throw new DiscoveryException("discovery_recovery_ownership");
            await client.GetBlobClient(BlobName).UploadAsync(BinaryData.FromBytes(initialDocument), new BlobUploadOptions
            {
                Conditions = new() { IfNoneMatch = ETag.All }, Metadata = metadata.ToDictionary(),
                HttpHeaders = new() { ContentType = "application/json", CacheControl = "no-store" }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await lease.ReleaseAsync(cancellationToken: cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is RequestFailedException or OperationCanceledException) { }
        }
    }

    public async Task<IPrivateDiscoveryLease> AcquireAsync(string container, CancellationToken token)
    {
        var client = blobs.GetBlobContainerClient(container);
        var containerLease = client.GetBlobLeaseClient();
        await containerLease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: token).ConfigureAwait(false);
        BlobLeaseClient? blobLease = null;
        try
        {
            var blob = client.GetBlobClient(BlobName);
            blobLease = blob.GetBlobLeaseClient();
            await blobLease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: token).ConfigureAwait(false);
            return new Lease(client, containerLease, blobLease);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await containerLease.ReleaseAsync(cancellationToken: cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is RequestFailedException or OperationCanceledException) { }
            throw;
        }
    }

    public async Task<Uri> CreateReadSasAsync(string container, string policyId, CancellationToken token)
    {
        using var response = await ArmAsync(HttpMethod.Post, "/listServiceSas", new ServiceSasRequest(
            $"/blob/{target.Account}/{container}/{BlobName}", "b", "https", policyId), token).ConfigureAwait(false);
        var root = response.RootElement;
        if (root.EnumerateObject().Count() != 1 || !root.TryGetProperty("serviceSasToken", out var sas) ||
            sas.ValueKind != JsonValueKind.String || sas.GetString() is not { Length: > 0 and <= 8192 } value)
            throw new DiscoveryException("discovery_arm_sas_response");
        var uri = new Uri($"{target.Endpoint.AbsoluteUri}{container}/{BlobName}?{value}");
        DiscoveryProtocol.ValidateBlobSasUri(uri);
        var actualPolicy = uri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("si=", StringComparison.Ordinal))[3..];
        if (Uri.UnescapeDataString(actualPolicy) != policyId) throw new DiscoveryException("discovery_sas_policy");
        return uri;
    }

    public async Task VerifyReadSasAsync(Uri sas, byte[] expectedDocument, CancellationToken token)
    {
        DiscoveryProtocol.ValidateBlobSasUri(sas);
        var options = new BlobClientOptions();
        options.Transport = blobTransport;
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        options.Diagnostics.IsTelemetryEnabled = false;
        options.AddPolicy(new PinnedBlobPolicy(target.Endpoint.Host), HttpPipelinePosition.PerRetry);
        var client = new BlobClient(sas, options);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = (await client.DownloadStreamingAsync(new BlobDownloadOptions
                    { Range = new HttpRange(0, DiscoveryProtocol.MaximumEnvelopeBytes + 1) }, token).ConfigureAwait(false)).Value.Content;
                var bytes = new byte[DiscoveryProtocol.MaximumEnvelopeBytes + 1];
                var length = 0;
                int read;
                while (length < bytes.Length && (read = await content.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false)) != 0) length += read;
                if (!bytes.AsSpan(0, length).SequenceEqual(expectedDocument)) throw new DiscoveryException("discovery_sas_read_verification");
                return;
            }
            catch (RequestFailedException error) when (error.Status == 403 && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
        }
    }

    private sealed record ServiceSasRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("canonicalizedResource")] string Resource,
        [property: System.Text.Json.Serialization.JsonPropertyName("signedResource")] string Scope,
        [property: System.Text.Json.Serialization.JsonPropertyName("signedProtocol")] string Protocol,
        [property: System.Text.Json.Serialization.JsonPropertyName("signedIdentifier")] string Policy);

    private async Task<JsonDocument> ArmAsync(HttpMethod method, string suffix, object? body, CancellationToken token)
    {
        try { return await ArmCoreAsync(method, suffix, body, token).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or RequestFailedException or AuthenticationFailedException)
        {
            throw new PrivateDiscoveryCloudException(null);
        }
        catch (JsonException) { throw new DiscoveryException("discovery_arm_response_json"); }
        catch (OperationCanceledException) { throw new OperationCanceledException("discovery_cloud_canceled", token); }
    }

    private async Task<JsonDocument> ArmCoreAsync(HttpMethod method, string suffix, object? body, CancellationToken token)
    {
        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, $"https://management.azure.com{target.ResourceId}{suffix}?api-version={ApiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await arm.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter;
            throw new PrivateDiscoveryCloudException(retry?.Date ?? (retry?.Delta is { } delay ? DateTimeOffset.UtcNow.Add(delay) : null));
        }
        if (response.Content.Headers.ContentLength is > 65536) throw new DiscoveryException("discovery_arm_response_size");
        using var bytes = new MemoryStream();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > 65536) throw new DiscoveryException("discovery_arm_response_size");
            bytes.Write(buffer, 0, count);
        }
        var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new DiscoveryException("discovery_arm_response_shape");
            ValidateProperties(document.RootElement);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static void ValidateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new DiscoveryException("discovery_arm_duplicate_property");
                ValidateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateProperties(item);
    }

    private static async Task<PrivateDiscoveryRemote> ReadAsync(BlobContainerClient client, CancellationToken token)
    {
        var properties = await client.GetPropertiesAsync(cancellationToken: token).ConfigureAwait(false);
        var acl = await client.GetAccessPolicyAsync(cancellationToken: token).ConfigureAwait(false);
        var names = new List<string>();
        await foreach (var blob in client.GetBlobsAsync(cancellationToken: token).ConfigureAwait(false))
        {
            if (names.Count >= 2) throw new DiscoveryException("discovery_foreign_blob");
            names.Add(blob.Name);
        }
        string? etag = null;
        IReadOnlyDictionary<string, string>? metadata = null;
        byte[]? document = null;
        if (names.Contains(BlobName, StringComparer.Ordinal))
        {
            var blob = client.GetBlobClient(BlobName);
            var details = (await blob.GetPropertiesAsync(cancellationToken: token).ConfigureAwait(false)).Value;
            if (details.ContentLength is <= 0 or > DiscoveryProtocol.MaximumEnvelopeBytes) throw new DiscoveryException("discovery_blob_size");
            etag = details.ETag.ToString();
            metadata = details.Metadata.ToDictionary();
            using var stream = (await blob.DownloadStreamingAsync(new BlobDownloadOptions
                { Conditions = new() { IfMatch = details.ETag }, Range = new HttpRange(0, DiscoveryProtocol.MaximumEnvelopeBytes + 1) }, token).ConfigureAwait(false)).Value.Content;
            using var bytes = new MemoryStream();
            var buffer = new byte[1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (bytes.Length + read > DiscoveryProtocol.MaximumEnvelopeBytes) throw new DiscoveryException("discovery_blob_size");
                bytes.Write(buffer, 0, read);
            }
            document = bytes.ToArray();
        }
        return new(properties.Value.ETag.ToString(), etag, properties.Value.Metadata.ToDictionary(),
            acl.Value.BlobPublicAccess == PublicAccessType.None,
            acl.Value.SignedIdentifiers.Select(p => new PrivateDiscoveryPolicy(p.Id, p.AccessPolicy.StartsOn,
                p.AccessPolicy.ExpiresOn, p.AccessPolicy.Permissions)).ToArray(), names, metadata, document);
    }

    private sealed class Lease(BlobContainerClient client, BlobLeaseClient containerLease, BlobLeaseClient blobLease) : IPrivateDiscoveryLease
    {
        public Task<PrivateDiscoveryRemote> ReadAsync(CancellationToken token) => PrivateDiscoveryAzure.ReadAsync(client, token);
        // Set Container ACL has no ETag condition; container leases exclude deletion only.
        // HTTP-date conditions cannot prove exact preservation of concurrent policy edits.
        public Task WritePoliciesAsync(IReadOnlyList<PrivateDiscoveryPolicy> policies, string etag, CancellationToken token) =>
            Task.FromException(new DiscoveryException("discovery_acl_atomic_concurrency_unsupported"));
        public async Task WriteBlobAsync(byte[] bytes, string etag, CancellationToken token) =>
            await client.GetBlobClient(BlobName).UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
            {
                Conditions = new() { LeaseId = blobLease.LeaseId, IfMatch = new ETag(etag) },
                Metadata = (await client.GetBlobClient(BlobName).GetPropertiesAsync(new() { LeaseId = blobLease.LeaseId, IfMatch = new ETag(etag) }, token).ConfigureAwait(false)).Value.Metadata,
                HttpHeaders = new() { ContentType = "application/json", CacheControl = "no-store" }
            }, token).ConfigureAwait(false);
        public async Task DeleteBlobAsync(string etag, CancellationToken token) =>
            await client.GetBlobClient(BlobName).DeleteAsync(DeleteSnapshotsOption.None,
                new() { LeaseId = blobLease.LeaseId, IfMatch = new ETag(etag) }, token).ConfigureAwait(false);
        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var lease in new[] { blobLease, containerLease })
                try { await lease.ReleaseAsync(cancellationToken: deadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is RequestFailedException or OperationCanceledException) { }
        }
    }
    private sealed class PinnedBlobPolicy(string host) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            var uri = message.Request.Uri.ToUri();
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.Host != host)
                throw new DiscoveryException("discovery_blob_destination");
        }
        public override void OnReceivedResponse(HttpMessage message)
        {
            if (message.Response.Status is >= 300 and < 400) throw new DiscoveryException("discovery_blob_redirect");
        }
    }
    public void Dispose() { arm.Dispose(); dataHttp?.Dispose(); }
}