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
/// The composite is a preview, not the world renderer: components are placed
/// on the 44x44 diamond grid with <c>Z * 4</c> lift and painted in the order
/// the client's depth buffer settles them: <c>(X + Y) + (127 + PriorityZ) *
/// 0.01</c>, with <c>PriorityZ</c> computed as <c>Chunk.AddGameObject</c>
/// does (Background -1, Height != 0 +1, MultiMovable +1), ties going to the
/// part <c>Chunk</c> would have put later on the tile. The World tab draws
/// multis through <c>GameScene</c> itself.
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

    /// <summary>
    /// The visible parts in the client's painting order. Mirrors
    /// <c>Chunk.AddGameObject</c> (priority Z) and <c>GameObject.CalculateDepthZ</c>
    /// (the sort key); within one tile, a Multi of equal priority is inserted
    /// before the existing ones, so later list entries paint first.
    /// </summary>
    internal static List<MultiInfo> ClientOrder(EditorData data, List<MultiInfo> parts)
    {
        StaticTiles[] tiles = data.Files.TileData.StaticData;
        var keyed = new List<(MultiInfo p, float depth, int seq)>();
        for (int i = 0; i < parts.Count; i++)
        {
            MultiInfo p = parts[i];
            if (!p.IsVisible)
            {
                continue;
            }

            int pz = p.Z;
            if (p.ID < tiles.Length)
            {
                StaticTiles t = tiles[p.ID];
                if (t.IsBackground)
                {
                    pz--;
                }

                if (t.Height != 0)
                {
                    pz++;
                }

                if (t.IsMultiMovable)
                {
                    pz++;
                }
            }

            keyed.Add((p, (p.X + p.Y) + (127 + pz) * 0.01f, i));
        }

        // Depth first; equal depth keeps the render list's order, which on a
        // tile is the reverse of insertion for equal-priority Multis.
        keyed.Sort((a, b) =>
        {
            int c = a.depth.CompareTo(b.depth);
            if (c != 0)
            {
                return c;
            }

            if (a.p.X == b.p.X && a.p.Y == b.p.Y)
            {
                return b.seq.CompareTo(a.seq);
            }

            c = a.p.Y.CompareTo(b.p.Y);
            return c != 0 ? c : a.seq.CompareTo(b.seq);
        });
        return keyed.Select(k => k.p).ToList();
    }

    private static Image Composite(EditorData data, List<MultiInfo> parts)
    {
        var placed = new List<(Image img, int x, int y)>();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        foreach (MultiInfo p in ClientOrder(data, parts))
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
