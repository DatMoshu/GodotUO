// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GUO.ModApi;
using GUO.Network.Mods;

namespace GUO.Store;

/// <summary>A publisher-signed mod component that the user approved, bound to the content hash
/// of its pack's verified dependency closure and to the publisher's key.</summary>
internal sealed class StoreModApproval
{
    [JsonPropertyName("identity")] public string Identity { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; }
    [JsonPropertyName("key")] public string Key { get; set; }
}

/// <summary>An installed mod component as the Store's review shows it.</summary>
internal sealed record StoreModInfo(StoreManifest Pack, StoreComponent Component, string Identity, string Fingerprint,
    IReadOnlyList<string> Shards, string Packets, bool Approved);

/// <summary>The `mod` component kind (docs/asset_pack_ecosystem.md, Mod components; the
/// ADR-0038 amendment). Installing is inert; a mod loads at start only with Load mods on and a
/// matching approval in <see cref="ApprovalsFile"/>.</summary>
internal static class StoreMods
{
    public const string ApprovalsFile = ".approved-mods.json";
    public const string Unavailable = "Mods are not available on this device.";
    private const int MaxModJson = 64 * 1024;

    /// <summary>Manifest-only checks, so a catalogue entry is refused before anything downloads.</summary>
    public static void Validate(StoreComponent c, StoreManifest m)
    {
        if (c.Type != "mod")
        {
            StorePack.Require(c.Assembly == null && c.Abi == null && c.Packets == null && c.Gumps == null && c.Key == null && c.Signature == null,
                "Mod fields on a component that is not a mod");
            return;
        }
        StorePack.Require(c.Target == "client", "A mod component targets the client only");
        StorePack.Require(Path.GetExtension(c.Entry).Equals(".json", StringComparison.OrdinalIgnoreCase), "A mod component's entry is its mod.json");
        StorePack.Require(c.References == null && c.Language == null && c.Runtime == null && c.RuntimeVersion == null && c.Capabilities == null,
            "Script fields on a mod component");
        StorePack.Require(c.Assembly != null && m.Files.ContainsKey(c.Assembly)
            && Path.GetExtension(c.Assembly).Equals(".dll", StringComparison.OrdinalIgnoreCase), "A mod component names its declared .dll");
        StorePack.Require(Folder(c.Assembly) == Folder(c.Entry), "A mod's assembly sits beside its mod.json");
        StorePack.Require(c.Abi == ModAbi.Version, $"This mod was built for mod ABI {c.Abi}; this client has ABI {ModAbi.Version}");
        StorePack.Require(c.Packets != null, "A mod component declares its packets");
        Declared(c);
        StorePack.Require(c.Gumps != null && c.Gumps.Count == 0, "Mod ABI 1 has no gump call; gumps must be empty");
        byte[] key = Ed25519.Decode(c.Key, 32);
        byte[] signature = Ed25519.Decode(c.Signature, 64);
        StorePack.Require(Ed25519.Verify(key, SignedMessage(m, c), signature), "The mod's publisher signature does not verify");
    }

    /// <summary>A .dll is allowed only as exactly one mod component's assembly.</summary>
    public static void ValidateAssemblies(StoreManifest m)
    {
        var assemblies = m.Components.Where(c => c.Type == "mod").Select(c => c.Assembly).ToList();
        StorePack.Require(assemblies.Distinct(StringComparer.OrdinalIgnoreCase).Count() == assemblies.Count, "Two mod components share an assembly");
        StorePack.Require(m.Files.Keys.Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .All(p => assemblies.Contains(p, StringComparer.Ordinal)), "A .dll is allowed only as a mod component's assembly");
        var folders = m.Components.Where(c => c.Type == "mod").Select(c => Folder(c.Entry)).ToList();
        StorePack.Require(folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() == folders.Count, "Each mod component needs its own folder");
    }

    /// <summary>What the publisher signs: the pack, the component and both payload hashes.</summary>
    public static byte[] SignedMessage(StoreManifest m, StoreComponent c) => Encoding.UTF8.GetBytes(
        "guo/mod-signature@1\n" + m.Id + "\n" + m.Version + "\n" + c.Id + "\n" + m.Files[c.Entry] + "\n" + m.Files[c.Assembly] + "\n");

    /// <summary>Payload checks once the bytes are here: mod.json agrees with the component.</summary>
    public static void CheckPayload(StoreManifest m, Func<string, byte[]> read)
    {
        if (m.Components == null)
            return;
        foreach (StoreComponent c in m.Components.Where(c => c.Type == "mod"))
        {
            StorePack.Require(ClientMods.Supported, Unavailable);
            byte[] bytes = read(c.Entry);
            StorePack.Require(bytes.Length <= MaxModJson, "A mod's mod.json is too large");
            ModManifest mod;
            try { mod = ModManifest.Parse(Encoding.UTF8.GetString(bytes), null); }
            catch (Exception e) when (e is ModException or JsonException) { throw new InvalidDataException("The mod's mod.json is refused: " + e.Message); }
            StorePack.Require(mod.Abi == c.Abi, "The mod's mod.json and its component disagree on abi");
            StorePack.Require(Folder(c.Entry) + mod.Assembly == c.Assembly, "The mod's mod.json and its component name different assemblies");
            StorePack.Require(Same(mod.Packets, Declared(c)), "The mod's mod.json and its component declare different packets");
        }
    }

    public static string Identity(StoreManifest m, StoreComponent c) => m.Id + ":" + c.Id;

    public static IReadOnlyList<StoreModApproval> Approvals(string root)
    {
        string path = Path.Combine(root, ApprovalsFile);
        if (!File.Exists(path))
            return Array.Empty<StoreModApproval>();
        StoreClient.NoLinks(path);
        var list = JsonSerializer.Deserialize<List<StoreModApproval>>(File.ReadAllBytes(path));
        return (IReadOnlyList<StoreModApproval>)list ?? Array.Empty<StoreModApproval>();
    }

    /// <summary>Every installed mod component, newest version of a pack first.</summary>
    public static List<StoreModInfo> Installed(StoreClient store)
    {
        var approvals = Approvals(store.Root);
        var result = new List<StoreModInfo>();
        foreach (StoreManifest m in store.Installed().Where(m => m.Components != null).OrderBy(m => m.Id, StringComparer.Ordinal).ThenByDescending(m => StorePack.Version(m.Version)))
            foreach (StoreComponent c in m.Components.Where(c => c.Type == "mod"))
            {
                string identity = Identity(m, c);
                bool approved = false;
                try
                {
                    string content = store.VerifyContent(m.Id, m.Version).IdentityHash;
                    approved = approvals.Any(a => a.Identity == identity && a.Content == content && a.Key == c.Key);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { }
                ModManifest mod = null;
                try { mod = ModManifest.Parse(Encoding.UTF8.GetString(store.ReadVerifiedPayload(m, c.Entry, MaxModJson)), null); }
                catch (Exception e) when (e is IOException or InvalidDataException or ModException or JsonException) { }
                result.Add(new StoreModInfo(m, c, identity, Ed25519.Fingerprint(Ed25519.Decode(c.Key, 32)),
                    mod?.Shards ?? new List<string>(), Describe(Declared(c)), approved));
            }
        return result;
    }

    /// <summary>Records the user's approval of one installed mod component, as it is now.</summary>
    public static void Approve(StoreClient store, string packId, string version, string componentId)
    {
        StorePack.Require(ClientMods.Supported, Unavailable);
        var closure = store.VerifyContent(packId, version);
        StoreManifest m = closure.Packs[packId].Manifest;
        StoreComponent c = m.Components?.SingleOrDefault(x => x.Id == componentId && x.Type == "mod");
        StorePack.Require(c != null, "No such mod component");
        CheckPayload(m, name => store.ReadVerifiedPayload(m, name, name == c.Entry ? MaxModJson : (int)StorePack.MaxFile));
        string identity = Identity(m, c);
        var list = Approvals(store.Root).Where(a => a.Identity != identity).ToList();
        list.Add(new StoreModApproval { Identity = identity, Content = closure.IdentityHash, Key = c.Key });
        Write(store.Root, list);
    }

    public static void Revoke(string root, string identity) =>
        Write(root, Approvals(root).Where(a => a.Identity != identity).ToList());

    /// <summary>Drops every approval of a pack's mods, when the pack is removed.</summary>
    public static void Forget(string root, string packId)
    {
        var approvals = Approvals(root);
        if (approvals.Any(a => a.Identity.StartsWith(packId + ":", StringComparison.Ordinal)))
            Write(root, approvals.Where(a => !a.Identity.StartsWith(packId + ":", StringComparison.Ordinal)).ToList());
    }

    /// <summary>Installed folders of the approved mods, for the loader at start. A mod whose pack
    /// changed since its approval, or whose key changed, is left out and reported.</summary>
    public static List<string> ApprovedFolders(StoreClient store, List<string> refused)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (StoreModInfo info in Installed(store))
        {
            if (!seen.Add(info.Identity))
                continue; // an older installed version of the same component
            if (!info.Approved)
            {
                if (Approvals(store.Root).Any(a => a.Identity == info.Identity))
                    refused?.Add($"{info.Identity}: changed since you approved it; review it again in the Store");
                continue;
            }
            try
            {
                CheckPayload(info.Pack, name => store.ReadVerifiedPayload(info.Pack, name, name == info.Component.Entry ? MaxModJson : (int)StorePack.MaxFile));
                folders.Add(Path.Combine(store.Root, info.Pack.Id, info.Pack.Version, Folder(info.Component.Entry).Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                refused?.Add($"{info.Identity}: {e.Message}");
            }
        }
        return folders;
    }

    private static void Write(string root, List<StoreModApproval> list)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, ApprovalsFile);
        StoreClient.NoLinks(path);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    private static List<ModPacket> Declared(StoreComponent c)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(c.Packets));
            return ModManifest.ParsePackets(doc.RootElement, c.Id);
        }
        catch (ModException e) { throw new InvalidDataException("A mod component's packets are refused: " + e.Message); }
    }

    private static bool Same(List<ModPacket> a, List<ModPacket> b) => a.Count == b.Count && a.Zip(b).All(p =>
        p.First.Id == p.Second.Id && p.First.Subcommand == p.Second.Subcommand && p.First.Length == p.Second.Length && p.First.Override == p.Second.Override);

    private static string Describe(List<ModPacket> packets) => string.Join(", ", packets.Select(p =>
        (p.Subcommand is ushort s ? $"0xBF/0x{s:X2}" : $"0x{p.Id:X2}") + (p.Override ? " (replaces the client's)" : "")));

    private static string Folder(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..(slash + 1)];
    }
}
