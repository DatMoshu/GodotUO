#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Game.GameObjects;

public partial class EditorTour
{
    private async Task WeBoundaries()
    {
        if (!await NeedWe()) return;
        await OpenWe(EditX, EditY);
        await Rail(WorldTool.Select);
        await AimCell(EditX, EditY);
        await Click();
        var picked = _world.Host.Picked as GameObject;
        Check(picked != null && _world.Area == ((int)picked.X, (int)picked.Y, (int)picked.X, (int)picked.Y)
            && _world.Guides.Area == _world.Area, "Select click highlights the owning ground cell");
        Say("One click highlights one tile. Drag to select a larger rectangle.");
        _overlay.ClearMarks();
        await Shot();

        Control root = EditorInterface.Singleton.GetBaseControl();
        async Task<PopupMenu> OpenMenu(WorldTool tool)
        {
            _world.Tool = tool;
            await AimCell(EditX, EditY);
            await Click(MouseButton.Right);
            return root.GetNodeOrNull<PopupMenu>("WorldMapActions");
        }
        bool Has(PopupMenu menu, string label) => menu != null &&
            Enumerable.Range(0, menu.ItemCount).Any(i => menu.GetItemText(i) == label);
        async Task Close(PopupMenu menu)
        {
            menu?.Hide();
            await Frames(3);
        }

        var menu = await OpenMenu(WorldTool.Brush);
        Check(Has(menu, "Brush settings") && !Has(menu, "Use area in Regions"),
            "Brush context shows brush actions");
        await Close(menu);
        menu = await OpenMenu(WorldTool.Measure);
        Check(Has(menu, "Start measurement here") && !Has(menu, "Finish measurement here"),
            "Measure context only offers a valid next step");
        await Close(menu);
        menu = await OpenMenu(WorldTool.Area);
        Check(Has(menu, "Use area in Regions") && Has(menu, "Use area in Audio Zones"),
            "Area offers separate handoffs for regions and audio");
        if (menu == null) return;
        int handoff = Enumerable.Range(0, menu.ItemCount).FirstOrDefault(i => menu.GetItemText(i) == "Use area in Regions", -1);
        if (handoff < 0) { await Close(menu); return; }
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, menu.GetItemId(handoff));
        await Frames(4);
        Check(_world.Tool == WorldTool.PickRegion && _world.Guides.BoundaryDraft is { Tiles: 1 },
            "Area hands its one tile to Regions as a visible draft");
        Say("Regions shows the draft boundary. Apply stages it in the Regions dock for saving.");
        await Shot();

        string scratch = Path.GetFullPath(Path.Combine(_out, "fixture"));
        bool writableFixture = System.Environment.GetEnvironmentVariable("GUO_MUSIC_DIR") == Path.Combine(scratch, "audio") &&
            System.Environment.GetEnvironmentVariable("UO_SHARD_DIST") == Path.Combine(scratch, "shard");
        if (!writableFixture)
        {
            await KeyPress(Key.Escape);
            Check(_world.Guides.BoundaryDraft == null, "Escape discards the boundary draft");
            return;
        }

        var name = All<LineEdit>(_world).First(c => c.IsVisibleInTree() && c.PlaceholderText == "Name");
        void NameBoundary(string text)
        {
            name.Text = text;
            name.EmitSignal(LineEdit.SignalName.TextChanged, text);
        }
        Button Button(string text) => All<Button>(_world).First(b => b.IsVisibleInTree() && b.Text == text);
        var region = _world.RegionEditor;
        string regionsFile = Path.Combine(scratch, "shard", "Data", "regions.json");
        string original = File.ReadAllText(regionsFile);
        int count = region.RegionCount;
        NameBoundary("Tour region");
        Button("Apply boundary").EmitSignal(BaseButton.SignalName.Pressed);
        Check(region.RegionCount == count + 1 && region.Dirty && File.ReadAllText(regionsFile) == original,
            "applying a region keeps disk unchanged until Save regions.json");
        Check(region.Save().StartsWith("saved", StringComparison.Ordinal) && File.ReadAllText(regionsFile).Contains("Tour region"),
            "the Regions dock saves the named region");

        await Rail(WorldTool.AudioZones);
        var mode = All<OptionButton>(_world).First(b => b.Name == "BoundaryMode");
        mode.Select(1);
        mode.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        await AimCell(EditX, EditY);
        await Click();
        NameBoundary("Tour music");
        Check(_world.Guides.BoundaryDraft is { Tiles: 1 }, "one click draws a one-tile music zone");
        mode.Select(2);
        mode.EmitSignal(OptionButton.SignalName.ItemSelected, 2);
        await Frames(3);
        Say("Audio Zones uses the same handles. Pick Music or SFX and save the audio zone independently.");
        await Shot();
        var before = _world.Guides.BoundaryDraft;
        if (before is { } bounds && CellGeometry.From(_world.Host) is { } geometry)
        {
            Aim((Vector2I)bounds.Corners(geometry)[2]);
            await Frames(3);
            _world.TourInput(MouseEv(MouseButton.Left, true));
            await AimCell(EditX + 2, EditY + 1);
            _world.TourInput(new InputEventMouseMotion { ButtonMask = MouseButtonMask.Left, Relative = new Vector2(24, 0) });
            await Frames(3);
            _world.TourInput(MouseEv(MouseButton.Left, false));
            await Frames(3);
            Check(_world.Guides.BoundaryDraft is { Tiles: > 1 }, "dragging a corner resizes the draft");
        }
        var saved = _world.Guides.BoundaryDraft;
        Button("Save zone").EmitSignal(BaseButton.SignalName.Pressed);
        var zone = GUO.IO.Audio.ZoneAudio.Music.Zones.SingleOrDefault(z => z.Name == "Tour music");
        Check(zone != null && saved == MapBoundary.Between(zone.X0, zone.Y0, zone.X1, zone.Y1)
            && File.Exists(Path.Combine(scratch, "audio", "music.json"))
            && !File.Exists(Path.Combine(scratch, "audio", "sfx.json")),
            "saving Music preserves its rectangle and writes only the music manifest");
        _world.ForcedMouse = null;
    }
}
#endif
