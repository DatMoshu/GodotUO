// GUO addition, not a port. ADR-0032: the per-user workspace. Plain .NET (no Godot types) so
// tools/server_manager/tests compiles this file as it stands.

using System;
using System.IO;

namespace GUO.Workspace;

/// <summary>
/// Where a person's server instances, client profiles and per-run state live, shared by every checkout and
/// worktree. Resolved: an override (probes, tests), then <c>UO_WORKSPACE_DIR</c> in the environment, then
/// <see cref="Setting"/> (the editor's config.local.bat / config.bat reader), then the platform root a host
/// set (Android and web: user://), then the desktop default. The layout is docs/data_formats.md section 30.
/// </summary>
internal static class Workspace
{
    public const string Key = "UO_WORKSPACE_DIR";

    /// <summary>For probes and tests: use this folder instead.</summary>
    public static string RootOverride { get; set; }

    /// <summary>Set by a host that has no desktop folders (Android, web): the folder to use.</summary>
    public static string PlatformRoot { get; set; }

    /// <summary>Set by the editor: reads a key from config.local.bat then config.bat (EditorData.Setting).</summary>
    public static Func<string, string> Setting { get; set; }

    public static string Root
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(RootOverride))
            {
                return Path.GetFullPath(RootOverride);
            }

            string env = Environment.GetEnvironmentVariable(Key);

            if (!string.IsNullOrWhiteSpace(env) && Path.IsPathFullyQualified(env))
            {
                return Path.GetFullPath(env);
            }

            string configured = Setting?.Invoke(Key);

            if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
            {
                return Path.GetFullPath(configured);
            }

            if (!string.IsNullOrWhiteSpace(PlatformRoot))
            {
                return Path.GetFullPath(PlatformRoot);
            }

            return DefaultRoot();
        }
    }

    /// <summary>The default with nothing configured: LOCALAPPDATA\GUO, or $XDG_DATA_HOME/guo.</summary>
    public static string DefaultRoot()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrEmpty(home))
        {
            home = Path.GetTempPath();
        }

        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return Path.Combine(string.IsNullOrEmpty(local) ? Path.Combine(home, "AppData", "Local") : local, "GUO");
        }

        string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "guo");
    }

    public static string ProfilesDir => Path.Combine(Root, "profiles");
    public static string ServersFile => Path.Combine(ProfilesDir, "servers.json");
    public static string ClientsFile => Path.Combine(ProfilesDir, "clients.json");

    /// <summary>A server instance's folder: its install, Config, Saves, process.json, store.</summary>
    public static string ServerHome(string serverId) => Path.Combine(Root, "servers", Id(serverId));

    public static string ClientHome(string clientId) => Path.Combine(Root, "clients", Id(clientId));
    public static string ClientFile(string clientId) => Path.Combine(ClientHome(clientId), "client.json");
    public static string ClientOverride(string clientId) => Path.Combine(ClientHome(clientId), "overlay");

    /// <summary>One run slot (1 based) of a server and client pair: cache, settings, log, process state.</summary>
    public static string RunSlot(string serverId, string clientId, int slot)
    {
        if (slot < 1 || slot > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "Slots are 1 to 4");
        }

        return Path.Combine(Root, "runs", Id(serverId), Id(clientId), "slot-" + slot);
    }

    public static string ClientConsole(string serverId, string clientId, int slot) => Path.Combine(RunSlot(serverId, clientId, slot), "client.log");

    /// <summary>A file name that cannot climb out of the workspace.</summary>
    private static string Id(string id)
    {
        if (!IsId(id))
        {
            throw new ArgumentException("An id is 32 lowercase hex characters", nameof(id));
        }

        return id;
    }

    public static bool IsId(string id)
    {
        if (id == null || id.Length != 32)
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Writes a file by temporary file and move, so a reader never sees half of it.</summary>
    public static void WriteAtomic(string path, byte[] bytes) => WriteAtomic(path, stream => stream.Write(bytes));

    /// <summary>Text as UTF-8 without a byte order mark, the same bytes as <see cref="File.WriteAllText(string, string)"/>.</summary>
    public static void WriteAtomic(string path, string text) => WriteAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(text));

    /// <summary>
    /// The temporary file sits in the target's folder, so the move is a rename on one volume. If
    /// <paramref name="write"/> throws, or the move fails, the old file is left whole and the temporary is removed.
    /// </summary>
    public static void WriteAtomic(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                write(stream);
                stream.Flush(true);
            }

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
