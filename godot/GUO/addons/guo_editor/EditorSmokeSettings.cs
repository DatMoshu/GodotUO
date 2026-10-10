#if TOOLS
namespace GUO.Editor;

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

/// <summary>ED5: the World tab's settings panel stays readable (folds announced, no overlap, names in words).</summary>
public partial class EditorSmoke
{
    /// <summary>Checks that need no window: the rail's names, the item box with ground, the legend under the hint.</summary>
    private void AddSettingsChecks()
    {
        uint keepArt = 0;
        _steps.Add((1, () =>
        {
            foreach (var (tool, button) in _world.RailButtons)
                Expect(button.TooltipText.StartsWith(WorldView.ToolName(tool)) && !Regex.IsMatch(button.TooltipText, "[a-z][A-Z]"),
                    $"rail_names_{tool}_in_words");
            WorldTool before = _world.Tool;
            _world.Tool = WorldTool.PlaceItem;
            Expect(_world.ToolLabel == "Place item", "active_tool_named_in_words");
            _world.Tool = before;
            keepArt = _data.CurrentArt;
            _data.CurrentArt = 0x0016; // a sand ground tile
        }));
        _steps.Add((3, () =>
        {
            AssetField box = _world.ItemBox;
            GD.Print($"[GUO settings] item box after a ground pick: kind {box.Kind}, value 0x{box.Value:X4}");
            Expect(box.Kind == AssetPickKind.Land && box.Value == 0x0016, "item_box_follows_ground_pick");
            SettingsShot("editor_item_box_ground");
            _data.CurrentArt = keepArt;
        }));
        _steps.Add((3, () =>
        {
            AssetField box = _world.ItemBox;
            Expect(box.Kind == AssetPickKind.Static && box.Value == (int)(keepArt - EditorData.LandCount), "item_box_follows_item_pick");
        }));
    }

    private void SettingsShot(string name)
    {
        if (Headless) return;
        using Image shot = EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture()?.GetImage();
        shot?.SavePng(Path.Combine(_out, $"{name}{Suffix}.png"));
    }

    /// <summary>The legend box sits under the map's hint line and its text is the editor's size.</summary>
    private void CheckLegendBelowHint()
    {
        Rect2 hint = _world.MapHint.GetGlobalRect(), legend = new(_world.Chip.GlobalPosition, _world.Chip.DrawnSize);
        GD.Print($"[GUO settings] hint {hint}, legend {legend}");
        ModeExpect(legend.Size.Y > 0 && !legend.Intersects(hint), "legend_clear_of_map_hint");
        ModeExpect(LegendChip.FontSize >= 16, "legend_text_editor_size");
        SettingsShot("editor_legend_walkability");
    }

    /// <summary>At one window size: Tools fits or announces its fold, Advanced announces its fold, the panels do not overlap.</summary>
    private void AddSettingsLayoutSteps(string size)
    {
        _steps.Add((1, () =>
        {
            TabContainer settings = _world.SettingsTabs, detail = _world.DetailTabs;
            settings.CurrentTab = 0;
            Rect2 s = settings.GetGlobalRect(), d = detail.GetGlobalRect();
            GD.Print($"[GUO settings] {size}: settings {s}, inspector {d}, tools fit {_world.CommonToolsFit()}");
            Expect(s.End.Y <= d.Position.Y + 1, $"inspector_strip_below_settings_{size}");
            Expect(_world.CommonToolsFit() || WorldView.FoldAnnounced(_world.CommonTools, _world.ScrollCues[0]), $"tools_fold_announced_{size}");
            Expect(_world.CommonToolsFit(), $"tools_get_their_rows_{size}");
            settings.CurrentTab = 1;
        }));
        _steps.Add((4, () =>
        {
            ScrollContainer advanced = _world.AdvancedTools;
            ScrollCue cue = _world.ScrollCues[1];
            bool overflow = advanced.GetVScrollBar().MaxValue > advanced.Size.Y + 2;
            GD.Print($"[GUO settings] {size}: Advanced {advanced.Size} content {advanced.GetVScrollBar().MaxValue}, cue {cue.Visible}");
            Expect(WorldView.FoldAnnounced(advanced, cue), $"advanced_fold_announced_{size}");
            Rect2 tab = _world.SettingsTabs.GetGlobalRect(), d = _world.DetailTabs.GetGlobalRect();
            Expect(tab.End.Y <= d.Position.Y + 1, $"inspector_strip_clear_of_advanced_{size}");
            if (overflow) Expect(cue.GetGlobalRect().End.Y <= d.Position.Y + 1, $"cue_inside_advanced_{size}");
            SettingsShot($"editor_settings_advanced_{size}");
        }));
        _steps.Add((1, () => _world.SettingsTabs.CurrentTab = 0));
    }
}
#endif
