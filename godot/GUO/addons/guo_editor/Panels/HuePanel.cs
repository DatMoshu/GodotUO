#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using GUO.Assets;

/// <summary>
/// Hues (plan §4.6 panel 4): every entry of hues.mul, its 32-colour table and
/// name, and the last art picked in the Art panel drawn plain, fully hued and
/// partially hued.
/// </summary>
/// <remarks>
/// The preview uses <see cref="HuesLoader.GetColor"/> and
/// <see cref="HuesLoader.GetPartialHueColor"/>, upstream's CPU hue path. The
/// game draws world hues on the GPU with the hue shader instead; both index the
/// same table by the source colour's red channel, but a pixel-exact claim for
/// the shader path belongs to /parity-check, not to this preview.
/// </remarks>
[Tool]
public partial class HuePanel : GridPanel
{
    private List<int> _ids;
    private readonly Dictionary<string, HueDocument> _documents = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly HashSet<string> _loadedProjects = new();
    internal event Action<HueDocument> DocumentReopening;
    internal Button ReopenButton { get; private set; }
    private string Project => Data.Assets?.Root ?? EditorData.ProjectRoot();
    private string Key(int id) => Project + "|" + id;

    protected override void BuildToolbar(HBoxContainer bar)
    {
        ReopenButton = AssetUi.Action("Reload", "Reopen selected hue and discard its unsaved draft…", () =>
        {
            if (Selected is not int id) return;
            var dialog = new ConfirmationDialog { Title = "Reopen hue?", DialogText = "Discard this hue's unsaved edits and reopen its saved project copy?", OkButtonText = "Reopen hue" };
            dialog.Canceled += dialog.QueueFree;
            dialog.Confirmed += () => { if (_documents.TryGetValue(Key(id), out var doc)) DocumentReopening?.Invoke(doc); _documents.Remove(Key(id)); _errors.Remove(Key(id)); dialog.QueueFree(); OnAssetsChanged(); };
            AddChild(dialog); dialog.PopupCentered();
        });
        bar.AddChild(ReopenButton);
        bar.AddChild(AssetUi.Action("Save", "Export Fiddler hue names list · UTF-8, zero-based hex IDs…", () => AssetActions.Pick(EditorFileDialog.FileModeEnum.SaveFile, "Hue names list.txt",
            ExportNames, new[] { "*.txt ; Hue names text" }, this, "Export hue names · zero-based IDs")));
    }

    internal HueDocument WorkingDocument(int id)
    {
        string key = Key(id);
        if (_documents.TryGetValue(key, out var document)) return document;
        if (_errors.ContainsKey(key)) return null;
        try
        {
            document = HueDocument.Open(Project, Data.ClientData, id, () => Data.SourceHueRecord(id));
            document.Saved = () => Data.ReapplyAssets(AssetKind.Hue, id);
            _documents[key] = document; return document;
        }
        catch (Exception e) { _errors[key] = e.Message; return null; }
    }

    public override string SmokeQuery => "0x0021";

    protected override int IconSize => 48;

    protected override string Placeholder => "hue (0x0021, 33) or name";

    private ref HuesBlock Block(int hue)
    {
        int h = hue - 1;
        return ref Data.Files.Hues.HuesRange[h >> 3].Entries[h % 8];
    }

    protected override IEnumerable<int> Ids()
    {
        if (_ids == null)
        {
            _ids = new List<int>();
            int count = Math.Min(Data.Files.Hues.HuesCount, Data.Files.Hues.HuesRange.Length * 8);
            for (int hue = 1; hue <= count; hue++)
            {
                _ids.Add(hue);
            }
        }

        if (_loadedProjects.Add(Project) && Data.Assets != null)
            foreach (int id in Data.Assets.Ids(AssetKind.Hue)) if (_ids.Contains(id)) WorkingDocument(id);
        return _ids;
    }

    private unsafe string Name(int hue, bool trim = true)
    {
        if (_documents.TryGetValue(Key(hue), out var document)) return document.Name;
        ref HuesBlock b = ref Block(hue);
        fixed (byte* p = b.Name)
        {
            int n = 0;
            while (n < 20 && p[n] != 0)
            {
                n++;
            }

            string name = Encoding.ASCII.GetString(p, n); return trim ? name.Trim() : name;
        }
    }

    /// <summary>A hue's name from hues.mul, for F3's name search.</summary>
    public string HueName(int hue) => Name(hue);

    protected override string Caption(int id) => $"{id:X4}";

    protected override string Tooltip(int id) => $"0x{id:X4} {Name(id)}";

    protected override bool Matches(int id, string query) =>
        Name(id).Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override Image Icon(int id) => Swatch(id, 1, 4);

    /// <summary>A hue dragged out of the grid drops on any hue field.</summary>
    protected override Godot.Collections.Dictionary DragPayload(int id) => new() { ["guo_hue"] = id };

    /// <summary>The 32 colours of a hue, <paramref name="cell"/> pixels each.</summary>
    private Image Swatch(int hue, int cellW, int height)
    {
        ref HuesBlock b = ref Block(hue);
        Image img = Image.CreateEmpty(32 * cellW, height, false, Image.Format.Rgba8);
        for (int i = 0; i < 32; i++)
        {
            ushort value = _documents.TryGetValue(Key(hue), out var document) ? document.ColorAt(i) : b.ColorTable[i];
            uint c = GUO.Utility.HuesHelper.Color16To32(value);
            img.FillRect(new Rect2I(i * cellW, 0, cellW, height), ToColor(c));
        }

        return img;
    }

    protected override Inspection Describe(int id)
    {
        HueDocument document = WorkingDocument(id);
        ref HuesBlock b = ref Block(id);
        Image swatch = Swatch(id, 6, 24);

        uint art = Data.CurrentArt;
        Image plain = Data.ArtImage(art);
        Image full = null, part = null;
        HuesBlock original = b;
        try
        {
            if (document != null) for (int i = 0; i < 32; i++) b.ColorTable[i] = document.ColorAt(i);
            full = plain != null ? Hued(plain, (ushort)id, partial: false) : null;
            part = plain != null ? Hued(plain, (ushort)id, partial: true) : null;
        }
        finally { b = original; }

        // Swatch on top, then the art plain | hued | partial hue.
        int artW = plain?.GetWidth() ?? 0, artH = plain?.GetHeight() ?? 0;
        int w = Math.Max(swatch.GetWidth(), artW * 3 + 8);
        int h = swatch.GetHeight() + 6 + artH;
        Image sheet = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        sheet.BlitRect(swatch, new Rect2I(0, 0, swatch.GetWidth(), swatch.GetHeight()), Vector2I.Zero);
        if (plain != null)
        {
            int y = swatch.GetHeight() + 6;
            var r = new Rect2I(0, 0, artW, artH);
            sheet.BlitRect(plain, r, new Vector2I(0, y));
            sheet.BlitRect(full, r, new Vector2I(artW + 4, y));
            sheet.BlitRect(part, r, new Vector2I(artW * 2 + 8, y));
        }

        bool isLand = art < EditorData.LandCount;
        uint artId = isLand ? art : art - EditorData.LandCount;
        var sb = new StringBuilder();
        sb.Append($"[b]Hue 0x{id:X4}[/b] ({id})  {Name(id)}\n");
        sb.Append($"table start 0x{b.TableStart:X4}   end 0x{b.TableEnd:X4}\n");
        sb.Append($"preview: {(isLand ? "land" : "static")} 0x{artId:X4} plain | hued | partial hue\n");
        sb.Append("(CPU hue path of HuesLoader; pick art in the Art tab to change it)\n");

        Inspection ins = Inspection.Still("Hues", $"0x{id:X4}", sheet, sb.ToString());
        ins.Origin = document?.IsDirty == true ? "Working project hue · unsaved" : Data.Assets?.Has(AssetKind.Hue, id) == true ? "Saved project hue" : "Install hue · read-only";
        if (document == null) { ins.Text += "\nHue document refused: " + _errors.GetValueOrDefault(Key(id)); return ins; }
        ins.Hue = document; ins.HueChanged = OnAssetsChanged;
        ins.Actions.Add(new("export-hue-stage", "Export verified hue stage…", "Export", () => OpenStageReview(document)));
        ins.Actions.Add(new("export-hue", "Export working palette PNG…", "Save", () => AssetActions.Pick(EditorFileDialog.FileModeEnum.SaveFile, $"hue_0x{id:X4}.png", path =>
        {
            if (!document.CommitDraft()) throw new IOException(document.RecoveryError ?? (document.SourceError.Length > 0 ? document.SourceError : document.Validation));
            using Image strip = Swatch(id, 1, 1); AssetActions.ExportPng(strip, path);
        }, owner: this)));
        ins.Actions.Add(new("import-hue", "Import palette PNG into working document…", "Load", () => AssetActions.Pick(EditorFileDialog.FileModeEnum.OpenFile, null, path =>
        {
            try { ImportPalette(id, path); OnAssetsChanged(); }
            catch (Exception e) { AssetActions.ReportEdit(e.Message, "Import hue palette", this); }
        }, owner: this)));
        ins.Actions.Add(new("import-hue-text", "Import palette text…", "Load", () => AssetActions.Pick(EditorFileDialog.FileModeEnum.OpenFile, null, path =>
        {
            byte[] bytes = HueText.Read(path);
            Callable.From(() => { if (IsInstanceValid(this) && IsInsideTree()) { try { OpenTextReview(document, bytes, path); } catch (Exception e) { AssetActions.ReportEdit(e.Message, "Import palette text", this); } } }).CallDeferred();
        }, new[] { "*.txt ; Hue palette text" }, this, "Choose hue palette text"), false));
        ins.Actions.Add(new("export-hue-text-1252", "Export palette text · Fiddler Windows-1252…", "Save", () => AssetActions.Pick(EditorFileDialog.FileModeEnum.SaveFile, $"hue_0x{id:X4}.txt",
            path => HueText.Export(document, path, HueTextFormat.Fiddler1252), new[] { "*.txt ; Hue palette text" }, this, "Export palette · Windows-1252"), false));
        ins.Actions.Add(new("export-hue-text-utf8", "Export palette text · UTF-8…", "Save", () => AssetActions.Pick(EditorFileDialog.FileModeEnum.SaveFile, $"hue_0x{id:X4}.txt",
            path => HueText.Export(document, path, HueTextFormat.Utf8), new[] { "*.txt ; Hue palette text" }, this, "Export palette · UTF-8"), false));
        return ins;
    }

    internal bool IsCurrentProject(HueDocument document) => string.Equals(document.Project, EditorPaths.Real(Project), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal HueStageDialog OpenStageReview(HueDocument document) => HueStageDialog.Open(this, Data, document);
    internal HueTextDialog OpenTextReview(HueDocument document, byte[] bytes, string filename, HueTextFormat format = HueTextFormat.Fiddler1252)
    {
        if (!IsCurrentProject(document)) throw new IOException("The selected world project changed. Reopen import from the current hue.");
        return HueTextDialog.Open(this, document, bytes, filename, format);
    }
    internal void ExportNames(string path)
    {
        if (Data?.IsLoaded != true) throw new IOException("Wait for the client library to finish loading before exporting hue names.");
        int[] ids = Ids().ToArray(); string project = Project;
        var opened = ids.Where(id => _documents.ContainsKey(Key(id))).Select(id => _documents[Key(id)]).ToArray();
        var tokens = opened.ToDictionary(document => document, document => document.ReviewToken);
        void Guard()
        {
            if (Project != project) throw new IOException("The world project changed. Export the names again.");
            foreach (int id in ids) if (_errors.TryGetValue(Key(id), out string error)) throw new IOException($"Hue 0x{id:X4}: {error}");
            foreach (var row in tokens)
                if (row.Key.Pending || row.Key.RecoveryError != null || row.Key.SourceError.Length > 0 || row.Key.ReviewToken != row.Value)
                    throw new IOException($"Hue 0x{row.Key.Id:X4} has pending fields, changed source or recovery. Commit, undo or reopen it before exporting names.");
        }
        Guard(); byte[] bytes = HueText.EncodeNames(ids.Select(id => (id, Name(id, trim: false)))); HueText.Write(path, Data.ClientData, bytes, Guard);
    }

    internal void ImportPalette(int id, string path)
    {
        using Image image = Image.LoadFromFile(path);
        if (image == null || image.GetWidth() < 32 || image.GetHeight() < 1) throw new IOException("Choose a palette PNG with at least 32 pixels in its first row.");
        ushort[] colors = new ushort[32];
        // Match the existing hue-strip importer: discard the low three bits,
        // ignore alpha, and retain native zero (hues are not static-art pixels).
        for (int i = 0; i < colors.Length; i++) { Color c = image.GetPixel(i, 0); colors[i] = (ushort)(((c.R8 >> 3) << 10) | ((c.G8 >> 3) << 5) | (c.B8 >> 3)); }
        (WorkingDocument(id) ?? throw new IOException("Reopen a valid hue document before importing.")).ReplacePalette(colors);
    }

    private Image Hued(Image src, ushort hue, bool partial)
    {
        HuesLoader hues = Data.Files.Hues;
        Image dst = (Image)src.Duplicate();
        for (int y = 0; y < src.GetHeight(); y++)
        {
            for (int x = 0; x < src.GetWidth(); x++)
            {
                Color c = src.GetPixel(x, y);
                if (c.A == 0)
                {
                    continue;
                }

                // Back to UO's 5-bit channels; the hue table is indexed by red.
                ushort c16 = (ushort)((To5(c.R8) << 10) | (To5(c.G8) << 5) | To5(c.B8));
                uint rgb = partial ? hues.GetPartialHueColor(c16, hue) : hues.GetColor(c16, hue);
                dst.SetPixel(x, y, ToColor(rgb));
            }
        }

        return dst;
    }

    private static int To5(int c8) => (c8 * 31 + 127) / 255;

    private static Color ToColor(uint rgb) =>
        Color.Color8((byte)(rgb & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)((rgb >> 16) & 0xFF), 0xFF);
}
#endif
