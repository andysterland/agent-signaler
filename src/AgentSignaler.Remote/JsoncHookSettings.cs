using System.Text;
using System.Text.Json;

namespace AgentSignaler.Remote;

/// <summary>Edits only the owned property tokens; all other UTF-8 bytes remain unchanged.</summary>
public static class JsoncHookSettings
{
    public const string Setting = "chat.hookFilesLocations";
    private sealed record Property(string Name, int Start, int ValueStart, int End, JsonTokenType Type);
    private sealed record ObjectSpan(int Open, int Close, List<Property> Properties);

    public static byte[] AddLocation(byte[] original, string location) =>
        HasOwnedValue(original, location) ? original : Add(original, location);
    public static byte[] RemoveLocation(byte[] original, string location) => Remove(original, location);

    public static IReadOnlyList<string> EnabledLocations(byte[] original)
    {
        var setting = Parse(original).Properties.SingleOrDefault(p => p.Name == Setting);
        if (setting is null) return [];
        if (setting.Type != JsonTokenType.StartObject)
            throw new InvalidDataException("chat.hookFilesLocations is not an object.");
        return Parse(original, setting.ValueStart).Properties
            .Where(p => p.Type == JsonTokenType.True).Select(p => p.Name).ToArray();
    }

    public static byte[] Add(byte[]? original, string location)
    {
        var bytes = original ?? "{}"u8.ToArray();
        var root = Parse(bytes);
        var setting = root.Properties.SingleOrDefault(p => p.Name == Setting);
        var quoted = JsonSerializer.Serialize(location);
        if (setting is null)
            return Insert(bytes, root, $"{JsonSerializer.Serialize(Setting)}: {{ {quoted}: true }}");
        if (setting.Type != JsonTokenType.StartObject)
            throw new InvalidDataException("chat.hookFilesLocations is not an object; settings were not changed.");
        var locations = Parse(bytes, setting.ValueStart);
        if (locations.Properties.Any(p => SameLocation(p.Name, location)))
            throw new InvalidDataException("Hook location is already configured without matching ownership; preserve it and resolve manually.");
        return Insert(bytes, locations, $"{quoted}: true");
    }

    public static bool HasOwnedValue(byte[] bytes, string location)
    {
        var setting = Parse(bytes).Properties.SingleOrDefault(p => p.Name == Setting);
        if (setting is null || setting.Type != JsonTokenType.StartObject) return false;
        var property = Parse(bytes, setting.ValueStart).Properties.SingleOrDefault(p => SameLocation(p.Name, location));
        return property?.Type == JsonTokenType.True;
    }

    public static byte[] Remove(byte[] bytes, string location)
    {
        var setting = Parse(bytes).Properties.SingleOrDefault(p => p.Name == Setting);
        if (setting is null || setting.Type != JsonTokenType.StartObject) return bytes;
        var locations = Parse(bytes, setting.ValueStart);
        var index = locations.Properties.FindIndex(p => SameLocation(p.Name, location));
        if (index < 0 || locations.Properties[index].Type != JsonTokenType.True) return bytes;
        var property = locations.Properties[index];
        var comma = FindComma(bytes, property.End, index + 1 < locations.Properties.Count
            ? locations.Properties[index + 1].Start : locations.Close);
        if (comma < 0 && index > 0)
            comma = FindComma(bytes, locations.Properties[index - 1].End, property.Start);
        // Strip only our property and one delimiter, not adjacent whitespace/comments.
        var result = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
            if ((i < property.Start || i >= property.End) && i != comma) result.Add(bytes[i]);
        var output = result.ToArray();
        _ = Parse(output);
        return output;
    }

    private static bool SameLocation(string a, string b) =>
        string.Equals(a.Replace('/', '\\'), b.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private static byte[] Insert(byte[] bytes, ObjectSpan obj, string property)
    {
        var last = obj.Properties.LastOrDefault();
        var hasComma = last is not null && FindComma(bytes, last.End, obj.Close) >= 0;
        var prefix = last is not null && !hasComma ? "," : "";
        var insertion = Encoding.UTF8.GetBytes(prefix + "\n  " + property + ",\n");
        // Insert at the last value rather than after a // comment.
        var position = last is not null && !hasComma ? last.End : obj.Close;
        var output = new byte[bytes.Length + insertion.Length];
        bytes.AsSpan(0, position).CopyTo(output);
        insertion.CopyTo(output, position);
        bytes.AsSpan(position).CopyTo(output.AsSpan(position + insertion.Length));
        _ = Parse(output);
        return output;
    }

    private static int FindComma(byte[] bytes, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (bytes[i] == '/' && i + 1 < end && bytes[i + 1] == '/')
            {
                while (i < end && bytes[i] != '\n') i++;
            }
            else if (bytes[i] == '/' && i + 1 < end && bytes[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < end && !(bytes[i] == '*' && bytes[i + 1] == '/')) i++;
                i++;
            }
            else if (bytes[i] == ',') return i;
        }
        return -1;
    }

    private static ObjectSpan Parse(byte[] bytes, int offset = 0)
    {
        if (bytes.Length > 1048576) throw new InvalidDataException("Settings file is too large.");
        var rootDocument = offset == 0;
        if (offset == 0 && bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) offset = 3;
        var reader = new Utf8JsonReader(bytes.AsSpan(offset),
            new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("Settings must contain a JSONC object.");
        var open = checked((int)reader.TokenStartIndex) + offset;
        var properties = new List<Property>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                var close = checked((int)reader.TokenStartIndex) + offset;
                if (rootDocument && reader.Read())
                    throw new InvalidDataException("Unexpected settings content.");
                return new(open, close, properties);
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new InvalidDataException("Invalid settings property.");
            var name = reader.GetString()!;
            if (!names.Add(name)) throw new InvalidDataException("Duplicate JSONC properties require manual resolution.");
            var start = checked((int)reader.TokenStartIndex) + offset;
            if (!reader.Read()) throw new InvalidDataException("Missing settings value.");
            var valueStart = checked((int)reader.TokenStartIndex) + offset;
            var type = reader.TokenType;
            reader.Skip();
            properties.Add(new(name, start, valueStart, checked((int)reader.BytesConsumed) + offset, type));
        }
        throw new InvalidDataException("Incomplete settings object.");
    }
}
