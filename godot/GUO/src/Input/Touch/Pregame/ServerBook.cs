// GUO addition, not a port: upstream ClassicUO connects to the one address in
// settings.json and keeps no list of servers.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using GUO.Configuration;

namespace GUO.Input.Touch.Pregame;

/// <summary>
/// One server the pre-game card can play on. The fields past the address are
/// the catalogue manifest's (docs/ui/second_screen_pregame.md); a server the
/// player adds has only a name, a host and a port.
/// </summary>
internal sealed class ServerEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; } = 2593;
    [JsonPropertyName("era")] public string Era { get; set; }
    [JsonPropertyName("emulator")] public string Emulator { get; set; }
    [JsonPropertyName("client_version")] public string ClientVersion { get; set; }
    [JsonPropertyName("encryption")] public int? Encryption { get; set; }
    [JsonPropertyName("needs_custom_data")] public bool NeedsCustomData { get; set; }
    /// <summary>The shard's content descriptor (guo/shard-content@1, ADR-0026): the packs it runs.</summary>
    [JsonPropertyName("content")] public string Content { get; set; }
    [JsonPropertyName("third_party_clients")] public bool ThirdPartyClients { get; set; } = true;
    [JsonPropertyName("site")] public string Site { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; }

    /// <summary>The player's own (added by hand): its address is theirs to see and edit.</summary>
    [JsonPropertyName("own")] public bool Own { get; set; }

    [JsonPropertyName("favourite")] public bool Favourite { get; set; }

    /// <summary>
    /// Where the player keeps this shard's own client files (picked once), for
    /// a shard that needs them; GUO restarts with them to play there.
    /// </summary>
    [JsonPropertyName("data_folder")] public string DataFolder { get; set; }

    /// <summary>The player's accounts on it (AccountBook), or null; passwords only as the keystore's ciphertext.</summary>
    [JsonPropertyName("accounts")] public List<Accounts.SavedAccount> Accounts { get; set; }

    /// <summary>When it was last played on (UTC), or null.</summary>
    [JsonPropertyName("last_played")] public DateTime? LastPlayed { get; set; }

    /// <summary>The dev build's own shard (never saved, never in a release build).</summary>
    [JsonIgnore] public bool Dev { get; set; }

    public bool Same(string host, int port) => string.Equals(Host?.Trim(), host?.Trim(), StringComparison.OrdinalIgnoreCase) && Port == port;
}

/// <summary>
/// The player's servers: their own, their favourites and the last five played
/// on, kept in <c>servers.json</c> beside upstream's settings.json (GUO's own
/// file, so upstream's settings keep their shape). Nothing leaves the device.
/// A dev build adds the dev shard as a favourite, from the run's
/// UO_SHARD_HOST / UO_SHARD_PORT; a release build never has it.
/// </summary>
internal static class ServerBook
{
    public const int RecentKept = 5;

    private sealed class File_
    {
        [JsonPropertyName("servers")] public List<ServerEntry> Servers { get; set; } = new();
    }

    private static List<ServerEntry> _servers;
    private static ServerEntry _dev;
    private static bool _wasInGame;

    /// <summary>For the probe: a folder to keep servers.json in instead of beside settings.json.</summary>
    public static string PathOverride { get; set; }

    public static string FilePath => PathOverride ?? Path.Combine(Path.GetDirectoryName(Settings.GetSettingsFilepath()) ?? "", "servers.json");

    /// <summary>
    /// Set once by Main in a debug build only (<see cref="OS.IsDebugBuild"/>): a
    /// release export has no way to turn it on.
    /// </summary>
    /// <summary>The dev build's own shard, or null (a release build, or none configured).</summary>
    public static ServerEntry DevEntry => _dev;

    public static void SetDevShard(string host, int port)
    {
        if (!OS.IsDebugBuild() || string.IsNullOrWhiteSpace(host) || port <= 0)
        {
            return;
        }

        _dev = new ServerEntry { Name = "Dev shard", Host = host, Port = port, Favourite = true, Dev = true, Description = "This dev build's own shard, from its configuration." };
    }

    private static List<ServerEntry> Servers
    {
        get
        {
            if (_servers == null)
            {
                Load();
            }

            return _servers;
        }
    }

    public static void Load()
    {
        _servers = new List<ServerEntry>();

        try
        {
            if (File.Exists(FilePath))
            {
                _servers = JsonSerializer.Deserialize<File_>(File.ReadAllText(FilePath))?.Servers ?? new List<ServerEntry>();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] servers: {FilePath} unreadable, starting empty: {ex.Message}");
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new File_ { Servers = Servers }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] servers: could not save {FilePath}: {ex.Message}");
        }
    }

    /// <summary>Favourites: the dev shard first (a dev build), then the player's, by name.</summary>
    public static IEnumerable<ServerEntry> Favourites =>
        (_dev != null ? new[] { _dev } : Array.Empty<ServerEntry>())
        .Concat(Servers.Where(s => s.Favourite).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase));

    /// <summary>The player's own servers that are not favourites.</summary>
    public static IEnumerable<ServerEntry> Own => Servers.Where(s => s.Own && !s.Favourite).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The last <see cref="RecentKept"/> played on, newest first, that are in no other group.</summary>
    public static IEnumerable<ServerEntry> Recent => Servers
        .Where(s => s.LastPlayed != null && !s.Favourite && !s.Own && !(_dev != null && s.Same(_dev.Host, _dev.Port)))
        .OrderByDescending(s => s.LastPlayed)
        .Take(RecentKept);

    /// <summary>The entry for an address, if the book has one (the dev shard included).</summary>
    public static ServerEntry Find(string host, int port) =>
        _dev != null && _dev.Same(host, port) ? _dev : Servers.FirstOrDefault(s => s.Same(host, port));

    /// <summary>The player adds a server by hand. Null with the reason when it cannot be added.</summary>
    public static ServerEntry Add(string name, string host, string portText, out string error)
    {
        error = null;
        host = host?.Trim() ?? "";
        name = string.IsNullOrWhiteSpace(name) ? host : name.Trim();

        if (host.Length == 0 || host.Contains(' '))
        {
            error = "Type the server's address, for example play.example.com.";
            return null;
        }

        if (!int.TryParse(portText?.Trim(), out int port) || port < 1 || port > 65535)
        {
            error = "The port is a number from 1 to 65535 (most shards use 2593).";
            return null;
        }

        ServerEntry e = Servers.FirstOrDefault(s => s.Same(host, port));

        if (e == null)
        {
            e = new ServerEntry { Host = host, Port = port };
            Servers.Add(e);
        }

        e.Name = name;
        e.Own = true;
        Save();

        return e;
    }

    /// <summary>
    /// Favourites a server, or not. A catalogue shard is copied into the book
    /// first; the book's entry is returned (the one the list now shows).
    /// </summary>
    public static ServerEntry SetFavourite(ServerEntry e, bool on)
    {
        if (e.Dev)
        {
            return e;
        }

        e = Keep(e);
        e.Favourite = on;
        Save();

        return e;
    }

    /// <summary>Keeps where a shard's own client files are (null forgets it); the book's entry is returned.</summary>
    public static ServerEntry SetDataFolder(ServerEntry e, string folder)
    {
        if (e.Dev)
        {
            return e;
        }

        e = Keep(e);
        e.DataFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        Save();

        return e;
    }

    /// <summary>
    /// The book's entry that keeps a server's accounts, made if need be. The dev
    /// shard's is a plain entry at its address, which no group shows.
    /// </summary>
    public static ServerEntry Hold(ServerEntry e)
    {
        if (!e.Dev)
        {
            return Keep(e);
        }

        ServerEntry kept = Servers.FirstOrDefault(s => s.Same(e.Host, e.Port));

        if (kept == null)
        {
            kept = new ServerEntry { Name = e.Name, Host = e.Host, Port = e.Port };
            Servers.Add(kept);
        }

        return kept;
    }

    /// <summary>The book's entry that keeps a server's accounts, if there is one yet.</summary>
    public static ServerEntry Holding(ServerEntry e) => Servers.Contains(e) ? e : Servers.FirstOrDefault(s => s.Same(e.Host, e.Port));

    /// <summary>The entries with saved accounts.</summary>
    public static IEnumerable<ServerEntry> WithAccounts => Servers.Where(s => s.Accounts is { Count: > 0 });

    /// <summary>The book's entry for a server, a catalogue shard copied in first.</summary>
    private static ServerEntry Keep(ServerEntry e)
    {
        if (Servers.Contains(e))
        {
            return e;
        }

        ServerEntry kept = Servers.FirstOrDefault(s => s.Same(e.Host, e.Port));

        if (kept == null)
        {
            kept = ServerCatalogue.Servers.Contains(e) ? ServerCatalogue.Copy(e) : e;
            Servers.Add(kept);
        }

        return kept;
    }

    /// <summary>Forgets one of the player's own servers (or a recent one).</summary>
    public static void Remove(ServerEntry e)
    {
        if (Servers.Remove(e))
        {
            Save();
        }
    }

    /// <summary>
    /// Once per entry into the world: the server in use goes to the top of
    /// Recent, with the name the shard gave its game server when it has one.
    /// Called every frame by the card; costs a bool test otherwise.
    /// </summary>
    public static void NoteWorld(bool inGame)
    {
        if (inGame == _wasInGame)
        {
            return;
        }

        _wasInGame = inGame;

        if (!inGame)
        {
            return;
        }

        Settings s = Settings.GlobalSettings;

        // The dev shard stays a favourite of its own; it is never written down.
        if (_dev != null && _dev.Same(s.IP, s.Port))
        {
            _dev.LastPlayed = DateTime.UtcNow;
            return;
        }

        ServerEntry e = Servers.FirstOrDefault(x => x.Same(s.IP, s.Port));

        if (e == null)
        {
            // A catalogue shard keeps its manifest; any other is its address.
            ServerEntry listed = ServerCatalogue.Find(s.IP, s.Port);
            e = listed != null ? ServerCatalogue.Copy(listed) : new ServerEntry { Host = s.IP, Port = s.Port };
            Servers.Add(e);
        }

        if (string.IsNullOrWhiteSpace(e.Name))
        {
            e.Name = string.IsNullOrWhiteSpace(s.LastServerName) ? s.IP : s.LastServerName;
        }

        e.LastPlayed = DateTime.UtcNow;

        // Recent keeps five: older plain entries (not own, not favourite, no files or accounts kept) go.
        foreach (ServerEntry old in Servers.Where(x => x.LastPlayed != null && !x.Own && !x.Favourite && x.DataFolder == null && x.Accounts == null).OrderByDescending(x => x.LastPlayed).Skip(RecentKept).ToList())
        {
            Servers.Remove(old);
        }

        Save();
        GD.Print($"[GUO] servers: played on \"{e.Name}\"");
    }
}
