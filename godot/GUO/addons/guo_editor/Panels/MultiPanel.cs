#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using GUO.Assets;

/// <summary>
/// Multis (plan §4.6 panel 5): every multi through
/// <see cref="MultiLoader.GetMultis"/>, its components, and a composite of
/// their art.
/// </summary>
/// <remarks>
/// The composite is a preview, not the world renderer: components are sorted
/// by <c>X + Y</c> then <c>Z</c> and placed on the 44x44 diamond grid with
/// <c>Z * 4</c> lift, which is how UO lays statics out. Phase 2's World tab
/// draws multis through <c>GameScene</c> itself.
/// </remarks>
[Tool]
public partial class MultiPanel : GridPanel
{
    private List<int> _ids;

    public override string SmokeQuery => "0x0064";

    protected override int IconSize => 0;

    protected override string Placeholder => "multi id (0x0064, 100)";

    protected override IEnumerable<int> Ids()
    {
        if (_ids == null)
        {
            _ids = new List<int>();
            for (int i = 0; i < MultiLoader.MAX_MULTI_DATA_INDEX_COUNT; i++)
            {
                try
                {
                    if (Data.Files.Multis.GetMultis((uint)i).Count > 0)
                    {
                        _ids.Add(i);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        return _ids;
    }

    protected override string Caption(int id) => $"0x{id:X4}  ({Data.Files.Multis.GetMultis((uint)id).Count} parts)";

    protected override bool Matches(int id, string query) => false;

    protected override Inspection Describe(int id)
    {
        List<MultiInfo> parts = Data.Files.Multis.GetMultis((uint)id);
        Image composite = Composite(Data, parts);

        var sb = new StringBuilder();
        sb.Append($"[b]Multi 0x{id:X4}[/b] ({id})   {parts.Count} components\n");
        if (parts.Count > 0)
        {
            sb.Append($"x {parts.Min(p => p.X)}..{parts.Max(p => p.X)}   y {parts.Min(p => p.Y)}..{parts.Max(p => p.Y)}   z {parts.Min(p => p.Z)}..{parts.Max(p => p.Z)}\n");
        }

        foreach (var group in parts.GroupBy(p => p.ID).OrderByDescending(g => g.Count()).Take(24))
        {
            uint index = EditorData.LandCount + group.Key;
            sb.Append($"  0x{group.Key:X4} x{group.Count()}  {Data.NameOf(index)}\n");
        }

        return Inspection.Still("Multis", $"0x{id:X4}", composite, sb.ToString());
    }

    /// <summary>The composite for a multi id, or null. Also the Parity panel's GUO side.</summary>
    public static Image CompositeOf(EditorData data, int id) => Composite(data, data.Files.Multis.GetMultis((uint)id));

    private static Image Composite(EditorData data, List<MultiInfo> parts)
    {
        var placed = new List<(Image img, int x, int y)>();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        foreach (MultiInfo p in parts.Where(p => p.IsVisible).OrderBy(p => p.X + p.Y).ThenBy(p => p.Z))
        {
            Image art = data.ArtImage(EditorData.LandCount + p.ID);
            if (art == null)
            {
                continue;
            }

            int w = art.GetWidth(), h = art.GetHeight();
            int x = (p.X - p.Y) * 22 + 22 - w / 2;
            int y = (p.X + p.Y) * 22 - p.Z * 4 + 44 - h;
            placed.Add((art, x, y));
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + w);
            maxY = Math.Max(maxY, y + h);
        }

        if (placed.Count == 0)
        {
            return null;
        }

        // Very large multis are clipped rather than allocating a huge image.
        int cw = Math.Min(maxX - minX, 4096), ch = Math.Min(maxY - minY, 4096);
        Image canvas = Image.CreateEmpty(cw, ch, false, Image.Format.Rgba8);
        foreach (var (img, x, y) in placed)
        {
            canvas.BlendRect(img, new Rect2I(0, 0, img.GetWidth(), img.GetHeight()), new Vector2I(x - minX, y - minY));
        }

        return canvas;
    }
}
#endif
