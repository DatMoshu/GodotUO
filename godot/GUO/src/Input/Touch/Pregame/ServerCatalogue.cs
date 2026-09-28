// GUO addition, not a port: upstream ClassicUO keeps no list of servers.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace GUO.Input.Touch.Pregame;

/// <summary>
/// The community catalogue (docs/data_formats.md section 18): the shards in
/// the repo's <c>servers/catalogue.json</c>, built into the client. Every entry
/// is owner-approved and allows third-party clients; an entry that says it
/// doesn't, or has no usable address, is skipped.
/// </summary>
internal static class ServerCatalogue
{
    private sealed class File_
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("servers")] public List<ServerEntry> Servers { get; set; } = new();
    }

    private static List<ServerEntry> _servers;

    /// <summary>For the probe: a catalogue file to read instead of the built-in one.</summary>
    public static string PathOverride { get; set; }

    /// <summary>False when the catalogue could not be read: the group says so.</summary>
    public static bool Loaded { get; private set; }

    public static IReadOnlyList<ServerEntry> Servers
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
        Loaded = false;

        try
        {
            string json;

            if (PathOverride != null)
            {
                json = File.ReadAllText(PathOverride);
            }
            else
            {
                using Stream s = typeof(ServerCatalogue).Assembly.GetManifestResourceStream("servers/catalogue.json");

                if (s == null)
                {
                    GD.PrintErr("[GUO] servers: the built-in catalogue is missing");
                    return;
                }

                using var reader = new StreamReader(s);
                json = reader.ReadToEnd();
            }

            List<ServerEntry> all = JsonSerializer.Deserialize<File_>(json)?.Servers ?? new List<ServerEntry>();

            foreach (ServerEntry e in all)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Host) || e.Port < 1 || e.Port > 65535 || !e.ThirdPartyClients)
                {
                    continue;
                }

                // The player's own fields are theirs, never the catalogue's.
                e.Own = false;
                e.Favourite = false;
                e.LastPlayed = null;
                e.DataFolder = null;
                _servers.Add(e);
            }

            Loaded = true;
            GD.Print($"[GUO] servers: catalogue, {_servers.Count} of {all.Count} listed");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] servers: the catalogue couldn't be read: {ex.Message}");
        }
    }

    public static ServerEntry Find(string host, int port) => Servers.FirstOrDefault(e => e.Same(host, port));

    /// <summary>A copy for the player's book: the manifest's fields, none of the player's.</summary>
    public static ServerEntry Copy(ServerEntry e)
    {
        ServerEntry c = JsonSerializer.Deserialize<ServerEntry>(JsonSerializer.Serialize(e));
        c.Own = false;
        c.Favourite = false;
        c.LastPlayed = null;
        c.DataFolder = null;
        return c;
    }
}
