#if TOOLS
namespace GUO.Editor;

using System.Linq;
using System.Text.RegularExpressions;
using Godot;

/// <summary>ED6: the World tab's messages in plain words (view legends, Set Z / hue, Keep existing statics, names before numbers).</summary>
public partial class EditorSmoke
{
    private static readonly Regex Jargon = new(@"\b(statics?|graphics?|z-fights?|tiledata)\b", RegexOptions.IgnoreCase);

    private void AddWordsChecks()
    {
        const ushort tree = 0x0E3D;
        int cx = (EditX & ~7) + 7 + 3, cy = EditY + 3;
        uint keepArt = 0;
        int undo = 0;
        _steps.Add((1, () =>
        {
            foreach (string name in _world.Modes.ModeNames)
            {
                string meaning = _world.Modes.ModeNamed(name).Meaning;
                Expect(meaning.Length > 0 && !Jargon.IsMatch(meaning), $"mode_{name.Replace(' ', '_')}_says_what_colours_mean");
            }
            Expect(_world.Modes.ModeNamed("IDs").Meaning.Contains("normal, not damage"), "ids_mode_not_read_as_damage");
            _world.SetViewMode("IDs");
        }));
        _steps.Add((3, () =>
        {
            LegendChip chip = _world.Chip;
            GD.Print($"[GUO words] legend: {chip.Title} | {chip.Meaning} | {string.Join(" / ", chip.Labels)}");
            Expect(chip.Visible && chip.Meaning == _world.Modes.ModeNamed("IDs").Meaning, "legend_shows_mode_meaning");
            Expect(chip.Labels.Any() && chip.Labels.All(l => !Jargon.IsMatch(l)), "legend_labels_in_plain_words");
            SettingsShot("editor_legend_ids");
            _world.SetViewMode("Problems");
        }));
        _steps.Add((3, () =>
        {
            Expect(_world.Chip.Labels.All(l => !Jargon.IsMatch(l)), "problems_legend_in_plain_words");
            _world.SetViewMode("Off");
            if (!_data.HasArt(EditorData.LandCount + tree)) return;
            // Keep existing statics: a second click on a taken cell says why nothing happened.
            keepArt = _data.CurrentArt;
            _world.QuickRecipeForSmoke("Single");
            _data.CurrentArt = EditorData.LandCount + tree;
            _world.SetZHueForSmoke();
            Expect(_world.BrushStatus.StartsWith("Set Z / hue: first click"), "set_z_hue_without_a_row_says_how");
            _world.SetKeepStaticsForSmoke(false);
            undo = _world.Editor.UndoCount;
            _world.PlaceForSmoke(new[] { (cx, cy) });
        }));
        _steps.Add((3, () =>
        {
            if (keepArt == 0) return;
            Expect(_world.Editor.UndoCount == undo + 1, "words_tree_placed");
            _world.SetKeepStaticsForSmoke(true);
            _world.PlaceForSmoke(new[] { (cx, cy) });
            GD.Print($"[GUO words] keep existing statics: {_world.BrushStatus}");
            Expect(_world.Editor.UndoCount == undo + 1 && _world.BrushStatus.Contains("already has an item")
                && _world.BrushStatus.Contains("Keep existing statics is on; untick it"), "keep_statics_says_why_nothing_happened");

            // Set Z / hue: choosing a Nearby row names the button; the ground row says to use Flatten.
            _world.ShowNearbyForSmoke(cx, cy);
            string[] rows = _world.StackRowsForSmoke().ToArray();
            GD.Print($"[GUO words] nearby rows: {string.Join(" / ", rows.Select(r => r.Replace('\n', ' ')))}");
            Expect(rows.Length >= 2 && rows.All(r => !r.StartsWith("0x") && r.Contains(" · 0x")), "nearby_rows_name_first");
            _world.ChooseStackRowForSmoke(0);
            Expect(_world.BrushStatus.StartsWith("Chosen: ") && _world.BrushStatus.EndsWith("then press Set Z / hue"), "nearby_choice_names_set_z_hue");
            _world.ChooseStackRowForSmoke(rows.Length - 1);
            Expect(_world.BrushStatus.Contains("Flatten"), "nearby_ground_choice_points_to_flatten");
            _world.ShowInspectorTabForSmoke();

            // Variation and preset rows: the name, then the number.
            _world.SetVariantsForSmoke("0x0E3D:3, 0x0E3E:1");
            string[] variants = _world.VariantRowsForSmoke().ToArray();
            GD.Print($"[GUO words] variation rows: {string.Join(" / ", variants)}");
            Expect(variants.Length == 2 && variants[0].EndsWith(" · 0x0E3D") && variants[1].EndsWith(" · 0x0E3E"), "variation_rows_name_then_id");
            _world.SetVariantsForSmoke("");
            _data.CurrentArt = EditorData.LandCount + tree; // choosing the ground row above made it the current art
            _world.SavePresetForSmoke("ed6 words");
            string preset = _world.PresetRowsForSmoke().FirstOrDefault(r => r.StartsWith("ed6 words"));
            GD.Print($"[GUO words] preset row: {preset}");
            Expect(preset != null && preset.EndsWith(" · 0x0E3D)") && !preset.Contains("(0x"), "preset_rows_name_then_id");
            if (_world.Editor.UndoCount == undo + 1) _world.Editor.Undo();
            _data.CurrentArt = keepArt;
        }));

        // Area to multi: the Multis tab announces the building and its Back to World button returns.
        int taken = 0;
        _steps.Add((1, () =>
        {
            if (MultiEdit == null || MultiEdit.Doc.Parts.Count > 0) return; // never replace work in the Multis tab
            _world.SetArea(1490, 1620, 1530, 1660);
            taken = _world.SaveAreaAsMulti();
        }));
        _steps.Add((5, () =>
        {
            if (taken == 0) return;
            GD.Print($"[GUO words] multi bar: {MultiEdit.FromWorldNotice} | world: {_world.BrushStatus}");
            Expect(MultiEdit.IsVisibleInTree() && MultiEdit.FromWorldNotice.StartsWith("From the World") && MultiEdit.FromWorldNotice.Contains($"{taken} items"), "area_to_multi_announced_in_multis_tab");
            Expect(_world.BrushStatus.Contains("Multis tab") && _world.BrushStatus.Contains("Back to World"), "area_to_multi_world_status_names_the_tab");
            SettingsShot("editor_multi_from_world");
            Button back = MultiEdit.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Text == "Back to World");
            Rect2 screen = MultiEdit.GetViewportRect();
            Expect(back != null && back.IsVisibleInTree() && screen.Encloses(back.GetGlobalRect()), "back_to_world_button_on_screen");
            back?.EmitSignal(BaseButton.SignalName.Pressed);
        }));
        _steps.Add((5, () =>
        {
            if (taken == 0) return;
            Expect(_world.IsVisibleInTree() && !MultiEdit.IsVisibleInTree() && MultiEdit.FromWorldNotice == "", "back_to_world_returns_and_closes_bar");
            _world.SetArea(0, 0, 0, 0);
            MultiEdit.NewMulti();
        }));
    }
}
#endif
