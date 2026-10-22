using GUO.Store;
using System.Text.Json;

internal static class WaterPolicyTests
{
    public static void Run()
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception("water policy: " + message); }
        StoreWaterPolicy Parse(string json) { using var doc = JsonDocument.Parse(json); return StoreWaterPolicy.Parse(doc.RootElement); }
        bool Refused(string json) { try { Parse(json); return false; } catch (Exception e) when (e is InvalidDataException or JsonException) { return true; } }
        var p = Parse("{\"default\":\"uo\",\"lock\":true,\"allowed\":[\"uo\",\"classic\"]}");
        Check(p.Select("classic") == "classic" && p.Select("unknown") == "uo" && p.Select("") == "uo", "locked selection");
        var onlyDefault = Parse("{\"default\":\"uo\",\"lock\":true}");
        Check(!onlyDefault.Allows("classic") && onlyDefault.Allows("uo"), "empty allowlist restricts to default");
        var unlocked = Parse("{\"default\":\"uo\"}");
        Check(unlocked.Select("my-pack:water") == "my-pack:water", "override permitted");
        string key = p.ConsentKey("https://shard.example/content");
        Check(key != p.ConsentKey("https://other.example/content"), "consent crosses shards");
        Check(key != unlocked.ConsentKey("https://shard.example/content"), "changed restrictions reuse consent");
        var reordered = Parse("{\"default\":\"UO\",\"lock\":true,\"allowed\":[\"Classic\",\"UO\"]}");
        Check(key == reordered.ConsentKey("https://shard.example/content"), "equivalent policies invalidate consent");
        Check(Refused("null") && Refused("[]") && Refused("{\"default\":null}") && Refused("{\"default\":\"bad id\"}"), "malformed default accepted");
        Check(Refused("{\"default\":\"uo\",\"lock\":true,\"allowed\":[\"classic\"]}")
            && Refused("{\"default\":\"uo\",\"lock\":false,\"allowed\":[\"classic\"]}")
            && Refused("{\"default\":\"unknown\"}") && Refused("{\"default\":\":water\"}")
            && Refused("{\"default\":\"pack:water:extra\"}")
            && Refused("{\"allowed\":null}") && Refused("{\"allowed\":[\"uo\",\"UO\"]}"), "invalid restrictions accepted");
        Check(Refused(JsonSerializer.Serialize(new { allowed = Enumerable.Range(0,65).Select(i => "pack:style"+i).ToArray() })), "unbounded allowlist accepted");
        Console.WriteLine("PASS: water policy parsing, override/lock, bounded styles, canonical shard-scoped consent");
    }
}
