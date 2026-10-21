#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;

/// <summary>
/// Sounds &amp; music (plan §4.6 panel 7). Effects and music play through the
/// game's own audio: <c>Renderer.Sounds.Sound</c> hands out the same
/// <c>UOSound</c> / <c>UOMusic</c> objects the client plays (ADR-0005).
/// </summary>
[Tool]
public partial class SoundPanel : GridPanel
{
    internal const int MaxSound = 0x1000;
    private const int MaxMusic = 0x100;

    private OptionButton _kind;
    private OptionButton _sort;
    private OptionButton _slots;
    private Label _counts, _audition;
    internal Button LoopButton { get; private set; }
    internal Button NextEmptyButton { get; private set; }
    private int? _loopId;
    internal bool Looping => _loopId.HasValue && ProjectPreviewPlaying && _projectStream?.LoopMode == AudioStreamWav.LoopModeEnum.Forward;
    internal int PreviewLoopEnd => _projectStream?.LoopEnd ?? 0;
    internal string SlotCounts => _counts?.Text ?? "";
    internal string AuditionStatus => _audition?.Text ?? "";
    internal Button ExportButton { get; private set; }
    internal bool CanExportEffects => !Music;
    private readonly List<int>[] _ids = new List<int>[2];
    private readonly Dictionary<int, (string Name, int Bytes)> _effects = new();
    private readonly Dictionary<int, (string Name, bool Loop)> _music = new();
    private IO.Audio.Sound _playing;
    private readonly HashSet<IO.Audio.Sound> _played = new();
    private AudioStreamPlayer _projectPlayer;
    private AudioStreamWav _projectStream;
    internal bool ProjectPreviewPlaying => IsInstanceValid(_projectPlayer) && _projectPlayer.Playing;
    internal int ProjectPreviewSamples => _projectStream?.Data.Length / 2 ?? 0;
    private readonly Dictionary<string, SoundDocument> _documents = new();
    private readonly Dictionary<string, string> _documentErrors = new();
    private readonly HashSet<string> _loadedProjects = new();
    internal event Action<SoundDocument> DocumentReopening;
    internal Button ReopenButton { get; private set; }
    internal Button NativeStageButton { get; private set; }
    private string Project => Data.Assets?.Root ?? EditorData.ProjectRoot();
    private string Key(int id) => Project + "|" + id;

    public override string SmokeQuery => Data?.IsLoaded == true && Ids().Any() ? $"0x{Ids().First():X4}" : "0x0000";

    public override bool SmokeNeedsImage => false;

    protected override int IconSize => 0;

    /// <summary>Switches between Effects and Music (F3 opens an id in the right one).</summary>
    public void SelectKind(bool music)
    {
        EnsureUi();
        Stop();
        _kind.Selected = music ? 1 : 0;
        UpdateSoundTools();
    }

    private bool Music => _kind != null && _kind.Selected == 1;

    protected override string Placeholder => "id (0x0055) or name";

    protected override void BuildToolbar(HBoxContainer bar)
    {
        _kind = new OptionButton();
        _kind.AddItem("Effects");
        _kind.AddItem("Music");
        _kind.ItemSelected += _ => { Stop(); UpdateSoundTools(); Refresh(); };
        bar.AddChild(_kind);

        LoopButton = AssetUi.Action("Loop", "Loop selected working sound until Stop; click again to stop", () => { if (Selected is int id) LoopEffect(id); });
        LoopButton.ToggleMode = true; bar.AddChild(LoopButton);
        var stop = AssetUi.Action("Stop", "Stop sound or music playback", Stop);
        bar.AddChild(stop);
        ReopenButton = AssetUi.Action("Reload", "Reopen selected sound and saved working document…", ReopenSound); bar.AddChild(ReopenButton);
        NativeStageButton = AssetUi.Action("Save", "Export selected sound to a verified native stage…", () =>
        { try { OpenNativeStage(); } catch (Exception e) { AssetActions.ReportEdit(e.Message, "Native sound export", this); } }); bar.AddChild(NativeStageButton);
        var slots = new HBoxContainer(); AddChild(slots);
        _slots = new OptionButton { AccessibilityName = "Sound effect slot view", TooltipText = "Active loader occupancy in IDs 0x0000–0x0FFF; aliases count as occupied. Empty is not a native insertion guarantee." };
        foreach (string label in new[] { "Occupied effects", "All effect slots", "Empty effect slots" }) _slots.AddItem(label);
        _slots.ItemSelected += _ => { Stop(); Refresh(); UpdateSoundTools(); }; slots.AddChild(_slots);
        NextEmptyButton = AssetUi.Action("Forward", "Select next empty effect ID after the selection; no wrap. Clears search and uses ID order.", () => NextEmpty()); slots.AddChild(NextEmptyButton);
        _counts = AssetUi.Label(""); slots.AddChild(_counts);
        _audition = AssetUi.Label(""); AddChild(_audition); _audition.Visible = false;
        VisibilityChanged += () => { if (!IsVisibleInTree()) Stop(); };
        Activated += id => Play(id, Music);
    }

    protected override void SearchUiReady()
    {
        var bar = (HBoxContainer)SearchBox.GetParent();
        _sort = new OptionButton { TooltipText = "Sort sound browser", AccessibilityName = "Sound sort order" };
        foreach (string label in new[] { "ID", "Name", "Shortest", "Longest" }) _sort.AddItem(label);
        _sort.ItemSelected += _ => Refresh(); bar.AddChild(_sort);
        ExportButton = AssetUi.Action("Save", "Export sound list or WAV batch…", () => OpenExportDialog()); bar.AddChild(ExportButton);
        UpdateSoundTools();
    }
    private void UpdateSoundTools()
    {
        if (_sort == null) return;
        _sort.SetItemDisabled(2, Music); _sort.SetItemDisabled(3, Music);
        if (Music && _sort.Selected >= 2) _sort.Selected = 0;
        ExportButton.Disabled = Music;
        ExportButton.TooltipText = Music ? "Batch export supports sound effects; music uses its existing playback controls." : "Export sound list or WAV batch…";
        _slots.Visible = NextEmptyButton.Visible = _counts.Visible = !Music;
        ReopenButton.Disabled = Music || Selected is not int selected || !_effects.ContainsKey(selected);
        NativeStageButton.Disabled = Music || Selected is not int native || !_effects.ContainsKey(native);
        LoopButton.Disabled = Music || Selected is not int effect || !_effects.ContainsKey(effect);
        NextEmptyButton.Disabled = Music || NextEmptyId() < 0;
        _counts.Text = $"{_effects.Count:N0} occupied · {MaxSound - _effects.Count:N0} empty · 0x0000–0x0FFF";
    }
    internal void ViewSlots(int view) { EnsureUi(); if (view < 0 || view > 2) throw new ArgumentOutOfRangeException(nameof(view)); _slots.Selected = view; Stop(); Refresh(); UpdateSoundTools(); }
    private int NextEmptyId()
    {
        for (int id = Math.Max(0, (Selected ?? -1) + 1); id < MaxSound; id++) if (!_effects.ContainsKey(id)) return id;
        return -1;
    }
    internal int? NextEmpty()
    {
        if (Music) return null;
        Ids(); int id = NextEmptyId(); if (id < 0) return null;
        Stop(); if (_slots.Selected == 0) _slots.Selected = 1; _sort.Selected = 0;
        return Search($"0x{id:X4}");
    }
    internal void SortSounds(int order) { EnsureUi(); _sort.Selected = order; Refresh(); }
    protected override void SortResults(List<int> results)
    {
        int order = _sort?.Selected ?? 0;
        if (order == 0) return;
        results.Sort((a, b) =>
        {
            int compared = order == 1 ? StringComparer.OrdinalIgnoreCase.Compare(NameOf(a), NameOf(b)) : SamplesOf(a).CompareTo(SamplesOf(b)) * (order == 3 ? -1 : 1);
            return compared == 0 ? a.CompareTo(b) : compared;
        });
    }
    private int SamplesOf(int id) => _documents.TryGetValue(Key(id), out var document) ? document.Samples : _effects.GetValueOrDefault(id).Bytes / 2;
    internal SoundExportDialog OpenExportDialog(int[] selected = null, bool wave = false)
    {
        if (Music) throw new IOException("Choose Effects before exporting a sound list or WAV batch.");
        Ids(); int[] browser = ResultAssetIds(), selection = selected ?? SelectedAssetIds();
        return SoundExportDialog.Open(this, _ids[0].ToArray(), browser.Where(_effects.ContainsKey).ToArray(), selection.Where(_effects.ContainsKey).ToArray(), wave, browser.Length, selection.Length);
    }
    internal SoundExportPlan ExportPlan(IEnumerable<int> ids) => new(ids, id =>
    {
        string key = Key(id);
        if (_documents.TryGetValue(key, out var document)) return document.ExportState();
        if (_documentErrors.TryGetValue(key, out string error)) throw new IOException($"Sound 0x{id:X4}: {error}");
        if (!Data.Files.Sounds.TryGetSound(id, out byte[] pcm, out string rawName)) throw new IOException($"Sound 0x{id:X4} is no longer readable.");
        return new(DisplaySoundName(rawName), pcm, SoundDocument.Fingerprint((pcm, rawName)));
    });
    private static string DisplaySoundName(string name) => (name ?? "").Split('\0')[0].TrimEnd(' ');

    protected override IEnumerable<int> Ids()
    {
        int k = Music ? 1 : 0;
        if (_ids[k] != null)
        {
            if (!Music) LoadProjectNames();
            return !Music && _slots.Selected > 0 ? Enumerable.Range(0, MaxSound) : _ids[k];
        }

        var list = new List<int>();
        if (Music)
        {
            for (int i = 0; i < MaxMusic; i++)
            {
                if (Data.Files.Sounds.TryGetMusicData(i, out string name, out bool loop))
                {
                    _music[i] = (name, loop);
                    list.Add(i);
                }
            }
        }
        else
        {
            for (int i = 0; i < MaxSound; i++)
            {
                if (Data.Files.Sounds.TryGetSound(i, out byte[] data, out string name) && data?.Length > 0)
                {
                    _effects[i] = (DisplaySoundName(name), data.Length);
                    list.Add(i);
                }
            }
        }

        _ids[k] = list;
        if (!Music) LoadProjectNames();
        UpdateSoundTools();
        return !Music && _slots.Selected > 0 ? Enumerable.Range(0, MaxSound) : list;
    }

    protected override bool Include(int id) => Music || _slots.Selected != 2 || !_effects.ContainsKey(id);

    private void LoadProjectNames()
    {
        if (!_loadedProjects.Add(Project)) return;
        string folder = EditorPaths.ProjectFile(Project, Data.ClientData, "metadata", "sounds");
        if (!Directory.Exists(folder)) return;
        foreach (string path in Directory.GetFiles(folder, "0x*.json"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(stem.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int id) && _ids[0].Contains(id)) WorkingDocument(id);
        }
    }
    internal SoundDocument WorkingDocument(int id)
    {
        string key = Key(id);
        if (_documents.TryGetValue(key, out var document)) return document;
        if (_documentErrors.ContainsKey(key)) return null;
        try
        {
            document = SoundDocument.Open(Project, Data.ClientData, id, () =>
            {
                if (!Data.Files.Sounds.TryGetSound(id, out byte[] pcm, out string name)) throw new IOException("Selected sound effect is no longer readable. Reopen the source library.");
                return (pcm, name);
            });
            _documents[key] = document; return document;
        }
        catch (Exception e) { _documentErrors[key] = e.Message; return null; }
    }
    private void ReopenSound()
    {
        if (Music || Selected is not int id) return;
        string key = Key(id);
        var dialog = new ConfirmationDialog { Title = "Reopen sound?", DialogText = $"Discard unsaved name and PCM edits for 0x{id:X4} and reopen its active source plus saved project document?", OkButtonText = "Reopen sound" };
        dialog.Canceled += dialog.QueueFree;
        dialog.Confirmed += () =>
        {
            Stop();
            if (_documents.TryGetValue(key, out var document)) DocumentReopening?.Invoke(document);
            _documents.Remove(key); _documentErrors.Remove(key); dialog.QueueFree(); OnAssetsChanged();
        };
        AddChild(dialog); dialog.PopupCentered();
    }

    private string NameOf(int id) =>
        Music
            ? _music.TryGetValue(id, out var m) ? m.Name : ""
            : _documents.TryGetValue(Key(id), out var document) ? document.CurrentName : _effects.TryGetValue(id, out var e) ? e.Name : "";

    protected override string Caption(int id) => $"0x{id:X4}  {(!Music && !_effects.ContainsKey(id) ? "[empty slot]" : NameOf(id))}";

    protected override bool Matches(int id, string query) =>
        NameOf(id).Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override Inspection Describe(int id)
    {
        if (_loopId.HasValue && (Music || _loopId != id)) Stop();
        UpdateSoundTools();
        if (!Music && !_effects.ContainsKey(id))
        {
            var empty = Inspection.Still("Sounds", $"0x{id:X4}", null, "No playable effect at this ID in the active library.");
            empty.Title = "Empty sound slot"; empty.Origin = "Active loader · read-only slot";
            empty.Fields.Add(("State", "Empty · no PCM"));
            empty.Fields.Add(("Scope", "0x0000–0x0FFF active effect IDs"));
            empty.Fields.Add(("Editing", "Choose an occupied effect to edit its name or import WAV audio."));
            return empty;
        }
        var sb = new StringBuilder();
        if (Music)
        {
            _music.TryGetValue(id, out var m);
            sb.Append($"[b]Music 0x{id:X4}[/b] ({id})\nname {m.Name}   loop {m.Loop}\n");
        }
        else
        {
            _effects.TryGetValue(id, out var e);
            // 16-bit mono at 22050 Hz, what UOSound declares.
            double seconds = e.Bytes / 2.0 / 22050.0;
            sb.Append($"[b]Sound 0x{id:X4}[/b] ({id})\nname {e.Name}\n{e.Bytes} bytes, {seconds:0.00} s at 22050 Hz mono\n");
        }

        bool music = Music;
        SoundDocument document = music ? null : WorkingDocument(id);
        var inspection = Inspection.Still("Sounds", $"0x{id:X4}", null, sb.ToString());
        inspection.Title = music ? "Music" : "Sound effect";
        inspection.Origin = music ? "Active music · read-only" : "Active sound effect · read-only";
        if (document == null) inspection.Fields.Add(("Name", NameOf(id)));
        if (!music && document != null)
        {
            inspection.Sound = document; inspection.SoundChanged = () => { if (IsVisibleInTree() && !Music) OnAssetsChanged(); };
            inspection.Origin = document.HasPcmChange ? "Project PCM · native archive unchanged" : "Source PCM · project working document";
        }
        else if (!music && _documentErrors.TryGetValue(Key(id), out string error)) inspection.Fields.Add(("Document", error));
        if (music) inspection.Fields.Add(("Loop", _music.GetValueOrDefault(id).Loop ? "Yes" : "No"));
        else
        {
            int bytes = document?.Samples * 2 ?? _effects.GetValueOrDefault(id).Bytes;
            inspection.Fields.Add(("Format", "PCM · mono · 16-bit"));
            inspection.Fields.Add(("Sample rate", "22050 Hz"));
            inspection.Fields.Add(("Samples", (bytes / 2).ToString()));
            inspection.Fields.Add(("Duration", $"{bytes / 2.0 / SoundWave.Frequency:0.000} s"));
            if (document?.HasPcmChange == true) inspection.Fields.Add(("Source duration", $"{document.SourceSamples / (double)SoundWave.Frequency:0.000} s"));
        }
        inspection.Actions.Add(new("play-sound", music ? "Play music" : document?.HasPcmChange == true ? "Play project PCM" : "Play sound", "Play", () => Play(id, music)));
        inspection.Actions.Add(new("stop-sound", "Stop playback", "Stop", Stop));
        if (!music)
        {
            inspection.Actions.Add(new("loop-sound", "Loop working sound until Stop; click again to stop", "Loop", () => LoopEffect(id)));
            inspection.Actions.Add(new("export-wav", "Export working WAV… · PCM 22050 Hz mono 16-bit", "Save", () => OpenWavExport(id)));
            if (document != null && document.RecoveryError == null) inspection.Actions.Add(new("import-wav", "Import WAV into working copy…", "Load", () => OpenWavImport(id)));
            if (document?.HasPcmChange == true) inspection.Actions.Add(new("play-source-sound", "Play active source sound", "Play", () => PlaySource(id, false), false));
        }
        return inspection;
    }

    internal EditorFileDialog OpenWavExport(int id) => AssetActions.Pick(EditorFileDialog.FileModeEnum.SaveFile,
        $"sound_0x{id:X4}.wav", path => ExportWav(id, path), new[] { "*.wav ; PCM WAVE audio" }, this, "Export sound WAV");

    internal EditorFileDialog OpenWavImport(int id) => AssetActions.Pick(EditorFileDialog.FileModeEnum.OpenFile, null,
        path => OpenImportReview(id, path), new[] { "*.wav ; PCM WAVE audio" }, this, "Import sound WAV");

    internal SoundImportDialog OpenImportReview(int id, string path)
    {
        SoundDocument document = WorkingDocument(id) ?? throw new IOException("Open a valid sound document before importing WAV.");
        if (document.RecoveryError != null) throw new IOException(document.RecoveryError);
        return SoundImportDialog.Open(this, document, SoundWave.ReadFile(path), path);
    }
    internal void RequireNativeContext(SoundDocument document)
    {
        if (Music || Selected != document.Id || document.Project != EditorPaths.Real(Project)
            || document.Install != EditorPaths.Real(Data.ClientData) || WorkingDocument(document.Id) != document)
            throw new IOException("Sound selection or project changed. Reopen native export for the active sound.");
    }
    internal SoundStageDialog OpenNativeStage()
    {
        if (Music || Selected is not int id || !_effects.ContainsKey(id)) throw new IOException("Choose an occupied sound effect before native export.");
        var document = WorkingDocument(id) ?? throw new IOException("Repair or reopen the sound document before native export.");
        if (!document.CommitDraft()) throw new IOException(document.RecoveryError ?? document.Validation);
        return SoundStageDialog.Open(this, Data, document);
    }

    internal void ExportWav(int id, string path)
    {
        SoundDocument document = WorkingDocument(id);
        if (document != null) { document.ExportWave(path); return; }
        if (_documentErrors.TryGetValue(Key(id), out string error))
            throw new IOException("Working sound document could not be opened. Repair or reopen it before WAV export: " + error);
        if (id < 0 || !Data.Files.Sounds.TryGetSound(id, out byte[] pcm, out _))
            throw new IOException("The selected sound effect could not be read. Reload the sound library and select it again.");
        SoundWave.Export(pcm, path);
    }

    /// <summary>Plays a sound or a piece of music. Returns false if the game's audio would not.</summary>
    public bool Play(int id, bool music)
    {
        if (!music && !_effects.ContainsKey(id)) { Stop(); return false; }
        if (!music && WorkingDocument(id) is { HasPcmChange: true } document) return PlayPcm(document.Pcm());
        return PlaySource(id, music);
    }

    private bool PlaySource(int id, bool music)
    {
        Stop();
        _playing = music ? Data.Sounds.GetMusic(id) : Data.Sounds.GetSound(id);
        if (_playing != null)
        {
            _played.Add(_playing);
        }

        return _playing != null && _playing.Play((uint)Time.GetTicksMsec());
    }

    /// <summary>For the smoke check: plays the selection and reports what happened.</summary>
    public string SmokePlay()
    {
        if (!Selected.HasValue)
        {
            return "nothing selected to play";
        }

        return Play(Selected.Value, Music) ? null : $"sound 0x{Selected.Value:X4} did not start";
    }

    internal bool LoopEffect(int id)
    {
        try
        {
        if (Music || !_effects.ContainsKey(id)) { Stop(); return false; }
        if (Looping && _loopId == id) { Stop(); return false; }
        var document = WorkingDocument(id) ?? throw new IOException(_documentErrors.GetValueOrDefault(Key(id), "Open a valid sound document before looping."));
        if (document.RecoveryError != null) throw new IOException(document.RecoveryError);
        bool playing = PlayPcm(document.Pcm(), loop: true);
        if (playing)
        {
            _loopId = id;
            _audition.Text = $"Looping 0x{id:X4} · {(document.HasPcmChange ? "project" : "source")} PCM · Stop or Escape ends audition"; _audition.Visible = true;
        }
        return playing;
        }
        finally { LoopButton?.SetPressedNoSignal(Looping); }
    }

    internal bool PlayPcm(byte[] pcm, bool loop = false)
    {
        SoundWave.ValidateLength(pcm?.Length ?? 0); Stop();
        if (!IsInstanceValid(_projectPlayer)) { _projectPlayer = new AudioStreamPlayer(); AddChild(_projectPlayer); }
        _projectPlayer.Stream = null; _projectStream?.Dispose();
        _projectStream = new AudioStreamWav { Data = pcm, MixRate = SoundWave.Frequency, Stereo = false,
            Format = AudioStreamWav.FormatEnum.Format16Bits, LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled,
            LoopBegin = 0, LoopEnd = pcm.Length / 2 };
        _projectPlayer.Stream = _projectStream; _projectPlayer.Play(); return _projectPlayer.Playing;
    }

    internal void Stop()
    {
        _playing?.Stop();
        if (IsInstanceValid(_projectPlayer)) _projectPlayer.Stop();
        _loopId = null;
        LoopButton?.SetPressedNoSignal(false);
        if (_audition != null) { _audition.Text = ""; _audition.Visible = false; }
    }

    public override void _Input(InputEvent input)
    {
        // Stop our audition before a text field/editor shortcut consumes Escape.
        // Leave the event available for its ordinary focus/dialog behavior too.
        if (_loopId.HasValue && input is InputEventKey { Pressed: true, Echo: false, Keycode: Godot.Key.Escape })
        { Stop(); }
    }

    public override void OnAssetsChanged() { Stop(); base.OnAssetsChanged(); UpdateSoundTools(); }

    public override void Shutdown()
    {
        Stop();
        if (IsInstanceValid(_projectPlayer)) { _projectPlayer.Stream = null; _projectPlayer.Free(); }
        _projectPlayer = null; _projectStream?.Dispose(); _projectStream = null;
        // Frees the AudioStreamPlayer the game's audio parented under the
        // editor's root.
        foreach (IO.Audio.Sound s in _played)
        {
            s.Stop();
            s.Dispose();
        }

        _played.Clear();
        _playing = null;
    }
}
#endif
