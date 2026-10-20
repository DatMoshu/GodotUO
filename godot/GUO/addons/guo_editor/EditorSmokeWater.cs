// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using GUO.Renderer.Water;
using GUO.Configuration;

public partial class EditorSmoke
{
    private void AddWaterChecks()
    {
        _steps.Add((1, () =>
        {
            var panel = _world.WaterWorkspace;
            Expect(panel != null && panel.Name == "Water", "water_workspace_registered");
            var previous = panel.Style;
            string playerStyle = ProfileManager.CurrentProfile.WaterStyle;
            panel.LoadStyle(WaterAuthoring.Presets[1]);
            panel.WorldToggle.ButtonPressed = true;
            Expect(WaterRender.EditorWorld == _world.Host.World && WaterRender.Resolve(ProfileManager.CurrentProfile)?.DisplayName == "Pixel water", "water_preview_scoped_to_world");
            var inspection = System.Text.Json.JsonSerializer.SerializeToElement(WaterRender.Inspect(_world.Host.World));
            Expect(inspection.GetProperty("selectedStyle").GetString() == "editor-draft", "water_diagnostics_report_selected_draft");
            Expect(WaterRender.Inspect(null) == null, "water_diagnostics_before_world_are_null");
            int history = panel.HistoryPosition;
            panel.ShowLibrary();
            Expect(panel.LibraryOpen && panel.LibraryCount >= WaterAuthoring.Presets.Length, "water_browser_starter_catalog_available");
            panel.LibraryAction(true);
            Expect(!panel.LibraryOpen && panel.HistoryPosition == history && panel.Style.Name == "Pixel water"
                && WaterRender.Resolve(ProfileManager.CurrentProfile)?.DisplayName == "Pixel water",
                "water_browser_reference_preserves_draft_history_and_world_style");
            panel.KeepReference();
            panel.LoadStyle(WaterAuthoring.Presets[6]);
            Expect(panel.Preview.ReferenceStyle.Name == "Pixel water"
                && WaterRender.Resolve(ProfileManager.CurrentProfile)?.DisplayName == WaterAuthoring.Presets[6].Name,
                "water_reference_does_not_replace_live_world_draft");
            panel.ClearReference();
            Expect(WaterRender.Resolve(ProfileManager.CurrentProfile)?.DisplayName == WaterAuthoring.Presets[6].Name,
                "water_clear_reference_preserves_live_world_draft");
            panel.CompareToggle.ButtonPressed = false;
            panel.LoadStyle(WaterAuthoring.Presets[1]);
            panel.ClassicToggle.ButtonPressed = true;
            Expect(WaterRender.Resolve(ProfileManager.CurrentProfile) == null, "water_classic_comparison_bypasses_style");
            panel.ClassicToggle.ButtonPressed = false;
            Expect(WaterRender.Resolve(ProfileManager.CurrentProfile)?.DisplayName == "Pixel water", "water_comparison_restores_draft");
            panel.WorldToggle.ButtonPressed = false;
            Expect(WaterRender.EditorWorld == null && ProfileManager.CurrentProfile.WaterStyle == playerStyle, "water_preview_restores_profile_without_saving");
            panel.LoadStyle(previous);
        }));
        _steps.Add((1, CheckWaterNativeActors));
    }

    private void CheckWaterNativeActors()
    {
        var panel = _world.WaterWorkspace;
        var host = _world.Host;
        var home = (host.Facet, host.X, host.Y, host.Z);
        var previous = panel.Style;
        uint ticks = GUO.Time.Ticks;
        try
        {
            _world.GoTo(0, 1505, 1785);
            panel.LoadStyle(WaterAuthoring.Presets[1]);
            int items = host.World.Items.Count, mobiles = host.World.Mobiles.Count;
            var before = (host.X, host.Y, host.Z);
            panel.StartWorldTest(true);
            var test = panel.NativeTest;
            uint boat = test.Serial;
            Expect(test.Active && test.Kind == "boat" && host.World.Get(boat) is GUO.Game.GameObjects.Item item && item.IsMulti,
                "water_native_boat_uses_real_multi");
            int startY = test.ActorY;
            for (int i = 0; i < 10; i++) test.Advance(.2f, false);
            Expect(test.TravelledCells > 0 && test.ActorY != startY, "water_native_boat_changes_world_coordinates");
            int moves = test.Moves, frozenY = test.ActorY;
            for (int i = 0; i < 10; i++) test.Advance(.2f, true);
            Expect(test.Moves == moves && test.ActorY == frozenY, "water_native_freeze_stops_actor_motion");
            panel.StopWorldTest();
            Expect(host.World.Get(boat) == null && !host.World.HouseManager.TryGetHouse(boat, out _)
                && host.World.Items.Count == items && (host.X, host.Y, host.Z) == before,
                "water_native_boat_cleanup_removes_components_and_restores_untouched_camera");

            panel.StartWorldTest(false);
            uint wader = test.Serial;
            Expect(test.Active && host.World.Get(wader) is GUO.Game.GameObjects.Mobile mobile && mobile.Graphic == 0x0190,
                "water_native_wader_uses_real_mobile");
            for (int i = 0; i < 4; i++) { GUO.Time.Ticks += 100; test.Advance(.1f, false); }
            var pending = host.World.Mobiles[wader];
            int queued = pending.Steps.Count, pausedY = test.ActorY;
            for (int i = 0; i < 10; i++) { GUO.Time.Ticks += 100; test.Advance(.1f, true); }
            Expect(queued > 0 && pending.Steps.Count == queued && test.ActorY == pausedY,
                "water_native_freeze_preserves_a_pending_mobile_step");
            for (int i = 0; i < 40; i++)
            {
                GUO.Time.Ticks += 100;
                test.Advance(.1f, false);
            }
            Expect(test.TravelledCells > 0, "water_native_wader_completes_native_steps");
            panel.StopWorldTest();
            Expect(host.World.Get(wader) == null && host.World.Mobiles.Count == mobiles,
                "water_native_wader_cleanup_preserves_other_mobiles");

            panel.StartWorldTest(true); boat = test.Serial;
            host.RemoveServerObject(boat);
            var replacement = host.PlaceServerItem(boat, 0x0E75, (ushort)host.X, (ushort)host.Y, (sbyte)host.Z);
            test.Advance(.1f, false);
            Expect(!test.Active && ReferenceEquals(host.World.Get(boat), replacement),
                "water_native_cleanup_preserves_a_replacement_with_the_same_serial");
            host.RemoveServerObject(boat);
            panel.StartWorldTest(false); wader = test.Serial;
            panel.WorldToggle.ButtonPressed = false;
            Expect(!test.Active && host.World.Get(wader) == null, "water_native_disabling_preview_removes_actor");
        }
        finally
        {
            panel.StopWorldTest(); panel.WorldToggle.ButtonPressed = false;
            panel.LoadStyle(previous);
            _world.GoTo(home.Facet, home.X, home.Y);
            host.World.Player.Z = (sbyte)home.Z;
            GUO.Time.Ticks = ticks;
        }
    }
}
#endif
