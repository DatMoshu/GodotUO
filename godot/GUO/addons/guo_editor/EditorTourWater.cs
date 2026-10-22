// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer.Water;

public partial class EditorTour
{
    private async Task WaterSeg()
    {
        // A single-segment run does not have an earlier World segment to boot it.
        (GetParent() as GuoEditorPlugin)?.ShowInWorld(0, 1490, 1755);
        for (int i = 0; i < 900 && !WorldUp && _world.Error == null; i++) await Frames(1);
        Check(WorldUp, "the World view initialized for water authoring");
        if (!WorldUp) return;
        var panel = _world.WaterWorkspace;
        int tab = _world.SettingsTabs.CurrentTab;
        var previous = panel.Style;
        try
        {
            EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
            _world.SettingsTabs.CurrentTab = 2;
            _world.GoTo(0, 1490, 1755);
            panel.LoadStyle(WaterAuthoring.Presets[1]);
            panel.WorldToggle.ButtonPressed = true;
            await Frames(12);
            Check(panel.IsVisibleInTree() && panel.Size.X >= 240 && panel.Size.Y >= 150,
                "the Water workspace is visible at a usable size");
            Check(WaterRender.EditorWorld == _world.Host.World, "the draft previews only in this editor world");
            Say("Water has editable presets and a synthetic preview. Preview in World applies the same style to the map's wet tiles without saving a player setting.");
            await Shot(3);
            float before = panel.Style.Glint;
            panel.NumberInput("Glints").Value = 0;
            Check(panel.Style.Glint == 0 && panel.Dirty, "the numeric control updates the draft");
            panel.Undo(); Check(panel.Style.Glint == before, "water tuning is undoable");
            panel.Redo(); Check(panel.Style.Glint == 0, "water tuning is redoable");
            panel.Preview.Splash();
            Say("Test a spreading splash or a moving synthetic hull, then freeze the water or compare with Classic tiles. These tests need no shard.");
            await Shot(3);
            panel.ClassicToggle.ButtonPressed = true;
            await Frames(8);
            Check(WaterRender.Resolve(GUO.Configuration.ProfileManager.CurrentProfile) == null, "Classic comparison bypasses the draft");
            Say("Classic comparison draws the original tiles. The draft remains available when comparison is turned off.");
            await Shot(3);
            panel.ClassicToggle.ButtonPressed = false;
            string zip = Path.Combine(_out, "water-authoring.zip");
            WaterAuthoring.Export(panel.Style, zip, "water-tour", "1.0.0", "GUO tour", "CC0-1.0");
            Check(File.Exists(zip), "a data-only water pack was exported locally");
            Say("Save a local draft or export a validated Store water pack. A pack carries only style numbers and an original palette swatch; nothing is published or activated here.");
            await Shot(3);
        }
        finally
        {
            panel.WorldToggle.ButtonPressed = false; panel.ClassicToggle.ButtonPressed = false;
            panel.LoadStyle(previous); _world.SettingsTabs.CurrentTab = tab;
        }
    }
}
#endif
