#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;

/// <summary>
/// The Zones dock: the shard's own regions (ModernUO Data/regions.json)
/// listed, jumped to, edited and saved, with a one-click bridge onto the
/// variant-atlas themes (paint a theme on every rect of a region).
/// Edits round-trip the file's JSON: unknown fields ($type extras, Parent,
/// GoLocation, Entrance...) are preserved, never rewritten.
/// </summary>
[Tool]
public partial class RegionsDock : EditorDock
{
    private static readonly string[] RegionTypes =
    {
        "BaseRegion", "TownRegion", "DungeonRegion", "NoHousingRegion",
        "GuardedRegion", "JailRegion", "GreenAcresRegion",
    };

    private WorldView _world;
    private LineEdit _search, _name, _music, _theme, _audioPrompt;
    private OptionButton _facetPick, _typePick, _audioLayer;
    private ItemList _list, _rects, _tracks;
    private SpinBox _priority, _audioSecs;
    private Label _status, _count;
    private Button _save;
    private AudioStreamPlayer _previewPlayer;
    private System.Threading.CancellationTokenSource _audioCts = new();
    private Task<GUO.Comfy.ComfyClient.AudioResult> _audioTask;

    private JsonArray _regions;
    private string _path = "";
    private readonly List<int> _shown = new();
    private int _selected = -1;
    private bool _dirty;
    private bool _ready;

    public RegionsDock()
    {
        Name = "UORegions";
        Title = "Regions";
        LayoutKey = "guo_regions";
        DefaultSlot = DockSlot.Bottom;
        AvailableLayouts = DockLayout.Horizontal | DockLayout.Vertical | DockLayout.Floating;
        IconName = "Search";
    }

    internal void Attach(WorldView world)
    {
        if (_world != null)
        {
            _world.RegionClick -= PickRegionAt;
            _world.NewZoneFromArea -= NewZoneFromArea;
        }

        _world = world;
        if (_world != null)
        {
            _world.RegionClick += PickRegionAt;
            _world.NewZoneFromArea += NewZoneFromArea;
        }
    }

    /// <summary>
    /// The map context menu's New-zone items: audio zones go straight to
    /// their manifests (tracks are added in the -audiozones gump), shard
    /// regions land in the list for naming and saving.
    /// </summary>
    private void NewZoneFromArea(WorldView.NewZoneKind kind, int facet, int x0, int y0, int x1, int y1)
    {
        switch (kind)
        {
            case WorldView.NewZoneKind.Music:
            case WorldView.NewZoneKind.Sfx:
            {
                var audio = kind == WorldView.NewZoneKind.Music ? GUO.IO.Audio.ZoneAudio.Music : GUO.IO.Audio.ZoneAudio.Sfx;
                string baseName = kind == WorldView.NewZoneKind.Music ? "New music zone" : "New sfx zone";
                int n = audio.Zones.Count(z => z.Name.StartsWith(baseName)) + 1;
                audio.Zones.Add(new GUO.IO.Audio.ZoneAudio.Zone
                {
                    Name = $"{baseName} {n}",
                    Facet = facet,
                    X0 = x0,
                    Y0 = y0,
                    X1 = x1,
                    Y1 = y1,
                });
                Report(audio.Save(audio.Zones)
                    ? $"'{baseName} {n}' created (add tracks in the -audiozones gump)"
                    : "save failed");
                break;
            }

            default:
            {
                if (_regions == null)
                {
                    Report("no regions.json to add to");
                    return;
                }

                var o = new JsonObject
                {
                    ["$type"] = "BaseRegion",
                    ["Map"] = ShardObjects.MapNames[Math.Clamp(facet, 0, ShardObjects.MapNames.Length - 1)],
                    ["Name"] = "New region",
                    ["Priority"] = 50,
                    ["Area"] = new JsonArray
                    {
                        new JsonObject { ["x1"] = x0, ["y1"] = y0, ["x2"] = x1, ["y2"] = y1 },
                    },
                };
                _regions.Add(o);
                MarkDirty();
                RefreshList();
                _selected = _regions.Count - 1;
                _list.Select(_shown.IndexOf(_selected));
                FillForm();
                Report("shard region added (name it, then Save)");
                break;
            }
        }
    }

    /// <summary>
    /// The PickRegion tool's click: select the smallest region covering the
    /// cell so it can be edited. Stays in the tool for more picks.
    /// </summary>
    private void PickRegionAt(int facet, int x, int y)
    {
        if (_regions == null)
        {
            Report("no regions loaded");
            return;
        }

        int best = -1;
        long bestArea = long.MaxValue;
        int matches = 0;
        for (int i = 0; i < _regions.Count; i++)
        {
            if (_regions[i] is not JsonObject o)
            {
                continue;
            }

            if (ShardObjects.FacetOf(TextOf(o, "Map")) != facet)
            {
                continue;
            }

            foreach ((int x0, int y0, int x1, int y1) in RectsOf(o))
            {
                if (x < Math.Min(x0, x1) || x > Math.Max(x0, x1) || y < Math.Min(y0, y1) || y > Math.Max(y0, y1))
                {
                    continue;
                }

                matches++;
                long area = (long)(Math.Abs(x1 - x0) + 1) * (Math.Abs(y1 - y0) + 1);
                if (area < bestArea)
                {
                    bestArea = area;
                    best = i;
                }

                break;
            }
        }

        if (best < 0)
        {
            Report($"no region at {x},{y}");
            return;
        }

        _selected = best;
        FillForm();
        int shown = _shown.IndexOf(best);
        if (shown >= 0 && _list != null)
        {
            _list.Select(shown);
            _list.EnsureCurrentIsVisible();
        }

        string name = TextOf(_regions[best] as JsonObject, "Name");
        Report(matches > 1
            ? $"picked '{name}' ({matches} overlap, smallest shown)"
            : $"picked '{name}'");
    }

    public string Status => _status?.Text ?? "";
    public int RegionCount => _regions?.Count ?? 0;
    public bool Dirty => _dirty;

    public override void _Ready()
    {
        if (_ready)
        {
            return;
        }

        _ready = true;
        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        var find = new HBoxContainer();
        root.AddChild(find);
        _search = new LineEdit { PlaceholderText = "search regions", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _search.TextChanged += _ => RefreshList();
        find.AddChild(_search);
        _facetPick = new OptionButton();
        _facetPick.AddItem("All facets");
        foreach (string m in ShardObjects.MapNames)
        {
            _facetPick.AddItem(m);
        }

        _facetPick.ItemSelected += _ => RefreshList();
        find.AddChild(_facetPick);
        var reload = new Button { Text = "Reload" };
        reload.Pressed += () => { LoadRegions(); RefreshList(); Report(_regions == null ? "no regions.json (UO_SHARD_DIST?)" : $"{_regions.Count} region(s)"); };
        find.AddChild(reload);

        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 90) };
        _list.ItemSelected += SelectRegion;
        root.AddChild(_list);
        _count = new Label { Text = "" };
        root.AddChild(_count);

        var go = new HBoxContainer();
        root.AddChild(go);
        var show = new Button { Text = "Show" };
        show.Pressed += () => Report(ShowSelected());
        go.AddChild(show);
        var rectFromArea = new Button { Text = "Rect from area", TooltipText = "Replace the rect list with the World tab's Area tool selection." };
        rectFromArea.Pressed += () => Report(RectFromArea());
        go.AddChild(rectFromArea);
        var rectAdd = new Button { Text = "Add rect from area", TooltipText = "Append the Area tool selection as another rect." };
        rectAdd.Pressed += () => Report(RectFromArea(append: true));
        go.AddChild(rectAdd);
        var rectDel = new Button { Text = "Remove rect" };
        rectDel.Pressed += () => Report(RemoveRect());
        go.AddChild(rectDel);

        var edit = new HBoxContainer();
        root.AddChild(edit);
        edit.AddChild(new Label { Text = "Name" });
        _name = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _name.TextChanged += _ => MarkDirty(editName: true);
        edit.AddChild(_name);
        edit.AddChild(new Label { Text = "Type" });
        _typePick = new OptionButton();
        foreach (string t in RegionTypes)
        {
            _typePick.AddItem(t);
        }

        _typePick.ItemSelected += _ => MarkDirty(editType: true);
        edit.AddChild(_typePick);
        edit.AddChild(new Label { Text = "Priority" });
        _priority = new SpinBox { MinValue = 0, MaxValue = 1000, Step = 1, CustomMinimumSize = new Vector2(70, 0) };
        _priority.ValueChanged += _ => MarkDirty(editPriority: true);
        edit.AddChild(_priority);

        var music = new HBoxContainer();
        root.AddChild(music);
        music.AddChild(new Label { Text = "Music" });
        _music = new LineEdit { PlaceholderText = "music id, empty for none", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _music.TextChanged += _ => MarkDirty(editMusic: true);
        music.AddChild(_music);

        _rects = new ItemList { CustomMinimumSize = new Vector2(0, 48) };
        root.AddChild(_rects);

        var theme = new HBoxContainer();
        root.AddChild(theme);
        theme.AddChild(new Label { Text = "Theme", TooltipText = "Variant-atlas theme to paint on every rect of this region." });
        _theme = new LineEdit { PlaceholderText = "theme name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        theme.AddChild(_theme);
        var paint = new Button { Text = "Paint theme on region" };
        paint.Pressed += () => Report(PaintTheme());
        theme.AddChild(paint);

        var audio = new HBoxContainer();
        root.AddChild(audio);
        audio.AddChild(new Label
        {
            Text = "Audio",
            TooltipText = "Generate AI music/sfx for this region, exactly like the in-game gump (same ComfyUI backend).",
        });
        _audioPrompt = new LineEdit { PlaceholderText = "audio prompt", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        audio.AddChild(_audioPrompt);
        _audioSecs = new SpinBox
        {
            MinValue = 1, MaxValue = 300, Step = 1, Value = 60,
            CustomMinimumSize = new Vector2(64, 0),
            TooltipText = "Track length in seconds (1-300).",
        };
        audio.AddChild(_audioSecs);
        _audioLayer = new OptionButton();
        _audioLayer.AddItem("Music");
        _audioLayer.AddItem("SFX");
        _audioLayer.ItemSelected += _ => RefreshTracks();
        audio.AddChild(_audioLayer);
        var genAudio = new Button { Text = "Generate" };
        genAudio.Pressed += GenerateAudioFromUi;
        audio.AddChild(genAudio);

        var listen = new HBoxContainer();
        root.AddChild(listen);
        _tracks = new ItemList { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 44) };
        listen.AddChild(_tracks);
        var play = new Button { Text = "Play" };
        play.Pressed += () => Report(PreviewTrack());
        listen.AddChild(play);
        var stop = new Button { Text = "Stop" };
        stop.Pressed += () => StopPreview();
        listen.AddChild(stop);

        var ops = new HBoxContainer();
        root.AddChild(ops);
        var add = new Button { Text = "New region" };
        add.Pressed += () => Report(NewRegion());
        ops.AddChild(add);
        var del = new Button { Text = "Delete region" };
        del.Pressed += () => Report(DeleteRegion());
        ops.AddChild(del);
        _save = new Button { Text = "Save regions.json" };
        _save.Pressed += () => Report(Save());
        ops.AddChild(_save);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_status);

        LoadRegions();
        RefreshList();
    }

    private void Report(string text)
    {
        if (_status != null)
        {
            _status.Text = text ?? "";
        }

        if (text != null)
        {
            GD.Print($"[GUO editor] regions: {text}");
        }
    }

    private string RegionsPath()
    {
        string dist = EditorData.Setting("UO_SHARD_DIST", "");
        return string.IsNullOrWhiteSpace(dist) ? "" : Path.Combine(dist, "Data", "regions.json");
    }

    private void LoadRegions()
    {
        _regions = null;
        _shown.Clear();
        _selected = -1;
        _dirty = false;
        _path = RegionsPath();
        if (_path.Length == 0 || !File.Exists(_path))
        {
            return;
        }

        try
        {
            _regions = JsonNode.Parse(File.ReadAllText(_path))?.AsArray();
        }
        catch (Exception ex)
        {
            Report($"{_path}: {ex.Message}");
        }
    }

    private static string TextOf(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out string s) ? s : "";

    private void RefreshList()
    {
        if (_list == null)
        {
            return;
        }

        _list.Clear();
        _shown.Clear();
        if (_regions == null)
        {
            _count.Text = "no regions.json";
            return;
        }

        string q = (_search?.Text ?? "").Trim().ToLowerInvariant();
        int facet = (_facetPick?.Selected ?? 0) - 1;
        for (int i = 0; i < _regions.Count; i++)
        {
            if (_regions[i] is not JsonObject o)
            {
                continue;
            }

            string name = TextOf(o, "Name");
            string type = TextOf(o, "$type");
            if (q.Length > 0 && !name.ToLowerInvariant().Contains(q) && !type.ToLowerInvariant().Contains(q))
            {
                continue;
            }

            int f = ShardObjects.FacetOf(TextOf(o, "Map"));
            if (facet >= 0 && f != facet)
            {
                continue;
            }

            int rects = o["Area"] is JsonArray a ? a.Count : o["Area"] != null ? 1 : 0;
            _shown.Add(i);
            _list.AddItem($"{(name.Length > 0 ? name : "(unnamed)")} [{type}] x{rects}");
        }

        _count.Text = $"{_shown.Count} of {_regions.Count} region(s)";
    }

    private JsonObject SelectedRegion() =>
        _selected >= 0 && _regions != null && _selected < _regions.Count
            ? _regions[_selected] as JsonObject : null;

    private void SelectRegion(long item)
    {
        if (item < 0 || item >= _shown.Count)
        {
            return;
        }

        _selected = _shown[(int)item];
        FillForm();
    }

    private void FillForm()
    {
        JsonObject o = SelectedRegion();
        if (o == null || _name == null)
        {
            return;
        }

        _name.Text = TextOf(o, "Name");
        string type = TextOf(o, "$type");
        _typePick.Select(Array.IndexOf(RegionTypes, type) is int t && t >= 0 ? t : 0);
        _priority.Value = o["Priority"] is JsonValue p && p.TryGetValue<int>(out int pr) ? pr : 50;
        _music.Text = TextOf(o, "Music");
        RefreshRects();
        RefreshTracks();
    }

    private void RefreshRects()
    {
        if (_rects == null)
        {
            return;
        }

        _rects.Clear();
        foreach ((int x0, int y0, int x1, int y1) in RectsOf(SelectedRegion()))
        {
            _rects.AddItem($"{x0},{y0} - {x1},{y1}");
        }
    }

    private static List<(int X0, int Y0, int X1, int Y1)> RectsOf(JsonObject o)
    {
        var out_ = new List<(int, int, int, int)>();
        if (o == null)
        {
            return out_;
        }

        JsonNode area = o["Area"];
        var arr = area as JsonArray ?? (area != null ? new JsonArray(area.DeepClone()) : null);
        if (arr == null)
        {
            return out_;
        }

        foreach (JsonNode a in arr)
        {
            if (a is JsonObject r)
            {
                out_.Add((Num(r, "x1"), Num(r, "y1"), Num(r, "x2"), Num(r, "y2")));
            }
        }

        return out_;
    }

    private static int Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out int n) ? n : 0;

    private void MarkDirty(bool editName = false, bool editType = false, bool editPriority = false, bool editMusic = false)
    {
        JsonObject o = SelectedRegion();
        if (o == null || _name == null)
        {
            return;
        }

        // The form writes straight into the region's JSON (unknown fields are
        // untouched); Save only persists it to disk.
        if (editName)
        {
            o["Name"] = _name.Text ?? "";
        }

        if (editType && _typePick.Selected >= 0 && _typePick.Selected < RegionTypes.Length)
        {
            o["$type"] = RegionTypes[_typePick.Selected];
        }

        if (editPriority)
        {
            o["Priority"] = (int)_priority.Value;
        }

        if (editMusic)
        {
            if ((_music.Text ?? "").Trim().Length == 0)
            {
                o.Remove("Music");
            }
            else if (int.TryParse(_music.Text.Trim(), out int m))
            {
                o["Music"] = m;
            }
        }

        if (!_dirty)
        {
            _dirty = true;
            _save.Text = "Save regions.json *";
        }

        RefreshRects();
    }

    private string ShowSelected()
    {
        JsonObject o = SelectedRegion();
        if (o == null)
        {
            return "select a region first";
        }

        var rects = RectsOf(o);
        if (rects.Count == 0)
        {
            return "the region has no rects";
        }

        int f = ShardObjects.FacetOf(TextOf(o, "Map"));
        if (f < 0)
        {
            return $"unknown map '{TextOf(o, "Map")}'";
        }

        var (x0, y0, x1, y1) = rects[0];
        _world?.GoTo(f, (x0 + x1) / 2, (y0 + y1) / 2);
        return $"showing {TextOf(o, "Name")} (turn on the Regions layer for outlines)";
    }

    private string RectFromArea(bool append = false)
    {
        JsonObject o = SelectedRegion();
        if (o == null)
        {
            return "select a region first";
        }

        if (_world?.Area is not { } a)
        {
            return "No Area selection: pick the Area tool in the World tab and click two corners.";
        }

        var arr = append && o["Area"] is JsonArray e ? e : new JsonArray();
        arr.Add(new JsonObject
        {
            ["x1"] = Math.Min(a.X0, a.X1),
            ["y1"] = Math.Min(a.Y0, a.Y1),
            ["x2"] = Math.Max(a.X0, a.X1),
            ["y2"] = Math.Max(a.Y0, a.Y1),
        });
        o["Area"] = arr;
        MarkDirty();
        RefreshRects();
        return append ? "rect appended" : "rects replaced from the Area tool";
    }

    private string RemoveRect()
    {
        JsonObject o = SelectedRegion();
        if (o == null)
        {
            return "select a region first";
        }

        int[] sel = _rects?.GetSelectedItems() ?? Array.Empty<int>();
        if (sel.Length == 0 || o["Area"] is not JsonArray arr || sel[0] >= arr.Count)
        {
            return "select a rect first";
        }

        arr.RemoveAt(sel[0]);
        MarkDirty();
        RefreshRects();
        return "rect removed";
    }

    private string NewRegion()
    {
        if (_regions == null)
        {
            return "no regions.json to add to";
        }

        if (_world?.Area is not { } a)
        {
            return "No Area selection: pick the Area tool in the World tab and click two corners first.";
        }

        int facet = (_facetPick?.Selected ?? 0) - 1;
        facet = facet < 0 ? 0 : facet;
        var o = new JsonObject
        {
            ["$type"] = "BaseRegion",
            ["Map"] = ShardObjects.MapNames[Math.Clamp(facet, 0, ShardObjects.MapNames.Length - 1)],
            ["Name"] = "New region",
            ["Priority"] = 50,
            ["Area"] = new JsonArray
            {
                new JsonObject
                {
                    ["x1"] = Math.Min(a.X0, a.X1),
                    ["y1"] = Math.Min(a.Y0, a.Y1),
                    ["x2"] = Math.Max(a.X0, a.X1),
                    ["y2"] = Math.Max(a.Y0, a.Y1),
                },
            },
        };
        _regions.Add(o);
        MarkDirty();
        RefreshList();
        _selected = _regions.Count - 1;
        _list.Select(_shown.IndexOf(_selected));
        FillForm();
        return "region added (name it, then Save)";
    }

    private string DeleteRegion()
    {
        JsonObject o = SelectedRegion();
        if (o == null || _regions == null)
        {
            return "select a region first";
        }

        string name = TextOf(o, "Name");
        _regions.RemoveAt(_selected);
        _selected = -1;
        MarkDirty();
        RefreshList();
        FillForm();
        return $"deleted '{name}' (Save commits)";
    }

    /// <summary>Write the file back (a .bak of the previous one is kept).</summary>
    public string Save()
    {
        if (_regions == null)
        {
            return "nothing loaded";
        }

        if (_path.Length == 0)
        {
            return "no regions.json path (UO_SHARD_DIST?)";
        }

        try
        {
            if (File.Exists(_path))
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
            }

            File.WriteAllText(_path, _regions.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        catch (Exception ex)
        {
            return $"save failed: {ex.Message}";
        }

        _dirty = false;
        _save.Text = "Save regions.json";
        return $"saved {_regions.Count} region(s) (shard restart picks it up)";
    }

    /// <summary>
    /// Paint a variant-atlas theme on every rect of the selected region:
    /// the shard zones become client theme zones. An empty theme name does
    /// the reverse: it cuts the region's rects out of every painted zone
    /// (splitting them, never dropping coverage elsewhere) so the region
    /// shows originals again.
    /// </summary>
    public string PaintTheme()
    {
        JsonObject o = SelectedRegion();
        if (o == null)
        {
            return "select a region first";
        }

        string theme = (_theme?.Text ?? "").Trim();
        if (theme.Length == 0)
        {
            return ClearRegionTheme(o);
        }

        int f = ShardObjects.FacetOf(TextOf(o, "Map"));
        if (f < 0)
        {
            return $"unknown map '{TextOf(o, "Map")}'";
        }

        var rects = RectsOf(o);
        if (rects.Count == 0)
        {
            return "the region has no rects";
        }

        string dir = VariantAtlas.ResolveDir();
        string path = dir != null ? VariantAtlas.ThemePath(dir, theme) : null;
        if (path == null || !File.Exists(path))
        {
            return $"no atlas theme '{theme}': save a variant under it first";
        }

        GUO.Game.Managers.Theme data;
        try
        {
            data = GUO.Game.Managers.Theme.Load(path);
        }
        catch (Exception ex)
        {
            return $"could not read {path}: {ex.Message}";
        }

        var zones = new List<GUO.Game.Managers.ThemeZone>();
        foreach ((int x0, int y0, int x1, int y1) in rects)
        {
            zones.Add(new GUO.Game.Managers.ThemeZone { Facet = f, X1 = x0, Y1 = y0, X2 = x1, Y2 = y1 });
        }

        GUO.Game.Managers.ThemeManager.Activate(data, zones);
        VariantAtlas.LoadTheme(dir, data.Name);
        VariantAtlas.SaveActive(dir);
        GUO.Game.World world = _world?.Host?.World;
        if (world == null)
        {
            return $"theme {theme} armed on {rects.Count} rect(s) (no world open to repaint)";
        }

        GUO.Game.Managers.ThemeManager.Reapply(world);
        return $"theme {theme} paints '{TextOf(o, "Name")}' ({rects.Count} rect(s))";
    }

    /// <summary>
    /// Cuts the region's rects out of every painted theme zone (rect
    /// subtraction, so coverage outside the region survives) and repaints.
    /// Themes left with no zones are deactivated.
    /// </summary>
    public string ClearRegionTheme(JsonObject o)
    {
        int f = ShardObjects.FacetOf(TextOf(o, "Map"));
        var rects = RectsOf(o);
        if (rects.Count == 0)
        {
            return "the region has no rects";
        }

        var cut = new List<(int X0, int Y0, int X1, int Y1)>();
        foreach ((int x0, int y0, int x1, int y1) in rects)
        {
            cut.Add((Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1)));
        }

        int touched = 0, dropped = 0;
        foreach (GUO.Game.Managers.ThemeManager.ActiveTheme a in GUO.Game.Managers.ThemeManager.Active.ToList())
        {
            if (a?.Theme == null)
            {
                continue;
            }

            var kept = new List<GUO.Game.Managers.ThemeZone>();
            bool changed = false;
            foreach (GUO.Game.Managers.ThemeZone z in a.Zones)
            {
                var pieces = new List<(int X0, int Y0, int X1, int Y1)> { NormZone(z) };
                foreach (var u in cut)
                {
                    if (z.Facet >= 0 && z.Facet != f)
                    {
                        continue;
                    }

                    var next = new List<(int X0, int Y0, int X1, int Y1)>();
                    foreach (var p in pieces)
                    {
                        next.AddRange(SubtractRect(p, u));
                    }

                    pieces = next;
                }

                if (pieces.Count == 1 && pieces[0] == NormZone(z))
                {
                    kept.Add(z);
                }
                else
                {
                    changed = true;
                    foreach (var p in pieces)
                    {
                        kept.Add(new GUO.Game.Managers.ThemeZone
                        {
                            Facet = z.Facet, X1 = p.X0, Y1 = p.Y0, X2 = p.X1, Y2 = p.Y1,
                        });
                    }
                }
            }

            if (!changed)
            {
                continue;
            }

            touched++;
            a.Zones.Clear();
            a.Zones.AddRange(kept);
            if (kept.Count == 0)
            {
                GUO.Game.Managers.ThemeManager.Deactivate(a.Theme.Name);
                dropped++;
            }
        }

        if (touched == 0)
        {
            return "no painted theme touches this region";
        }

        GUO.Game.Managers.ThemeManager.Touch();
        string dir = VariantAtlas.ResolveDir();
        if (dir != null)
        {
            VariantAtlas.SaveActive(dir);
        }

        GUO.Game.World world = _world?.Host?.World;
        if (world != null)
        {
            GUO.Game.Managers.ThemeManager.Reapply(world);
        }

        return dropped > 0
            ? $"cleared {touched} theme(s) on '{TextOf(o, "Name")}' ({dropped} deactivated, originals restored)"
            : $"cleared paint on '{TextOf(o, "Name")}' ({touched} theme(s) trimmed, originals restored)";
    }

    private static (int X0, int Y0, int X1, int Y1) NormZone(GUO.Game.Managers.ThemeZone z) =>
        (Math.Min(z.X1, z.X2), Math.Min(z.Y1, z.Y2), Math.Max(z.X1, z.X2), Math.Max(z.Y1, z.Y2));

    /// <summary>What is left of rect p once u is cut out (up to four strips, none when covered).</summary>
    private static List<(int X0, int Y0, int X1, int Y1)> SubtractRect(
        (int X0, int Y0, int X1, int Y1) p, (int X0, int Y0, int X1, int Y1) u)
    {
        var out_ = new List<(int, int, int, int)>();
        int ix0 = Math.Max(p.X0, u.X0), iy0 = Math.Max(p.Y0, u.Y0);
        int ix1 = Math.Min(p.X1, u.X1), iy1 = Math.Min(p.Y1, u.Y1);
        if (ix0 > ix1 || iy0 > iy1)
        {
            out_.Add(p);
            return out_;
        }

        // Inclusive bounds: the strips around the overlap stay covered.
        if (p.Y0 < iy0)
        {
            out_.Add((p.X0, p.Y0, p.X1, iy0 - 1));
        }

        if (iy1 < p.Y1)
        {
            out_.Add((p.X0, iy1 + 1, p.X1, p.Y1));
        }

        if (p.X0 < ix0)
        {
            out_.Add((p.X0, iy0, ix0 - 1, iy1));
        }

        if (ix1 < p.X1)
        {
            out_.Add((ix1 + 1, iy0, p.X1, iy1));
        }

        return out_;
    }

    // --- region audio (same backend as the in-game gump) -------------------------

    private GUO.IO.Audio.ZoneAudio.Layer AudioLayer =>
        (_audioLayer?.Selected ?? 0) == 1 ? GUO.IO.Audio.ZoneAudio.Sfx : GUO.IO.Audio.ZoneAudio.Music;

    /// <summary>Region-bound audio zone names: "<name>", "<name> 2", ...</summary>
    private static string AudioZoneName(string region, int i) =>
        i == 0 ? region : $"{region} {i + 1}";

    /// <summary>The selected region's audio zones on the picked layer.</summary>
    private List<GUO.IO.Audio.ZoneAudio.Zone> RegionAudioZones()
    {
        var out_ = new List<GUO.IO.Audio.ZoneAudio.Zone>();
        JsonObject o = SelectedRegion();
        if (o == null)
        {
            return out_;
        }

        string name = TextOf(o, "Name");
        int f = ShardObjects.FacetOf(TextOf(o, "Map"));
        foreach (GUO.IO.Audio.ZoneAudio.Zone z in AudioLayer.Zones)
        {
            if ((z.Facet < 0 || z.Facet == f) && z.Name.StartsWith(name) && RegionCovers(o, z))
            {
                out_.Add(z);
            }
        }

        return out_;
    }

    private static bool RegionCovers(JsonObject o, GUO.IO.Audio.ZoneAudio.Zone z)
    {
        foreach ((int x0, int y0, int x1, int y1) in RectsOf(o))
        {
            if (z.X0 == Math.Min(x0, x1) && z.Y0 == Math.Min(y0, y1)
                && z.X1 == Math.Max(x0, x1) && z.Y1 == Math.Max(y0, y1))
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshTracks()
    {
        if (_tracks == null)
        {
            return;
        }

        _tracks.Clear();
        foreach (GUO.IO.Audio.ZoneAudio.Zone z in RegionAudioZones())
        {
            foreach (string t in z.Tracks)
            {
                _tracks.AddItem($"{z.Name}: {t}");
            }
        }
    }

    private async void GenerateAudioFromUi()
    {
        JsonObject o = SelectedRegion();
        if (o == null)
        {
            Report("select a region first");
            return;
        }

        string prompt = (_audioPrompt?.Text ?? "").Trim();
        if (prompt.Length == 0)
        {
            Report("type an audio prompt first");
            return;
        }

        float secs = (float)(_audioSecs?.Value ?? 60);
        if (secs < 1f || secs > 300f)
        {
            Report("Secs must be 1-300.");
            return;
        }

        if (_audioTask != null && !_audioTask.IsCompleted)
        {
            Report("Already generating...");
            return;
        }

        string url = GUO.Comfy.ComfyClient.ResolveUrl();
        Report($"Contacting ComfyUI at {url}...");
        _audioCts = new System.Threading.CancellationTokenSource();
        var layer = AudioLayer;
        try
        {
            _audioTask = Task.Run(() => GUO.Comfy.ComfyClient.GenerateAudioAsync(
                url, prompt, secs, Random.Shared.Next(1, int.MaxValue), null, _audioCts.Token));
            GUO.Comfy.ComfyClient.AudioResult r = await _audioTask;
            Report(OnAudioGenerated(layer, o, r));
        }
        catch (Exception ex)
        {
            Report($"Generate failed: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// <summary>
    /// Files the track (same stem scheme as the gump) and attaches it to one
    /// audio zone per region rect, creating them. Returns the status line.
    /// </summary>
    private string OnAudioGenerated(GUO.IO.Audio.ZoneAudio.Layer layer, JsonObject o, GUO.Comfy.ComfyClient.AudioResult r)
    {
        if (r?.Audio == null || r.Audio.Length == 0)
        {
            return "Empty result.";
        }

        string name = TextOf(o, "Name");
        int f = ShardObjects.FacetOf(TextOf(o, "Map"));
        var rects = RectsOf(o);
        if (rects.Count == 0)
        {
            return "the region has no rects";
        }

        try
        {
            string dir = GUO.IO.Audio.ZoneAudio.TracksDir();
            Directory.CreateDirectory(dir);
            string file = $"{layer.StemFor(name)}_{Directory.EnumerateFiles(dir).Count() + 1:D2}.mp3";
            File.WriteAllBytes(Path.Combine(dir, file), r.Audio);
            for (int i = 0; i < rects.Count; i++)
            {
                var (x0, y0, x1, y1) = rects[i];
                string zoneName = AudioZoneName(name, i);
                var zone = layer.Zones.Find(z => z.Name == zoneName && z.Facet == f
                    && z.X0 == Math.Min(x0, x1) && z.Y0 == Math.Min(y0, y1)
                    && z.X1 == Math.Max(x0, x1) && z.Y1 == Math.Max(y0, y1));
                if (zone == null)
                {
                    zone = new GUO.IO.Audio.ZoneAudio.Zone
                    {
                        Name = zoneName,
                        Facet = f,
                        X0 = Math.Min(x0, x1),
                        Y0 = Math.Min(y0, y1),
                        X1 = Math.Max(x0, x1),
                        Y1 = Math.Max(y0, y1),
                    };
                    layer.Zones.Add(zone);
                }

                if (!zone.Tracks.Contains(file))
                {
                    zone.Tracks.Add(file);
                }
            }

            if (!layer.Save(layer.Zones))
            {
                return "track saved but the manifest failed";
            }
        }
        catch (Exception ex)
        {
            return $"Save failed: {ex.Message}";
        }

        RefreshTracks();
        return $"track attached to {rects.Count} zone(s) for '{name}'";
    }

    private string PreviewTrack()
    {
        int[] sel = _tracks?.GetSelectedItems() ?? Array.Empty<int>();
        if (sel.Length == 0)
        {
            return "select a track first";
        }

        string row = _tracks.GetItemText(sel[0]);
        int at = row.LastIndexOf(':');
        string file = (at >= 0 ? row.Substring(at + 1) : row).Trim();
        string path = Path.Combine(GUO.IO.Audio.ZoneAudio.TracksDir(), file);
        if (!File.Exists(path))
        {
            return $"missing file {file}";
        }

        try
        {
            if (_previewPlayer == null || !IsInstanceValid(_previewPlayer))
            {
                _previewPlayer = new AudioStreamPlayer();
                AddChild(_previewPlayer);
            }

            var stream = new AudioStreamMP3();
            stream.Data = File.ReadAllBytes(path);
            _previewPlayer.Stream = stream;
            _previewPlayer.Play();
        }
        catch (Exception ex)
        {
            return $"cannot play: {ex.Message}";
        }

        return $"playing {file} (Stop to silence)";
    }

    private void StopPreview()
    {
        try
        {
            _previewPlayer?.Stop();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Cancels audio work and preview; called before a reload or when the dock closes.</summary>
    public void Shutdown()
    {
        try
        {
            _audioCts?.Cancel();
        }
        catch (Exception)
        {
        }

        _audioCts = new System.Threading.CancellationTokenSource();
        StopPreview();
    }
}
#endif
