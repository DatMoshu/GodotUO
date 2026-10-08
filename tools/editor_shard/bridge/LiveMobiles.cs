// The "mobiles" op: players and mobiles in a rectangle on a facet, for the
// editor's Live map layer (ADR-0027). Read-only: it changes nothing.
//
// Request:  {"op":"mobiles","facet":0,"x0":1400,"y0":1550,"x1":1560,"y1":1700,"req":7,"as":"<staff character, optional>"}
// Reply:    {"op":"mobiles","req":7,"ok":true,"facet":0,"x0":..,"y0":..,"y1":..,
//            "count":2,"truncated":false,
//            "mobiles":[{"serial":1,"name":"..","body":400,"x":..,"y":..,"z":..,"facet":0,
//                        "isPlayer":true,"hits":50,"maxHits":50,"notoriety":1,
//                        "direction":4,"hue":0,
//                        "equip":[{"serial":2,"layer":13,"id":4188,"hue":0}]}]}
// Refusal:  {"op":"mobiles","req":7,"ok":false,"error":"..."}
//
// Guards: authorised like the bridge's other ops (a loopback editor connection
// that has said hello; and if "as" names a character, that character must be
// online and a GameMaster or above), rate-limited per connection, and capped
// in span and in results.

using System;
using System.Text.Json.Nodes;
using Server;
using Server.Mobiles;

namespace GUO.EditorBridge;

internal static class LiveMobiles
{
    // High enough that a town view is never cut: the editor polls its
    // visible rectangle about once a second, and a truncated reply drops
    // objects the next poll keeps (visible flicker). Loopback can carry it.
    public const int MaxResults = 2000;
    public const int MaxSpan = 1024;
    public const int MinIntervalMs = 250;
    private const int MaxEquip = 25;

    /// <summary>Refusal text if the request is not allowed, or null. Game thread.</summary>
    public static string Authorise(JsonNode msg, bool hello)
    {
        if (!hello)
        {
            return "say hello first";
        }

        if ((string)msg["as"] is { Length: > 0 } who)
        {
            Mobile m = null;
            foreach (var ns in Server.Network.NetState.Instances)
            {
                if (ns.Mobile != null && string.Equals(ns.Mobile.RawName, who, StringComparison.OrdinalIgnoreCase))
                {
                    m = ns.Mobile;
                    break;
                }
            }

            if (m == null)
            {
                return $"'{who}' is not online";
            }

            if (m.AccessLevel < AccessLevel.GameMaster)
            {
                return $"'{who}' is not staff";
            }
        }

        return null;
    }

    public static JsonObject Query(JsonNode msg)
    {
        int req = (int?)msg["req"] ?? 0;
        int facet = (int?)msg["facet"] ?? -1;
        Map map = facet >= 0 && facet < Map.Maps.Length ? Map.Maps[facet] : null;
        if (map == null || map == Map.Internal)
        {
            return Refuse(req, $"no map {facet}");
        }

        int x0 = (int?)msg["x0"] ?? 0, y0 = (int?)msg["y0"] ?? 0, x1 = (int?)msg["x1"] ?? 0, y1 = (int?)msg["y1"] ?? 0;
        if (x1 < x0)
        {
            (x0, x1) = (x1, x0);
        }

        if (y1 < y0)
        {
            (y0, y1) = (y1, y0);
        }

        // Clamped to the map and to a span, so one request cannot sweep the world.
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(map.Width - 1, Math.Min(x1, x0 + MaxSpan));
        y1 = Math.Min(map.Height - 1, Math.Min(y1, y0 + MaxSpan));

        var list = new JsonArray();
        bool truncated = false;
        foreach (Mobile m in map.GetMobilesInBounds(new Rectangle2D(x0, y0, x1 - x0 + 1, y1 - y0 + 1)))
        {
            if (m.Deleted)
            {
                continue;
            }

            if (list.Count >= MaxResults)
            {
                truncated = true;
                break;
            }

            int noto;
            try
            {
                noto = Notoriety.Compute(null, m);
            }
            catch (Exception)
            {
                noto = Notoriety.CanBeAttacked;
            }

            var equip = new JsonArray();
            foreach (Item worn in m.Items)
            {
                if (worn == null || worn.Deleted)
                {
                    continue;
                }

                if (equip.Count >= MaxEquip)
                {
                    break;
                }

                equip.Add(new JsonObject
                {
                    ["serial"] = (uint)worn.Serial,
                    ["layer"] = (int)worn.Layer,
                    ["id"] = worn.ItemID,
                    ["hue"] = worn.Hue,
                });
            }

            list.Add(new JsonObject
            {
                ["serial"] = (uint)m.Serial,
                ["name"] = m.Name ?? m.RawName ?? "",
                ["body"] = (int)m.Body,
                ["x"] = m.X, ["y"] = m.Y, ["z"] = m.Z,
                ["facet"] = facet,
                ["isPlayer"] = m is PlayerMobile,
                ["hits"] = m.Hits,
                ["maxHits"] = m.HitsMax,
                ["notoriety"] = noto,
                ["direction"] = (int)m.Direction & 7,
                ["hue"] = m.Hue,
                ["equip"] = equip,
            });
        }

        return new JsonObject
        {
            ["op"] = "mobiles", ["req"] = req, ["ok"] = true, ["facet"] = facet,
            ["x0"] = x0, ["y0"] = y0, ["x1"] = x1, ["y1"] = y1,
            ["count"] = list.Count, ["truncated"] = truncated, ["mobiles"] = list,
        };
    }

    public static JsonObject Refuse(int req, string error) =>
        new() { ["op"] = "mobiles", ["req"] = req, ["ok"] = false, ["error"] = error };
}
