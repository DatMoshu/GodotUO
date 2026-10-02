#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using GUO.Assets;
using GUO.IO;

/// <summary>The kinds of asset a world project can replace (ADR-0020).</summary>
public enum AssetKind
{
    Land,
    Static,
    Gump,
    Hue,
}

/// <summary>
/// The asset half of a world project (docs/editor_plan.md phase 5, ADR-0020):
/// replaced land art, static art and gumps as PNG files, and replaced hues as
/// JSON, under <c>&lt;project&gt;/assets/</c>.
/// </summary>
/// <remarks>
/// <para>
/// On disk (docs/data_formats.md §11): <c>assets/art/land/0xNNNN.png</c>,
/// <c>assets/art/statics/0xNNNN.png</c>, <c>assets/gumps/0xNNNN.png</c> and
/// <c>assets/hues/0xNNNN.json</c>. A PNG is stored already reduced to UO's
/// 15-bit colour, so what the file shows is exactly what the client will draw.
/// </para>
/// <para>
/// Applying works like the verdata patch layer: each replaced image is encoded
/// into a scratch file under the project's <c>.cache</c>, in the client's own
/// layout, and its <see cref="UOFileIndex"/> is repointed there (the loaders
/// read through <c>entry.File</c> when it is set). Hues are written into
/// <see cref="HuesLoader.HuesRange"/>. No ported file changes and nothing is
/// written under <c>UO_CLIENT_DATA</c>.
/// </para>
/// </remarks>
public sealed class AssetOverlay
{
    public const int Format = 1;
    public const int MaxStaticSize = 1024;
    public const int MaxGumpSize = 2048;

    /// <summary>What opaque black becomes: 0 is transparent in statics and gumps.</summary>
    public const ushort NearBlack = 0x0421;

    public string Root { get; }

    public AssetOverlay(string projectRoot)
    {
        Root = Path.GetFullPath(projectRoot);
    }

    public static string Folder(AssetKind kind) => kind switch
    {
        AssetKind.Land => Path.Combine("assets", "art", "land"),
        AssetKind.Static => Path.Combine("assets", "art", "statics"),
        AssetKind.Gump => "assets" + Path.DirectorySeparatorChar + "gumps",
        _ => "assets" + Path.DirectorySeparatorChar + "hues",
    };

    public string PathOf(AssetKind kind, int id) =>
        Path.Combine(Root, Folder(kind), $"0x{id:X4}" + (kind == AssetKind.Hue ? ".json" : ".png"));

    /// <summary>The same file as the world project names it ("assets/art/statics/0x0E75.png"): what the UI shows, never the machine's full path.</summary>
    public string RelativePathOf(AssetKind kind, int id) =>
        Path.GetRelativePath(Root, PathOf(kind, id)).Replace(Path.DirectorySeparatorChar, '/');

    public bool Has(AssetKind kind, int id) => File.Exists(PathOf(kind, id));

    /// <summary>Every id the project replaces for a kind, ascending.</summary>
    public List<int> Ids(AssetKind kind)
    {
        var ids = new List<int>();
        string dir = Path.Combine(Root, Folder(kind));
        if (!Directory.Exists(dir))
        {
            return ids;
        }

        foreach (string f in Directory.GetFiles(dir, kind == AssetKind.Hue ? "*.json" : "*.png"))
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (n.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(n[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int id))
            {
                ids.Add(id);
            }
        }

        ids.Sort();
        return ids;
    }

    public int Count => Ids(AssetKind.Land).Count + Ids(AssetKind.Static).Count
                        + Ids(AssetKind.Gump).Count + Ids(AssetKind.Hue).Count;

    // --- importing ---------------------------------------------------------

    /// <summary>
    /// Imports an image as the replacement for an art or gump id. Returns null
    /// on success, or why it was refused. The file written is the image
    /// reduced to UO colour, so a reload shows exactly what was saved.
    /// </summary>
    public string Import(AssetKind kind, int id, Image source)
    {
        if (kind == AssetKind.Hue)
        {
            return "use ImportHue for hues";
        }

        if (source == null || source.IsEmpty())
        {
            return "no image";
        }

        Image img = (Image)source.Duplicate();
        if (img.IsCompressed())
        {
            img.Decompress();
        }

        img.Convert(Image.Format.Rgba8);
        int w = img.GetWidth(), h = img.GetHeight();

        switch (kind)
        {
            case AssetKind.Land when w != 44 || h != 44:
                return $"land art is 44x44; this image is {w}x{h}";
            case AssetKind.Land when id < 0 || id >= EditorData.LandCount:
                return $"land ids run 0x0000-0x{EditorData.LandCount - 1:X4}";
            case AssetKind.Static when w > MaxStaticSize || h > MaxStaticSize:
                return $"static art is at most {MaxStaticSize}x{MaxStaticSize}; this image is {w}x{h}";
            case AssetKind.Gump when w > MaxGumpSize || h > MaxGumpSize:
                return $"a gump is at most {MaxGumpSize}x{MaxGumpSize}; this image is {w}x{h}";
        }

        ushort[] px = ToUo(img, kind == AssetKind.Land);
        if (kind == AssetKind.Static && EncodeStatic(px, w, h) == null)
        {
            return "too much detail for one static (its row table overflows)";
        }

        string path = PathOf(kind, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Error err = FromUo(px, w, h, kind == AssetKind.Land).SavePng(path);
        return err == Godot.Error.Ok ? null : $"could not write {path}: {err}";
    }

    /// <summary>Imports a hue: 32 colours, from the first row of an image at least 32 wide.</summary>
    public string ImportHue(int hue, Image source, string name, ushort tableStart, ushort tableEnd)
    {
        if (source == null || source.IsEmpty() || source.GetWidth() < 32)
        {
            return "a hue image is a strip at least 32 pixels wide (one colour per pixel of the first row)";
        }

        Image img = (Image)source.Duplicate();
        if (img.IsCompressed())
        {
            img.Decompress();
        }

        img.Convert(Image.Format.Rgba8);
        var colors = new ushort[32];
        for (int i = 0; i < 32; i++)
        {
            colors[i] = To16(img.GetPixel(i, 0), land: true);
        }

        WriteHue(hue, name, colors, tableStart, tableEnd);
        return null;
    }

    public void WriteHue(int hue, string name, ushort[] colors, ushort tableStart, ushort tableEnd)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"format\": {Format},\n");
        sb.Append($"  \"hue\": {hue},\n");
        sb.Append($"  \"name\": {JsonValue.Create(name ?? "").ToJsonString()},\n");
        sb.Append($"  \"table_start\": \"0x{tableStart:X4}\",\n");
        sb.Append($"  \"table_end\": \"0x{tableEnd:X4}\",\n");
        sb.Append("  \"colors\": [\n");
        for (int row = 0; row < 4; row++)
        {
            sb.Append("    ");
            for (int i = 0; i < 8; i++)
            {
                sb.Append($"\"{colors[row * 8 + i]:X4}\"");
                if (row * 8 + i < 31)
                {
                    sb.Append(i < 7 ? ", " : ",");
                }
            }

            sb.Append('\n');
        }

        sb.Append("  ]\n}\n");
        string path = PathOf(AssetKind.Hue, hue);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, sb.ToString());
    }

    public (string Name, ushort[] Colors, ushort Start, ushort End) ReadHue(int hue)
    {
        JsonNode j = JsonNode.Parse(File.ReadAllText(PathOf(AssetKind.Hue, hue)));
        ushort[] colors = j["colors"].AsArray().Select(c => ushort.Parse((string)c, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        return ((string)j["name"], colors, Hex((string)j["table_start"]), Hex((string)j["table_end"]));
    }

    private static ushort Hex(string s) =>
        ushort.Parse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Removes a replacement; the install's asset shows again once re-applied.</summary>
    public bool Revert(AssetKind kind, int id)
    {
        string path = PathOf(kind, id);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>A replaced image as saved (already in UO colour), or null.</summary>
    public Image Load(AssetKind kind, int id)
    {
        string path = PathOf(kind, id);
        if (kind == AssetKind.Hue || !File.Exists(path))
        {
            return null;
        }

        Image img = Image.LoadFromFile(path);
        img?.Convert(Image.Format.Rgba8);
        return img;
    }

    // --- colour ------------------------------------------------------------

    /// <summary>
    /// One pixel to UO's 15-bit colour. Statics and gumps keep 0 for
    /// transparent (alpha below half), so opaque black becomes
    /// <see cref="NearBlack"/>. Land has no transparency: 0 is black.
    /// </summary>
    public static ushort To16(Color c, bool land)
    {
        if (!land && c.A8 < 128)
        {
            return 0;
        }

        ushort v = (ushort)(((c.R8 >> 3) << 10) | ((c.G8 >> 3) << 5) | (c.B8 >> 3));
        return v == 0 && !land ? NearBlack : v;
    }

    public static ushort[] ToUo(Image img, bool land)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        var px = new ushort[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                px[y * w + x] = land && !InDiamond(x, y) ? (ushort)0 : To16(img.GetPixel(x, y), land);
            }
        }

        return px;
    }

    /// <summary>UO colour back to an image, through the loaders' own 5-to-8-bit table.</summary>
    public static Image FromUo(ushort[] px, int w, int h, bool land)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bool opaque = land ? InDiamond(i % w, i / w) : px[i] != 0;
            if (!opaque)
            {
                continue;
            }

            uint c = GUO.Utility.HuesHelper.Color16To32(px[i]);
            rgba[i * 4 + 0] = (byte)(c & 0xFF);
            rgba[i * 4 + 1] = (byte)((c >> 8) & 0xFF);
            rgba[i * 4 + 2] = (byte)((c >> 16) & 0xFF);
            rgba[i * 4 + 3] = 0xFF;
        }

        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
    }

    /// <summary>True for the 1,012 pixels of the 44x44 square a land tile stores.</summary>
    public static bool InDiamond(int x, int y)
    {
        if (y < 22)
        {
            int start = 21 - y;
            return x >= start && x < start + 2 * (y + 1);
        }

        int i = y - 22;
        return x >= i && x < i + 2 * (22 - i);
    }

    // --- encoding (the client's layouts; tools/guo/uoart.py mirrors these) ---

    /// <summary>Land: the diamond's 1,012 colours, row by row. 2,024 bytes.</summary>
    public static byte[] EncodeLand(ushort[] px)
    {
        var o = new List<byte>(2024);
        for (int y = 0; y < 44; y++)
        {
            for (int x = 0; x < 44; x++)
            {
                if (InDiamond(x, y))
                {
                    ushort c = px[y * 44 + x];
                    o.Add((byte)c);
                    o.Add((byte)(c >> 8));
                }
            }
        }

        return o.ToArray();
    }

    /// <summary>
    /// Static art: <c>uint flags, ushort w, ushort h</c>, a row table of
    /// ushort offsets (in words, from the end of the table), then per row
    /// <c>(gap, run, colours...)</c> spans ended by <c>(0, 0)</c>. Null when the
    /// row table would overflow 16 bits.
    /// </summary>
    public static byte[] EncodeStatic(ushort[] px, int w, int h)
    {
        var data = new List<ushort>();
        var rows = new ushort[h];
        for (int y = 0; y < h; y++)
        {
            if (data.Count > ushort.MaxValue)
            {
                return null;
            }

            rows[y] = (ushort)data.Count;
            int x = 0, at = 0;
            while (x < w)
            {
                if (px[y * w + x] == 0)
                {
                    x++;
                    continue;
                }

                int start = x;
                while (x < w && px[y * w + x] != 0)
                {
                    x++;
                }

                data.Add((ushort)(start - at));
                data.Add((ushort)(x - start));
                for (int i = start; i < x; i++)
                {
                    data.Add(px[y * w + i]);
                }

                at = x;
            }

            data.Add(0);
            data.Add(0);
        }

        var o = new byte[8 + h * 2 + data.Count * 2];
        BitConverter.TryWriteBytes(o.AsSpan(0), 0u);
        BitConverter.TryWriteBytes(o.AsSpan(4), (ushort)w);
        BitConverter.TryWriteBytes(o.AsSpan(6), (ushort)h);
        for (int y = 0; y < h; y++)
        {
            BitConverter.TryWriteBytes(o.AsSpan(8 + y * 2), rows[y]);
        }

        for (int i = 0; i < data.Count; i++)
        {
            BitConverter.TryWriteBytes(o.AsSpan(8 + h * 2 + i * 2), data[i]);
        }

        return o;
    }

    /// <summary>
    /// A gump: a row table of int32 offsets (in 4-byte units, from the start),
    /// then per row <c>(colour, run)</c> pairs covering the whole width; colour
    /// 0 is transparent. Its size comes from the index entry, not the data.
    /// </summary>
    public static byte[] EncodeGump(ushort[] px, int w, int h)
    {
        var pairs = new List<(ushort c, ushort n)>[h];
        int total = h;
        for (int y = 0; y < h; y++)
        {
            var row = new List<(ushort, ushort)>();
            int x = 0;
            while (x < w)
            {
                ushort c = px[y * w + x];
                int start = x;
                while (x < w && px[y * w + x] == c && x - start < ushort.MaxValue)
                {
                    x++;
                }

                row.Add((c, (ushort)(x - start)));
            }

            pairs[y] = row;
            total += row.Count;
        }

        var o = new byte[total * 4];
        int at = h;
        for (int y = 0; y < h; y++)
        {
            BitConverter.TryWriteBytes(o.AsSpan(y * 4), at);
            foreach ((ushort c, ushort n) in pairs[y])
            {
                BitConverter.TryWriteBytes(o.AsSpan(at * 4), c);
                BitConverter.TryWriteBytes(o.AsSpan(at * 4 + 2), n);
                at++;
            }
        }

        return o;
    }

    // --- applying ------------------------------------------------------------

    /// <summary>
    /// Lays every replacement over a loaded <see cref="UOFileManager"/>.
    /// Dispose (or <see cref="Applied.Restore"/>) the result to put the
    /// install's entries back. Re-applying means restoring the old result first.
    /// </summary>
    public Applied Apply(UOFileManager files, string tag)
    {
        var a = new Applied(files);
        string cache = Path.Combine(Root, ".cache");
        Directory.CreateDirectory(cache);
        string path = Path.Combine(cache, $"overlay_assets_{tag}_{DateTime.UtcNow.Ticks:x}.bin");

        var art = new List<(int index, long at, int len)>();
        var gumps = new List<(int index, long at, int len, int w, int h)>();
        using (var w = new BinaryWriter(File.Create(path)))
        {
            foreach (AssetKind kind in new[] { AssetKind.Land, AssetKind.Static, AssetKind.Gump })
            {
                foreach (int id in Ids(kind))
                {
                    Image img = Load(kind, id);
                    if (img == null)
                    {
                        continue;
                    }

                    int iw = img.GetWidth(), ih = img.GetHeight();
                    ushort[] px = ToUo(img, kind == AssetKind.Land);
                    byte[] bytes = kind switch
                    {
                        AssetKind.Land => iw == 44 && ih == 44 ? EncodeLand(px) : null,
                        AssetKind.Static => EncodeStatic(px, iw, ih),
                        _ => EncodeGump(px, iw, ih),
                    };

                    if (bytes == null)
                    {
                        GD.PrintErr($"[GUO editor] {kind} 0x{id:X4}: {PathOf(kind, id)} cannot be encoded; skipped");
                        continue;
                    }

                    long at = w.BaseStream.Position;
                    w.Write(bytes);
                    if (kind == AssetKind.Gump)
                    {
                        gumps.Add((id, at, bytes.Length, iw, ih));
                    }
                    else
                    {
                        art.Add((kind == AssetKind.Land ? id : (int)EditorData.LandCount + id, at, bytes.Length));
                    }
                }
            }

            // ArtLoader.LoadArt reads Length bytes after the 8-byte header,
            // so the last static would read past the end of the file.
            w.Write(new byte[16]);
        }

        a.Reader = new UOFileMul(path);
        UOFileIndex[] artEntries = files.Arts.File.Entries;
        foreach ((int index, long at, int len) in art)
        {
            if (index >= artEntries.Length)
            {
                GD.PrintErr($"[GUO editor] art index 0x{index:X5} is beyond this install's {artEntries.Length} entries; skipped");
                continue;
            }

            a.Art[index] = artEntries[index];
            artEntries[index] = new UOFileIndex(a.Reader, at, len, 0);
        }

        UOFileIndex[] gumpEntries = files.Gumps.File.Entries;
        foreach ((int index, long at, int len, int gw, int gh) in gumps)
        {
            if (index >= gumpEntries.Length)
            {
                GD.PrintErr($"[GUO editor] gump 0x{index:X4} is beyond this install's {gumpEntries.Length} entries; skipped");
                continue;
            }

            a.Gumps[index] = gumpEntries[index];
            gumpEntries[index] = new UOFileIndex(a.Reader, at, len, 0, 0, gw, gh);
        }

        HuesGroup[] groups = files.Hues.HuesRange;
        foreach (int hue in Ids(AssetKind.Hue))
        {
            int h = hue - 1;
            if (h < 0 || (h >> 3) >= groups.Length)
            {
                GD.PrintErr($"[GUO editor] hue {hue} is outside hues.mul; skipped");
                continue;
            }

            var (name, colors, start, end) = ReadHue(hue);
            ref HuesBlock b = ref groups[h >> 3].Entries[h & 7];
            a.Hues[hue] = b;
            for (int i = 0; i < 32; i++)
            {
                b.ColorTable[i] = colors[i];
            }

            b.TableStart = start;
            b.TableEnd = end;
            SetName(ref b, name);
        }

        return a;
    }

    private static unsafe void SetName(ref HuesBlock b, string name)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(name ?? "");
        for (int i = 0; i < 20; i++)
        {
            b.Name[i] = i < ascii.Length ? ascii[i] : (byte)0;
        }
    }

    /// <summary>One application of the overlay to one file manager.</summary>
    public sealed class Applied : IDisposable
    {
        private readonly UOFileManager _files;
        internal UOFileMul Reader;
        internal readonly Dictionary<int, UOFileIndex> Art = new();
        internal readonly Dictionary<int, UOFileIndex> Gumps = new();
        internal readonly Dictionary<int, HuesBlock> Hues = new();

        internal Applied(UOFileManager files)
        {
            _files = files;
        }

        public IEnumerable<int> ArtIndices => Art.Keys;
        public IEnumerable<int> GumpIds => Gumps.Keys;
        public IEnumerable<int> HueIds => Hues.Keys;

        /// <summary>Puts every repointed entry and hue back as the install had it.</summary>
        public void Restore()
        {
            foreach (var (i, e) in Art)
            {
                _files.Arts.File.Entries[i] = e;
            }

            foreach (var (i, e) in Gumps)
            {
                _files.Gumps.File.Entries[i] = e;
            }

            foreach (var (hue, b) in Hues)
            {
                int h = hue - 1;
                _files.Hues.HuesRange[h >> 3].Entries[h & 7] = b;
            }

            Art.Clear();
            Gumps.Clear();
            Hues.Clear();
        }

        public void Dispose()
        {
            Restore();
            if (Reader != null)
            {
                string p = Reader.FilePath;
                Reader.Dispose();
                Reader = null;
                try
                {
                    File.Delete(p);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
#endif
