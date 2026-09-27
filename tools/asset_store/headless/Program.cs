using GUO.Store;

try
{
    using var client = new StoreClient(args[0], args[1], 6);
    var entries = await client.FetchIndex();
    StorePack.Require(entries.Count >= 1, "Empty index");
    var entry = entries.First();
    string path = await client.Install(entry);
    foreach (var file in entry.Manifest.Files)
        StorePack.Require(StorePack.HashFile(Path.Combine(path, file.Key)) == file.Value, "Installed hash mismatch");
    StorePack.Require(client.Installed().Count == 1, "Installed pack not discoverable");
    await client.Install(entry); // Repeat installation is an idempotent, verified no-op.
    var newer = new StoreEntry { Manifest = new StoreManifest { Id = entry.Manifest.Id, Version = "1.0.10", MinProfileVersion = 6 } };
    StorePack.Require(client.HasUpdate(newer), "Newer version not detected");
    StorePack.Require(StorePack.Version("1.0.10") > StorePack.Version("1.0.9"), "Versions compared lexically");
    client.Uninstall(entry.Manifest.Id, entry.Manifest.Version);
    StorePack.Require(!Directory.Exists(path) && client.Installed().Count == 0, "Uninstall failed");
    var bad = new StoreEntry { Manifest = entry.Manifest, Url = entry.Url, Size = entry.Size, Sha256 = new string('0', 64) };
    bool rejected = false;
    try { await client.Install(bad); } catch (InvalidDataException) { rejected = true; }
    StorePack.Require(rejected && !Directory.Exists(path), "Corrupt download was installed");
    using var oldClient = new StoreClient(args[0], args[1], 0);
    rejected = false;
    try { await oldClient.Install(entry); } catch (InvalidDataException) { rejected = true; }
    StorePack.Require(rejected, "Profile compatibility gate failed");
    StorePack.Require(!Directory.EnumerateFileSystemEntries(args[1], ".install-*").Any(), "Staging leaked");
    Console.WriteLine("PASS: index, install, payload hashes, discovery, repeat install, updates, uninstall, corruption, compatibility, cleanup");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("FAIL: " + e.Message);
    return 1;
}
