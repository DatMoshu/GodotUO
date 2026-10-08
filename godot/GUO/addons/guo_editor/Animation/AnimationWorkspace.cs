#if TOOLS
namespace GUO.Editor;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;

/// <summary>Working-copy animation editor and classic VD interchange, outside the client install.</summary>
[Tool]
public partial class AnimationWorkspace : Window
{
    private AnimationDocument _doc;
    private int _body, _slot, _frame;
    private SpinBox _action, _direction, _cx, _cy, _fps;
    private TextureRect _preview;
    private Label _status;
    private HSlider _scrub;
    private bool _playing, _refreshing;
    private double _clock;
    private readonly Stack<byte[]> _undo = new();
    private readonly Stack<byte[]> _redo = new();
    private AnimationRecord Record => _doc.Records[_slot];
    /// <summary>Writes one clip into the editor's animation overlay; returns why not, or null. Set by the panel.</summary>
    public Func<OverlayAnimationClip, string> ApplyOverlay { get; set; }
    public void ImportSpritesheet() => Attempt(ImportSheet);
    public void ImportImageSequence() => Attempt(ImportSequence);

    public AnimationDocument Document => _doc;
    public static AnimationWorkspace Open(AnimationDocument doc, int body, int action, int direction, Node owner = null)
    {
        var view = new AnimationWorkspace { _doc = doc, _body = body, _slot = action * 5 + direction,
            Title = $"Animation 0x{body:X4} — working copy", Size = new Vector2I(1200, 850) };
        (owner ?? EditorInterface.Singleton.GetBaseControl()).AddChild(view);
        view.PopupCentered();
        return view;
    }
    public override void _Ready()
    {
        CloseRequested += () =>
        {
            if (_undo.Count == 0) { QueueFree(); return; }
            var dialog = new ConfirmationDialog { Title = "Close animation working copy", DialogText = "Export the VD to retain your changes. Close this working copy?" };
            AddChild(dialog); dialog.Confirmed += () => QueueFree(); dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
        };
        var scroll = new ScrollContainer(); scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; scroll.AddChild(root);
        var toolbar = new HFlowContainer(); root.AddChild(toolbar);
        _action = Number(toolbar, "Action", 0, AnimationDocument.Actions(_doc.Profile) - 1, _slot / 5);
        _direction = Number(toolbar, "Stored direction", 0, 4, _slot % 5);
        _action.ValueChanged += _ => SelectSlot(); _direction.ValueChanged += _ => SelectSlot();
        _cx = Number(toolbar, "Center X", short.MinValue, short.MaxValue, 0);
        _cy = Number(toolbar, "Center Y", short.MinValue, short.MaxValue, 0);
        _cx.ValueChanged += _ => ChangeCenter(); _cy.ValueChanged += _ => ChangeCenter();
        _fps = Number(toolbar, "Preview FPS", 1, 60, 8);
        Button(toolbar, "Play / pause", () => _playing = !_playing);
        Button(toolbar, "Undo", () => History(_undo, _redo)); Button(toolbar, "Redo", () => History(_redo, _undo));
        _preview = new TextureRect { TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 320) };
        root.AddChild(_preview);
        _scrub = new HSlider { Step = 1 }; _scrub.ValueChanged += value => { _frame = (int)value; RefreshFrame(); }; root.AddChild(_scrub);
        var commands = new HFlowContainer(); root.AddChild(commands);
        Button(commands, "Apply to editor overlay", ApplyToOverlay);
        Button(commands, "Import image sequence...", ImportSequence);
        Button(commands, "Import spritesheet...", ImportSheet);
        Button(commands, "Export frames...", () => Pick(EditorFileDialog.FileModeEnum.OpenDir, "", path => ExportFrames(path)));
        Button(commands, "Export spritesheet...", () => Pick(EditorFileDialog.FileModeEnum.SaveFile, "*.png ; PNG sheet", path => ExportSheet(path)));
        Button(commands, "Import classic VD...", () => Pick(EditorFileDialog.FileModeEnum.OpenFile, "*.vd ; Classic VD", path =>
        {
            var next = ClassicVd.Read(File.ReadAllBytes(path)); Snapshot(); _doc = next; _slot = 0;
            _action.MaxValue = AnimationDocument.Actions(next.Profile) - 1; _action.SetValueNoSignal(0); _direction.SetValueNoSignal(0); RefreshRecord();
        }));
        Button(commands, "Export classic VD...", () => Pick(EditorFileDialog.FileModeEnum.SaveFile, "*.vd ; Classic VD", path => WriteVd(path)));
        Button(commands, "Export palette...", () => Pick(EditorFileDialog.FileModeEnum.SaveFile, "*.json ; VD palette words", path =>
        {
            if (Record == null) throw new InvalidDataException("This slot is missing.");
            EnsureSafe(path); var words = new JsonArray(); foreach (ushort word in Record.Palette) words.Add((int)word);
            File.WriteAllText(path, new JsonObject { ["format"] = 1, ["encoding"] = "VD on-disk RGB555 words", ["colors"] = words }.ToJsonString());
        }));
        Button(commands, "Import palette...", () => Pick(EditorFileDialog.FileModeEnum.OpenFile, "*.json ; VD palette words", path =>
        {
            if (Record == null) throw new InvalidDataException("This slot is missing.");
            var json = JsonNode.Parse(File.ReadAllText(path));
            if (json["colors"] is not JsonArray colors || colors.Count != 256) throw new InvalidDataException("Palette must contain exactly 256 on-disk RGB555 words.");
            ushort[] palette = colors.Select(word => checked((ushort)(int)word)).ToArray();
            Snapshot(); Record.Palette = palette; Record.Edited = true; RefreshFrame();
        }));
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; root.AddChild(_status);
        RefreshRecord();
    }
    private static SpinBox Number(Control parent, string title, double min, double max, double value)
    {
        var group = new HBoxContainer(); parent.AddChild(group); group.AddChild(new Label { Text = title });
        var spin = new SpinBox { MinValue = min, MaxValue = max, Value = value }; group.AddChild(spin); return spin;
    }
    private void Button(Control parent, string title, Action action)
    {
        var button = new Godot.Button { Text = title }; button.Pressed += () => Attempt(action); parent.AddChild(button);
    }
    private void Attempt(Action action) { try { action(); } catch (Exception ex) { AssetActions.ReportEdit(ex.Message); } }
    private void Snapshot()
    {
        _undo.Push(ClassicVd.Write(_doc)); _redo.Clear();
        if (_undo.Count > 32)
        {
            byte[][] keep = _undo.Take(32).Reverse().ToArray(); _undo.Clear();
            foreach (byte[] state in keep) _undo.Push(state);
        }
    }
    private void History(Stack<byte[]> from, Stack<byte[]> to)
    {
        if (from.Count == 0) return; to.Push(ClassicVd.Write(_doc)); _doc = ClassicVd.Read(from.Pop());
        _action.MaxValue = AnimationDocument.Actions(_doc.Profile) - 1; _slot = Math.Min(_slot, _doc.Records.Length - 1);
        _action.SetValueNoSignal(_slot / 5); _direction.SetValueNoSignal(_slot % 5); RefreshRecord();
    }
    private void SelectSlot() { _slot = (int)_action.Value * 5 + (int)_direction.Value; RefreshRecord(); }
    private void ChangeCenter()
    {
        if (_refreshing || Record == null || Record.Frames.Count == 0) return;
        Attempt(() =>
        {
            var next = ClassicVd.Read(ClassicVd.Write(_doc)); var record = next.Records[_slot]; var f = record.Frames[_frame];
            f.CenterX = (short)_cx.Value; f.CenterY = (short)_cy.Value; record.Edited = true;
            ClassicVd.Write(next); Snapshot(); _doc = next; RefreshFrame();
        });
    }
    private void RefreshRecord()
    {
        _frame = 0; _scrub.MaxValue = Math.Max(0, (Record?.Frames.Count ?? 0) - 1); _scrub.SetValueNoSignal(0); RefreshFrame();
    }
    private void RefreshFrame()
    {
        _refreshing = true;
        if (Record?.Frames.Count > 0)
        {
            _frame = Math.Clamp(_frame, 0, Record.Frames.Count - 1); var f = Record.Frames[_frame];
            int minX = Math.Min(-5, Record.Frames.Min(frame => -frame.CenterX)), minY = Math.Min(-5, Record.Frames.Min(frame => -frame.Height - frame.CenterY));
            int maxX = Math.Max(5, Record.Frames.Max(frame => frame.Width - frame.CenterX)), maxY = Math.Max(5, Record.Frames.Max(frame => -frame.CenterY));
            if ((long)(maxX - minX) * (maxY - minY) > 16 * 1024 * 1024) throw new InvalidDataException("Preview anchors exceed canvas budget.");
            Image canvas = Image.CreateEmpty(maxX - minX, maxY - minY, false, Image.Format.Rgba8);
            if (f.Width > 0) canvas.BlitRect(ToImage(Record, f), new Rect2I(0, 0, f.Width, f.Height), new Vector2I(-f.CenterX - minX, -f.Height - f.CenterY - minY));
            for (int d = -4; d <= 4; d++) { canvas.SetPixel(-minX + d, -minY, Colors.Cyan); canvas.SetPixel(-minX, -minY + d, Colors.Cyan); }
            _preview.Texture = ImageTexture.CreateFromImage(canvas);
            _cx.SetValueNoSignal(f.CenterX); _cy.SetValueNoSignal(f.CenterY);
            _status.Text = $"Type {_doc.Profile}; action {_slot / 5}, stored direction {_slot % 5}; frame {_frame + 1}/{Record.Frames.Count}, {f.Width}×{f.Height}; index extra {Record.Extra}. Populated slots: {_doc.Records.Count(r => r != null)}/{_doc.Records.Length}. Browser capture includes only the selected action/direction. Import VD for a complete package. Foot at (center X, height + center Y). Preview FPS is not exported timing. Export VD to retain changes; runtime animation overlay is not available.";
        }
        else { _preview.Texture = null; _status.Text = $"Type {_doc.Profile}; action {_slot / 5}, stored direction {_slot % 5}: missing slot. Import frames to author it."; }
        _refreshing = false;
    }
    public override void _Process(double delta)
    {
        _clock += delta;
        if (_clock < 1.0 / (_fps?.Value ?? 8)) return; _clock = 0;
        if (_playing && Record?.Frames.Count > 1) { _frame = (_frame + 1) % Record.Frames.Count; _scrub.SetValueNoSignal(_frame); RefreshFrame(); }
    }
    public static Image ToImage(AnimationRecord record, AnimationFrame f)
    {
        var bytes = new byte[f.Width * f.Height * 4];
        for (int i = 0; i < f.Indices.Length; i++) if (f.Opaque[i])
        {
            ushort word = record.Palette[f.Indices[i]];
            uint color = GUO.Utility.HuesHelper.Color16To32(word);
            bytes[i * 4] = (byte)color; bytes[i * 4 + 1] = (byte)(color >> 8); bytes[i * 4 + 2] = (byte)(color >> 16); bytes[i * 4 + 3] = 255;
        }
        return Image.CreateFromData(f.Width, f.Height, false, Image.Format.Rgba8, bytes);
    }
    public static AnimationRecord FromImages(Image[] images, (short X, short Y)[] centers, AnimationRecord prior = null)
    {
        if (images.Length == 0 || images.Length > 4096 || centers.Length != images.Length) throw new InvalidDataException("Invalid frame/center count.");
        var record = new AnimationRecord { Extra = prior?.Extra ?? -1, Edited = true, Palette = prior == null ? new ushort[256] : (ushort[])prior.Palette.Clone() };
        var words = new HashSet<ushort>();
        long pixelCount = 0;
        foreach (Image image in images)
        {
            if (image == null || image.IsEmpty() || image.GetWidth() > 1024 || image.GetHeight() > 1024) throw new InvalidDataException("Frame must be an image up to 1024×1024.");
            pixelCount += (long)image.GetWidth() * image.GetHeight();
            if (pixelCount > 16 * 1024 * 1024) throw new InvalidDataException("Animation frame pixels exceed the import budget.");
            for (int y = 0; y < image.GetHeight(); y++) for (int x = 0; x < image.GetWidth(); x++)
            {
                Color c = image.GetPixel(x, y); if (c.A8 < 128) continue;
                ushort word = (ushort)((c.R8 >> 3) << 10 | (c.G8 >> 3) << 5 | (c.B8 >> 3));
                words.Add(word);
                if (words.Count > 256) throw new InvalidDataException("More than 256 RGB555 colors: reduce the shared palette explicitly before import.");
            }
        }
        var palette = new Dictionary<ushort, byte>();
        var occupied = new HashSet<int>();
        if (prior != null)
            for (int i = 0; i < 256; i++)
            {
                ushort word = (ushort)(prior.Palette[i] & 0x7FFF);
                if (words.Contains(word) && !palette.ContainsKey(word)) { palette[word] = (byte)i; occupied.Add(i); }
            }
        foreach (ushort word in words.OrderBy(w => w))
        {
            if (palette.ContainsKey(word)) continue;
            int index = Enumerable.Range(0, 256).First(i => !occupied.Contains(i));
            occupied.Add(index); palette[word] = (byte)index; record.Palette[index] = word;
        }
        for (int n = 0; n < images.Length; n++)
        {
            Image image = images[n]; var f = new AnimationFrame { Width = image.GetWidth(), Height = image.GetHeight(), CenterX = centers[n].X, CenterY = centers[n].Y };
            f.Indices = new byte[f.Width * f.Height]; f.Opaque = new bool[f.Indices.Length];
            for (int y = 0; y < f.Height; y++) for (int x = 0; x < f.Width; x++)
            {
                Color c = image.GetPixel(x, y); int at = y * f.Width + x; if (c.A8 < 128) continue;
                f.Opaque[at] = true; f.Indices[at] = palette[(ushort)((c.R8 >> 3) << 10 | (c.G8 >> 3) << 5 | (c.B8 >> 3))];
            }
            record.Frames.Add(f);
        }
        return record;
    }
    private void Replace(Image[] images, (short X, short Y)[] centers, int slot, AnimationRecord metadata = null)
    {
        var next = FromImages(images, centers, metadata ?? _doc.Records[slot]);
        var validation = new AnimationDocument(_doc.Profile); validation.Records[slot] = next; ClassicVd.Write(validation);
        Snapshot(); _doc.Records[slot] = next; RefreshRecord();
    }
    private void ImportSequence()
    {
        var dlg = new EditorFileDialog { FileMode = EditorFileDialog.FileModeEnum.OpenFiles, Access = EditorFileDialog.AccessEnum.Filesystem, Filters = new[] { "*.png,*.bmp,*.webp ; Images (filename order)" } };
        dlg.FilesSelected += paths => { Attempt(() =>
        {
            string[] ordered = paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (ordered.Length > 4096) throw new InvalidDataException("Too many image files.");
            var images = ordered.Select(Image.LoadFromFile).ToArray();
            Replace(images, images.Select((im, i) => i < (Record?.Frames.Count ?? 0) ? (Record.Frames[i].CenterX, Record.Frames[i].CenterY) : ((short)_cx.Value, (short)_cy.Value)).ToArray(), _slot);
        }); dlg.QueueFree(); }; dlg.Canceled += () => dlg.QueueFree(); AddChild(dlg); dlg.PopupFileDialog();
    }
    private void ImportSheet() => Pick(EditorFileDialog.FileModeEnum.OpenFile, "*.png ; PNG sheet", path =>
    {
        string json = Path.ChangeExtension(path, ".json");
        if (File.Exists(json)) { ImportMappedSheet(path, JsonNode.Parse(File.ReadAllText(json)), _slot); return; }
        var dialog = new ConfirmationDialog { Title = "Spritesheet layout (row-major order)" }; var box = new VBoxContainer(); dialog.AddChild(box);
        var width = Number(box, "Cell width", 1, 1024, 96); var height = Number(box, "Cell height", 1, 1024, 96);
        dialog.Confirmed += () => { Attempt(() =>
        {
            Image sheet = Image.LoadFromFile(path); int w = (int)width.Value, h = (int)height.Value;
            if (sheet == null || sheet.GetWidth() % w != 0 || sheet.GetHeight() % h != 0) throw new InvalidDataException("Sheet dimensions must be a multiple of the cell size.");
            var images = new List<Image>(); for (int y = 0; y < sheet.GetHeight(); y += h) for (int x = 0; x < sheet.GetWidth(); x += w) images.Add(sheet.GetRegion(new Rect2I(x, y, w, h)));
            if (images.Count > 4096) throw new InvalidDataException("Too many frames.");
            Replace(images.ToArray(), images.Select(_ => ((short)_cx.Value, (short)_cy.Value)).ToArray(), _slot);
        }); dialog.QueueFree(); }; dialog.Canceled += () => dialog.QueueFree(); AddChild(dialog); dialog.PopupCentered();
    });
    internal void ImportMappedSheet(string path, JsonNode side, int slot)
    {
        Image sheet = Image.LoadFromFile(path); if (sheet == null || side["frames"] is not JsonArray map || map.Count == 0 || map.Count > 4096) throw new InvalidDataException("Invalid animation sheet metadata.");
        var images = new List<Image>(); var centers = new List<(short, short)>();
        foreach (var node in map)
        {
            var rect = new Rect2I((int)node["x"], (int)node["y"], (int)node["width"], (int)node["height"]);
            if (rect.Position.X < 0 || rect.Position.Y < 0 || rect.Size.X <= 0 || rect.Size.Y <= 0 || rect.End.X > sheet.GetWidth() || rect.End.Y > sheet.GetHeight()) throw new InvalidDataException("Frame rectangle outside sheet.");
            images.Add(sheet.GetRegion(rect)); centers.Add((checked((short)(int)node["center_x"]), checked((short)(int)node["center_y"])));
        }
        AnimationRecord metadata = null;
        if (side["palette"] is JsonArray palette)
        {
            if (palette.Count != 256) throw new InvalidDataException("Animation palette must contain exactly 256 words.");
            metadata = new AnimationRecord { Palette = palette.Select(word => checked((ushort)(int)word)).ToArray(),
                Extra = side["index_extra"] == null ? _doc.Records[slot]?.Extra ?? -1 : (int)side["index_extra"] };
        }
        Replace(images.ToArray(), centers.ToArray(), slot, metadata);
    }
    internal JsonObject ExportSheet(string path)
    {
        EnsureSafe(path); if (Record?.Frames.Count is not > 0) throw new InvalidDataException("This slot has no frames.");
        int w = Record.Frames.Max(f => f.Width), h = Record.Frames.Max(f => f.Height);
        if (w <= 0 || h <= 0 || (long)w * h * Record.Frames.Count > 16 * 1024 * 1024) throw new InvalidDataException("Sheet exceeds pixel budget.");
        Image sheet = Image.CreateEmpty(w * Record.Frames.Count, h, false, Image.Format.Rgba8); var frames = new JsonArray();
        for (int i = 0; i < Record.Frames.Count; i++)
        {
            var f = Record.Frames[i]; if (f.Width == 0) throw new InvalidDataException("Empty zero-size frames need VD interchange.");
            sheet.BlitRect(ToImage(Record, f), new Rect2I(0, 0, f.Width, f.Height), new Vector2I(i * w, 0));
            frames.Add(new JsonObject { ["index"] = i, ["x"] = i * w, ["y"] = 0, ["width"] = f.Width, ["height"] = f.Height, ["center_x"] = (int)f.CenterX, ["center_y"] = (int)f.CenterY });
        }
        var colors = new JsonArray(); foreach (ushort word in Record.Palette) colors.Add((int)word);
        var side = new JsonObject { ["format"] = 1, ["kind"] = "animation", ["id"] = _body, ["profile"] = _doc.Profile, ["action"] = _slot / 5, ["direction"] = _slot % 5, ["columns"] = Record.Frames.Count, ["cell_width"] = w, ["cell_height"] = h,
            ["stem"] = Path.GetFileNameWithoutExtension(path), ["frames"] = frames, ["palette"] = colors, ["index_extra"] = Record.Extra, ["provenance"] = new JsonObject { ["derived_from_client_art"] = true, ["tool"] = "GUO animation working copy" } };
        if (sheet.SavePng(path) != Error.Ok) throw new IOException("Could not export animation sheet.");
        File.WriteAllText(Path.ChangeExtension(path, ".json"), side.ToJsonString()); return side;
    }
    private void ExportFrames(string path)
    {
        EnsureSafe(path); if (Record == null) throw new InvalidDataException("This slot is missing.");
        string prefix = $"anim_0x{_body:X4}_a{_slot / 5}_d{_slot % 5}";
        string sheetPath = Path.Combine(path, prefix + "_sheet.png");
        if (File.Exists(sheetPath) || Enumerable.Range(0, Record.Frames.Count).Any(i => File.Exists(Path.Combine(path, $"{prefix}_{i:D4}.png")))) throw new IOException("Export files already exist; choose another folder.");
        ExportSheet(sheetPath);
        for (int i = 0; i < Record.Frames.Count; i++) ToImage(Record, Record.Frames[i]).SavePng(Path.Combine(path, $"{prefix}_{i:D4}.png"));
    }
    /// <summary>
    /// The selected slot as an overlay clip. A stored direction is 0..4; the client shows it unmirrored
    /// as direction stored + 3, and the overlay keeps each shown direction by itself.
    /// </summary>
    internal OverlayAnimationClip ToClip()
    {
        if (Record?.Frames.Count is not > 0) throw new InvalidDataException("This slot has no frames.");
        if (Record.Frames.Any(f => f.Width == 0)) throw new InvalidDataException("Empty zero-size frames need VD interchange.");
        return new OverlayAnimationClip { Action = _slot / 5, Direction = _slot % 5 + 3, Fps = _fps?.Value ?? 8,
            Frames = Record.Frames.Select(f => ToImage(Record, f)).ToArray(),
            Centers = Record.Frames.Select(f => new Vector2I(f.CenterX, f.CenterY)).ToArray() };
    }
    private void ApplyToOverlay()
    {
        if (ApplyOverlay == null) throw new InvalidOperationException("No editor overlay is attached to this working copy.");
        OverlayAnimationClip clip = ToClip();
        string why = ApplyOverlay(clip);
        if (why != null) throw new InvalidDataException(why);
        _status.Text = $"Applied action {clip.Action}, direction {clip.Direction} to the editor overlay. Its mirrored direction is separate; Edit in Pixelorama on the Animations panel continues from here.";
    }
    private void WriteVd(string path)
    {
        EnsureSafe(path); byte[] bytes = ClassicVd.Write(_doc); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); ClassicVd.Read(File.ReadAllBytes(temp)); File.Move(temp, path, true); _undo.Clear(); _redo.Clear(); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void EnsureSafe(string path)
    {
        AssetActions.EnsureExportDestination(path);
    }
    private void Pick(EditorFileDialog.FileModeEnum mode, string filter, Action<string> action)
    {
        var dialog = new EditorFileDialog { FileMode = mode, Access = EditorFileDialog.AccessEnum.Filesystem, Filters = filter.Length > 0 ? new[] { filter } : Array.Empty<string>() };
        dialog.FileSelected += path => { Attempt(() => action(path)); dialog.QueueFree(); }; dialog.DirSelected += path => { Attempt(() => action(path)); dialog.QueueFree(); };
        dialog.Canceled += () => dialog.QueueFree(); AddChild(dialog); dialog.PopupFileDialog();
    }
}
#endif
