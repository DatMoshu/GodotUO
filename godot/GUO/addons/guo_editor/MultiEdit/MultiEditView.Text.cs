#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

public partial class MultiEditView
{
    internal ConfirmationDialog TextReview { get; private set; }
    private int _textReviewRequest;

    private async Task<MultiPart[]> ReadFiddlerTextAsync(byte[] bytes)
    {
        JsonObject raw = await _gen.RequestAsync("import", new JsonObject {
            ["format"] = "txt-fiddler", ["data"] = Convert.ToBase64String(bytes), ["base64"] = true, ["recentre"] = false });
        if (raw["error"] != null) throw new IOException(raw["error"].GetValue<string>());
        JsonArray rows = raw["components"] as JsonArray ?? throw new IOException("TXT reader returned no components.");
        JsonArray flags = raw["text_flags"] as JsonArray ?? throw new IOException("TXT reader returned no flags.");
        if (rows.Count is < 1 or > 65535 || rows.Count != flags.Count) throw new IOException("TXT reader returned an invalid component count.");
        return rows.Select((row, i) => new MultiPart { Id = checked((ushort)(int)row[0]), X = checked((short)(int)row[1]),
            Y = checked((short)(int)row[2]), Z = checked((short)(int)row[3]), Shown = (int)flags[i] != 0, TextFlags = (int)flags[i] }).ToArray();
    }

    private async Task<byte[]> FiddlerTextBytesAsync(MultiPart[] parts)
    {
        var rows = new JsonArray(); var flags = new JsonArray();
        foreach (MultiPart p in parts) { rows.Add((JsonNode)new JsonArray(p.Id, p.X, p.Y, p.Z, p.Shown ? 1 : 0)); flags.Add(p.ExportTextFlags); }
        JsonObject result = await _gen.RequestAsync("export", new JsonObject { ["format"] = "txt-fiddler", ["components"] = rows, ["text_flags"] = flags });
        if (result["error"] != null) throw new IOException(result["error"].GetValue<string>());
        return Encoding.UTF8.GetBytes(result["data"].GetValue<string>());
    }

    /// <summary>GUI exchange always reviews captured records, coordinate policy and representational loss.</summary>
    internal async Task ReviewFiddlerTextAsync(string path, bool export)
    {
        try
        {
            int request = ++_textReviewRequest;
            var lifetime = _writeLifetime.Token;
            TextReview?.QueueFree(); TextReview = null;
            MultiTextSnapshot snapshot = new(_doc);
            MultiTextInput input = export ? null : new(path);
            MultiTextDestination destination = export ? new(path) : null;
            MultiPart[] original = export ? snapshot.Parts : await ReadFiddlerTextAsync(input.Bytes);
            byte[] output = export ? await FiddlerTextBytesAsync(original) : null;
            if (lifetime.IsCancellationRequested || !IsInsideTree() || request != _textReviewRequest) return;
            snapshot.Check(_doc); input?.Check(); destination?.Check();
            var dialog = new ConfirmationDialog { Title = export ? "Review Fiddler TXT export" : "Review Fiddler TXT import",
                OkButtonText = export ? "Export TXT" : "Open working copy", DialogHideOnOk = false };
            TextReview = dialog;
            var body = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0) * EditorInterface.Singleton.GetEditorScale() }; dialog.AddChild(body);
            body.AddChild(AssetUi.Label(Path.GetFileName(path)));
            body.AddChild(AssetUi.Label($"{original.Length:N0} components · hexadecimal IDs · signed decimal offsets/flags · order and duplicates preserved"));
            int opaque = original.Count(p => p.ExportTextFlags is not (0 or 1));
            body.AddChild(AssetUi.Label($"{opaque:N0} components have flags beyond 0/1. Project copies and Fiddler TXT retain them; native staging requires a separate flag translation."));
            CheckBox centre = null, loss = null;
            if (!export)
            {
                centre = new CheckBox { Text = "Centre X/Y around zero", ButtonPressed = false, TooltipText = "Off preserves every source coordinate. On shifts X/Y only; Z and flags stay unchanged.", AccessibilityName = "Centre imported multi coordinates" };
                body.AddChild(centre);
            }
            int hued = original.Count(p => p.Hue != 0);
            if (export && hued > 0)
            {
                loss = new CheckBox { Text = $"Export without hue ({hued:N0} components)", TooltipText = "Fiddler TXT has no hue field. The working document keeps its hues.", AccessibilityName = "Confirm hue loss in TXT export" };
                body.AddChild(loss); dialog.GetOkButton().Disabled = true;
                loss.Toggled += value => dialog.GetOkButton().Disabled = !value;
            }
            var bounds = AssetUi.Label(""); body.AddChild(bounds);
            var rows = new Tree { Columns = 5, HideRoot = true, ColumnTitlesVisible = true, AccessibilityName = "Reviewed multi components",
                CustomMinimumSize = new Vector2(0, 200) * EditorInterface.Singleton.GetEditorScale() }; body.AddChild(rows);
            foreach (var c in new[] { (0,"Item ID"), (1,"X"), (2,"Y"), (3,"Z"), (4,"Flags") }) rows.SetColumnTitle(c.Item1,c.Item2);
            var status = AssetUi.Label(""); status.Visible = false; body.AddChild(status);
            void Show()
            {
                try
                {
                    MultiPart[] parts = centre?.ButtonPressed == true ? MultiTextSnapshot.Centre(original) : original;
                    bounds.Text = $"X {parts.Min(p => p.X)}…{parts.Max(p => p.X)} · Y {parts.Min(p => p.Y)}…{parts.Max(p => p.Y)} · Z {parts.Min(p => p.Z)}…{parts.Max(p => p.Z)}";
                    rows.Clear(); TreeItem root = rows.CreateItem();
                    foreach (MultiPart p in parts.Take(200)) { TreeItem row=rows.CreateItem(root); row.SetText(0,$"0x{p.Id:X}"); row.SetText(1,p.X.ToString()); row.SetText(2,p.Y.ToString()); row.SetText(3,p.Z.ToString()); row.SetText(4,p.ExportTextFlags.ToString()); }
                    status.Visible = false; dialog.GetOkButton().Disabled = loss != null && !loss.ButtonPressed;
                }
                catch (Exception e) { status.Text = "Cannot centre: " + e.Message; status.Visible = true; dialog.GetOkButton().Disabled = true; }
            }
            if (centre != null) centre.Toggled += _ => Show();
            if (original.Length > 200) body.AddChild(AssetUi.Label("First 200 components shown; the operation includes every reviewed component."));
            body.AddChild(AssetUi.Label(export ? "Windows-compatible TXT · no hue or level fields · working document unchanged" : "Opens a new working copy · installed data unchanged"));
            dialog.Confirmed += () =>
            {
                try
                {
                    if (lifetime.IsCancellationRequested) throw new IOException("The Multi Editor moved or closed. Close this review and open it again.");
                    snapshot.Check(_doc); input?.Check();
                    if (export) { if (loss != null && !loss.ButtonPressed) throw new IOException("Confirm hue loss before exporting."); destination.Write(output); }
                    else OpenParts(MultiStore.SafeName(Path.GetFileNameWithoutExtension(path)), centre.ButtonPressed ? MultiTextSnapshot.Centre(original) : original);
                    _status.Text = export ? $"Exported {original.Length:N0} components as Fiddler TXT" : $"Opened {original.Length:N0} components from Fiddler TXT";
                    dialog.Hide(); dialog.QueueFree(); TextReview = null;
                }
                catch (Exception e) { status.Text=e.Message; status.Visible=true; }
            };
            dialog.Canceled += () => { dialog.QueueFree(); if (TextReview == dialog) TextReview=null; };
            AddChild(dialog); Show(); dialog.PopupCenteredClamped();
        }
        catch (Exception e) { if (!_writeLifetime.IsCancellationRequested) { _status.Text=e.Message; AssetActions.ReportEdit(e.Message,"Fiddler TXT",this); } }
    }
}
#endif
