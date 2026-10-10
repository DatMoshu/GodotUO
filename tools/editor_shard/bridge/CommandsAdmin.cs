// The Admin tab's Commands (AD3): the server's commands listed with their
// access level, usage and description, and one run with its output returned
// to the tab. AdminCommandRules.cs (godot/GUO/src/Workspace, linked) decides
// which commands are dangerous; this file runs them on ModernUO. Game thread only.
//
// Requests (each with an optional "req" echoed back):
//   {"op":"admin_commands"}                                          every command the connection's level may run
//   {"op":"admin_command","text":"[where"[,"as":"Moshu"][,"confirm":"wipe"]}
//   {"op":"command","as":"Guosweep","text":"[add ..."}               the old op (ADR-0012): "as" is required
// Replies: admin_commands {"commands":[{"name","aliases","access","usage","description","danger"}],"count",
// "output_available"}; admin_command {"text","as","command","known","output":[".."],"output_available",
// "needs_target","needs_prompt"} or {"ok":false,"error":"..","danger":{"kind","confirm","why"}} in plain words.
//
// No character needs to be online: left without "as", a command runs as a
// hidden admin mobile at the connection's own level. It is made in memory,
// never added to the world and never saved, lives on the Internal map, and
// has no client: a target cursor or a prompt it is given is cancelled at once
// (the reply says so), and a gump it is sent goes nowhere. With "as", the
// command runs as that online staff character, exactly as if they had typed it.
//
// The same hidden mobile is the god view's hidden presence (AD2c,
// GodViewActions.cs): it stays on the Internal map, and its spot in the world
// (Place) is only remembered. Go there and Follow move the spot, Bring here
// brings to it, and a command that moves the mobile ([go) moves it too.
//
// Output: every system message the mobile is sent while the command runs and
// for SettleMs after (a command that answers on a timer), read from MUO patch
// 0005's Mobile.SystemMessageSent. The bridge binds that hook by reflection,
// so it loads on an unpatched server too and says output_available false.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Server;
using Server.Commands;
using Server.Network;
using Server.Targeting;
using GUO.Workspace;

namespace GUO.EditorBridge;

internal static class CommandsAdmin
{
    /// <summary>How long after a command its output is still collected.</summary>
    public const int SettleMs = 300;

    /// <summary>The most output lines one command returns.</summary>
    public const int MaxLines = 200;

    // Not a world serial: the mobile is never added to the world, so nothing can find or save it.
    private static readonly Serial HiddenSerial = (Serial)0x3FFFFFFEu;

    private sealed class Capture
    {
        public Mobile Mobile;
        public readonly List<string> Lines = new();
        public int Dropped;
    }

    // Game thread only.
    private static readonly List<Capture> _captures = new();
    private static Mobile _hidden;
    private static bool? _hooked;

    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"~(\d+)_[^~]*~", RegexOptions.Compiled);

    /// <summary>True when MUO patch 0005's hook is there and bound: command output reaches the tab.</summary>
    public static bool OutputAvailable => _hooked ??= Hook();

    private static bool Hook()
    {
        FieldInfo f = typeof(Mobile).GetField("SystemMessageSent", BindingFlags.Public | BindingFlags.Static);
        if (f == null || f.FieldType != typeof(Action<Mobile, int, string, string, bool>))
        {
            return false;
        }

        Action<Mobile, int, string, string, bool> handler = OnSystemMessage;
        f.SetValue(null, Delegate.Combine((Delegate)f.GetValue(null), handler));
        return true;
    }

    private static void OnSystemMessage(Mobile m, int number, string args, string affix, bool append)
    {
        if (_captures.Count == 0)
        {
            return;
        }

        string text = null;
        foreach (Capture c in _captures)
        {
            if (c.Mobile != m)
            {
                continue;
            }

            text ??= number == 0 ? args : Cliloc(number, args, affix, append);
            if (c.Lines.Count < MaxLines)
            {
                c.Lines.Add(text);
            }
            else
            {
                c.Dropped++;
            }
        }
    }

    /// <summary>A cliloc message as the client would show it: ~1_NAME~ filled from the tab-separated arguments.</summary>
    private static string Cliloc(int number, string args, string affix, bool append)
    {
        string[] a = string.IsNullOrEmpty(args) ? Array.Empty<string>() : args.Split('\t');
        for (int i = 0; i < a.Length; i++)
        {
            // "#1041522" names another cliloc.
            if (a[i].Length > 1 && a[i][0] == '#' && int.TryParse(a[i].AsSpan(1), out int n))
            {
                a[i] = Localization.GetText(n) ?? a[i];
            }
        }

        string text = Localization.GetText(number);
        text = text == null
            ? $"#{number}" + (a.Length > 0 ? " " + string.Join(" ", a) : "")
            : Placeholder.Replace(text, mt => int.TryParse(mt.Groups[1].Value, out int k) && k >= 1 && k <= a.Length ? a[k - 1] : "");
        text = Tags.Replace(text, "");
        return string.IsNullOrEmpty(affix) ? text : append ? text + affix : affix + text;
    }

    private static string Plain(string html) => html == null ? "" : Tags.Replace(html, "").Replace("&lt;", "<").Replace("&gt;", ">").Trim();

    /// <summary>admin_commands: every command a connection holding <paramref name="held"/> may run, by name.</summary>
    public static void List(JsonObject reply, AdminLevel held)
    {
        var rows = new JsonArray();
        foreach (CommandInfo c in HelpInfo.SortedHelpInfo)
        {
            if ((int)c.AccessLevel > (int)held)
            {
                continue;
            }

            AdminCommandRules.Danger d = AdminCommandRules.Classify(c.Name);
            rows.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["aliases"] = new JsonArray((c.Aliases ?? Array.Empty<string>()).Select(x => (JsonNode)x).ToArray()),
                ["access"] = c.AccessLevel.ToString(),
                ["usage"] = Plain(c.Usage),
                ["description"] = Plain(c.Description),
                // The name alone: a generic command (Global, Area, ..) is dangerous only with what follows it.
                ["danger"] = d?.Kind,
            });
        }

        reply["commands"] = rows;
        reply["count"] = rows.Count;
        reply["output_available"] = OutputAvailable;
    }

    /// <summary>The hidden presence's spot in the world (AD2c), or null before Go there, Follow or a [go gave it one.</summary>
    public static (Map Map, Point3D At)? Place { get; set; }

    /// <summary>True for the hidden admin mobile: the Commands palette's runner and the god view's hidden presence.</summary>
    public static bool IsHidden(Mobile m) => m != null && m == _hidden;

    /// <summary>
    /// The hidden admin mobile, at <paramref name="level"/>: Commands run as it, and it is the god view's hidden
    /// presence (GodViewActions). Made once; never in the world.
    /// </summary>
    public static Mobile Hidden(AccessLevel level)
    {
        if (_hidden == null)
        {
            var m = new Mobile(HiddenSerial);
            m.DefaultMobileInit();
            m.RawName = "Admin tab";
            m.Hidden = true;
            m.Player = false;
            _hidden = m;
        }

        // The setter's own notice ("Your access level has been changed") goes to no client and to no capture.
        _hidden.AccessLevel = level;
        return _hidden;
    }

    // A command that put the hidden mobile on a map ([go, [teleport) moved the presence there: keep the spot, then
    // take the mobile back off the map, where nothing in the world can meet it.
    private static void Internalize(Mobile m)
    {
        if (m.Map == Map.Internal)
        {
            return;
        }

        if (m.Map != null)
        {
            Place = (m.Map, m.Location);
        }

        m.Internalize();
    }

    /// <summary>
    /// admin_command (and the old "command" op, with <paramref name="legacy"/>): checks, runs and answers. Returns false
    /// with the refusal in reply["error"]; true when the command was handed to the server, answered after SettleMs.
    /// </summary>
    public static bool Run(JsonNode msg, JsonObject reply, AdminLevel held, bool legacy, Action<JsonObject> send, out string done)
    {
        done = null;
        string raw = (string)msg["text"];
        string who = ((string)msg["as"])?.Trim();
        string refused = AdminCommandRules.Check(raw);
        if (refused == null && legacy && string.IsNullOrEmpty(who))
        {
            refused = "the command op runs as an online character: name it in 'as' (admin_command runs without one)";
        }

        string text = refused == null ? AdminCommandRules.Normalise(raw) : null;
        reply["text"] = text == null ? null : AdminCommandRules.ForLog(text);
        AdminCommandRules.Danger danger = text == null ? null : AdminCommandRules.Classify(text);
        if (refused == null && danger != null && !AdminCommandRules.Confirms(danger, (string)msg["confirm"]))
        {
            refused = $"'{AdminCommandRules.Name(text)}' is on the dangerous list ({danger.Kind}): type '{danger.Confirm}' to confirm it";
            reply["danger"] = new JsonObject { ["kind"] = danger.Kind, ["confirm"] = danger.Confirm, ["why"] = danger.Why };
        }

        Mobile m = null;
        if (refused == null && !string.IsNullOrEmpty(who))
        {
            m = NetState.Instances.Select(ns => ns.Mobile)
                .FirstOrDefault(x => x is { Deleted: false } && string.Equals(x.RawName, who, StringComparison.OrdinalIgnoreCase));
            refused = AdminCommandRules.CheckCharacter(who, m != null, m == null ? 0 : (int)m.AccessLevel, (int)held);
        }

        if (refused != null)
        {
            reply["ok"] = false;
            reply["error"] = refused;
            return false;
        }

        m ??= Hidden((AccessLevel)(int)held);
        bool hidden = m == _hidden;
        string name = AdminCommandRules.Name(text);
        var capture = new Capture { Mobile = m };
        bool output = OutputAvailable;
        _captures.Add(capture);

        bool handled;
        try
        {
            handled = CommandSystem.Handle(m, text);
        }
        catch (Exception e)
        {
            // A command that throws is the command's bug; the server goes on, and the tab hears what it was.
            _captures.Remove(capture);
            reply["ok"] = false;
            reply["error"] = $"'{name}' failed on the server: {e.GetType().Name}: {e.Message}";
            return false;
        }

        // A mobile with no client cannot answer a target cursor or a prompt: cancel them, and say so.
        bool needsTarget = false, needsPrompt = false;
        if (hidden)
        {
            if (m.Target != null)
            {
                needsTarget = true;
                m.Target.Cancel(m, TargetCancelType.Canceled);
                m.ClearTarget();
            }

            if (m.Prompt is { } prompt)
            {
                needsPrompt = true;
                prompt.OnCancel(m);
                m.Prompt = null;
            }

            Internalize(m);
        }

        reply["as"] = hidden ? null : m.RawName;
        reply["command"] = name;
        reply["known"] = CommandSystem.Entries.ContainsKey(name);
        reply["needs_target"] = needsTarget;
        reply["needs_prompt"] = needsPrompt;
        reply["output_available"] = output;
        if (!handled)
        {
            // Only a player's text falls through; staff get "That is not a valid command." as output.
            reply["handled"] = false;
        }

        // The rest of the output arrives on a timer; the reply follows once it has had time to.
        Timer.DelayCall(TimeSpan.FromMilliseconds(SettleMs), () =>
        {
            _captures.Remove(capture);
            if (hidden)
            {
                Internalize(_hidden);
            }

            var lines = new JsonArray(capture.Lines.Select(l => (JsonNode)l).ToArray());
            if (capture.Dropped > 0)
            {
                lines.Add($"... and {capture.Dropped} more lines");
            }

            if (needsTarget)
            {
                lines.Add("It asked for a target. Only a client can give one: run it as your staff character ('run as') to target in game.");
            }

            if (needsPrompt)
            {
                lines.Add("It asked for typed text in game. Only a client can answer: run it as your staff character ('run as').");
            }

            reply["output"] = lines;
            send(reply);
        });
        done = $"ran \"{AdminCommandRules.ForLog(text)}\" as {(hidden ? "the Admin tab" : m.RawName)}" +
               $"{(needsTarget ? " (wanted a target, cancelled)" : "")}{(needsPrompt ? " (wanted a prompt, cancelled)" : "")}";
        return true;
    }
}
