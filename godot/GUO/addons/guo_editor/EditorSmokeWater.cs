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
            panel.ClassicToggle.ButtonPressed = true;
            Expect(WaterRender.Resolve(ProfileManager.CurrentProfile) == null, "water_classic_comparison_bypasses_style");
            panel.ClassicToggle.ButtonPressed = false;
            Expect(WaterRender.Resolve(ProfileManager.CurrentProfile)?.DisplayName == "Pixel water", "water_comparison_restores_draft");
            panel.WorldToggle.ButtonPressed = false;
            Expect(WaterRender.EditorWorld == null && ProfileManager.CurrentProfile.WaterStyle == playerStyle, "water_preview_restores_profile_without_saving");
            panel.LoadStyle(previous);
        }));
    }
}
#endif
