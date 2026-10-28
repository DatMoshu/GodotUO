#if TOOLS
namespace GUO.Editor;

using System;
using System.Linq;
using Godot;

public partial class EditorSmoke
{
    private void SoundAuditionCheck(string name, bool ok)
    { _assetReport["sound_audition_" + name] = ok; if (!ok) AssetFail("Sound audition: " + name); }

    private void CheckSoundAudition()
    {
        var panel = _assets.Panel<SoundPanel>(); _assets.ShowPanel(panel); panel.SelectKind(false); panel.ViewSlots(0); panel.SortSounds(0); panel.Search("");
        int[] occupied = panel.ResultAssetIds(); int id = occupied[0];
        panel.ViewSlots(1); panel.Search("");
        SoundAuditionCheck("all_slots_include_full_existing_browser_range", panel.ResultAssetIds().SequenceEqual(Enumerable.Range(0, SoundPanel.MaxSound)));
        panel.ViewSlots(2); panel.Search(""); int[] empty = panel.ResultAssetIds();
        SoundAuditionCheck("empty_occupied_partition", empty.Length + occupied.Length == SoundPanel.MaxSound && !empty.Intersect(occupied).Any()
            && empty.Concat(occupied).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, SoundPanel.MaxSound)));
        if (empty.Length == 0) throw new InvalidOperationException("Audition smoke needs one empty effect slot in the active library.");
        int free = empty[0]; string counts = panel.SlotCounts;
        SoundAuditionCheck("empty_slot_inspector_is_read_only", panel.Search($"0x{free:X4}") == free && _inspector.Current.Title == "Empty sound slot"
            && _inspector.Current.Sound == null && _inspector.Current.Actions.Count == 0 && !_inspector.Sound.Visible && _inspector.Current.Fields.Any(f => f.Item2 == "Empty · no PCM"));
        SoundAuditionCheck("empty_slot_cannot_play_or_loop", !panel.Play(free, false) && !panel.LoopEffect(free) && !panel.ProjectPreviewPlaying && panel.LoopButton.Disabled && panel.ReopenButton.Disabled);
        SoundAuditionCheck("counts_are_library_wide_not_search_totals", panel.SlotCounts == counts && counts.Contains($"{occupied.Length:N0} occupied") && counts.Contains($"{empty.Length:N0} empty"));
        var review = panel.OpenExportDialog(new[] { id, free }, true);
        SoundAuditionCheck("mixed_selection_exports_occupied_with_explicit_counts", review.Plan.Rows.Count == 1 && review.Plan.Rows[0].Id == id
            && GodotUi.Walk(review).OfType<OptionButton>().Any(b => Enumerable.Range(0, b.ItemCount).Any(i => b.GetItemText(i) == "Selected occupied (1/2)")));
        review.Free(); review = panel.OpenExportDialog(new[] { free }, true);
        SoundAuditionCheck("empty_only_export_has_no_fallback_scope", review.Plan == null && review.GetOkButton().Disabled);
        review.Choose(1, SoundExportFormat.Utf8Csv); SoundAuditionCheck("empty_browser_export_has_no_fallback_scope", review.Plan == null && review.GetOkButton().Disabled); review.Free();
        panel.ViewSlots(1); panel.Search($"0x{id:X4}");
        int expected = empty.FirstOrDefault(e => e > id, -1); int? next = panel.NextEmpty();
        SoundAuditionCheck("next_empty_ascending_from_selection", next == (expected >= 0 ? expected : null) && (next == null || _inspector.Current.Title == "Empty sound slot"));
        panel.Search($"0x{SoundPanel.MaxSound - 1:X4}");
        SoundAuditionCheck("next_empty_does_not_wrap", panel.NextEmpty() == null && panel.NextEmptyButton.Disabled);
        panel.SortSounds(1); SoundAuditionCheck("sorted_empty_id_jump", panel.Search($"0x{free:X4}") == free); panel.SortSounds(0);
        panel.ViewSlots(0); panel.Search(id.ToString());
        var document = panel.WorkingDocument(id); byte[] original = document.Pcm();
        SoundAuditionCheck("loop_source_pcm_exact", panel.LoopEffect(id) && panel.Looping && panel.LoopButton.ButtonPressed && panel.ProjectPreviewSamples == original.Length / 2
            && panel.PreviewLoopEnd == original.Length / 2 && ((AudioStreamWav)GodotUi.Walk(panel).OfType<AudioStreamPlayer>().Single().Stream).Data.AsSpan().SequenceEqual(original)
            && panel.AuditionStatus.Contains("source PCM"));
        SoundAuditionCheck("second_loop_click_stops", !panel.LoopEffect(id) && !panel.Looping && !panel.ProjectPreviewPlaying && !panel.LoopButton.ButtonPressed);
        document.ImportWave(SoundWave.Encode(new byte[4410])); panel.OnAssetsChanged();
        SoundAuditionCheck("loop_project_pcm_exact_bounds", panel.LoopEffect(id) && panel.Looping && panel.ProjectPreviewSamples == 2205 && panel.PreviewLoopEnd == 2205 && panel.AuditionStatus.Contains("project PCM"));
        panel.Search(occupied[1].ToString()); SoundAuditionCheck("selection_change_stops_loop", !panel.Looping && !panel.ProjectPreviewPlaying && panel.AuditionStatus.Length == 0);
        panel.Search(id.ToString()); panel.LoopEffect(id); panel.OnAssetsChanged(); SoundAuditionCheck("working_document_change_stops_loop", !panel.Looping && !panel.ProjectPreviewPlaying);
        panel.LoopEffect(id); panel._Input(new InputEventKey { Pressed = true, Keycode = Godot.Key.Escape });
        SoundAuditionCheck("escape_stops_loop", !panel.Looping && !panel.ProjectPreviewPlaying);
        panel.LoopEffect(id); panel.GetViewport().PushInput(new InputEventKey { Pressed = true, Keycode = Godot.Key.Escape });
        SoundAuditionCheck("viewport_escape_dispatch_stops_loop", !panel.Looping && !panel.ProjectPreviewPlaying);
        panel.LoopEffect(id); panel.SelectKind(true); SoundAuditionCheck("music_has_no_effect_loop", !panel.Looping && !panel.ProjectPreviewPlaying && panel.LoopButton.Disabled && !panel.NextEmptyButton.Visible);
        panel.SelectKind(false); panel.Search(id.ToString()); panel.LoopEffect(id); _assets.ShowPanel(_assets.Panel<ArtPanel>());
        SoundAuditionCheck("leaving_sound_family_stops_loop", !panel.Looping && !panel.ProjectPreviewPlaying);
        _assets.ShowPanel(panel); panel.Search(id.ToString()); document.Revert(); panel.OnAssetsChanged();
        panel.PlayPcm(new byte[4410]);
        SoundAuditionCheck("ordinary_project_preview_is_not_looped", !panel.Looping && ((AudioStreamWav)GodotUi.Walk(panel).OfType<AudioStreamPlayer>().Single().Stream).LoopMode == AudioStreamWav.LoopModeEnum.Disabled); panel.Stop();
        SoundAuditionCheck("loop_icon_exists_and_actions_are_named", EditorInterface.Singleton.GetEditorTheme().HasIcon("Loop", "EditorIcons") && panel.LoopButton.Text.Length == 0
            && panel.LoopButton.AccessibilityName.Length > 0 && panel.LoopButton.TooltipText.Length > 0 && _inspector.Current.Actions.Any(a => a.Id == "loop-sound"));
        var probe = new SoundPanel(); probe.Attach(_data); AddChild(probe); probe.SelectKind(false); probe.Search(id.ToString());
        var invalid = probe.WorkingDocument(id); invalid.RecoveryError = "Invalid recovered sound fixture"; probe.LoopButton.SetPressedNoSignal(true);
        SoundAuditionCheck("refused_loop_resets_pressed_state", SoundDocumentRefused(() => probe.LoopEffect(id)) && !probe.LoopButton.ButtonPressed && !probe.Looping);
        invalid.RecoveryError = null; probe.LoopEffect(id);
        var player = GodotUi.Walk(probe).OfType<AudioStreamPlayer>().Single(); probe.Shutdown();
        SoundAuditionCheck("shutdown_frees_active_loop_player", !probe.Looping && !GodotObject.IsInstanceValid(player)); probe.Free();
        panel.ViewSlots(0); panel.Search(id.ToString());
        _assetReport["sound_audition_occupied"] = occupied.Length; _assetReport["sound_audition_empty"] = empty.Length; _assetReport["sound_audition_empty_id"] = free;
    }
}
#endif
