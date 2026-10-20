using System.Security.Cryptography;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;

namespace AgentSignaler.Dashboard;

internal sealed class PrivateDiscoveryController(IPrivateDiscoveryJournal journal,
    Func<PrivateDiscoveryTarget, IPrivateDiscoveryAzure> azureFactory, TimeProvider clock,
    Func<Uri, CancellationToken, Task>? verifyEndpoint = null)
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private PrivateDiscoveryRegistration? registration;
    private PrivateDiscoveryPreview? preview;
    private long generation;
    private Uri? verifiedEndpoint;
    private CancellationTokenSource generationCancellation = new();
    public PrivateDiscoveryStatus Status { get; private set; } = new(false, false, false, "Private discovery is not configured.");
    public event Action? Changed;
    internal PrivateDiscoveryRegistration? Registration => registration;

    public async Task InitializeAsync(CancellationToken token)
    {
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            registration = await journal.LoadAsync(token).ConfigureAwait(false);
            if (registration is null) return;
            ValidateRegistration(registration);
            SetStatus("Private discovery loaded. Verify Azure access and renew before copying.");
        }
        finally { serial.Release(); }
    }

    public async Task<PrivateDiscoveryPreview> PreviewAsync(PrivateDiscoveryTarget target, CancellationToken token)
    {
        target.Validate();
        await serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (registration is not null && registration.Phase != PrivateDiscoveryPhase.Removed)
                throw new DiscoveryException("discovery_already_configured");
            using var adapter = new Adapter(azureFactory(target));
            var account = await adapter.Value.ValidateAccountAsync(token).ConfigureAwait(false);
            RequireAtomicPolicyUpdates(account);
            var id = Guid.NewGuid();
            var container = "agentsignaler-" + id.ToString("N");
            if (await adapter.Value.InspectAsync(container, token).ConfigureAwait(false) is not null)
                throw new DiscoveryException("discovery_name_collision");
            preview = new(target, id, Guid.NewGuid().ToString("N"), container, NewPolicyId(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(10), account);
            Status = Status with { Preview = preview, Message = "Preview ready. Confirm creation of the exact private container and read-only policy." };
            Changed?.Invoke();
            return preview;
        }
        finally { serial.Release(); }
    }

    public async Task ProvisionAsync(Guid confirmationId, CancellationToken token)
    {
        await ExecuteAsync(async ct =>
        {
            var selected = preview;
            if (selected is null || selected.ConfirmationId != confirmationId || selected.ExpiresAtUtc <= clock.GetUtcNow())
                throw new DiscoveryException("discovery_preview_expired");
            if (registration is not null && registration.Phase != PrivateDiscoveryPhase.Removed)
                throw new DiscoveryException("discovery_already_configured");
            using var adapter = new Adapter(azureFactory(selected.Target));
            RequireAtomicPolicyUpdates(await adapter.Value.ValidateAccountAsync(ct).ConfigureAwait(false));
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var retired = registration?.RetiredPolicyIds ?? [];
            if (retired.Count >= 4095 || retired.Contains(selected.PolicyId, StringComparer.Ordinal))
                throw new DiscoveryException("discovery_retirement_capacity");
            registration = new(selected.Target, selected.DiscoveryId, selected.Owner, selected.Container, selected.PolicyId,
                Convert.ToBase64String(key.ExportPkcs8PrivateKey()), DiscoveryCodec.ExportPublicKey(key), PrivateDiscoveryPhase.Prepared, 0,
                RetiredPolicyIds: retired);
            await SaveAsync(ct).ConfigureAwait(false);
            preview = null;
            var bytes = await NextDocumentAsync(null, ct).ConfigureAwait(false);
            await adapter.Value.CreateAsync(registration.Container, Metadata(registration), bytes, ct).ConfigureAwait(false);
            registration = registration with { Phase = PrivateDiscoveryPhase.ContainerCreated };
            await SaveAsync(ct).ConfigureAwait(false);
            await CompletePolicyAsync(adapter.Value, false, ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    private void RequireAtomicPolicyUpdates(PrivateDiscoveryAccountInfo account)
    {
        if (account.AtomicPolicyUpdatesSupported) return;
        SetStatus("Azure setup and policy changes are blocked: Set Container ACL does not provide documented exact atomic concurrency protection. No cloud resources were created or policy changes submitted. Retained resources require authorized manual cleanup.");
        throw new DiscoveryException("discovery_acl_atomic_concurrency_unsupported");
    }

    public Task RecoverAsync(CancellationToken token) => ExecuteAsync(async ct =>
    {
        RequireRegistration();
        using var adapter = new Adapter(azureFactory(registration!.Target));
        RequireAtomicPolicyUpdates(await adapter.Value.ValidateAccountAsync(ct).ConfigureAwait(false));
        var remote = await adapter.Value.InspectAsync(registration.Container, ct).ConfigureAwait(false);
        if (remote is null && registration.Phase == PrivateDiscoveryPhase.Prepared)
        {
            var bytes = await NextDocumentAsync(null, ct).ConfigureAwait(false);
            await adapter.Value.CreateAsync(registration.Container, Metadata(registration), bytes, ct).ConfigureAwait(false);
            registration = registration with { Phase = PrivateDiscoveryPhase.ContainerCreated };
            await SaveAsync(ct).ConfigureAwait(false);
        }
        else if (remote is null) throw new DiscoveryException("discovery_owned_resource_missing");
        else if (registration.Phase == PrivateDiscoveryPhase.Prepared && remote.Blobs.Count == 0)
        {
            VerifyContainer(remote, registration);
            if (remote.Policies.Count != 0) throw new DiscoveryException("discovery_foreign_policy");
            var bytes = await NextDocumentAsync(null, ct).ConfigureAwait(false);
            await adapter.Value.InitializeBlobAsync(registration.Container, Metadata(registration), bytes, remote.ContainerETag, ct).ConfigureAwait(false);
            registration = registration with { Phase = PrivateDiscoveryPhase.ContainerCreated };
            await SaveAsync(ct).ConfigureAwait(false);
        }
        else if (registration.Phase == PrivateDiscoveryPhase.RemovalPending && remote.Blobs.Count == 0)
        {
            VerifyContainer(remote, registration);
            if (remote.Policies.Count != 0) throw new DiscoveryException("discovery_foreign_policy");
            registration = registration with { Phase = PrivateDiscoveryPhase.Removed, Package = null, PolicyExpiry = null };
            await SaveAsync(ct).ConfigureAwait(false);
            SetStatus($"Discovery removal recovered. Empty owned container {registration.Container} in account {registration.Target.Account} remains. Inspect and manually remove it in Azure if desired; no account or resource group is deleted.");
            return;
        }
        else VerifyOwnership(remote, registration);
        if (registration.Phase is PrivateDiscoveryPhase.RevocationPending or PrivateDiscoveryPhase.RemovalPending)
            await RevokeCoreAsync(adapter.Value, ct).ConfigureAwait(false);
        else if (registration.Phase == PrivateDiscoveryPhase.RetirementPending)
            await RetireCoreAsync(adapter.Value, ct).ConfigureAwait(false);
        else if (registration.Phase != PrivateDiscoveryPhase.Revoked)
            await CompletePolicyAsync(adapter.Value, false, ct).ConfigureAwait(false);
    }, token);

    public Task RenewAsync(CancellationToken token) => ExecuteAsync(async ct =>
    {
        RequireReady();
        using var adapter = new Adapter(azureFactory(registration!.Target));
        await CompletePolicyAsync(adapter.Value, false, ct).ConfigureAwait(false);
    }, token);

    public Task RotateAsync(CancellationToken token) => ExecuteAsync(async ct =>
    {
        RequireReady();
        if (registration!.PreviousPolicyId is not null) throw new DiscoveryException("discovery_retire_previous_first");
        if (registration.RetiredPolicyIds!.Count >= 4094) throw new DiscoveryException("discovery_retirement_capacity");
        registration = registration with { Phase = PrivateDiscoveryPhase.PolicyPending,
            PendingPolicyId = NewPolicyId(), Package = null };
        await SaveAsync(ct).ConfigureAwait(false);
        using var adapter = new Adapter(azureFactory(registration.Target));
        await CompletePolicyAsync(adapter.Value, true, ct).ConfigureAwait(false);
    }, token);

    public Task RevokeAsync(bool remove, CancellationToken token) => ExecuteAsync(async ct =>
    {
        RequireRegistration();
        var retired = registration!.RetiredPolicyIds!.Append(registration.PolicyId)
            .Concat(registration.PendingPolicyId is { } pending ? [pending] : Array.Empty<string>())
            .Concat(registration.PreviousPolicyId is { } previous ? [previous] : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if (retired.Length > 4096) throw new DiscoveryException("discovery_retirement_capacity");
        registration = registration with { Phase = remove ? PrivateDiscoveryPhase.RemovalPending : PrivateDiscoveryPhase.RevocationPending,
            RetiredPolicyIds = retired, Package = null };
        await SaveAsync(ct).ConfigureAwait(false);
        using var adapter = new Adapter(azureFactory(registration.Target));
        await RevokeCoreAsync(adapter.Value, ct).ConfigureAwait(false);
    }, token);

    public Task RetirePreviousAsync(CancellationToken token) => ExecuteAsync(async ct =>
    {
        RequireReady();
        var current = registration!;
        if (current.PreviousPolicyId is null) throw new DiscoveryException("discovery_no_previous_policy");
        registration = current with { Phase = PrivateDiscoveryPhase.RetirementPending,
            RetiredPolicyIds = current.RetiredPolicyIds!.Append(current.PreviousPolicyId).Distinct(StringComparer.Ordinal).ToArray() };
        await SaveAsync(ct).ConfigureAwait(false);
        using var adapter = new Adapter(azureFactory(current.Target));
        await RetireCoreAsync(adapter.Value, ct).ConfigureAwait(false);
    }, token);

    private async Task RetireCoreAsync(IPrivateDiscoveryAzure azure, CancellationToken token)
    {
        var current = registration!;
        await using var held = await azure.AcquireAsync(current.Container, token).ConfigureAwait(false);
        var remote = await held.ReadAsync(token).ConfigureAwait(false);
        VerifyOwnership(remote, current);
        VerifyPolicies(remote, current);
        await held.WritePoliciesAsync(remote.Policies.Where(p => p.Id != current.PreviousPolicyId).ToArray(), remote.ContainerETag, token).ConfigureAwait(false);
        registration = current with { Phase = PrivateDiscoveryPhase.Ready, PreviousPolicyId = null, PreviousPolicyExpiry = null };
        await SaveAsync(token).ConfigureAwait(false);
        SetStatus("Previous reader group retired permanently. Existing rotated readers remain authorized; revocation may take 30 seconds.");
    }

    public Task SetPublishingAsync(bool enabled, CancellationToken token) => ExecuteAsync(async ct =>
    {
        RequireReady();
        registration = registration! with { PublishingEnabled = enabled };
        await SaveAsync(ct).ConfigureAwait(false);
        SetStatus(enabled ? "Discovery publishing enabled." : "Discovery publishing disabled. This does not revoke reader access or delete Azure resources; the last document expires within 24 hours.");
    }, token);

    public Task TestReaderAsync(CancellationToken token) => ExecuteAsync(async ct =>
    {
        RequireReady();
        var package = DiscoveryCodec.ParsePackage(CopyPackage());
        using var adapter = new Adapter(azureFactory(registration!.Target));
        await using var held = await adapter.Value.AcquireAsync(registration.Container, ct).ConfigureAwait(false);
        var remote = await held.ReadAsync(ct).ConfigureAwait(false);
        VerifyOwnership(remote, registration);
        VerifyPolicies(remote, registration);
        await adapter.Value.VerifyReadSasAsync(package.BlobSasUri, remote.Document!, ct).ConfigureAwait(false);
        await adapter.Value.VerifyAnonymousDeniedAsync(registration.Container, ct).ConfigureAwait(false);
        SetStatus("Reader access verified. A successful Blob read does not prove the tunnel receiver is currently available.");
    }, token);

    private async Task RevokeCoreAsync(IPrivateDiscoveryAzure azure, CancellationToken token)
    {
        var current = registration!;
        await using var held = await azure.AcquireAsync(current.Container, token).ConfigureAwait(false);
        var remote = await held.ReadAsync(token).ConfigureAwait(false);
        VerifyOwnership(remote, current);
        VerifyPolicies(remote, current);
        await held.WritePoliciesAsync([], remote.ContainerETag, token).ConfigureAwait(false);
        if (current.Phase == PrivateDiscoveryPhase.RemovalPending)
        {
            await held.DeleteBlobAsync(remote.BlobETag!, token).ConfigureAwait(false);
            // Keep the empty owned container as a permanent tombstone; never delete an Azure resource recursively.
            registration = current with { Phase = PrivateDiscoveryPhase.Removed, Package = null, PolicyExpiry = null,
                PendingPolicyId = null, PreviousPolicyId = null, PreviousPolicyExpiry = null, PendingDocument = null, PendingBlobETag = null };
        }
        else
        {
            var bytes = await NextDocumentAsync(null, token, remote.BlobETag).ConfigureAwait(false);
            await held.WriteBlobAsync(bytes, remote.BlobETag!, token).ConfigureAwait(false);
            registration = registration! with { Phase = PrivateDiscoveryPhase.Revoked, Package = null, PolicyExpiry = null,
                PendingPolicyId = null, PreviousPolicyId = null, PreviousPolicyExpiry = null, PendingDocument = null, PendingBlobETag = null };
        }
        await SaveAsync(token).ConfigureAwait(false);
        SetStatus(current.Phase == PrivateDiscoveryPhase.RemovalPending
            ? $"Discovery reader access and owned blob removed. Empty owned container {current.Container} in account {current.Target.Account} remains. Inspect and manually remove it in Azure if desired; no unrelated resources were deleted."
            : "Discovery revoked. Policy revocation may take 30 seconds to propagate.");
    }

    private async Task CompletePolicyAsync(IPrivateDiscoveryAzure azure, bool rotate, CancellationToken token)
    {
        var current = registration!;
        await using var held = await azure.AcquireAsync(current.Container, token).ConfigureAwait(false);
        var remote = await held.ReadAsync(token).ConfigureAwait(false);
        VerifyOwnership(remote, current);
        VerifyPolicies(remote, current);
        var policyId = current.PendingPolicyId ?? current.PolicyId;
        if (current.RetiredPolicyIds!.Contains(policyId, StringComparer.Ordinal)) throw new DiscoveryException("discovery_retired_policy");
        var now = clock.GetUtcNow();
        var existing = remote.Policies.SingleOrDefault(p => p.Id == policyId);
        var expiry = existing?.ExpiresAtUtc;
        if (rotate || expiry is null || expiry < now.AddDays(30))
        {
            var policyNow = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
            expiry = policyNow.AddDays(90);
            registration = current with { Phase = PrivateDiscoveryPhase.PolicyPending, PolicyExpiry = expiry };
            await SaveAsync(token).ConfigureAwait(false);
            var policies = remote.Policies.Where(p => p.Id != policyId).Append(new(policyId, policyNow.AddMinutes(-5), expiry.Value, "r")).ToArray();
            await held.WritePoliciesAsync(policies, remote.ContainerETag, token).ConfigureAwait(false);
            var effective = await held.ReadAsync(token).ConfigureAwait(false);
            VerifyOwnership(effective, registration);
            if (effective.Policies.Count != policies.Length || policies.Any(p => !effective.Policies.Contains(p)))
                throw new DiscoveryException("discovery_effective_policy_mismatch");
            remote = effective;
            expiry = effective.Policies.Single(p => p.Id == policyId).ExpiresAtUtc;
        }
        var sas = await azure.CreateReadSasAsync(current.Container, policyId, token).ConfigureAwait(false);
        await azure.VerifyReadSasAsync(sas, remote.Document!, token).ConfigureAwait(false);
        await azure.VerifyAnonymousDeniedAsync(current.Container, token).ConfigureAwait(false);
        var package = DiscoveryCodec.EncodePackage(new(current.DiscoveryId, sas, current.PublicKey));
        registration = registration! with { PolicyId = policyId, PendingPolicyId = null, Phase = PrivateDiscoveryPhase.Ready,
            PolicyExpiry = expiry, Package = package,
            PreviousPolicyId = current.PendingPolicyId is not null ? current.PolicyId : current.PreviousPolicyId,
            PreviousPolicyExpiry = current.PendingPolicyId is not null ? remote.Policies.SingleOrDefault(p => p.Id == current.PolicyId)?.ExpiresAtUtc : current.PreviousPolicyExpiry };
        await SaveAsync(token).ConfigureAwait(false);
        SetStatus("Private discovery ready. Copy the connection package only to trusted Clients; it grants read access.");
    }

    public long ObserveTunnel(Uri? endpoint)
    {
        if (endpoint is not null) DiscoveryProtocol.ValidateEndpoint(endpoint);
        lock (this)
        {
            if (verifiedEndpoint == endpoint) return generation;
            generationCancellation.Cancel();
            generationCancellation.Dispose();
            generationCancellation = new();
            verifiedEndpoint = endpoint;
            return ++generation;
        }
    }

    public Task PublishAsync(long expectedGeneration, CancellationToken token) => ExecuteAsync(async ct =>
    {
        if (registration?.Phase != PrivateDiscoveryPhase.Ready || !registration.PublishingEnabled) return;
        Uri? endpoint;
        CancellationToken generationToken;
        lock (this)
        {
            if (expectedGeneration != generation) return;
            endpoint = verifiedEndpoint;
            generationToken = generationCancellation.Token;
        }
        using var currentGeneration = CancellationTokenSource.CreateLinkedTokenSource(ct, generationToken);
        ct = currentGeneration.Token;
        if (endpoint is not null && verifyEndpoint is not null)
            await verifyEndpoint(endpoint, ct).ConfigureAwait(false);
        using var adapter = new Adapter(azureFactory(registration.Target));
        await using var held = await adapter.Value.AcquireAsync(registration.Container, ct).ConfigureAwait(false);
        var remote = await held.ReadAsync(ct).ConfigureAwait(false);
        VerifyOwnership(remote, registration);
        VerifyPolicies(remote, registration);
        var bytes = await NextDocumentAsync(endpoint, ct, remote.BlobETag).ConfigureAwait(false);
        lock (this) if (expectedGeneration != generation) throw new DiscoveryException("discovery_generation_changed");
        if (!remote.Document!.AsSpan().SequenceEqual(bytes))
        {
            if (registration!.PendingBlobETag is null || registration.PendingBlobETag != remote.BlobETag)
                throw new DiscoveryException("discovery_publication_conflict");
            if (endpoint is not null && verifyEndpoint is not null)
                await verifyEndpoint(endpoint, ct).ConfigureAwait(false);
            lock (this) if (expectedGeneration != generation) throw new DiscoveryException("discovery_generation_changed");
            await held.WriteBlobAsync(bytes, registration.PendingBlobETag, ct).ConfigureAwait(false);
        }
        lock (this) if (expectedGeneration != generation) throw new DiscoveryException("discovery_generation_changed");
        registration = registration with { PendingDocument = null, PendingBlobETag = null, LastPublishedAtUtc = ReadSignedPayload(bytes).PublishedAtUtc };
        await SaveAsync(ct).ConfigureAwait(false);
        Status = Status with { PublishedAtUtc = ReadSignedPayload(bytes).PublishedAtUtc, Revision = registration.Revision,
            PublicationPending = false, RetryNotBeforeUtc = null,
            Message = endpoint is null ? "Signed unavailable state published; package provisioned but not currently connectable." : "Verified tunnel published to private discovery." };
        Changed?.Invoke();
    }, token);

    public string CopyPackage()
    {
        RequireReady();
        if (registration!.PolicyExpiry <= clock.GetUtcNow() || registration.Package is null)
            throw new DiscoveryException("discovery_package_unavailable");
        return registration.Package;
    }

    private async Task<byte[]> NextDocumentAsync(Uri? endpoint, CancellationToken token, string? expectedBlobETag = null)
    {
        var current = registration!;
        var now = clock.GetUtcNow();
        if (current.PendingDocument is { } pending)
        {
            var intent = ReadSignedPayload(pending);
            if (intent.ExpiresAtUtc > now && intent.DashboardBaseUrl == endpoint) return pending;
        }
        var revision = checked(current.Revision + 1);
        var keyBytes = Convert.FromBase64String(current.SigningKey);
        try
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(keyBytes, out var read);
            if (read != keyBytes.Length || DiscoveryCodec.ExportPublicKey(key) != current.PublicKey)
                throw new DiscoveryException("discovery_signing_key");
            var bytes = DiscoveryCodec.Sign(new(DiscoveryProtocol.Version, current.DiscoveryId, revision, now, now.AddHours(24),
                endpoint is null ? DiscoveryState.Unavailable : DiscoveryState.Available, endpoint), key);
            registration = current with { Revision = revision, PendingDocument = bytes, PendingBlobETag = expectedBlobETag };
            await SaveAsync(token).ConfigureAwait(false);
            return bytes;
        }
        finally { CryptographicOperations.ZeroMemory(keyBytes); }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        await serial.WaitAsync(token).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try { await action(linked.Token).ConfigureAwait(false); }
        catch (Exception error)
        {
            SetStatus(error is DiscoveryException { Code: "discovery_acl_atomic_concurrency_unsupported" }
                ? "Azure policy changes are blocked: Set Container ACL does not provide documented exact atomic concurrency protection. No policy update was submitted. Existing policies retain their expiry; retained resources require authorized manual cleanup."
                : "Sharing and discovery have separate status: discovery update did not complete. Review ownership and use Recover; changes may already be committed.");
            var raw = (error as Azure.RequestFailedException)?.GetRawResponse();
            DateTimeOffset? retryAt = (error as PrivateDiscoveryCloudException)?.RetryNotBeforeUtc;
            if (raw is not null && raw.Headers.TryGetValue("Retry-After", out var retry))
            {
                if (int.TryParse(retry, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 86400)
                    retryAt = clock.GetUtcNow().AddSeconds(seconds);
                else if (DateTimeOffset.TryParse(retry, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp)) retryAt = timestamp;
            }
            if (raw is not null && raw.Headers.TryGetValue("x-ms-retry-after-ms", out var milliseconds) &&
                int.TryParse(milliseconds, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms is >= 0 and <= 86400000)
                retryAt = clock.GetUtcNow().AddMilliseconds(ms);
            Status = Status with { RetryNotBeforeUtc = retryAt };
            Changed?.Invoke();
            throw;
        }
        finally { serial.Release(); }
    }

    private static IReadOnlyDictionary<string, string> Metadata(PrivateDiscoveryRegistration current) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["agentsignaler"] = "private-discovery-v1", ["discoveryid"] = current.DiscoveryId.ToString("N"),
            ["owner"] = current.Owner, ["publickeyhash"] = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(current.PublicKey))) };

    private static void VerifyOwnership(PrivateDiscoveryRemote remote, PrivateDiscoveryRegistration current)
    {
        VerifyContainer(remote, current);
        var expected = Metadata(current);
        if (remote.Blobs.Count != 1 || remote.Blobs[0] != PrivateDiscoveryAzure.BlobName || remote.BlobETag is null ||
            remote.BlobMetadata is null || remote.BlobMetadata.Count != expected.Count ||
            expected.Any(p => !remote.BlobMetadata.TryGetValue(p.Key, out var value) || value != p.Value) || remote.Document is null)
            throw new DiscoveryException("discovery_ownership_mismatch");
        using var envelope = DiscoveryProtocol.ParseObject(remote.Document, DiscoveryProtocol.MaximumEnvelopeBytes, "payload", "signature");
        byte[] payload;
        byte[] signature;
        try
        {
            payload = Convert.FromBase64String(DiscoveryProtocol.Text(envelope.RootElement, "payload"));
            signature = Convert.FromBase64String(DiscoveryProtocol.Text(envelope.RootElement, "signature"));
        }
        catch (FormatException) { throw new DiscoveryException("discovery_owned_document_invalid"); }
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(current.PublicKey), out _);
        if (signature.Length != 64 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new DiscoveryException("discovery_owned_document_invalid");
        var document = DiscoveryProtocol.ParseDocument(payload);
        if (document.DiscoveryId != current.DiscoveryId || document.Revision > current.Revision)
            throw new DiscoveryException("discovery_owned_document_invalid");
        if (document.Revision == current.Revision && current.PendingDocument is { } intent && !intent.AsSpan().SequenceEqual(remote.Document))
            throw new DiscoveryException("discovery_same_revision_conflict");
    }
    private static void VerifyContainer(PrivateDiscoveryRemote remote, PrivateDiscoveryRegistration current)
    {
        var expected = Metadata(current);
        if (!remote.IsPrivate || remote.Metadata.Count != expected.Count ||
            expected.Any(p => !remote.Metadata.TryGetValue(p.Key, out var value) || value != p.Value))
            throw new DiscoveryException("discovery_ownership_mismatch");
    }
    private static void VerifyPolicies(PrivateDiscoveryRemote remote, PrivateDiscoveryRegistration current)
    {
        if (remote.Policies.Count > 2 || remote.Policies.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != remote.Policies.Count || remote.Policies.Any(p =>
            p.Id != current.PolicyId && p.Id != current.PendingPolicyId && p.Id != current.PreviousPolicyId ||
            p.Permissions != "r" || p.StartsAtUtc.Offset != TimeSpan.Zero || p.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            p.ExpiresAtUtc <= p.StartsAtUtc || p.ExpiresAtUtc - p.StartsAtUtc > TimeSpan.FromDays(90).Add(TimeSpan.FromMinutes(5)) ||
            current.Phase == PrivateDiscoveryPhase.Ready &&
                (p.Id == current.PolicyId && p.ExpiresAtUtc != current.PolicyExpiry || p.Id == current.PreviousPolicyId && p.ExpiresAtUtc != current.PreviousPolicyExpiry)))
            throw new DiscoveryException("discovery_foreign_policy");
    }
    private static void ValidateRegistration(PrivateDiscoveryRegistration value)
    {
        if (value.Target is null || value.Owner is null || value.Container is null || value.SigningKey is null || value.PublicKey is null ||
            value.PolicyId is null || value.SigningKey.Length is < 1 or > 512 || value.PublicKey.Length is < 1 or > 256)
            throw new DiscoveryException("discovery_journal_invalid");
        value.Target.Validate();
        if (value.DiscoveryId == Guid.Empty || value.Container != "agentsignaler-" + value.DiscoveryId.ToString("N") ||
            value.Owner.Length != 32 || !Guid.TryParseExact(value.Owner, "N", out _) || value.Revision < 0 ||
            !Enum.IsDefined(value.Phase) || value.RetiredPolicyIds is null || value.RetiredPolicyIds.Count > 4096 ||
            value.RetiredPolicyIds.Distinct(StringComparer.Ordinal).Count() != value.RetiredPolicyIds.Count ||
            !IsPolicy(value.PolicyId) || value.PendingPolicyId is not null && !IsPolicy(value.PendingPolicyId) ||
            value.PreviousPolicyId is not null && !IsPolicy(value.PreviousPolicyId) || value.RetiredPolicyIds.Any(p => !IsPolicy(p)))
            throw new DiscoveryException("discovery_journal_invalid");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value.SigningKey); }
        catch (FormatException) { throw new DiscoveryException("discovery_signing_key"); }
        try
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(bytes, out var read);
            if (read != bytes.Length || DiscoveryCodec.ExportPublicKey(key) != value.PublicKey)
                throw new DiscoveryException("discovery_signing_key");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        if (value.Phase == PrivateDiscoveryPhase.Ready && (value.RetiredPolicyIds.Contains(value.PolicyId, StringComparer.Ordinal) ||
            value.PreviousPolicyId is not null && value.RetiredPolicyIds.Contains(value.PreviousPolicyId, StringComparer.Ordinal)))
            throw new DiscoveryException("discovery_retired_policy");
        if (value.PendingDocument is { } pending)
        {
            var document = ReadSignedPayload(pending);
            if (document.DiscoveryId != value.DiscoveryId || document.Revision != value.Revision)
                throw new DiscoveryException("discovery_publication_intent_invalid");
            if (value.PendingBlobETag is { } etag && (etag.Length is < 1 or > 128 || etag.Any(c => c > 127 || char.IsControl(c))))
                throw new DiscoveryException("discovery_publication_intent_invalid");
        }
    }
    private static DiscoveryDocument ReadSignedPayload(byte[] envelope)
    {
        using var parsed = DiscoveryProtocol.ParseObject(envelope, DiscoveryProtocol.MaximumEnvelopeBytes, "payload", "signature");
        try { return DiscoveryProtocol.ParseDocument(Convert.FromBase64String(DiscoveryProtocol.Text(parsed.RootElement, "payload"))); }
        catch (FormatException) { throw new DiscoveryException("discovery_publication_intent_invalid"); }
    }
    private static string NewPolicyId() => "as-" + Guid.NewGuid().ToString("N");
    private static bool IsPolicy(string value) => value is not null && value.StartsWith("as-", StringComparison.Ordinal) && Guid.TryParseExact(value[3..], "N", out _);
    private void RequireRegistration() { if (registration is null || registration.Phase == PrivateDiscoveryPhase.Removed) throw new DiscoveryException("discovery_not_configured"); }
    private void RequireReady() { RequireRegistration(); if (registration!.Phase != PrivateDiscoveryPhase.Ready) throw new DiscoveryException("discovery_recovery_required"); }
    private Task SaveAsync(CancellationToken token) => journal.SaveAsync(registration!, token);
    private void SetStatus(string message)
    {
        var value = registration;
        Status = new(value is not null && value.Phase != PrivateDiscoveryPhase.Removed,
            value is not null && value.Phase is not (PrivateDiscoveryPhase.Ready or PrivateDiscoveryPhase.Revoked or PrivateDiscoveryPhase.Removed),
            value?.Phase == PrivateDiscoveryPhase.Revoked, message, value?.PolicyExpiry, value?.LastPublishedAtUtc, value?.Revision ?? 0,
            PublishingEnabled: value?.PublishingEnabled ?? true, ReaderGroupId: value?.PolicyId, PreviousReaderGroupId: value?.PreviousPolicyId,
            PublicationPending: value?.PendingDocument is not null);
        Changed?.Invoke();
    }
    private sealed class Adapter(IPrivateDiscoveryAzure value) : IDisposable
    {
        public IPrivateDiscoveryAzure Value { get; } = value;
        public void Dispose() => (Value as IDisposable)?.Dispose();
    }
}