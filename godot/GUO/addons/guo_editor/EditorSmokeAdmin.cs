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
/// The Admin tab's scripted run (AD1, AD2a, AD2b, AD3, AD4, AD5, AD6): the run bar starts the private shard named on the command line, the tab
/// connects on the admin channel, reads Health, saves, restarts through the run bar and reads Health again. Then the
/// god view: it watches the facet again after the restart, a spawner put through the bridge arrives in a change-only
/// push with the creatures it spawned, Find finds it, a filter hides the NPCs, its Respawn and Clear buttons (AD2b) replace and remove the horses, and its
/// delete removes it. Then the Settings form (AD4): it reads the shard's configuration with plain labels and its secrets
/// masked, refuses values out of range, and Save and restart writes two settings and a mail password, keeping the
/// previous files; the restarted server reports the new values and the password lands in the scratch secrets file.
/// Then the Accounts list (AD5): every account listed, the owner out of reach, one made with a generated 16-character
/// password, given a level and a typed password, banned and unbanned. Then the Backups list (AD6): Back up now keeps a
/// snapshot and Health shows it, an account made afterwards is gone once that snapshot is restored (the server restarted
/// by the run bar, a before-restore snapshot kept), and keep 2 removes the oldest. Then the Commands palette (AD3): the
/// server's commands listed and searched, the help line for the one typed, [where run with nobody online and its output
/// shown, [wipe held for its typed word and run with it (it only asks for a target, cancelled), the history, and "run as"
/// a character who is not online refused. Last the run bar stops the server. tools/editor_shard/run.py admin-tab starts it, in a scratch workspace.
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
    private string _setSecret;
    private int _setMaxAccounts;
    private string _accName, _accGenerated, _accTyped;
    private int _accMark;
    private int _godSettlePushes = -1, _godSettleFull = -1;
    private double _godSettleClock;
    private string _bakFirst, _bakAccount;
    private int _bakMark, _bakKeepBefore;
    private int _cmdMark;

    // The settings the form's check changes, by field id ("file:path").
    private const string SetMaxAccounts = "modernuo:settings/accountHandler.maxAccountsPerIP";
    private const string SetSaveDelay = "modernuo:settings/autosave.saveDelay";
    private const string SetMailPassword = "email:emailPassword";
    private const string SetSecretVariable = "GUO_SMOKE_SETTINGS_SECRET";

    // The Accounts list's check (AD5): a typed password from admin_tab.py, so it can look for it in the server's logs too.
    private const string AccTypedVariable = "GUO_SMOKE_ACCOUNT_SECRET";

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
                    // Then the first change-only push after that full list, so the subscription is settled before the
                    // spawner goes in (AD4's flake: a put sent at once could land in a second full list, not a push).
                    if (g.FullReplies != _godSettleFull)
                    {
                        _godSettleFull = g.FullReplies;
                        _godSettlePushes = g.Pushes;
                        _godSettleClock = _elapsed;
                        break;
                    }

                    if (g.Pushes == _godSettlePushes && _elapsed - _godSettleClock < 30)
                    {
                        break;
                    }

                    _admin["godview_settled_s"] = Math.Round(_elapsed - _godSettleClock, 1);
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
                        AdminNext(131);
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

            case 131:
            {
                // AD4: the form over the shard's own files, read afresh.
                SettingsPanel p = v.Settings;
                v.ShowSettings(true);
                if (!p.Reload())
                {
                    AdminFail("the Settings form could not read the shard: " + p.Problem);
                    return;
                }

                SettingsField max = p.Settings.Field(SetMaxAccounts);
                _admin["settings_fields"] = p.Settings.Fields.Count();
                _admin["settings_missing_files"] = string.Join(", ", p.Settings.MissingFiles);
                _admin["settings_labels_ok"] = max != null && max.Label == "Accounts per address" && p.Settings.Field(SetSaveDelay)?.Label == "Time between saves"
                    && p.Settings.Fields.Where(f => !f.IsOther).All(f => f.Label.Length > 0 && !f.Label.Contains('.'));
                _admin["settings_secrets_masked"] = p.SecretsMasked() && p.Settings.Fields.Count(f => f.IsSecret) >= 3;
                _admin["settings_status_form"] = p.StatusText;
                _setMaxAccounts = int.Parse(p.Settings.Get(max) ?? "1", System.Globalization.CultureInfo.InvariantCulture);
                _admin["settings_max_accounts_before"] = _setMaxAccounts;
                _admin["settings_save_delay_before"] = p.Settings.Get(p.Settings.Field(SetSaveDelay));

                // Out of range and not a time: both marked, and nothing can be saved.
                p.SetValue(SetMaxAccounts, "0");
                p.SetValue(SetSaveDelay, "soon");
                Dictionary<string, string> problems = p.Problems();
                _admin["settings_problems"] = problems.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                _admin["settings_invalid_refused"] = problems.ContainsKey(SetMaxAccounts) && problems.ContainsKey(SetSaveDelay)
                    && p.SaveBlocker() != null && !p.Save() && p.LastWrite == null;
                AdminShot("settings-form");
                AdminNext(132);
                break;
            }

            case 132:
            {
                // Good values: one fewer account per address (at least one), saves every 7 minutes, and a mail password.
                SettingsPanel p = v.Settings;
                _setSecret = System.Environment.GetEnvironmentVariable(SetSecretVariable);
                if (string.IsNullOrEmpty(_setSecret))
                {
                    AdminFail($"{SetSecretVariable} is not set (tools/editor_shard/run.py admin-tab sets it, to look for it in the logs after)");
                    return;
                }

                int target = _setMaxAccounts > 1 ? _setMaxAccounts - 1 : _setMaxAccounts + 1;
                p.SetValue(SetMaxAccounts, target.ToString(System.Globalization.CultureInfo.InvariantCulture));
                p.SetValue(SetSaveDelay, "00:07:00");
                p.SetValue(SetMailPassword, _setSecret);
                _admin["settings_max_accounts_target"] = target;
                _admin["settings_diff"] = p.ChangesText;
                _admin["settings_diff_ok"] = p.Problems().Count == 0 && p.Settings.Diff().Count == 3
                    && p.ChangesText.Contains("Accounts per address") && p.ChangesText.Contains("00:07:00")
                    && p.ChangesText.Contains("Mail password") && p.ChangesText.Contains("kept in your secrets file")
                    && !p.ChangesText.Contains(_setSecret);
                _admin["settings_save_enabled"] = p.SaveBlocker() == null;
                AdminShot("settings-diff");
                AdminNext(133);
                break;
            }

            case 133:
            {
                SettingsPanel p = v.Settings;
                if (!_admin.ContainsKey("settings_save_called"))
                {
                    _admin["settings_save_called"] = p.Save();
                    break;
                }

                if (!p.SavePending && p.LiveMatches != null && v.Restarting == AdminView.RestartPhase.None && v.Granted != null)
                {
                    _admin["settings_live_matches"] = p.LiveMatches == true;
                    _admin["settings_live"] = p.LastLive?.ToJsonString();
                    _admin["settings_written"] = p.LastWrite == null ? null : string.Join(", ", p.LastWrite.Files);
                    _admin["settings_write_error"] = p.LastWriteError;
                    string home = run.SelectedServer.ServerDirectory;
                    string previous = p.LastWrite?.PreviousFolder;
                    _admin["settings_previous"] = previous == null ? null : Path.GetRelativePath(home, previous);
                    string[] kept = previous != null && Directory.Exists(previous) ? Directory.GetFiles(previous) : Array.Empty<string>();
                    _admin["settings_previous_files"] = string.Join(", ", kept.Select(Path.GetFileName));
                    _admin["settings_previous_ok"] = kept.Any(f => Path.GetFileName(f) == "modernuo.json") && kept.Any(f => Path.GetFileName(f) == "email-settings.json")
                        && kept.All(f => !File.ReadAllText(f).Contains(_setSecret));
                    string mail = File.ReadAllText(Path.Combine(home, "Configuration", "email-settings.json"));
                    _admin["settings_secret_in_server_file"] = mail.Contains(_setSecret);
                    _admin["settings_secret_in_secrets_file"] = ShardSecrets.ReadFile("UO_SHARD_EMAIL_PASSWORD") == _setSecret;
                    _admin["settings_form_reloaded"] = p.Settings != null && p.Settings.Diff().Count == 0
                        && p.Settings.Get(p.Settings.Field(SetSaveDelay)) == "00:07:00" && p.Settings.SecretKept(p.Settings.Field(SetMailPassword));
                    _admin["settings_status_saved"] = p.StatusText;
                    AdminShot("settings-saved");
                    AdminNext(134);
                }
                else AdminTimeout("Save and restart never came back with the server's values");

                break;
            }

            // AD5, the Accounts list: made, given a level and a typed password, banned and unbanned, all in the tab.
            case 134:
            {
                AccountsPanel p = v.Accounts;
                v.ShowAccounts();
                if (p.Lists > 0 && v.Granted != null)
                {
                    string owner = EditorData.Setting("UO_SHARD_OWNER", "");
                    _admin["accounts_listed"] = p.Total;
                    _admin["accounts_rows_ok"] = p.Rows.Count > 0 && p.Rows.All(r => r["access"] != null && r["characters"] is JsonArray && r.ContainsKey("last_login"))
                        && p.StatusText.Contains($"of {p.Total} account");
                    p.SelectAccount(owner);
                    _admin["accounts_owner_blocked"] = owner.Length > 0 && p.Selected != null && p.Blocker()?.Contains("below its own level") == true;
                    _admin["accounts_owner_hint"] = p.HintText;
                    _accName = "ad5t" + System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000);
                    _accGenerated = AccountsPanel.GeneratePassword();
                    _accTyped = System.Environment.GetEnvironmentVariable(AccTypedVariable);
                    _admin["accounts_generated_fits"] = _accGenerated.Length == AccountsPanel.MaxLength && AccountsPanel.PasswordProblem(_accGenerated) == null
                        && AccountsPanel.PasswordProblem(AccountsPanel.GeneratePassword(17)) != null;
                    _admin["accounts_name"] = _accName;
                    AdminShot("accounts-list");
                    AdminNext(141);
                }
                else AdminTimeout("the Accounts list never came");

                break;
            }

            case 135:
            case 136:
            case 137:
            case 138:
            {
                AccountsPanel p = v.Accounts;
                if (p.Replies == _accMark)
                {
                    AdminTimeout($"the server never answered the Accounts list's change (stage {_stage})");
                    break;
                }

                JsonNode r = p.LastReply;
                JsonObject row = p.Rows.FirstOrDefault(x => (string)x["name"] == _accName);
                bool ok = (bool?)r?["ok"] == true;
                _accMark = p.Replies;
                switch (_stage)
                {
                    case 135:
                        _admin["accounts_created"] = ok && (string)row?["access"] == "Player" && p.Selected == _accName && p.StatusText.Contains("made account");
                        p.SetAccess(_accName, "Counselor");
                        break;
                    case 136:
                        _admin["accounts_access_set"] = ok && (string)row?["access"] == "Counselor";
                        _admin["accounts_typed_refused_short"] = !p.ResetPassword(_accName, "short", false);
                        if (string.IsNullOrEmpty(_accTyped) || !p.ResetPassword(_accName, _accTyped, false))
                        {
                            AdminFail($"no typed password to give the account ({AccTypedVariable}), or the tab would not send it");
                            return;
                        }

                        break;
                    case 137:
                        _admin["accounts_password_set"] = ok && p.StatusText.Contains("new password");
                        AdminShot("accounts-created");
                        AdminNext(142);
                        return;
                    case 138:
                        _admin["accounts_banned"] = ok && (bool?)row?["banned"] == true && p.DetailsText.Contains("banned");
                        AdminShot("accounts-banned");
                        AdminNext(143);
                        return;
                }

                AdminNext(_stage + 1);
                break;
            }

            // The change after each still, sent once the still is taken.
            case 141:
                _accMark = v.Accounts.Replies;
                v.Accounts.Create(_accName, "Player", _accGenerated, true);
                AdminNext(135);
                break;
            case 142:
            case 143:
                v.Accounts.Ban(_accName, _stage == 142);
                AdminNext(_stage == 142 ? 138 : 139);
                break;

            case 139:
            {
                AccountsPanel p = v.Accounts;
                if (p.Replies == _accMark)
                {
                    AdminTimeout("the server never lifted the ban");
                    break;
                }

                JsonObject row = p.Rows.FirstOrDefault(x => (string)x["name"] == _accName);
                _admin["accounts_unbanned"] = (bool?)p.LastReply?["ok"] == true && (bool?)row?["banned"] == false;
                _accMark = p.Lists;
                p.RequestList();
                AdminNext(140);
                break;
            }

            case 140:
            {
                AccountsPanel p = v.Accounts;
                if (p.Lists > _accMark)
                {
                    JsonObject row = p.Rows.FirstOrDefault(x => (string)x["name"] == _accName);
                    _admin["accounts_listed_after"] = (string)row?["access"] == "Counselor" && (bool?)row?["banned"] == false;
                    AdminNext(150);
                }
                else AdminTimeout("the Accounts list never came again");

                break;
            }

            // AD6, the Backups list: Back up now, Health's last backup, a restore that undoes a change, keep N.
            case 150:
            {
                BackupsPanel p = v.Backups;
                v.ShowBackups();
                if (!_admin.ContainsKey("backups_list_sent"))
                {
                    _bakMark = p.Lists;
                    _admin["backups_list_sent"] = p.RequestList();
                    break;
                }

                if (p.Lists > _bakMark)
                {
                    _admin["backups_before"] = p.Rows.Count;
                    _bakKeepBefore = p.Keep;
                    p.SetKeep(10);
                    _bakMark = p.Replies;
                    _admin["backups_now_sent"] = p.BackUpNow();
                    AdminNext(151);
                }
                else AdminTimeout("the Backups list never came");

                break;
            }

            case 151:
            {
                BackupsPanel p = v.Backups;
                if (p.Replies > _bakMark)
                {
                    JsonNode r = p.LastReply;
                    _bakFirst = (string)r?["snapshot"]?["name"];
                    _admin["backup_first"] = r?.ToJsonString();
                    _admin["backup_now_ok"] = (bool?)r?["ok"] == true && _bakFirst != null && (int?)r["snapshot"]["files"] > 0
                        && p.Rows.Count > 0 && (string)p.Rows[0]["name"] == _bakFirst && (string)p.Rows[0]["reason"] == "manual";
                    _adminMark = v.StatusReplies;
                    v.RequestStatus();
                    AdminNext(152);
                }
                else AdminTimeout("Back up now never answered");

                break;
            }

            case 152:
                if (v.StatusReplies > _adminMark)
                {
                    _admin["backup_health"] = Plain(v.LastStatus);
                    _admin["backup_health_ok"] = (string)v.LastStatus["last_backup_name"] == _bakFirst && (long?)v.LastStatus["last_backup_s"] < 120
                        && v.PlainText.Contains("The last backup was taken");
                    v.Backups.SelectBackup(_bakFirst);
                    AdminShot("backup-done");
                    // A change after the backup: an account the restore must take away again.
                    _bakAccount = "ad6t" + System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000);
                    _accMark = v.Accounts.Replies;
                    AdminNext(153);
                }
                else AdminTimeout("Health never came after Back up now");

                break;

            case 153:
                if (!_admin.ContainsKey("backup_account_sent"))
                {
                    _admin["backup_account_sent"] = v.Accounts.Create(_bakAccount, "Player", AccountsPanel.GeneratePassword(), true);
                    break;
                }

                if (v.Accounts.Replies > _accMark)
                {
                    _admin["backup_account_made"] = (bool?)v.Accounts.LastReply?["ok"] == true && v.Accounts.Rows.Any(x => (string)x["name"] == _bakAccount);
                    v.ShowBackups();
                    v.Backups.SelectBackup(_bakFirst);
                    _admin["backup_restore_blocker"] = v.Backups.RestoreBlocker(_bakFirst);
                    v.Backups.ConfirmRestore(_bakFirst);
                    _admin["backup_confirm_shown"] = v.Backups.ConfirmOpen;
                    AdminShot("restore-confirm");
                    AdminNext(154);
                }
                else AdminTimeout("the account made after the backup never came back");

                break;

            case 154:
                // The confirm's Restore, pressed (the dialog's own button calls the same).
                v.Backups.CloseConfirm();
                _bakMark = v.Backups.RestoresFinished;
                _admin["backup_restore_sent"] = v.Backups.Restore(_bakFirst);
                AdminNext(155);
                break;

            case 155:
            {
                BackupsPanel p = v.Backups;
                if (p.RestoresFinished > _bakMark && v.Granted != null && p.Lists > 0)
                {
                    _admin["backup_restored"] = p.LastRestored == _bakFirst && p.LastRestoreError == null;
                    _admin["backup_restore_error"] = p.LastRestoreError;
                    _admin["backup_before_kept"] = p.Rows.Any(r => (string)r["reason"] == ShardBackups.BeforeRestore) && p.Rows.Any(r => (string)r["name"] == _bakFirst);
                    _admin["backup_rows_after_restore"] = string.Join(", ", p.Rows.Select(r => (string)r["name"]));
                    _accMark = v.Accounts.Lists;
                    v.Accounts.RequestList();
                    AdminNext(156);
                }
                else AdminTimeout("the restore never finished (the server did not come back)");

                break;
            }

            case 156:
                if (v.Accounts.Lists > _accMark)
                {
                    _admin["backup_world_restored"] = v.Accounts.Rows.Count > 0 && v.Accounts.Rows.All(x => (string)x["name"] != _bakAccount);
                    v.ShowBackups();
                    AdminShot("restored");
                    AdminNext(157);
                }
                else AdminTimeout("the Accounts list never came after the restore");

                break;

            case 157:
                v.Backups.SetKeep(2);
                _bakMark = v.Backups.Replies;
                _admin["backup_keep_sent"] = v.Backups.BackUpNow();
                AdminNext(158);
                break;

            case 158:
            {
                BackupsPanel p = v.Backups;
                if (p.Replies > _bakMark)
                {
                    JsonNode r = p.LastReply;
                    var pruned = (r?["pruned"] as JsonArray)?.Select(n => (string)n).ToList() ?? new List<string>();
                    _admin["backup_pruned"] = string.Join(", ", pruned);
                    _admin["backup_keep_ok"] = (bool?)r?["ok"] == true && p.Rows.Count == 2 && pruned.Contains(_bakFirst)
                        && p.Rows.Any(x => (string)x["reason"] == ShardBackups.BeforeRestore);
                    AdminShot("backup-kept");
                    AdminNext(160);
                }
                else AdminTimeout("Back up now with keep 2 never answered");

                break;
            }

            case 160:
            {
                // The palette listed the commands when the admin channel opened (again after each restart).
                CommandsPanel c = v.Commands;
                if (c.Lists > 0 && c.Rows.Count > 0)
                {
                    v.ShowCommands();
                    _admin["commands_listed"] = c.Rows.Count;
                    _admin["commands_output_available"] = c.OutputAvailable;
                    _admin["commands_rows_ok"] = c.Rows.Count > 50 && c.OutputAvailable
                        && c.Rows.Any(r => (string)r["name"] == "Where" && (string)r["access"] == "Counselor" && ((string)r["description"] ?? "").Length > 0)
                        && c.Rows.Any(r => (string)r["name"] == "Wipe" && (string)r["danger"] == "wipe");
                    c.Search("wipe");
                    var shown = c.Shown.ToList();
                    _admin["commands_search"] = string.Join(", ", shown);
                    _admin["commands_search_ok"] = shown.Contains("Wipe") && shown.Count < c.Rows.Count;
                    c.Search("");
                    c.Type("[where");
                    _admin["commands_help"] = c.HelpText;
                    _admin["commands_help_ok"] = c.HelpText.Contains("Where") && c.HelpText.Contains("Counselor") && c.HelpText.Contains("coordinates");
                    c.Type("[wipe");
                    _admin["commands_help_danger"] = c.HelpText;
                    _admin["commands_help_danger_ok"] = c.HelpText.Contains("type 'wipe'");
                    c.Type("");
                    _cmdMark = c.Replies;
                    _admin["commands_where_sent"] = c.Run("[where");
                    AdminNext(161);
                }
                else AdminTimeout("the Commands palette never listed the server's commands");

                break;
            }

            case 161:
            {
                CommandsPanel c = v.Commands;
                if (c.Replies > _cmdMark)
                {
                    JsonNode r = c.LastReply;
                    _admin["commands_where"] = r?.ToJsonString();
                    _admin["commands_where_ok"] = (bool?)r?["ok"] == true && (string)r?["as"] == null
                        && (r?["output"] as JsonArray)?.Any(l => ((string)l ?? "").StartsWith("You are at ")) == true
                        && c.OutputText.Contains("You are at ");
                    // A dangerous command opens the confirm box and sends nothing until its word is typed.
                    _cmdMark = c.Replies;
                    bool sent = c.Run("[wipe");
                    _admin["commands_confirm_shown"] = !sent && c.Pending is { Kind: "wipe", Confirm: "wipe" };
                    _admin["commands_wrong_word_refused"] = !c.Run("[wipe", "yes");
                    AdminShot("commands-confirm");
                    AdminNext(162);
                }
                else AdminTimeout("[where never answered");

                break;
            }

            case 162:
            {
                CommandsPanel c = v.Commands;
                _admin["commands_nothing_sent"] = c.Replies == _cmdMark;
                c.CancelConfirm();
                // The word typed: it runs; [Wipe asks for a target, which the hidden admin presence cannot give.
                _admin["commands_wipe_sent"] = c.Run("[wipe", "wipe");
                AdminNext(163);
                break;
            }

            case 163:
            {
                CommandsPanel c = v.Commands;
                if (c.Replies > _cmdMark)
                {
                    JsonNode r = c.LastReply;
                    _admin["commands_wipe"] = r?.ToJsonString();
                    _admin["commands_wipe_ok"] = (bool?)r?["ok"] == true && (bool?)r?["needs_target"] == true;
                    // Up twice brings back [wipe then [where (the help line follows the line); Down twice empties it.
                    c.Walk(-1);
                    bool newest = c.HelpText.Contains("type 'wipe'");
                    c.Walk(-1);
                    _admin["commands_history"] = string.Join(" | ", c.History);
                    _admin["commands_history_ok"] = c.History.SequenceEqual(new[] { "[where", "[wipe" }) && newest && c.HelpText.Contains("coordinates");
                    c.Walk(1);
                    c.Walk(1);
                    _cmdMark = c.Replies;
                    c.RunAs("NoSuchStaffCharacter");
                    c.Run("[where");
                    AdminNext(164);
                }
                else AdminTimeout("[wipe with its word never answered");

                break;
            }

            case 164:
            {
                CommandsPanel c = v.Commands;
                if (c.Replies > _cmdMark)
                {
                    JsonNode r = c.LastReply;
                    _admin["commands_run_as_refused"] = (bool?)r?["ok"] == false && ((string)r?["error"] ?? "").Contains("not online");
                    _admin["commands_run_as_error"] = (string)r?["error"];
                    c.RunAs("");
                    AdminShot("commands-output");
                    AdminNext(124);
                }
                else AdminTimeout("run as a character not online never answered");

                break;
            }

            case 124:
                // The keep value as it was before the run (after the last still, which shows keep 2).
                if (_bakKeepBefore > 0) v.Backups.SetKeep(_bakKeepBefore);
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
        // A Settings still never shows a folder on this computer (the UO install): its value is covered until taken.
        if (name.StartsWith("settings", StringComparison.Ordinal) && _adminView?.Settings is { } form)
        {
            _admin[$"masked_{name}"] = form.MaskFolders(true) > 0;
        }

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
        _adminView?.Settings?.MaskFolders(false);
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
            _admin["settings_secret_in_tab_log"] = !string.IsNullOrEmpty(_setSecret) && v.LogText.Contains(_setSecret);
            if ((bool)_admin["settings_secret_in_tab_log"]) _failures.Add("Admin tab: the test mail password is in the tab's log");
            _admin["account_password_in_tab_log"] = new[] { _accGenerated, _accTyped }.Any(s => !string.IsNullOrEmpty(s) && v.LogText.Contains(s));
            if ((bool)_admin["account_password_in_tab_log"]) _failures.Add("Admin tab: an account's password is in the tab's log");
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
        Check("settings_labels_ok", "the Settings form shows the shard's settings with plain labels");
        Check("settings_secrets_masked", "the Settings form masks every secret");
        Check("settings_invalid_refused", "the Settings form marks values out of range and will not save them");
        Check("settings_diff_ok", "the list of changes names the three changes and not the password");
        Check("settings_save_enabled", "Save and restart is on with good changes");
        Check("settings_live_matches", "after Save and restart the server reports the new values");
        Check("settings_previous_ok", "the previous files are kept, without the password");
        Check("settings_secret_in_server_file", "the mail password reached the server's own file");
        Check("settings_secret_in_secrets_file", "the mail password is kept in the workspace's secrets file");
        Check("settings_form_reloaded", "the form reads the saved files back with nothing left to save");
        Check("accounts_rows_ok", "the Accounts list shows every account with its level, last login and characters");
        Check("accounts_owner_blocked", "the shard's owner is out of the tab's reach, and the hint says why");
        Check("accounts_generated_fits", "a generated password has 16 characters, the login box's size");
        Check("accounts_created", "the tab made an account with a generated password");
        Check("accounts_access_set", "the tab gave it a level");
        Check("accounts_typed_refused_short", "the tab will not send a password too short");
        Check("accounts_password_set", "the tab gave it a typed password");
        Check("accounts_banned", "the tab banned it");
        Check("accounts_unbanned", "the tab lifted the ban");
        Check("accounts_listed_after", "the list read again shows the account as the tab left it");
        Check("backup_now_ok", "Back up now kept a snapshot of the save, listed first");
        Check("backup_health_ok", "Health shows the last backup, in numbers and in plain words");
        Check("backup_account_made", "an account made after the backup was on the server");
        Check("backup_confirm_shown", "Restore asks first, in plain words");
        Check("backup_restored", "Restore put the backup in place while the run bar had the server stopped");
        Check("backup_before_kept", "a before-restore backup of the world as it was is kept, beside the restored one");
        Check("backup_world_restored", "after the restore the account made after the backup is gone");
        Check("backup_keep_ok", "Back up now with keep 2 removed the oldest backups and spared the before-restore one");
        Check("commands_rows_ok", "the Commands palette lists the server's commands with level, description and the dangerous ones");
        Check("commands_search_ok", "the palette's search narrows the list");
        Check("commands_help_ok", "the help line shows the typed command's usage, level and description");
        Check("commands_help_danger_ok", "the help line says a dangerous command asks for its word");
        Check("commands_where_ok", "[where ran with nobody online and its output is in the palette");
        Check("commands_confirm_shown", "[wipe opened the confirm box and was not sent");
        Check("commands_wrong_word_refused", "a wrong word does not run a dangerous command");
        Check("commands_nothing_sent", "nothing reached the server before the word was typed");
        Check("commands_wipe_ok", "[wipe with its word ran and only asked for a target, cancelled");
        Check("commands_history_ok", "Up brings back the commands run, newest first");
        Check("commands_run_as_refused", "run as a character who is not online is refused");
        if (!Headless)
        {
            Check("masked_settings-form", "the Settings stills cover the UO data folders");
        }

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
