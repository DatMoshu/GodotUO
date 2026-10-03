// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GUO.UI.Authoring;

public sealed record GumpReply(int ButtonId, uint[] Switches, Tuple<ushort, string>[] Entries);

/// <summary>
/// Native Godot presentation of a document. The host owns game actions and subscribes to Reply;
/// this view never sends packets or changes game state. Page zero is shared across pages.
/// </summary>
[Tool]
public partial class AuthoredGumpView : Control
{
    [Export(PropertyHint.MultilineText)] public string DocumentJson { get; set; } = "";
    public override void _Ready()
    {
        if (DocumentJson.Length > 0) Build(GumpDocument.Parse(DocumentJson));
    }
    private GumpDocument _document;
    private readonly Dictionary<string, Control> _controls = new();
    private readonly Dictionary<int, Texture2D> _localTextures = new();
    public IReadOnlyDictionary<string, Control> Elements => _controls;
    public int ActivePage { get; private set; } = 1;
    public event Action<GumpReply> Reply;

    public void Build(GumpDocument document, Func<int, Texture2D> gumpTexture = null)
    {
        document.Validate();
        if (gumpTexture == null && !Engine.IsEditorHint()) gumpTexture = RuntimeTexture;
        foreach (Node child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        _controls.Clear();
        _document = document;
        TextureFilter = TextureFilterEnum.Nearest;
        CustomMinimumSize = document.Modern ? new Vector2(16, 16) : new Vector2(document.Width, document.Height);
        Size = new Vector2(document.Width, document.Height);
        MouseFilter = MouseFilterEnum.Pass;
        var groups = new Dictionary<int, ButtonGroup>();
        foreach (GumpElement e in document.Elements)
        {
            Control c;
            Texture2D tex = e.Graphic > 0 ? gumpTexture?.Invoke(e.Graphic) : null;
            switch (e.Kind)
            {
                case GumpElementKind.Panel:
                    var panel = new Panel();
                    panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                    {
                        BgColor = Color.FromString(e.Background, new Color("242c3b")),
                        BorderColor = new Color("82714b"), BorderWidthBottom = 1, BorderWidthTop = 1,
                        BorderWidthLeft = 1, BorderWidthRight = 1,
                        CornerRadiusBottomLeft = document.Modern ? 8 : 0, CornerRadiusBottomRight = document.Modern ? 8 : 0,
                        CornerRadiusTopLeft = document.Modern ? 8 : 0, CornerRadiusTopRight = document.Modern ? 8 : 0
                    });
                    c = panel;
                    if (!document.Modern && e.Graphic > 0 && gumpTexture != null)
                        BuildClassicFrame(panel, e, gumpTexture);
                    break;
                case GumpElementKind.Image: case GumpElementKind.TiledImage:
                    c = tex != null ? new TextureRect
                    {
                        Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = e.Kind == GumpElementKind.TiledImage ? TextureRect.StretchModeEnum.Tile : TextureRect.StretchModeEnum.Scale,
                        TextureRepeat = TextureRepeatEnum.Enabled
                    } : new Label { Text = $"Art 0x{e.Graphic:X4}", VerticalAlignment = VerticalAlignment.Center };
                    break;
                case GumpElementKind.Label:
                    c = new Label { Text = e.Text, ClipText = true, VerticalAlignment = VerticalAlignment.Center }; break;
                case GumpElementKind.Html:
                    // Classic HTML is not BBCode. Display text safely; never interpret embedded markup as code.
                    c = new RichTextLabel { Text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(e.Text, "<[^>]*>", "")), BbcodeEnabled = false }; break;
                case GumpElementKind.Button:
                    if (!document.Modern && tex != null)
                    {
                        var artButton = new TextureButton { TextureNormal = tex, TexturePressed = gumpTexture?.Invoke(e.GraphicDown), IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.Scale };
                        artButton.Pressed += () => Activate(e);
                        c = artButton;
                    }
                    else
                    {
                        var button = new Button { Text = e.Text.Length > 0 ? e.Text : $"Reply {e.ReplyId}" };
                        button.Pressed += () => Activate(e);
                        c = button;
                    }
                    break;
                case GumpElementKind.CheckBox: case GumpElementKind.Radio:
                    var check = new CheckBox { Text = e.Text, ButtonPressed = e.Checked };
                    if (e.Kind == GumpElementKind.Radio)
                    {
                        if (!groups.TryGetValue(e.Group, out var group)) groups[e.Group] = group = new ButtonGroup();
                        check.ButtonGroup = group;
                    }
                    c = check; break;
                case GumpElementKind.TextEntry:
                    c = new LineEdit { Text = e.Text, MaxLength = e.MaxLength }; break;
                case GumpElementKind.Progress:
                    c = new ProgressBar { Value = 65, ShowPercentage = true }; break;
                default:
                    // A live snapshot includes structural/custom controls as selectable bounds.
                    // Painting an "Unsupported" label over every container obscures its real children.
                    c = e.SourceType.Length > 0 ? new Control { TooltipText = $"Custom control: {e.Name}" }
                        : new Label { Text = $"Unsupported: {e.Name}", Modulate = new Color("ebad63"), ClipText = true }; break;
            }
            c.Name = "Element_" + e.Id;
            c.TextureFilter = TextureFilterEnum.Nearest;
            c.AddThemeFontSizeOverride("font_size", e.FontSize);
            c.AddThemeColorOverride("font_color", Color.FromString(e.Color, Colors.White));
            AddChild(c);
            c.Position = new Vector2(e.X, e.Y);
            c.Size = new Vector2(e.Width, e.Height);
            if (document.Modern) ApplyAnchor(c, e, document);
            if (c is not BaseButton && c is not LineEdit && c is not RichTextLabel) c.MouseFilter = MouseFilterEnum.Ignore;
            _controls[e.Id] = c;
        }
        SetPage(1);
    }

    private static void BuildClassicFrame(Panel parent, GumpElement e, Func<int, Texture2D> texture)
    {
        // UO resizepic is nine consecutive gump images, not a stretched single image.
        // Classic ordering is TL,T,TR,L,R,BL,B,BR,centre.
        var art = new[] { 0, 1, 2, 3, 8, 4, 5, 6, 7 }.Select(i => texture(e.Graphic + i)).ToArray();
        if (art.Any(t => t == null)) return;
        parent.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        float left = Math.Max(art[0].GetWidth(), art[3].GetWidth());
        float right = Math.Max(art[2].GetWidth(), art[5].GetWidth());
        float top = Math.Max(art[0].GetHeight(), art[1].GetHeight());
        float bottom = Math.Max(art[6].GetHeight(), art[7].GetHeight());
        float[] x = { 0, left, Math.Max(left, e.Width - right) }, y = { 0, top, Math.Max(top, e.Height - bottom) };
        float[] w = { left, Math.Max(1, e.Width - left - right), right }, h = { top, Math.Max(1, e.Height - top - bottom), bottom };
        for (int i = 0; i < 9; i++) parent.AddChild(new TextureRect
        {
            Texture = art[i], Position = new Vector2(x[i % 3], y[i / 3]), Size = new Vector2(w[i % 3], h[i / 3]),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Tile,
            TextureRepeat = TextureRepeatEnum.Enabled, TextureFilter = TextureFilterEnum.Nearest, MouseFilter = MouseFilterEnum.Ignore
        });
    }

    private static void ApplyAnchor(Control c, GumpElement e, GumpDocument d)
    {
        switch (e.Anchor)
        {
            case "TopRight": c.AnchorLeft = c.AnchorRight = 1; c.OffsetLeft = e.X - d.Width; c.OffsetRight = e.X + e.Width - d.Width; break;
            case "BottomLeft": c.AnchorTop = c.AnchorBottom = 1; c.OffsetTop = e.Y - d.Height; c.OffsetBottom = e.Y + e.Height - d.Height; break;
            case "BottomRight":
                c.AnchorLeft = c.AnchorRight = c.AnchorTop = c.AnchorBottom = 1;
                c.OffsetLeft = e.X - d.Width; c.OffsetRight = e.X + e.Width - d.Width;
                c.OffsetTop = e.Y - d.Height; c.OffsetBottom = e.Y + e.Height - d.Height; break;
            case "Stretch": c.AnchorRight = c.AnchorBottom = 1; c.OffsetRight = e.X + e.Width - d.Width; c.OffsetBottom = e.Y + e.Height - d.Height; break;
        }
    }

    public void SetPage(int page)
    {
        ActivePage = page;
        foreach (var e in _document.Elements) _controls[e.Id].Visible = e.Visible && (e.Page == 0 || e.Page == page);
    }
    public void Activate(GumpElement button)
    {
        if (button.PageButton) { SetPage(button.TargetPage); return; }
        Reply?.Invoke(CollectReply(button.ReplyId));
    }
    public GumpReply CollectReply(int buttonId)
    {
        var switches = new List<uint>();
        var entries = new List<Tuple<ushort, string>>();
        // Classic replies include fields from all pages, not only the visible page.
        foreach (var e in _document.Elements.Where(e => e.Visible))
        {
            if (_controls[e.Id] is CheckBox c && c.ButtonPressed) switches.Add((uint)e.ReplyId);
            if (_controls[e.Id] is LineEdit t) entries.Add(Tuple.Create((ushort)e.EntryId, t.Text));
        }
        return new GumpReply(buttonId, switches.ToArray(), entries.ToArray());
    }

    /// <summary>Host-provided display values. Keys are explicit authoring bindings, never executable expressions.</summary>
    public void ApplyBindings(IReadOnlyDictionary<string, Variant> values)
    {
        foreach (var e in _document.Elements)
        {
            if (e.Binding.Length == 0 || !values.TryGetValue(e.Binding, out var value)) continue;
            switch (_controls[e.Id])
            {
                case Label label: label.Text = value.AsString(); break;
                case RichTextLabel rich: rich.Text = value.AsString(); break;
                case ProgressBar bar: bar.Value = value.AsDouble(); break;
                case Button button when button is not CheckBox: button.Text = value.AsString(); break;
            }
        }
    }

    private Texture2D RuntimeTexture(int id)
    {
        if (_localTextures.TryGetValue(id, out var texture)) return texture;
        var loader = Client.Game?.UO?.FileManager?.Gumps;
        if (loader == null || id < 0 || id > 65535) return null;
        var art = loader.GetGump((uint)id);
        if (art.Width <= 0 || art.Height <= 0 || art.Pixels.Length < art.Width * art.Height) return null;
        byte[] rgba = new byte[art.Width * art.Height * 4];
        for (int i = 0; i < art.Width * art.Height; i++)
        {
            uint pixel = art.Pixels[i]; int at = i * 4;
            rgba[at] = (byte)pixel; rgba[at + 1] = (byte)(pixel >> 8); rgba[at + 2] = (byte)(pixel >> 16); rgba[at + 3] = pixel == 0 ? (byte)0 : (byte)255;
        }
        using var image = Image.CreateFromData(art.Width, art.Height, false, Image.Format.Rgba8, rgba);
        return _localTextures[id] = ImageTexture.CreateFromImage(image);
    }
}
