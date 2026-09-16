using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class WindowsAppConnectionValidatorTests
{
    [Fact]
    public void ValidOriginalStringIsNeverReconstructed()
    {
        var original = ConnectionTestData.Uri.Replace("ms-cloudpc", "MS-CLOUDPC")
            .Replace("user%40", "USER%40").Replace("environment=prod", "environment=%70rod");
        var uri = WindowsAppConnectionValidator.Validate(original, ConnectionTestData.Mapping.AzureAccountUpn);
        Assert.Equal(original, uri.OriginalString);
        Assert.Equal(WindowsAppConnection.ValidateConnectionUri(original, "user@example.com").OriginalString, uri.OriginalString);
    }

    public static IEnumerable<object[]> UnsafeUris()
    {
        var uri = ConnectionTestData.Uri;
        foreach (var value in new[]
        {
            "", "relative", uri.Replace("ms-cloudpc:", "https:"), uri.Replace("connect?", "//connect?"),
            uri.Replace("connect?", "//host/connect?"), uri.Replace("connect?", "//user@host:42/connect?"),
            uri.Replace("connect?", "Connect?"), uri.Replace("connect?", "/connect?"),
            uri.Replace("connect?", "connect/anything?"), uri.Replace("connect?", "%63onnect?"),
            uri + "#fragment", uri + "#", uri + "\n", uri + "\0",
            uri.Replace("source=test", "source=%"), uri.Replace("source=test", "source=%0"),
            uri.Replace("source=test", "source=%GG"), uri.Replace("source=test", "source=%0a"),
            uri.Replace("source=test", "source=%7F"), uri.Replace("source=test", "source=%C2%85"),
            uri.Replace("source=test", "source="), uri.Replace("source=test", "source"),
            uri.Replace("source=test", "source=" + new string('a', 513)),
            uri.Replace("source=test", "=test"), uri + "&other=secret", uri + "&",
            uri.Replace("cpcid=", "CPCID="), uri.Replace("cpcid=", "%63pcid="),
            uri.Replace(ConnectionTestData.CloudPcId.ToString(), Guid.Empty.ToString()),
            uri.Replace(ConnectionTestData.CloudPcId.ToString(), "not-a-guid"),
            uri.Replace("user%40example.com", "different%40example.com"),
            uri.Replace("user%40example.com", "user+%40example.com"),
            new string('a', 4097)
        })
            yield return [value];
        foreach (var name in new[] { "cpcid", "username", "environment", "version", "source" })
        {
            var segments = uri[(uri.IndexOf('?') + 1)..].Split('&');
            yield return ["ms-cloudpc:connect?" + string.Join("&", segments.Where(s => !s.StartsWith(name + "=")))];
            yield return [uri + "&" + segments.Single(s => s.StartsWith(name + "="))];
        }
    }

    [Theory]
    [MemberData(nameof(UnsafeUris))]
    public void EveryUnsafeRuleFailsWithSanitizedError(string uri)
    {
        var error = Assert.Throws<WindowsAppConnectionException>(() =>
            WindowsAppConnectionValidator.Validate(uri, "user@example.com"));
        Assert.Equal(WindowsAppFailure.UnsafeUri, error.Failure);
        Assert.Equal("Unavailable", error.Status);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("cpcid", error.ToString());
        Assert.DoesNotContain("user@example.com", error.ToString());
    }

    [Fact]
    public void DecodedValueBoundaryIsAccepted()
    {
        var original = ConnectionTestData.Uri.Replace("source=test", "source=" + new string('a', 512));
        Assert.Equal(original, WindowsAppConnectionValidator.Validate(original, "user@example.com").OriginalString);
    }

    [Fact]
    public void ExactTotalLengthBoundaryIsAcceptedAndOneMoreIsRejected()
    {
        var prefix = $"ms-cloudpc:connect?cpcid={ConnectionTestData.CloudPcId}&username=user%40example.com&environment=";
        var original = prefix + string.Concat(Enumerable.Repeat("%61", 512)) +
            "&version=" + string.Concat(Enumerable.Repeat("%62", 512)) + "&source=";
        var remaining = 4096 - original.Length;
        original += string.Concat(Enumerable.Repeat("%63", remaining / 3)) + new string('c', remaining % 3);
        Assert.Equal(4096, original.Length);
        Assert.Equal(original, WindowsAppConnectionValidator.Validate(original, "user@example.com").OriginalString);
        Assert.Throws<WindowsAppConnectionException>(() => WindowsAppConnectionValidator.Validate(original + "c", "user@example.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" user@example.com")]
    [InlineData("user@@example.com")]
    public void InvalidIdentityUsesSharedValidation(string upn) =>
        Assert.Throws<WindowsAppConnectionException>(() => WindowsAppConnectionValidator.Validate(ConnectionTestData.Uri, upn));

    [Fact]
    public void MissingAndInvalidMappingAreDistinct()
    {
        Assert.Equal(WindowsAppFailure.MissingMapping,
            Assert.Throws<WindowsAppConnectionException>(() => WindowsAppConnectionValidator.ValidateMapping(null)).Failure);
        Assert.Equal(WindowsAppFailure.InvalidMapping,
            Assert.Throws<WindowsAppConnectionException>(() => WindowsAppConnectionValidator.ValidateMapping(
                ConnectionTestData.Mapping with { ProjectName = "../invalid" })).Failure);
    }
}

internal static class ConnectionTestData
{
    public static readonly Guid Tenant = Guid.Parse("e9a9a367-4440-4c28-ad4e-9283d2f616fd");
    public static readonly Guid Subscription = Guid.Parse("0c44c67d-ab9b-414e-84e5-f8c45d77a0d3");
    public static readonly Guid CloudPcId = Guid.Parse("79b94943-d50c-4412-b826-fb055b7f920c");
    public static readonly DateTimeOffset RetrievedAt = new(2026, 9, 15, 3, 0, 0, TimeSpan.Zero);
    public static string Uri => $"ms-cloudpc:connect?cpcid={CloudPcId}&username=user%40example.com&environment=prod&version=1&source=test";
    public static WindowsAppConnection Mapping => new(new Uri("https://example.region.devcenter.azure.com/"),
        "project.one", "devbox-01", "user@example.com", Tenant, null, null);
    public static WindowsAppConnection CachedMapping => Mapping with { LastKnownConnectionUri = Uri, ConnectionUriRetrievedAtUtc = RetrievedAt };
    public static string Account => $$$"""{"id":"{{{Subscription}}}","tenantId":"{{{Tenant}}}","state":"Enabled","user":{"name":"USER@example.com","type":"user"}}""";
    public static string Response => System.Text.Json.JsonSerializer.Serialize(new
    {
        cloudPcConnectionUrl = Uri,
        rdpConnectionUrl = "ms-avd:connect?resourceid=ignored",
        webUrl = "https://example.test/ignored"
    });
    public static ResolvedDevBoxConnection Resolved => new(new Uri(Uri), "user@example.com", Tenant, Subscription, RetrievedAt);
}
