#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// What the editor tour (<see cref="EditorTour"/>) draws over the editor: a
/// caption card (step, title, text), an optional detail box above it, yellow
/// outlines round the controls being talked about, a tooltip bubble standing
/// in for a hover, and a pointer where a scripted click lands. All of it is
/// ordinary Control drawing, so the editor's own frame capture sees it.
/// </summary>
/// <remarks>
/// A screenshot has no mouse pointer and a scripted run has no real hover, so
/// the pointer is drawn and the tooltip is the control's own
/// <c>TooltipText</c> shown in a bubble. It is never an input surface: the
/// overlay ignores the mouse.
/// </remarks>
[Tool]
public partial class TourOverlay : Control
{
    private VBoxContainer _stack;
    private PanelContainer _captionCard, _detailCard;
    private Label _step, _title, _body, _detail;
    private bool _atTop;

    private readonly List<(Rect2 Rect, string Label)> _marks = new();
    private (Rect2 Anchor, string Text)? _tip;
    private Vector2? _pointer;

    public override void _Ready()
    {
        if (_stack != null)
        {
            return;
        }

        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        _stack = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _stack.AddThemeConstantOverride("separation", 8);
        AddChild(_stack);

        _detailCard = Card(new Color(0.07f, 0.06f, 0.05f, 0.92f));
        _detail = new Label { AutowrapMode = TextServer.AutowrapMode.Off };
        _detail.AddThemeFontSizeOverride("font_size", 20);
        _detail.AddThemeColorOverride("font_color", new Color(0.82f, 0.95f, 0.8f));
        _detailCard.AddChild(_detail);
        _detailCard.Visible = false;

        _captionCard = Card(new Color(0.16f, 0.11f, 0.06f, 0.94f));
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        _captionCard.AddChild(box);
        _step = new Label();
        _step.AddThemeFontSizeOverride("font_size", 22);
        _step.AddThemeColorOverride("font_color", new Color(0.78f, 0.62f, 0.3f));
        box.AddChild(_step);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 40);
        _title.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.7f));
        box.AddChild(_title);
        _body = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(2100, 0) };
        _body.AddThemeFontSizeOverride("font_size", 29);
        _body.AddThemeColorOverride("font_color", new Color(0.96f, 0.94f, 0.9f));
        box.AddChild(_body);

        PlaceStack();
    }

    private PanelContainer Card(Color fill)
    {
        var style = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = new Color(0.78f, 0.62f, 0.3f),
            ContentMarginLeft = 18,
            ContentMarginRight = 18,
            ContentMarginTop = 10,
            ContentMarginBottom = 12,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(4);
        var card = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        card.AddThemeStyleboxOverride("panel", style);
        _stack.AddChild(card);
        return card;
    }

    /// <summary>Where the caption sits: the bottom edge (default) or the top, under the menu bar.</summary>
    public bool AtTop
    {
        get => _atTop;
        set
        {
            _atTop = value;
            if (_stack != null)
            {
                PlaceStack();
            }
        }
    }

    private void PlaceStack()
    {
        // Caption nearest its edge, detail box on the inner side of it.
        _stack.MoveChild(_atTop ? _captionCard : _detailCard, 0);
        _stack.MoveChild(_atTop ? _detailCard : _captionCard, 1);
        _stack.ResetSize();
    }

    public override void _Process(double delta)
    {
        if (_stack == null)
        {
            return;
        }

        // Placed by hand each frame: the card's height follows its text.
        Vector2 area = GetViewportRect().Size;
        // Sized for 3840 px wide; smaller windows get a smaller card.
        float k = Math.Clamp(area.X / 3840f, 0.4f, 1f);
        _body.CustomMinimumSize = new Vector2(2100 * k, 0);
        _body.AddThemeFontSizeOverride("font_size", (int)(29 * k));
        _title.AddThemeFontSizeOverride("font_size", (int)(40 * k));
        _step.AddThemeFontSizeOverride("font_size", (int)(22 * k));
        _detail.AddThemeFontSizeOverride("font_size", (int)(20 * k));
        Vector2 size = _stack.GetCombinedMinimumSize();
        _stack.Size = size;
        _stack.Position = new Vector2((area.X - size.X) / 2, _atTop ? 90 : area.Y - size.Y - 28);
    }

    public void SetCaption(string step, string title, string body)
    {
        _step.Text = step;
        _title.Text = title;
        _body.Text = body;
        _captionCard.Visible = !string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(body);
        Relayout();
    }

    /// <summary>Monospace-ish lines above the caption (a tool's output, a block file); null or empty hides the box.</summary>
    public void SetDetail(string text)
    {
        _detail.Text = text ?? "";
        _detailCard.Visible = !string.IsNullOrEmpty(text);
        Relayout();
    }

    private void Relayout()
    {
        _stack.ResetSize();
        _captionCard.ResetSize();
        _detailCard.ResetSize();
        PlaceStack();
    }

    public void Mark(Rect2 rect, string label = null)
    {
        _marks.Add((rect, label));
        QueueRedraw();
    }

    public void Tip(Rect2 anchor, string text)
    {
        _tip = (anchor, text);
        QueueRedraw();
    }

    public void Pointer(Vector2? at)
    {
        _pointer = at;
        QueueRedraw();
    }

    public void ClearMarks()
    {
        _marks.Clear();
        _tip = null;
        _pointer = null;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var yellow = new Color(1f, 0.85f, 0.2f);
        Font font = GetThemeDefaultFont();
        foreach (var (rect, label) in _marks)
        {
            Rect2 r = rect.Grow(4);
            DrawRect(r.Grow(2), new Color(0, 0, 0, 0.85f), false, 2f);
            DrawRect(r, yellow, false, 3f);
            if (!string.IsNullOrEmpty(label))
            {
                Vector2 size = font.GetStringSize(label, HorizontalAlignment.Left, -1, 20);
                var tag = new Rect2(r.Position.X, r.Position.Y - size.Y - 8, size.X + 12, size.Y + 6);
                if (tag.Position.Y < 0)
                {
                    tag.Position = new Vector2(r.Position.X, r.End.Y + 2);
                }

                DrawRect(tag, yellow);
                DrawString(font, tag.Position + new Vector2(6, size.Y - 3), label, HorizontalAlignment.Left, -1, 20, new Color(0.1f, 0.07f, 0.02f));
            }
        }

        if (_tip is { } tip && !string.IsNullOrEmpty(tip.Text))
        {
            Vector2 size = font.GetMultilineStringSize(tip.Text, HorizontalAlignment.Left, 560, 20);
            Vector2 at = new(tip.Anchor.Position.X, tip.Anchor.End.Y + 8);
            if (at.X + size.X + 16 > Size.X)
            {
                at.X = Size.X - size.X - 20;
            }

            var bubble = new Rect2(at, size + new Vector2(16, 12));
            DrawRect(bubble, new Color(1f, 0.97f, 0.8f));
            DrawRect(bubble, new Color(0.2f, 0.15f, 0.05f), false, 1.5f);
            DrawMultilineString(font, at + new Vector2(8, 8 + 12), tip.Text, HorizontalAlignment.Left, 560, 20, -1, new Color(0.1f, 0.08f, 0.04f));
        }

        if (_pointer is { } p)
        {
            var arrow = new[]
            {
                p, p + new Vector2(0, 21), p + new Vector2(5, 16.5f), p + new Vector2(9, 25),
                p + new Vector2(13, 23.5f), p + new Vector2(9, 15), p + new Vector2(16, 15),
            };
            DrawColoredPolygon(arrow, Colors.White);
            var outline = new Vector2[arrow.Length + 1];
            arrow.CopyTo(outline, 0);
            outline[arrow.Length] = arrow[0];
            DrawPolyline(outline, Colors.Black, 1.5f);
            DrawArc(p, 14, 0, Mathf.Tau, 24, new Color(1f, 0.85f, 0.2f, 0.9f), 2.5f);
        }
    }
}
#endif
