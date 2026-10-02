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
        foreach (var pack in closure.Packs.Values)
            foreach (var component in pack.Manifest.Components ?? new())
            {
                if (component.Target == "client") continue;
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
        StorePack.Require(items.Count > 0, "No server items in deployment");
        string destination = Path.GetFullPath(output);
        StoreClient.NoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = "guo/server-content@1", identity_hash = closure.IdentityHash, items }, new JsonSerializerOptions { WriteIndented = true });
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
}
