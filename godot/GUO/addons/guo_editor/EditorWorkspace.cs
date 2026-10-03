#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.IO;
using Godot;
using GUO.Workspace;

/// <summary>
/// The editor's side of the per-user workspace (ADR-0032): the setting resolver that adds config.local.bat and
/// config.bat to the environment lookup, the earlier per-checkout profiles file, the one-time migration, and the
/// paths the run bar and other tabs share.
/// </summary>
internal static class EditorWorkspace
{
    private static bool _migrated;

    /// <summary>The earlier per-checkout profiles file.</summary>
    public static string LegacyProfiles => LegacyOverride ?? Path.Combine(EditorData.RepoRoot, "build", "editor_servers", "profiles.json");

    /// <summary>For the smoke check: a fixture in place of the earlier profiles file.</summary>
    public static string LegacyOverride { get; set; }

    /// <summary>The project the editor has open: what a guo-project client with no program starts.</summary>
    public static string HostProject => ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\');

    public static void Ensure() => Workspace.Setting ??= key => EditorData.Setting(key, "");

    /// <summary>The server list's path; the earlier file is migrated first, once per editor session.</summary>
    public static string ServersPath
    {
        get
        {
            Ensure();
            if (!_migrated)
            {
                _migrated = true;
                try { MigrateNow(); }
                catch (Exception e) { GD.PushError("Server profiles migration: " + e.Message); }
            }
            return Workspace.ServersFile;
        }
    }

    /// <summary>For the smoke check: migrate again after the workspace was pointed somewhere else.</summary>
    public static void ForgetMigration() => _migrated = false;

    private static void MigrateNow()
    {
        if (!File.Exists(LegacyProfiles)) return;
        ClientRegistry.Reset();
        string legacyRoot = Path.GetDirectoryName(LegacyProfiles);
        var before = File.Exists(Workspace.ServersFile) ? ServerProfiles.Load(Workspace.ServersFile) : new ServerProfiles();
        var known = new System.Collections.Generic.HashSet<string>();
        foreach (var s in before.Servers) known.Add(s.Id);
        if (!ServerProfiles.Migrate(LegacyProfiles, ClientRegistry.Current)) return;
        // A managed server started before the move keeps its identity file.
        foreach (var s in ServerProfiles.Load(Workspace.ServersFile).Servers)
        {
            if (known.Contains(s.Id)) continue;
            string old = Path.Combine(legacyRoot, s.Id, "process.json"), now = Path.Combine(Workspace.ServerHome(s.Id), "process.json");
            if (File.Exists(old) && !File.Exists(now))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(now));
                File.Copy(old, now);
            }
        }
    }

    /// <summary>The server profiles of the workspace, and the client registry they point into.</summary>
    public static ServerProfiles LoadServers()
    {
        string path = ServersPath;
        return ServerProfiles.Load(path);
    }

    /// <summary>A server's content store: its configured one, else the instance's own folder in the workspace.</summary>
    public static string ContentStore(ServerProfile s)
    {
        if (!string.IsNullOrEmpty(s.ContentStore)) return s.ContentStore;
        string old = Path.Combine(Path.GetDirectoryName(LegacyProfiles), s.Id, "store");
        return Directory.Exists(old) ? old : Path.Combine(Workspace.ServerHome(s.Id), "store");
    }
}
#endif
