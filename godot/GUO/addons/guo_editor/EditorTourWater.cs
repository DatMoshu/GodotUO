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
        var previousReference = panel.Preview.ReferenceStyle;
        bool previousCompare = panel.CompareToggle.ButtonPressed;
        bool previousFreeze = panel.FreezeToggle.ButtonPressed;
        bool previousReplay = panel.ReplayExpanded;
        int previousTest = panel.ReplayTest.Selected;
        double previousTestTime = panel.ReplayTime.Value;
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
            panel.ShowLibrary();
            await Frames(3);
            Check(panel.LibraryOpen && panel.LibraryCount >= WaterAuthoring.Presets.Length,
                "the inline preset browser lists starter styles");
            Check(panel.GetGlobalRect().HasPoint(panel.LibraryTree.GetGlobalRect().Position + new Vector2(20, 40)),
                "the browser opens with a result row visible in the native scroll area");
            Say("Browse presets searches by name and filters by model or source. Use as reference compares a saved style while keeping the working draft and undo history.");
            await Shot(3);
            var browserDraft = WaterAuthoring.Serialize(panel.Style);
            int browserHistory = panel.HistoryPosition;
            panel.LibraryAction(true);
            Check(!panel.LibraryOpen && panel.HistoryPosition == browserHistory
                && WaterAuthoring.Serialize(panel.Style).AsSpan().SequenceEqual(browserDraft),
                "browser reference keeps the working draft and history intact");
            Say("Water has editable presets and a synthetic preview. Preview in World applies the same style to the map's wet tiles without saving a player setting.");
            await Shot(3);
            panel.KeepReference();
            float before = panel.Style.Glint;
            int scrubHistory = panel.HistoryPosition;
            var scrubLabel = WaterWorkbenchProbe.ScrubLabel(panel, "Glints");
            WaterWorkbenchProbe.ScrubButton(scrubLabel, true);
            for (int i = 0; i < 5; i++) WaterWorkbenchProbe.ScrubMove(scrubLabel, 3);
            WaterWorkbenchProbe.ScrubButton(scrubLabel, false);
            Check(panel.HistoryPosition == scrubHistory + 1, "a numeric label drag creates one history action");
            panel.Undo(); Check(panel.Style.Glint == before, "one Undo restores the label drag's starting value");
            panel.NumberInput("Glints").Value = 0;
            Check(panel.Style.Glint == 0 && panel.Dirty, "the numeric control updates the draft");
            panel.Undo(); Check(panel.Style.Glint == before, "water tuning is undoable");
            panel.Redo(); Check(panel.Style.Glint == 0, "water tuning is redoable");
            Check(panel.Preview.ReferenceStyle.Glint == before, "kept reference survives tuning and undo");
            panel.Preview.Splash();
            Say("Keep reference compares the original preset on the left with your edited draft on the right. Splash and hull inputs share the same clock; only the draft previews in World.");
            await Shot(3);
            panel.ClassicToggle.ButtonPressed = true;
            await Frames(8);
            Check(WaterRender.Resolve(GUO.Configuration.ProfileManager.CurrentProfile) == null, "Classic comparison bypasses the draft");
            Say("Classic comparison draws the original tiles. The draft remains available when comparison is turned off.");
            await Shot(3);
            panel.ClassicToggle.ButtonPressed = false;
            panel.WorldToggle.ButtonPressed = false;
            int replayHistory = panel.HistoryPosition;
            panel.ReplayTest.Select(2); panel.ReplayTime.Value = 1.5;
            panel.ReplayPreview(); panel.ShowReplayControls();
            await Frames(3);
            Check(panel.Preview.Paused && panel.Preview.Seconds == 1.5f && panel.HistoryPosition == replayHistory,
                "repeatable synthetic test pauses at the chosen time without editing history");
            Check(panel.GetGlobalRect().HasPoint(panel.Preview.GetGlobalRect().GetCenter()),
                "repeatable test controls keep the synthetic preview visible in the native scroll area");
            Say("Repeatable synthetic tests rebuild splash and hull inputs from time zero, then pause at the chosen time. Step 0.05 s compares the next fixed pose; this does not replay native World actors.");
            await Shot(3);
            string png = Path.Combine(_out, "water-synthetic-preview.png");
            Check(panel.SavePreviewPath(png) && File.Exists(png), "the synthetic comparison was exported locally as PNG");
            Check(panel.Preview.Paused && panel.Preview.Seconds == 1.5f && panel.HistoryPosition == replayHistory,
                "PNG export preserves the repeatable pose and draft history");
            panel.ShowReplayControls(false);
            await Frames(3);
            panel.ScrollVertical = (int)panel.Preview.Position.Y;
            Say("Save preview PNG keeps the rendered synthetic comparison at its original resolution. The saved image excludes World artwork, controls and preset metadata; save a draft separately to keep settings.");
            await Shot(3);
            panel.WorldToggle.ButtonPressed = true;
            panel.FreezeToggle.ButtonPressed = false;
            string zip = Path.Combine(_out, "water-authoring.zip");
            WaterAuthoring.Export(panel.Style, zip, "water-tour", "1.0.0", "GUO tour", "CC0-1.0");
            Check(File.Exists(zip), "a data-only water pack was exported locally");
            Say("Save a local draft or export a validated Store water pack. A pack carries only style numbers and an original palette swatch; nothing is published or activated here.");
            await Shot(3);
            panel.FreezeToggle.ButtonPressed = false;
            panel.StartWorldTest(true);
            Check(panel.NativeTest.Active && panel.NativeTest.Kind == "boat", "native boat test found a clear water route");
            await Frames(6);
            var boatState = System.Text.Json.JsonSerializer.SerializeToElement(WaterRender.Inspect(_world.Host.World));
            Check(boatState.GetProperty("active").GetBoolean() && !boatState.GetProperty("playerDisturbs").GetBoolean(),
                "native boat style is active and the camera does not disturb the water");
            int stamps = boatState.GetProperty("rippleStamps").GetInt32();
            await Frames(120);
            Check(panel.NativeTest.TravelledCells > 0, $"native boat moved {panel.NativeTest.TravelledCells} world cells");
            int boatStamps = System.Text.Json.JsonSerializer.SerializeToElement(WaterRender.Inspect(_world.Host.World)).GetProperty("rippleStamps").GetInt32() - stamps;
            Check(boatStamps > 0, $"native boat preview added {boatStamps} ripple stamps with the invisible camera above water");
            panel.FreezeToggle.ButtonPressed = true; panel.ScrollVertical = 300;
            Say("Boat in World uses a native small boat on a temporary scripted route. Its movement drives the client's own wake field; Freeze pauses the test.");
            await Shot(3);
            panel.StopWorldTest(); panel.FreezeToggle.ButtonPressed = false;
            panel.StartWorldTest(false);
            Check(panel.NativeTest.Active && panel.NativeTest.Kind == "wader", "native wader test found a clear water route");
            stamps = System.Text.Json.JsonSerializer.SerializeToElement(WaterRender.Inspect(_world.Host.World)).GetProperty("rippleStamps").GetInt32();
            await Frames(120);
            Check(panel.NativeTest.TravelledCells > 0, $"native wader completed {panel.NativeTest.TravelledCells} cells through the mobile step API");
            Check(System.Text.Json.JsonSerializer.SerializeToElement(WaterRender.Inspect(_world.Host.World)).GetProperty("rippleStamps").GetInt32() > stamps,
                "native wader preview adds ripple stamps");
            panel.FreezeToggle.ButtonPressed = true;
            Say("Wader in World uses native mobile steps and artwork. This is a rendering fixture; it does not prove gameplay collision, pathfinding or network movement.");
            await Shot(3);
            uint actor = panel.NativeTest.Serial;
            panel.StopWorldTest();
            Check(_world.Host.World.Get(actor) == null, "stopping the native test removes its actor");
            Say("Stop test removes temporary actors. The map, player profile and shard are unchanged by these test actions.");
            await Shot(3);
        }
        finally
        {
            panel.StopWorldTest(); panel.FreezeToggle.ButtonPressed = previousFreeze;
            panel.ShowReplayControls(previousReplay); panel.ReplayTest.Select(previousTest); panel.ReplayTime.Value = previousTestTime;
            panel.WorldToggle.ButtonPressed = false; panel.ClassicToggle.ButtonPressed = false;
            panel.SetReference(previousReference);
            panel.CompareToggle.ButtonPressed = previousCompare;
            panel.LoadStyle(previous); _world.SettingsTabs.CurrentTab = tab;
        }
    }
}
#endif
