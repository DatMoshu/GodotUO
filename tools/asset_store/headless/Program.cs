using GUO.Store;

try
{
    string profile = Path.Combine(args[1], "..", "profile-settings");
    StorePack.Require(StoreAddress.Load(profile, args[0]) == args[0], "Default store address changed");
    StoreAddress.Save(profile, args[0]);
    string savedAddress = StoreAddress.Load(profile, "https://unused.example.com/");
    StorePack.Require(savedAddress == StoreAddress.Normalize(args[0]), "Store address did not persist");
    foreach (string invalid in new[] { "ftp://example.com", "http://user:pass@example.com", "http:///", "http://example.com:0", "http://example.com/?secret=x", "http://example.com/#x", "http://example.com\\other", "" })
    {
        bool refused = false;
        try { StoreAddress.Save(profile, invalid); } catch (InvalidDataException) { refused = true; }
        StorePack.Require(refused && StoreAddress.Load(profile, "") == savedAddress, "Invalid address changed saved setting");
    }
    StorePack.Require(StoreAddress.Load(profile + "-other", "https://example.com") == "https://example.com", "Profile address leaked");
    StorePack.Require(StoreAddress.Normalize(" https://example.com:8443/catalogue ") == "https://example.com:8443/catalogue/", "Address path/port lost");
    using var client = new StoreClient(savedAddress, args[1], 6);
    var entries = await client.FetchIndex();
    StorePack.Require(entries.Count >= 1, "Empty index");
    if (args.Length == 4 && args[2] == "install")
    {
        var selected = entries.Single(p => p.Manifest.Id == args[3]);
        await client.Install(selected);
        Console.WriteLine($"Installed {selected.Manifest.Id} {selected.Manifest.Version}; all hashes verified.");
        return 0;
    }
    var entry = entries.First();
    byte[] preview = await client.FetchPreview(entry);
    StorePack.Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(preview)).ToLowerInvariant()
        == entry.Manifest.Files[entry.Manifest.Preview], "Preview hash mismatch");
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
    Console.WriteLine("PASS: profile address persistence/isolation/validation, index, verified preview, install, payload hashes, discovery, repeat install, updates, uninstall, corruption, compatibility, cleanup");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("FAIL: " + e.Message);
    return 1;
}
