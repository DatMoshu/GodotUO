// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GUO.Store;

internal static class StoreServerExport
{
    public static void Export(StoreClient store, string lockPath, string output)
    {
        var contentLock = StoreContentLock.Read(lockPath);
        var closure = contentLock.Verify(store);
        var items = new List<object>();
        var maps = new List<object>();
        var tiles = new List<object>();
        var regions = new List<object>();
        foreach (var pack in closure.Packs.Values)
            foreach (var component in pack.Manifest.Components ?? new())
            {
                if (component.Target == "client") continue;
                if (component.Type == "region")
                {
                    using var regionDoc = JsonDocument.Parse(pack.ReadPayload(component.Entry));
                    var region = regionDoc.RootElement;
                    StoreRegionDefinition.Validate(region);
                    int music = -1;
                    if (region.TryGetProperty("music", out var musicValue))
                    {
                        string reference = musicValue.GetString();
                        StorePack.Require(component.References != null && component.References.Contains(reference)
                            && contentLock.Bindings.TryGetValue(reference, out var ignored), "Undeclared region music reference");
                        var musicBinding = contentLock.Bindings[reference];
                        StorePack.Require(musicBinding.Type == "music", "Region music reference type mismatch");
                        music = musicBinding.Id;
                    }
                    regions.Add(new { identity = pack.Id + ":" + component.Id, music_id = music, content = region.Clone() });
                    continue;
                }
                if (component.Type == "tiledata")
                {
                    StorePack.Require(contentLock.Bindings.TryGetValue(pack.Id + ":" + component.Id, out var tileBinding) && tileBinding.Type == "tiledata", "Missing tiledata binding");
                    using var tileDoc = JsonDocument.Parse(pack.ReadPayload(component.Entry));
                    StoreTileDefinition.Validate(tileDoc.RootElement);
                    tiles.Add(new { identity = pack.Id + ":" + component.Id, id = tileBinding.Id, content = tileDoc.RootElement.Clone() });
                    continue;
                }
                if (component.Type == "map")
                {
                    StorePack.Require(contentLock.Bindings.TryGetValue(pack.Id + ":" + component.Id, out var mapBinding) && mapBinding.Type == "map", "Missing map facet binding");
                    using var mapDoc = JsonDocument.Parse(pack.ReadPayload(component.Entry));
                    StoreMapDefinition.Validate(mapDoc.RootElement);
                    maps.Add(new { identity = pack.Id + ":" + component.Id, facet = mapBinding.Id, content = mapDoc.RootElement.Clone() });
                    continue;
                }
                StorePack.Require(component.Type == "item", "ModernUO export consumer not implemented: " + component.Type);
                using var doc = JsonDocument.Parse(pack.ReadPayload(component.Entry));
                var row = doc.RootElement;
                string graphic = row.GetProperty("graphic").GetString();
                StorePack.Require(component.References != null && component.References.Contains(graphic), "Graphic must be an explicit component reference");
                StorePack.Require(contentLock.Bindings.TryGetValue(graphic, out var binding) && binding.Type == "static", "Missing static graphic binding");
                string name = row.GetProperty("name").GetString();
                double weight = row.GetProperty("weight").GetDouble();
                StorePack.Require(name != null && name.Length is > 0 and <= 100 && double.IsFinite(weight) && weight >= 0 && weight <= 100000, "Invalid item definition");
                items.Add(new { identity = pack.Id + ":" + component.Id, graphic = binding.Id, name, weight, movable = row.GetProperty("movable").GetBoolean() });
            }
        StorePack.Require(items.Count > 0 || maps.Count > 0 || tiles.Count > 0 || regions.Count > 0, "No supported server content in deployment");
        string destination = Path.GetFullPath(output);
        StoreClient.NoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = "guo/server-content@1", identity_hash = closure.IdentityHash, items, maps, tiles, regions }, new JsonSerializerOptions { WriteIndented = true });
        StorePack.Require(bytes.Length <= 16 * 1024 * 1024, "Server export exceeds adapter size limit");
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
}
