#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>The selected server profile, as far as the Logs tab needs it (read from the run bar's profiles.json).</summary>
public sealed class LogProfile
{
    public string Id = "";
    public string Name = "";
    public string ServerDirectory = "";
    public string ServerProject = "";
    public string ClientProject = "";
    public string Executable = "";
}

/// <summary>
/// Where the logs are. Reads the run bar's profile list with JsonNode (never the typed serializer in addon
/// code) and works out, for the selected profile, the folders the shard writes its logs under and the
/// per-client console files the run bar redirects to. Nothing here writes anything.
/// </summary>
public static class LogSources
{
    public static string ServersRoot => Path.Combine(EditorData.RepoRoot, "build", "editor_servers");

    /// <summary>The selected profile, or null when there is no readable profiles.json.</summary>
    public static LogProfile SelectedProfile(string serversRoot = null)
    {
        try
        {
            string path = Path.Combine(serversRoot ?? ServersRoot, "profiles.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
            {
                return null;
            }

            JsonNode root = JsonNode.Parse(File.ReadAllText(path));
            string selected = (string)root?["Selected"];
            JsonArray servers = root?["Servers"] as JsonArray;
            if (servers == null)
            {
                return null;
            }

            JsonNode pick = servers.FirstOrDefault(s => (string)s?["Id"] == selected) ?? servers.FirstOrDefault();
            if (pick == null)
            {
                return null;
            }

            return new LogProfile
            {
                Id = (string)pick["Id"] ?? "",
                Name = (string)pick["Name"] ?? "server",
                ServerDirectory = (string)pick["ServerDirectory"] ?? "",
                ServerProject = (string)pick["ServerProject"] ?? "",
                ClientProject = (string)pick["ClientProject"] ?? "",
                Executable = (string)pick["Executable"] ?? "",
            };
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// The folders a profile's shard writes logs under: ModernUO's <c>Logs</c> next to the executable, and
    /// the distribution folder of a source checkout. A profile with no server folder (the configured dev
    /// shard) falls back to the dev shard's distribution (UO_SHARD_DIST).
    /// </summary>
    public static string[] ServerFolders(LogProfile p, string devDist)
    {
        var dirs = new List<string>();
        void Add(string root)
        {
            if (!string.IsNullOrEmpty(root))
            {
                dirs.Add(Path.Combine(root, "Logs"));
            }
        }

        if (p != null)
        {
            Add(p.ServerDirectory);
            if (!string.IsNullOrEmpty(p.Executable))
            {
                Add(Path.GetDirectoryName(p.Executable));
            }

            if (!string.IsNullOrEmpty(p.ServerProject))
            {
                Add(p.ServerProject);
                Add(Path.Combine(p.ServerProject, "Distribution"));
            }
        }

        if (p == null || string.IsNullOrEmpty(p.ServerDirectory))
        {
            Add(devDist);
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The console file the run bar redirects client slot <paramref name="slot"/> (0 based) of a profile to.</summary>
    public static string ClientConsole(string profileId, int slot, string serversRoot = null) =>
        Path.Combine(serversRoot ?? ServersRoot, profileId, "clients", slot.ToString(), "client.log");

    /// <summary>The newest log (.log or .txt) under any of the folders, or null. Archived folders are skipped.</summary>
    public static string Newest(IEnumerable<string> folders)
    {
        string best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string folder in folders ?? Array.Empty<string>())
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
                int seen = 0;
                foreach (string file in Directory.EnumerateFiles(folder, "*.*", options))
                {
                    if (++seen > 3000)
                    {
                        break;
                    }

                    string ext = Path.GetExtension(file);
                    if (!ext.Equals(".log", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (file.Contains("Archive", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    DateTime t = File.GetLastWriteTimeUtc(file);
                    if (t > bestTime)
                    {
                        bestTime = t;
                        best = file;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that vanishes or is locked is just not a source right now.
            }
        }

        return best;
    }

    /// <summary>The Godot log of the project (the editor's, and what the clients write when file logging is on).</summary>
    public static string GodotLog() => Godot.ProjectSettings.GlobalizePath("user://logs/godot.log");

    /// <summary>The client's own file logs: the packet logger and crash dumps under the client project's Logs folder.</summary>
    public static string[] ClientFileFolders(LogProfile p, string projectDir)
    {
        var dirs = new List<string>();
        if (p != null && !string.IsNullOrEmpty(p.ClientProject))
        {
            dirs.Add(Path.Combine(p.ClientProject, "Logs"));
        }

        if (!string.IsNullOrEmpty(projectDir))
        {
            dirs.Add(Path.Combine(projectDir, "Logs"));
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // --- the user's extra files (build/editor_logs/sources.json, docs/data_formats.md section 25) ---

    public static string SettingsPath => Path.Combine(EditorData.RepoRoot, "build", "editor_logs", "sources.json");

    public sealed class Settings
    {
        public int Cap = LogView.DefaultCap;
        public List<string> Files = new();
    }

    public static Settings LoadSettings(string path)
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024)
            {
                return s;
            }

            JsonNode root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["cap"] != null)
            {
                s.Cap = Math.Clamp((int)root["cap"], 100, 200000);
            }

            if (root?["files"] is JsonArray files)
            {
                foreach (JsonNode f in files)
                {
                    string p = (string)f?["path"];
                    if (!string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p) && !s.Files.Contains(p))
                    {
                        s.Files.Add(p);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidCastException or FormatException)
        {
            // An unreadable list is an empty list; the next save replaces it.
        }

        return s;
    }

    public static void SaveSettings(string path, Settings s)
    {
        var files = new JsonArray();
        foreach (string f in s.Files)
        {
            files.Add(new JsonObject { ["path"] = f });
        }

        var root = new JsonObject { ["format"] = 1, ["cap"] = s.Cap, ["files"] = files };
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString());
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
#endif
