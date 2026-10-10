#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using System.Linq;
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
    private string _meaning = "";

    /// <summary>Number of legend rows shown, for the smoke check.</summary>
    internal int Rows => _items.Count;

    /// <summary>The legend rows' words, for the smoke check.</summary>
    internal IEnumerable<string> Labels => _items.Select(i => i.Label);

    /// <summary>The size of the box last drawn (the control itself has no size; the tour marks the box).</summary>
    internal Vector2 DrawnSize { get; private set; }

    public string Title => _title;

    /// <summary>The plain sentence under the title saying what the colours mean (ED6).</summary>
    public string Meaning => _meaning;

    /// <summary>Text size in unscaled pixels: the editor's default UI size.</summary>
    internal const int FontSize = 16;

    public LegendChip()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    internal void Set(string title, IReadOnlyList<LegendItem> items, string hover, string meaning = "")
    {
        meaning ??= "";
        if (title == _title && hover == _hover && meaning == _meaning && SameItems(items))
        {
            return;
        }

        _title = title;
        _meaning = meaning;
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
        // Readable at a glance: the editor's own text size, scaled with the editor (ED5; 13 px read as tiny).
        float scale = Engine.IsEditorHint() ? EditorInterface.Singleton.GetEditorScale() : 1f;
        int size = Mathf.RoundToInt(FontSize * scale), row = Mathf.RoundToInt((FontSize + 6) * scale);
        string[] hover = _hover.Length == 0 ? System.Array.Empty<string>() : _hover.Split('\n');
        List<string> meaning = Wrap(font, _meaning, size, 380 * scale);
        float w = font.GetStringSize(_title, HorizontalAlignment.Left, -1, size + 1).X;
        foreach (string m in meaning)
        {
            w = Mathf.Max(w, font.GetStringSize(m, HorizontalAlignment.Left, -1, size).X);
        }

        foreach (LegendItem i in _items)
        {
            w = Mathf.Max(w, font.GetStringSize(i.Label, HorizontalAlignment.Left, -1, size).X + size + 6);
        }

        foreach (string h in hover)
        {
            w = Mathf.Max(w, font.GetStringSize(h, HorizontalAlignment.Left, -1, size).X);
        }

        float height = row * (1 + meaning.Count + _items.Count + hover.Length) + 12;
        DrawnSize = new Vector2(w + 16, height);
        Size = DrawnSize;
        DrawRect(new Rect2(0, 0, w + 16, height), new Color(0.07f, 0.07f, 0.09f, 0.88f));
        DrawRect(new Rect2(0, 0, w + 16, height), new Color(0.78f, 0.62f, 0.3f), false, 1f);
        float y = row - 2;
        DrawString(font, new Vector2(8, y), _title, HorizontalAlignment.Left, -1, size + 1, new Color(1f, 0.9f, 0.5f));
        foreach (string m in meaning)
        {
            y += row;
            DrawString(font, new Vector2(8, y), m, HorizontalAlignment.Left, -1, size, new Color(0.85f, 0.85f, 0.8f));
        }
        foreach (LegendItem i in _items)
        {
            y += row;
            float swatch = size - 2;
            DrawRect(new Rect2(8, y - swatch + 1, swatch, swatch), i.Colour);
            DrawRect(new Rect2(8, y - swatch + 1, swatch, swatch), Colors.Black, false, 1f);
            DrawString(font, new Vector2(14 + swatch, y), i.Label, HorizontalAlignment.Left, -1, size, Colors.White);
        }

        foreach (string h in hover)
        {
            y += row;
            DrawString(font, new Vector2(8, y), h, HorizontalAlignment.Left, -1, size, new Color(0.8f, 0.9f, 1f));
        }
    }

    /// <summary>Breaks a sentence into lines no wider than <paramref name="max"/> pixels, at spaces.</summary>
    private static List<string> Wrap(Font font, string text, int size, float max)
    {
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && font.GetStringSize(next, HorizontalAlignment.Left, -1, size).X > max)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = next;
            }
        }

        if (line.Length > 0)
        {
            lines.Add(line);
        }

        return lines;
    }
}
#endif
