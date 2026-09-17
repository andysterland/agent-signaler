using System.Globalization;
using System.Text.Json;

namespace AgentSignaler.RpcHost;

internal readonly struct RpcParameters
{
    private readonly JsonElement value;
    public RpcParameters(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) value = default;
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            throw new RpcFault(-32602);
        if (value.ValueKind == JsonValueKind.Object &&
            value.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new RpcFault(-32602);
        this.value = value;
    }
    public bool Has(string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out _);
    public JsonElement Get(string name) => Has(name) ? value.GetProperty(name) : default;
    public string? String(string name, bool required = false)
    {
        var field = Get(name);
        if (!required && field.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (field.ValueKind != JsonValueKind.String) throw new RpcFault(-32602, new(Field: name));
        return field.GetString();
    }
    public int Int(string name, int fallback)
    {
        var field = Get(name);
        if (field.ValueKind == JsonValueKind.Undefined) return fallback;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out var number))
            throw new RpcFault(-32602, new(Field: name));
        return number;
    }
    public bool Bool(string name, bool fallback = false)
    {
        var field = Get(name);
        if (field.ValueKind == JsonValueKind.Undefined) return fallback;
        if (field.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new RpcFault(-32602, new(Field: name));
        return field.GetBoolean();
    }
    public Guid Guid(string name)
    {
        var text = String(name, required: true);
        if (!System.Guid.TryParseExact(text, "D", out var result) || result == System.Guid.Empty)
            throw new RpcFault(1001, new(Field: name));
        return result;
    }
    public Guid? OptionalGuid(string name) => String(name) is null ? null : Guid(name);
    public long? Revision(string name = "expectedRevision", bool required = false)
    {
        var text = String(name, required);
        if (text is null && !required) return null;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 0)
            throw new RpcFault(1001, new(Field: name));
        return result;
    }
}
