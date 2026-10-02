#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
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

        return _ids;
    }

    private unsafe string Name(int hue)
    {
        ref HuesBlock b = ref Block(hue);
        fixed (byte* p = b.Name)
        {
            int n = 0;
            while (n < 20 && p[n] != 0)
            {
                n++;
            }

            return Encoding.ASCII.GetString(p, n).Trim();
        }
    }

    /// <summary>A hue's name from hues.mul, for F3's name search.</summary>
    public string HueName(int hue) => Name(hue);

    protected override string Caption(int id) => $"{id:X4}";

    protected override string Tooltip(int id) => $"0x{id:X4} {Name(id)}";

    protected override bool Matches(int id, string query) =>
        Name(id).Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override Image Icon(int id) => Swatch(id, 1, 4);

    /// <summary>The 32 colours of a hue, <paramref name="cell"/> pixels each.</summary>
    private Image Swatch(int hue, int cellW, int height)
    {
        ref HuesBlock b = ref Block(hue);
        Image img = Image.CreateEmpty(32 * cellW, height, false, Image.Format.Rgba8);
        for (int i = 0; i < 32; i++)
        {
            uint c = GUO.Utility.HuesHelper.Color16To32(b.ColorTable[i]);
            img.FillRect(new Rect2I(i * cellW, 0, cellW, height), ToColor(c));
        }

        return img;
    }

    protected override Inspection Describe(int id)
    {
        ref HuesBlock b = ref Block(id);
        Image swatch = Swatch(id, 6, 24);

        uint art = Data.CurrentArt;
        Image plain = Data.ArtImage(art);
        Image full = plain != null ? Hued(plain, (ushort)id, partial: false) : null;
        Image part = plain != null ? Hued(plain, (ushort)id, partial: true) : null;

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
        AssetActions.AddHue(ins, Data, id, Swatch(id, 1, 1), Name(id), b.TableStart, b.TableEnd);
        return ins;
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
