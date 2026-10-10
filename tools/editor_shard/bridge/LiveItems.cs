// The "items" op: world items in a rectangle on a facet, for the editor's
// Live map layer and world view (ADR-0027, like LiveMobiles). Read-only.
//
// Request:  {"op":"items","facet":0,"x0":1400,"y0":1550,"x1":1560,"y1":1700,"req":7,"as":"<staff character, optional>"}
// Reply:    {"op":"items","req":7,"ok":true,"facet":0,"x0":..,"y0":..,"x1":..,"y1":..,
//            "count":2,"truncated":false,
//            "items":[{"serial":1,"id":4188,"hue":0,"x":..,"y":..,"z":..,"facet":0,"amount":1}]}
// Refusal:  {"op":"items","req":7,"ok":false,"error":"..."}
// Top-level items only: worn, held and container contents are skipped.
// Same guards as mobiles (authorised, rate-limited, capped).

using System;
using System.Text.Json.Nodes;
using Server;

namespace GUO.EditorBridge;

internal static class LiveItems
{
    // Same reasoning as mobiles: town views hold thousands of decorated
    // items, and a truncated reply flickers them.
    public const int MaxResults = 10000;
    public const int MaxSpan = 1024;
    public const int MinIntervalMs = 250;

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
        foreach (Item item in map.GetItemsInBounds(new Rectangle2D(x0, y0, x1 - x0 + 1, y1 - y0 + 1)))
        {
            if (item.Deleted || item.Parent != null || item.Map != map)
            {
                continue;
            }

            if (list.Count >= MaxResults)
            {
                truncated = true;
                break;
            }

            list.Add(new JsonObject
            {
                ["serial"] = (uint)item.Serial,
                ["id"] = item.ItemID,
                ["hue"] = item.Hue,
                ["x"] = item.X, ["y"] = item.Y, ["z"] = item.Z,
                ["facet"] = facet,
                ["amount"] = item.Amount,
            });
        }

        return new JsonObject
        {
            ["op"] = "items", ["req"] = req, ["ok"] = true, ["facet"] = facet,
            ["x0"] = x0, ["y0"] = y0, ["x1"] = x1, ["y1"] = y1,
            ["count"] = list.Count, ["truncated"] = truncated, ["items"] = list,
        };
    }

    public static JsonObject Refuse(int req, string error) =>
        new() { ["op"] = "items", ["req"] = req, ["ok"] = false, ["error"] = error };
}
