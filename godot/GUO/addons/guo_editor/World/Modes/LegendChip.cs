#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using Godot;

/// <summary>
/// A small chip in the corner of the World view naming the active render
/// mode and what its colours mean (ADR-0027), plus the hovered cell's text.
/// Never takes the pointer.
/// </summary>
[Tool]
public partial class LegendChip : Control
{
    private string _title = "";
    private IReadOnlyList<LegendItem> _items = System.Array.Empty<LegendItem>();
    private string _hover = "";

    /// <summary>Number of legend rows shown, for the smoke check.</summary>
    internal int Rows => _items.Count;

    public string Title => _title;

    public LegendChip()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    internal void Set(string title, IReadOnlyList<LegendItem> items, string hover)
    {
        if (title == _title && hover == _hover && SameItems(items))
        {
            return;
        }

        _title = title;
        _items = items;
        _hover = hover;
        Visible = title.Length > 0;
        QueueRedraw();
    }

    private bool SameItems(IReadOnlyList<LegendItem> items)
    {
        if (items.Count != _items.Count)
        {
            return false;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] != _items[i])
            {
                return false;
            }
        }

        return true;
    }

    public override void _Draw()
    {
        if (_title.Length == 0)
        {
            return;
        }

        Font font = ThemeDB.FallbackFont;
        const int size = 13, row = 18;
        string[] hover = _hover.Length == 0 ? System.Array.Empty<string>() : _hover.Split('\n');
        float w = font.GetStringSize(_title, HorizontalAlignment.Left, -1, size + 1).X;
        foreach (LegendItem i in _items)
        {
            w = Mathf.Max(w, font.GetStringSize(i.Label, HorizontalAlignment.Left, -1, size).X + 22);
        }

        foreach (string h in hover)
        {
            w = Mathf.Max(w, font.GetStringSize(h, HorizontalAlignment.Left, -1, size).X);
        }

        float height = row * (1 + _items.Count + hover.Length) + 12;
        DrawRect(new Rect2(0, 0, w + 16, height), new Color(0.07f, 0.07f, 0.09f, 0.88f));
        DrawRect(new Rect2(0, 0, w + 16, height), new Color(0.78f, 0.62f, 0.3f), false, 1f);
        float y = 16;
        DrawString(font, new Vector2(8, y), _title, HorizontalAlignment.Left, -1, size + 1, new Color(1f, 0.9f, 0.5f));
        foreach (LegendItem i in _items)
        {
            y += row;
            DrawRect(new Rect2(8, y - 11, 12, 12), i.Colour);
            DrawRect(new Rect2(8, y - 11, 12, 12), Colors.Black, false, 1f);
            DrawString(font, new Vector2(26, y), i.Label, HorizontalAlignment.Left, -1, size, Colors.White);
        }

        foreach (string h in hover)
        {
            y += row;
            DrawString(font, new Vector2(8, y), h, HorizontalAlignment.Left, -1, size, new Color(0.8f, 0.9f, 1f));
        }
    }
}
#endif
