// The god view's actions (Admin tab, AD2b), with the admin's OWN logged-in
// staff character: Go there, Bring here, Open paperdoll, Follow; and a
// spawner's Respawn and Clear. A hidden server-side admin presence, so these
// work with nobody logged in, is AD2c.
//
// Requests (each with an optional "req" echoed back; "as" names the admin's
// online character, which must be GameMaster or higher; left out, the one
// online staff character is used when there is exactly one):
//   {"op":"admin_goto","as":"Moshu","serial":N}                 the character goes to N
//   {"op":"admin_goto","as":"Moshu","facet":0,"x":..,"y":..[,"z":..]}   or to a spot
//   {"op":"admin_bring","as":"Moshu","serial":N}                N (a player or NPC) comes to the character
//   {"op":"admin_paperdoll","as":"Moshu","serial":N}            N's paperdoll opens in the character's client
//   {"op":"admin_follow","as":"Moshu","serial":N}               the character keeps within 2 tiles of N
//   {"op":"admin_follow","stop":true}                           ends it
//   {"op":"admin_spawner","serial":N,"action":"respawn"|"clear"}
// Replies: {"op":..,"req":..,"ok":true,"as":"Moshu","serial":N,"facet","x","y","z"} (where the moved one stands),
// admin_spawner adds "spawned" (what it holds now). A refusal is {"ok":false,"error":".."} in plain words.
// Follow ends on its own when the target or the character leaves the world, with a push
// {"op":"admin_follow","following":false,"serial":N,"reason":".."}.
//
// Every action also tells the character in game what the Admin tab did, so
// the person at the client is never moved without a word.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Server;
using Server.Engines.Spawners;
using Server.Mobiles;
using Server.Network;

namespace GUO.EditorBridge;

internal static class GodViewActions
{
    private sealed class Follow
    {
        public Mobile Character;
        public Mobile Target;
        public Action<JsonObject> Send;
        public int Moves;
    }

    // Game thread only.
    private static readonly Dictionary<object, Follow> _follows = new();
    private static Timer _timer;

    /// <summary>The kind the god view gives an entity ("player", "npc", "spawner", "item"), or null.</summary>
    private static string KindOf(IEntity e) => e switch
    {
        null => null,
        Mobile { Deleted: true } or Item { Deleted: true } => null,
        PlayerMobile => "player",
        Mobile => "npc",
        BaseSpawner => "spawner",
        Item => "item",
        _ => null,
    };

    private static string NameOf(IEntity e) => e switch
    {
        Mobile m => m.Name ?? m.RawName ?? m.GetType().Name,
        Item i => i.Name ?? i.GetType().Name,
        _ => "?",
    };

    /// <summary>The admin's own character, or null with the refusal in <paramref name="error"/>.</summary>
    private static Mobile Character(JsonNode msg, out string error)
    {
        error = null;
        string who = ((string)msg["as"] ?? "").Trim();
        List<Mobile> online = NetState.Instances.Select(ns => ns.Mobile).Where(m => m is { Deleted: false }).Distinct().ToList();
        if (who.Length == 0)
        {
            List<Mobile> staff = online.Where(m => (AdminLevel)(int)m.AccessLevel >= GodViewRules.CharacterLevel).ToList();
            if (staff.Count == 1)
            {
                return staff[0];
            }

            error = staff.Count == 0
                ? "no staff character is online: log in with yours first"
                : $"{staff.Count} staff characters are online; pick yours ({string.Join(", ", staff.Select(m => m.RawName))})";
            return null;
        }

        Mobile m = online.FirstOrDefault(x => string.Equals(x.RawName, who, StringComparison.OrdinalIgnoreCase));
        error = GodViewRules.CheckCharacter(who, m != null, m == null ? AdminLevel.Player : (AdminLevel)(int)m.AccessLevel);
        return error == null ? m : null;
    }

    private static IEntity Target(JsonNode msg) =>
        msg["serial"] is JsonNode s && (uint?)s is uint serial && serial != 0 ? World.FindEntity((Serial)serial) : null;

    private static void Where(JsonObject reply, IEntity e)
    {
        Point3D at = e is Item i ? i.GetWorldLocation() : e.Location;
        reply["facet"] = e.Map?.MapIndex;
        reply["x"] = at.X;
        reply["y"] = at.Y;
        reply["z"] = at.Z;
    }

    /// <summary>
    /// Runs one god view action into <paramref name="reply"/> (ok already true); false with "error" set when it
    /// was refused. Game thread.
    /// </summary>
    public static bool Run(object key, string op, JsonNode msg, JsonObject reply, Action<JsonObject> send)
    {
        string error = op switch
        {
            "admin_spawner" => Spawner(msg, reply),
            "admin_follow" when (bool?)msg["stop"] == true => Stop(key, reply),
            _ => WithCharacter(key, op, msg, reply, send),
        };
        if (error != null)
        {
            reply["ok"] = false;
            reply["error"] = error;
            return false;
        }

        return true;
    }

    private static string WithCharacter(object key, string op, JsonNode msg, JsonObject reply, Action<JsonObject> send)
    {
        Mobile me = Character(msg, out string error);
        if (me == null)
        {
            return error;
        }

        reply["as"] = me.RawName;
        if (op == "admin_goto" && msg["serial"] == null)
        {
            return GotoSpot(me, msg, reply);
        }

        IEntity target = Target(msg);
        string kind = KindOf(target);
        error = GodViewRules.CheckTarget(op, kind, target == me);
        if (error != null)
        {
            return error;
        }

        reply["serial"] = (uint)target.Serial;
        reply["name"] = NameOf(target);
        if (target.Map == null || target.Map == Map.Internal)
        {
            return $"{NameOf(target)} is not in the world (logged out, or held in a container off the map)";
        }

        switch (op)
        {
            case "admin_goto":
            {
                Point3D at = target is Item i ? i.GetWorldLocation() : target.Location;
                me.MoveToWorld(at, target.Map);
                me.SendMessage($"The Admin tab moved you to {NameOf(target)}.");
                Where(reply, me);
                break;
            }
            case "admin_bring":
            {
                var m = (Mobile)target;
                m.MoveToWorld(me.Location, me.Map);
                me.SendMessage($"The Admin tab brought {NameOf(m)} to you.");
                if (m != me && m.NetState != null)
                {
                    m.SendMessage("A staff member has summoned you.");
                }

                Where(reply, m);
                break;
            }
            case "admin_paperdoll":
                ((Mobile)target).DisplayPaperdollTo(me);
                me.SendMessage($"The Admin tab opened {NameOf(target)}'s paperdoll.");
                Where(reply, target);
                break;
            case "admin_follow":
                StartFollow(key, me, (Mobile)target, send);
                reply["following"] = true;
                Where(reply, me);
                break;
        }

        return null;
    }

    private static string GotoSpot(Mobile me, JsonNode msg, JsonObject reply)
    {
        int facet = (int?)msg["facet"] ?? -1;
        Map map = facet >= 0 && facet < Map.Maps.Length && Map.Maps[facet] is { } m && m != Map.Internal ? m : null;
        if (map == null)
        {
            return $"no facet {facet}";
        }

        if (msg["x"] == null || msg["y"] == null)
        {
            return "Go there needs a serial, or a facet with x and y";
        }

        int x = (int)msg["x"], y = (int)msg["y"];
        if (x < 0 || y < 0 || x >= map.Width || y >= map.Height)
        {
            return $"{x}, {y} is off {map.Name} ({map.Width}x{map.Height})";
        }

        int z = (int?)msg["z"] ?? map.GetAverageZ(x, y);
        me.MoveToWorld(new Point3D(x, y, z), map);
        me.SendMessage($"The Admin tab moved you to {x}, {y} on {map.Name}.");
        Where(reply, me);
        return null;
    }

    private static string Spawner(JsonNode msg, JsonObject reply)
    {
        IEntity target = Target(msg);
        string error = GodViewRules.CheckTarget("admin_spawner", KindOf(target), false);
        if (error != null)
        {
            return error;
        }

        string action = (string)msg["action"];
        if (!GodViewRules.SpawnerActions.Contains(action))
        {
            return $"a spawner can be {string.Join(" or ", GodViewRules.SpawnerActions)}, not '{action}'";
        }

        var s = (BaseSpawner)target;
        int before = s.Spawned?.Count ?? 0;
        if (action == "respawn")
        {
            s.Respawn();
        }
        else
        {
            s.RemoveSpawns();
        }

        reply["serial"] = (uint)s.Serial;
        reply["name"] = NameOf(s);
        reply["action"] = action;
        reply["before"] = before;
        reply["spawned"] = s.Spawned?.Count ?? 0;
        reply["running"] = s.Running;
        Where(reply, s);
        return null;
    }

    private static void StartFollow(object key, Mobile me, Mobile target, Action<JsonObject> send)
    {
        _follows[key] = new Follow { Character = me, Target = target, Send = send };
        if (GodViewRules.FollowMustMove(me.Map?.MapIndex ?? -1, me.X, me.Y, target.Map.MapIndex, target.X, target.Y))
        {
            me.MoveToWorld(target.Location, target.Map);
        }

        me.SendMessage($"The Admin tab: you follow {NameOf(target)}.");
        _timer ??= Timer.DelayCall(TimeSpan.FromMilliseconds(GodViewRules.FollowIntervalMs),
            TimeSpan.FromMilliseconds(GodViewRules.FollowIntervalMs), Tick);
    }

    private static string Stop(object key, JsonObject reply)
    {
        if (_follows.Remove(key, out Follow f))
        {
            reply["serial"] = (uint)f.Target.Serial;
            reply["moves"] = f.Moves;
            if (!f.Character.Deleted)
            {
                f.Character.SendMessage($"The Admin tab: you no longer follow {NameOf(f.Target)}.");
            }
        }

        reply["following"] = false;
        return null;
    }

    /// <summary>The editor left: its Follow ends. Game thread.</summary>
    public static void Forget(object key) => _follows.Remove(key);

    private static void Tick()
    {
        if (_follows.Count == 0)
        {
            _timer?.Stop();
            _timer = null;
            return;
        }

        foreach ((object key, Follow f) in _follows.ToList())
        {
            Mobile me = f.Character, t = f.Target;
            string ended = me.Deleted || me.NetState == null ? $"{me.RawName} logged out"
                : t.Deleted ? $"{NameOf(t)} is gone (deleted)"
                : t.Map == null || t.Map == Map.Internal ? $"{NameOf(t)} left the world"
                : null;
            if (ended != null)
            {
                _follows.Remove(key);
                f.Send(new JsonObject
                {
                    ["op"] = "admin_follow", ["ok"] = true, ["following"] = false, ["serial"] = (uint)t.Serial,
                    ["moves"] = f.Moves, ["reason"] = ended,
                });
                if (!me.Deleted && me.NetState != null)
                {
                    me.SendMessage($"The Admin tab: Follow ended, {ended}.");
                }

                continue;
            }

            if (GodViewRules.FollowMustMove(me.Map?.MapIndex ?? -1, me.X, me.Y, t.Map.MapIndex, t.X, t.Y))
            {
                me.MoveToWorld(t.Location, t.Map);
                f.Moves++;
            }
        }
    }
}
