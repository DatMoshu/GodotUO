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
    [JsonPropertyName("scale")] public double? Scale { get; set; }
    [JsonPropertyName("native_w")] public int? NativeWidth { get; set; }
    [JsonPropertyName("native_h")] public int? NativeHeight { get; set; }
    [JsonPropertyName("asset")] public string Asset { get; set; }
    [JsonPropertyName("graphic")] public int? Graphic { get; set; }
    [JsonPropertyName("body")] public int? Body { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("wav")] public List<string> Wav { get; set; }
}

internal static class StoreContent
{
    public static readonly HashSet<string> Types = new("static land texmap tiledata gump animation wearable hue sound music effect light translation font map region multi decoration item crafting loot vendor creature spawner encounter quest dialogue presentation authoring script hd-art splat voice water".Split(' '), StringComparer.Ordinal);

    private static void ValidateMedia(StoreComponent c, StoreManifest m)
    {
        bool hd = c.Type == "hd-art", splat = c.Type == "splat", voice = c.Type == "voice";
        StorePack.Require(hd || c.Scale == null && c.NativeWidth == null && c.NativeHeight == null && c.Asset == null,
            "Media field on wrong component type");
        StorePack.Require(hd || splat || c.Graphic == null, "Media field on wrong component type");
        StorePack.Require(voice || c.Body == null && c.Name == null && c.Wav == null, "Media field on wrong component type");
        if (!hd && !splat && !voice) return;
        StorePack.Require(c.Target == "client", "Media component must target client");
        string extension = Path.GetExtension(c.Entry).ToLowerInvariant();
        if (hd || splat) StorePack.Require(c.Graphic is >= 0 and <= 65535, "Invalid graphic binding");
        if (hd)
        {
            StorePack.Require(extension == ".png", "hd-art entry must be PNG");
            StorePack.Require(c.Asset is "static" or "land", "Invalid hd-art asset namespace");
            StorePack.Require(c.Scale is >= 1 and <= 16, "Invalid hd-art scale");
            StorePack.Require(c.NativeWidth is >= 1 and <= 4096 && c.NativeHeight is >= 1 and <= 4096, "Invalid hd-art native dimensions");
            StorePack.Require(c.Asset != "land" || c.NativeWidth == c.NativeHeight, "hd-art land must be square");
        }
        else if (splat) StorePack.Require(extension == ".ply", "Splat entry must be PLY");
        else
        {
            StorePack.Require(extension == ".json", "Voice entry must be JSON");
            StorePack.Require(c.Body.HasValue != (c.Name != null), "Voice needs one NPC body or name binding");
            if (c.Body.HasValue) StorePack.Require(c.Body is >= 0 and <= 65535, "Invalid voice body binding");
            else StorePack.Require(!string.IsNullOrWhiteSpace(c.Name) && c.Name.Length <= 100
                && !c.Name.Any(ch => ch < 32 || ch == 127), "Invalid voice name binding");
            StorePack.Require(c.Wav != null && c.Wav.Count is > 0 and <= 512, "Voice needs WAV entries");
            foreach (string path in c.Wav)
                StorePack.Require(path != null && m.Files.ContainsKey(path) && Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase), "Undeclared voice WAV entry");
            StorePack.Require(c.Wav.Distinct(StringComparer.Ordinal).Count() == c.Wav.Count, "Duplicate voice WAV entry");
        }
    }

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
            StorePack.Require(Path.GetExtension(c.Entry).ToLowerInvariant() is ".json" or ".png" or ".wav" or ".ogg" or ".razor" or ".ply", "Invalid content entry type");
            ValidateMedia(c, m);
            if (c.Type == "water")
            {
                // A style (ADR-0037 decision 6): data for a built-in model, read by the client only.
                StorePack.Require(c.Target == "client", "Water component must target client");
                StorePack.Require(Path.GetExtension(c.Entry).Equals(".json", StringComparison.OrdinalIgnoreCase), "Water entry must be JSON");
            }
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
        var ply = m.Components.Where(c => c.Type == "splat").Select(c => c.Entry).ToHashSet(StringComparer.Ordinal);
        StorePack.Require(m.Files.Keys.All(path => !Path.GetExtension(path).Equals(".ply", StringComparison.OrdinalIgnoreCase) || ply.Contains(path)), "PLY payload requires splat component");
        StorePack.Require(m.Components.All(c => !Path.GetExtension(c.Entry).Equals(".ply", StringComparison.OrdinalIgnoreCase) || c.Type == "splat"), "PLY entry requires splat component");
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
