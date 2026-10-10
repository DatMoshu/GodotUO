#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using GUO.Workspace;

/// <summary>
/// The Admin tab's scripted run (AD1, AD2a, AD2b): the run bar starts the private shard named on the command line, the tab
/// connects on the admin channel, reads Health, saves, restarts through the run bar and reads Health again. Then the
/// god view: it watches the facet again after the restart, a spawner put through the bridge arrives in a change-only
/// push with the creatures it spawned, Find finds it, a filter hides the NPCs, its Respawn and Clear buttons (AD2b) replace and remove the horses, and its
/// delete removes it.
/// Last the run bar stops the server. tools/editor_shard/run.py admin-tab starts it, in a scratch workspace.
/// </summary>
public partial class EditorSmoke
{
    /// <summary>The private shard's folder (tools/editor_shard's home): the run bar's "Private shard" profile.</summary>
    private const string AdminFlag = "--guo-editor-admin";

    private const double AdminStepSeconds = 300;

    private readonly Dictionary<string, object> _admin = new();
    private AdminView _adminView;
    private double _adminClock, _adminRetry, _adminFrameClock;
    private int _adminFrames;
    private int _adminMark, _adminShotFrames = -1;
    private string _adminShot;
    private int _godFullBefore, _godFullAtPut, _godPushesBefore;
    private Guid _godSpawner;
    private uint _godSpawnerSerial;
    private HashSet<uint> _godHorsesBefore = new();
    private int _godActionMark;

    // The god view's test spawner: three horses west of Britain on Felucca, where the live-objects smoke puts its own.
    private const string GodSpawnerName = "GUO Horse";

    private void StepAdmin()
    {
        AdminView v = _adminView ??= GuoEditorPlugin.AdminMain;
        if (v?.Run == null || v.Run.Servers.Count == 0)
        {
            if (_elapsed > 60) AdminFail("the Admin tab or the run bar never appeared");
            return;
        }

        AdminClipFrame();

        // A still of the tab, a few frames after the state it shows.
        if (_adminShotFrames >= 0)
        {
            if (_adminShotFrames-- == 0) AdminCapture(_adminShot);
            return;
        }

        RunBar run = v.Run;
        switch (_stage)
        {
            case 120:
            {
                string home = Path.GetFullPath(ArgValue(AdminFlag));
                ServerProfile s = run.Servers.FirstOrDefault(p => !string.IsNullOrEmpty(p.ServerDirectory)
                    && string.Equals(Path.GetFullPath(p.ServerDirectory).TrimEnd('\\', '/'), home.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (s == null)
                {
                    AdminFail($"no run-bar profile for {home} (the run bar adds one when tools/editor_shard set it up)");
                    return;
                }

                run.SelectServer(s.Id);
                _admin["server"] = s.Name;
                _admin["offline_plain_words"] = v.Describe().Contains("Not connected");
                _admin["offline_restart_blocked"] = v.RestartBlocker() != null;
                _admin["managed_before"] = run.SelectedManaged;
                if (!run.SelectedManaged)
                {
                    run.StartServerNow();
                }

                _admin["started_by_run_bar"] = run.SelectedManaged;
                if (!Headless) v.MakeVisible();
                AdminNext(121);
                break;
            }

            case 121:
                // The shard takes a while to load the world; the bridge answers once it has.
                if (!v.Connected && (_adminRetry += GetProcessDeltaTime()) >= 2)
                {
                    _adminRetry = 0;
                    int port = int.Parse(ShardSecrets.BridgeEnvironment(run.SelectedServer.ServerDirectory).GetValueOrDefault("GUO_BRIDGE_PORT", "2595"));
                    v.Connect("127.0.0.1", port, quiet: true);
                }

                if (v.Granted != null && v.StatusReplies > 0)
                {
                    _admin["granted"] = v.Granted;
                    _admin["status_first"] = Plain(v.LastStatus);
                    _admin["plain_words"] = v.PlainText;
                    _admin["restart_enabled"] = v.RestartBlocker() == null;
                    _godFullBefore = v.GodView.FullReplies;
                    AdminShot("connected");
                    AdminNext(122);
                    // The status Save now asks for afterwards is the one after this mark.
                    _adminMark = v.StatusReplies;
                    v.SaveNow();
                }
                else AdminTimeout("the Admin tab never got the admin channel and a status");

                break;

            case 122:
                if (!v.Saving && v.LastSave != null && v.StatusReplies > _adminMark)
                {
                    _admin["save"] = Plain(v.LastSave);
                    _admin["status_after_save"] = Plain(v.LastStatus);
                    AdminShot("saved");
                    _adminMark = v.StatusReplies;
                    _admin["restart_called"] = v.Restart();
                    AdminNext(123);
                }
                else AdminTimeout("Save now never answered");

                break;

            case 123:
                if (v.Restarting == AdminView.RestartPhase.None && v.Granted != null && v.LastRestartSeconds > 0 && v.StatusReplies > _adminMark)
                {
                    _admin["restart_s"] = v.LastRestartSeconds;
                    _admin["status_after_restart"] = Plain(v.LastStatus);
                    _admin["managed_after_restart"] = run.SelectedManaged;
                    AdminShot("restarted");
                    AdminNext(125);
                }
                else AdminTimeout("the server did not come back after Restart");

                break;

            case 125:
                // The god view watched the facet again by itself once the restarted server gave the channel back.
                if (v.GodView.FullReplies > _godFullBefore && v.GodView.Rows.Count > 0)
                {
                    GodViewPanel g = v.GodView;
                    _admin["godview_resubscribed"] = true;
                    _admin["godview_first"] = new Dictionary<string, object>
                    {
                        ["facet"] = g.FacetName(g.Facet), ["rows"] = g.Rows.Count, ["status"] = g.StatusText,
                        ["players"] = g.Rows.Values.Count(r => (string)r["kind"] == "player"),
                        ["npcs"] = g.Rows.Values.Count(r => (string)r["kind"] == "npc"),
                        ["spawners"] = g.Rows.Values.Count(r => (string)r["kind"] == "spawner"),
                    };
                    _godPushesBefore = g.Pushes;
                    _godFullAtPut = g.FullReplies;
                    _godSpawner = Guid.NewGuid();
                    g.Send(new JsonObject
                    {
                        ["op"] = "object", ["action"] = "put", ["kind"] = "spawner",
                        ["object"] = new JsonObject
                        {
                            ["id"] = _godSpawner.ToString(), ["map"] = "Felucca", ["x"] = LiveSpawnerX, ["y"] = LiveSpawnerY, ["z"] = 0,
                            ["count"] = 3, ["home_range"] = 4, ["entries"] = new JsonArray(new JsonObject { ["name"] = "Horse", ["max"] = 3 }),
                        },
                    });
                    AdminShot("godview");
                    AdminNext(126);
                }
                else AdminTimeout("the god view did not show the facet after the restart");

                break;

            case 126:
            {
                // The spawner and its horses arrive in a push, not in a new full list.
                GodViewPanel g = v.GodView;
                JsonObject spawner = g.Rows.Values.FirstOrDefault(r => (string)r["kind"] == "spawner" && (string)r["name"] == GodSpawnerName
                    && (int)r["x"] == LiveSpawnerX && (int)r["y"] == LiveSpawnerY);
                int horses = spawner == null ? 0 : g.Rows.Values.Count(r => (uint?)r["spawner"] == (uint)spawner["serial"]);
                if (spawner != null && horses > 0 && g.Pushes > _godPushesBefore)
                {
                    _godSpawnerSerial = (uint)spawner["serial"];
                    _admin["godview_push"] = new Dictionary<string, object>
                    {
                        ["pushes"] = g.Pushes - _godPushesBefore, ["full_lists"] = g.FullReplies - _godFullAtPut,
                        ["last_push_rows"] = g.LastPushUpserts, ["rows"] = g.Rows.Count, ["horses"] = horses,
                        ["spawner"] = spawner.ToJsonString(),
                    };
                    g.Select(_godSpawnerSerial);
                    _admin["godview_details"] = g.DetailsText;
                    g.Find(GodSpawnerName);
                    AdminShot("spawner");
                    AdminNext(127);
                }
                else AdminTimeout("the spawner put through the bridge never reached the god view with its creatures");

                break;
            }

            case 127:
            {
                GodViewPanel g = v.GodView;
                if (g.LastFind is { } found && (string)found["text"] == GodSpawnerName)
                {
                    _admin["godview_find"] = found.ToJsonString();
                    _admin["godview_found"] = found["matches"] is JsonArray m && m.Any(x => (uint)x["serial"] == _godSpawnerSerial && (int)x["facet"] == 0);
                    g.SetFilter("npc", false);
                    _admin["godview_filter_status"] = g.StatusText;
                    _admin["godview_filtered"] = !g.ShowNpcs && g.StatusText.Contains("hiding NPCs");
                    AdminShot("filtered");
                    AdminNext(129);
                }
                else AdminTimeout("Find never answered");

                break;
            }

            case 129:
            {
                // AD2b: the spawner's Respawn button, through the tab. With the spawner selected Go there, Respawn and
                // Clear are on (Bring here, the paperdoll and Follow take a mobile); with no staff character online the
                // hint says Go there needs one, for a horse and for the spawner alike.
                GodViewPanel g = v.GodView;
                if (!_admin.ContainsKey("godview_respawn_sent"))
                {
                    g.SetFilter("npc", true);
                    g.Select(_godSpawnerSerial);
                    _godHorsesBefore = g.Rows.Values.Where(r => (uint?)r["spawner"] == _godSpawnerSerial).Select(r => (uint)r["serial"]).ToHashSet();
                    _admin["godview_spawner_buttons"] = g.ActionsEnabled.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                    _admin["godview_spawner_buttons_ok"] = g.ActionsEnabled.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(k => k)
                        .SequenceEqual(new[] { "Clear", "Go there", "Respawn" }) && g.ActionHint.Contains("No staff character is online");
                    g.Select(g.Rows.Values.Where(r => (uint?)r["spawner"] == _godSpawnerSerial).Select(r => (uint)r["serial"]).FirstOrDefault());
                    _admin["godview_npc_hint"] = g.ActionHint;
                    _admin["godview_npc_hint_ok"] = g.ActionHint.Contains("No staff character is online") && g.StaffOnline.Count == 0;
                    g.Select(_godSpawnerSerial);
                    _godActionMark = g.ActionReplies;
                    _admin["godview_respawn_sent"] = g.Press("Respawn");
                    AdminShot("respawn");
                    break;
                }

                HashSet<uint> now = g.Rows.Values.Where(r => (uint?)r["spawner"] == _godSpawnerSerial).Select(r => (uint)r["serial"]).ToHashSet();
                if (g.ActionReplies > _godActionMark && (bool?)g.LastAction?["ok"] == true && now.Count > 0 && !now.Overlaps(_godHorsesBefore))
                {
                    _admin["godview_respawn"] = g.LastAction.ToJsonString();
                    _admin["godview_respawned"] = true;
                    _godActionMark = g.ActionReplies;
                    _admin["godview_clear_sent"] = g.Press("Clear");
                    AdminNext(130);
                }
                else AdminTimeout("Respawn never answered with new creatures in the god view");

                break;
            }

            case 130:
            {
                GodViewPanel g = v.GodView;
                bool none = !g.Rows.Values.Any(r => (uint?)r["spawner"] == _godSpawnerSerial);
                if (g.ActionReplies > _godActionMark && (bool?)g.LastAction?["ok"] == true && none)
                {
                    _admin["godview_clear"] = g.LastAction.ToJsonString();
                    _admin["godview_cleared"] = g.Rows.ContainsKey(_godSpawnerSerial);
                    AdminShot("cleared");
                    AdminNext(128);
                }
                else AdminTimeout("Clear never answered, or the creatures stayed in the god view");

                break;
            }

            case 128:
            {
                GodViewPanel g = v.GodView;
                if (_admin.ContainsKey("godview_deleted_sent"))
                {
                    if (!g.Rows.ContainsKey(_godSpawnerSerial) && !g.Rows.Values.Any(r => (uint?)r["spawner"] == _godSpawnerSerial))
                    {
                        _admin["godview_removed"] = true;
                        _admin["godview_rows_end"] = g.Rows.Count;
                        AdminNext(124);
                    }
                    else AdminTimeout("the deleted spawner and its creatures never left the god view");
                }
                else
                {
                    g.SetFilter("npc", true);
                    g.Send(new JsonObject { ["op"] = "object", ["action"] = "delete", ["kind"] = "spawner", ["id"] = _godSpawner.ToString() });
                    _admin["godview_deleted_sent"] = true;
                }

                break;
            }

            case 124:
                run.StopSelectedNow();
                _admin["stopped"] = !run.SelectedManaged;
                AdminFinish();
                break;
        }
    }

    private void AdminNext(int stage)
    {
        _stage = stage;
        _adminClock = _elapsed;
        _adminRetry = 2;
    }

    private void AdminTimeout(string what)
    {
        if (_elapsed - _adminClock > AdminStepSeconds) AdminFail($"{what} within {AdminStepSeconds:0} s");
    }

    private void AdminShot(string name)
    {
        if (Headless) return;
        // The editor restores its own main screen after the plugins load: bring the tab forward for each still.
        _adminView?.MakeVisible();
        _adminShot = name;
        _adminShotFrames = 20;
    }

    private void AdminCapture(string name)
    {
        Image frame = GetViewport()?.GetTexture()?.GetImage();
        if (frame == null || frame.IsEmpty())
        {
            _failures.Add($"Admin tab: no editor frame for '{name}'");
            return;
        }

        string file = Path.Combine(_out, $"admin_{name}.png");
        frame.SavePng(file);
        _admin[$"still_{name}"] = file;
    }

    // A windowed run keeps four frames a second of the editor, for a clip of the whole run (admin_tab.py makes it).
    private void AdminClipFrame()
    {
        if (Headless || _stage < 121 || (_adminFrameClock += GetProcessDeltaTime()) < 0.25) return;
        _adminFrameClock = 0;
        Image frame = GetViewport()?.GetTexture()?.GetImage();
        if (frame == null || frame.IsEmpty()) return;
        string dir = Path.Combine(_out, "clip_frames");
        Directory.CreateDirectory(dir);
        frame.SavePng(Path.Combine(dir, $"f_{_adminFrames++:D4}.png"));
    }

    private static Dictionary<string, object> Plain(JsonNode n) =>
        n is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => (object)kv.Value?.ToJsonString()) : null;

    private void AdminFail(string why)
    {
        _failures.Add("Admin tab: " + why);
        try { _adminView?.Run?.StopSelectedNow(); _admin["stopped"] = _adminView?.Run?.SelectedManaged == false; } catch (Exception e) { _failures.Add("Admin tab: stopping the server: " + e.Message); }
        AdminFinish();
    }

    private void AdminFinish()
    {
        AdminView v = _adminView;
        if (v != null)
        {
            _admin["log"] = v.LogText;
            // The token must never reach the tab's log or the editor's output.
            string token = ShardSecrets.BridgeAdminToken();
            _admin["token_in_tab_log"] = !string.IsNullOrEmpty(token) && v.LogText.Contains(token);
            if ((bool)_admin["token_in_tab_log"]) _failures.Add("Admin tab: the admin token is in the tab's log");
        }

        Check("offline_plain_words", "the tab, not connected, says so in plain words");
        Check("offline_restart_blocked", "Restart is off before the run bar starts the server");
        Check("started_by_run_bar", "the run bar started the server");
        Check("restart_enabled", "Restart is on for a server the run bar started");
        Check("managed_after_restart", "the run bar still manages the server after Restart");
        Check("godview_resubscribed", "the god view watched the facet again after the restart");
        Check("godview_found", "Find found the new spawner on Felucca");
        Check("godview_filtered", "the NPCs filter hid the NPCs and the status line says so");
        Check("godview_spawner_buttons_ok", "with a spawner selected Go there, Respawn and Clear are on, and the hint says Go there needs a staff character");
        Check("godview_npc_hint_ok", "with no staff character online the hint says why Go there and the rest are off");
        Check("godview_respawned", "Respawn, pressed in the tab, replaced the horses with new ones in the god view");
        Check("godview_cleared", "Clear, pressed in the tab, removed the horses and kept the spawner");
        Check("godview_removed", "the deleted spawner and its creatures left the god view");
        Check("stopped", "the run bar stopped the server at the end");
        _admin["ok"] = _failures.Count == 0;
        _report["admin"] = _admin;
        Finish();

        void Check(string key, string what)
        {
            if (_failures.Count == 0 && !(_admin.TryGetValue(key, out object val) && val is true)) _failures.Add($"Admin tab: {what}: no");
        }
    }
}
#endif
