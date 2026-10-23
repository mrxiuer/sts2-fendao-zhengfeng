using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FendaoZhengfeng.Core;

// Only a transport representation. Disk archives always contain complete Match JSON.
public sealed record StateChange(string[] Path, bool Remove, bool Append, JsonNode? Value);
public sealed record StateDelta(string MatchId, string Version, long BaseRevision, long Revision,
    string BaseHash, string ResultHash, List<StateChange> Changes)
{
    public static string Normalize(string json) => Normalize(JsonNode.Parse(json));
    public static string Normalize(JsonNode? node)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) Write(writer, node);
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            writer.WriteStartObject();
            foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            { writer.WritePropertyName(pair.Key); Write(writer, pair.Value); }
            writer.WriteEndObject();
        }
        else if (node is JsonArray array)
        { writer.WriteStartArray(); foreach (var item in array) Write(writer, item); writer.WriteEndArray(); }
        else if (node == null) writer.WriteNullValue();
        else node.WriteTo(writer);
    }
    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    public static StateDelta Create(string before, string after)
    {
        return Create(JsonNode.Parse(before)!.AsObject(), JsonNode.Parse(after)!.AsObject(), before, after);
    }
    public static StateDelta Create(JsonObject old, JsonObject next, string before, string after)
    {
        string id = next["Id"]!.GetValue<string>(), version = next["Version"]!.GetValue<string>();
        long revision = next["Revision"]!.GetValue<long>(), previous = old["Revision"]!.GetValue<long>();
        if (old["Id"]!.GetValue<string>() != id || old["Version"]!.GetValue<string>() != version || revision <= previous)
            throw new InvalidDataException("增量状态的比赛或版本不连续。");
        var changes = new List<StateChange>(); Diff(old, next, [], changes);
        return new(id, version, previous, revision, Hash(before), Hash(after), changes);
    }
    private static bool Same(JsonNode? a, JsonNode? b)
    {
        // JsonNode.DeepEquals treats 1.0 and 1.00 as equal. The transport hashes
        // exact normalized bytes, so numeric spelling must also be preserved.
        if (a is JsonObject x && b is JsonObject y)
            return x.Count == y.Count && x.All(pair => y.TryGetPropertyValue(pair.Key,out var value) && Same(pair.Value,value));
        if (a is JsonArray xs && b is JsonArray ys)
            return xs.Count == ys.Count && xs.Select((item,index)=>Same(item,ys[index])).All(equal=>equal);
        if (a is JsonValue av && b is JsonValue bv
            && av.TryGetValue<JsonElement>(out var ae) && bv.TryGetValue<JsonElement>(out var be)
            && ae.ValueKind == JsonValueKind.Number && be.ValueKind == JsonValueKind.Number)
            return ae.GetRawText() == be.GetRawText();
        return JsonNode.DeepEquals(a,b);
    }
    private static void Diff(JsonNode? old, JsonNode? next, string[] path, List<StateChange> changes)
    {
        if (Same(old, next)) return;
        if (old is JsonObject a && next is JsonObject b)
        {
            foreach (var pair in a) if (!b.ContainsKey(pair.Key)) changes.Add(new([..path, pair.Key], true, false, null));
            foreach (var pair in b)
                if (a.TryGetPropertyValue(pair.Key, out var previous)) Diff(previous, pair.Value, [..path, pair.Key], changes);
                else changes.Add(new([..path, pair.Key], false, false, pair.Value?.DeepClone()));
            return;
        }
        if (old is JsonArray oldArray && next is JsonArray newArray && newArray.Count > oldArray.Count
            && oldArray.Select((item,index) => Same(item,newArray[index])).All(equal => equal))
        {
            changes.Add(new(path, false, true, new JsonArray(newArray.Skip(oldArray.Count).Select(n => n?.DeepClone()).ToArray())));
            return;
        }
        changes.Add(new(path, false, false, next?.DeepClone()));
    }
    public bool TryApply(string before, out string after)
    {
        after = before;
        if (Hash(before) != BaseHash || Changes.Count > 20000) return false;
        try
        {
            var root = JsonNode.Parse(before)!.AsObject();
            if (root["Id"]!.GetValue<string>() != MatchId || root["Version"]!.GetValue<string>() != Version
                || root["Revision"]!.GetValue<long>() != BaseRevision || Revision <= BaseRevision) return false;
            foreach (var change in Changes)
            {
                if (change.Path.Length is < 1 or > 60 || change.Remove && change.Append) return false;
                JsonObject parent = root;
                foreach (string segment in change.Path[..^1]) parent = parent[segment]!.AsObject();
                string key = change.Path[^1];
                if (change.Remove) { if (!parent.Remove(key)) return false; }
                else if (change.Append)
                { foreach (var item in change.Value!.AsArray()) parent[key]!.AsArray().Add(item?.DeepClone()); }
                else parent[key] = change.Value?.DeepClone();
            }
            if (root["Id"]!.GetValue<string>() != MatchId || root["Version"]!.GetValue<string>() != Version
                || root["Revision"]!.GetValue<long>() != Revision) return false;
            string candidate = Normalize(root);
            if (Hash(candidate) != ResultHash) return false;
            after = candidate; return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or ArgumentException)
        { return false; }
    }
}
