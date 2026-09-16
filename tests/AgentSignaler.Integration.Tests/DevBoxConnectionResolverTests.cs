using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class DevBoxConnectionResolverTests
{
    [Fact]
    public async Task SuccessfulResponseUsesExactGetIdentityAndInjectedRetrievalTime()
    {
        var cli = new FakeCli(new(0, ConnectionTestData.Account, ""), new(0, ConnectionTestData.Response, ""));
        var time = new TestTimeProvider();
        time.Advance(TimeSpan.FromHours(1));
        using var cancellation = new CancellationTokenSource();
        var result = await new DevBoxConnectionResolver(cli, time).ResolveAsync(ConnectionTestData.Mapping, cancellation.Token);
        Assert.Equal(ConnectionTestData.Uri, result.ConnectionUri.OriginalString);
        Assert.Equal("user@example.com", result.AzureAccountUpn);
        Assert.Equal(ConnectionTestData.Tenant, result.AzureTenantId);
        Assert.Equal(ConnectionTestData.Subscription, result.SubscriptionId);
        Assert.Equal(time.GetUtcNow(), result.RetrievedAtUtc);
        Assert.Equal(2, cli.Commands.Count);
        Assert.Equal(["account", "show", "--output", "json"], cli.Commands[0].CreateStartInfo(@"C:\CLI\az.exe").ArgumentList);
        Assert.Equal(["rest", "--method", "get", "--resource", "https://devcenter.azure.com", "--url",
            "https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01/remoteConnection?api-version=2025-02-01"],
            cli.Commands[1].CreateStartInfo(@"C:\CLI\az.exe").ArgumentList);
        Assert.All(cli.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.DoesNotContain("user@", result.ToString());
        Assert.DoesNotContain("ms-cloudpc:", result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteResponseReadsCloudPcUrlRegardlessOfAdditionalProperties(bool extraProperties)
    {
        var response = new JsonObject();
        if (extraProperties)
        {
            response["rdpConnectionUrl"] = "ms-avd:connect?resourceid=ignored";
            response["webUrl"] = "https://example.test/ignored";
            response["futureProperty"] = new JsonObject { ["ignored"] = true };
        }
        response["cloudPcConnectionUrl"] = ConnectionTestData.Uri;
        var cli = new FakeCli(new(0, ConnectionTestData.Account, ""), new(0, response.ToJsonString(), ""));

        var result = await new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default);

        Assert.Equal(ConnectionTestData.Uri, result.ConnectionUri.OriginalString);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("A-z_0.9")]
    [InlineData("a..............................................................")]
    public void UrlUsesValidatedEscapedSegmentsAndSameOrigin(string name)
    {
        var mapping = ConnectionTestData.Mapping with { ProjectName = name, DevBoxName = name };
        var start = AzureCliCommand.RemoteConnection(mapping).CreateStartInfo();
        var url = new Uri(start.ArgumentList[^1]);
        Assert.Equal(mapping.DevCenterEndpoint.IdnHost, url.IdnHost);
        Assert.Equal("https", url.Scheme);
        Assert.Equal(443, url.Port);
        Assert.Empty(url.UserInfo);
        Assert.Empty(url.Fragment);
        Assert.Equal($"/projects/{Uri.EscapeDataString(name)}/users/me/devboxes/{Uri.EscapeDataString(name)}/remoteConnection", url.AbsolutePath);
        Assert.Equal("?api-version=2025-02-01", url.Query);
    }

    [Theory]
    [InlineData("../start")]
    [InlineData("box/stop")]
    [InlineData("box?api-version=evil")]
    [InlineData("box#frag")]
    [InlineData("box%2fstart")]
    [InlineData("box --method post")]
    [InlineData("box\\start")]
    public void NameInjectionCannotChangeEndpointOrAddActions(string name) =>
        Assert.Throws<WindowsAppConnectionException>(() => AzureCliCommand.RemoteConnection(ConnectionTestData.Mapping with { DevBoxName = name }));

    [Theory]
    [InlineData("http://example.region.devcenter.azure.com/")]
    [InlineData("https://example.region.devcenter.azure.com.evil.com/")]
    [InlineData("https://example.region.devcenter.azure.com:444/")]
    [InlineData("https://example.region.devcenter.azure.com/action")]
    [InlineData("https://example.region.devcenter.azure.com/?query")]
    [InlineData("https://user@example.region.devcenter.azure.com/")]
    [InlineData("https://example.region.devcenter.azure.com/#fragment")]
    [InlineData("https://management.azure.com/")]
    [InlineData("https://\u200D.region.devcenter.azure.com/")]
    [InlineData("https://\uFFFD.region.devcenter.azure.com/")]
    public async Task InvalidMappingNeverReachesCli(string endpoint)
    {
        var cli = new FakeCli();
        await Assert.ThrowsAsync<WindowsAppConnectionException>(() => new DevBoxConnectionResolver(cli)
            .ResolveAsync(ConnectionTestData.Mapping with { DevCenterEndpoint = new Uri(endpoint) }, default));
        Assert.Empty(cli.Commands);
    }

    public static IEnumerable<object[]> InvalidAccountJson()
    {
        foreach (var json in new[] { "", "{", "[]", "null", "\"secret\"", "123", "{}",
            ConnectionTestData.Account + " {}", ConnectionTestData.Account + " trailing",
            ConnectionTestData.Account.Replace("\"state\":", "\"state\":\"Enabled\",\"state\":"),
            ConnectionTestData.Account.Replace("\"name\":", "\"name\":\"user@example.com\",\"name\":"),
            ConnectionTestData.Account.Replace("\"user\":", "\"user\":{},\"user\":"),
            ConnectionTestData.Account.Replace("\"id\":", "\"id\":null,\"id\":"),
            ConnectionTestData.Account.Replace("\"state\":", "\"State\":"),
            ConnectionTestData.Account[..^1] + ",}",
            "/* comment */" + ConnectionTestData.Account })
            yield return [json];
        foreach (var property in new[] { "id", "tenantId", "state", "user" })
        {
            var root = JsonNode.Parse(ConnectionTestData.Account)!.AsObject();
            root.Remove(property);
            yield return [root.ToJsonString()];
            foreach (var value in new[] { "null", "0", "true", "[]", "{}" })
            {
                root = JsonNode.Parse(ConnectionTestData.Account)!.AsObject();
                root[property] = JsonNode.Parse(value);
                yield return [root.ToJsonString()];
            }
        }
        foreach (var property in new[] { "name", "type" })
        {
            foreach (var value in new[] { "null", "0", "true", "[]", "{}" })
            {
                var root = JsonNode.Parse(ConnectionTestData.Account)!;
                root["user"]![property] = JsonNode.Parse(value);
                yield return [root.ToJsonString()];
            }
            var missing = JsonNode.Parse(ConnectionTestData.Account)!;
            missing["user"]!.AsObject().Remove(property);
            yield return [missing.ToJsonString()];
        }
        yield return [new string(' ', AzureCliProcess.MaximumOutputBytes + 1)];
        yield return [ConnectionTestData.Account[..^1] + ",\"ignored\":" + new string('[', 17) + "0" + new string(']', 17) + "}"];
        foreach (var invalidString in new[] { "\\uD800", "\\uDC00", "\\uD800x" })
        {
            foreach (var property in new[] { "id", "tenantId", "state", "name", "type" })
                yield return [ConnectionTestData.Account.Replace($"\"{property}\":",
                    $"\"{property}\":\"{invalidString}\",\"unused\":")];
            yield return [ConnectionTestData.Account[..^1] + $",\"{invalidString}\":\"secret\"}}"];
            yield return [ConnectionTestData.Account.Replace("\"name\":", $"\"{invalidString}\":\"secret\",\"name\":")];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidAccountJson))]
    public async Task AccountRequiresUnambiguousBoundedStrictJson(string json)
    {
        var cli = new FakeCli(new AzureCliResult(0, json, ""));
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() =>
            new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Single(cli.Commands);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("id", "", WindowsAppFailure.CliUnavailable)]
    [InlineData("id", "not-guid", WindowsAppFailure.CliUnavailable)]
    [InlineData("id", "00000000-0000-0000-0000-000000000000", WindowsAppFailure.CliUnavailable)]
    [InlineData("tenantId", "", WindowsAppFailure.CliUnavailable)]
    [InlineData("tenantId", "not-guid", WindowsAppFailure.CliUnavailable)]
    [InlineData("tenantId", "00000000-0000-0000-0000-000000000000", WindowsAppFailure.CliUnavailable)]
    [InlineData("tenantId", "22222222-2222-2222-2222-222222222222", WindowsAppFailure.AccountMismatch)]
    [InlineData("state", "Disabled", WindowsAppFailure.CliUnavailable)]
    [InlineData("state", "", WindowsAppFailure.CliUnavailable)]
    [InlineData("name", "different@example.com", WindowsAppFailure.AccountMismatch)]
    [InlineData("name", " USER@example.com", WindowsAppFailure.AccountMismatch)]
    [InlineData("name", "", WindowsAppFailure.AccountMismatch)]
    [InlineData("type", "servicePrincipal", WindowsAppFailure.CliUnavailable)]
    [InlineData("type", "USER", WindowsAppFailure.CliUnavailable)]
    [InlineData("type", "", WindowsAppFailure.CliUnavailable)]
    public async Task AccountRejectsInvalidOrMismatchedIdentity(string property, string value, object expected)
    {
        var json = JsonNode.Parse(ConnectionTestData.Account)!;
        if (property is "name" or "type") json["user"]![property] = value;
        else json[property] = value;
        var cli = new FakeCli(new AzureCliResult(0, json.ToJsonString(), ""));
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default));
        Assert.Equal((WindowsAppFailure)expected, error.Failure);
        Assert.Single(cli.Commands);
        AssertSafe(error);
    }

    [Fact]
    public async Task AccountAllowsUnneededPropertiesAndEnabledCaseVariants()
    {
        var json = JsonNode.Parse(ConnectionTestData.Account)!;
        json["state"] = "eNaBlEd";
        json["unneeded"] = new JsonObject { ["ignored"] = "secret" };
        json["user"]!["unneeded"] = "secret";
        var cli = new FakeCli(new(0, json.ToJsonString(), ""), new(0, ConnectionTestData.Response, ""));
        await new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default);
        Assert.Equal(2, cli.Commands.Count);
    }

    [Theory]
    [InlineData("Please run 'az login' to setup account.", true)]
    [InlineData("AADSTS50058: secret", true)]
    [InlineData("AADSTS50076: secret", true)]
    [InlineData("AADSTS700082: secret", true)]
    [InlineData("AADSTS70043: secret", true)]
    [InlineData("LoginRequired: secret", true)]
    [InlineData("InteractiveAuthenticationRequired: secret", true)]
    [InlineData("unrecognized secret error", false)]
    public async Task AccountExitCodesAreClassifiedWithoutRawOutput(string message, bool login)
    {
        foreach (var stderr in new[] { false, true })
        {
            var cli = new FakeCli(new AzureCliResult(1, stderr ? "" : message, stderr ? message : ""));
            var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default));
            Assert.Equal(login ? WindowsAppFailure.SignInRequired : WindowsAppFailure.CliUnavailable, error.Failure);
            Assert.Equal(login ? "Sign-in required" : "Unavailable", error.Status);
            Assert.Single(cli.Commands);
            AssertSafe(error);
        }
    }

    [Theory]
    [InlineData(401, WindowsAppFailure.DevBoxUnavailable)]
    [InlineData(403, WindowsAppFailure.DevBoxUnavailable)]
    [InlineData(404, WindowsAppFailure.DevBoxUnavailable)]
    [InlineData(408, WindowsAppFailure.ApiUnavailable)]
    [InlineData(429, WindowsAppFailure.ApiUnavailable)]
    [InlineData(500, WindowsAppFailure.ApiUnavailable)]
    [InlineData(502, WindowsAppFailure.ApiUnavailable)]
    [InlineData(503, WindowsAppFailure.ApiUnavailable)]
    [InlineData(504, WindowsAppFailure.ApiUnavailable)]
    public async Task HttpFailuresNeverProduceAConnection(int status, object expected)
    {
        var cli = new FakeCli(new(0, ConnectionTestData.Account, ""),
            new(1, "", $"ERROR: HTTP {status} secret {ConnectionTestData.Uri}"));
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default));
        Assert.Equal((WindowsAppFailure)expected, error.Failure);
        AssertSafe(error);
    }

    public static IEnumerable<object[]> MalformedResponses()
    {
        foreach (var json in new[] { "", "{", "[]", "null", "42", "\"secret\"", "{}", "{\"cloudPcConnectionUrl\":null}",
            "{\"cloudPcConnectionUrl\":123}", "{\"cloudPcConnectionUrl\":true}", "{\"cloudPcConnectionUrl\":[]}",
            "{\"cloudPcConnectionUrl\":{}}", "{\"CloudPcConnectionUrl\":\"secret\"}",
            ConnectionTestData.Response + "{}", ConnectionTestData.Response + " trailing",
            ConnectionTestData.Response[..^1] + ",}",
            "{\"rdpConnectionUrl\":\"ms-avd:connect?resourceid=ignored\",\"webUrl\":\"https://example.test/ignored\"}",
            ConnectionTestData.Response[..^1] + ",\"cloudPcConnectionUrl\":\"secret\"}",
            "/*comment*/" + ConnectionTestData.Response,
            new string(' ', AzureCliProcess.MaximumOutputBytes + 1),
            "{\"cloudPcConnectionUrl\":\"" + new string('é', AzureCliProcess.MaximumOutputBytes / 2) + "\"}" })
            yield return [json];
        foreach (var invalidString in new[] { "\\uD800", "\\uDC00", "\\uD800x" })
        {
            yield return [$"{{\"cloudPcConnectionUrl\":\"{invalidString}\"}}"];
            yield return [$"{{\"{invalidString}\":\"secret\"}}"];
        }
    }

    [Theory]
    [MemberData(nameof(MalformedResponses))]
    public async Task RemoteResponseRequiresUniqueCloudPcStringAndNoTrailingContent(string json)
    {
        var cli = new FakeCli(new(0, ConnectionTestData.Account, ""), new(0, json, ""));
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://evil.example/secret")]
    [InlineData("ms-cloudpc:connect?cpcid=secret")]
    public async Task UnsafeServiceUriIsDistinctFromMalformedJson(string uri)
    {
        var cli = new FakeCli(new(0, ConnectionTestData.Account, ""),
            new(0, JsonSerializer.Serialize(new { cloudPcConnectionUrl = uri }), ""));
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default));
        Assert.Equal(WindowsAppFailure.UnsafeUri, error.Failure);
        AssertSafe(error);
    }

    [Fact]
    public async Task LoginRequiredDuringRestIsClassifiedAndOversizedStderrIsRejected()
    {
        foreach (var oversized in new[] { false, true })
        {
            var cli = new FakeCli(new(0, ConnectionTestData.Account, ""),
                new(1, "", oversized ? new string('s', AzureCliProcess.MaximumOutputBytes + 1) : "Please run az login"));
            var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default));
            Assert.Equal(oversized ? WindowsAppFailure.MalformedResponse : WindowsAppFailure.SignInRequired, error.Failure);
        }
    }

    [Fact]
    public async Task CancellationBetweenCommandsPreventsRest()
    {
        using var source = new CancellationTokenSource();
        var cli = new FakeCli(new AzureCliResult(0, ConnectionTestData.Account, "")) { AfterRun = () => source.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, source.Token));
        Assert.Single(cli.Commands);
    }

    [Fact]
    public async Task CancellationAfterRestDoesNotReturnAResolvedConnection()
    {
        using var source = new CancellationTokenSource();
        var calls = 0;
        var cli = new FakeCli(new(0, ConnectionTestData.Account, ""), new(0, ConnectionTestData.Response, ""))
        {
            AfterRun = () => { if (++calls == 2) source.Cancel(); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, source.Token));
        Assert.Equal(2, cli.Commands.Count);
    }

    [Fact]
    public async Task CancellationBeforeResolutionMakesNoCalls()
    {
        var cli = new FakeCli();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, new CancellationToken(true)));
        Assert.Empty(cli.Commands);
    }

    [Fact]
    public async Task ClassifiedCliFailuresArePreserved()
    {
        var expected = new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable);
        var cli = new FakeCli { AfterRun = () => throw expected };
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default));
        Assert.Same(expected, error);
        AssertSafe(error);
    }

    [Fact]
    public async Task UnexpectedCliProgrammingErrorsPropagateUnclassified()
    {
        var expected = new InvalidOperationException("Programming error");
        var cli = new FakeCli { AfterRun = () => throw expected };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DevBoxConnectionResolver(cli).ResolveAsync(ConnectionTestData.Mapping, default));
        Assert.Same(expected, error);
    }

    private static void AssertSafe(WindowsAppConnectionException error)
    {
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("ms-cloudpc:", error.ToString());
        Assert.DoesNotContain("user@example.com", error.ToString());
    }

    private sealed class FakeCli(params AzureCliResult[] results) : IAzureCliProcess
    {
        private readonly Queue<AzureCliResult> results = new(results);
        public List<AzureCliCommand> Commands { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Action? AfterRun;
        public Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal(command.Timeout, timeout);
            Commands.Add(command);
            Tokens.Add(cancellationToken);
            AfterRun?.Invoke();
            return Task.FromResult(results.Dequeue());
        }
    }
}
