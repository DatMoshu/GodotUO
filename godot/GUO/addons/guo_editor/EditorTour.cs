#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;

/// <summary>
/// The scripted editor tour, driven by tools/editor_tour. Present only when
/// the editor was started with <c>-- --guo-editor-tour &lt;dir&gt;</c>. It walks
/// every feature of the addon on the real client install: for each one it puts
/// a caption over the editor (<see cref="TourOverlay"/>), does the thing
/// through the same calls the smoke check uses, asserts it worked, and saves a
/// frame of the editor window with a hold time. <c>tour.json</c> lists the
/// frames and the per-segment outcome; tools/editor_tour stitches the frames
/// into a video and writes summary.md.
/// </summary>
/// <remarks>
/// Nothing here writes to the client install: edits go to a world project
/// under the output folder. It never presses Start server or Start client
/// (they open windows on the desktop); it shows them and their tooltips.
/// The live part needs a private shard's bridge, given with
/// <c>--guo-editor-tour-live-port</c>; without it the Shard dock is shown
/// offline and the segment says so.
/// </remarks>
[Tool]
public partial class EditorTour : Node
{
    public const string Flag = "--guo-editor-tour";
    public const string LivePortFlag = "--guo-editor-tour-live-port";
    public const string SizeFlag = "--guo-editor-tour-size";
    public const string SegmentsFlag = "--guo-editor-tour-segments";

    /// <summary>The neutral name the Shard dock shows during the tour (never the user's account name).</summary>
    private const string TourEditorName = "guo-editor";

    private sealed class Segment
    {
        public string Id, Title, Skipped;
        public readonly List<string> Passed = new(), Failures = new();
        public int FirstFrame, FrameCount;
    }

    private readonly string _out;
    private readonly EditorData _data;
    private readonly AssetsView _assets;
    private readonly InspectorDock _inspector;
    private readonly WorldView _world;
    private readonly ShardDock _shard;
    private readonly RunBar _run;

    private readonly List<Segment> _segments = new();
    private readonly List<(string File, double Seconds, string Segment)> _frames = new();
    private TourOverlay _overlay;
    private Segment _seg;
    private int _segNo, _segTotal;
    private string _framesDir;
    private bool _started;
    private double _waited;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>The F3 popup the plugin made.</summary>
    public SearchPopup Search { get; set; }

    public EditorTour() : this(null, null, null, null, null, null, null)
    {
    }

    public EditorTour(string outDir, EditorData data, AssetsView assets, InspectorDock inspector, WorldView world, ShardDock shard, RunBar run)
    {
        _out = outDir;
        _data = data;
        _assets = assets;
        _inspector = inspector;
        _world = world;
        _shard = shard;
        _run = run;
        Name = "GuoEditorTour";
    }

    public static string OutDirFromArgs() => EditorSmoke.ArgValue(Flag);

    public override void _ExitTree()
    {
        if (_overlay != null && IsInstanceValid(_overlay))
        {
            _overlay.QueueFree();
        }
    }

    public override void _Process(double delta)
    {
        if (_out == null || _started)
        {
            return;
        }

        _waited += delta;
        if (_data.IsLoaded || _data.Error != null)
        {
            _started = true;
            _ = RunAsync();
        }
        else if (_waited > 180)
        {
            _started = true;
            Finish("client data did not load within 180 s");
        }
    }

    // --- plumbing ---------------------------------------------------------

    private async Task Frames(int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task Secs(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private static IEnumerable<T> All<T>(Node root) where T : Node
    {
        foreach (Node c in root.GetChildren())
        {
            if (c is T t)
            {
                yield return t;
            }

            foreach (T d in All<T>(c))
            {
                yield return d;
            }
        }
    }

    private string Scrub(string text)
    {
        string t = Slashes(text ?? "");
        foreach (var (from, to) in new[]
                 {
                     (_data.ClientData, "UO_CLIENT_DATA"),
                     (EditorData.RepoRoot, "."),
                     (System.Environment.UserName, "user"),
                 })
        {
            if (!string.IsNullOrEmpty(from))
            {
                t = System.Text.RegularExpressions.Regex.Replace(
                    t, System.Text.RegularExpressions.Regex.Escape(Slashes(from)), to,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }

        return t;
    }

    private static string Slashes(string text) => text.Replace((char)92, (char)47);

    private bool Private(string text) =>
        !string.IsNullOrEmpty(text) && Scrub(text) != Slashes(text);

    /// <summary>Rewrites any machine path or user name still on screen (a panel's path box, a log) before a frame is taken.</summary>
    private void ScrubUi(Node root)
    {
        foreach (Node n in root.GetChildren())
        {
            switch (n)
            {
                case LineEdit le when Private(le.Text):
                    le.Text = Scrub(le.Text);
                    break;
                case Label l when Private(l.Text):
                    l.Text = Scrub(l.Text);
                    break;
                case RichTextLabel r when Private(r.GetParsedText()):
                    string plain = Scrub(r.GetParsedText());
                    r.Clear();
                    r.AppendText(plain.Replace("[", "[lb]"));
                    break;
                case TextEdit te when Private(te.Text):
                    te.Text = Scrub(te.Text);
                    break;
            }

            ScrubUi(n);
        }
    }

    private void Check(bool ok, string what)
    {
        (ok ? _seg.Passed : _seg.Failures).Add(what);
        if (!ok)
        {
            GD.PrintErr($"[GUO tour] {_seg.Id}: FAILED {what}");
        }
    }

    private void Skip(string why) => _seg.Skipped = why;

    private static double HoldFor(string text) => Math.Clamp(1.8 + (text?.Length ?? 0) / 22.0, 2.5, 7.0);

    private void Say(string body, bool top = false, string title = null)
    {
        _overlay.AtTop = top;
        _overlay.SetCaption($"{_segNo} / {_segTotal}", title ?? _seg.Title, body);
        _holdText = body;
    }

    private string _holdText = "";

    private void MarkControl(Control c, string label = null)
    {
        if (c != null && IsInstanceValid(c) && c.IsVisibleInTree())
        {
            _overlay.Mark(c.GetGlobalRect(), label);
        }
    }

    private void PointAt(Control c)
    {
        if (c != null && c.IsVisibleInTree())
        {
            _overlay.Pointer(c.GetGlobalRect().GetCenter());
        }
    }

    private async Task Shot(double hold = -1, int settle = 4)
    {
        await Frames(settle);
        ScrubUi(EditorInterface.Singleton.GetBaseControl());
        await Frames(2);
        Image img = EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage();
        if (img == null || img.IsEmpty())
        {
            Check(false, "could not capture the editor window");
            return;
        }

        string name = $"{_frames.Count:D4}_{_seg.Id}.png";
        img.SavePng(Path.Combine(_framesDir, name));
        _frames.Add((name, hold < 0 ? HoldFor(_holdText) : hold, _seg.Id));
    }

    /// <summary>Several frames in real time, for something that moves (an animation).</summary>
    private async Task Burst(int count, double interval)
    {
        for (int i = 0; i < count; i++)
        {
            await Secs(interval);
            await Shot(interval, 0);
        }
    }

    private bool InspectorHas(string source, bool image)
    {
        Inspection i = _inspector.Current;
        return i != null && i.Source == source && i.Text.Length > 0 && (!image || i.Image != null && _inspector.Texture != null);
    }

    /// <summary>Types a query into a panel's search box (half, then all), searches, and returns the id selected.</summary>
    private async Task<int?> Query(AssetPanel panel, string query, double hold = -1)
    {
        LineEdit box = All<LineEdit>(panel).FirstOrDefault();
        MarkControl(box, "search");
        if (box != null && query.Length > 3)
        {
            box.Text = query[..(query.Length / 2 + 1)];
            await Shot(0.5, 2);
        }

        int? id = panel.Search(query);
        await Frames(8);
        await Shot(hold);
        return id;
    }

    private async Task ShowTab(AssetPanel panel)
    {
        _assets.MakeVisible();
        _assets.ShowPanel(panel);
        await Frames(6);
    }

    private async Task Run(string id, string title, Func<Task> body)
    {
        _seg = new Segment { Id = id, Title = title, FirstFrame = _frames.Count };
        _segNo++;
        _overlay.ClearMarks();
        _overlay.SetDetail(null);
        if (id != "shard")
        {
            // The shard dock lives in the bottom panel; it covers the view only while it is the subject.
            (GetParent() as EditorPlugin)?.HideBottomPanel();
        }

        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Check(false, $"threw {ex.GetType().Name}: {ex.Message}");
        }

        _seg.FrameCount = _frames.Count - _seg.FirstFrame;
        _segments.Add(_seg);
        _overlay.ClearMarks();
        GD.Print($"[GUO tour] {id}: {(_seg.Skipped != null ? "skipped" : _seg.Failures.Count == 0 ? "ok" : "FAILED")}, {_seg.FrameCount} frame(s)");
    }

    // --- the tour ---------------------------------------------------------

    private string _projectRoot, _overlayRoot, _exportDir;
    private bool WorldUp => _world.IsBooted;

    private async Task RunAsync()
    {
        string fatal = null;
        try
        {
            _framesDir = Path.Combine(_out, "frames");
            Directory.CreateDirectory(_framesDir);
            _projectRoot = Path.Combine(_out, "world_project");
            _overlayRoot = Path.Combine(_out, "overlay_project");
            _exportDir = Path.Combine(_out, "export");

            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            Vector2I window = new(3840, 2160);
            string sizeText = EditorSmoke.ArgValue(SizeFlag);
            if (sizeText != null && sizeText.Split('x') is { Length: 2 } wh && int.TryParse(wh[0], out int ww) && int.TryParse(wh[1], out int wy))
            {
                window = new Vector2I(ww, wy);
            }

            DisplayServer.WindowSetSize(window);
            GD.Print($"[GUO tour] window {window.X}x{window.Y}, editor scale {EditorInterface.Singleton.GetEditorScale():0.00}, screen {DisplayServer.ScreenGetSize()}");
            _shard.EditorName = TourEditorName;
            DisplayServer.WindowSetPosition(new Vector2I(0, 0));
            await Frames(40);
            EditorInterface.Singleton.SetMainScreenEditor("2D");
            await Frames(10);

            _overlay = new TourOverlay { Name = "GuoTourOverlay" };
            EditorInterface.Singleton.GetBaseControl().AddChild(_overlay);
            await Frames(5);

            var plan = new List<(string, string, Func<Task>)>
            {
                ("intro", "The GUO editor", Intro),
                ("layout", "The layout", Layout),
                ("runbar", "Run bar: start a server, start clients", RunBarSeg),
                ("search", "F3: search everything", SearchSeg),
                ("art", "Assets: Art", ArtSeg),
                ("gumps", "Assets: Gumps", GumpSeg),
                ("anims", "Assets: Animations", AnimSeg),
                ("hues", "Assets: Hues", HueSeg),
                ("multis", "Assets: Multis", MultiSeg),
                ("cliloc", "Assets: Cliloc", ClilocSeg),
                ("sounds", "Assets: Sounds", SoundSeg),
                ("parity", "Assets: Parity against the reference renderer", ParitySeg),
                ("bulk", "Assets: Bulk unpack", BulkSeg),
                ("maps", "Assets: Maps, then jump to UO World", MapsSeg),
                ("pick", "World tab: pick and inspect", PickSeg),
                ("layers", "World tab: the Layers menu and seasons", LayersSeg),
                ("guides", "World tab: the Guides menu", GuidesSeg),
                ("minimap", "World tab: the minimap", MinimapSeg),
                ("multi", "World tab: placing a multi", MultiPlaceSeg),
                ("overlay", "World project: the overlay", OverlaySeg),
                ("edit", "World edits with undo and redo", EditSeg),
                ("objects", "World objects: items and spawners", ObjectsSeg),
                ("assetoverlay", "Asset overlay: replace art, never the install", AssetOverlaySeg),
                ("export", "Export and verify", ExportSeg),
                ("shard", "UO Shard dock: live", ShardSeg),
                ("outro", "That is the editor", Outro),
            };
            _segTotal = plan.Count;
            string only = EditorSmoke.ArgValue(SegmentsFlag);
            HashSet<string> wanted = only?.Split(',').ToHashSet();
            var chosen = plan.Where(p => wanted == null || wanted.Contains(p.Item1)).ToList();
            _segTotal = chosen.Count;
            foreach (var (id, title, body) in chosen)
            {
                await Run(id, title, body);
            }
        }
        catch (Exception ex)
        {
            fatal = $"{ex.GetType().Name}: {ex.Message}";
        }

        Finish(fatal);
    }

    private async Task Intro()
    {
        Say("Godot as the Ultima Online workbench: browse every asset the client reads, walk the real world in the game's own renderer, "
            + "edit it in a project overlay, export it to a shard. This tour is scripted and runs on the real client install. "
            + "Nothing in it writes to that install.", title: "The GUO editor");
        Check(_data.IsLoaded, $"client data loaded through the ported loaders in {_data.LoadMilliseconds} ms");
        await Shot(5);
    }

    private async Task Layout()
    {
        (GetParent() as GuoEditorPlugin)?.ResetLayout();
        await Frames(20);
        var art = _assets.Panel<ArtPanel>();
        _assets.MakeVisible();
        _assets.ShowPanel(art);
        _inspector.MakeVisible();
        await Frames(10);
        art.Search("anvil");
        await Frames(15);
        Button assetsTab = All<Button>(EditorInterface.Singleton.GetBaseControl()).FirstOrDefault(b => b.Text == AssetsView.TabName);
        Say("UO Assets is a tab of its own, the whole centre of the editor: the asset tabs, a big grid (cell size S, M or L) and the search at the top. "
            + "Whatever you pick shows in the UO Inspector, which has the full height of the right column.");
        MarkControl(assetsTab, "UO Assets tab");
        MarkControl(_assets, "the grid");
        MarkControl(_inspector, "UO Inspector: preview on top, details below");
        Check(_assets.IsInsideTree() && _inspector.IsInsideTree() && _run.IsInsideTree(), "the UO Assets tab, the inspector dock and the run bar are in the editor");
        Check(assetsTab != null, "UO Assets is a main-screen tab");
        await Shot(5);
        _overlay.ClearMarks();

        Control sceneDock = All<Control>(EditorInterface.Singleton.GetBaseControl()).FirstOrDefault(c => c.Name == "Scene" && c.GetClass() == "SceneTreeDock");
        Control fs = EditorInterface.Singleton.GetFileSystemDock();
        Check(sceneDock != null && fs != null && sceneDock.GetParent() == fs.GetParent(), "Scene and FileSystem share one tabbed dock on the left");
        Check(_inspector.GetParent() is TabContainer { CurrentTab: 0 } tc && tc.GetChild(0) == _inspector, "the UO Inspector is the first tab of the right column, in front of Godot's Inspector");
        Button worldTab = All<Button>(EditorInterface.Singleton.GetBaseControl()).FirstOrDefault(b => b.Text == GuoEditorPlugin.WorldTabName);
        Say("The UO World tab is a main screen beside 2D, 3D and Script: the game's own renderer, read only until you open a world project. "
            + "Scene and FileSystem share the narrow dock on the left; the toolbar carries the run bar; the UO Shard dock sits in the bottom panel beside Output; "
            + "Editor > Reset GUO layout brings this arrangement back.");
        MarkControl(worldTab, "UO World tab");
        MarkControl(_run, "run bar");
        MarkControl(sceneDock, "Scene + FileSystem");
        await Shot(5);
    }

    private async Task RunBarSeg()
    {
        var server = All<OptionButton>(_run).First();
        var count = All<OptionButton>(_run).Last();
        var buttons = All<Button>(_run).Where(b => b is not OptionButton).ToList();
        Label dot = All<Label>(_run).First();
        Button startServer = buttons.First(), startClient = buttons.Last();

        Say("The toolbar runs the same launchers a person runs: a shard (the shared dev shard or this checkout's private ModernUO), then one to four clients. "
            + "This tour only shows them: it never presses Start, which opens windows on the desktop.", top: true);
        MarkControl(_run, "run bar");
        await Shot(4);

        _overlay.ClearMarks();
        MarkControl(server, "which shard");
        _overlay.Tip(server.GetGlobalRect(), server.TooltipText);
        await Shot(4.5);

        server.Select(1);
        server.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        await Secs(1.5);
        await Frames(5);
        _overlay.ClearMarks();
        Say("Choosing the private shard points the status dot at it: green when it answers, grey when nothing listens. "
            + "Start server is off while the shard is up.", top: true);
        MarkControl(dot, "status dot");
        MarkControl(startServer);
        _overlay.Tip(dot.GetGlobalRect(), dot.TooltipText);
        Check(startServer.Text.Length > 0, $"server button reads '{startServer.Text}'");
        await Shot(4.5);

        _overlay.ClearMarks();
        Say("Start client launches launchers\\game\\play.bat logged out; pick 1 to 4 and they tile across the screen. One account per client.", top: true);
        MarkControl(startClient, "Start client");
        MarkControl(count, "how many");
        _overlay.Tip(startClient.GetGlobalRect(), startClient.TooltipText);
        await Shot(4.5);
        count.Select(1);
        count.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        await Frames(3);
        _overlay.Tip(count.GetGlobalRect(), "x 2: the button now reads '" + startClient.Text + "'");
        Check(startClient.Text == "Start 2 clients", "the count dropdown changes the button to 'Start 2 clients'");
        await Shot(3);
        count.Select(0);
        count.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
    }

    private async Task ArtSeg()
    {
        var art = _assets.Panel<ArtPanel>();
        await ShowTab(art);
        Say("Art and tiledata: search by id (0x0E75, 3701) or by name. Land and statics, each with its size and tiledata flags.");
        OptionButton kind = All<OptionButton>(art).First();
        kind.Select(1);
        kind.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        await Frames(4);
        int? land = await Query(art, "0x0003");
        Check(land != null && InspectorHas("Art", true), "a land tile by id shows pixels and text in the inspector");

        Say("The same box searches names: 'anvil' finds the statics called anvil.");
        kind.Select(0);
        kind.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        await Frames(4);
        int? anvil = await Query(art, "anvil");
        Check(anvil != null && InspectorHas("Art", true), "a static by name ('anvil')");

        Say("Pick a static by id: 0x0E75, the backpack. Height, weight, layer, animation id and flags come from tiledata.");
        int? bp = await Query(art, "0x0E75", 5);
        Check(bp == 0x0E75 && InspectorHas("Art", true), "static 0x0E75 by id");
        _overlay.ClearMarks();
        MarkControl(_inspector, "inspector: pixels + tiledata");
        await Shot(3);
    }

    private async Task GumpSeg()
    {
        var gumps = _assets.Panel<GumpPanel>();
        await ShowTab(gumps);
        Say("Gumps: the UI art. The inspector says which classes in src/Game/UI name the gump.");
        int? id = await Query(gumps, "0x07D0");
        if (id == null)
        {
            id = await Query(gumps, "0x0064");
        }

        Check(id != null && InspectorHas("Gumps", true), "a gump by id shows pixels and text");
    }

    private async Task AnimSeg()
    {
        var anims = _assets.Panel<AnimationPanel>();
        await ShowTab(anims);
        Say("Animations: a body, an action and a direction, composited exactly as the client does it. The inspector plays it.");
        int? id = await Query(anims, "0x0190", 1);
        Check(id != null && InspectorHas("Animations", true) && _inspector.Current.Frames.Length > 1, "body 0x0190 gives an animation of several frames");
        _overlay.ClearMarks();
        MarkControl(_inspector, "playing");
        await Burst(7, 0.125);

        var spins = All<SpinBox>(anims).ToList();
        if (spins.Count >= 2)
        {
            Say("Action and direction are spin boxes: here direction 3, then a dragon.");
            MarkControl(spins[1], "direction");
            spins[1].Value = 3;
            await Frames(6);
            await Burst(5, 0.125);
        }

        int? dragon = await Query(anims, "0x003C", 1);
        Check(dragon != null, "a second body (0x003C) is found");
        await Burst(6, 0.125);
    }

    private async Task HueSeg()
    {
        var hues = _assets.Panel<HuePanel>();
        await ShowTab(hues);
        Say("Hues: the 32-colour ramp of any hue, and the art you last picked drawn in it, whole and partially hued, with the game's hue maths.");
        int? a = await Query(hues, "0x0021", 4);
        Check(a != null && InspectorHas("Hues", true), "hue 0x0021 shows a swatch and hued art");
        Say("Another hue.");
        int? b = await Query(hues, "0x0030", 3);
        Check(b != null && InspectorHas("Hues", true), "a second hue");
    }

    private async Task MultiSeg()
    {
        var multis = _assets.Panel<MultiPanel>();
        await ShowTab(multis);
        Say("Multis: houses, keeps and boats as composites, with their component list by tile.");
        int? id = await Query(multis, "0x0064", 5);
        Check(id != null && InspectorHas("Multis", true), "multi 0x0064 shows its composite and parts");
    }

    private async Task ClilocSeg()
    {
        var cliloc = _assets.Panel<ClilocPanel>();
        await ShowTab(cliloc);
        Say("Cliloc: the client's localised strings, searchable, with the language switch.");
        int? id = await Query(cliloc, "backpack", 4);
        Check(id != null && InspectorHas("Cliloc", false), "a cliloc search for 'backpack' finds text");
    }

    private async Task SoundSeg()
    {
        var sounds = _assets.Panel<SoundPanel>();
        await ShowTab(sounds);
        Say("Sounds and music: played through the game's own audio (ADR-0005). The video has no audio track; the editor played it.");
        int? id = await Query(sounds, sounds.SmokeQuery, 4);
        Check(id != null && InspectorHas("Sounds", false), "a sound is selected and described");
        string played = sounds.SmokePlay();
        Check(played == null, "the game's audio started the sound" + (played != null ? $" ({played})" : ""));
        await Shot(2);
    }

    private async Task ParitySeg()
    {
        var parity = _assets.Panel<ParityPanel>();
        await ShowTab(parity);
        if (parity.Unavailable != null)
        {
            Say("Parity compares the port's frame with a reference renderer built on upstream's loaders (UOWW's uoasset). It is not installed here, so the tab is shown without a result.");
            Skip(Scrub(parity.Unavailable));
            await Shot(4);
            return;
        }

        Say("Parity: reference render from UOWW's uoasset get-image (the only verb of it the editor runs) beside the port's frame and a diff.");
        int? id = await Query(parity, "0x0E75", 5);
        Check(id != null && InspectorHas("Parity", true), "reference, port and diff images reach the inspector");
    }

    private async Task BulkSeg()
    {
        var bulk = _assets.Panel<BulkPanel>();
        await ShowTab(bulk);
        Say("Bulk: unpack client assets to PNG with JSON sidecars (tools/uopack), edit them anywhere, pack them into a staged data set. Folders inside the install are refused.");
        await Shot(3);
        int? id = bulk.Search("0x0E75");
        await Frames(8);
        Say("Unpacked static 0x0E75 to build\\editor_bulk, verified by decoding it back equal. The log is in the panel, the PNG in the inspector.");
        Check(id != null && InspectorHas("Bulk", true), "unpack ran, exited 0 and the PNG reached the inspector");
        await Shot(5);
    }

    private async Task MapsSeg()
    {
        var maps = _assets.Panel<MapPanel>();
        await ShowTab(maps);
        await Secs(1);
        Say("Maps: the whole centre is the radar of a facet, one pixel per four cells. The wheel zooms about the pointer, a drag pans.");
        MarkControl(maps.Radar, "radar: wheel zooms, drag pans");
        await Shot(4);
        _overlay.ClearMarks();

        maps.Radar.Focus(new Vector2I(1496 / 4, 1628 / 4), 8);
        await Frames(10);
        Say("Zoomed on Britain. Click a cell: the inspector lists what is on it. Double-click, or the Jump button, goes to UO World.");
        await Shot(4);
        maps.ScriptedClick(1496, 1628);
        await Frames(8);
        Check(InspectorHas("Maps", false) || _inspector.Current != null, "a map cell reaches the inspector");
        var jump = _inspector.Current?.Actions.FirstOrDefault(a => a.Label == "Show in UO World");
        Button jumpButton = All<Button>(_inspector).FirstOrDefault(b => b.Text == "Show in UO World");
        MarkControl(jumpButton, "Show in UO World");
        MarkControl(All<Button>(maps).FirstOrDefault(b => b.Text == "Jump to UO World"), "Jump");
        await Shot(4);
        _overlay.ClearMarks();
        if (jump?.Run == null)
        {
            Check(false, "no Show in UO World button");
            return;
        }

        Say("The first jump boots the game's renderer in the UO World tab.");
        maps.ScriptedClick(1496, 1628, true);
        for (int i = 0; i < 900 && !WorldUp && _world.Error == null; i++)
        {
            await Frames(1);
        }

        await Frames(50);
        Check(WorldUp, "the World tab booted" + (WorldUp ? $" in {_world.Host.BootMilliseconds} ms" : $": {_world.Error}"));
        if (WorldUp)
        {
            Check(_world.Host.Scene.RenderedObjectsCount > 0, $"GameScene drew {_world.Host.Scene.RenderedObjectsCount} objects");
            Check(_world.Host.Facet == 0 && _world.Host.X == 1496, "a double click on the radar jumped to map0 1496,1628");
        }

        Say("The UO World tab: Britain, drawn by GameScene itself. Arrow keys or right-drag pan, the wheel zooms. The minimap is in the corner.");
        MarkControl(_world, "UO World");
        await Shot(5);
    }

    private Vector2I Centre => new((int)_world.Size.X / 2, (int)_world.Size.Y / 2);

    private void Aim(Vector2I at)
    {
        _world.ForcedMouse = at;
        SubViewportContainer c = All<SubViewportContainer>(_world).FirstOrDefault();
        if (c != null)
        {
            _overlay.Pointer(c.GlobalPosition + new Vector2(at.X, at.Y));
        }
    }

    private bool NeedWorld()
    {
        if (WorldUp)
        {
            return true;
        }

        Skip($"the World tab did not boot: {_world.Error}");
        return false;
    }

    private async Task PickSeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        _world.Tool = WorldTool.Select;
        Aim(Centre);
        await Frames(10);
        Inspection picked = _world.InspectPicked();
        await Frames(3);
        Check(picked != null && _inspector.Current == picked, "the game's own PixelPicker found an object and it reached the inspector");
        Say("Click inspects: the game's own pixel picking resolves the click to a land cell or static, and the inspector shows block, cell, hue and name.");
        await Shot(5);
    }

    private async Task Toggle(string text, bool on)
    {
        if (!_world.SetMenuItem(text, on))
        {
            Check(false, $"no '{text}' item in the Layers or Guides menu");
            return;
        }

        await Frames(6);
    }

    private async Task OpenMenu(MenuButton menu, string label)
    {
        _overlay.ClearMarks();
        MarkControl(menu, label);
        menu.ShowPopup();
        await Frames(6);
    }

    private void CloseMenu(MenuButton menu)
    {
        menu.GetPopup().Hide();
    }

    private async Task LayersSeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        _world.ForcedMouse = null;
        _world.GoTo(0, 1496, 1628);
        await Frames(10);
        Say("One Layers menu holds the layer toggles (Land, Statics, Multis, Roofs, Objects) so the toolbar fits at any width. Statics off leaves bare land.");
        await OpenMenu(_world.LayersMenu, "Layers");
        await Shot(3);
        await Toggle("Statics", false);
        await Shot(3);
        Check(!_world.Host.ShowStatics && !_world.MenuItemChecked("Statics"), "Statics off hides the statics layer");
        await Toggle("Statics", true);
        Check(_world.Host.ShowStatics, "Statics back on");
        CloseMenu(_world.LayersMenu);
        _overlay.ClearMarks();

        Say("The season the shard sends for a map: the game's own seasonal graphics. Winter.");
        _world.Season = GUO.Game.Managers.Season.Winter;
        MarkControl(All<OptionButton>(_world).FirstOrDefault(o => o.TooltipText.StartsWith("Season")), "season");
        await Frames(25);
        await Shot(4);
        Check(_world.Season == GUO.Game.Managers.Season.Winter, "the season switches");
        _world.Season = GUO.Game.Managers.Season.Summer;
        await Frames(15);
    }

    private async Task GuidesSeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        _world.GoTo(0, 1496, 1628);
        await Toggle("Grid", true);
        await Toggle("Altitude", true);
        await Frames(15);
        Say("The Guides menu: editor-only guides the game does not draw (the cell grid, altitude numbers, block boundaries) and the minimap switch.");
        await OpenMenu(_world.GuidesMenu, "Guides");
        await Shot(5);
        Check(_world.Guides.CellsDrawn > 0, $"guides drew {_world.Guides.CellsDrawn} cells");
        CloseMenu(_world.GuidesMenu);
        await Frames(5);
        _overlay.ClearMarks();
        await Shot(3);
        await Toggle("Grid", false);
        await Toggle("Altitude", false);
    }

    private async Task MinimapSeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        _overlay.ClearMarks();
        _world.GoTo(0, 1496, 1628);
        await Frames(30);
        MiniMap map = _world.Minimap;
        Say("The minimap: the Maps radar around the camera, with the camera's viewport drawn on it. Click or drag on it to jump.");
        MarkControl(map, "minimap");
        await Shot(4);
        Check(map.HasImage && map.ViewPolygon.Length == 4, "the minimap shows a radar and the viewport rectangle");

        map.ScriptedJump(1330, 1580);
        await Frames(30);
        Check(_world.Host.X == 1330 && _world.Host.Y == 1580, "a click on the minimap moved the camera to 1330,1580");
        Say("A click on the minimap moves the camera there.");
        await Shot(4);

        _world.SetMenuItem("Minimap", false);
        await Frames(6);
        Check(!map.Visible, "the Guides menu hides the minimap");
        _overlay.ClearMarks();
        Say("Guides > Minimap hides it.");
        await Shot(2.5);
        _world.SetMenuItem("Minimap", true);
        await Frames(6);
        _world.GoTo(0, 1496, 1628);
        await Frames(10);
    }

    private async Task MultiPlaceSeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        _overlay.ClearMarks();
        _world.GoTo(0, 1496, 1628);
        await Frames(10);
        Say("Placing a multi: house 0x0064 goes into the world through the same calls a shard's world-object packet makes, so it is drawn exactly as the game draws a house. "
            + "(The editor has no place-a-multi tool yet: this is the server path.)");
        await Shot(2);
        var item = _world.Host.PlaceServerMulti(0x4000_0064, 0x0064, 1500, 1634, 10);
        await Frames(25);
        bool house = _world.Host.World.HouseManager.TryGetHouse(0x4000_0064, out var h) && h.Components.Count > 0;
        Check(item != null && house, house ? $"the house has {h.Components.Count} components in the map" : "the multi was not placed");
        await Shot(5);
        _world.Host.RemoveServerObject(0x4000_0064);
        await Frames(10);
    }

    private string Stamps()
    {
        long Mt(string f) => File.GetLastWriteTimeUtc(f).Ticks;
        var sb = new StringBuilder();
        foreach (string f in Directory.GetFiles(_data.ClientData).OrderBy(f => f))
        {
            string n = Path.GetFileName(f).ToLowerInvariant();
            if (n.StartsWith("map") || n.StartsWith("statics") || n.StartsWith("staidx") || n.StartsWith("art") || n.StartsWith("gump") || n.StartsWith("hues") || n.StartsWith("verdata"))
            {
                sb.Append(n).Append(':').Append(Mt(f)).Append(';');
            }
        }

        return sb.ToString();
    }

    private async Task OverlaySeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        string before = Stamps();
        if (Directory.Exists(_overlayRoot))
        {
            Directory.Delete(_overlayRoot, true);
        }

        WorldProject.OpenOrCreate(_overlayRoot, _data.ClientData, _data.ClientVersion).Dispose();
        WorldProject project = _world.OpenProject(_overlayRoot);
        _world.GoTo(0, 1496, 1628);
        await Frames(15);
        Say("A world project is a folder of ours holding whole replaced blocks over the read-only install. Here is block 187,203 as the install has it.");
        await Shot(3);

        WorldBlock b = WorldProject.Capture(Client.Game.UO.FileManager.Maps, 0, 187, 203);
        for (int i = 0; i < 3; i++)
        {
            b.Statics.Add(new WorldStatic { Id = 0x0CCA, X = (byte)(1 + i * 2), Y = (byte)(1 + i * 2), Z = b.LandZ[(1 + i * 2) * 9] });
        }

        for (int i = 0; i < 64; i++)
        {
            b.LandId[i] = 0x00A8;
        }

        string file = project.WriteBlock(b);
        _world.OpenProject(_overlayRoot);
        int applied = _world.Host.ApplyOverlay();
        await Frames(30);
        Say("The block is written as JSON (blocks/0/187_203.json): all land turned to water, three trees added. The loaders read it before the install; the world shows it at once.");
        _overlay.SetDetail($"{Scrub(file)}\nblocks applied: {applied}   statics in block: {b.Statics.Count}");
        Check(applied > 0 && File.Exists(file), $"the block file was written and {applied} block(s) applied");
        await Shot(6);

        _world.Host.CloseProject();
        await Frames(30);
        _overlay.SetDetail(null);
        string after = Stamps();
        Say("Close the project and the install's block is back. The install's map, statics, art, gump, hues and verdata files kept their modification times throughout.");
        Check(before == after, "the install's files kept their modification times");
        await Shot(5);
    }

    private WorldBlock ProjectBlock(int bx, int by) =>
        _world.Host.Project?.BlockText(0, bx, by) is null ? null : WorldProject.ReadBlock(_world.Host.Project.BlockPath(0, bx, by));

    private const int EditX = 1164, EditY = 1668, EditBx = EditX >> 3, EditBy = EditY >> 3;
    private const ushort Crate = 0x0E3D;

    private string EditDetail()
    {
        WorldBlock b = ProjectBlock(EditBx, EditBy);
        return $"{_world.Editor.LastWhat}   undo {_world.Editor.UndoCount}, redo {_world.Editor.RedoCount}\n"
            + (b == null ? "block 145,208: no file (the install's)" : $"blocks/0/{EditBx}_{EditBy}.json: {b.Statics.Count} static(s)");
    }

    /// <summary>Picks a tool in the dropdown and re-marks it, so the label never names the previous one.</summary>
    private void SetTool(OptionButton dropdown, WorldTool tool)
    {
        _world.Tool = tool;
        _overlay.ClearMarks();
        MarkControl(dropdown, $"tool: {tool}");
        if (_world.ForcedMouse is { } at)
        {
            Aim(at);
        }
    }

    private async Task EditSeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        if (Directory.Exists(_projectRoot))
        {
            Directory.Delete(_projectRoot, true);
        }

        WorldProject.OpenOrCreate(_projectRoot, _data.ClientData, _data.ClientVersion).Dispose();
        _data.OpenAssets(_projectRoot);
        _world.OpenProject(_projectRoot);
        _world.GoTo(0, EditX, EditY);
        Aim(Centre);
        _data.CurrentArt = EditorData.LandCount + Crate;
        OptionButton tool = All<OptionButton>(_world).FirstOrDefault(o => o.TooltipText == "What a left click does");
        await Frames(30);

        Say("Editing: pick a tool, pick art in UO Assets, click. Stamp puts the crate (0x0E3D) on the cell under the pointer. Every edit is written to the project block file.");
        SetTool(tool, WorldTool.Stamp);
        await Shot(3);
        _world.ApplyTool();
        await Frames(25);
        Check(ProjectBlock(EditBx, EditBy)?.Statics.Count(s => s.Id == Crate) == 1, "Stamp wrote one crate into the project block");
        _overlay.SetDetail(EditDetail());
        await Shot(4);

        Vector2I? at = WorldAim.ScreenOf(_world, _data, EditBx, EditBy, Crate);
        Check(at != null, "the crate is on the block");
        if (at is { } p)
        {
            Aim(p);
        }

        await Frames(10);
        Say("Hue: recolour that static (hue 33, red).");
        SetTool(tool, WorldTool.Hue);
        _world.BrushHue = 0x0021;
        _world.ApplyTool();
        await Frames(25);
        Check(ProjectBlock(EditBx, EditBy)?.Statics.Any(s => s.Id == Crate && s.Hue == 0x0021) == true, "Hue recoloured the crate in the project");
        _overlay.SetDetail(EditDetail());
        await Shot(4);

        Say("Raise: lift the land under it one step.");
        SetTool(tool, WorldTool.Raise);
        _world.ApplyTool();
        await Frames(25);
        _overlay.SetDetail(EditDetail());
        await Shot(3.5);

        if (WorldAim.ScreenOf(_world, _data, EditBx, EditBy, Crate) is { } p2)
        {
            Aim(p2);
        }

        await Frames(10);
        Say("Erase: remove the crate.");
        SetTool(tool, WorldTool.Erase);
        _world.ApplyTool();
        await Frames(25);
        Check(ProjectBlock(EditBx, EditBy)?.Statics.Count(s => s.Id == Crate) == 0, "Erase took the crate out of the project");
        _overlay.SetDetail(EditDetail());
        await Shot(3.5);

        for (int i = 0; i < 4; i++)
        {
            Say($"Undo, {i + 1} of 4 (Ctrl+Z).");
            _world.Editor.Undo();
            await Frames(20);
            _overlay.SetDetail(EditDetail());
            await Shot(2);
        }

        Check(ProjectBlock(EditBx, EditBy) == null, "four undos returned the block to the install's (no file)");
        Say("Redo (Ctrl+Y): the crate comes back.");
        _world.Editor.Redo();
        await Frames(20);
        Check(ProjectBlock(EditBx, EditBy)?.Statics.Count(s => s.Id == Crate) == 1, "Redo restored the stamp");
        _overlay.SetDetail(EditDetail());
        await Shot(4);

        // A second stamp so the exported block carries something visible.
        _world.Editor.Stamp(0, EditX + 3, EditY - 4, 0, 0x0CE3, 0);
        _world.ForcedMouse = null;
        _overlay.ClearMarks();
    }

    private async Task ObjectsSeg()
    {
        if (!NeedWorld())
        {
            return;
        }

        _overlay.SetDetail(null);
        // Away from the crate the edit segment left on the block, so it cannot hide the objects.
        _world.GoTo(0, EditX - 8, EditY + 8);
        await Frames(25);
        OptionButton tool = All<OptionButton>(_world).FirstOrDefault(o => o.TooltipText == "What a left click does");
        Say("World objects live on the shard, not in the map: decoration items and spawners, in the project's shard/objects.json. PlaceItem puts an anvil.");
        _data.CurrentArt = EditorData.LandCount + 0x0FAF;
        SetTool(tool, WorldTool.PlaceItem);
        Aim(Centre + new Vector2I(-70, 20));
        await Frames(10);
        _world.ApplyTool();
        await Frames(15);
        int items = _world.Objects.DrawnCount;
        Check(items == 1, "PlaceItem drew one anvil");
        await Shot(3.5);

        Say("PlaceSpawner puts a spawner (here: Horse), drawn as the spawner gem.");
        SetTool(tool, WorldTool.PlaceSpawner);
        Aim(Centre + new Vector2I(70, 20));
        await Frames(10);
        _world.ApplyTool();
        await Frames(15);
        Check(_world.Objects.DrawnCount == 2, "PlaceSpawner added a spawner");
        string file = _world.Objects.Objects?.Path;
        int lines = file != null && File.Exists(file) ? File.ReadAllText(file).Split('\n').Count(l => l.Contains("\"id\"")) : -1;
        _overlay.SetDetail($"shard/objects.json: {lines} object(s)   drawn: {_world.Objects.DrawnCount}");
        Check(lines == 2, "shard/objects.json holds the two objects");
        await Shot(4);

        Say("DeleteObject takes the anvil away again.");
        SetTool(tool, WorldTool.DeleteObject);
        // Aim at the anvil's sprite, as a person would.
        GUO.Game.GameObjects.Item anvil = _world.Objects.DrawnItemWithGraphic(0x0FAF);
        Check(anvil != null, "the placed anvil is in the world");
        if (anvil != null)
        {
            Aim(WorldAim.ScreenOfObject(_world, _data, anvil));
        }

        await Frames(10);
        _world.ApplyTool();
        await Frames(15);
        Check(_world.Objects.DrawnCount == 1, "DeleteObject removed the anvil");
        _overlay.SetDetail($"drawn: {_world.Objects.DrawnCount}");
        await Shot(3.5);
        SetTool(tool, WorldTool.Select);
        _world.ForcedMouse = null;
    }

    private async Task AssetOverlaySeg()
    {
        var art = _assets.Panel<ArtPanel>();
        await ShowTab(art);
        _overlay.SetDetail(null);
        _overlay.ClearMarks();
        string before = Stamps();
        AssetOverlay assets = _data.Assets ?? _data.OpenAssets(_projectRoot);
        Say("Asset overlay: art, gumps and hues are replaced the same way, in the world project. Here is the install's static 0x0E75.");
        art.Search("0x0E75");
        await Frames(8);
        await Shot(3);

        string why = assets.Import(AssetKind.Static, 0x0E75, EditorSmoke.FixtureStaticImage());
        _data.ReapplyAssets(AssetKind.Static, 0x0E75);
        art.Search("0x0E75");
        await Frames(8);
        bool replaced = _inspector.Current?.Text.Contains("replaced by the world project") == true;
        Check(why == null && replaced, "an imported PNG replaces the static and the inspector says so");
        Say("Import PNG replaced it with a PNG drawn for this tour (a ring); the inspector says 'replaced by the world project'. Size and format are checked: a wrong-sized land tile is refused.");
        MarkControl(All<Button>(_inspector).FirstOrDefault(b => b.Text == "Import PNG..."), "import / save / revert");
        await Shot(6);

        assets.Revert(AssetKind.Static, 0x0E75);
        _data.ReapplyAssets(AssetKind.Static, 0x0E75);
        art.Search("0x0E75");
        await Frames(8);
        Check(_inspector.Current?.Text.Contains("from the install") == true, "Revert brings the install's static back");
        Say("Revert, and the install's art is back.");
        _overlay.ClearMarks();
        await Shot(3);

        assets.Import(AssetKind.Static, 0x0E75, EditorSmoke.FixtureStaticImage());
        _data.ReapplyAssets(AssetKind.Static, 0x0E75);
        string hueWhy = assets.ImportHue(0x0021, EditorSmoke.FixtureHueImage(), "tour", 0, 31);
        _data.ReapplyAssets(AssetKind.Static, 0x0E75);
        var hues = _assets.Panel<HuePanel>();
        await ShowTab(hues);
        hues.Search("0x0021");
        await Frames(8);
        Check(hueWhy == null && _inspector.Current?.Text.Contains("replaced by the world project") == true, "a hue strip replaces hue 33");
        Say("Hues are replaced as a 32-pixel strip. Both replacements stay in the project for the export.");
        await Shot(5);
        Check(before == Stamps(), "the install's art, gump, hues and verdata files kept their modification times");
    }

    private async Task<(int Code, string Text)> RunTool(params string[] args)
    {
        var psi = new ProcessStartInfo(EditorData.Setting("UO_PYTHON", "python"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = EditorData.RepoRoot,
        };
        psi.ArgumentList.Add(Path.Combine(EditorData.RepoRoot, "tools", "world", "run.py"));
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process proc = Process.Start(psi);
        Task<string> o = proc.StandardOutput.ReadToEndAsync(), e = proc.StandardError.ReadToEndAsync();
        while (!proc.HasExited)
        {
            await Frames(2);
        }

        string text = await o + await e;
        return (proc.ExitCode, text);
    }

    private static string Tail(string text, int lines) =>
        string.Join("\n", text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));

    private async Task ExportSeg()
    {
        _overlay.ClearMarks();
        Say("Export writes the project to the shard's data folder (never the install): changed map and statics blocks, plus verdata.mul and hues.mul for the replaced art. tools/world does it.");
        await Shot(3);
        if (Directory.Exists(_exportDir))
        {
            Directory.Delete(_exportDir, true);
        }

        var (c1, t1) = await RunTool("export", "--project", _projectRoot, "--out", _exportDir);
        var files = Directory.Exists(_exportDir) ? Directory.GetFiles(_exportDir).Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} B)") : Enumerable.Empty<string>();
        _overlay.SetDetail(Scrub("$ tools/world export\n" + Tail(t1, 6) + "\n" + string.Join("   ", files)));
        Check(c1 == 0 && File.Exists(Path.Combine(_exportDir, "verdata.mul")), "export exited 0 and wrote verdata.mul");
        await Shot(6);

        var (c2, t2) = await RunTool("verify", "--project", _projectRoot, "--out", _exportDir);
        _overlay.SetDetail(Scrub("$ tools/world verify\n" + Tail(t2, 6)));
        Say("Verify decodes the exported files with the ported loaders and compares them with the project, block by block and pixel by pixel.");
        Check(c2 == 0, "verify passed on the export");
        await Shot(6);

        string probe = Path.Combine(_data.ClientData, "guo_export_refusal_probe");
        var (c3, t3) = await RunTool("export", "--project", _projectRoot, "--out", probe);
        bool clean = !Directory.Exists(probe);
        _overlay.SetDetail(Scrub("$ tools/world export --out UO_CLIENT_DATA/...\n" + Tail(t3, 3)));
        Say("The rule: an export into the client install is refused, and nothing is created there.");
        Check(c3 == 1 && t3.Contains("REFUSED") && clean, "an export into the install was refused and created nothing");
        await Shot(6);
        _overlay.SetDetail(null);
    }

    private async Task ShardSeg()
    {
        _shard.MakeVisible();
        await Frames(15);
        Say("The UO Shard dock is the live tier: connect to a shard's editor bridge and every edit goes to it, which applies it, pushes it to UltimaLive clients and relays it to other editors. "
            + "A command line runs GM commands as an online character.", top: true);
        MarkControl(_shard, "UO Shard dock");
        await Shot(5);

        string portText = EditorSmoke.ArgValue(LivePortFlag);
        if (!int.TryParse(portText, out int port))
        {
            Say("Live: off. No private shard was available for this run, so the dock is shown without connecting (it never targets the shared dev shard).", top: true);
            Skip("no private shard bridge was given (--guo-editor-tour-live-port); the dock was shown offline");
            await Shot(4);
            return;
        }

        CheckButton live = All<CheckButton>(_shard).First();
        var spin = All<SpinBox>(_shard).First();
        spin.Value = port;
        _shard.EditorName = TourEditorName;
        _overlay.ClearMarks();
        MarkControl(live, "Live");
        PointAt(live);
        Say($"Live on this checkout's private shard (bridge port {port}); the shared dev shard is never used.", top: true);
        await Shot(3);
        live.ButtonPressed = true;
        for (int i = 0; i < 120 && !_shard.Live; i++)
        {
            await Frames(1);
        }

        Check(_shard.Live, "the dock connected to the private shard's bridge");
        await Secs(1);
        await Shot(4);
        if (!_shard.Live || !WorldUp)
        {
            return;
        }

        EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
        _world.OpenProject(_projectRoot);
        _world.GoTo(0, EditX, EditY);
        await Frames(30);
        Say("A stamp in the World tab now goes to the shard: a tree on the wilderness block. The log shows the shard's acknowledgement and how many clients and editors it was passed to.", top: true);
        _data.CurrentArt = EditorData.LandCount + 0x0CE3;
        bool ok = _world.Editor.Stamp(0, EditX + 1, EditY - 1, _world.Host.World.Map.GetTileZ(EditX + 1, EditY - 1), 0x0CE3, 0);
        for (int i = 0; i < 300 && _shard.LastAck == null; i++)
        {
            await Frames(1);
        }

        await Frames(20);
        Check(ok && _shard.LastAck != null, _shard.LastAck is { } a
            ? $"the shard applied the block: {a.AckMs - a.SentMs} ms round trip, pushed to {a.Clients} client(s), {a.Editors} other editor(s)"
            : "the shard never acknowledged the block");
        _shard.MakeVisible();
        await Frames(5);
        await Shot(7);
        live.ButtonPressed = false;
        await Frames(5);
        Check(!_shard.Live, "disconnecting closed the link");
        await Shot(2.5);
    }

    private async Task SearchSeg()
    {
        if (Search == null)
        {
            Skip("the F3 popup was not created");
            return;
        }

        Say("F3 opens a search over everything: the editor's own menus, settings and screens, the GUO commands, and the UO data the client has open. "
            + "It matches word starts and letters in order, forgives one typo, and takes ids in hex or decimal. What you ran lately ranks higher.", top: true);
        Search.Open();
        for (int i = 0; i < 1800 && !Search.Index.Ready; i++)
        {
            await Frames(1);
        }

        Check(Search.Index.Ready, "the background index finished");
        foreach (var (query, kind, line) in new[]
        {
            ("backpack", "Static", "A name finds art, cliloc text and settings, grouped by kind."),
            ("bp", "Static", "Two letters: the start of each word."),
            ("bakcpack", "Static", "One typo is forgiven."),
            ("0x0E75", "Static", "An id in hex or decimal (3701) opens that asset."),
            ("grid", "World", "World tab toggles and tools are commands."),
            ("1434 1699", "Place", "Coordinates jump the World tab: x y [z] [mapN]."),
            ("project settings", "Menu", "Every item of the editor's own menus."),
        })
        {
            Say(line, top: true);
            Search.SetQuery(query[..Math.Max(1, query.Length / 2)]);
            await Shot(0.4, 2);
            var groups = Search.SetQuery(query);
            await Frames(3);
            SearchEntry top = SearchIndex.Top(groups);
            bool ok = query == "bp" || query == "bakcpack"
                ? groups.Any(g => g.Items.Any(i => i.Entry.Title == "backpack"))
                : top?.Kind == kind;
            Check(ok, $"'{query}' -> {top?.Kind} '{top?.Title}'");
            await Shot(3);
        }

        Search.Close();
        await Frames(3);
    }

    private async Task Outro()
    {
        (GetParent() as EditorPlugin)?.HideBottomPanel();
        _assets.MakeVisible();
        await Frames(5);
        int ok = _segments.Count(s => s.Skipped == null && s.Failures.Count == 0);
        Say($"{ok} of {_segTotal - 1} segments checked out on this run; the rest are listed, with why, in summary.md. "
            + "Everything edited lived in a world project and was exported to a scratch folder: the client install was not written.",
            title: "That is the editor");
        await Shot(6);
    }

    // --- output -------------------------------------------------------------

    private void Finish(string fatal)
    {
        try
        {
            var report = new Dictionary<string, object>
            {
                ["ok"] = fatal == null && _segments.All(s => s.Failures.Count == 0),
                ["fatal"] = fatal,
                ["elapsed_s"] = Math.Round(_clock.Elapsed.TotalSeconds, 1),
                ["window"] = new[] { DisplayServer.WindowGetSize().X, DisplayServer.WindowGetSize().Y },
                ["live_port"] = EditorSmoke.ArgValue(LivePortFlag),
                ["segments"] = _segments.Select(s => new Dictionary<string, object>
                {
                    ["id"] = s.Id, ["title"] = s.Title, ["skipped"] = s.Skipped, ["passed"] = s.Passed,
                    ["failures"] = s.Failures, ["frames"] = s.FrameCount,
                }).ToList(),
                ["frames"] = _frames.Select(f => new Dictionary<string, object> { ["file"] = f.File, ["seconds"] = f.Seconds, ["segment"] = f.Segment }).ToList(),
            };
            Directory.CreateDirectory(_out);
            File.WriteAllText(Path.Combine(_out, "tour.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO tour] could not write tour.json: {ex.Message}");
        }

        GD.Print($"[GUO tour] done: {_segments.Count} segment(s), {_frames.Count} frame(s){(fatal != null ? ", FATAL " + fatal : "")}");
        GetTree().Quit(fatal == null ? 0 : 1);
    }
}
#endif
