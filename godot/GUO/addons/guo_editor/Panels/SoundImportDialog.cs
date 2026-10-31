#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using Godot;

/// <summary>Review a captured WAV candidate; confirmation changes only the working document.</summary>
[Tool]
public partial class SoundImportDialog : ConfirmationDialog
{
    internal static SoundImportDialog Open(SoundPanel panel, SoundDocument document, byte[] wave, string filename)
    {
        byte[] captured = (byte[])wave.Clone(), pcm = SoundWave.Decode(captured);
        string priorName = document.CurrentName, priorPcm = document.PcmHash, priorDraft = document.DraftName;
        var dialog = new SoundImportDialog
        {
            Title = $"Import WAV · sound 0x{document.Id:X4}", OkButtonText = "Import into working copy",
            DialogHideOnOk = false, Size = (Vector2I)(new Vector2(660, 370) * EditorInterface.Singleton.GetEditorScale()),
        };
        var body = new VBoxContainer(); dialog.AddChild(body);
        body.AddChild(AssetUi.Label(Path.GetFileName(filename)));
        body.AddChild(AssetUi.Label("PCM · mono · 16-bit · 22050 Hz. Samples are copied without resampling or normalization."));
        var table = new GridContainer { Columns = 3 }; body.AddChild(table);
        foreach (string value in new[] { "", "Current working copy", "Incoming WAV", "Samples", document.Samples.ToString(), (pcm.Length / 2).ToString(),
            "Duration", $"{document.Samples / (double)SoundWave.Frequency:0.000} s", $"{pcm.Length / 2.0 / SoundWave.Frequency:0.000} s" }) table.AddChild(AssetUi.Label(value));
        var playback = new HBoxContainer(); body.AddChild(playback);
        var current = AssetUi.Action("Play", "Play current working PCM", () => panel.PlayPcm(document.Pcm())); current.Text = "Play current"; playback.AddChild(current);
        var incoming = AssetUi.Action("Play", "Play incoming WAV", () => panel.PlayPcm(pcm)); incoming.Text = "Play incoming"; playback.AddChild(incoming);
        playback.AddChild(AssetUi.Action("Stop", "Stop preview playback", panel.Stop));
        body.AddChild(AssetUi.Label("The working name is retained. Import is undoable; Save copy persists the project document. The installed sound archive is unchanged."));
        var status = AssetUi.Label(""); status.Visible = false; body.AddChild(status);
        dialog.Confirmed += () =>
        {
            try
            {
                if (document.CurrentName != priorName || document.PcmHash != priorPcm || document.DraftName != priorDraft)
                    throw new IOException("The working copy changed while this review was open. Close it and choose the WAV again.");
                document.ImportWave(captured); panel.Stop(); panel.OnAssetsChanged(); dialog.QueueFree();
            }
            catch (Exception e) { status.Text = e.Message; status.Visible = true; }
        };
        dialog.Canceled += () => { panel.Stop(); dialog.QueueFree(); };
        panel.AddChild(dialog); dialog.PopupCentered(); return dialog;
    }
}
#endif
