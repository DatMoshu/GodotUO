using GUO.Store;
using System.Text.Json;

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
        string payload = Path.Combine(root, art.Id, art.Version, "stone.png");
        File.AppendAllText(payload, "tampered");
        bool refused = false;
        try { art.ReadPayload("stone.png"); } catch (InvalidDataException) { refused = true; }
        StorePack.Require(refused, "Modified payload was accepted");
        Console.WriteLine("content: dependency closure, stable identity, immutable manifest, bounded verified read and tamper refusal PASS");
    }
}
