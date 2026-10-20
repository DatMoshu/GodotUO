// SPDX-License-Identifier: BSD-2-Clause
// A shard's pack offer, guo/shard-offer@1 (docs/data_formats.md section 12, ADR-0026 section 4a). No Godot types:
// the in-game question is StoreShardOfferPrompt; this file reads the offer, decides, and remembers the answers.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GUO.Store;

internal sealed class StoreOfferedPack
{
    public string Id { get; init; }
    public string Version { get; init; }
    public string Title { get; init; }
}

internal sealed class StoreShardOffer
{
    public const string Schema = "guo/shard-offer@1";
    /// <summary>The 0xBF subcommand GUO reserves for the offer ("GU").</summary>
    public const ushort Subcommand = 0x4755;
    public const byte Format = 1;
    public const int MaxJson = 16384, MaxFeature = 4096;
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "schema", "shard", "content", "packs", "features" };

    public string ShardName { get; init; }
    /// <summary>The shard's descriptor, its Url "offer:&lt;identity_hash&gt;"; null when it asks for no packs.</summary>
    public StoreShardContent Content { get; init; }
    public IReadOnlyList<StoreOfferedPack> Packs { get; init; }
    /// <summary>Each feature's own block, as raw JSON text, for the feature that owns the name.</summary>
    public IReadOnlyDictionary<string, string> Features { get; init; }

    /// <summary>A feature that reads its block from a shard's offer (water: ADR-0037). Called on the game thread.</summary>
    public static event Action<string, string> FeatureOffered;

    /// <summary>The bytes after the 0xBF subcommand: the format byte, then the UTF-8 JSON.</summary>
    public static StoreShardOffer Decode(ReadOnlySpan<byte> payload)
    {
        StorePack.Require(payload.Length >= 1 && payload[0] == Format, "unknown offer format");
        StorePack.Require(payload.Length - 1 <= MaxJson, "offer too large");
        return Parse(payload[1..].ToArray());
    }

    public static StoreShardOffer Parse(byte[] json)
    {
        StorePack.Require(json.Length <= MaxJson, "offer too large");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        StorePack.Require(root.ValueKind == JsonValueKind.Object, "an offer is a JSON object");
        StorePack.UniqueJson(root);
        foreach (var p in root.EnumerateObject()) StorePack.Require(Fields.Contains(p.Name), "unknown offer field " + p.Name);
        StorePack.Require(root.GetProperty("schema").GetString() == Schema, "not a guo/shard-offer@1 offer");
        string name = root.GetProperty("shard").GetProperty("name").GetString();
        StorePack.Require(Text(name), "invalid shard name");

        StoreShardContent content = null;
        if (root.TryGetProperty("content", out var c) && c.ValueKind != JsonValueKind.Null)
        {
            byte[] raw = Encoding.UTF8.GetBytes(c.GetRawText());
            content = StoreShardContent.Parse("offer:" + StoreShardContent.Parse("offer:", raw).IdentityHash, raw);
        }

        var packs = new List<StoreOfferedPack>();
        if (root.TryGetProperty("packs", out var list))
            foreach (var row in list.EnumerateArray())
            {
                StorePack.Require(packs.Count < 64, "too many packs in the offer");
                var pack = new StoreOfferedPack { Id = row.GetProperty("id").GetString(), Version = row.GetProperty("version").GetString(), Title = row.GetProperty("title").GetString() };
                StorePack.Id(pack.Id); StorePack.Version(pack.Version);
                StorePack.Require(Text(pack.Title), "invalid pack title");
                packs.Add(pack);
            }
        StorePack.Require(packs.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == packs.Count, "duplicate offered pack");
        if (content != null && packs.Count > 0)
            StorePack.Require(packs.Any(p => p.Id == content.Lock.Pack && p.Version == content.Lock.Version), "pack list omits the deployment root");

        var features = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("features", out var f))
            foreach (var feature in f.EnumerateObject())
            {
                StorePack.Require(features.Count < 32, "too many features in the offer");
                StorePack.Id(feature.Name);
                StorePack.Require(feature.Value.ValueKind == JsonValueKind.Object, "a feature block is a JSON object");
                string text = feature.Value.GetRawText();
                StorePack.Require(Encoding.UTF8.GetByteCount(text) <= MaxFeature, "feature block too large: " + feature.Name);
                features.Add(feature.Name, text);
            }

        return new StoreShardOffer { ShardName = name, Content = content, Packs = packs, Features = features };
    }

    private static bool Text(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl);

    /// <summary>Hands each feature block to whoever reads it; one feature's failure never stops the others.</summary>
    public void OfferFeatures(Action<string> log)
    {
        foreach (var pair in Features)
        {
            foreach (Action<string, string> handler in FeatureOffered?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { handler(pair.Key, pair.Value); }
                catch (Exception ex) { log("feature " + pair.Key + ": " + ex.Message); }
            }
        }
    }

    public enum Decision { NoPacks, Mounted, Declined, Ask }

    /// <summary>Whether to ask: never when the offer has no packs, the run already mounts them, or the player said no to these.</summary>
    public Decision Decide(string server, string mountedIdentity, StoreShardOfferAnswers answers)
    {
        if (Content == null) return Decision.NoPacks;
        if (mountedIdentity == Content.IdentityHash) return Decision.Mounted;
        return answers.Declined(server, Content.IdentityHash) ? Decision.Declined : Decision.Ask;
    }

    /// <summary>What the player is asked, before anything is fetched.</summary>
    public string Question()
    {
        string packs = Packs.Count > 0 ? string.Join(", ", Packs.Select(p => $"{p.Title} ({p.Id} {p.Version})")) : $"{Content.Lock.Pack} {Content.Lock.Version}";
        string from = string.Join(", ", Content.Catalogues.Select(c => new Uri(c.Url).Host + (c.Key == null ? " (unsigned)" : " (key " + Ed25519.Fingerprint(Ed25519.Decode(c.Key, 32)) + ")")));
        return $"{ShardName} asks for packs: {packs}. From {from}."
            + (Content.ScriptsAllowed ? "" : " Script packs are off there.")
            + " Install them and restart GUO? Nothing is downloaded if you say no.";
    }

    /// <summary>The server as the client saw it at login. Moves to GUO.Network.ServerIdentity when that lands.</summary>
    public static string ServerKey(string host, int port, string shardName) => $"{host?.Trim().ToLowerInvariant()}:{port}|{shardName}";
}

/// <summary>The player's answers to shard offers, &lt;store&gt;/.shard-offers.json.</summary>
internal sealed class StoreShardOfferAnswers
{
    public sealed class Row
    {
        [JsonPropertyName("server")] public string Server { get; set; }
        [JsonPropertyName("identity_hash")] public string IdentityHash { get; set; }
        [JsonPropertyName("answer")] public string Answer { get; set; }
        [JsonPropertyName("at")] public DateTime At { get; set; }
    }

    public const int MaxRows = 256;
    private readonly string _path;

    public StoreShardOfferAnswers(string storeRoot) => _path = Path.Combine(Path.GetFullPath(storeRoot), ".shard-offers.json");

    private List<Row> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 1024 * 1024) return new List<Row>();
            return (JsonSerializer.Deserialize<List<Row>>(File.ReadAllBytes(_path)) ?? new List<Row>())
                .Where(r => r != null).TakeLast(MaxRows).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new List<Row>(); }
    }

    public bool Declined(string server, string identityHash) =>
        Load().Any(r => r.Server == server && r.IdentityHash == identityHash && r.Answer == "declined");

    public void Decline(string server, string identityHash)
    {
        var rows = Load().Where(r => !(r.Server == server && r.IdentityHash == identityHash)).ToList();
        rows.Add(new Row { Server = server, IdentityHash = identityHash, Answer = "declined", At = DateTime.UtcNow });
        if (rows.Count > MaxRows) rows.RemoveRange(0, rows.Count - MaxRows);
        Directory.CreateDirectory(Path.GetDirectoryName(_path));
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(rows, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
