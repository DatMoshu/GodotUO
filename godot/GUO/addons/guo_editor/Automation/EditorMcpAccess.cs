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
    internal static string Problem { get; private set; } = "";
    private static bool PolicyFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException;
    internal static string FilePath { get { EditorWorkspace.Ensure(); return Path.Combine(Workspace.Root, "editor", "mcp_access.json"); } }
    private static JsonObject Document
    {
        get
        {
            try
            {
                string file = FilePath;
                if (_document != null && _file == file) return _document;
                JsonObject next;
                try { next = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new IOException("Invalid MCP access settings"); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                { next = new JsonObject { ["groups"] = new JsonObject(), ["audit"] = new JsonArray() }; }
                if (next["groups"] is not JsonObject groups || next["audit"] is not JsonArray audit
                    || groups.Any(pair => pair.Value is not JsonValue value || !value.TryGetValue<bool>(out _))
                    || audit.Any(entry => entry is not JsonObject))
                    throw new IOException("Invalid MCP access settings");
                _ = AdminAuditView.FromEntries(audit); // Audit must also be readable before permissions are cached.
                // Publish only a fully validated policy. A failed read must never become a cache hit.
                _document = next;
                _file = file;
                Problem = "";
                return next;
            }
            catch (Exception ex) when (PolicyFailure(ex))
            {
                _document = null; _file = null;
                Problem = "MCP access is disabled: the policy could not be read. Restore or repair editor/mcp_access.json in your per-user workspace, then reload the editor. The file was left unchanged; manual authoring remains available.";
                throw;
            }
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
            _ = Document; // Invalid policy denies every tool, including ungrouped read-only calls.
            string group = GroupFor(tool, args);
            if (group != null && !Enabled(group)) return $"refused: enable Editor Settings > GUO > MCP Access > {group.Replace('_', ' ')} ({Prefix}{group})";
            // Every scripted call intersects the launcher's exact names, including read-only tools.
            if (mcp && Environment.GetEnvironmentVariable("GUO_EDITOR_SCRIPTED") == "1"
                && !(Environment.GetEnvironmentVariable("GUO_EDITOR_MCP_PREAPPROVED") ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(tool))
                return $"refused: {tool} is outside --allow-editor-tool (GUO_EDITOR_MCP_PREAPPROVED)";
            return null;
        }
        catch (Exception ex) when (PolicyFailure(ex))
        { return "refused: " + Problem; }
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

    internal static IReadOnlyList<AdminAuditRow> AuditRows
    {
        get
        {
            try { return AdminAuditView.FromEntries(Document["audit"] as JsonArray); }
            catch (Exception ex) when (PolicyFailure(ex)) { return Array.Empty<AdminAuditRow>(); }
        }
    }
    internal static void Forget() { _document = null; _file = null; Problem = ""; }

    internal static void InitializeSettings(EditorSettings settings)
    {
        _syncing = true;
        try
        {
            if (!AiFeatures.Enabled)
            {
                foreach (Group g in Groups) settings.Erase(Prefix + g.Id);
                return; // Hide AI settings without reading or changing the saved policy.
            }
            bool healthy;
            try { _ = Document; healthy = true; }
            catch (Exception ex) when (PolicyFailure(ex)) { healthy = false; GD.PushWarning(Problem); }
            foreach (Group g in Groups)
            {
                settings.SetSetting(Prefix + g.Id, healthy && Enabled(g.Id));
                settings.SetInitialValue(Prefix + g.Id, healthy && g.Default, false);
            }
        }
        finally { _syncing = false; }
    }

    internal static void SettingsChanged(EditorSettings settings)
    {
        if (_syncing || !AiFeatures.Enabled) return;
        try
        {
            foreach (Group g in Groups)
            {
                bool enabled = settings.GetSetting(Prefix + g.Id).AsBool();
                if (Enabled(g.Id) != enabled) Set(g.Id, enabled);
            }
        }
        catch (Exception ex) when (PolicyFailure(ex))
        {
            InitializeSettings(settings);
            GD.PushWarning("MCP access could not be saved; permission unchanged. Check the per-user workspace.");
        }
        GuoEditorPlugin.AdminMain?.Audit.RefreshLocal();
    }
}
#endif
