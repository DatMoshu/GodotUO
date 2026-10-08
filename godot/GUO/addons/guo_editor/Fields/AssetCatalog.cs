#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using GUO.Assets;
using GUO.IO;

/// <summary>What an <see cref="AssetField"/> holds.</summary>
public enum AssetPickKind
{
    Gump,
    Static,
    Land,
    Hue,
    Sound,
    Music,
    Cliloc,
    Multi,
}

/// <summary>One suggestion: an id, its name, and how well it matched.</summary>
public readonly record struct AssetHit(int Id, string Name, int Score);

/// <summary>
/// What every <see cref="AssetField"/> searches: for each kind, the ids the
/// install has, a name for each (tiledata, hues.mul, the sound index, the
/// cliloc table, the UI classes that name a gump), a thumbnail, and the ids
/// picked lately. Built a kind at a time, the first time a field of that kind
/// asks, on the main thread (the loaders are single threaded).
/// </summary>
public sealed class AssetCatalog
{
    private const int MaxRecent = 12;
    private const int MaxThumbs = 400;
    private const int MaxCliloc = 3_200_000;
    private const string RecentSection = "guo_asset_field";

    private static AssetCatalog _shared;

    private readonly EditorData _data;
    private readonly Dictionary<AssetPickKind, List<int>> _ids = new();
    private readonly Dictionary<AssetPickKind, string[]> _names = new();
    private readonly Dictionary<(AssetPickKind, int), Texture2D> _thumbs = new();
    private int _overlayCount;

    private AssetCatalog(EditorData data)
    {
        _data = data;
        _data.Loaded += Forget;
        _data.AssetsApplied += Forget;
    }

    /// <summary>The catalog for the editor's open client data; one per data set.</summary>
    public static AssetCatalog Of(EditorData data)
    {
        if (data == null)
        {
            return null;
        }

        if (_shared == null || _shared._data != data)
        {
            _shared = new AssetCatalog(data);
        }

        return _shared;
    }

    public bool Ready => _data.IsLoaded;

    /// <summary>Drops everything built: the data was loaded again or an import changed it.</summary>
    public void Forget()
    {
        _ids.Clear();
        _names.Clear();
        _thumbs.Clear();
    }

    /// <summary>The word for a kind in captions: "gump", "static", "hue".</summary>
    public static string Noun(AssetPickKind kind) => kind switch
    {
        AssetPickKind.Gump => "gump",
        AssetPickKind.Static => "static",
        AssetPickKind.Land => "land tile",
        AssetPickKind.Hue => "hue",
        AssetPickKind.Sound => "sound",
        AssetPickKind.Music => "music track",
        AssetPickKind.Cliloc => "cliloc",
        _ => "multi",
    };

    /// <summary>How an id is written: cliloc numbers in decimal, everything else in UO's 0x0000 hex.</summary>
    public static string Format(AssetPickKind kind, int id) =>
        kind == AssetPickKind.Cliloc ? id.ToString(CultureInfo.InvariantCulture) : $"0x{id:X4}";

    /// <summary>"0x0E75", "3701" or "e75h"; null for anything else.</summary>
    public static int? ParseNumber(string text)
    {
        text = text.Trim();
        if (text.EndsWith('h') && text.Length > 1 && int.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int h))
        {
            return h;
        }

        return SearchQuery.TryNumber(text, out long v) && v <= int.MaxValue ? (int)v : null;
    }

    /// <summary>Every id of a kind that the install has, ascending.</summary>
    public List<int> Ids(AssetPickKind kind)
    {
        if (!Ready)
        {
            return new List<int>();
        }

        // Multis written to a stage this session join the list (MultiLoader's editor overlay).
        if (kind == AssetPickKind.Multi && _ids.ContainsKey(kind) && (MultiLoader.EditorOverlay?.Count ?? 0) != _overlayCount)
        {
            _ids.Remove(kind);
        }

        if (!_ids.TryGetValue(kind, out List<int> ids))
        {
            Build(kind);
            ids = _ids[kind];
        }

        return ids;
    }

    public bool Exists(AssetPickKind kind, int id) => Ids(kind).BinarySearch(id) >= 0;

    /// <summary>The id's name, or "" when the data has none (most gumps, every multi).</summary>
    public string Name(AssetPickKind kind, int id)
    {
        List<int> ids = Ids(kind);
        int at = ids.BinarySearch(id);
        return at >= 0 ? _names[kind][at] : "";
    }

    private void Build(AssetPickKind kind)
    {
        var ids = new List<int>();
        var names = new List<string>();
        UOFileManager files = _data.Files;
        switch (kind)
        {
            case AssetPickKind.Gump:
                UOFileIndex[] entries = files.Gumps.File.Entries;
                for (int i = 0; i < entries.Length; i++)
                {
                    if (files.Gumps.File.GetValidRefEntry(i).Length > 0)
                    {
                        ids.Add(i);
                        names.Add(GumpPanel.NameOf(i));
                    }
                }

                break;
            case AssetPickKind.Static:
            case AssetPickKind.Land:
                bool land = kind == AssetPickKind.Land;
                int count = land ? (int)EditorData.LandCount : Math.Min(files.Arts.File.Entries.Length - (int)EditorData.LandCount, 0x10000);
                for (int id = 0; id < count; id++)
                {
                    uint index = land ? (uint)id : EditorData.LandCount + (uint)id;
                    if (_data.HasArt(index))
                    {
                        ids.Add(id);
                        names.Add(_data.NameOf(index).Replace("%", "").Trim());
                    }
                }

                break;
            case AssetPickKind.Hue:
                int hues = Math.Min(files.Hues.HuesCount, files.Hues.HuesRange.Length * 8);
                for (int hue = 1; hue <= hues; hue++)
                {
                    ids.Add(hue);
                    names.Add(HueName(hue));
                }

                break;
            case AssetPickKind.Sound:
                for (int i = 0; i < 0x1000; i++)
                {
                    if (files.Sounds.TryGetSound(i, out byte[] data, out string name) && data?.Length > 0)
                    {
                        ids.Add(i);
                        names.Add((name ?? "").TrimEnd('\0', ' '));
                    }
                }

                break;
            case AssetPickKind.Music:
                for (int i = 0; i < 0x100; i++)
                {
                    if (files.Sounds.TryGetMusicData(i, out string name, out _))
                    {
                        ids.Add(i);
                        names.Add(name ?? "");
                    }
                }

                break;
            case AssetPickKind.Cliloc:
                ClilocLoader cl = files.Clilocs;
                for (int n = 0; n < MaxCliloc; n++)
                {
                    string t = cl.GetString(n, "");
                    if (t.Length > 0)
                    {
                        ids.Add(n);
                        names.Add(t.Replace('\n', ' '));
                    }
                }

                break;
            case AssetPickKind.Multi:
                for (int i = 0; i < MultiLoader.MAX_MULTI_DATA_INDEX_COUNT; i++)
                {
                    try
                    {
                        int parts = files.Multis.GetMultis((uint)i).Count;
                        if (parts > 0)
                        {
                            ids.Add(i);
                            names.Add($"{parts} parts");
                        }
                    }
                    catch (Exception)
                    {
                        // A broken multi entry is the data's problem; leave it out.
                    }
                }

                _overlayCount = MultiLoader.EditorOverlay?.Count ?? 0;
                if (MultiLoader.EditorOverlay != null)
                {
                    foreach (uint staged in MultiLoader.EditorOverlay.Keys)
                    {
                        if (!ids.Contains((int)staged))
                        {
                            int at = ~ids.BinarySearch((int)staged);
                            ids.Insert(at, (int)staged);
                            names.Insert(at, "staged this session");
                        }
                    }
                }

                break;
        }

        _ids[kind] = ids;
        _names[kind] = names.ToArray();
    }

    private ref HuesBlock Hue(int hue)
    {
        int h = hue - 1;
        return ref _data.Files.Hues.HuesRange[h >> 3].Entries[h % 8];
    }

    private unsafe string HueName(int hue)
    {
        ref HuesBlock b = ref Hue(hue);
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

    /// <summary>A hue's 32 colours, <paramref name="cell"/> pixels each, <paramref name="height"/> tall.</summary>
    public Image HueStrip(int hue, int cell, int height)
    {
        if (!Ready || hue < 1 || !Exists(AssetPickKind.Hue, hue))
        {
            return null;
        }

        ref HuesBlock b = ref Hue(hue);
        Image img = Image.CreateEmpty(32 * cell, height, false, Image.Format.Rgba8);
        for (int i = 0; i < 32; i++)
        {
            uint c = GUO.Utility.HuesHelper.Color16To32(b.ColorTable[i]);
            img.FillRect(new Rect2I(i * cell, 0, cell, height), Color.Color8((byte)c, (byte)(c >> 8), (byte)(c >> 16)));
        }

        return img;
    }

    /// <summary>
    /// The picture for an id, decoded once and kept (up to a few hundred).
    /// Null when the kind has no picture (cliloc) or the id has none.
    /// </summary>
    public Texture2D Thumb(AssetPickKind kind, int id)
    {
        if (!Ready)
        {
            return null;
        }

        if (_thumbs.TryGetValue((kind, id), out Texture2D t))
        {
            return t;
        }

        Image img = kind switch
        {
            AssetPickKind.Gump => _data.GumpImage(id),
            AssetPickKind.Static => _data.ArtImage(EditorData.LandCount + (uint)id),
            AssetPickKind.Land => _data.ArtImage((uint)id),
            AssetPickKind.Hue => HueStrip(id, 2, 12),
            AssetPickKind.Multi => SafeMulti(id),
            _ => null,
        };

        if (_thumbs.Count >= MaxThumbs)
        {
            _thumbs.Clear();
        }

        return _thumbs[(kind, id)] = img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    /// <summary>Whether this kind has pictures at all (the suggestion list leaves room for them).</summary>
    public static bool HasThumbs(AssetPickKind kind) => kind is not (AssetPickKind.Cliloc or AssetPickKind.Sound or AssetPickKind.Music);

    private Image SafeMulti(int id)
    {
        try
        {
            return MultiPanel.CompositeOf(_data, id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The best matches for what was typed: an exact id first, then ids whose
    /// hex or decimal digits start with it, then names holding every word.
    /// Nothing typed gives the recent picks, then the first ids.
    /// </summary>
    public List<AssetHit> Query(AssetPickKind kind, string text, int max)
    {
        var hits = new List<AssetHit>();
        if (!Ready)
        {
            return hits;
        }

        List<int> ids = Ids(kind);
        string[] names = _names[kind];
        text = (text ?? "").Trim();
        int[] recent = Recent(kind);

        if (text.Length == 0)
        {
            var seen = new HashSet<int>();
            foreach (int id in recent)
            {
                int at = ids.BinarySearch(id);
                if (at >= 0 && seen.Add(id))
                {
                    hits.Add(new AssetHit(id, names[at], 1));
                }
            }

            for (int i = 0; i < ids.Count && hits.Count < max; i++)
            {
                if (seen.Add(ids[i]))
                {
                    hits.Add(new AssetHit(ids[i], names[i], 0));
                }
            }

            return hits;
        }

        var scores = new Dictionary<int, int>();
        void Score(int index, int score)
        {
            if (Array.IndexOf(recent, ids[index]) >= 0)
            {
                score += 150;
            }

            if (!scores.TryGetValue(index, out int old) || old < score)
            {
                scores[index] = score;
            }
        }

        if (ParseNumber(text) is int exact && ids.BinarySearch(exact) is int e && e >= 0)
        {
            Score(e, 100_000);
        }

        // Digits typed so far: "0x0E7", "e7", "37" find the ids that start with them.
        string lower = text.ToLowerInvariant();
        string hex = lower.StartsWith("0x", StringComparison.Ordinal) ? lower[2..] : lower;
        bool hexDigits = hex.Length is > 0 and <= 6 && IsHex(hex);
        bool decDigits = !lower.StartsWith("0x", StringComparison.Ordinal) && lower.Length <= 9 && IsDec(lower);
        if (hexDigits || decDigits)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                if (hexDigits && kind != AssetPickKind.Cliloc
                    && (id.ToString("x4", CultureInfo.InvariantCulture).StartsWith(hex, StringComparison.Ordinal)
                        || id.ToString("x", CultureInfo.InvariantCulture).StartsWith(hex, StringComparison.Ordinal)))
                {
                    Score(i, 50_000 - Math.Min(id, 0xFFFF) / 16);
                }
                else if (decDigits && id.ToString(CultureInfo.InvariantCulture).StartsWith(lower, StringComparison.Ordinal))
                {
                    Score(i, 40_000 - Math.Min(id, 0xFFFF) / 16);
                }

                if (scores.Count > max * 20)
                {
                    break;
                }
            }
        }

        // Names: every word somewhere in the name; a whole name, then a start, then a shorter name wins.
        string[] words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && (kind != AssetPickKind.Cliloc || lower.Length >= 3))
        {
            int found = 0;
            for (int i = 0; i < names.Length && found < max * 50; i++)
            {
                string name = names[i];
                if (name.Length == 0)
                {
                    continue;
                }

                int score = 0;
                bool all = true;
                foreach (string w in words)
                {
                    int at = name.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                    if (at < 0)
                    {
                        all = false;
                        break;
                    }

                    score += name.Length == w.Length ? 900 : at == 0 ? 700 : name[at - 1] == ' ' ? 600 : 400;
                }

                if (all)
                {
                    found++;
                    Score(i, 10_000 + score / words.Length - Math.Min(name.Length * 2, 300));
                }
            }
        }

        var order = new List<KeyValuePair<int, int>>(scores);
        order.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : a.Key.CompareTo(b.Key));
        for (int n = 0; n < order.Count && n < max; n++)
        {
            int i = order[n].Key;
            hits.Add(new AssetHit(ids[i], names[i], order[n].Value));
        }

        return hits;
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDec(string s)
    {
        foreach (char c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return s.Length > 0;
    }

    /// <summary>The ids picked lately in fields of this kind, newest first (kept with the project's editor metadata).</summary>
    public static int[] Recent(AssetPickKind kind)
    {
        try
        {
            Variant v = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata(RecentSection, kind.ToString(), new int[0]);
            return v.VariantType == Variant.Type.PackedInt32Array ? v.AsInt32Array() : Array.Empty<int>();
        }
        catch (Exception)
        {
            return Array.Empty<int>();
        }
    }

    public static void Remember(AssetPickKind kind, int id)
    {
        var list = new List<int> { id };
        foreach (int r in Recent(kind))
        {
            if (r != id && list.Count < MaxRecent)
            {
                list.Add(r);
            }
        }

        try
        {
            EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata(RecentSection, kind.ToString(), list.ToArray());
        }
        catch (Exception)
        {
            // Recents are a convenience; a settings error never blocks a pick.
        }
    }
}
#endif
