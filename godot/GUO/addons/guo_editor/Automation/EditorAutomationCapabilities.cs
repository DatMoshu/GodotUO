#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

/// <summary>Background editor and Admin operations. Reuses the panels' validation and bridge authorization.</summary>
internal static class EditorAutomationCapabilities
{
    private static string Text(JsonNode a, string key) => (string)a?[key] ?? "";
    private static int Number(JsonNode a, string key, int fallback = 0) => (int?)a?[key] ?? fallback;
    private static bool Flag(JsonNode a, string key) => (bool?)a?[key] ?? false;
    private static string Json(object value) => Safe(JsonSerializer.SerializeToNode(value)).ToJsonString();

    // Panel replies can include generated passwords. Never forward credentials or raw secret replies.
    internal static JsonNode Safe(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj)
            {
                bool secretFlag = pair.Key == "secret" && pair.Value is JsonValue flag && flag.TryGetValue<bool>(out _);
                result[pair.Key] = EditorCapabilities.Sensitive(pair.Key) && !secretFlag ? JsonValue.Create("[redacted]") : Safe(pair.Value);
            }
            return result;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(Safe).ToArray());
        if (node is JsonValue value && value.TryGetValue<string>(out string text)) return JsonValue.Create(LogText.Redact(text));
        return node?.DeepClone();
    }

    internal static string ArgumentsForLog(string name, JsonNode args)
    {
        JsonNode safe = Safe(args);
        if (safe is JsonObject obj)
        {
            if (name == "admin_settings_edit" && obj.ContainsKey("value")) obj["value"] = "[redacted]";
            if (name == "admin_command" && EditorCapabilities.Sensitive(Text(args, "text"))) obj["text"] = "[redacted credential command]";
        }
        return safe?.ToJsonString() ?? "{}";
    }

    private static JsonObject Schema(params (string Name, string Type)[] fields)
    {
        var properties = new JsonObject();
        foreach (var f in fields) properties[f.Name] = new JsonObject { ["type"] = f.Type };
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
    }

    private static IEnumerable<Node> Nodes(Node root)
    {
        yield return root;
        foreach (Node child in root.GetChildren()) foreach (Node node in Nodes(child)) yield return node;
    }

    private static IEnumerable<Button> Tabs()
    {
        var world = Nodes(EditorInterface.Singleton.GetBaseControl()).OfType<Button>().FirstOrDefault(b => b.Text == GuoEditorPlugin.WorldTabName && b.ToggleMode && b.GetParent() is HBoxContainer);
        return world?.GetParent().GetChildren().OfType<Button>() ?? Enumerable.Empty<Button>();
    }

    internal static void Register(AiToolHost host, SearchContext ctx, Func<AdminView> admin = null)
    {
        admin ??= () => GuoEditorPlugin.AdminMain;
        AdminView View() => admin() ?? throw new InvalidOperationException("Admin tab is not available");
        void Add(string name, string description, Func<JsonNode, string> run, bool read, params (string, string)[] fields) =>
            host.Register(new AiToolHost.Tool { Name = name, Description = description, Parameters = Schema(fields), ReadOnly = read, Run = run, MaxResult = 64000 });
        string Accepted(bool accepted, string blocker = null) => Json(new { accepted, blocker, completion = "Poll admin_state for replies and operation phases; acceptance is not completion." });

        Add("editor_interface", "Read editor window geometry, main-screen tabs, run-bar layout and server/client choices. Does not read chat or editable credential fields.", _ =>
        {
            var window = EditorInterface.Singleton.GetBaseControl().GetWindow();
            return Json(new
            {
                window = new { width = window.Size.X, height = window.Size.Y, scale = EditorInterface.Singleton.GetEditorScale(), mode = window.Mode.ToString(), headless = DisplayServer.GetName() == "headless", noFocus = DisplayServer.GetName() == "headless" || window.GetFlag(Window.Flags.NoFocus) },
                tabs = Tabs().Where(b => b.Text.Length > 0).Select(b => new { name = b.Text, active = b.ButtonPressed, width = b.Size.X }),
                runBar = ctx.Run == null ? null : new { height = ctx.Run.Size.Y, outside = ctx.Run.Outside(new Rect2(Vector2.Zero, (Vector2)window.Size / window.ContentScaleFactor)), selectedServer = ctx.Run.SelectedServer?.Id, selectedClient = ctx.Run.EffectiveClient?.Id, warning = ctx.Run.Warning },
                servers = ctx.Run?.Servers.Select(s => new { id = s.Id, name = s.Name, host = s.Host, port = s.Port }),
            });
        }, true);
        Add("editor_window_resize", "Resize this editor to logical pixels without taking desktop focus; keeps the window windowed, non-topmost and NoFocus. Requires approval or an explicit scripted allowlist.", a =>
        {
            int width = Number(a, "width"), height = Number(a, "height");
            if (width < 640 || width > 4096 || height < 480 || height > 2160) throw new ArgumentException("Use width 640..4096 and height 480..2160");
            var window = EditorInterface.Singleton.GetBaseControl().GetWindow();
            window.SetFlag(Window.Flags.NoFocus, true); window.AlwaysOnTop = false;
            window.Mode = Window.ModeEnum.Windowed;
            float scale = EditorInterface.Singleton.GetEditorScale();
            window.Size = new Vector2I((int)(width * scale), (int)(height * scale));
            return Json(new { accepted = true, width, height, scale, completion = "Read editor_interface after layout settles." });
        }, false, ("width", "integer"), ("height", "integer"));
        Add("editor_navigate", "Open a registered main-screen tab by a name returned by editor_interface; changes no desktop focus or world data.", a =>
        {
            string tab = Text(a, "tab");
            if (!Tabs().Any(b => b.Text == tab)) throw new ArgumentException("Unknown editor tab; read editor_interface");
            EditorInterface.Singleton.SetMainScreenEditor(tab);
            return Json(new { accepted = true, tab });
        }, false, ("tab", "string"));
        Add("editor_server_select", "Select a saved server id and its default client through the run bar.", a =>
        {
            string id = Text(a, "id");
            if (ctx.Run == null || !ctx.Run.Servers.Any(s => s.Id == id)) throw new ArgumentException("Unknown server id; read editor_interface");
            ctx.Run.SelectServer(id); return Json(new { selected = ctx.Run.SelectedServer?.Id });
        }, false, ("id", "string"));

        Add("admin_state", "Read paged Admin health/godview/settings/accounts/backups/commands state, reply counters and completion phases. Credentials are redacted; settings secrets expose no values.", a => State(View(), a), true, ("section", "string"), ("offset", "integer"), ("limit", "integer"));
        Add("admin_view", "Show the Admin tab and one section: godview, settings, accounts, backups or commands. No desktop input is injected.", a =>
        {
            var v = View(); string section = Text(a, "section");
            switch (section)
            {
                case "godview": v.ShowSettings(false); break;
                case "settings": v.ShowSettings(true); break;
                case "accounts": v.ShowAccounts(); break;
                case "backups": v.ShowBackups(); break;
                case "commands": v.ShowCommands(); break;
                default: throw new ArgumentException("Unknown Admin section");
            }
            v.MakeVisible(); return Json(new { accepted = true, section });
        }, false, ("section", "string"));
        Add("admin_connect", "Connect the Admin tab using its selected server/bridge configuration and workspace token. Returns dispatch only; poll admin_state for granted access.", _ => Accepted(View().Connect(quiet: true)), false);
        Add("admin_refresh", "Request fresh health, godview, accounts, backups or commands data from the connected Admin bridge. Poll reply counters in admin_state.", a =>
        {
            var v = View(); bool sent = Text(a, "section") switch
            {
                "health" => v.RequestStatus(), "godview" => v.GodView.Watch(v.GodView.Facet < 0 ? 0 : v.GodView.Facet),
                "accounts" => v.Accounts.RequestList(), "backups" => v.Backups.RequestList(), "commands" => v.Commands.RequestList(),
                _ => throw new ArgumentException("Unknown Admin section"),
            };
            return Accepted(sent, sent ? null : v.AdminProblem ?? "Connect the Admin channel first");
        }, true, ("section", "string"));
        Add("admin_save", "Save the connected shard's world through Admin. Requires approval; poll Saving, LastSave and statusReplies in admin_state.", _ => Accepted(View().SaveNow()), false);
        Add("admin_restart", "Save and restart only the server managed by this run bar, then reconnect. Existing restart blockers apply; poll restarting and lastRestartSeconds.", _ => { var v = View(); return Accepted(v.Restart(), v.RestartBlocker()); }, false);

        Add("admin_godview", "Use godview watch/find/select/filter/act_as or an enabled action button: Go there, Bring here, Open paperdoll, Follow, Stop following, Respawn, Clear. Uses existing UI and bridge validation.", a =>
        {
            var g = View().GodView; bool sent;
            switch (Text(a, "action"))
            {
                case "watch": int facet = Number(a, "facet"); if (facet < 0 || facet > 5) throw new ArgumentException("facet must be 0..5"); sent = g.Watch(facet); break;
                case "find": sent = g.Find(Text(a, "text")); break;
                case "select": long serial = (long?)a?["serial"] ?? 0; if (serial <= 0 || serial > uint.MaxValue) throw new ArgumentException("Invalid serial"); sent = g.Select((uint)serial); break;
                case "filter": string kind = Text(a, "kind"); if (kind is not ("player" or "npc" or "spawner")) throw new ArgumentException("Unknown kind"); g.SetFilter(kind, Flag(a, "enabled")); sent = true; break;
                case "act_as": sent = g.SetActAs(Text(a, "text")); break;
                case "press": sent = g.Press(Text(a, "text")); break;
                default: throw new ArgumentException("Unknown godview action");
            }
            return Accepted(sent, sent ? null : g.StatusText);
        }, false, ("action", "string"), ("facet", "integer"), ("serial", "integer"), ("kind", "string"), ("enabled", "boolean"), ("text", "string"));
        Add("admin_account", "Select/create/set_access/reset_password/ban/unban an account through the Accounts panel. Owner and level restrictions apply. Passwords come only from an environment variable named GUO_ADMIN_AUTOMATION_* in the editor process, never MCP arguments.", a =>
        {
            var accounts = View().Accounts; string action = Text(a, "action"), account = Text(a, "account"); bool sent;
            switch (action)
            {
                case "select": accounts.SelectAccount(account); sent = true; break;
                case "create": sent = accounts.Create(account, Text(a, "level"), Secret(a), false); break;
                case "set_access": sent = accounts.SetAccess(account, Text(a, "level")); break;
                case "reset_password": sent = accounts.ResetPassword(account, Secret(a), false); break;
                case "ban": sent = accounts.Ban(account, true); break;
                case "unban": sent = accounts.Ban(account, false); break;
                default: throw new ArgumentException("Unknown account action");
            }
            return Accepted(sent, sent ? null : accounts.StatusText);
        }, false, ("action", "string"), ("account", "string"), ("level", "string"), ("secret_env", "string"));
        Add("admin_settings_edit", "Reload or edit the existing Admin settings form. Reload refuses unsaved edits. Secret fields require secret_env; non-secret fields use value. Does not save files or restart.", a =>
        {
            var panel = View().Settings;
            if (Text(a, "action") == "reload")
            {
                if (panel.Settings?.Diff().Count > 0) throw new InvalidOperationException("Settings have unsaved edits; save them before reloading");
                return Accepted(panel.Reload(), panel.Problem);
            }
            if (Text(a, "action") != "set") throw new ArgumentException("action must be reload or set");
            var field = panel.Settings?.Field(Text(a, "id")) ?? throw new ArgumentException("Unknown field; read admin_state section=settings");
            if (field.IsSecret && a?["value"] != null) throw new ArgumentException("Use secret_env for secret fields");
            bool changed = panel.SetValue(field.Id, field.IsSecret ? Secret(a) : Text(a, "value"));
            return Json(new { accepted = changed, problems = panel.Problems(), blocker = panel.SaveBlocker() });
        }, false, ("action", "string"), ("id", "string"), ("value", "string"), ("secret_env", "string"));
        Add("admin_settings_save", "Commit the existing settings diff through the Settings panel, preserving previous files and saving/restarting when connected. Existing validation and managed-server blockers apply.", _ => { var p = View().Settings; return Accepted(p.Save(), p.SaveBlocker()); }, false);
        Add("admin_backup_create", "Back up the connected shard with the existing retention policy. Optional keep uses the Backups panel's validation. Poll backups replies and LastReply.", a => { var p = View().Backups; if (a?["keep"] != null) p.SetKeep(Number(a, "keep")); return Accepted(p.BackUpNow(), p.BackupBlocker()); }, false, ("keep", "integer"));
        Add("admin_backup_restore", "Restore a named backup using the existing before-restore snapshot and managed restart flow. Requires approval and confirm equal to name; poll restoring, restoresFinished and lastRestoreError.", a =>
        {
            string name = Text(a, "name"); if (name.Length == 0 || Text(a, "confirm") != name) throw new ArgumentException("confirm must exactly match the backup name");
            var p = View().Backups; return Accepted(p.Restore(name), p.RestoreBlocker(name));
        }, false, ("name", "string"), ("confirm", "string"));
        Add("admin_command", "Search or run the Commands palette. Run uses existing level and dangerous-command confirmation rules. Credential commands are refused; use the account/settings tools with secret_env instead. Poll commands replies/output.", a =>
        {
            var p = View().Commands; string text = Text(a, "text");
            if (EditorCapabilities.Sensitive(text)) throw new ArgumentException("Credential commands are not exposed");
            if (Text(a, "action") == "search") { p.Search(text); return Json(new { accepted = true, shown = p.Shown.Take(50) }); }
            if (Text(a, "action") != "run") throw new ArgumentException("action must be search or run");
            p.RunAs(Text(a, "as")); return Accepted(p.Run(text, Text(a, "confirm")), p.StatusText);
        }, false, ("action", "string"), ("text", "string"), ("as", "string"), ("confirm", "string"));
    }

    private static string Secret(JsonNode a)
    {
        string name = Text(a, "secret_env");
        if (!name.StartsWith("GUO_ADMIN_AUTOMATION_", StringComparison.Ordinal) || name.Length > 96 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("secret_env must name a GUO_ADMIN_AUTOMATION_* environment variable");
        return System.Environment.GetEnvironmentVariable(name) ?? throw new ArgumentException("The editor process has no value for secret_env");
    }

    private static string State(AdminView v, JsonNode a)
    {
        string section = Text(a, "section");
        int offset = Math.Max(0, Number(a, "offset")), limit = Math.Clamp(Number(a, "limit", 25), 1, 50);
        object Page<T>(IEnumerable<T> rows) => new { total = rows.Count(), offset, rows = rows.Skip(offset).Take(limit).ToArray() };
        object data = section switch
        {
            "" or "health" => new { v.Connected, v.Granted, v.AdminProblem, v.StatusReplies, v.LastStatus, v.Saving, v.LastSave, restarting = v.Restarting.ToString(), v.LastRestartSeconds, restartBlocker = v.RestartBlocker() },
            "godview" => new { v.GodView.Facet, v.GodView.Selected, v.GodView.FullReplies, v.GodView.Pushes, v.GodView.ActionReplies, v.GodView.LastAction, v.GodView.LastFind, v.GodView.Following, v.GodView.ShowPlayers, v.GodView.ShowNpcs, v.GodView.ShowSpawners, actions = v.GodView.ActionsEnabled, page = Page(v.GodView.Rows.OrderBy(r => r.Key).Select(r => r.Value)) },
            "accounts" => new { v.Accounts.Lists, v.Accounts.Replies, v.Accounts.Selected, v.Accounts.StatusText, v.Accounts.LastReply, page = Page(v.Accounts.Rows) },
            "backups" => new { v.Backups.Lists, v.Backups.Replies, v.Backups.Keep, v.Backups.Selected, v.Backups.LastReply, restoring = v.Backups.Restoring.ToString(), v.Backups.RestoresFinished, v.Backups.LastRestored, v.Backups.LastRestoreError, page = Page(v.Backups.Rows) },
            "commands" => new { v.Commands.Lists, v.Commands.Replies, v.Commands.StatusText, v.Commands.LastReply, v.Commands.OutputAvailable, output = LogText.Redact(v.Commands.OutputText), page = Page(v.Commands.Rows) },
            "settings" => new { v.Settings.Problem, v.Settings.Replies, v.Settings.SavePending, v.Settings.LastWriteError, v.Settings.LiveMatches, problems = v.Settings.Problems(), changes = v.Settings.ChangesText, page = Page(v.Settings.Settings?.Fields.Select(f => new { id = f.Id, label = f.Label, type = f.Type, secret = f.IsSecret, value = f.IsSecret ? "[redacted]" : v.Settings.Settings.Get(f) }) ?? Enumerable.Empty<object>()) },
            _ => throw new ArgumentException("Unknown Admin section"),
        };
        return Json(new { section = section.Length == 0 ? "health" : section, data });
    }
}
#endif
