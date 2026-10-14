// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GUO.IO.Audio;

namespace GUO.Store;

/// <summary>Verified recorded audio only. No synthesis, service calls or runtime disk lookup.</summary>
internal sealed class StoreAudioContent
{
    public readonly List<ZoneAudio.Zone> Music = new(), Sound = new();
    private readonly Dictionary<(string Name, int Body, string Text), byte[]> _voices = new();
    private readonly List<(ZoneAudio.Zone Zone, string[] References, bool Music, int Priority)> _regions = new();

    public void AddRegion(string identity, StoreComponent component, byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        var row = doc.RootElement;
        StorePack.UniqueJson(row);
        StoreRegionDefinition.Validate(row);
        foreach (string layer in new[] { "music", "sound" })
        {
            if (!row.TryGetProperty(layer, out var tracks)) continue;
            StorePack.Require(tracks.ValueKind == JsonValueKind.Array && tracks.GetArrayLength() is > 0 and <= 32, "Invalid region audio playlist");
            var references = new string[tracks.GetArrayLength()];
            for (int i = 0; i < references.Length; i++)
            {
                references[i] = tracks[i].GetString();
                StorePack.Require(references[i] != null && component.References?.Contains(references[i]) == true, "Undeclared region audio reference");
            }
            foreach (var area in row.GetProperty("areas").EnumerateArray())
            {
                int x = area.GetProperty("x").GetInt32(), y = area.GetProperty("y").GetInt32(), z = area.GetProperty("z").GetInt32();
                var zone = new ZoneAudio.Zone { Name = row.GetProperty("name").GetString(), Facet = row.GetProperty("facet").GetInt32(),
                    Key = identity + ":" + _regions.Count,
                    X0 = x, Y0 = y, X1 = x + area.GetProperty("width").GetInt32() - 1, Y1 = y + area.GetProperty("height").GetInt32() - 1,
                    Z0 = z, Z1 = z + area.GetProperty("depth").GetInt32() - 1, PcmTracks = new() };
                _regions.Add((zone, references, layer == "music", row.GetProperty("priority").GetInt32()));
                StorePack.Require(_regions.Count <= 4096, "Region audio area budget exceeded");
            }
        }
    }

    public void ResolveRegions(StoreRuntimeContent content, StoreContentLock contentLock)
    {
        foreach (var row in _regions.OrderByDescending(r => r.Priority))
        {
            foreach (string reference in row.References)
            {
                StorePack.Require(contentLock.Bindings.TryGetValue(reference, out var binding) && binding.Type == (row.Music ? "music" : "sound"), "Region audio binding type mismatch");
                byte[] pcm;
                bool found = row.Music ? content.TryMusic(binding.Id, out pcm) : content.TrySound(binding.Id, out pcm);
                StorePack.Require(found, "Region audio payload not mounted");
                row.Zone.Tracks.Add(reference);
                row.Zone.PcmTracks.Add(pcm);
            }
            (row.Music ? Music : Sound).Add(row.Zone);
        }
        _regions.Clear();
    }

    public void AddVoice(StoreComponent component, byte[] data, Func<string, byte[]> readWave)
    {
        using var doc = JsonDocument.Parse(data);
        var row = doc.RootElement;
        StorePack.UniqueJson(row);
        StorePack.Require(row.GetProperty("schema").GetString() == "guo/voice@1", "Unsupported voice payload schema");
        var clips = row.GetProperty("clips");
        StorePack.Require(clips.ValueKind == JsonValueKind.Object, "Invalid voice clips");
        var waves = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string entry in component.Wav) waves.Add(entry, readWave(entry));
        int count = 0;
        foreach (var clip in clips.EnumerateObject())
        {
            StorePack.Require(++count <= 512 && !string.IsNullOrWhiteSpace(clip.Name) && clip.Name.Length <= 512, "Invalid voice clip text");
            string entry = clip.Value.GetString();
            StorePack.Require(entry != null && waves.TryGetValue(entry, out var unused), "Undeclared voice WAV");
            StorePack.Require(_voices.TryAdd((component.Name?.ToUpperInvariant() ?? "", component.Body ?? -1, clip.Name.Trim()), waves[entry]), "Conflicting voice clip binding");
        }
        StorePack.Require(count > 0 && _voices.Count <= 4096, "Voice clip budget exceeded");
    }

    public bool TryVoice(string name, ushort body, string text, out byte[] pcm) =>
        _voices.TryGetValue(((name ?? "").ToUpperInvariant(), -1, (text ?? "").Trim()), out pcm)
        || _voices.TryGetValue(("", body, (text ?? "").Trim()), out pcm);
}
