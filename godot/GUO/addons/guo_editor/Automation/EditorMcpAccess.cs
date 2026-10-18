#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using GUO.Workspace;
using Environment = System.Environment;

/// <summary>Per-user MCP permissions. Only the editor settings UI changes these; no tool can grant itself access.</summary>
internal static class EditorMcpAccess
{
    internal sealed record Group(string Id, bool Default);
    internal static readonly Group[] Groups =
    {
        new("read_state", true), new("navigate_resize", true), new("connect_refresh", true),
        new("save_backup_create", false), new("restart", false), new("restore_settings_save", false),
        new("accounts", false), new("commands", false), new("godview_actions", false),
    };
    internal const string Prefix = "guo/mcp_access/";
    private static JsonObject _document;
    private static string _file;
    private static bool _syncing;
    internal static string FilePath { get { EditorWorkspace.Ensure(); return Path.Combine(Workspace.Root, "editor", "mcp_access.json"); } }
    private static JsonObject Document
    {
        get
        {
            if (_document != null && _file == FilePath) return _document;
            _file = FilePath;
            // A damaged policy must not silently grant the default-on groups.
            _document = File.Exists(_file) ? JsonNode.Parse(File.ReadAllText(_file)) as JsonObject
                ?? throw new IOException("Invalid MCP access settings") : new JsonObject { ["groups"] = new JsonObject(), ["audit"] = new JsonArray() };
            if (_document["groups"] is not JsonObject || _document["audit"] is not JsonArray)
                throw new IOException("Invalid MCP access settings");
            return _document;
        }
    }
    internal static bool Enabled(string id) => (bool?)Document["groups"]?[id] ?? Groups.Single(g => g.Id == id).Default;

    internal static string GroupFor(string tool, JsonNode args) => tool switch
    {
        "editor_state" or "editor_interface" or "admin_state" => "read_state",
        "admin_view" or "editor_window_resize" or "editor_navigate" or "editor_server_select" => "navigate_resize",
        "admin_connect" or "admin_refresh" => "connect_refresh",
        "admin_save" or "admin_backup_create" => "save_backup_create",
        "admin_restart" => "restart",
        "admin_backup_restore" or "admin_settings_edit" or "admin_settings_save" => "restore_settings_save",
        "admin_account" => "accounts",
        "admin_command" => "commands",
        "admin_godview" => (string)args?["action"] switch
        {
            "watch" or "find" or "select" or "filter" => "navigate_resize",
            "press" when (string)args?["text"] is "Open paperdoll" or "Follow" or "Stop following" => "navigate_resize",
            _ => "godview_actions",
        },
        _ => null,
    };

    // Called on the main thread, both before approval and immediately before dispatch.
    internal static string Refusal(string tool, JsonNode args, bool mcp, bool readOnly)
    {
        if (!AiFeatures.Enabled) return "refused: " + AiFeatures.DisabledMessage;
        try
        {
            string group = GroupFor(tool, args);
            if (group != null && !Enabled(group)) return $"refused: enable Editor Settings > GUO > MCP Access > {group.Replace('_', ' ')} ({Prefix}{group})";
            // A scripted mutation is restricted to the launcher's exact names even when a dialog approver exists.
            if (mcp && !readOnly && Environment.GetEnvironmentVariable("GUO_EDITOR_SCRIPTED") == "1"
                && !(Environment.GetEnvironmentVariable("GUO_EDITOR_MCP_PREAPPROVED") ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(tool))
                return $"refused: {tool} is outside --allow-editor-tool (GUO_EDITOR_MCP_PREAPPROVED)";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { return "refused: MCP access settings could not be read; repair Editor Settings > GUO > MCP Access"; }
    }

    internal static void Set(string group, bool enabled)
    {
        if (Enabled(group) == enabled) return;
        var next = (JsonObject)Document.DeepClone();
        next["groups"][group] = enabled;
        ((JsonArray)next["audit"]).Add(new JsonObject
        {
            ["at"] = DateTime.UtcNow.ToString("O"), ["editor"] = "local user", ["op"] = "editor_mcp_access",
            ["level"] = "local", ["ok"] = true,
            ["args"] = new JsonObject { ["setting"] = Prefix + group, ["enabled"] = enabled },
        });
        // Permission and audit entry commit together, including while disconnected from a shard.
        Workspace.WriteAtomic(FilePath, next.ToJsonString());
        _document = next;
    }

    internal static IReadOnlyList<AdminAuditRow> AuditRows => AdminAuditView.FromEntries(Document["audit"] as JsonArray);
    internal static void Forget() { _document = null; _file = null; }

    internal static void InitializeSettings(EditorSettings settings)
    {
        _syncing = true;
        try
        {
            foreach (Group g in Groups)
            {
                settings.SetSetting(Prefix + g.Id, Enabled(g.Id));
                settings.SetInitialValue(Prefix + g.Id, g.Default, false);
            }
        }
        finally { _syncing = false; }
    }

    internal static void SettingsChanged(EditorSettings settings)
    {
        if (_syncing) return;
        foreach (Group g in Groups)
        {
            bool enabled = settings.GetSetting(Prefix + g.Id).AsBool();
            if (Enabled(g.Id) == enabled) continue;
            try { Set(g.Id, enabled); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                InitializeSettings(settings);
                GD.PushError("MCP access could not be saved; permission unchanged. Check the per-user workspace.");
                return;
            }
        }
        GuoEditorPlugin.AdminMain?.Audit.RefreshLocal();
    }
}
#endif
