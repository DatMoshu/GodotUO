using GUO.Store;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ContentChecks
{
    public static void Run(string examples, string root)
    {
        Directory.CreateDirectory(root);
        foreach (string archive in Directory.GetFiles(examples, "*.zip"))
        {
            string stage = Path.Combine(root, ".stage");
            Directory.CreateDirectory(stage);
            var m = StorePack.Extract(archive, stage);
            string destination = Path.Combine(root, m.Id, m.Version);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Directory.Move(stage, destination);
        }
        using var client = new StoreClient("http://127.0.0.1:18865", root, 100);
        var closure = client.VerifyContent("sample-content-server", "1.0.0");
        StorePack.Require(closure.Packs.Count == 2, "Dependency closure missing");
        var art = closure.Packs["sample-content-art"];
        StorePack.Require(art.ReadPayload("stone.png").Length > 0, "Read failed");
        string fingerprint = closure.IdentityHash;
        var editable = art.Manifest;
        editable.Files.Clear(); editable.Components.Clear();
        StorePack.Require(art.Manifest.Files.Count > 0 && art.Manifest.Components.Count > 0, "Manifest snapshot mutated");
        StorePack.Require(client.VerifyContent("sample-content-server", "1.0.0").IdentityHash == fingerprint, "Unstable identity");
        var first = new StoreContentLock { Pack = "sample-content-server", Version = "1.0.0", IdentityHash = fingerprint };
        first.Bindings.Add("sample-content-art:stone", new StoreContentBinding { Type = "static", Id = 3701 });
        string candidate = Path.Combine(root, "candidate.json"), active = Path.Combine(root, "active.json");
        File.WriteAllText(candidate, JsonSerializer.Serialize(first));
        StoreDeployment.Activate(client, candidate, active);
        first.Bindings["sample-content-art:stone"].Id = 3702;
        File.WriteAllText(candidate, JsonSerializer.Serialize(first));
        StoreDeployment.Activate(client, candidate, active);
        StoreDeployment.Rollback(client, active);
        StorePack.Require(StoreContentLock.Read(active).Bindings["sample-content-art:stone"].Id == 3701, "Rollback lost numeric mapping");
        first.IdentityHash = new string('0', 64);
        File.WriteAllText(candidate, JsonSerializer.Serialize(first));
        bool badLock = false;
        try { StoreDeployment.Activate(client, candidate, active); } catch (InvalidDataException) { badLock = true; }
        StorePack.Require(badLock && StoreContentLock.Read(active).Bindings["sample-content-art:stone"].Id == 3701, "Invalid candidate changed active deployment");
        var world = client.VerifyContent("sample-content-world", "1.0.0");
        var worldLock = new StoreContentLock { Pack = "sample-content-world", Version = "1.0.0", IdentityHash = world.IdentityHash };
        worldLock.Bindings.Add("sample-content-world:courtyard", new StoreContentBinding { Type = "map", Id = 0 });
        string worldLockPath = Path.Combine(root, "world-lock.json"), exportPath = Path.Combine(root, "world-export.json");
        File.WriteAllText(worldLockPath, JsonSerializer.Serialize(worldLock));
        StoreServerExport.Export(client, worldLockPath, exportPath);
        using (var exported = JsonDocument.Parse(File.ReadAllBytes(exportPath)))
        {
            var map = exported.RootElement.GetProperty("maps")[0];
            StorePack.Require(map.GetProperty("facet").GetInt32() == 0 && map.GetProperty("content").GetProperty("blocks")[0].GetProperty("land")[0].GetProperty("graphic").GetInt32() == 3, "Server export lost shared terrain binding/data");
        }
        byte[] authored = world.Packs["sample-content-world"].ReadPayload("courtyard.json");
        Action<JsonNode>[] invalidMaps = {
            node => node["blocks"][0]["x"] = -1,
            node => node["blocks"][0]["land"][0]["graphic"] = 0x4000,
            node => node["blocks"][0]["statics"][0]["x"] = 8,
            node => node["blocks"].AsArray().Add(node["blocks"][0].DeepClone()),
            node => node["blocks"][0]["land"].AsArray().RemoveAt(0)
        };
        foreach (var mutate in invalidMaps)
        {
            var node = JsonNode.Parse(authored); mutate(node);
            using var invalid = JsonDocument.Parse(node.ToJsonString());
            bool rejected = false;
            try { StoreMapDefinition.Validate(invalid.RootElement); } catch (InvalidDataException) { rejected = true; }
            StorePack.Require(rejected, "Malformed map definition accepted");
        }
        Console.WriteLine("content: shared map export and five malformed-map rejection cases PASS");
        var collision = client.VerifyContent("sample-content-collision", "1.0.0");
        var collisionLock = new StoreContentLock { Pack = "sample-content-collision", Version = "1.0.0", IdentityHash = collision.IdentityHash };
        collisionLock.Bindings.Add("sample-content-collision:courtyard", new StoreContentBinding { Type = "map", Id = 0 });
        collisionLock.Bindings.Add("sample-content-collision:barrier-data", new StoreContentBinding { Type = "tiledata", Id = 3702 });
        string collisionLockPath = Path.Combine(root, "collision-lock.json"), collisionExportPath = Path.Combine(root, "collision-export.json");
        File.WriteAllText(collisionLockPath, JsonSerializer.Serialize(collisionLock));
        StoreServerExport.Export(client, collisionLockPath, collisionExportPath);
        using (var exported = JsonDocument.Parse(File.ReadAllBytes(collisionExportPath)))
        {
            var tile = exported.RootElement.GetProperty("tiles")[0];
            StorePack.Require(tile.GetProperty("id").GetInt32() == 3702 && tile.GetProperty("content").GetProperty("flags").GetUInt64() == 64
                && tile.GetProperty("content").GetProperty("height").GetByte() == 8, "Server export lost shared collision data");
        }
        foreach (string malformed in new[] { "{\"height\":256}", "{\"flags\":-1}", "{\"heigth\":8}" })
        {
            using var invalid = JsonDocument.Parse(malformed);
            bool rejected = false;
            try { StoreTileDefinition.Validate(invalid.RootElement); }
            catch (InvalidDataException) { rejected = true; }
            catch (FormatException) { rejected = true; }
            StorePack.Require(rejected, "Malformed tile metadata accepted");
        }
        Console.WriteLine("content: shared collision metadata export and invalid-field/range rejection PASS");
        var regionClosure = client.VerifyContent("sample-content-region", "1.0.0");
        var regionLock = new StoreContentLock { Pack = "sample-content-region", Version = "1.0.0", IdentityHash = regionClosure.IdentityHash };
        regionLock.Bindings.Add("sample-content-art:ambience", new StoreContentBinding { Type = "music", Id = 100 });
        string regionLockPath = Path.Combine(root, "region-lock.json"), regionExportPath = Path.Combine(root, "region-export.json");
        File.WriteAllText(regionLockPath, JsonSerializer.Serialize(regionLock));
        StoreServerExport.Export(client, regionLockPath, regionExportPath);
        using (var exported = JsonDocument.Parse(File.ReadAllBytes(regionExportPath)))
            StorePack.Require(exported.RootElement.GetProperty("regions")[0].GetProperty("music_id").GetInt32() == 100, "Region music binding lost");
        var authoredRegion = regionClosure.Packs["sample-content-region"].ReadPayload("region.json");
        Action<JsonNode>[] invalidRegions = {
            node => node["priority"] = 151,
            node => node["facet"] = 256,
            node => node["areas"][0]["width"] = 0,
            node => node["areas"][0]["depth"] = 257
        };
        foreach (var mutate in invalidRegions)
        {
            var node = JsonNode.Parse(authoredRegion); mutate(node);
            using var invalid = JsonDocument.Parse(node.ToJsonString());
            bool rejected = false;
            try { StoreRegionDefinition.Validate(invalid.RootElement); } catch (InvalidDataException) { rejected = true; }
            StorePack.Require(rejected, "Malformed region accepted");
        }
        Console.WriteLine("content: region export, music reference binding and four malformed-region cases PASS");
        string payload = Path.Combine(root, art.Id, art.Version, "stone.png");
        File.AppendAllText(payload, "tampered");
        bool refused = false;
        try { art.ReadPayload("stone.png"); } catch (InvalidDataException) { refused = true; }
        StorePack.Require(refused, "Modified payload was accepted");
        Console.WriteLine("content: dependency closure, stable identity, immutable manifest, verified read, tamper refusal, atomic activation, rollback and failed-candidate preservation PASS");
    }
}
