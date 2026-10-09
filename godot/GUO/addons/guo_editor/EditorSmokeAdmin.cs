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
/// The Admin tab's scripted run (AD1): the run bar starts the private shard named on the command line, the tab
/// connects on the admin channel, reads Health, saves, restarts through the run bar and reads Health again; then
/// the run bar stops the server. tools/editor_shard/run.py admin-tab starts it, in a scratch workspace.
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
                    AdminNext(124);
                }
                else AdminTimeout("the server did not come back after Restart");

                break;

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
