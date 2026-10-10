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
/// section 10). Filters hide a kind; Find looks on every facet (<c>admin_godview_find</c>). The actions (AD2b) act with
/// the admin's own logged-in staff character, picked in "Act as": Go there, Bring here, Open paperdoll and Follow for a
/// player or NPC, Go there, Respawn and Clear for a spawner (<c>admin_goto</c>, <c>admin_bring</c>, <c>admin_paperdoll</c>,
/// <c>admin_follow</c>, <c>admin_spawner</c>). Follow also keeps the map centred on whoever is followed. With nobody on
/// staff logged in, or "the Admin tab's hidden presence" picked, the server's hidden presence acts (AD2c): Go there and
/// Follow move its spot (a white cross on the map), Bring here brings to that spot, and the paperdoll is listed in the
/// Selected panel, as no client is there to open it.
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
    private OptionButton _actAs;
    private Button _goto, _bring, _paperdoll, _follow, _respawn, _clear;
    private Label _actionHint;
    private readonly List<string> _staffOnline = new();
    private uint? _followLookup;
    private bool _followHidden;
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

    /// <summary>The serial the admin's character follows (Follow is on), or null.</summary>
    public uint? Following { get; private set; }

    /// <summary>The last reply to an action (admin_goto, admin_bring, admin_paperdoll, admin_follow, admin_spawner), or null.</summary>
    public JsonNode LastAction { get; private set; }

    /// <summary>Action replies received, ok or refused.</summary>
    public int ActionReplies { get; private set; }

    /// <summary>"Act as"'s choice for the hidden presence (AD2c): the Admin tab acts with no character of its own.</summary>
    public const string HiddenPresence = "the Admin tab's hidden presence";

    /// <summary>
    /// The character the actions use: a name, <see cref="HiddenPresence"/>, or null for "automatic" (the bridge picks
    /// the one staff character online, or the hidden presence when none is).
    /// </summary>
    public string ActAs => _actAs == null || _actAs.Selected <= 0 ? null : _actAs.GetItemText(_actAs.Selected);

    /// <summary>The hidden presence's spot (facet, x, y) as the last Go there or Follow left it, or null.</summary>
    public (int Facet, int X, int Y)? PresenceAt { get; private set; }

    /// <summary>The last paperdoll the hidden presence read (its "paperdoll" object), or null.</summary>
    public JsonNode LastPaperdoll { get; private set; }

    // Whose paperdoll that is: it is listed only while that one is selected.
    private uint? _paperdollSerial;

    /// <summary>The online staff characters on the watched facet, the choices of "Act as".</summary>
    public IReadOnlyList<string> StaffOnline => _staffOnline;

    /// <summary>The action hint under the buttons (why some are off).</summary>
    public string ActionHint => _actionHint?.Text ?? "";

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

        // The actions, with the admin's own character.
        var actAs = new HBoxContainer();
        side.AddChild(actAs);
        actAs.AddChild(new Label { Text = "Act as" });
        _actAs = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true,
            TooltipText = "Your own staff character, logged in to this server: Go there and Follow move it, Bring here brings to it, "
                + "the paperdoll opens in its client. Staff characters online on this facet are listed. With nobody on staff online, "
                + "or the hidden presence picked, the server's hidden presence acts: no character moves, its spot does.",
        };
        _actAs.AddItem(AutoText(0));
        _actAs.AddItem(HiddenPresence);
        _actAs.ItemSelected += _ => UpdateActions();
        actAs.AddChild(_actAs);
        var acts = new HFlowContainer();
        side.AddChild(acts);
        _goto = ActionButton(acts, "Go there", "Moves your character (or the hidden presence's spot) to it, on its facet", () => Act("admin_goto"));
        _bring = ActionButton(acts, "Bring here", "Brings it to your character (or to the hidden presence's spot)", () => Act("admin_bring"));
        _paperdoll = ActionButton(acts, "Open paperdoll",
            "Opens its paperdoll in your character's client (for the hidden presence, lists it under Selected)", () => Act("admin_paperdoll"));
        _follow = ActionButton(acts, "Follow",
            "Your character (or the hidden presence's spot) keeps beside it, and the map keeps it in the centre; press again to stop",
            () => ToggleFollow());
        _respawn = ActionButton(acts, "Respawn", "Removes what this spawner spawned and spawns its full count again", () => Act("admin_spawner", "respawn"));
        _clear = ActionButton(acts, "Clear", "Removes what this spawner spawned; it spawns again on its own timer while it runs",
            () => Act("admin_spawner", "clear"));
        _actionHint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.7f) };
        side.AddChild(_actionHint);

        side.AddChild(new Label { Text = "Find results" });
        _results = new ItemList { CustomMinimumSize = new Vector2(0, Px(150)), SizeFlagsVertical = SizeFlags.ExpandFill };
        _results.ItemSelected += i => ShowResult((int)i);
        side.AddChild(_results);

        _status = new Label { Text = "not watching: connect with the admin channel", ClipText = true };
        AddChild(_status);
        UpdateActions();
    }

    private static Button ActionButton(Container parent, string text, string tip, Action pressed)
    {
        var b = new Button { Text = text, TooltipText = tip };
        b.Pressed += pressed;
        parent.AddChild(b);
        return b;
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
        // The bridge ends a Follow when its editor leaves.
        Following = null;
        _followLookup = null;
        UpdateActions();
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

        if ((string)msg["op"] is "admin_goto" or "admin_bring" or "admin_paperdoll" or "admin_follow" or "admin_spawner")
        {
            OnAction(msg);
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

        if (Following is uint followed)
        {
            if (_rows.TryGetValue(followed, out JsonObject f))
            {
                _followLookup = null;
                if (_followHidden)
                {
                    // The bridge moves the presence's spot with whoever it follows: the cross goes along.
                    PresenceAt = (Facet, (int)f["x"], (int)f["y"]);
                }

                Centre(followed);
            }
            else if (_followLookup != followed)
            {
                // The followed one left this facet, and the character with it: find where, and watch that facet.
                _followLookup = followed;
                Find("0x" + followed.ToString("X", CultureInfo.InvariantCulture));
            }
        }

        UpdateStaff();
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

        DrawPresence(origin, scale, unit);
        if (Selected is uint s && _rows.TryGetValue(s, out JsonObject picked))
        {
            var ring = new Rect2(Pos(picked) - new Vector2(7, 7) * unit, new Vector2(15, 15) * unit);
            _map.DrawRect(ring.Grow(unit), Outline, false, 2 * unit);
            _map.DrawRect(ring, SelectColour, false, 2 * unit);
        }
    }

    // The hidden presence's spot (AD2c): a white cross, outlined, on the facet it is on.
    private void DrawPresence(Vector2 origin, float scale, int unit)
    {
        if (PresenceAt is not { } at || at.Facet != Facet)
        {
            return;
        }

        Vector2 p = (origin + new Vector2(at.X, at.Y) / CellsPerPixel * scale).Floor();
        foreach ((Color c, int grow) in new[] { (Outline, unit), (new Color(1, 1, 1), 0) })
        {
            _map.DrawRect(new Rect2(p - new Vector2(4 * unit + grow, unit / 2 + grow), new Vector2(9 * unit + 2 * grow, unit + 2 * grow)), c);
            _map.DrawRect(new Rect2(p - new Vector2(unit / 2 + grow, 4 * unit + grow), new Vector2(unit + 2 * grow, 9 * unit + 2 * grow)), c);
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

        _details.Text = Selected is uint s && _rows.TryGetValue(s, out JsonObject row)
            ? Describe(row) + (LastPaperdoll is { } pd && _paperdollSerial == s ? DescribePaperdoll(pd) : "")
            : "Click a marker on the map, or Find one.";
        UpdateActions();
    }

    /// <summary>The selected row's kind ("player", "npc", "spawner"), or null.</summary>
    public string SelectedKind => Selected is uint s && _rows.TryGetValue(s, out JsonObject r) ? (string)r["kind"] : null;

    /// <summary>Which action buttons are on, by their text (the smoke check reads them).</summary>
    public IReadOnlyDictionary<string, bool> ActionsEnabled => new[] { _goto, _bring, _paperdoll, _follow, _respawn, _clear }
        .Where(b => b != null).ToDictionary(b => b.Text, b => !b.Disabled);

    /// <summary>Presses an action button by its text, as a click would; false when it is off or there is none.</summary>
    public bool Press(string text)
    {
        Button b = new[] { _goto, _bring, _paperdoll, _follow, _respawn, _clear }.FirstOrDefault(x => x != null && x.Text == text);
        if (b == null || b.Disabled)
        {
            return false;
        }

        b.EmitSignal(BaseButton.SignalName.Pressed);
        return true;
    }

    // The character the actions would move: the one picked, or the only staff character online; null for the presence.
    private string Me => ActAs == HiddenPresence ? null : ActAs ?? (_staffOnline.Count == 1 ? _staffOnline[0] : null);

    /// <summary>True when the actions would be done by the hidden presence: picked, or automatic with nobody on staff online.</summary>
    public bool PresenceActs => ActAs == HiddenPresence || (ActAs == null && _staffOnline.Count == 0);

    // "Act as"'s first choice, naming what automatic means now.
    private static string AutoText(int staff) => staff switch
    {
        0 => "automatic (nobody on staff online: the hidden presence)",
        1 => "automatic (the one staff character online)",
        _ => "automatic (pick yours: several on staff online)",
    };

    // Each action button is on only for the kind it acts on; a line under them says why the others are off.
    private void UpdateActions()
    {
        if (_goto == null)
        {
            return;
        }

        string kind = SelectedKind;
        bool mobile = kind is "player" or "npc";
        bool self = mobile && Me is { } me && _rows.TryGetValue(Selected!.Value, out JsonObject r) && r["staff"] != null
            && string.Equals((string)r["name"], me, StringComparison.OrdinalIgnoreCase);
        bool linked = Send != null && _seq > 0;
        _goto.Disabled = !linked || kind == null || self;
        _bring.Disabled = !linked || !mobile || self || (PresenceActs && PresenceAt == null);
        _paperdoll.Disabled = !linked || !mobile;
        _follow.Text = Following != null ? "Stop following" : "Follow";
        _follow.Disabled = !linked || (Following == null && (!mobile || self));
        _respawn.Disabled = _clear.Disabled = !linked || kind != "spawner";
        _actionHint.Text = !linked ? "Actions need the admin channel."
            : kind == null ? "Select a player, NPC or spawner to act on it."
            : self ? "That is your own character."
            : PresenceActs
                ? "The hidden presence acts (no character): Go there and Follow move its spot, the white cross; Bring here brings to it"
                  + (PresenceAt == null ? " once Go there has given it one" : "") + "; the paperdoll is listed under Selected."
            : "";
    }

    /// <summary>
    /// Sends an action on the selected row (or <paramref name="serial"/>): admin_goto, admin_bring, admin_paperdoll,
    /// admin_follow, or admin_spawner with <paramref name="spawnerAction"/>. False when there is no link or nothing selected.
    /// </summary>
    public bool Act(string op, string spawnerAction = null, uint? serial = null)
    {
        uint? target = serial ?? Selected;
        if (Send == null || target == null)
        {
            return false;
        }

        var msg = new JsonObject { ["op"] = op, ["serial"] = target.Value, ["req"] = ++_req };
        if (op == "admin_spawner")
        {
            msg["action"] = spawnerAction;
        }
        else if (ActAs == HiddenPresence)
        {
            msg["hidden"] = true;
        }
        else if (ActAs is { } me)
        {
            msg["as"] = me;
        }

        return Send(msg);
    }

    /// <summary>Follow on the selected row, or off when it is on.</summary>
    public bool ToggleFollow()
    {
        if (Following != null)
        {
            return Send != null && Send(new JsonObject { ["op"] = "admin_follow", ["stop"] = true, ["req"] = ++_req });
        }

        return Act("admin_follow");
    }

    /// <summary>
    /// Picks who the actions use, by name, or <see cref="HiddenPresence"/> (null: automatic). False when it is not listed.
    /// </summary>
    public bool SetActAs(string name)
    {
        if (_actAs == null)
        {
            return false;
        }

        int at = 0;
        for (int i = 1; name != null && i < _actAs.ItemCount; i++)
        {
            at = string.Equals(_actAs.GetItemText(i), name, StringComparison.OrdinalIgnoreCase) ? i : at;
        }

        if (name != null && at == 0)
        {
            return false;
        }

        _actAs.Select(at);
        UpdateActions();
        return true;
    }

    // "Act as" lists the online staff characters on the facet; the one picked stays picked while it is listed.
    private void UpdateStaff()
    {
        List<string> now = _rows.Values.Where(r => (string)r["kind"] == "player" && r["staff"] != null && (bool?)r["online"] == true)
            .Select(r => (string)r["name"]).Where(n => !string.IsNullOrEmpty(n)).Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (_actAs == null || now.SequenceEqual(_staffOnline))
        {
            return;
        }

        string picked = ActAs;
        _staffOnline.Clear();
        _staffOnline.AddRange(now);
        _actAs.Clear();
        _actAs.AddItem(now.Count == 1 ? $"automatic (the one staff character online, {now[0]})" : AutoText(now.Count));
        _actAs.AddItem(HiddenPresence);
        foreach (string n in now)
        {
            _actAs.AddItem(n);
        }

        if (picked == null || !SetActAs(picked))
        {
            _actAs.Select(0);
        }
    }

    // The bridge's answer to an action, or the push that ends a Follow.
    private void OnAction(JsonNode msg)
    {
        string op = (string)msg["op"];
        if (op == "admin_follow" && msg["req"] == null)
        {
            Following = null;
            Logged?.Invoke($"[color=orange]Follow ended: {Escape((string)msg["reason"])}[/color]");
            UpdateActions();
            return;
        }

        LastAction = msg;
        ActionReplies++;
        if ((bool?)msg["ok"] != true)
        {
            Logged?.Invoke($"[color=orange]{ActionName(msg)} refused: {Escape((string)msg["error"])}[/color]");
            UpdateActions();
            return;
        }

        string name = Escape((string)msg["name"]);
        bool hidden = (bool?)msg["hidden"] == true;
        string who = hidden ? "the hidden presence" : Escape((string)msg["as"]);
        string at = msg["x"] != null ? $"{(int)msg["x"]}, {(int)msg["y"]}, z {(int)msg["z"]} on {FacetName((int?)msg["facet"] ?? -1)}" : "";
        switch (op)
        {
            case "admin_goto":
                Logged?.Invoke(msg["name"] != null ? $"Go there: {who} is at {name} ({at})" : $"Go there: {who} is at {at}");
                if (hidden)
                {
                    PlacePresence(msg);
                }

                break;
            case "admin_bring":
                Logged?.Invoke($"Bring here: {name} is with {who} ({at})");
                break;
            case "admin_paperdoll" when hidden:
                LastPaperdoll = msg["paperdoll"];
                _paperdollSerial = (uint?)msg["serial"];
                Logged?.Invoke($"Paperdoll: {name}, read by the hidden presence ({(msg["paperdoll"]?["items"] as JsonArray)?.Count ?? 0} items, under Selected)");
                ShowDetails();
                break;
            case "admin_paperdoll":
                Logged?.Invoke($"Open paperdoll: {name}'s paperdoll is open in {who}'s client");
                break;
            case "admin_follow" when (bool?)msg["following"] == true:
                Following = (uint)msg["serial"];
                _followHidden = hidden;
                Logged?.Invoke($"Follow: {who} follows {name}; the map keeps it in the centre");
                if (hidden)
                {
                    PlacePresence(msg);
                }

                Centre(Following.Value);
                break;
            case "admin_follow":
                Following = null;
                Logged?.Invoke("Follow stopped" + (msg["moves"] != null ? $" ({(int)msg["moves"]} moves)" : ""));
                break;
            case "admin_spawner":
                Logged?.Invoke((string)msg["action"] == "respawn"
                    ? $"Respawn: {name} removed {(int?)msg["before"] ?? 0} and spawned {(int?)msg["spawned"] ?? 0}"
                    : $"Clear: {name} removed {(int?)msg["before"] ?? 0}" + ((bool?)msg["running"] == true ? "; it spawns again on its timer" : ""));
                break;
        }

        UpdateActions();
    }

    private static string ActionName(JsonNode msg) => (string)msg["op"] switch
    {
        "admin_goto" => "Go there",
        "admin_bring" => "Bring here",
        "admin_paperdoll" => "Open paperdoll",
        "admin_follow" => "Follow",
        "admin_spawner" => (string)msg["action"] == "clear" ? "Clear" : "Respawn",
        _ => (string)msg["op"],
    };

    // The hidden presence's spot from an answer (facet, x, y): the cross moves there and the map centres on it.
    private void PlacePresence(JsonNode msg)
    {
        if (msg["x"] == null || msg["facet"] == null)
        {
            return;
        }

        PresenceAt = ((int)msg["facet"], (int)msg["x"], (int)msg["y"]);
        if (PresenceAt.Value.Facet == Facet)
        {
            _map.Focus(new Vector2I(PresenceAt.Value.X / CellsPerPixel, PresenceAt.Value.Y / CellsPerPixel), _map.Zoom);
        }

        _map.QueueRedraw();
    }

    // Follow keeps the followed one in the centre at the zoom the admin chose.
    private void Centre(uint serial)
    {
        if (_rows.TryGetValue(serial, out JsonObject row))
        {
            _map.Focus(new Vector2I((int)row["x"] / CellsPerPixel, (int)row["y"] / CellsPerPixel), _map.Zoom);
        }
    }

    /// <summary>A paperdoll the hidden presence read, in plain words, for the Selected panel.</summary>
    internal static string DescribePaperdoll(JsonNode pd)
    {
        var t = new StringBuilder("\n[b]Paperdoll[/b]");
        if (!string.IsNullOrEmpty((string)pd["title"]))
        {
            t.Append($" ({Escape((string)pd["title"])})");
        }

        var items = pd["items"] as JsonArray ?? new JsonArray();
        if (items.Count == 0)
        {
            t.Append("\nnothing worn");
        }

        foreach (JsonNode i in items)
        {
            int hue = (int?)i["hue"] ?? 0;
            t.Append($"\n{Escape((string)i["layer"])}: {Escape((string)i["name"])} 0x{(int?)i["item_id"] ?? 0:X4}");
            t.Append(hue != 0 ? $", hue {hue}" : "");
        }

        return t.ToString();
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

        if (_followLookup is uint followed)
        {
            int hit = matches.Select((m, i) => (m, i)).FirstOrDefault(x => (uint)x.m["serial"] == followed, (null, -1)).i;
            // Looked up once per facet change: it stays set until the followed one shows on the facet watched.
            if (Following == followed && hit >= 0)
            {
                ShowResult(hit);
            }
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
