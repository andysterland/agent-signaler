using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Discovery;

/// <summary>Version-one bounded package encoding and exact-byte ECDSA P-256/SHA-256 verification.</summary>
public static class DiscoveryCodec
{
    public static DiscoveryConnectionPackage ParsePackage(string encoded)
    {
        if (encoded is null || encoded.Length > DiscoveryProtocol.MaximumPackageBytes ||
            !encoded.StartsWith(DiscoveryProtocol.PackagePrefix, StringComparison.Ordinal))
            throw new DiscoveryException("discovery_package_format");
        var value = encoded.AsSpan(DiscoveryProtocol.PackagePrefix.Length);
        if (value.Length == 0 || value.Length % 4 == 1 || value.ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"))
            throw new DiscoveryException("discovery_package_encoding");
        byte[] bytes;
        try
        {
            var base64 = value.ToString().Replace('-', '+').Replace('_', '/');
            bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            if (Base64Url(bytes) != value.ToString()) throw new DiscoveryException("discovery_package_encoding");
        }
        catch (FormatException) { throw new DiscoveryException("discovery_package_encoding"); }
        try
        {
            var package = DiscoveryProtocol.ParsePackageJson(bytes);
            using var key = ImportPublicKey(package.SigningPublicKey);
            return package;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string EncodePackage(DiscoveryConnectionPackage package)
    {
        DiscoveryProtocol.ValidatePackage(package);
        using var key = ImportPublicKey(package.SigningPublicKey);
        var bytes = Write(writer =>
        {
            writer.WriteNumber("version", DiscoveryProtocol.Version);
            writer.WriteString("discoveryId", package.DiscoveryId);
            writer.WriteString("blobSasUri", package.BlobSasUri.OriginalString);
            writer.WriteString("signingPublicKey", package.SigningPublicKey);
        });
        try
        {
            var result = DiscoveryProtocol.PackagePrefix + Base64Url(bytes);
            if (result.Length > DiscoveryProtocol.MaximumPackageBytes) throw new DiscoveryException("discovery_package_size");
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static byte[] Sign(DiscoveryDocument document, ECDsa signingKey)
    {
        DiscoveryProtocol.ValidateDocument(document);
        ValidateKey(signingKey);
        var payload = Write(writer =>
        {
            writer.WriteNumber("version", document.Version);
            writer.WriteString("discoveryId", document.DiscoveryId);
            writer.WriteNumber("revision", document.Revision);
            writer.WriteString("publishedAtUtc", document.PublishedAtUtc);
            writer.WriteString("expiresAtUtc", document.ExpiresAtUtc);
            writer.WriteString("state", document.State == DiscoveryState.Available ? "available" : "unavailable");
            if (document.DashboardBaseUrl is not null) writer.WriteString("dashboardBaseUrl", document.DashboardBaseUrl.AbsoluteUri);
        });
        var signature = signingKey.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = Write(writer =>
        {
            writer.WriteBase64String("payload", payload);
            writer.WriteBase64String("signature", signature);
        });
        if (envelope.Length > DiscoveryProtocol.MaximumEnvelopeBytes) throw new DiscoveryException("discovery_document_size");
        return envelope;
    }

    /// <summary>Authenticates before parsing, enforces freshness, and compares an optional persisted replay checkpoint.</summary>
    public static DiscoveryVerificationResult Verify(ReadOnlySpan<byte> envelope, DiscoveryConnectionPackage package,
        DateTimeOffset now, DiscoveryReplayState? previous = null)
    {
        DiscoveryProtocol.ValidatePackage(package);
        using var key = ImportPublicKey(package.SigningPublicKey);
        using var parsed = DiscoveryProtocol.ParseObject(envelope, DiscoveryProtocol.MaximumEnvelopeBytes, "payload", "signature");
        var payload = Decode(DiscoveryProtocol.Text(parsed.RootElement, "payload"), DiscoveryProtocol.MaximumEnvelopeBytes);
        var signature = Decode(DiscoveryProtocol.Text(parsed.RootElement, "signature"), 64);
        if (signature.Length != 64 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new DiscoveryException("discovery_signature");
        // Parse only authenticated bytes; no JSON canonicalization participates in verification.
        var document = DiscoveryProtocol.ParseDocument(payload);
        if (document.DiscoveryId != package.DiscoveryId) throw new DiscoveryException("discovery_identity_mismatch");
        if (document.ExpiresAtUtc <= now || document.PublishedAtUtc > now && document.PublishedAtUtc - now > DiscoveryProtocol.MaximumClockSkew)
            throw new DiscoveryException("discovery_expired_or_future");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(payload));
        if (previous is not null && (previous.DiscoveryId != document.DiscoveryId || previous.Revision <= 0 ||
            !IsFingerprint(previous.PayloadFingerprint) || document.Revision < previous.Revision ||
            document.Revision == previous.Revision && !string.Equals(previous.PayloadFingerprint, fingerprint, StringComparison.Ordinal)))
            throw new DiscoveryException("discovery_replay");
        return new(document, new(document.DiscoveryId, document.Revision, fingerprint));
    }

    public static string ExportPublicKey(ECDsa key)
    {
        ValidateKey(key);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private static ECDsa ImportPublicKey(string encoded)
    {
        byte[] bytes;
        try { bytes = Decode(encoded, 128); }
        catch (DiscoveryException) { throw new DiscoveryException("discovery_public_key"); }
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length) throw new DiscoveryException("discovery_public_key");
            ValidateKey(key);
            return key;
        }
        catch (CryptographicException) { key.Dispose(); throw new DiscoveryException("discovery_public_key"); }
        catch { key.Dispose(); throw; }
    }

    private static void ValidateKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new DiscoveryException("discovery_key_curve");
    }

    private static byte[] Decode(string encoded, int maximumBytes)
    {
        if (encoded.Length > (maximumBytes + 2) / 3 * 4) throw new DiscoveryException("discovery_base64_size");
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > maximumBytes || Convert.ToBase64String(bytes) != encoded)
                throw new DiscoveryException("discovery_base64");
            return bytes;
        }
        catch (FormatException) { throw new DiscoveryException("discovery_base64"); }
    }

    private static bool IsFingerprint(string fingerprint) => fingerprint is { Length: 64 } &&
        fingerprint.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
