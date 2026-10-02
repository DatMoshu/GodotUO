using GUO.Store;

if (args.Length == 5 && args[0] == "content-lock")
{
    using var store = new StoreClient("http://127.0.0.1:18865", args[1], int.MaxValue);
    var snapshot = store.VerifyContent(args[2], args[3]);
    var contentLock = new StoreContentLock { Pack = args[2], Version = args[3], IdentityHash = snapshot.IdentityHash };
    File.WriteAllText(args[4], System.Text.Json.JsonSerializer.Serialize(contentLock, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Wrote inert content lock. Assign explicit numeric bindings before activation.");
    return 0;
}

if (args.Length == 3 && args[0] == "content-check")
{
    ContentChecks.Run(args[1], args[2]);
    return 0;
}

// The shared validation corpus (tools/asset_store/corpus, S6): the C# installer's verdict on every file.
if (args.Length == 3 && args[0] == "corpus")
{
    var results = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    foreach (string file in Directory.GetFiles(args[1]).OrderBy(f => f, StringComparer.Ordinal))
    {
        string name = Path.GetFileName(file);
        if (name == "expect.json") continue;
        string staging = Path.Combine(Path.GetTempPath(), "guo-corpus-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (name.EndsWith(".zip", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(staging);
                StorePack.Extract(file, staging);
            }
            else
            {
                StorePack.Parse(File.ReadAllBytes(file));
            }

            results[name] = new() { ["result"] = "accept" };
        }
        catch (Exception e)
        {
            results[name] = new() { ["result"] = "reject", ["error"] = e.GetType().Name + ": " + e.Message };
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
        }
    }

    File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"corpus: {results.Count} file(s) -> {args[2]}");
    return 0;
}

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
    if (args.Length >= 4 && args[2] == "install")
    {
        // Optional fifth argument: the client's profile version (default 6).
        var selected = entries.Single(p => p.Manifest.Id == args[3]);
        using var installer = new StoreClient(savedAddress, args[1], args.Length >= 5 ? int.Parse(args[4]) : 6);
        await installer.Install(selected);
        Console.WriteLine($"Installed {selected.Manifest.Id} {selected.Manifest.Version}; all hashes verified.");
        return 0;
    }
    var entry = entries.First(p => p.Manifest.Kind == "background");
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
    string activePath = $"user://store/{entry.Manifest.Id}/{entry.Manifest.Version}/still.png";
    StorePack.Require(!StoreBackground.BelongsTo(activePath, entry.Manifest.Id + "-other", entry.Manifest.Version), "Different pack matched");
    StorePack.Require(!StoreBackground.BelongsTo(activePath, entry.Manifest.Id, "1.0.10"), "Different version matched");
    StorePack.Require(!StoreBackground.BelongsTo("user://store/test/1.0.10/still.png", "test", "1.0.1"), "Version prefix matched");
    StorePack.Require(!StoreBackground.BelongsTo(null, "test", "1.0.0"), "Missing path matched");
    client.BackgroundRemoved = (id, version) =>
    {
        bool reset = StoreBackground.BelongsTo(activePath, id, version);
        if (reset) activePath = "";
        return reset;
    };
    client.Uninstall(entry.Manifest.Id, entry.Manifest.Version);
    StorePack.Require(activePath == "" && client.LastUninstallMessage.Contains("reset"), "Active background not reset/reported");
    client.Uninstall(entry.Manifest.Id, entry.Manifest.Version);
    StorePack.Require(!client.LastUninstallMessage.Contains("reset"), "Unrelated uninstall reported reset");
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
    var saver = entries.SingleOrDefault(p => p.Manifest.Kind == "screensaver");
    if (saver != null)
    {
        using var v10 = new StoreClient(args[0], args[1], 10);
        rejected = false;
        try { await v10.Install(saver); } catch (InvalidDataException) { rejected = true; }
        StorePack.Require(rejected, "Screensaver installed on a v10 client");
        using var v11 = new StoreClient(args[0], args[1], 11);
        string saverPath = await v11.Install(saver);
        var installed = v11.Installed().Single(m => m.Kind == "screensaver");
        StorePack.Require(File.Exists(Path.Combine(saverPath, StorePack.ScreensaverLoop(installed))), "Screensaver loop missing");
        StorePack.Require(StoreBackground.BelongsTo($"user://store/{installed.Id}/{installed.Version}/{StorePack.ScreensaverLoop(installed)}", installed.Id, installed.Version), "Screensaver path not owned by its pack");
        v11.Uninstall(installed.Id, installed.Version);
        StorePack.Require(!Directory.Exists(saverPath), "Screensaver uninstall failed");
        Console.WriteLine("PASS: screensaver kind: v10 refused, v11 install, loop found, uninstall");
    }
    Console.WriteLine("PASS: profile address persistence/isolation/validation, index, verified preview, install, payload hashes, discovery, repeat install, updates, uninstall, corruption, compatibility, cleanup");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("FAIL: " + e.Message);
    return 1;
}
