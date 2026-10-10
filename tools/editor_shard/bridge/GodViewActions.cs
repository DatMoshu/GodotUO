// The god view's actions (Admin tab, AD2b, AD2c): Go there, Bring here, Open
// paperdoll, Follow; and a spawner's Respawn and Clear.
//
// Who acts: the admin's OWN logged-in staff character (AD2b), or, with nobody
// logged in, the Admin tab's hidden presence (AD2c). "as" names the character:
// online, GameMaster or higher, and no higher than the level the connection's
// token grants. Left out, the one such character online acts; with none online
// (or with "hidden":true) the hidden presence does. The presence is
// CommandsAdmin's hidden admin mobile, which never enters the world: it stays
// on the Internal map and only its spot (CommandsAdmin.Place) moves. So for
// the presence, Go there and Follow move the spot (the tab's map goes with
// it), Bring here brings to the spot (or to a facet, x and y in the request),
// and Open paperdoll answers with what the paperdoll shows, as there is no
// client to open it in.
//
// Requests (each with an optional "req" echoed back):
//   {"op":"admin_goto","as":"Moshu","serial":N}                 the character goes to N
//   {"op":"admin_goto","as":"Moshu","facet":0,"x":..,"y":..[,"z":..]}   or to a spot
//   {"op":"admin_bring","as":"Moshu","serial":N}                N (a player or NPC) comes to the character
//   {"op":"admin_bring","hidden":true,"serial":N[,"facet":0,"x":..,"y":..[,"z":..]]}   to the presence's spot, or that one
//   {"op":"admin_paperdoll","as":"Moshu","serial":N}            N's paperdoll opens in the character's client
//   {"op":"admin_follow","as":"Moshu","serial":N}               the character keeps within 2 tiles of N
//   {"op":"admin_follow","stop":true}                           ends it
//   {"op":"admin_spawner","serial":N,"action":"respawn"|"clear"}
// Replies: {"op":..,"req":..,"ok":true,"as":"Moshu","serial":N,"facet","x","y","z"} (where the moved one stands);
// for the presence "as" is null, "hidden" true, and Go there and Follow give its spot. admin_paperdoll for the
// presence adds "paperdoll":{"name","title","body","female","items":[{"layer","name","item_id","hue","serial"}]};
// admin_spawner adds "spawned" (what it holds now). A refusal is {"ok":false,"error":".."} in plain words.
// Follow ends on its own when the target or the character leaves the world, with a push
// {"op":"admin_follow","following":false,"serial":N,"reason":".."}.
//
// Every action also tells the character in game what the Admin tab did, so
// the person at the client is never moved without a word; a summoned player
// is told too, whoever summoned them.

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
        // Null when the hidden presence follows: its spot moves, not a mobile.
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

    /// <summary>
    /// Who acts: the admin's own character (returned), the hidden presence (<paramref name="presence"/> true, null
    /// returned), or nobody, with the refusal in <paramref name="error"/>.
    /// </summary>
    private static Mobile Actor(JsonNode msg, AdminLevel held, out bool presence, out string error)
    {
        presence = false;
        string who = ((string)msg["as"] ?? "").Trim();
        List<Mobile> online = NetState.Instances.Select(ns => ns.Mobile).Where(m => m is { Deleted: false }).Distinct().ToList();
        // A staff character above the tab's own level is never picked for it.
        List<Mobile> staff = online
            .Where(m => (AdminLevel)(int)m.AccessLevel >= GodViewRules.CharacterLevel && (AdminLevel)(int)m.AccessLevel <= held)
            .ToList();
        switch (GodViewRules.Pick(who, (bool?)msg["hidden"] == true, staff.Select(m => m.RawName).ToList(), out error))
        {
            case GodViewRules.Actor.Presence:
                presence = true;
                return null;
            case GodViewRules.Actor.Refused:
                return null;
        }

        if (who.Length == 0)
        {
            return staff[0];
        }

        Mobile m = online.FirstOrDefault(x => string.Equals(x.RawName, who, StringComparison.OrdinalIgnoreCase));
        error = GodViewRules.CheckCharacter(who, m != null, m == null ? AdminLevel.Player : (AdminLevel)(int)m.AccessLevel, held);
        return error == null ? m : null;
    }

    private static IEntity Target(JsonNode msg) =>
        msg["serial"] is JsonNode s && (uint?)s is uint serial && serial != 0 ? World.FindEntity((Serial)serial) : null;

    private static void Where(JsonObject reply, IEntity e) => Where(reply, e.Map, e is Item i ? i.GetWorldLocation() : e.Location);

    private static void Where(JsonObject reply, Map map, Point3D at)
    {
        reply["facet"] = map?.MapIndex;
        reply["x"] = at.X;
        reply["y"] = at.Y;
        reply["z"] = at.Z;
    }

    /// <summary>The spot a request names (facet, x, y, and z or the ground there), or null with the refusal in <paramref name="error"/>.</summary>
    private static (Map Map, Point3D At)? Spot(JsonNode msg, string missing, out string error)
    {
        error = null;
        int facet = (int?)msg["facet"] ?? -1;
        Map map = facet >= 0 && facet < Map.Maps.Length && Map.Maps[facet] is { } m && m != Map.Internal ? m : null;
        if (map == null)
        {
            error = $"no facet {facet}";
            return null;
        }

        if (msg["x"] == null || msg["y"] == null)
        {
            error = missing;
            return null;
        }

        int x = (int)msg["x"], y = (int)msg["y"];
        if (x < 0 || y < 0 || x >= map.Width || y >= map.Height)
        {
            error = $"{x}, {y} is off {map.Name} ({map.Width}x{map.Height})";
            return null;
        }

        return (map, new Point3D(x, y, (int?)msg["z"] ?? map.GetAverageZ(x, y)));
    }

    /// <summary>
    /// Runs one god view action into <paramref name="reply"/> (ok already true); false with "error" set when it
    /// was refused. <paramref name="held"/> is the level the connection's token grants. Game thread.
    /// </summary>
    public static bool Run(object key, string op, JsonNode msg, JsonObject reply, AdminLevel held, Action<JsonObject> send)
    {
        string error = op switch
        {
            "admin_spawner" => Spawner(msg, reply),
            "admin_follow" when (bool?)msg["stop"] == true => Stop(key, reply),
            _ => WithActor(key, op, msg, reply, held, send),
        };
        if (error != null)
        {
            reply["ok"] = false;
            reply["error"] = error;
            return false;
        }

        return true;
    }

    private static string WithActor(object key, string op, JsonNode msg, JsonObject reply, AdminLevel held, Action<JsonObject> send)
    {
        Mobile me = Actor(msg, held, out bool presence, out string error);
        if (presence)
        {
            return WithPresence(key, op, msg, reply, held, send);
        }

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
        if (Spot(msg, "Go there needs a serial, or a facet with x and y", out string error) is not (Map map, Point3D at))
        {
            return error;
        }

        me.MoveToWorld(at, map);
        me.SendMessage($"The Admin tab moved you to {at.X}, {at.Y} on {map.Name}.");
        Where(reply, me);
        return null;
    }

    // The hidden presence acts (AD2c), so nobody needs to be logged in. The mobile is never put in the world; its spot is
    // only remembered.
    private static string WithPresence(object key, string op, JsonNode msg, JsonObject reply, AdminLevel held, Action<JsonObject> send)
    {
        // The same mobile the Commands palette runs as, at this connection's level.
        CommandsAdmin.Hidden((AccessLevel)(int)held);
        reply["as"] = null;
        reply["hidden"] = true;
        string error;
        if (op == "admin_goto" && msg["serial"] == null)
        {
            if (Spot(msg, "Go there needs a serial, or a facet with x and y", out error) is not (Map map, Point3D at))
            {
                return error;
            }

            CommandsAdmin.Place = (map, at);
            Where(reply, map, at);
            return null;
        }

        IEntity target = Target(msg);
        bool spotGiven = op == "admin_bring" && msg["facet"] != null;
        error = GodViewRules.CheckTarget(op, KindOf(target), false) ?? GodViewRules.CheckPresence(op, CommandsAdmin.Place != null, spotGiven);
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
                CommandsAdmin.Place = (target.Map, target is Item i ? i.GetWorldLocation() : target.Location);
                Where(reply, CommandsAdmin.Place.Value.Map, CommandsAdmin.Place.Value.At);
                break;
            case "admin_bring":
            {
                (Map Map, Point3D At)? to = spotGiven
                    ? Spot(msg, "Bring here to a spot needs a facet with x and y", out error)
                    : CommandsAdmin.Place;
                if (to == null)
                {
                    return error;
                }

                var m = (Mobile)target;
                m.MoveToWorld(to.Value.At, to.Value.Map);
                if (m.NetState != null)
                {
                    m.SendMessage("A staff member has summoned you.");
                }

                Where(reply, m);
                break;
            }
            case "admin_paperdoll":
                reply["paperdoll"] = Paperdoll((Mobile)target);
                Where(reply, target);
                break;
            case "admin_follow":
            {
                var t = (Mobile)target;
                _follows[key] = new Follow { Target = t, Send = send };
                CommandsAdmin.Place = (t.Map, t.Location);
                StartTimer();
                reply["following"] = true;
                Where(reply, t.Map, t.Location);
                break;
            }
        }

        return null;
    }

    // What a paperdoll shows, for the presence, which has no client to open one in: the bank box is not on it.
    private static JsonObject Paperdoll(Mobile m) => new()
    {
        ["name"] = NameOf(m),
        ["title"] = m.Title,
        ["body"] = m.Body.BodyID,
        ["female"] = m.Female,
        ["items"] = new JsonArray(m.Items.Where(i => !i.Deleted && i.Layer != Layer.Bank).OrderBy(i => (int)i.Layer)
            .Select(i => (JsonNode)new JsonObject
            {
                ["layer"] = i.Layer.ToString(), ["name"] = NameOf(i), ["item_id"] = i.ItemID, ["hue"] = i.Hue,
                ["serial"] = (uint)i.Serial,
            }).ToArray()),
    };

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
        StartTimer();
    }

    private static void StartTimer() =>
        _timer ??= Timer.DelayCall(TimeSpan.FromMilliseconds(GodViewRules.FollowIntervalMs),
            TimeSpan.FromMilliseconds(GodViewRules.FollowIntervalMs), Tick);

    private static string Stop(object key, JsonObject reply)
    {
        if (_follows.Remove(key, out Follow f))
        {
            reply["serial"] = (uint)f.Target.Serial;
            reply["moves"] = f.Moves;
            if (f.Character is { Deleted: false })
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
            string ended = me != null && (me.Deleted || me.NetState == null) ? $"{me.RawName} logged out"
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
                if (me is { Deleted: false, NetState: not null })
                {
                    me.SendMessage($"The Admin tab: Follow ended, {ended}.");
                }

                continue;
            }

            if (me == null)
            {
                // The hidden presence: its spot follows.
                (Map Map, Point3D At)? p = CommandsAdmin.Place;
                if (p == null || GodViewRules.FollowMustMove(p.Value.Map.MapIndex, p.Value.At.X, p.Value.At.Y, t.Map.MapIndex, t.X, t.Y))
                {
                    CommandsAdmin.Place = (t.Map, t.Location);
                    f.Moves++;
                }
            }
            else if (GodViewRules.FollowMustMove(me.Map?.MapIndex ?? -1, me.X, me.Y, t.Map.MapIndex, t.X, t.Y))
            {
                me.MoveToWorld(t.Location, t.Map);
                f.Moves++;
            }
        }
    }
}
