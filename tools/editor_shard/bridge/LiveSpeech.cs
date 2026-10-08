// The "speech" op: recent overhead speech in a rectangle on a facet, for
// live mobiles' overhead text in the editor (like LiveMobiles/LiveItems).
// Read-only: a ring buffer filled from SpeechEvent, served since a timestamp.
//
// Request:  {"op":"speech","facet":0,"x0":1400,"y0":1550,"x1":1560,"y1":1700,"req":7,"since":123456789,"as":"<staff character, optional>"}
// Reply:    {"op":"speech","req":7,"ok":true,"count":2,
//            "speech":[{"id":9,"serial":1,"x":..,"y":..,"z":..,"facet":0,"text":"..","hue":33,"ms":123456790}]}
// Refusal:  {"op":"speech","req":7,"ok":false,"error":"..."}
// Same guards as mobiles (authorised, rate-limited, capped).

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Server;

namespace GUO.EditorBridge;

internal static class LiveSpeech
{
    public const int MaxResults = 100;
    public const int MaxSpan = 1024;
    public const int MinIntervalMs = 250;
    private const int BufferSize = 200;
    private const long KeepMs = 60000;

    private sealed class Entry
    {
        public long Id;
        public uint Serial;
        public int X, Y, Z, Facet;
        public string Text = "";
        public int Hue;
        public long Ms;
    }

    private static readonly List<Entry> _buffer = new();
    private static long _nextId;
    private static readonly object _lock = new();

    public static void OnSpeech(SpeechEventArgs e)
    {
        Mobile m = e?.Mobile;
        if (m == null || m.Deleted || string.IsNullOrEmpty(e.Speech))
        {
            return;
        }

        int facet = Map.Maps.IndexOf(m.Map);
        lock (_lock)
        {
            _buffer.Add(new Entry
            {
                Id = ++_nextId,
                Serial = (uint)m.Serial,
                X = m.X, Y = m.Y, Z = m.Z,
                Facet = facet,
                Text = e.Speech,
                Hue = e.Hue,
                Ms = Environment.TickCount64,
            });

            while (_buffer.Count > BufferSize)
            {
                _buffer.RemoveAt(0);
            }
        }
    }

    public static JsonObject Query(JsonNode msg)
    {
        int req = (int?)msg["req"] ?? 0;
        int facet = (int?)msg["facet"] ?? -1;
        long since = (long?)msg["since"] ?? 0;
        int x0 = (int?)msg["x0"] ?? 0, y0 = (int?)msg["y0"] ?? 0, x1 = (int?)msg["x1"] ?? 0, y1 = (int?)msg["y1"] ?? 0;
        if (x1 < x0)
        {
            (x0, x1) = (x1, x0);
        }

        if (y1 < y0)
        {
            (y0, y1) = (y1, y0);
        }

        long now = Environment.TickCount64;
        var list = new JsonArray();
        lock (_lock)
        {
            _buffer.RemoveAll(e => now - e.Ms > KeepMs);
            foreach (Entry e in _buffer)
            {
                if (e.Ms <= since || e.Facet != facet || e.X < x0 || e.X > x1 || e.Y < y0 || e.Y > y1)
                {
                    continue;
                }

                if (list.Count >= MaxResults)
                {
                    break;
                }

                list.Add(new JsonObject
                {
                    ["id"] = e.Id,
                    ["serial"] = e.Serial,
                    ["x"] = e.X, ["y"] = e.Y, ["z"] = e.Z, ["facet"] = e.Facet,
                    ["text"] = e.Text,
                    ["hue"] = e.Hue,
                    ["ms"] = e.Ms,
                });
            }
        }

        return new JsonObject
        {
            ["op"] = "speech", ["req"] = req, ["ok"] = true,
            ["count"] = list.Count, ["speech"] = list,
        };
    }

    public static JsonObject Refuse(int req, string error) =>
        new() { ["op"] = "speech", ["req"] = req, ["ok"] = false, ["error"] = error };
}
