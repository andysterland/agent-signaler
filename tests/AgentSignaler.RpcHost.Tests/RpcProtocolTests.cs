using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts.Rpc.V1;
using AgentSignaler.RpcHost;
using Xunit;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcProtocolTests
{
    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("100", "1e2")]
    [InlineData("-0", "0.000e+99999999999999999999999999999999999999999")]
    [InlineData("1.20e-9999999999999999999999999", "12e-10000000000000000000000000")]
    [InlineData("9007199254740993", "9007199254740993.0")]
    public void NumericIdsCompareExactlyByValue(string first, string second)
    {
        using var a = JsonDocument.Parse(first);
        using var b = JsonDocument.Parse(second);
        Assert.True(RpcId.TryCreate(a.RootElement, out var left));
        Assert.True(RpcId.TryCreate(b.RootElement, out var right));
        Assert.Equal(left, right);
    }

    [Fact]
    public void NumberStringAndNullAreDistinctAndNumericEchoIsExact()
    {
        using var number = JsonDocument.Parse("1.00e+2");
        using var text = JsonDocument.Parse("\"100\"");
        using var nil = JsonDocument.Parse("null");
        RpcId.TryCreate(number.RootElement, out var a);
        RpcId.TryCreate(text.RootElement, out var b);
        RpcId.TryCreate(nil.RootElement, out var c);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        var response = Encoding.UTF8.GetString(RpcProtocol.Response(number.RootElement, new RpcCancelResult("notFound")));
        Assert.Contains("\"id\":1.00e+2", response);
    }

    [Fact]
    public void FullLengthHugeExponentsNormalizeSymbolicallyAndEchoOriginalJson()
    {
        var cases = new[]
        {
            ("1e+" + new string('9', 125), "10e" + new string('9', 124) + "8"),
            ("1.0e-" + new string('9', 123), "1e-" + new string('9', 123)),
            ("-0e-" + new string('9', 124), "0")
        };
        foreach (var (raw, equivalent) in cases)
        {
            Assert.Equal(128, raw.Length);
            var request = Parse(Request(raw)).Requests[0];
            Assert.Null(request.Fault);
            using var other = JsonDocument.Parse(equivalent);
            Assert.True(RpcId.TryCreate(request.Id, out var first));
            Assert.True(RpcId.TryCreate(other.RootElement, out var second));
            Assert.Equal(first, second);
            var response = Encoding.UTF8.GetString(RpcProtocol.Response(request.Id, null));
            Assert.Contains("\"id\":" + raw + ",", response);
            Assert.Equal(-32600, Parse(Request(raw + "0")).Requests[0].Fault!.Code);
        }
    }

    [Fact]
    public void AdjacentLargeIntegersAndHugeExponentsRemainDistinct()
    {
        foreach (var (left, right) in new[]
        {
            ("9007199254740992", "9007199254740993"),
            ("1e" + new string('9', 126), "1e" + new string('9', 125) + "8"),
            ("1e-" + new string('9', 125), "1e-" + new string('9', 124) + "8")
        })
        {
            using var a = JsonDocument.Parse(left);
            using var b = JsonDocument.Parse(right);
            Assert.True(RpcId.TryCreate(a.RootElement, out var first));
            Assert.True(RpcId.TryCreate(b.RootElement, out var second));
            Assert.NotEqual(first, second);
        }
    }

    [Theory]
    [InlineData("{", -32700)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"\\uD800\"}", -32700)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"jsonrpc\":\"2.0\",\"method\":\"settings.get\"}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"settings.update\",\"params\":{\"a\":{\"x\":1,\"x\":2}}}", -32600)]
    public void RejectsMalformedAndDuplicateObjects(string input, int code)
    {
        Assert.Equal(code, Assert.Throws<RpcFault>(() => Parse(input)).Code);
    }

    [Fact]
    public void IdAndStringLimitsAreExact()
    {
        Assert.Null(Parse(Request("\"" + new string('a', 128) + "\"")).Requests[0].Fault);
        Assert.NotNull(Parse(Request("\"" + new string('a', 129) + "\"")).Requests[0].Fault);
        var supplementary = string.Concat(Enumerable.Repeat("\U0001F600", 64));
        Assert.Null(Parse(Request(JsonSerializer.Serialize(supplementary))).Requests[0].Fault);
        Assert.NotNull(Parse(Request(JsonSerializer.Serialize(supplementary + "a"))).Requests[0].Fault);
        Assert.Null(Parse(Request(new string('1', 128))).Requests[0].Fault);
        Assert.NotNull(Parse(Request(new string('1', 129))).Requests[0].Fault);
        Parse("{\"jsonrpc\":\"2.0\",\"method\":\"settings.update\",\"params\":{\"note\":\"" + new string('a', 32768) + "\"}}");
        Assert.Equal(-32600, Assert.Throws<RpcFault>(() => Parse(
            "{\"jsonrpc\":\"2.0\",\"method\":\"settings.update\",\"params\":{\"note\":\"" + new string('a', 32769) + "\"}}")).Code);
    }

    [Fact]
    public void ObjectPropertyAndBatchLimitsAreExact()
    {
        var properties = Enumerable.Range(0, 128).Select(i => $"\"p{i}\":0");
        Parse("{\"jsonrpc\":\"2.0\",\"method\":\"settings.update\",\"params\":{" + string.Join(',', properties) + "}}");
        Assert.Equal(-32600, Assert.Throws<RpcFault>(() => Parse(
            "{\"jsonrpc\":\"2.0\",\"method\":\"settings.update\",\"params\":{" + string.Join(',', properties) + ",\"overflow\":0}}")).Code);
        var batch = "[" + string.Join(',', Enumerable.Repeat(Request("1"), 32)) + "]";
        Assert.Equal(32, Parse(batch).Requests.Count);
        var overflow = Parse("[" + string.Join(',', Enumerable.Repeat(Request("1"), 33)) + "]");
        Assert.False(overflow.IsBatch);
        Assert.Equal(-32600, Assert.Single(overflow.Requests).Fault!.Code);
    }

    [Fact]
    public void DepthAndUtf8AreStrict()
    {
        Parse(new string('[', 32) + "0" + new string(']', 32));
        Assert.Equal(-32700, Assert.Throws<RpcFault>(() => Parse(new string('[', 33) + "0" + new string(']', 33))).Code);
        Assert.Throws<DecoderFallbackException>(() => RpcProtocol.Parse(new byte[] { 0xc0, 0xaf }));
    }

    [Fact]
    public void MissingAndNullIdsHaveDifferentNotificationSemantics()
    {
        Assert.False(Parse("{\"jsonrpc\":\"2.0\",\"method\":\"settings.get\"}").Requests[0].HasId);
        Assert.True(Parse(Request("null")).Requests[0].HasId);
        Assert.Equal(JsonValueKind.Null, Parse(Request("null")).Requests[0].Id.ValueKind);
        var batch = Parse("[1,{\"jsonrpc\":\"2.0\",\"method\":\"settings.get\"}," + Request("\"x\"") + "]");
        Assert.True(batch.IsBatch);
        Assert.Equal(-32600, batch.Requests[0].Fault!.Code);
        Assert.False(batch.Requests[1].HasId);
        Assert.True(batch.Requests[2].HasId);
    }

    [Fact]
    public void SnapshotSerializerUsesStringsForRevisionAndOmitsNoRequiredNullResult()
    {
        var snapshot = new RpcSnapshot<RpcCancelResult>(1, Guid.Empty.ToString("D"), "machines",
            ulong.MaxValue.ToString(), new("notFound"));
        var json = JsonSerializer.Serialize(snapshot, RpcProtocol.Json);
        Assert.Contains("\"revision\":\"18446744073709551615\"", json);
        using var response = JsonDocument.Parse(RpcProtocol.Response(default, null));
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("result").ValueKind);
        Assert.False(response.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void OutboundSerializationEnforcesExactFourMiBLimitBeforeAllocatingOversizedResponse()
    {
        var overhead = RpcProtocol.Response(default, "").Length;
        Assert.Equal(RpcProtocol.OutboundBytes,
            RpcProtocol.Response(default, new string('a', RpcProtocol.OutboundBytes - overhead)).Length);
        Assert.Throws<RpcResultLimitException>(() =>
            RpcProtocol.Response(default, new string('a', RpcProtocol.OutboundBytes - overhead + 1)));
    }

    [Fact]
    public void DuplicatePropertiesInvalidateOnlyTheirBatchMemberAndEmptyBatchUsesSingleError()
    {
        var batch = Parse("[{\"jsonrpc\":\"2.0\",\"method\":\"settings.get\",\"id\":\"ok\"}," +
            "{\"jsonrpc\":\"2.0\",\"method\":\"settings.update\",\"params\":{\"a\":1,\"a\":2}}]");
        Assert.Null(batch.Requests[0].Fault);
        Assert.Equal(-32600, batch.Requests[1].Fault!.Code);
        var empty = Parse("[]");
        Assert.False(empty.IsBatch);
        Assert.Equal(-32600, Assert.Single(empty.Requests).Fault!.Code);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("https://LOCALHOST:65535")]
    [InlineData("http://127.0.0.1:3000")]
    [InlineData("https://[::1]:443")]
    public void AcceptsExactLocalOrigins(string origin) => Assert.True(RpcEndpointPolicy.IsOriginAllowed(origin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("file://localhost")]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost/path")]
    [InlineData("http://localhost?query")]
    [InlineData("http://localhost#fragment")]
    [InlineData("http://user@localhost")]
    [InlineData("http://localhost.evil.test")]
    [InlineData("http://127.1")]
    [InlineData("http://127.0.0.2")]
    [InlineData("http://[::ffff:127.0.0.1]")]
    [InlineData("http://localhost:65536")]
    [InlineData("http://localhost:1,http://localhost:2")]
    [InlineData(" http://localhost")]
    [InlineData("http://localhost.")]
    public void RejectsNonOriginAndNonexactLocalForms(string? origin) =>
        Assert.False(RpcEndpointPolicy.IsOriginAllowed(origin));

    [Fact]
    public void ArgumentsRejectDuplicatesUnknownAndPrivilegedPorts()
    {
        Assert.Equal(1024, RpcHostOptions.Parse(["--rpc-port", "1024"]).RpcPort);
        Assert.Equal(65535, RpcHostOptions.Parse(["--rpc-port", "65535"]).RpcPort);
        foreach (var invalid in new[] { "0", "1023", "65536", "-1", "+1024", " 1024", "1.0" })
            Assert.Throws<ArgumentException>(() => RpcHostOptions.Parse(["--rpc-port", invalid]));
        Assert.Throws<ArgumentException>(() => RpcHostOptions.Parse(["--rpc-port", "51821", "--rpc-port", "51822"]));
        Assert.Throws<ArgumentException>(() => RpcHostOptions.Parse(["--unknown", "1"]));
        Assert.Throws<ArgumentException>(() => RpcHostOptions.Parse(["--rpc-port"]));
        Assert.Throws<ArgumentException>(() => RpcHostOptions.Parse(["--data-directory", "relative"]));
    }

    private static RpcMessage Parse(string input) => RpcProtocol.Parse(Encoding.UTF8.GetBytes(input));
    private static string Request(string id) => "{\"jsonrpc\":\"2.0\",\"method\":\"settings.get\",\"id\":" + id + "}";

    [Fact]
    public void InstallerMetadataRequiresExactProductPathUserAndPortWithoutFirewallMutation()
    {
        const string directory = @"C:\synthetic\RpcHost";
        const string executable = directory + @"\AgentSignaler.RpcHost.exe";
        const string sid = "S-1-5-21-100-200-300-1001";
        Assert.Equal(51820, InstalledReceiverMetadata.Validate(InstalledReceiverMetadata.UpgradeCode, directory, 51820, sid, executable, sid));
        Assert.Null(InstalledReceiverMetadata.Validate(Guid.NewGuid().ToString("B"), directory, 51820, sid, executable, sid));
        Assert.Null(InstalledReceiverMetadata.Validate(InstalledReceiverMetadata.UpgradeCode, directory, 1023, sid, executable, sid));
        Assert.Null(InstalledReceiverMetadata.Validate(InstalledReceiverMetadata.UpgradeCode, directory, 51820, sid, @"C:\standalone\AgentSignaler.RpcHost.exe", sid));
        Assert.Null(InstalledReceiverMetadata.Validate(InstalledReceiverMetadata.UpgradeCode, directory, 51820, sid, executable, "S-1-5-21-100-200-300-1002"));
    }
}
