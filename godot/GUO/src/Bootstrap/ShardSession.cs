namespace GUO.Host;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using GUO.Input.Touch.Pregame;

/// <summary>
/// Playing on a shard with its own client files (docs/ui/second_screen_pregame.md,
/// "Play: what happens when you tap it"; ADR-0021's custom data slot). Play on
/// such a shard writes <c>shard_session.json</c> beside settings.json and
/// restarts GUO; at boot Main reads it before the data is resolved and runs on
/// the shard's folder, client version, encryption and address until the player
/// goes back to their own files, which restarts GUO again.
/// </summary>
/// <remarks>
/// GUO-owned; not in ClassicUO. The shard's folder is read in place, never
/// written. A folder with a guo_data.json manifest goes into the custom slot
/// (complete, or layered over the player's install); a folder without one must
/// be a whole client install (a shard's own client usually is) and is opened as
/// the install for the run. Going back leaves a one-shot file that carries the
/// player's own encryption and the server to play on next, and is deleted as
/// soon as it is read.
/// </remarks>
internal static class ShardSession
{
    public sealed class Data
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("host")] public string Host { get; set; } = "";
        [JsonPropertyName("port")] public int Port { get; set; }

        /// <summary>The shard's files; null in the one-shot file that goes back.</summary>
        [JsonPropertyName("data_folder")] public string DataFolder { get; set; }

        [JsonPropertyName("client_version")] public string ClientVersion { get; set; }
        [JsonPropertyName("encryption")] public int? Encryption { get; set; }

        /// <summary>The player's own encryption, put back when they go back.</summary>
        [JsonPropertyName("own_encryption")] public int? OwnEncryption { get; set; }

        [JsonPropertyName("started")] public DateTime? Started { get; set; }

        /// <summary>The shard's content (ADR-0026): its descriptor's address, the lock this run mounts,
        /// and whether its script packs may run. Null when the shard names no content.</summary>
        [JsonPropertyName("content_url")] public string ContentUrl { get; set; }
        [JsonPropertyName("content_lock")] public string ContentLock { get; set; }
        [JsonPropertyName("content_identity")] public string ContentIdentity { get; set; }
        [JsonPropertyName("scripts_allowed")] public bool? ScriptsAllowed { get; set; }

        /// <summary>A session that changes what this run loads: the shard's files, or its content.</summary>
        [JsonIgnore] public bool Holds => DataFolder != null || ContentLock != null;
    }

    /// <summary>Set by Main: shard_session.json beside settings.json.</summary>
    public static string FilePath { get; set; }

    /// <summary>The shard this run plays with the files of, or null.</summary>
    public static Data Current { get; private set; }

    public static bool Active => Current?.Holds == true;

    /// <summary>Whether this run mounts <paramref name="e"/>'s content, as its descriptor named it when GUO restarted.</summary>
    public static bool HasContentFor(ServerEntry e) =>
        Active && e != null && e.Same(Current.Host, Current.Port) && !string.IsNullOrWhiteSpace(e.Content) && Current.ContentUrl == e.Content.Trim();

    /// <summary>What this run holds of the shard's, for the Servers screen: "files", "packs" or "files and packs".</summary>
    public static string Holding => Current == null ? "" : Current.DataFolder != null && Current.ContentLock != null ? "files and packs"
        : Current.ContentLock != null ? "packs" : "files";

    /// <summary>Script packs are off on a shard whose descriptor says so.</summary>
    public static bool ScriptsForbidden => Active && Current.ScriptsAllowed == false;

    /// <summary>Why a session's files could not be used at boot, for the Servers tab to say once.</summary>
    public static string Dropped { get; set; }

    /// <summary>For the probe: called instead of restarting.</summary>
    public static Action RestartHook { get; set; }

    public static bool IsFor(ServerEntry e) => Active && e != null && e.Same(Current.Host, Current.Port);

    /// <summary>
    /// How a folder can serve as a shard's files: "custom" (a valid
    /// guo_data.json), "install" (a whole client), or null and why not.
    /// </summary>
    public static string FolderKind(string folder, out string why)
    {
        why = null;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            why = "that folder does not exist";
            return null;
        }

        if (File.Exists(Path.Combine(folder, DataSources.Manifest)))
        {
            (var manifest, string bad) = DataSources.ReadManifest(folder);

            if (manifest == null)
            {
                why = bad;
                return null;
            }

            return "custom";
        }

        string missing = DataSources.Validate(folder);

        if (missing != null)
        {
            why = $"it has no {DataSources.Manifest} and is not a whole client ({missing})";
            return null;
        }

        return "install";
    }

    /// <summary>At boot: the file, if there is one. A one-shot file (going back) is deleted once read.</summary>
    public static Data Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            Data d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));

            if (d == null || !d.Holds)
            {
                File.Delete(FilePath);
            }

            Current = d?.Holds == true ? d : null;
            return d;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] shard session: {FilePath} unreadable, ignored: {ex.Message}");
            TryDelete();
            return null;
        }
    }

    /// <summary>At boot, when the session's files can't be used: the player's own files, and why.</summary>
    public static void Drop(Data d, string why, string what = "files")
    {
        Current = null;
        Dropped = $"{d.Name}'s {what} can't be used ({why}). GUO started with your own files.";
        TryDelete();
        GD.Print($"[GUO] shard session: dropped: {why}");
    }

    /// <summary>Play with <paramref name="e"/>'s files (its data folder is set) and, when given, its content
    /// (a lock that GUO.Store.StoreShardContent.Prepare wrote): write the session and restart.</summary>
    public static void Start(ServerEntry e, GUO.Store.StoreShardContent content = null, string contentLock = null)
    {
        Configuration.Settings s = Configuration.Settings.GlobalSettings;
        var d = new Data
        {
            Name = e.Name,
            Host = e.Host.Trim(),
            Port = e.Port,
            DataFolder = string.IsNullOrWhiteSpace(e.DataFolder) ? null : e.DataFolder,
            ClientVersion = string.IsNullOrWhiteSpace(e.ClientVersion) ? null : e.ClientVersion.Trim(),
            Encryption = e.Encryption,
            // Going from one shard's files to another's keeps the player's own.
            OwnEncryption = Current != null ? Current.OwnEncryption : s.Encryption,
            Started = DateTime.UtcNow,
            ContentUrl = content != null ? e.Content?.Trim() : null,
            ContentLock = content != null ? contentLock : null,
            ContentIdentity = content?.IdentityHash,
            ScriptsAllowed = content?.ScriptsAllowed,
        };

        Write(d);
        GD.Print($"[GUO] shard session: restart with \"{e.Name}\"'s {(d.DataFolder != null && content != null ? "files and content" : content != null ? "content" : "files")}");
        Restart();
    }

    /// <summary>
    /// Back to the player's own files: a one-shot file with their own
    /// encryption and, when given, the server to play on next; then restart.
    /// </summary>
    public static void End(ServerEntry then = null)
    {
        var d = new Data
        {
            Name = then?.Name ?? "",
            Host = then?.Host?.Trim() ?? "",
            Port = then?.Port ?? 0,
            Encryption = Current?.OwnEncryption,
        };

        Write(d);
        GD.Print($"[GUO] shard session: restart with your own files{(then != null ? $", then \"{then.Name}\"" : "")}");
        Restart();
    }

    private static void Write(Data d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
        File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
    }

    public static void TryDelete()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Quits and starts GUO again with the same arguments: on the desktop a
    /// new process, on Android the activity relaunched (Godot's restart on exit).
    /// </summary>
    public static void Restart()
    {
        if (RestartHook != null)
        {
            RestartHook();
            return;
        }

        var args = OS.GetCmdlineArgs().ToList();
        string[] user = OS.GetCmdlineUserArgs();

        // A run from source (the editor, godot-console --path) is given the
        // project again, as an absolute path: the engine's own --path is not in
        // the list, and Main has moved the working directory to the client
        // home. An exported build finds its pack beside itself.
        if (!OS.HasFeature("template"))
        {
            int at = args.IndexOf("--path");

            if (at >= 0)
            {
                args.RemoveRange(at, Math.Min(2, args.Count - at));
            }

            args.InsertRange(0, new[] { "--path", ProjectSettings.GlobalizePath("res://").TrimEnd('/') });
        }

        // The user arguments follow "--"; add them unless the engine's list already has them.
        if (user.Length > 0 && !args.Contains("--") && !args.Contains("++"))
        {
            args.Add("--");
            args.AddRange(user);
        }

        GD.Print($"[GUO] shard session: restarting with {args.Count} arguments: {string.Join(" ", args.Where(x => !x.Contains("password", StringComparison.OrdinalIgnoreCase)).Take(6))} ...");
        OS.SetRestartOnExit(true, args.ToArray());
        ((SceneTree) Engine.GetMainLoop()).Quit();
    }

    /// <summary>For the probe: a session as if this run had started with it.</summary>
    public static void SetCurrentForProbe(Data d) => Current = d;
}
