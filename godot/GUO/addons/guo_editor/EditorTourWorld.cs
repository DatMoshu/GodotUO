#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;
using GUO.Game.GameObjects;

/// <summary>
/// ED3: every World-tab map-editing feature, on camera. These segments drive the real controls: the rail buttons,
/// the brush spin boxes and check boxes, the preset buttons, and mouse and key events fed to the map viewport's own
/// input handler (<see cref="WorldView.TourInput"/>), so a click is resolved by the game's picking exactly as a
/// person's click is. They edit a scratch world project under the tour's output folder, never the default one.
/// </summary>
public partial class EditorTour
{
    private const ushort Tree = 0x0CCA, Anvil = 0x0FAF, Grass = 0x0003, Water = 0x00A8;
    private string _weRoot;

    // --- plumbing -------------------------------------------------------------

    private Vector2I? CellPx(int x, int y)
    {
        var chunk = _world.Host.World.Map.GetChunk2(x >> 3, y >> 3, true);
        for (GameObject o = chunk?.GetHeadObject(x & 7, y & 7); o != null; o = o.TNext)
        {
            if (o is Land)
            {
                var s = _world.Host.Scene.Camera.WorldToScreen(new GUO.Compat.Point(o.RealScreenPosition.X + 22, o.RealScreenPosition.Y + 22));
                return new Vector2I(s.X, s.Y);
            }
        }

        return null;
    }

    private async Task AimCell(int x, int y)
    {
        if (CellPx(x, y) is { } p)
        {
            Aim(p);
        }
        else
        {
            Check(false, $"cell {x},{y} is not on screen");
        }

        await Frames(4);
    }

    private static readonly (int, int)[] AimOffsets =
        new[] { 0, -12, -24, -36, 12, 24, 36 }.SelectMany(x => new[] { 0, -12, -24, -36, 12, 24 }.Select(y => (x, y))).OrderBy(t => t.x * t.x + t.y * t.y).ToArray();

    /// <summary>Aims near a sprite until the game's picking returns what a person would be pointing at (art is taller than its cell).</summary>
    private async Task<bool> AimAt(Vector2I basePx, Func<GameObject, bool> want)
    {
        foreach (var (dx, dy) in AimOffsets)
        {
            Aim(basePx + new Vector2I(dx, dy));
            await Frames(4);
            if (_world.Host.Picked is GameObject o && want(o))
            {
                return true;
            }
        }

        Check(false, "could not point at the sprite (the picking never returned it)");
        return false;
    }

    /// <summary>The drawn static with this graphic on this cell, or null.</summary>
    private Static StaticAt(int x, int y, ushort id)
    {
        var chunk = _world.Host.World.Map.GetChunk2(x >> 3, y >> 3, true);
        for (GameObject o = chunk?.GetHeadObject(x & 7, y & 7); o != null; o = o.TNext)
        {
            if (o is Static st && st.Graphic == id)
            {
                return st;
            }
        }

        return null;
    }

    private bool PickedIs(int x, int y) => _world.Host.Picked is GameObject o && o.X == x && o.Y == y;

    private InputEventMouseButton MouseEv(MouseButton b, bool down, bool shift = false, bool alt = false)
    {
        Vector2 p = _world.ForcedMouse is { } f ? new Vector2(f.X, f.Y) : Vector2.Zero;
        return new InputEventMouseButton { ButtonIndex = b, Pressed = down, ShiftPressed = shift, AltPressed = alt, Position = p, GlobalPosition = p };
    }

    private async Task Click(MouseButton b = MouseButton.Left, bool shift = false, bool alt = false)
    {
        _world.TourInput(MouseEv(b, true, shift, alt));
        await Frames(2);
        _world.TourInput(MouseEv(b, false, shift, alt));
        await Frames(3);
    }

    private async Task ClickCell(int x, int y, bool shift = false, bool alt = false)
    {
        await AimCell(x, y);
        await Click(MouseButton.Left, shift, alt);
    }

    private async Task Wheel(bool up, bool alt = false, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            _world.TourInput(MouseEv(up ? MouseButton.WheelUp : MouseButton.WheelDown, true, false, alt));
            await Frames(3);
        }
    }

    private async Task KeyPress(Key key, bool shift = false)
    {
        _world.TourInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true, ShiftPressed = shift });
        await Frames(3);
    }

    /// <summary>A brush stroke with the real input path: press on the first cell, drag over the rest, optional shot before release, release.</summary>
    private async Task Stroke(IReadOnlyList<(int X, int Y)> cells, Func<Task> beforeRelease = null)
    {
        await AimCell(cells[0].X, cells[0].Y);
        _world.TourInput(MouseEv(MouseButton.Left, true));
        await Frames(2);
        for (int i = 1; i < cells.Count; i++)
        {
            await AimCell(cells[i].X, cells[i].Y);
            _world.TourInput(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left, Relative = new Vector2(8, 0) });
            await Frames(2);
        }

        if (beforeRelease != null)
        {
            await beforeRelease();
        }

        _world.TourInput(MouseEv(MouseButton.Left, false));
        await Frames(25);
    }

    private static List<(int X, int Y)> Line(int x0, int y0, int x1, int y1)
    {
        int n = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        return Enumerable.Range(0, n + 1).Select(i => (x0 + (x1 - x0) * i / Math.Max(1, n), y0 + (y1 - y0) * i / Math.Max(1, n))).ToList();
    }

    private async Task Rail(WorldTool tool)
    {
        Button b = _world.ToolButton(tool);
        Check(b != null, $"the {tool} button is on the tool rail");
        if (b == null)
        {
            _world.Tool = tool;
            return;
        }

        _overlay.ClearMarks();
        // The mark reads as words: PlaceItem is "Place item".
        string name = System.Text.RegularExpressions.Regex.Replace(tool.ToString(), "(?<=[a-z])([A-Z])", m => " " + m.Value.ToLowerInvariant());
        MarkControl(b, name);
        PointAt(b);
        b.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        Check(_world.Tool == tool, $"the rail button selects {tool}");
    }

    private async Task Press(string text, string what = null)
    {
        // A button on a closed tab or fold is opened to first, on camera.
        Button b = _world.TourButton(text) ?? All<Button>(_world).FirstOrDefault(x => x is not OptionButton and not MenuButton && x.Text == text);
        Check(b != null, $"the '{text}' button exists" + (b == null ? $" (buttons seen: {string.Join(", ", _world.TourButtonNames().Take(40))})" : ""));
        if (b != null)
        {
            await ShowControl(b);
            MarkControl(b, what ?? text);
            PointAt(b);
            b.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(4);
        }
    }

    /// <summary>The pointer on one tab of a tab strip, where a person clicks to open it.</summary>
    private void PointAtTab(TabContainer tabs, int index)
    {
        TabBar bar = tabs.GetTabBar();
        _overlay.Pointer(bar.GetGlobalTransform() * bar.GetTabRect(index).GetCenter());
        _ringLands = () => IsInstanceValid(bar) && bar.IsVisibleInTree() ? TourOverlay.FramePx(bar, bar.GetTabRect(index).GetCenter()) : null;
    }

    /// <summary>
    /// Brings a control on screen the way a person would, on camera: a frame with the pointer on the tab it is on, then
    /// on the fold it is under, then the tab or fold opens and its panel scrolls to it. Nothing is filmed when it is
    /// already in view. Marks made before are cleared when anything has to open (they may be on the tab that closes).
    /// </summary>
    private async Task ShowControl(Control c)
    {
        if (c == null || Hidden(c, c.GetGlobalRect()) == null)
        {
            return;
        }

        var chain = new List<Control>();
        for (Node n = c; n is Control a; n = a.GetParent())
        {
            chain.Add(a);
        }

        chain.Reverse();
        for (int i = 0; i < chain.Count - 1; i++)
        {
            Control parent = chain[i], child = chain[i + 1];
            if (parent is TabContainer tabs && tabs.GetTabIdxFromControl(child) is int idx and >= 0 && tabs.CurrentTab != idx)
            {
                _overlay.ClearMarks();
                MarkTab(tabs, idx, tabs.GetTabTitle(idx));
                PointAtTab(tabs, idx);
                await Shot(2.5);
                tabs.CurrentTab = idx;
                await Frames(4);
            }

            if (!child.Visible && child.GetIndex() > 0 && parent.GetChild(child.GetIndex() - 1) is Button { ToggleMode: true } fold)
            {
                await Reveal(fold);
                _overlay.ClearMarks();
                MarkControl(fold, fold.Text);
                PointAt(fold);
                await Shot(2.5);
                fold.ButtonPressed = true;
                await Frames(4);
            }
        }

        _overlay.ClearMarks();
        await Reveal(c);
    }

    private async Task SetNumber(string label, double value)
    {
        SpinBox s = _world.TourNumber(label);
        Check(s != null, $"the {label} box exists");
        if (s != null)
        {
            await ShowControl(s);
            MarkControl(s, label);
            PointAt(s);
            s.Value = value;
            await Frames(3);
        }
    }

    private async Task SetCheck(string label, bool on)
    {
        CheckBox c = _world.TourCheck(label);
        Check(c != null, $"the {label} check box exists");
        if (c != null)
        {
            await ShowControl(c);
            MarkControl(c, c.Text);
            PointAt(c);
            c.ButtonPressed = on;
            await Frames(3);
        }
    }

    /// <summary>Types into a text box (as Enter would leave it).</summary>
    private async Task SetText(LineEdit e, string label, string text)
    {
        Check(e != null, $"the {label} box exists");
        if (e != null)
        {
            await ShowControl(e);
            MarkControl(e, label);
            PointAt(e);
            e.Text = text;
            e.EmitSignal(LineEdit.SignalName.TextChanged, text);
            await Frames(3);
        }
    }

    /// <summary>
    /// Chooses what to place the way a person does: types into the item library's search box (the first match is
    /// chosen) and shows the chosen item in the box at the top of Tools. Returns the item's number.
    /// </summary>
    private async Task<ushort?> ChooseArt(string typed)
    {
        LineEdit search = _world.TourLibrarySearch;
        Check(search != null && _world.TourLibrary.IsVisibleInTree(), "the item library's search box is on screen");
        if (search == null)
        {
            return null;
        }

        await ShowControl(_world.TourArtBox);
        await Reveal(search);
        int? id = _world.BrushLibrary.Search(typed);
        await Frames(10);
        bool land = _data.CurrentArt < EditorData.LandCount;
        Check(id is int f && _data.CurrentArt == (land ? (uint)f : EditorData.LandCount + (uint)f), $"searching '{typed}' chose an item ({id}, current art {_data.CurrentArt})");
        MarkControl(search, "search");
        // The item box keeps naming the last item when a ground tile is chosen (a finding, not fixed here): marking it
        // "chosen" for ground would point at the wrong name.
        if (!land)
        {
            MarkControl(_world.TourArtBox, "chosen");
        }
        PointAt(search);
        await Shot();
        return id is int g ? (ushort)g : null;
    }

    private WorldBlock WeBlock(int x, int y) =>
        _world.Host.Project?.BlockText(0, x >> 3, y >> 3) is null ? null : WorldProject.ReadBlock(_world.Host.Project.BlockPath(0, x >> 3, y >> 3));

    private int StaticCount(int x0, int y0, int x1, int y1, ushort id) =>
        _world.Editor.StaticsInRect(0, x0, y0, x1, y1).Count(s => s.S.Id == id);

    private HashSet<(int, int)> StaticCells(int x0, int y0, int x1, int y1, ushort id) =>
        _world.Editor.StaticsInRect(0, x0, y0, x1, y1).Where(s => s.S.Id == id).Select(s => (s.X, s.Y)).ToHashSet();

    private int LandZ(int x, int y)
    {
        WorldBlock b = WeBlock(x, y);
        return b != null ? b.LandZ[(y & 7) * 8 + (x & 7)] : _world.Host.World.Map.GetTileZ(x, y);
    }

    private void UseArt(ushort staticId, bool land = false)
    {
        _data.CurrentArt = land ? staticId : EditorData.LandCount + staticId;
    }

    private void ToolNote(string text) => _overlay.SetDetail($"Tool: {_world.ToolLabel}   {text}");

    private async Task OpenWe(int x, int y)
    {
        _world.ForcedMouse = null;
        _world.GoTo(0, x, y);
        await Frames(35);
    }

    /// <summary>Boots the World tab if needed and opens the scratch edit project, so any one segment can run alone.</summary>
    private async Task<bool> NeedWe()
    {
        if (!WorldUp)
        {
            (GetParent() as GuoEditorPlugin)?.ShowInWorld(0, EditX, EditY);
            for (int i = 0; i < 900 && !WorldUp && _world.Error == null; i++)
            {
                await Frames(1);
            }

            await Frames(40);
        }

        if (!NeedWorld())
        {
            return false;
        }

        // The World view may have booted earlier in the editor's life while another main screen is open now.
        if (!_world.IsVisibleInTree())
        {
            EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
            await Frames(20);
        }

        Check(_world.IsVisibleInTree(), "the World tab is the open main screen");

        _weRoot ??= Path.Combine(_out, "edit_project");
        if (_world.Host.Project == null || !Slash(_world.Host.Project.Root).EndsWith("edit_project"))
        {
            if (Directory.Exists(_weRoot))
            {
                Directory.Delete(_weRoot, true);
            }

            WorldProject.OpenOrCreate(_weRoot, _data.ClientData, _data.ClientVersion).Dispose();
            _data.OpenAssets(_weRoot);
            _world.OpenProject(_weRoot);
            _world.Season = GUO.Game.Managers.Season.Summer;
            await Frames(10);
            Aim(Centre);
            await Wheel(true, times: 5);
            await Frames(10);
            _world.ForcedMouse = null;
        }

        return true;
    }

    // --- Stamp, then Select (ED3b: rebuilt on the fixed layout, in plain words) ---------------------

    /// <summary>The art the tour placed with Stamp, for Select to inspect.</summary>
    private ushort _stampArt = Crate;
    private (int X, int Y) _stampCell = (EditX + 6, EditY + 6);

    private async Task WeStamp()
    {
        if (!await NeedWe())
        {
            return;
        }

        var (cx, cy) = _stampCell;
        await OpenWe(cx, cy);
        await Rail(WorldTool.Select);
        _overlay.ClearMarks();

        Say("This is the World tab: the real map, ready to edit. Your edits go into a project folder of their own; "
            + "the game's files are never changed. The tools are the narrow bar on the left.");
        MarkControl(_world.TourRail, "tools");
        MarkControl(_world.TourSettings, "settings");
        MarkControl(_world.TourLibrary, "items to place");
        await Shot();
        _overlay.ClearMarks(keepPointer: true);

        // Choose what to place: typed into the item library's search, as a person would.
        LineEdit search = _world.TourLibrarySearch;
        Check(search != null, "the item library has a search box");
        Say("First choose what to place. Type its name in the search box above the item library: \"crate\". "
            + "The first match is chosen, and the box at the top of Tools shows it.");
        int? found = null;
        if (search != null)
        {
            await Reveal(search);
            search.Text = "cra";
            MarkControl(search, "search");
            PointAt(search);
            await Shot();
            found = _world.BrushLibrary.Search("crate");
            await Frames(10);
        }

        Check(found is int f && _data.CurrentArt == EditorData.LandCount + (uint)f, $"searching 'crate' chose an item ({found}, current art {_data.CurrentArt})");
        if (found is int id)
        {
            _stampArt = (ushort)id;
            UseArt(_stampArt);
        }
        else
        {
            UseArt(Crate);
        }

        await Frames(4);
        MarkControl(_world.TourArtBox, "chosen item");
        await Shot();
        _overlay.ClearMarks(keepPointer: true);

        Say("Now pick the Stamp tool on the bar (keyboard: S). Stamp places one of the chosen item wherever you click.");
        await Rail(WorldTool.Stamp);
        await Shot();

        Say("Point at an open patch of grass and click once.");
        await AimCell(cx, cy);
        await Shot();
        int undo = _world.Editor.UndoCount;
        await Click();
        await Frames(20);
        Check(StaticCount(cx, cy, cx, cy, _stampArt) == 1 && _world.Editor.UndoCount == undo + 1, "one click placed exactly one crate, one undo step");
        // The ring stays on the click: it must be on the cell the crate went to, as drawn now.
        CheckRing(CellPx(cx, cy) is { } cell ? MapFramePx(cell) : null, "Stamp");

        _overlay.ClearMarks(keepPointer: true);
        Say("One click, one crate. The line under the map confirms what was placed, where, and at what height.");
        MarkControl(_world.TourStatus, "what happened");
        await Shot();
        _overlay.ClearMarks(keepPointer: true);

        Say("Changed your mind? Undo (or Ctrl+Z) takes the crate away again.");
        // The hand has left the map for the Undo button: no hover left on the map for a pointer that is not there.
        _world.ForcedMouse = null;
        await Press("Undo");
        await Frames(15);
        Check(StaticCount(cx, cy, cx, cy, _stampArt) == 0, "Undo removed the crate");
        await Shot();
        _overlay.ClearMarks(keepPointer: true);

        Say("Redo (or Ctrl+Y) puts it back.");
        await Press("Redo");
        await Frames(15);
        Check(StaticCount(cx, cy, cx, cy, _stampArt) == 1, "Redo put the crate back");
        await Shot();
        _overlay.ClearMarks(keepPointer: true);
        _world.ForcedMouse = null;
    }

    private async Task MapGenResultSeg()
    {
        var view = GuoEditorPlugin.MapGenMain;
        Check(view != null, "MapGen view exists");
        if (view == null) return;
        EditorInterface.Singleton.SetMainScreenEditor(MapGenView.TabName);
        bool schemaAlreadyLoaded = view.PassGroupCount > 0;
        var schemaClock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 900 && view.PassGroupCount == 0; i++) await Frames(1);
        Check(view.PassGroupCount > 0, schemaAlreadyLoaded ? "the generator schema was already loaded"
            : $"the generator schema loaded ({schemaClock.ElapsedMilliseconds} ms from tab selection)");
        if (view.PassGroupCount == 0) return;

        Button button(string label) => All<Button>(view).First(b => b.Text == label);
        void press(Button b) => b.EmitSignal(BaseButton.SignalName.Pressed);
        var size = All<OptionButton>(view).First(b => Enumerable.Range(0, b.ItemCount)
            .Any(i => b.GetItemText(i) == "256 x 256"));
        void selectSize(string text) => size.Select(Enumerable.Range(0, size.ItemCount).First(i => size.GetItemText(i) == text));
        selectSize("256 x 256");
        var generate = button("Generate");
        var export = button("Export map files");
        var cancel = button("Cancel");
        var restore = button("Show previous completed run");
        var setup = All<TabContainer>(view).FirstOrDefault(t => t.GetTabTitle(0) == "Setup");
        async Task capture(string caption)
        {
            Say(caption);
            _overlay.SetCaption("", "", "");
            await Shot(settle: 1);
        }
        await Frames(15); // the native editor rearranges its chrome after a compact resize
        await capture("Setup with the default preset and a 256 x 256 trial map");
        if (setup != null)
        {
            Rect2 window = EditorInterface.Singleton.GetBaseControl().GetGlobalRect();
            Check(window.Encloses(generate.GetGlobalRect()) && window.Encloses(export.GetGlobalRect())
                && window.Encloses(button("Final map").GetGlobalRect()),
                "Setup actions and preview controls are inside the editor window");
        }
        var generateClock = System.Diagnostics.Stopwatch.StartNew();
        press(generate);
        for (int i = 0; i < 3600 && generate.Disabled; i++) await Frames(1);
        string completed = view.LastRun;
        Check(completed != null && !export.Disabled, $"completed generation is available for export ({generateClock.ElapsedMilliseconds} ms)");
        if (completed == null || generate.Disabled) return;
        await capture("Completed result A and its hash");

        if (setup != null) setup.CurrentTab = 1;
        var filter = All<LineEdit>(view).First(l => l.PlaceholderText == "Filter passes and settings");
        filter.Text = "River Carve";
        filter.EmitSignal(LineEdit.SignalName.TextChanged, filter.Text);
        var fold = All<Button>(view).FirstOrDefault(b => b.Text.EndsWith("  River Carve", StringComparison.Ordinal));
        if (fold != null) fold.ButtonPressed = true;
        var slider = All<HSlider>(view).Single(s => s.Editable && s.MaxValue > 1);
        var previewClock = System.Diagnostics.Stopwatch.StartNew();
        slider.Value = Math.Min(7, slider.MaxValue - 1);
        double previewMs = previewClock.Elapsed.TotalMilliseconds;
        await capture("Advanced pass search and a saved intermediate image");
        var preview = All<TextureRect>(view).First(t => t.Texture != null && t.MouseFilter == Control.MouseFilterEnum.Stop);
        Check(preview.Texture != null, $"selecting a saved pass updated the preview ({previewMs:0.0} ms synchronous)");
        if (setup != null)
        {
            Rect2 window = EditorInterface.Singleton.GetBaseControl().GetGlobalRect();
            Check(window.Encloses(preview.GetGlobalRect()) && preview.Size.Y >= 120,
                $"advanced preview fits the window ({preview.Size.X:0} x {preview.Size.Y:0})");
        }
        var sourceCount = All<SpinBox>(view).First(s => s.TooltipText.Contains("River Carve.SourceCount", StringComparison.Ordinal));
        sourceCount.Value = 28;
        var river = All<CheckBox>(view).First(b => b.Text == "River Carve" || b.TooltipText.StartsWith("Enable River Carve.", StringComparison.Ordinal));
        river.ButtonPressed = false;
        if (setup != null) setup.CurrentTab = 0;
        selectSize("2048 x 2048");
        var seedText = All<LineEdit>(view).FirstOrDefault(l => l.TooltipText.StartsWith("Signed 64-bit", StringComparison.Ordinal));
        if (seedText != null) seedText.Text = "7654321";
        else All<SpinBox>(view).First(s => s.MaxValue == int.MaxValue).Value = 7654321;
        press(generate);
        for (int i = 0; i < 240 && generate.Disabled
            && !All<Label>(view).Any(l => l.Text.StartsWith("Pass 1/", StringComparison.Ordinal)); i++)
            await Frames(1);
        Check(generate.Disabled, "the larger second attempt is in progress before cancellation");
        await capture("Generating a different seed and size with River Carve disabled");
        press(cancel);
        for (int i = 0; i < 900 && generate.Disabled; i++) await Frames(1);
        Check(view.LastRun == null && export.Disabled && !restore.Disabled,
            "cancelled generation has no export and offers the completed run explicitly");
        if (restore.Disabled) return;
        await capture("Cancelled B offers explicit restoration of A");
        press(restore);
        Check(view.LastRun == completed && !export.Disabled, "restore selects the original result and export");
        await capture("Restored A with B's settings still in Setup");

        var open = button("Open in UO World");
        press(export);
        for (int i = 0; i < 5400 && generate.Disabled; i++) await Frames(1);
        Check(!open.Disabled, "the restored result exported and verified");
        if (open.Disabled) return;
        await capture("Verified export of A before opening World");
        const int x = 37, y = 59;
        Vector2 tex = preview.Texture.GetSize();
        float scale = Math.Min(preview.Size.X / tex.X, preview.Size.Y / tex.Y);
        Vector2 point = (preview.Size - tex * scale) / 2 + new Vector2(x + 0.5f, y + 0.5f) * tex * (scale / 256f);
        var projectRoot = Path.Combine(completed, "export");
        preview.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
            { ButtonIndex = MouseButton.Left, Pressed = true, Position = point });
        await Frames(30);
        string marker = _world.Host.Project?.Root is { } root ? Path.Combine(root, "generated.json") : "";
        bool opened = WorldUp && _world.IsVisibleInTree() && File.Exists(marker);
        Check(opened, "a click on the verified preview opened the generated World project");
        string verifyPath = Path.Combine(projectRoot, "export-verify.json");
        if (!opened || !File.Exists(verifyPath)) return;
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker));
        using var verify = System.Text.Json.JsonDocument.Parse(File.ReadAllText(verifyPath));
        using var run = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(completed, "run.json")));
        var generated = manifest.RootElement;
        var origin = generated.GetProperty("origin");
        string hash = run.RootElement.GetProperty("hash").GetString();
        Check(generated.GetProperty("run").GetString() == Path.GetFullPath(completed)
            && generated.GetProperty("hash").GetString() == hash && verify.RootElement.GetProperty("hash").GetString() == hash,
            "the completed run, exported files, and World project share the same hash");
        if (setup != null)
        {
            Check(verify.RootElement.TryGetProperty("sha256", out var digests) && digests.GetRawText().Length > 2
                && digests.EnumerateObject().Count() == 3 && digests.EnumerateObject().All(entry =>
                {
                    using var file = File.OpenRead(Path.Combine(projectRoot, entry.Name));
                    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file))
                        .Equals(entry.Value.GetString(), StringComparison.OrdinalIgnoreCase);
                }), "the actual MUL triad matches the verification manifest's byte hashes");
        }
        Check(_world.Host.Facet == generated.GetProperty("facet").GetInt32()
            && _world.Host.X == origin[0].GetInt32() + x && _world.Host.Y == origin[1].GetInt32() + y,
            "the preview jumped to the exact generated cell on its facet");
        await capture($"World at facet {_world.Host.Facet}, cell {_world.Host.X}, {_world.Host.Y}");

        EditorInterface.Singleton.SetMainScreenEditor(MapGenView.TabName);
        selectSize("256 x 256");
        var height = All<CheckBox>(view).First(b => b.Text == "Noise Height" || b.TooltipText.StartsWith("Enable Noise Height.", StringComparison.Ordinal));
        height.ButtonPressed = false;
        press(generate);
        for (int i = 0; i < 900 && generate.Disabled; i++) await Frames(1);
        Check(view.LastRun == null && export.Disabled && !restore.Disabled,
            "missing height dependency fails and leaves the completed run restorable");
        await capture("Invalid dependency is reported with the export action disabled");
        height.ButtonPressed = true;
        river.ButtonPressed = true;
        if (setup != null)
        {
            string python = System.Environment.GetEnvironmentVariable("UO_PYTHON");
            try
            {
                System.Environment.SetEnvironmentVariable("UO_PYTHON", Path.Combine(completed, "missing-python.exe"));
                press(generate);
                for (int i = 0; i < 900 && generate.Disabled; i++) await Frames(1);
                Check(!generate.Disabled && view.LastRun == null && export.Disabled && !restore.Disabled
                    && All<Label>(view).Any(l => l.Text.Contains("Could not start Python", StringComparison.Ordinal)),
                    "missing Python explains the dependency and leaves Generate available for retry");
                await capture("Missing Python gives a recovery message");
            }
            finally { System.Environment.SetEnvironmentVariable("UO_PYTHON", python); }
        }
        press(generate);
        for (int i = 0; i < 3600 && generate.Disabled; i++) await Frames(1);
        Check(view.LastRun != null && view.LastRun != completed && !export.Disabled, "correcting the dependency lets the advanced setup retry");
        if (view.LastRun != null)
        {
            using var retry = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(view.LastRun, "run.json")));
            Check(retry.RootElement.GetProperty("seed").GetInt64() == 7654321
                && retry.RootElement.GetProperty("sets").EnumerateArray().Any(s => s.GetString() == "River Carve.SourceCount=28"),
                "the retry used the advanced seed and edited river source count");
        }
        await capture("Retry completes the advanced setup with the new seed");
    }

    private async Task WeSafety()
    {
        if (!await NeedWe()) return;
        int x = EditX, y = EditY;
        await OpenWe(x, y);
        await Rail(WorldTool.Select);
        var visible = EditorInterface.Singleton.GetBaseControl().GetGlobalRect();
        var outside = _world.CommandRowOutside(visible);
        Check(outside.Count == 0, outside.Count == 0 ? "World command bar and minimap fit"
            : $"clipped World controls: {string.Join(", ", outside)}");
        if (visible.Size.X < 1150)
        {
            await Frames(5);
            float canvas = _world.TourStage.Size.X;
            bool library = _world.TourLibrary.IsVisibleInTree();
            Check(!library && canvas >= visible.Size.X * 0.5f,
                $"compact layout gives the map {canvas:0} of {visible.Size.X:0} pixels; library visible: {library}");
        }
        await KeyPress(Key.Escape);
        int depth = _world.Editor.UndoCount;
        Say("Drag from one tile to another in Select. World outlines the rectangle and shows its size before you release.");
        await AimCell(x, y);
        var start = _world.Host.Picked as GameObject;
        (int X, int Y)? anchor = start == null ? null : (start.X, start.Y);
        _world.TourInput(MouseEv(MouseButton.Left, true));
        await AimCell(x + 2, y + 1);
        _world.TourInput(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left, Relative = new Vector2(16, 0) });
        await Frames(3);
        Check(_world.Area == null, "the draft is not selected until release");
        _overlay.ClearMarks();
        await Shot();
        var end = _world.Host.Picked as GameObject;
        (int X, int Y)? last = end == null ? null : (end.X, end.Y);
        _world.TourInput(MouseEv(MouseButton.Left, false));
        await Frames(4);
        Check(anchor is { } a && last is { } corner && a != corner && _world.Area ==
            (Math.Min(a.X, corner.X), Math.Min(a.Y, corner.Y), Math.Max(a.X, corner.X), Math.Max(a.Y, corner.Y)),
            "drag commits the exact inclusive rectangle between the picked cells");
        Check(_world.Editor.UndoCount == depth, "selecting does not write any blocks");
        Say("Release to keep the selection. Right-click for Select actions, or press Escape to clear it.");
        _overlay.ClearMarks(keepPointer: true);
        await Shot();
        Control root = EditorInterface.Singleton.GetBaseControl();
        await Click(MouseButton.Right);
        var menu = root.GetNodeOrNull<PopupMenu>("WorldMapActions");
        var labels = menu == null ? Array.Empty<string>() : Enumerable.Range(0, menu.ItemCount).Select(menu.GetItemText).ToArray();
        Check(menu is { Visible: true } && labels.Contains("Inspect tile stack") && labels.Contains("Clear selection")
            && !labels.Any(label => label.Contains("zone", StringComparison.OrdinalIgnoreCase) || label.Contains("region", StringComparison.OrdinalIgnoreCase)),
            "Select context menu shows tile and selection actions without boundary creation");
        menu?.Hide();
        await Frames(3);

        _world.TourInput(MouseEv(MouseButton.Right, true));
        _world.TourInput(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Right, Relative = new Vector2(55, 0) });
        _world.TourInput(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Right, Relative = new Vector2(-55, 0) });
        _world.TourInput(MouseEv(MouseButton.Right, false));
        await Frames(3);
        Check(root.GetNodeOrNull<PopupMenu>("WorldMapActions") is not { Visible: true },
            "an out-and-back right drag stays a pan");
        await KeyPress(Key.Escape);
        Check(_world.Area == null, "Escape clears the selected rectangle");

        await OpenWe(x, y);
        await Rail(WorldTool.Brush);
        await AimCell(x, y);
        _world.TourInput(MouseEv(MouseButton.Left, true));
        await KeyPress(Key.Escape);
        _world.TourInput(MouseEv(MouseButton.Left, false));
        Check(_world.Editor.UndoCount == depth, "Escape cancels a brush before release");
        await Rail(WorldTool.Select);

        var project = _world.Host.Project;
        var editor = _world.Editor;
        int bx = x >> 3, by = y >> 3;
        var before = new WorldProject.BlockSnapshot(0, bx, by, project.BlockText(0, bx, by));
        Check(editor.Stamp(0, x, y, 110, Crate, 0), "first edit writes a block");
        string first = project.BlockText(0, bx, by);
        Check(editor.Stamp(0, x, y, 110, Crate, 4), "the same art and Z can have a different hue");
        string second = project.BlockText(0, bx, by);
        Check(editor.EditStatic(0, x, y, 110, Crate, 4, 0, second, "exact erase", _ => null)
            && project.BlockText(0, bx, by) == first, "exact erase leaves the same art and Z with a different hue");
        project.SetBlockText(0, bx, by, second);
        int withChanges = editor.UndoCount;
        Check(!editor.Undo() && editor.UndoCount == withChanges && project.BlockText(0, bx, by) == second,
            "stale history leaves the modified file and history entry intact");

        // Model an interruption after a block was written and before the journal was removed.
        string journal = Path.Combine(project.Root, ".block-transaction.json");
        File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new[] { before }));
        using (var reopened = WorldProject.OpenOrCreate(project.Root, _data.ClientData, _data.ClientVersion))
        {
            Check(reopened.BlockText(0, bx, by) == before.Text && !File.Exists(journal),
                "reopen rolls back an interrupted write and removes the journal");
        }
        project.SetBlockText(0, bx, by, second);
        File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new[] { before }));
        _world.ReloadOverlay();
        Check(project.BlockText(0, bx, by) == before.Text && !File.Exists(journal),
            "Reload project also recovers the interrupted edit");
        editor.Clear();
        _world.ForcedMouse = null;
    }

    private async Task WeSelect()
    {
        if (!await NeedWe())
        {
            return;
        }

        var (cx, cy) = _stampCell;
        if (StaticAt(cx, cy, _stampArt) == null && StaticCount(cx, cy, cx, cy, _stampArt) == 0)
        {
            // Run alone: put the crate there without showing it (the Stamp segment shows how).
            _world.Editor.Stamp(0, cx, cy, (sbyte)LandZ(cx, cy), _stampArt, 0);
        }

        await OpenWe(cx, cy);
        _world.ShowInspectorTab(true);
        Say("The Select tool (keyboard: V) never changes the map. It only tells you what is there.");
        await Rail(WorldTool.Select);
        await Shot();

        Say("Click the crate. The UO Inspector, at the bottom left, names it (small crate) and gives its item number, "
            + "its place on the map and its height (z).");
        if (StaticAt(cx, cy, _stampArt) is { } crate)
        {
            await AimAt(WorldAim.ScreenOfObject(_world, _data, crate), o => o is Static st && st.Graphic == _stampArt && st.X == cx && st.Y == cy);
        }

        await Click();
        await Frames(8);
        // Where the game's own picking looked (its mouse), against the ring; the Inspector check below says it found the crate.
        CheckRing(MapFramePx(new Vector2I(GUO.Input.Mouse.Position.X, GUO.Input.Mouse.Position.Y)), "Select");
        Check(_world.Host.Picked is Static picked && picked.Graphic == _stampArt && picked.X == cx && picked.Y == cy,
            "the picking found the crate under the ring");
        Check(_inspector.Current != null && _inspector.Current.Text.Contains($"{cx},{cy}"), $"the click reached the UO Inspector ({_inspector.Current?.Text?.Split('\n')[0]})");
        _overlay.ClearMarks(keepPointer: true);
        await Reveal(_inspector.TourFields);
        MarkControl(_inspector.TourFields, "UO Inspector");
        await Shot();
        _overlay.ClearMarks(keepPointer: true);

        Say("Beside it, the Nearby tiles tab lists everything stacked on the clicked spot, the top item first, "
            + "and the ground around it.");
        MarkTab(_world.TourDetailTabs, 1, "Nearby tiles");
        PointAtTab(_world.TourDetailTabs, 1);
        await Shot();
        _world.ShowInspectorTab(false);
        await Frames(10);
        _overlay.ClearMarks(keepPointer: true);
        MarkControl(_world.TourStack, "on this spot");
        Check(_world.TourStack.ItemCount >= 2, $"the stack lists the crate and the ground ({_world.TourStack.ItemCount} rows)");
        await Shot();
        _overlay.ClearMarks(keepPointer: true);
        _world.ShowInspectorTab(true);
        _world.ForcedMouse = null;
    }


    // --- One spot, several things: Alt + wheel, Alt + click, Shift + click -----------------------------

    private async Task WeStack()
    {
        if (!await NeedWe())
        {
            return;
        }

        var (cx, cy) = _stampCell;
        if (StaticAt(cx, cy, _stampArt) == null && StaticCount(cx, cy, cx, cy, _stampArt) == 0)
        {
            _world.Editor.Stamp(0, cx, cy, (sbyte)LandZ(cx, cy), _stampArt, 0);
        }

        await OpenWe(cx, cy);
        _world.ShowInspectorTab(true);
        Say("Things can stand on top of each other. Choose a second item the same way: type \"barrel\" in the search box.");
        ushort top = await ChooseArt("barrel") ?? Anvil;
        _overlay.ClearMarks();

        Say("With Stamp, a click on the crate puts the barrel on top of the crate, not beside it.");
        await Rail(WorldTool.Stamp);
        if (StaticAt(cx, cy, _stampArt) is { } crate)
        {
            await AimAt(WorldAim.ScreenOfObject(_world, _data, crate), o => o is Static st && st.Graphic == _stampArt && st.X == cx && st.Y == cy);
        }

        await Shot(2.5);
        await Click();
        await Frames(20);
        var pile = _world.Editor.StaticsAt(0, cx, cy).ToList();
        Check(pile.Any(s => s.Id == top) && pile.First(s => s.Id == top).Z > pile.Where(s => s.Id == _stampArt).Select(s => (int)s.Z).DefaultIfEmpty(0).Max(),
            $"the barrel went on top of the crate ({string.Join(", ", pile.Select(s => $"0x{s.Id:X4} z {s.Z}"))})");
        await Shot();

        Say("Select the pile and open Nearby tiles: the list shows the barrel on top, then the crate, then the ground.");
        await Rail(WorldTool.Select);
        if (StaticAt(cx, cy, top) is { } barrel)
        {
            await AimAt(WorldAim.ScreenOfObject(_world, _data, barrel), o => o is Static st && st.Graphic == top && st.X == cx && st.Y == cy);
        }

        await Click();
        await Frames(8);
        _overlay.ClearMarks(keepPointer: true);
        MarkTab(_world.TourDetailTabs, 1, "Nearby tiles");
        PointAtTab(_world.TourDetailTabs, 1);
        await Shot(2.5);
        _world.ShowInspectorTab(false);
        await Frames(10);
        _overlay.ClearMarks(keepPointer: true);
        MarkControl(_world.TourStack, "on this spot");
        Check(_world.TourStack.ItemCount >= 3, $"the list shows barrel, crate and ground ({_world.TourStack.ItemCount} rows)");
        await Shot();

        Say("Hold Alt and turn the mouse wheel over the pile to step down through it: barrel, then crate. The highlighted row is what the next click works on.");
        if (StaticAt(cx, cy, top) is { } barrel2)
        {
            await AimAt(WorldAim.ScreenOfObject(_world, _data, barrel2), o => o is Static st && st.Graphic == top && st.X == cx && st.Y == cy);
        }

        await Wheel(true, alt: true);
        await Frames(4);
        int first = _world.TourStackIndex;
        await Shot(2.5);
        await Wheel(true, alt: true);
        await Frames(4);
        Check(first == 0 && _world.TourStackIndex == 1, $"Alt + wheel stepped down the pile (row {first}, then row {_world.TourStackIndex})");
        await Shot();

        Say("Alt + click takes the highlighted thing, the crate, as the item to place: the box at the top of Tools shows the crate again.");
        await Click(alt: true);
        await Frames(6);
        Check(_data.CurrentArt == EditorData.LandCount + _stampArt, $"Alt + click took the crate as the item to place (now {_data.CurrentArt - EditorData.LandCount:X4})");
        MarkControl(_world.TourArtBox, "item to place");
        await Shot();
        _overlay.ClearMarks(keepPointer: true);

        Say("Shift + click starts again from the top of the pile.");
        await Click(shift: true);
        await Frames(6);
        Check(_world.TourStackIndex < 0, "Shift + click went back to the top of the pile");
        MarkControl(_world.TourStack, "on this spot");
        await Shot();
        _overlay.ClearMarks();
        _world.ShowInspectorTab(true);
        _world.ForcedMouse = null;
    }

    // --- Getting around: pan, zoom, Brushes, Precision, Focus ---------------------------------------------

    private async Task WeView()
    {
        if (!await NeedWe())
        {
            return;
        }

        await OpenWe(EditX, EditY);
        _overlay.ClearMarks();
        int x0 = _world.Host.X, y0 = _world.Host.Y;
        Say("The arrow keys move the view one step; with Shift, eight steps. Two presses of the Up arrow:");
        await Shot(2.5);
        Aim(Centre);
        _overlay.ClearMarks();
        await KeyPress(Key.Up);
        await KeyPress(Key.Up, true);
        await Frames(10);
        Check(_world.Host.X != x0 || _world.Host.Y != y0, $"the arrow keys moved the view ({x0},{y0} to {_world.Host.X},{_world.Host.Y})");
        await Shot();

        int px = _world.Host.X, py = _world.Host.Y;
        Say("Or hold the right mouse button and drag: the map follows the pointer.");
        Aim(Centre);
        await Shot(2.5);
        _world.TourInput(MouseEv(MouseButton.Right, true));
        for (int i = 0; i < 6; i++)
        {
            _world.TourInput(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Right, Relative = new Vector2(30, 14) });
            await Frames(2);
        }

        _world.TourInput(MouseEv(MouseButton.Right, false));
        await Frames(15);
        Check(_world.Host.X != px || _world.Host.Y != py, "right-drag moved the view");
        await Shot();

        await OpenWe(EditX, EditY);
        float z0 = _world.Host.Scene.Camera.Zoom;
        Say("The mouse wheel zooms. Wheel down to step back and see more of the map:");
        Aim(Centre);
        await Shot(2.5);
        await Wheel(false, times: 3);
        await Frames(10);
        Check(_world.Host.Scene.Camera.Zoom != z0, $"the wheel zoomed out ({z0} to {_world.Host.Scene.Camera.Zoom})");
        await Shot();
        Say("Wheel up to come close again.");
        await Wheel(true, times: 3);
        await Frames(8);
        Check(Math.Abs(_world.Host.Scene.Camera.Zoom - z0) < 0.01f, "wheel up returned to the same zoom");
        await Shot();
        _world.ForcedMouse = null;
        _overlay.ClearMarks();

        Say("Making room. Brushes, top right, hides the item library to give the map more space; press it again to bring it back.");
        bool lib = _world.TourLibrary.Visible;
        await Press("Brushes");
        Check(_world.TourLibrary.Visible != lib, "Brushes hid the item library");
        await Shot();
        await Press("Brushes");
        Check(_world.TourLibrary.Visible == lib, "Brushes again brought it back");
        _overlay.ClearMarks();

        Say("Precision hides the library too, so the settings get the whole left side.");
        await Press("Precision");
        Check(_world.TourSettings.IsVisibleInTree() && !_world.TourLibrary.Visible, "Precision shows the settings and hides the library");
        MarkControl(_world.TourSettings, "settings");
        await Shot();
        _overlay.ClearMarks();
        await Press("Brushes");
        _overlay.ClearMarks();

        Say("Focus (or the Tab key) hides every panel so the map fills the window.");
        bool left = _world.TourLeft.Visible;
        Button focus = _world.TourButton("Focus");
        await Press("Focus");
        await Frames(10);
        // The panels went away and the bar re-laid itself out: the hand is still on the button.
        PointAt(focus);
        Check(_world.TourLeft.Visible != left, "Focus hid the side panels");
        await Shot();
        Say("Press Focus again to bring the panels back.");
        await Press("Focus");
        await Frames(10);
        PointAt(focus);
        Check(_world.TourLeft.Visible == left, "Focus again brought the panels back");
        await Shot();
        _overlay.ClearMarks();
    }

    // --- Changing one item: Hue, Set Z / hue, Erase -------------------------------------------------------

    private async Task WeEdit()
    {
        if (!await NeedWe())
        {
            return;
        }

        int cx = EditX + 2, cy = EditY + 8;
        await OpenWe(cx, cy);
        UseArt(_stampArt);
        _world.SetKeepStaticsForSmoke(true);
        Say("First a crate to work on, placed with Stamp as before.");
        await Rail(WorldTool.Stamp);
        await AimCell(cx, cy);
        await Click();
        await Frames(20);
        Check(StaticCount(cx, cy, cx, cy, _stampArt) == 1, "Stamp placed the crate to work on");
        await Shot();
        _overlay.ClearMarks();

        Say("Hue recolours an item. Type a colour number in the Hue box on the Tools tab: 33 is a red.");
        Control hueBox = _world.TourHueBox;
        await ShowControl(hueBox);
        _world.BrushHue = 0x0021;
        await Frames(4);
        MarkControl(hueBox, "Hue");
        PointAt(hueBox);
        await Shot();
        _overlay.ClearMarks();

        Say("Then pick the Hue tool on the bar (keyboard: H) and click the crate. It turns red.");
        await Rail(WorldTool.Hue);
        if (StaticAt(cx, cy, _stampArt) is { } crate)
        {
            await AimAt(WorldAim.ScreenOfObject(_world, _data, crate), o => o is Static st && st.Graphic == _stampArt && st.X == cx && st.Y == cy);
        }

        await Shot(2.5);
        await Click();
        await Frames(15);
        Check(_world.Editor.StaticsAt(0, cx, cy).Any(s => s.Id == _stampArt && s.Hue == 0x0021), $"Hue recoloured the crate (status: {_world.TourStatus.Text})");
        await Shot();
        _overlay.ClearMarks();

        Say("Set Z / hue changes one item exactly. Select the crate, open Nearby tiles and click the crate's row.");
        await Rail(WorldTool.Select);
        if (StaticAt(cx, cy, _stampArt) is { } crate2)
        {
            await AimAt(WorldAim.ScreenOfObject(_world, _data, crate2), o => o is Static st && st.Graphic == _stampArt && st.X == cx && st.Y == cy);
        }

        await Click();
        await Frames(8);
        _world.ShowInspectorTab(false);
        await Frames(10);
        _overlay.ClearMarks();
        ItemList stack = _world.TourStack;
        Check(stack.ItemCount >= 2, $"the list shows the crate and the ground ({stack.ItemCount} rows)");
        if (stack.ItemCount > 0)
        {
            Rect2 row = stack.GetItemRect(0);
            MarkRect(stack, () => new Rect2(stack.GlobalPosition + row.Position, row.Size), "the crate");
            _overlay.Pointer(stack.GetGlobalTransform() * row.GetCenter());
            _ringLands = () => IsInstanceValid(stack) && stack.IsVisibleInTree() ? TourOverlay.FramePx(stack, row.GetCenter()) : null;
            stack.Select(0);
            stack.EmitSignal(ItemList.SignalName.ItemSelected, 0L);
            await Frames(4);
        }

        await Shot();
        _overlay.ClearMarks();

        Say("Type the new height in Z / offset (12) and a colour in Hue (53), then press Set Z / hue: the crate lifts to height 12 and takes the new colour.");
        await SetNumber("Z / ground offset", 12);
        _world.BrushHue = 0x0035;
        await Frames(3);
        MarkControl(hueBox, "Hue");
        PointAt(hueBox);
        await Shot(2.5);
        await Press("Set Z / hue");
        await Frames(15);
        Check(_world.Editor.StaticsAt(0, cx, cy).Any(s => s.Id == _stampArt && s.Z == 12 && s.Hue == 0x0035), "Set Z / hue moved the crate to height 12, colour 53");
        await Shot();
        _overlay.ClearMarks();
        _world.ShowInspectorTab(true);
        _world.TourNumber("Z / ground offset").Value = 0;
        _world.BrushHue = 0;

        Say("Erase (keyboard: E) removes the item you click. Only items: the ground itself cannot be erased.");
        await Rail(WorldTool.Erase);
        if (StaticAt(cx, cy, _stampArt) is { } at)
        {
            await AimAt(WorldAim.ScreenOfObject(_world, _data, at), o => o is Static st && st.Graphic == _stampArt);
        }

        await Shot(2.5);
        await Click();
        await Frames(15);
        Check(StaticCount(cx, cy, cx, cy, _stampArt) == 0, "Erase removed the crate");
        await Shot();
        _overlay.ClearMarks();
        _world.ForcedMouse = null;
    }

    // --- The ground: Raise, Lower, Shift for five, Lock terrain ---------------------------------------------

    private async Task WeLand()
    {
        if (!await NeedWe())
        {
            return;
        }

        int cx = EditX + 4, cy = EditY + 2;
        await OpenWe(cx, cy);
        _overlay.ClearMarks();
        Say("Raise (keyboard: R) lifts the ground under the pointer one step per click.");
        await Rail(WorldTool.Raise);
        await AimCell(cx, cy);
        await Shot(2.5);
        int z0 = LandZ(cx, cy);
        await Click();
        await Frames(15);
        int z1 = LandZ(cx, cy);
        Check(z1 == z0 + 1, $"Raise lifted the ground one step ({z0} to {z1})");
        _overlay.SetDetail($"Ground height here: {z0} â†’ {z1}");
        await Shot();

        Say("Shift + click lifts it five steps at once.");
        await AimCell(cx, cy);
        await Click(MouseButton.Left, true);
        await Frames(15);
        int z2 = LandZ(cx, cy);
        Check(z2 == z1 + 5, $"Shift + Raise lifted it five steps ({z1} to {z2})");
        _overlay.SetDetail($"Ground height here: {z1} â†’ {z2}");
        await Shot();

        Say("Lower (keyboard: F) works the same way, downward. Shift + click: five steps down.");
        await Rail(WorldTool.Lower);
        await AimCell(cx, cy);
        await Click(MouseButton.Left, true);
        await Frames(15);
        int z3 = LandZ(cx, cy);
        Check(z3 == z2 - 5, $"Shift + Lower dropped it five steps ({z2} to {z3})");
        _overlay.SetDetail($"Ground height here: {z2} â†’ {z3}");
        await Shot();
        _overlay.SetDetail(null);
        _overlay.ClearMarks();

        Say("Lock terrain protects the ground while you place items. Tick it on the Tools tab, then try Raise again.");
        await SetCheck("Lock terrain", true);
        await Shot(2.5);
        _overlay.ClearMarks();
        await Rail(WorldTool.Raise);
        await AimCell(cx, cy);
        await Click();
        await Frames(10);
        Check(LandZ(cx, cy) == z3 && _world.TourStatus.Text == "Terrain is locked", $"Raise was refused and the line under the map said '{_world.TourStatus.Text}'");
        MarkControl(_world.TourStatus, "why nothing happened");
        Say("The ground stays as it is, and the line under the map says why: \"Terrain is locked\".");
        await Shot();
        _overlay.ClearMarks();
        await SetCheck("Lock terrain", false);
        _overlay.ClearMarks();
        _world.ForcedMouse = null;
    }

    // --- Heights: Fixed Z, the visible height range, Ghost roofs ------------------------------------------------

    private async Task WeHeights()
    {
        if (!await NeedWe())
        {
            return;
        }

        int cx = EditX - 4, cy = EditY + 4;
        await OpenWe(cx, cy);
        UseArt(_stampArt);
        _overlay.ClearMarks();
        Say("Normally an item sits on the ground. Tick Fixed Z and set Z / offset to 30, and everything you place goes at height 30, whatever the ground does.");
        await SetCheck("Fixed placement plane", true);
        await SetNumber("Z / ground offset", 30);
        await Shot();
        _overlay.ClearMarks();

        Say("Then Stamp and click: the crate floats at height 30, above the grass.");
        await Rail(WorldTool.Stamp);
        await AimCell(cx, cy);
        await Frames(10);
        await Shot(2.5);
        await Click();
        await Frames(20);
        Check(_world.Editor.StaticsInRect(0, cx - 3, cy - 3, cx + 3, cy + 3).Any(s => s.S.Id == _stampArt && s.S.Z == 30), "with Fixed Z the crate went to height 30");
        await Shot();
        _overlay.ClearMarks();
        await SetCheck("Fixed placement plane", false);
        await SetNumber("Z / ground offset", 0);
        _overlay.ClearMarks();
        _world.ForcedMouse = null;

        Say("Z min and Z max hide everything below or above a height, to work on one floor of a building. Z min 20 hides the ground and leaves the floating crate.");
        await SetNumber("Visible Z min", 20);
        await Frames(25);
        Check(_world.Host.MinVisibleZ == 20, "Z min 20 is the lowest height drawn");
        await Shot();
        _overlay.ClearMarks();
        await SetNumber("Visible Z min", -128);
        await Frames(15);
        Check(_world.Host.MinVisibleZ == -128, "Z min is back at -128");
        Say("Z min back at -128 shows everything again.");
        await Shot();
        _overlay.ClearMarks();

        Say("Ghost roofs. This is the bank in Britain with its roof on, so you cannot see inside.");
        await OpenWe(1434, 1697);
        await Frames(10);
        await Shot();
        Say("Tick Ghost roofs: roofs turn into faint outlines, so you can see and work inside the building.");
        await SetCheck("Ghost roofs", true);
        await Frames(30);
        Check(_world.Host.GhostRoofs, "Ghost roofs is on");
        await Shot();
        await SetCheck("Ghost roofs", false);
        _overlay.ClearMarks();
        _world.ForcedMouse = null;
    }


    private static string Slash(string path) => path.Replace((char)92, (char)47);

    // --- Export and verify the project the tour edited ----------------------------------------------------

    private async Task WeExport()
    {
        if (_weRoot == null || !Directory.Exists(_weRoot))
        {
            Skip("the edit project was not created (the World tab did not boot)");
            return;
        }

        string export = Path.Combine(_out, "edit_export");
        if (Directory.Exists(export))
        {
            Directory.Delete(export, true);
        }

        int blocks = Directory.GetFiles(Path.Combine(_weRoot, "blocks"), "*.json", SearchOption.AllDirectories).Length;
        _overlay.ClearMarks();
        Say($"Last, getting the work out. Everything changed in this project is {blocks} patches of 8 by 8 tiles. Export writes them as map files a shard can use, in a folder of their own; the game's own files are never touched.");
        await Shot(4);
        var (c1, t1) = await RunTool("export", "--project", _weRoot, "--out", export);
        _overlay.SetDetail(Scrub("$ tools/world export\n" + Tail(t1, 6)));
        Check(c1 == 0 && Directory.Exists(export) && Directory.GetFiles(export).Length > 0, "export exited 0 and wrote files");
        await Shot(5);
        var (c2, t2) = await RunTool("verify", "--project", _weRoot, "--out", export);
        _overlay.SetDetail(Scrub("$ tools/world verify\n" + Tail(t2, 6)));
        Say("Verify reads the exported files back and compares them with the project, patch by patch: they match.");
        Check(c2 == 0, "verify passed on the export of the edited project");
        await Shot(5);
        _overlay.SetDetail(null);
    }
}
#endif
