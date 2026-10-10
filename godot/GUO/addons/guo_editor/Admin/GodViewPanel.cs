#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

/// <summary>
/// The Admin tab's god view (sprint "Admin tab", AD2a): every player, NPC and spawner on one facet of the server,
/// over the facet's radar, kept current by the bridge's change-only pushes (<c>admin_godview</c>, docs/data_formats.md
/// section 10). Filters hide a kind; Find looks on every facet (<c>admin_godview_find</c>). Read-only: the actions
/// (Follow, Go there, Bring here, paperdoll, respawn) are AD2b.
/// </summary>
/// <remarks>
/// The map is the Maps panel's radar (one image pixel per 4 cells, nearest sampled), or a dark field when the
/// client data is not loaded. Markers are whole-pixel squares in the notoriety colours of docs/ui/uo_godot_style.md:
/// players a 5 px square (staff gold), NPCs 3 px, spawners a hollow 7 px square, in the editor's scale and doubled
/// when zoomed in close. A selected spawner draws a line to
/// each creature it spawned.
/// </remarks>
[Tool]
public partial class GodViewPanel : VBoxContainer
{
    /// <summary>Cells per radar pixel (MapPanel's overview stride).</summary>
    public const int CellsPerPixel = 4;

    private static readonly Color PlayerColour = new("#3cc83c");
    private static readonly Color StaffColour = new("#e0b050");
    private static readonly Color SpawnerColour = new("#40d0e0");
    private static readonly Color SelectColour = new(1, 0, 1);
    private static readonly Color Outline = new("#1c1812");

    private OptionButton _facet;
    private Button _players, _npcs, _spawners;
    private LineEdit _find;
    private RadarView _map;
    private RichTextLabel _details;
    private ItemList _results;
    private Label _status;
    private readonly Dictionary<uint, JsonObject> _rows = new();
    private readonly Dictionary<int, ImageTexture> _radar = new();
    private JsonArray _facets;
    private JsonNode _counts;
    private long _seq;
    private DateTime _updatedAt;
    private uint? _pendingSelect;
    private int _req;
    private bool _filling;
    private bool _awaitingFull;

    /// <summary>Sends an admin op on the tab's bridge link; false when there is no admin channel.</summary>
    public Func<JsonObject, bool> Send { get; set; }

    /// <summary>The radar of a map file (MapPanel.RadarFor), or null without client data.</summary>
    public Func<int, Image> RadarSource { get; set; }

    /// <summary>Raised with a line for the tab's log.</summary>
    public event Action<string> Logged;

    /// <summary>The facet watched (the server's map index), or -1.</summary>
    public int Facet { get; private set; } = -1;

    /// <summary>Rows held for the facet: players, NPCs and spawners, by serial.</summary>
    public IReadOnlyDictionary<uint, JsonObject> Rows => _rows;

    /// <summary>Full lists received (one per subscribe).</summary>
    public int FullReplies { get; private set; }

    /// <summary>Change-only pushes received.</summary>
    public int Pushes { get; private set; }

    /// <summary>Rows the last push changed or added.</summary>
    public int LastPushUpserts { get; private set; }

    /// <summary>The last admin_godview_find reply.</summary>
    public JsonNode LastFind { get; private set; }

    /// <summary>The selected row's serial, or null.</summary>
    public uint? Selected { get; private set; }

    /// <summary>The details panel's text.</summary>
    public string DetailsText => _details?.GetParsedText() ?? "";

    /// <summary>The status line under the map.</summary>
    public string StatusText => _status?.Text ?? "";

    public bool ShowPlayers => _players?.ButtonPressed ?? true;

    public bool ShowNpcs => _npcs?.ButtonPressed ?? true;

    public bool ShowSpawners => _spawners?.ButtonPressed ?? true;

    /// <summary>The map control (the smoke check frames it).</summary>
    public RadarView Map => _map;

    public GodViewPanel()
    {
        Name = "GodView";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        if (_map != null)
        {
            return;
        }

        var bar = new HBoxContainer();
        AddChild(bar);
        _facet = new OptionButton { TooltipText = "The facet to watch. Each has its own players, NPCs and spawners." };
        _facet.ItemSelected += i =>
        {
            if (!_filling)
            {
                Watch(_facet.GetItemId((int)i));
            }
        };
        bar.AddChild(_facet);
        _players = Toggle("Players", "Show players (green; staff in gold)");
        bar.AddChild(_players);
        _npcs = Toggle("NPCs", "Show NPCs and creatures, in their notoriety colour");
        bar.AddChild(_npcs);
        _spawners = Toggle("Spawners", "Show spawners (hollow squares); a selected one draws a line to each creature it spawned");
        bar.AddChild(_spawners);
        _find = new LineEdit
        {
            PlaceholderText = "Find: a name, a creature or 0x serial",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClearButtonEnabled = true,
        };
        _find.TextSubmitted += t => Find(t);
        bar.AddChild(_find);
        var find = new Button { Text = "Find", TooltipText = "Looks on every facet of the server" };
        find.Pressed += () => Find(_find.Text);
        bar.AddChild(find);
        var fit = new Button { Text = "Fit", TooltipText = "The whole facet (the wheel zooms, a drag pans)" };
        fit.Pressed += () => _map.ZoomToFit();
        bar.AddChild(fit);

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);
        _map = new RadarView { Overlay = DrawMarkers };
        _map.Clicked += PickAt;
        split.AddChild(_map);

        var side = new VBoxContainer { CustomMinimumSize = new Vector2(Px(260), 0) };
        split.AddChild(side);
        side.AddChild(new Label { Text = "Selected" });
        _details = new RichTextLabel
        {
            BbcodeEnabled = true, SelectionEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill,
            Text = "Click a marker on the map, or Find one.",
        };
        side.AddChild(_details);
        side.AddChild(new Label { Text = "Find results" });
        _results = new ItemList { CustomMinimumSize = new Vector2(0, Px(150)), SizeFlagsVertical = SizeFlags.ExpandFill };
        _results.ItemSelected += i => ShowResult((int)i);
        side.AddChild(_results);

        _status = new Label { Text = "not watching: connect with the admin channel", ClipText = true };
        AddChild(_status);
    }

    private Button Toggle(string text, string tip)
    {
        var b = new Button { Text = text, ToggleMode = true, ButtonPressed = true, TooltipText = tip };
        b.Toggled += _ =>
        {
            _map?.QueueRedraw();
            UpdateStatus();
        };
        return b;
    }

    private static int Px(int size) => (int)Math.Round(size * EditorInterface.Singleton.GetEditorScale());

    /// <summary>Shows or hides one kind ("player", "npc", "spawner").</summary>
    public void SetFilter(string kind, bool on)
    {
        Button b = kind switch { "player" => _players, "npc" => _npcs, "spawner" => _spawners, _ => null };
        if (b != null)
        {
            b.ButtonPressed = on;
        }
    }

    /// <summary>The admin channel opened: watch the facet watched before, or the first.</summary>
    public void OnAdminOpen() => Watch(Facet < 0 ? 0 : Facet);

    /// <summary>The link closed: the rows stay on screen, marked stale.</summary>
    public void OnClosed()
    {
        _seq = 0;
        if (_status != null)
        {
            _status.Text = _rows.Count > 0 ? $"not connected: showing the last state, from {Clock(_updatedAt)}" : "not watching: connect with the admin channel";
        }
    }

    /// <summary>Watches a facet: the bridge answers with all of it, then pushes changes.</summary>
    public bool Watch(int facet)
    {
        if (Send == null || !Send(new JsonObject { ["op"] = "admin_godview", ["facet"] = facet, ["watch"] = true, ["req"] = ++_req }))
        {
            return false;
        }

        if (facet != Facet)
        {
            _rows.Clear();
            Selected = null;
            ShowDetails();
        }

        Facet = facet;
        _seq = 0;
        _awaitingFull = true;
        if (_status != null)
        {
            _status.Text = $"loading {FacetName(facet)}...";
        }

        return true;
    }

    /// <summary>Asks the bridge for matches on every facet; the results list fills when it answers.</summary>
    public bool Find(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (_find != null)
        {
            _find.Text = text;
        }

        return Send != null && Send(new JsonObject { ["op"] = "admin_godview_find", ["text"] = text, ["req"] = ++_req });
    }

    /// <summary>Selects a row on the watched facet and centres the map on it.</summary>
    public bool Select(uint serial, bool centre = true)
    {
        if (!_rows.TryGetValue(serial, out JsonObject row))
        {
            return false;
        }

        Selected = serial;
        if (centre)
        {
            _map.Focus(new Vector2I((int)row["x"] / CellsPerPixel, (int)row["y"] / CellsPerPixel), 12f);
        }

        ShowDetails();
        _map.QueueRedraw();
        return true;
    }

    /// <summary>An admin_godview or admin_godview_find message from the bridge.</summary>
    public void Handle(JsonNode msg)
    {
        if ((string)msg["op"] == "admin_godview_find")
        {
            OnFind(msg);
            return;
        }

        if ((bool?)msg["ok"] != true)
        {
            Logged?.Invoke($"[color=orange]god view refused: {(string)msg["error"]}[/color]");
            return;
        }

        if (msg["facet"] == null)
        {
            return;
        }

        int facet = (int)msg["facet"];
        if (facet != Facet)
        {
            return;
        }

        bool full = (bool?)msg["full"] == true;
        long seq = (long?)msg["seq"] ?? 0;
        if (!full && _awaitingFull)
        {
            // A push from before the last subscribe: the full list that follows replaces it.
            return;
        }

        if (!full && seq != _seq + 1)
        {
            // A push was missed (or arrived before the full list): ask for the whole facet again.
            Watch(Facet);
            return;
        }

        if (msg["facets"] is JsonArray facets)
        {
            FillFacets(facets);
        }

        if (full)
        {
            _awaitingFull = false;
            _rows.Clear();
            FullReplies++;
        }
        else
        {
            Pushes++;
        }

        int upserts = 0;
        if (msg["upsert"] is JsonArray up)
        {
            foreach (JsonNode n in up)
            {
                _rows[(uint)n["serial"]] = n.AsObject();
                upserts++;
            }
        }

        if (msg["removed"] is JsonArray gone)
        {
            foreach (JsonNode n in gone)
            {
                _rows.Remove((uint)n);
            }
        }

        LastPushUpserts = full ? LastPushUpserts : upserts;
        _seq = seq;
        _counts = msg.DeepClone();
        _updatedAt = DateTime.UtcNow;
        if (full)
        {
            _map.Texture = RadarTexture(facet);
            Logged?.Invoke($"god view: watching {FacetName(facet)}, {Count("players")} players, {Count("npcs")} NPCs, {Count("spawners")} spawners"
                + (Truncated ? " (capped: only part of the facet is shown)" : ""));
        }

        if (_pendingSelect is uint want && _rows.ContainsKey(want))
        {
            _pendingSelect = null;
            Select(want);
        }
        else if (Selected is uint sel && !_rows.ContainsKey(sel))
        {
            Selected = null;
        }

        ShowDetails();
        UpdateStatus();
        _map.QueueRedraw();
    }

    private int Count(string kind) => (int?)_counts?[kind] ?? 0;

    private bool Truncated => (bool?)_counts?["truncated"]?["mobiles"] == true || (bool?)_counts?["truncated"]?["spawners"] == true;

    private void FillFacets(JsonArray facets)
    {
        _facets = facets;
        _filling = true;
        _facet.Clear();
        foreach (JsonNode f in facets)
        {
            _facet.AddItem($"{(string)f["name"]} ({(int)f["width"]}x{(int)f["height"]})", (int)f["id"]);
        }

        int at = _facet.GetItemIndex(Facet);
        if (at >= 0)
        {
            _facet.Select(at);
        }

        _filling = false;
    }

    private JsonNode FacetInfo(int facet) => _facets?.FirstOrDefault(f => (int)f["id"] == facet);

    /// <summary>The facet's name as the server calls it, or "facet N".</summary>
    public string FacetName(int facet) => (string)FacetInfo(facet)?["name"] ?? $"facet {facet}";

    private ImageTexture RadarTexture(int facet)
    {
        JsonNode info = FacetInfo(facet);
        int map = (int?)info?["map"] ?? facet;
        if (_radar.TryGetValue(map, out ImageTexture cached))
        {
            return cached;
        }

        Image image = null;
        try
        {
            image = RadarSource?.Invoke(map);
        }
        catch (Exception e)
        {
            Logged?.Invoke($"[color=orange]god view: no radar for map {map}: {e.Message}[/color]");
        }

        if (image == null)
        {
            // No client data: a dark field the facet's size, so markers still land where they are.
            int w = Math.Max(1, ((int?)info?["width"] ?? 7168) / CellsPerPixel), h = Math.Max(1, ((int?)info?["height"] ?? 4096) / CellsPerPixel);
            image = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
            image.Fill(new Color(0.09f, 0.08f, 0.07f));
        }
        else
        {
            image = (Image)image.Duplicate();
            // Dimmed, so the markers read over it.
            image.AdjustBcs(0.55f, 0.8f, 1f);
        }

        var tex = ImageTexture.CreateFromImage(image);
        _radar[map] = tex;
        return tex;
    }

    private bool Shown(JsonObject row) => (string)row["kind"] switch
    {
        "player" => ShowPlayers,
        "npc" => ShowNpcs,
        "spawner" => ShowSpawners,
        _ => false,
    };

    /// <summary>A marker's colour: notoriety for NPCs, green for players, gold for staff.</summary>
    internal static Color MarkerColour(JsonObject row) => (string)row["kind"] switch
    {
        "player" => row["staff"] != null ? StaffColour : PlayerColour,
        "spawner" => SpawnerColour,
        _ => (int?)row["notoriety"] switch
        {
            1 => new Color("#3c8cf0"),
            2 => new Color("#3cc83c"),
            5 => new Color("#f0961e"),
            6 => new Color("#e6281e"),
            7 => new Color("#f0e61e"),
            _ => new Color("#a0a0a0"),
        },
    };

    // Whole-pixel squares over the radar; drawn in the order spawners, NPCs, players so players stay on top.
    private void DrawMarkers(Vector2 origin, float scale)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        Vector2 Pos(JsonObject r) => (origin + new Vector2((int)r["x"], (int)r["y"]) / CellsPerPixel * scale).Floor();
        // Whole art pixels: the editor's scale (2 on a 4K display), doubled once the view is close enough to tell cells apart.
        int unit = Math.Max(1, Px(1)) * (scale >= 8 ? 2 : 1);
        var view = new Rect2(Vector2.Zero, _map.Size).Grow(32);
        if (Selected is uint sel && _rows.TryGetValue(sel, out JsonObject chosen) && (string)chosen["kind"] == "spawner" && ShowNpcs)
        {
            Vector2 from = Pos(chosen);
            foreach (JsonObject r in _rows.Values)
            {
                if ((uint?)r["spawner"] == sel)
                {
                    _map.DrawLine(from, Pos(r), new Color(SpawnerColour, 0.7f), unit);
                }
            }
        }

        foreach (string kind in new[] { "spawner", "npc", "player" })
        {
            foreach (JsonObject r in _rows.Values)
            {
                if ((string)r["kind"] != kind || !Shown(r))
                {
                    continue;
                }

                Vector2 p = Pos(r);
                if (!view.HasPoint(p))
                {
                    continue;
                }

                Color c = MarkerColour(r);
                if (kind == "spawner")
                {
                    var box = new Rect2(p - new Vector2(3, 3) * unit, new Vector2(7, 7) * unit);
                    _map.DrawRect(box.Grow(unit), Outline, false, unit);
                    _map.DrawRect(box, (bool?)r["running"] == false ? new Color(c, 0.45f) : c, false, unit);
                }
                else
                {
                    int side = (kind == "player" ? 5 : 3) * unit;
                    var dot = new Rect2(p - new Vector2(side / 2, side / 2), new Vector2(side, side));
                    _map.DrawRect(dot.Grow(unit), Outline);
                    _map.DrawRect(dot, (bool?)r["hidden"] == true ? new Color(c, 0.5f) : c);
                }
            }
        }

        if (Selected is uint s && _rows.TryGetValue(s, out JsonObject picked))
        {
            var ring = new Rect2(Pos(picked) - new Vector2(7, 7) * unit, new Vector2(15, 15) * unit);
            _map.DrawRect(ring.Grow(unit), Outline, false, 2 * unit);
            _map.DrawRect(ring, SelectColour, false, 2 * unit);
        }
    }

    // A click selects the nearest shown marker within a few pixels.
    private void PickAt(Vector2 at)
    {
        uint? best = null;
        float bestD = Px(9);
        foreach ((uint serial, JsonObject r) in _rows)
        {
            if (!Shown(r))
            {
                continue;
            }

            float d = _map.ImageToView(new Vector2((int)r["x"], (int)r["y"]) / CellsPerPixel).DistanceTo(at);
            // Players and NPCs win a tie with the spawner under them.
            if ((string)r["kind"] == "spawner")
            {
                d += 2;
            }

            if (d < bestD)
            {
                bestD = d;
                best = serial;
            }
        }

        Selected = best;
        ShowDetails();
        _map.QueueRedraw();
    }

    private void ShowDetails()
    {
        if (_details == null)
        {
            return;
        }

        _details.Text = Selected is uint s && _rows.TryGetValue(s, out JsonObject row) ? Describe(row) : "Click a marker on the map, or Find one.";
    }

    /// <summary>One row in plain words, for the Selected panel.</summary>
    internal string Describe(JsonObject r)
    {
        var t = new StringBuilder();
        string kind = (string)r["kind"];
        string Hex(uint v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);
        t.Append($"[b]{Escape((string)r["name"])}[/b]\n");
        t.Append(kind switch
        {
            "player" => r["staff"] is JsonNode st ? $"Player, staff ({(string)st})" : "Player",
            "spawner" => "Spawner",
            _ => $"NPC ({(string)r["type"]})",
        });
        t.Append($", serial {Hex((uint)r["serial"])}\n");
        t.Append($"At {(int)r["x"]}, {(int)r["y"]}, z {(int)r["z"]} on {FacetName(Facet)}\n");
        if (kind == "spawner")
        {
            bool running = (bool?)r["running"] == true;
            t.Append(running ? "Running" : "[color=orange]Stopped[/color]");
            t.Append($": {(int?)r["spawned"] ?? 0} of {(int?)r["count"] ?? 0} spawned, home range {(int?)r["homeRange"] ?? 0}\n");
            if ((string)r["nextSpawn"] is { } next)
            {
                t.Append($"Next spawn at {Clock(DateTime.Parse(next, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))}\n");
            }

            t.Append("Spawns:\n");
            if (r["entries"] is JsonArray entries && entries.Count > 0)
            {
                foreach (JsonNode e in entries)
                {
                    t.Append($"  {Escape((string)e["name"])}  {(int?)e["spawned"] ?? 0}/{(int?)e["max"] ?? 0}\n");
                }
            }
            else
            {
                t.Append("  nothing (no entries)\n");
            }

            if ((int?)r["moreEntries"] is > 0 and int more)
            {
                t.Append($"  and {more} more\n");
            }

            int here = _rows.Values.Count(x => (uint?)x["spawner"] == (uint)r["serial"]);
            t.Append($"{here} of its creatures are on this facet now (lines on the map).\n");
        }
        else
        {
            t.Append($"Hits {(int?)r["hits"] ?? 0} / {(int?)r["maxHits"] ?? 0}, body {Hex((uint)(int)r["body"])}\n");
            if (kind == "player")
            {
                t.Append((bool?)r["online"] == true ? "Online\n" : "Logged out (still in the world)\n");
            }
            else
            {
                t.Append($"Notoriety: {Notoriety((int?)r["notoriety"] ?? 0)}\n");
            }

            if ((bool?)r["hidden"] == true)
            {
                t.Append("Hidden\n");
            }

            if ((uint?)r["spawner"] is uint sp)
            {
                t.Append(_rows.TryGetValue(sp, out JsonObject from)
                    ? $"Spawned by {Escape((string)from["name"])} ({Hex(sp)}) at {(int)from["x"]}, {(int)from["y"]}\n"
                    : $"Spawned by spawner {Hex(sp)}\n");
            }
        }

        return t.ToString();
    }

    private static string Notoriety(int n) => n switch
    {
        1 => "innocent (blue)",
        2 => "ally (green)",
        3 => "attackable (grey)",
        4 => "criminal (grey)",
        5 => "enemy (orange)",
        6 => "murderer (red)",
        7 => "invulnerable (yellow)",
        _ => "-",
    };

    private static string Escape(string s) => (s ?? "").Replace("[", "[lb]");

    private void OnFind(JsonNode msg)
    {
        LastFind = msg;
        _results.Clear();
        if ((bool?)msg["ok"] != true)
        {
            Logged?.Invoke($"[color=orange]find refused: {(string)msg["error"]}[/color]");
            return;
        }

        JsonArray matches = msg["matches"] as JsonArray ?? new JsonArray();
        foreach (JsonNode m in matches)
        {
            int i = _results.AddItem($"{(string)m["name"]}  ({(string)m["kind"]}, {FacetName((int)m["facet"])} {(int)m["x"]},{(int)m["y"]})");
            _results.SetItemMetadata(i, m.ToJsonString());
        }

        Logged?.Invoke($"find \"{(string)msg["text"]}\": {matches.Count} found" + ((bool?)msg["truncated"] == true ? " (the first ones only)" : ""));
        if (matches.Count == 0)
        {
            _results.AddItem("nothing found");
            _results.SetItemDisabled(0, true);
        }
    }

    /// <summary>Shows a Find result: on this facet it is selected; on another the view switches facet first.</summary>
    public void ShowResult(int index)
    {
        if (index < 0 || index >= _results.ItemCount || _results.GetItemMetadata(index).VariantType != Variant.Type.String)
        {
            return;
        }

        JsonNode m = JsonNode.Parse((string)_results.GetItemMetadata(index));
        uint serial = (uint)m["serial"];
        int facet = (int)m["facet"];
        if (facet == Facet && Select(serial))
        {
            return;
        }

        _pendingSelect = serial;
        if (facet != Facet)
        {
            Watch(facet);
        }
    }

    private void UpdateStatus()
    {
        if (_status == null || _counts == null)
        {
            return;
        }

        var hidden = new List<string>();
        if (!ShowPlayers)
        {
            hidden.Add("players");
        }

        if (!ShowNpcs)
        {
            hidden.Add("NPCs");
        }

        if (!ShowSpawners)
        {
            hidden.Add("spawners");
        }

        _status.Text = $"{FacetName(Facet)}: {Count("players")} players, {Count("npcs")} NPCs, {Count("spawners")} spawners"
            + (Truncated ? " (capped)" : "")
            + (hidden.Count > 0 ? $"; hiding {string.Join(", ", hidden)}" : "")
            + $"; updated {Clock(_updatedAt)}";
    }

    /// <summary>The tab's one clock: UTC, with a Z.</summary>
    internal static string Clock(DateTime utc) => utc.ToUniversalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
}
#endif
