using System.Net;
using System.Text;
using System.Text.Json;
using AgentSignaler.Updates;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class ReleasePinningTests
{
    private static readonly byte[] Manifest = Encoding.ASCII.GetBytes(new string('a', 64) + "  AgentSignaler.Dashboard.msi\r\n" +
        new string('b', 64) + "  AgentSignaler.Remote.msi\r\n");
    private static ReleaseCandidate Candidate => new(10, "v1.0.1", new(1, 0, 1),
        new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), true,
        [new(11, "AgentSignaler.Dashboard.msi", 100), new(12, "AgentSignaler.Remote.msi", 100), new(13, "SHA256SUMS.txt", Manifest.Length)]);

    [Theory]
    [InlineData("release")]
    [InlineData("tag")]
    [InlineData("size")]
    [InlineData("asset")]
    [InlineData("deleted")]
    [InlineData("draft")]
    public async Task ChangedOrDeletedPinnedReleaseFailsBeforeAnyAssetDownload(string change)
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            count++;
            if (change == "deleted") return new(HttpStatusCode.NotFound);
            var candidate = change switch
            {
                "release" => Candidate with { Id = 99 },
                "tag" => Candidate with { Tag = "v1.0.2", Version = new(1, 0, 2) },
                "size" => Candidate with { Assets = [Candidate.Assets[0], Candidate.Assets[1] with { Size = 101 }, Candidate.Assets[2]] },
                "asset" => Candidate with { Assets = [Candidate.Assets[0], Candidate.Assets[1] with { Id = 99 }, Candidate.Assets[2]] },
                _ => Candidate
            };
            return Json(candidate, change == "draft");
        }));
        Assert.Equal("update-pin-validation-failed", (await Assert.ThrowsAsync<UpdateDiscoveryException>(
            () => new ReleasePinning(http).PinAsync(Candidate, UpdatePackage.Remote))).Code);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/fixture")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/fixture")]
    [InlineData("https://evil.example/fixture")]
    [InlineData("https://user@release-assets.githubusercontent.com/fixture")]
    [InlineData("https://github.com/other/repo/releases/download/v1.0.1/SHA256SUMS.txt")]
    [InlineData("https://release-assets.githubusercontent.com:444/fixture")]
    public async Task UnsafeRedirectNeverReceivesARequest(string target)
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++count == 1) return Json(Candidate);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new(target);
            return response;
        }));
        await Assert.ThrowsAsync<UpdateDiscoveryException>(() => new ReleasePinning(http).PinAsync(Candidate, UpdatePackage.Remote));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task RedirectLoopsAndChangedDownloadSizeAreBounded()
    {
        var count = 0;
        using var loop = new HttpClient(new Handler(_ =>
        {
            if (++count == 1) return Json(Candidate);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://release-assets.githubusercontent.com/loop");
            return response;
        }));
        await Assert.ThrowsAsync<UpdateDiscoveryException>(() => new ReleasePinning(loop).PinAsync(Candidate, UpdatePackage.Remote));
        Assert.Equal(7, count);
        count = 0;
        using var changed = new HttpClient(new Handler(_ => ++count == 1 ? Json(Candidate) :
            new(HttpStatusCode.OK) { Content = new ByteArrayContent([.. Manifest, (byte)' ']) }));
        await Assert.ThrowsAsync<UpdateDiscoveryException>(() => new ReleasePinning(changed).PinAsync(Candidate, UpdatePackage.Remote));
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("case")]
    [InlineData("separator")]
    [InlineData("hash")]
    [InlineData("missing")]
    [InlineData("encoding")]
    public void StrictManifestRejectsAmbiguousOrUnsupportedEntries(string kind)
    {
        var valid = Encoding.ASCII.GetString(Manifest);
        var invalid = kind switch
        {
            "duplicate" => valid + valid,
            "unknown" => valid + new string('c', 64) + "  Other.msi\n",
            "case" => valid.Replace("Dashboard", "dashboard", StringComparison.Ordinal),
            "separator" => valid.Replace("  Agent", " *Agent", StringComparison.Ordinal),
            "hash" => valid.Replace('a', 'g'),
            "missing" => valid.Split('\n')[0],
            "encoding" => "\uFEFF" + valid,
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidDataException>(() => ReleasePinning.ReadChecksums(Encoding.UTF8.GetBytes(invalid)));
    }

    private static HttpResponseMessage Json(ReleaseCandidate candidate, bool draft = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            id = candidate.Id, tag_name = candidate.Tag, draft, prerelease = candidate.Prerelease,
            published_at = candidate.PublishedAt, assets = candidate.Assets.Select(asset => new { id = asset.Id, name = asset.Name, size = asset.Size })
        }))
    };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
}