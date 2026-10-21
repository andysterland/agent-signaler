using System.Collections.Immutable;
using System.Text.Json;

namespace AgentSignaler.Updates;

public sealed record UpdateCheckCache(int SchemaVersion, ReleaseCandidate? Candidate, string? ETag,
    bool SinglePage, DateTimeOffset CheckedAt, DateTimeOffset NextCheckAt, int FailureCount,
    string? DiagnosticCode = null);

public interface IUpdateCheckStore
{
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
    Task<UpdateCheckCache?> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(UpdateCheckCache cache, CancellationToken cancellationToken);
}

public static class UpdateCheckCacheCodec
{
    public static byte[] Write(UpdateCheckCache cache)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", cache.SchemaVersion);
            writer.WritePropertyName("candidate");
            if (cache.Candidate is not { } candidate) writer.WriteNullValue();
            else
            {
                UpdatePolicy.ValidateCandidate(candidate);
                writer.WriteStartObject();
                writer.WriteNumber("id", candidate.Id);
                writer.WriteString("tag", candidate.Tag);
                writer.WriteString("publishedAt", candidate.PublishedAt);
                writer.WriteBoolean("prerelease", candidate.Prerelease);
                writer.WriteStartArray("assets");
                foreach (var asset in candidate.Assets)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", asset.Id);
                    writer.WriteString("name", asset.Name);
                    writer.WriteNumber("size", asset.Size);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteString("etag", cache.ETag);
            writer.WriteBoolean("singlePage", cache.SinglePage);
            writer.WriteString("checkedAt", cache.CheckedAt);
            writer.WriteString("nextCheckAt", cache.NextCheckAt);
            writer.WriteNumber("failureCount", cache.FailureCount);
            writer.WriteString("diagnosticCode", cache.DiagnosticCode);
            writer.WriteEndObject();
        }
        var bytes = stream.ToArray();
        _ = Read(bytes);
        return bytes;
    }

    public static UpdateCheckCache Read(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = StrictUpdateJson.Parse(bytes, UpdatePolicy.CacheLimit);
            var root = document.RootElement;
            StrictUpdateJson.Fields(root, "schemaVersion", "candidate", "etag", "singlePage", "checkedAt", "nextCheckAt", "failureCount", "diagnosticCode");
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidDataException("update-cache-schema");
            ReleaseCandidate? candidate = null;
            var release = root.GetProperty("candidate");
            if (release.ValueKind != JsonValueKind.Null)
            {
                StrictUpdateJson.Fields(release, "id", "tag", "publishedAt", "prerelease", "assets");
                var assets = release.GetProperty("assets");
                if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() != 3)
                    throw new InvalidDataException("update-cache-assets");
                var tag = StrictUpdateJson.Text(release.GetProperty("tag"), 14);
                candidate = new(StrictUpdateJson.PositiveId(release.GetProperty("id")), tag, MsiReleaseVersion.Parse(tag, true),
                    StrictUpdateJson.Utc(release.GetProperty("publishedAt")), release.GetProperty("prerelease").GetBoolean(),
                    assets.EnumerateArray().Select(asset =>
                    {
                        StrictUpdateJson.Fields(asset, "id", "name", "size");
                        return new ReleaseAsset(StrictUpdateJson.PositiveId(asset.GetProperty("id")),
                            StrictUpdateJson.Text(asset.GetProperty("name"), 64), StrictUpdateJson.PositiveId(asset.GetProperty("size")));
                    }).ToImmutableArray());
                UpdatePolicy.ValidateCandidate(candidate);
            }
            var etag = root.GetProperty("etag").ValueKind == JsonValueKind.Null ? null : StrictUpdateJson.Text(root.GetProperty("etag"), 256);
            if (etag is not null && !System.Net.Http.Headers.EntityTagHeaderValue.TryParse(etag, out _))
                throw new InvalidDataException("update-cache-etag");
            var code = root.GetProperty("diagnosticCode").ValueKind == JsonValueKind.Null ? null : StrictUpdateJson.Text(root.GetProperty("diagnosticCode"), 64);
            if (code is not null && code is not ("update-rate-limited" or "update-check-timeout" or "update-network-failed" or
                "update-metadata-invalid" or "update-http-failed" or "update-check-incomplete" or "update-no-published-release"))
                throw new InvalidDataException("update-cache-code");
            var count = root.GetProperty("failureCount").GetInt32();
            var checkedAt = StrictUpdateJson.Utc(root.GetProperty("checkedAt"));
            var next = StrictUpdateJson.Utc(root.GetProperty("nextCheckAt"));
            if (count is < 0 or > 32 || (count == 0) != (code is null) || next < checkedAt || count == 0 && candidate is null)
                throw new InvalidDataException("update-cache-times");
            return new(1, candidate, etag, root.GetProperty("singlePage").GetBoolean(), checkedAt, next, count, code);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("update-cache-invalid"); }
    }
}