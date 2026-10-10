#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
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

    private readonly List<(Func<Rect2?> Rect, Control Owner, string Label)> _marks = new();
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

    /// <summary>
    /// Where the caption goes, in global coordinates, asked each frame: the map tour puts it over the lower edge of
    /// the map, so it never covers the panels whose controls are being pointed at. Null: the whole window.
    /// </summary>
    public Func<Rect2?> CaptionArea { get; set; }

    /// <summary>The caption and detail cards as drawn, for the tour's check that nothing it points at is under them.</summary>
    public Rect2? CardsRect => _stack != null && (_captionCard.Visible || _detailCard.Visible) ? _stack.GetGlobalRect() : null;

    public override void _Process(double delta)
    {
        if (_stack == null)
        {
            return;
        }

        // Placed by hand each frame: the card's height follows its text.
        Rect2 area = CaptionArea?.Invoke() ?? GetViewportRect();
        // Sized for reading on a 1080p video; a 4K window doubles it.
        float k = Math.Clamp(GetViewportRect().Size.Y / 1080f, 0.75f, 2f);
        float width = Math.Min(area.Size.X - 32 * k, 1100 * k);
        _body.CustomMinimumSize = new Vector2(width - 40 * k, 0);
        _detail.CustomMinimumSize = new Vector2(width - 40 * k, 0);
        _body.AddThemeFontSizeOverride("font_size", (int)(21 * k));
        _title.AddThemeFontSizeOverride("font_size", (int)(25 * k));
        _step.AddThemeFontSizeOverride("font_size", (int)(14 * k));
        _detail.AddThemeFontSizeOverride("font_size", (int)(17 * k));
        Vector2 size = _stack.GetCombinedMinimumSize();
        _stack.Size = size;
        _stack.Position = new Vector2(area.Position.X + (area.Size.X - size.X) / 2,
            _atTop ? area.Position.Y + 16 * k : area.End.Y - size.Y - 16 * k);
        QueueRedraw();
    }

    public void SetCaption(string step, string title, string body)
    {
        _step.Text = step;
        _step.Visible = !string.IsNullOrEmpty(step);
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
        _marks.Add((() => rect, null, label));
        QueueRedraw();
    }

    /// <summary>A mark that follows its control: its rect is read again every frame, so a layout change never leaves it floating.</summary>
    public void Mark(Control owner, Func<Rect2?> rect, string label = null)
    {
        _marks.Add((rect, owner, label));
        QueueRedraw();
    }

    /// <summary>The marks that follow a control, for the tour's per-frame check.</summary>
    public IEnumerable<(Control Owner, Func<Rect2?> Rect, string Label)> LiveMarks
    {
        get
        {
            foreach (var (rect, owner, label) in _marks)
            {
                if (owner != null)
                {
                    yield return (owner, rect, label);
                }
            }
        }
    }

    /// <summary>The pointer's tip and click ring centre, in global (canvas) coordinates, as given to <see cref="Pointer"/>.</summary>
    public Vector2? PointerAt => _pointer;

    /// <summary>
    /// The click ring's centre in the pixels of a saved frame: through this overlay's own canvas transform and the
    /// window's stretch, so an editor display scale or a moved overlay is accounted for, not assumed away.
    /// </summary>
    public Vector2? PointerFramePx => _pointer is { } p ? FramePx(this, GetGlobalTransform().AffineInverse() * p) : null;

    /// <summary>Where a point in <paramref name="item"/>'s local space lands in the pixels of a saved frame of its window.</summary>
    public static Vector2 FramePx(CanvasItem item, Vector2 local) =>
        item.GetViewport().GetFinalTransform() * (item.GetGlobalTransformWithCanvas() * local);

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

    /// <summary>Removes the outlines and the tooltip; the pointer too unless <paramref name="keepPointer"/>.</summary>
    public void ClearMarks(bool keepPointer = false)
    {
        _marks.Clear();
        _tip = null;
        if (!keepPointer)
        {
            _pointer = null;
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        var yellow = new Color(1f, 0.85f, 0.2f);
        Font font = GetThemeDefaultFont();
        var tags = new List<Rect2>();
        foreach (var (rectNow, owner, label) in _marks)
        {
            if (owner != null && (!IsInstanceValid(owner) || !owner.IsVisibleInTree()) || rectNow() is not { } rect)
            {
                continue;
            }

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

                // Never on top of another mark's tag: slide right past it.
                for (int guard = 0; guard < 8 && tags.FirstOrDefault(t => t.Intersects(tag)) is { Size.X: > 0 } hit; guard++)
                {
                    tag.Position = new Vector2(hit.End.X + 6, tag.Position.Y);
                }

                tags.Add(tag);

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

        if (_pointer is { } global)
        {
            // Given in global coordinates; drawn in this control's own, so the ring is on the click wherever the overlay sits.
            Vector2 p = GetGlobalTransform().AffineInverse() * global;
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
