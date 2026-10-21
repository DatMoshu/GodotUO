#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
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
    internal static bool IsReviewedText(string format) => format is "txt-fiddler" or "uoa-fiddler" or "wsc-fiddler";

    private async Task<MultiPart[]> ReadFiddlerTextAsync(byte[] bytes, string format = "txt-fiddler")
    {
        JsonObject raw = await _gen.RequestAsync("import", new JsonObject {
            ["format"] = format, ["data"] = Convert.ToBase64String(bytes), ["base64"] = true, ["recentre"] = false });
        if (raw["error"] != null) throw new IOException(raw["error"].GetValue<string>());
        JsonArray rows = raw["components"] as JsonArray ?? throw new IOException("TXT reader returned no components.");
        JsonArray flags = raw["text_flags"] as JsonArray ?? throw new IOException("TXT reader returned no flags.");
        if (rows.Count is < 1 or > 65535 || rows.Count != flags.Count) throw new IOException("TXT reader returned an invalid component count.");
        return rows.Select((row, i) => new MultiPart { Id = checked((ushort)(int)row[0]), X = checked((short)(int)row[1]),
            Y = checked((short)(int)row[2]), Z = checked((short)(int)row[3]), Shown = (int)flags[i] != 0, TextFlags = (int)flags[i] }).ToArray();
    }

    private async Task<byte[]> FiddlerTextBytesAsync(MultiPart[] parts, string format = "txt-fiddler")
    {
        var rows = new JsonArray(); var flags = new JsonArray();
        foreach (MultiPart p in parts) { rows.Add((JsonNode)new JsonArray(p.Id, p.X, p.Y, p.Z, p.Shown ? 1 : 0)); flags.Add(p.ExportTextFlags); }
        JsonObject result = await _gen.RequestAsync("export", new JsonObject { ["format"] = format, ["components"] = rows, ["text_flags"] = flags });
        if (result["error"] != null) throw new IOException(result["error"].GetValue<string>());
        return Encoding.UTF8.GetBytes(result["data"].GetValue<string>());
    }

    /// <summary>GUI exchange always reviews captured records, coordinate policy and representational loss.</summary>
    internal async Task ReviewFiddlerTextAsync(string path, bool export, string format = "txt-fiddler")
    {
        try
        {
            int request = ++_textReviewRequest;
            if (!IsReviewedText(format)) throw new IOException("Choose a supported reviewed text format.");
            bool uoa = format == "uoa-fiddler", wsc = format == "wsc-fiddler";
            string extension = wsc ? "WSC" : uoa ? "UOA" : "TXT";
            string label = $"Fiddler {extension}";
            var lifetime = _writeLifetime.Token;
            TextReview?.QueueFree(); TextReview = null;
            MultiTextSnapshot snapshot = new(_doc);
            MultiTextInput input = export ? null : new(path);
            MultiTextDestination destination = export ? new(path) : null;
            MultiPart[] original = export ? snapshot.Parts : await ReadFiddlerTextAsync(input.Bytes, format);
            byte[] output = export ? await FiddlerTextBytesAsync(original, format) : null;
            if (lifetime.IsCancellationRequested || !IsInsideTree() || request != _textReviewRequest) return;
            snapshot.Check(_doc); input?.Check(); destination?.Check();
            var dialog = new ConfirmationDialog { Title = $"Review {label} {(export ? "export" : "import")}",
                OkButtonText = export ? $"Export {extension}" : "Open working copy", DialogHideOnOk = false };
            TextReview = dialog;
            var body = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0) * EditorInterface.Singleton.GetEditorScale() }; dialog.AddChild(body);
            body.AddChild(AssetUi.Label(Path.GetFileName(path)));
            body.AddChild(AssetUi.Label($"{original.Length:N0} components · {(uoa || wsc ? "decimal" : "hexadecimal")} IDs · signed decimal offsets · order and duplicates preserved"));
            if (uoa) body.AddChild(AssetUi.Label("Fiddler ignores the first three input header values. Export writes version 6, template 1, item version −1 and the current component count."));
            int opaque = original.Count(p => p.ExportTextFlags is not (0 or 1));
            body.AddChild(AssetUi.Label(wsc
                ? (export ? "WSC writes Color 0 and no flags. Fiddler may shift positive coordinates when reopening it. ID 65535 is refused to avoid its sentinel defect."
                    : "Color is ignored. The table shows Fiddler's imported offsets after its world-coordinate heuristic. ID 65535 is refused to avoid its sentinel defect.")
                : $"{opaque:N0} components have flags beyond 0/1. Project copies and {label} retain them; native staging requires a separate flag translation."));
            CheckBox centre = null, loss = null;
            LineEdit dynamic = null;
            if (!export)
            {
                if (wsc)
                {
                    body.AddChild(AssetUi.Label("Dynamic item IDs · decimal, separated by commas · empty means all visible"));
                    dynamic = new LineEdit { Name = "WscDynamicIds", PlaceholderText = "e.g. 1650, 42", AccessibilityName = "WSC dynamic item IDs", MaxLength = 393210,
                        TooltipText = "These IDs open hidden, matching Fiddler's optional dynamic-ID set. WSC stores no such set; the project document retains the resulting visibility." };
                    body.AddChild(dynamic);
                }
                centre = new CheckBox { Text = "Centre X/Y around zero", ButtonPressed = false, TooltipText = wsc ? "Off keeps Fiddler's imported offsets after its world-coordinate heuristic. On additionally centers X/Y only." : "Off preserves every source coordinate. On shifts X/Y only; Z and flags stay unchanged.", AccessibilityName = "Centre imported multi coordinates" };
                body.AddChild(centre);
            }
            int hued = original.Count(p => p.Hue != 0);
            int flagLoss = wsc ? original.Count(p => p.ExportTextFlags != 1) : 0;
            if (export && (hued > 0 || flagLoss > 0))
            {
                loss = new CheckBox { Text = wsc ? $"Export with Color 0 and no flags ({hued:N0} hued · {flagLoss:N0} changed flags)" : $"Export without hue ({hued:N0} components)",
                    TooltipText = $"{label} cannot preserve these fields. The working document keeps them.", AccessibilityName = "Confirm field loss in text export" };
                body.AddChild(loss); dialog.GetOkButton().Disabled = true;
                loss.Toggled += value => dialog.GetOkButton().Disabled = !value;
            }
            var bounds = AssetUi.Label(""); body.AddChild(bounds);
            var rows = new Tree { Columns = 5, HideRoot = true, ColumnTitlesVisible = true, AccessibilityName = "Reviewed multi components",
                CustomMinimumSize = new Vector2(0, wsc ? 160 : 200) * EditorInterface.Singleton.GetEditorScale() }; body.AddChild(rows);
            foreach (var c in new[] { (0,"Item ID"), (1,"X"), (2,"Y"), (3,"Z"), (4,"Flags") }) rows.SetColumnTitle(c.Item1,c.Item2);
            var status = AssetUi.Label(""); status.Visible = false; body.AddChild(status);
            MultiPart[] SelectedParts()
            {
                MultiPart[] parts = centre?.ButtonPressed == true ? MultiTextSnapshot.Centre(original) : (MultiPart[])original.Clone();
                if (dynamic != null)
                {
                    var ids = new HashSet<ushort>();
                    foreach (string word in dynamic.Text.Split(new[]{',',' ','\t'},StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!ushort.TryParse(word,NumberStyles.None,CultureInfo.InvariantCulture,out ushort id))
                            throw new IOException("Dynamic item IDs must be decimal numbers from 0 to 65535.");
                        ids.Add(id);
                    }
                    for(int i=0;i<parts.Length;i++) { parts[i].Shown=!ids.Contains(parts[i].Id); parts[i].TextFlags=parts[i].Shown ? 1 : 0; }
                }
                return parts;
            }
            void Show()
            {
                try
                {
                    MultiPart[] parts = SelectedParts();
                    bounds.Text = $"X {parts.Min(p => p.X)}…{parts.Max(p => p.X)} · Y {parts.Min(p => p.Y)}…{parts.Max(p => p.Y)} · Z {parts.Min(p => p.Z)}…{parts.Max(p => p.Z)}";
                    rows.Clear(); TreeItem root = rows.CreateItem();
                    foreach (MultiPart p in parts.Take(200)) { TreeItem row=rows.CreateItem(root); row.SetText(0,uoa || wsc ? p.Id.ToString() : $"0x{p.Id:X}"); row.SetText(1,p.X.ToString()); row.SetText(2,p.Y.ToString()); row.SetText(3,p.Z.ToString()); row.SetText(4,p.ExportTextFlags.ToString()); }
                    status.Visible = false; dialog.GetOkButton().Disabled = loss != null && !loss.ButtonPressed;
                }
                catch (Exception e) { status.Text = e.Message; status.Visible = true; dialog.GetOkButton().Disabled = true; }
            }
            if (centre != null) centre.Toggled += _ => Show();
            if (dynamic != null) dynamic.TextChanged += _ => Show();
            if (original.Length > 200) body.AddChild(AssetUi.Label("First 200 components shown; the operation includes every reviewed component."));
            body.AddChild(AssetUi.Label(export ? $"Windows-compatible {extension} · {(wsc ? "Color 0, no flags or level" : "no hue or level fields")} · working document unchanged" : "Opens a new working copy · installed data unchanged"));
            dialog.Confirmed += () =>
            {
                try
                {
                    if (lifetime.IsCancellationRequested) throw new IOException("The Multi Editor moved or closed. Close this review and open it again.");
                    snapshot.Check(_doc); input?.Check();
                    if (export) { if (loss != null && !loss.ButtonPressed) throw new IOException("Confirm field loss before exporting."); destination.Write(output); }
                    else OpenParts(MultiStore.SafeName(Path.GetFileNameWithoutExtension(path)), SelectedParts());
                    _status.Text = export ? $"Exported {original.Length:N0} components as {label}" : $"Opened {original.Length:N0} components from {label}";
                    dialog.Hide(); dialog.QueueFree(); TextReview = null;
                }
                catch (Exception e) { status.Text=e.Message; status.Visible=true; }
            };
            dialog.Canceled += () => { dialog.QueueFree(); if (TextReview == dialog) TextReview=null; };
            AddChild(dialog); Show(); dialog.PopupCenteredClamped();
        }
        catch (Exception e) { if (!_writeLifetime.IsCancellationRequested) { _status.Text=e.Message; AssetActions.ReportEdit(e.Message,"Fiddler text",this); } }
    }
}
#endif
