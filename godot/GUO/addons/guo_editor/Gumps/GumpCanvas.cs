#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.UI.Authoring;

/// <summary>Design surface in document coordinates; zoom belongs to its parent.</summary>
[Tool]
public partial class GumpCanvas : Control
{
    public GumpDocument Document { get; set; }
    public HashSet<string> Selection { get; } = new();
    public int Page { get; set; } = 1;
    public int Grid { get; set; } = 8;
    public bool Snap { get; set; } = true;
    public bool Preview { get; private set; }
    public AuthoredGumpView View { get; private set; }
    public event Action SelectionChanged;
    public event Action BeginEdit;
    public event Action EndEdit;
    public event Action<GumpReply> Replied;

    /// <summary>Gump art dragged from UO Assets was dropped here, at this point in document pixels.</summary>
    public event Action<int, Vector2> ArtDropped;
    private Control _overlay;
    private bool _dragging, _resizing, _edited;
    private Vector2 _start;
    private readonly Dictionary<string, Rect2> _before = new();

    public void Rebuild(Func<int, Texture2D> textures, bool preview)
    {
        Preview = preview;
        foreach (Node n in GetChildren()) { RemoveChild(n); n.QueueFree(); }
        CustomMinimumSize = Size = new Vector2(Document.Width, Document.Height);
        View = new AuthoredGumpView();
        AddChild(View);
        View.Build(Document, textures);
        View.SetPage(Page);
        View.Reply += r => Replied?.Invoke(r);
        _overlay = new Control { MouseFilter = preview ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop, FocusMode = FocusModeEnum.All };
        AddChild(_overlay);
        _overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _overlay.GuiInput += HandleInput;
        _overlay.Draw += DrawGuides;
        _overlay.SetDragForwarding(default,
            Callable.From<Vector2, Variant, bool>((_, data) => !Preview && DroppedGump(data) != null),
            Callable.From<Vector2, Variant>((at, data) => { if (DroppedGump(data) is int id) ArtDropped?.Invoke(id, at); }));
        QueueRedraw();
    }

    private static int? DroppedGump(Variant data) =>
        data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary() is var d && d.ContainsKey("guo_gump") ? d["guo_gump"].AsInt32() : null;

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("141923"));
        if (!Preview && Grid > 1)
        {
            int spacing = Grid * Math.Max(1, (int)Math.Ceiling(Math.Max(Size.X, Size.Y) / (Grid * 180)));
            for (int x = 0; x < Size.X; x += spacing)
                for (int y = 0; y < Size.Y; y += spacing) DrawCircle(new Vector2(x, y), 0.6f, new Color("344050"));
        }
    }
    private void DrawGuides()
    {
        if (Preview) return;
        foreach (var e in Document.Elements.Where(e => Selection.Contains(e.Id) && e.Visible && (e.Page == 0 || e.Page == Page)))
        {
            var r = new Rect2(e.X, e.Y, e.Width, e.Height);
            _overlay.DrawRect(r, new Color("63c6eb"), false, 2);
            _overlay.DrawRect(new Rect2(r.End - new Vector2(6, 6), new Vector2(8, 8)), new Color("63c6eb"));
        }
    }

    private void HandleInput(InputEvent input)
    {
        if (Preview) return;
        if (input is InputEventMouseButton b && b.ButtonIndex == MouseButton.Left)
        {
            if (b.Pressed)
            {
                _overlay.GrabFocus();
                _start = b.Position;
                var hit = Document.Elements.LastOrDefault(e => e.Visible && !e.Locked && (e.Page == 0 || e.Page == Page)
                    && new Rect2(e.X, e.Y, e.Width + 3, e.Height + 3).HasPoint(b.Position));
                if (hit == null) { Selection.Clear(); SelectionChanged?.Invoke(); _overlay.QueueRedraw(); return; }
                if (b.ShiftPressed)
                {
                    if (!Selection.Add(hit.Id)) Selection.Remove(hit.Id);
                }
                else if (!Selection.Contains(hit.Id)) { Selection.Clear(); Selection.Add(hit.Id); }
                _resizing = Selection.Count == 1 && new Rect2(hit.X + hit.Width - 9, hit.Y + hit.Height - 9, 14, 14).HasPoint(b.Position);
                _before.Clear();
                foreach (var e in Document.Elements.Where(e => Selection.Contains(e.Id) && !e.Locked))
                    _before[e.Id] = new Rect2(e.X, e.Y, e.Width, e.Height);
                _dragging = _before.Count > 0;
                _edited = false;
                SelectionChanged?.Invoke();
            }
            else if (_dragging)
            {
                _dragging = false;
                if (_edited) EndEdit?.Invoke();
            }
            _overlay.QueueRedraw();
            AcceptEvent();
        }
        else if (input is InputEventMouseMotion m && _dragging)
        {
            Vector2 delta = m.Position - _start;
            if (!_edited && delta.Length() < 2) return;
            if (!_edited) { BeginEdit?.Invoke(); _edited = true; }
            int Quantize(float value) => Snap ? (int)Math.Round(value / Grid) * Grid : (int)Math.Round(value);
            foreach (var e in Document.Elements.Where(e => _before.ContainsKey(e.Id)))
            {
                var old = _before[e.Id];
                if (_resizing)
                {
                    e.Width = Math.Clamp(Quantize(old.Size.X + delta.X), 8, 8192);
                    e.Height = Math.Clamp(Quantize(old.Size.Y + delta.Y), 8, 8192);
                }
                else { e.X = Math.Clamp(Quantize(old.Position.X + delta.X), -32767, 32767); e.Y = Math.Clamp(Quantize(old.Position.Y + delta.Y), -32767, 32767); }
                var c = View.Elements[e.Id];
                c.Position = new Vector2(e.X, e.Y); c.Size = new Vector2(e.Width, e.Height);
            }
            _overlay.QueueRedraw();
            AcceptEvent();
        }
    }
    public void RefreshSelection() => _overlay?.QueueRedraw();
}
#endif
