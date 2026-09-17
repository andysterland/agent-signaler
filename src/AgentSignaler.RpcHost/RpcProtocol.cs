using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSignaler.Contracts.Rpc.V1;

namespace AgentSignaler.RpcHost;

internal static class RpcProtocol
{
    public const int InboundBytes = 1024 * 1024;
    public const int OutboundBytes = 4 * 1024 * 1024;
    public const int BatchEntries = 32;
    public const int QueueMessages = 64;
    public const int QueueBytes = 16 * 1024 * 1024;
    public const int ApplicationSlots = 32;
    public const int ControlSlots = 4;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static RpcLimitProfile Limits { get; } = new(InboundBytes, OutboundBytes, 32, 128,
        BatchEntries, 128, 32768, 16384, 100, 250, ApplicationSlots, ControlSlots, QueueMessages, QueueBytes);

    public static RpcMessage Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > InboundBytes) throw new RpcFault(1011);
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                ValidateStructure(root);
                return new(false, [ParseEntry(root)]);
            }
            if (root.GetArrayLength() is 0 or > BatchEntries)
                return new(false, [RpcRequest.Invalid()]);
            return new(true, root.EnumerateArray().Select(entry =>
            {
                try { ValidateStructure(entry); return ParseEntry(entry); }
                catch (RpcFault) { return RpcRequest.Invalid(); }
            }).ToArray());
        }
        catch (JsonException) { throw new RpcFault(-32700); }
        catch (InvalidOperationException) { throw new RpcFault(-32700); }
    }

    private static void ValidateStructure(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Length > 32768 || !names.Add(property.Name) || names.Count > 128)
                    throw new RpcFault(-32600);
                ValidateStructure(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) ValidateStructure(child);
        }
        else if (element.ValueKind == JsonValueKind.String && element.GetString()!.Length > 32768)
            throw new RpcFault(-32600);
    }

    private static RpcRequest ParseEntry(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return RpcRequest.Invalid();
        var hasId = root.TryGetProperty("id", out var id);
        if (hasId && !RpcId.TryCreate(id, out _)) return RpcRequest.Invalid();
        if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != "2.0" || !root.TryGetProperty("method", out var method) ||
            method.ValueKind != JsonValueKind.String ||
            root.EnumerateObject().Any(p => p.Name is not ("id" or "jsonrpc" or "method" or "params")))
            return RpcRequest.Invalid(hasId ? id.Clone() : default);
        var parameters = root.TryGetProperty("params", out var supplied) ? supplied.Clone() : default;
        return new(hasId, hasId ? id.Clone() : default, method.GetString()!, parameters, null);
    }

    public static byte[] Response(JsonElement id, object? result, RpcFault? fault = null, int budget = OutboundBytes)
    {
        using var stream = new BoundedResponseStream(budget);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
            else id.WriteTo(writer);
            if (fault is null)
            {
                writer.WritePropertyName("result");
                JsonSerializer.Serialize(writer, result, Json);
            }
            else
            {
                writer.WriteStartObject("error");
                writer.WriteNumber("code", fault.Code);
                writer.WriteString("message", fault.SafeMessage);
                writer.WritePropertyName("data");
                JsonSerializer.Serialize(writer, fault.Data, Json);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static byte[] Notification(string method, object state) =>
        JsonSerializer.SerializeToUtf8Bytes(new NotificationEnvelope("2.0", method, state), Json);

    private sealed record NotificationEnvelope(string Jsonrpc, string Method, object Params);

    private sealed class BoundedResponseStream(int budget) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > budget) throw new RpcResultLimitException();
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > budget) throw new RpcResultLimitException();
            base.Write(buffer, offset, count);
        }
        public override void WriteByte(byte value)
        {
            if (Length >= budget) throw new RpcResultLimitException();
            base.WriteByte(value);
        }
    }
}

internal sealed class RpcResultLimitException : Exception;
internal sealed record RpcMessage(bool IsBatch, IReadOnlyList<RpcRequest> Requests);
internal sealed record RpcRequest(bool HasId, JsonElement Id, string Method, JsonElement Parameters, RpcFault? Fault)
{
    public static RpcRequest Invalid(JsonElement id = default) => new(true, id, "", default, new(-32600));
}

internal readonly record struct RpcId(string Key)
{
    public static bool TryCreate(JsonElement id, out RpcId result)
    {
        result = default;
        if (id.ValueKind == JsonValueKind.Null) { result = new("null"); return true; }
        if (id.ValueKind == JsonValueKind.String && id.GetString()!.Length <= 128)
        {
            result = new("s:" + id.GetString());
            return true;
        }
        if (id.ValueKind != JsonValueKind.Number) return false;
        var raw = id.GetRawText();
        if (raw.Length > 128) return false;
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var exponent = exponentIndex < 0 ? BigInteger.Zero :
            BigInteger.Parse(raw[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var negative = mantissa.StartsWith('-');
        if (negative) mantissa = mantissa[1..];
        var dot = mantissa.IndexOf('.');
        if (dot >= 0) { exponent -= mantissa.Length - dot - 1; mantissa = mantissa.Remove(dot, 1); }
        mantissa = mantissa.TrimStart('0');
        if (mantissa.Length == 0) { result = new("n:0"); return true; }
        var trimmed = mantissa.TrimEnd('0');
        exponent += mantissa.Length - trimmed.Length;
        result = new("n:" + (negative ? "-" : "") + trimmed + "e" + exponent.ToString(CultureInfo.InvariantCulture));
        return true;
    }
}

internal sealed class RpcFault(int code, RpcErrorData? data = null) : Exception
{
    public int Code { get; } = code;
    public new RpcErrorData Data { get; } = data ?? new();
    public string SafeMessage => Code switch
    {
        -32700 => "Parse error", -32600 => "Invalid request", -32601 => "Method not found",
        -32602 => "Invalid params", -32603 => "Internal error", 1001 => "Validation failed",
        1002 => "Not found", 1003 => "Busy", 1004 => "Stale revision", 1005 => "Unavailable",
        1006 => "Cancelled", 1007 => "Timed out", 1008 => "Persistence failed",
        1009 => "Confirmation required", 1010 => "Prerequisite failed", 1011 => "Resource limit",
        _ => "Internal error"
    };
}
