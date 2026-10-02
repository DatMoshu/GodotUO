// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GUO.Store;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class StoreComponent
{
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("target")] public string Target { get; set; }
    [JsonPropertyName("entry")] public string Entry { get; set; }
    [JsonPropertyName("references")] public List<string> References { get; set; }
    [JsonPropertyName("language")] public string Language { get; set; }
    [JsonPropertyName("runtime")] public string Runtime { get; set; }
    [JsonPropertyName("runtime_version")] public string RuntimeVersion { get; set; }
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; }
}

internal static class StoreContent
{
    public static readonly HashSet<string> Types = new("static land texmap tiledata gump animation wearable hue sound music effect light translation font map region multi decoration item crafting loot vendor creature spawner encounter quest dialogue presentation authoring script".Split(' '), StringComparer.Ordinal);

    public static void Validate(StoreManifest m)
    {
        StorePack.Require(m.Target is "client" or "server" or "combined", "Invalid content target");
        StorePack.Require(m.Dependencies != null && m.Dependencies.Count <= 128, "Invalid dependencies");
        foreach (var (id, version) in m.Dependencies)
        {
            StorePack.Id(id); StorePack.Version(version);
            StorePack.Require(id != m.Id, "Self dependency");
        }
        StorePack.Require(m.Components != null && m.Components.Count is > 0 and <= 512, "Invalid components");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in m.Components)
        {
            StorePack.Require(c != null, "Invalid component");
            StorePack.Id(c.Id);
            StorePack.Require(ids.Add(c.Id), "Duplicate component id");
            StorePack.Require(c.Type != null && Types.Contains(c.Type), "Unsupported content type");
            StorePack.Require(c.Target is "client" or "server" or "shared", "Invalid component target");
            StorePack.Require(m.Target == "combined" || c.Target == m.Target || c.Target == "shared", "Component target mismatch");
            targets.Add(c.Target);
            StorePack.Require(c.Entry != null && m.Files.ContainsKey(c.Entry), "Undeclared component entry");
            StorePack.Require(Path.GetExtension(c.Entry).ToLowerInvariant() is ".json" or ".png" or ".wav" or ".ogg" or ".razor", "Invalid content entry type");
            StorePack.Require(c.References == null || c.References.Count <= 512, "Invalid references");
            foreach (string reference in c.References ?? new())
            {
                StorePack.Require(reference != null && reference.Count(x => x == ':') == 1, "Invalid content reference");
                string[] parts = reference.Split(':');
                StorePack.Id(parts[0]); StorePack.Id(parts[1]);
                StorePack.Require(parts[0] == m.Id || m.Dependencies.ContainsKey(parts[0]), "Undeclared reference dependency");
            }
            if (c.Type == "script")
            {
                StorePack.Require(c.Language == "razor-ce" && c.Runtime == "guo-razor", "Unsupported script runtime");
                StorePack.Version(c.RuntimeVersion);
                StorePack.Require(c.Capabilities != null && c.Capabilities.Count <= 64, "Invalid script capabilities");
                foreach (string cap in c.Capabilities)
                    StorePack.Require(cap != null && cap.Length <= 100 && Regex.IsMatch(cap, @"\A[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)*\z"), "Invalid script capability");
                StorePack.Require(c.Capabilities.Distinct(StringComparer.Ordinal).Count() == c.Capabilities.Count, "Duplicate script capability");
            }
        }
        StorePack.Require(m.Target != "combined" || targets.Contains("shared") || targets.Contains("client") && targets.Contains("server"), "Combined pack missing target");
        foreach (var c in m.Components)
            foreach (string reference in c.References ?? new())
            {
                string[] parts = reference.Split(':');
                StorePack.Require(parts[0] != m.Id || ids.Contains(parts[1]), "Missing local component reference");
            }
    }

    public static bool Equivalent(StoreManifest a, StoreManifest b) =>
        a.Target == b.Target && JsonSerializer.Serialize(a.Components) == JsonSerializer.Serialize(b.Components)
        && (a.Dependencies == null ? b.Dependencies == null : b.Dependencies != null
            && a.Dependencies.Count == b.Dependencies.Count && a.Dependencies.All(p => b.Dependencies.TryGetValue(p.Key, out string v) && v == p.Value));
}
