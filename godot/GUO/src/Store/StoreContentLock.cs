// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GUO.Store;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class StoreContentBinding
{
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("id")] public int Id { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class StoreContentLock
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = "guo/content-lock@1";
    [JsonPropertyName("pack")] public string Pack { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; }
    [JsonPropertyName("identity_hash")] public string IdentityHash { get; set; }
    [JsonPropertyName("bindings")] public Dictionary<string, StoreContentBinding> Bindings { get; set; } = new();

    public static StoreContentLock Read(string path)
    {
        StoreClient.NoLinks(path);
        StorePack.Require(new FileInfo(path).Length <= StorePack.MaxManifest, "Content lock too large");
        byte[] bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        StorePack.UniqueJson(document.RootElement);
        var value = JsonSerializer.Deserialize<StoreContentLock>(bytes);
        StorePack.Require(value != null && value.Schema == "guo/content-lock@1", "Invalid content lock");
        StorePack.Id(value.Pack); StorePack.Version(value.Version); StorePack.Digest(value.IdentityHash);
        StorePack.Require(value.Bindings != null && value.Bindings.Count <= 65536, "Invalid bindings");
        return value;
    }

    public StoreVerifiedContent Verify(StoreClient client)
    {
        var snapshot = client.VerifyContent(Pack, Version);
        return VerifySnapshot(snapshot);
    }

    internal StoreVerifiedContent VerifySnapshot(StoreVerifiedContent snapshot)
    {
        StorePack.Require(snapshot.IdentityHash == IdentityHash, "Content lock no longer matches installed content");
        var numeric = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (identity, binding) in Bindings)
        {
            string[] parts = identity.Split(':');
            StorePack.Require(parts.Length == 2 && snapshot.Packs.ContainsKey(parts[0]), "Binding references missing pack");
            var component = snapshot.Packs[parts[0]].Manifest.Components?.SingleOrDefault(c => c.Id == parts[1]);
            StorePack.Require(component != null && binding != null && binding.Type == component.Type, "Binding type mismatch");
            int limit = binding.Type switch { "land" or "texmap" => 0x3fff, "hue" => 0x3fff, "animation" => 2047, "multi" => 0x21ff, "sound" or "music" => 0xfffe, "translation" => int.MaxValue, _ => 0xffff };
            StorePack.Require(binding.Id >= (binding.Type == "hue" ? 1 : 0) && binding.Id <= limit, "Numeric binding outside supported range");
            StorePack.Require(numeric.Add(binding.Type + ":" + binding.Id), "Numeric binding collision");
        }
        return snapshot;
    }
}
