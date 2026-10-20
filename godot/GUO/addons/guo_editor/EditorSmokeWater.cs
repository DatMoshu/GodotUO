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
