#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Godot;

/// <summary>
/// Shared by the UO data providers: they read only through the loaders the
/// editor already has open (<see cref="EditorData.Files"/>), and only on the
/// main thread, a slice at a time.
/// </summary>
public abstract class UoProvider : SearchProvider
{
    protected readonly SearchContext Ctx;

    protected UoProvider(SearchContext ctx)
    {
        Ctx = ctx;
    }

    protected EditorData Data => Ctx.Data;

    /// <summary>False until the client data has loaded; providers wait (and stay "indexing").</summary>
    protected bool DataReady => Data != null && Data.IsLoaded;

    protected static bool InRange(long id, long max) => id >= 0 && id < max;

    protected static long KeyId(string key, string kind) =>
        key.StartsWith(kind + ":", StringComparison.Ordinal) && SearchQuery.TryNumber(key[(kind.Length + 1)..], out long id) ? id : -1;
}

/// <summary>Art by tiledata name or id: statics and land.</summary>
public sealed class UoArtProvider : UoProvider
{
    private readonly List<SearchEntry> _entries = new();
    private int _next;
    private int _total = 1;
    private bool _done;

    public UoArtProvider(SearchContext ctx) : base(ctx)
    {
    }

    public override string Name => "art names";

    public override bool Done => _done;

    public override double Progress => _done ? 1 : (double)_next / _total;

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override void Step(double ms)
    {
        if (_done || !DataReady)
        {
            return;
        }

        var tiles = Data.Files.TileData;
        _total = tiles.LandData.Length + tiles.StaticData.Length;
        var sw = Stopwatch.StartNew();
        while (_next < _total && sw.Elapsed.TotalMilliseconds < ms)
        {
            for (int n = 0; n < 512 && _next < _total; n++, _next++)
            {
                bool land = _next < tiles.LandData.Length;
                int id = land ? _next : _next - tiles.LandData.Length;
                string name = (land ? tiles.LandData[id].Name : tiles.StaticData[id].Name) ?? "";
                name = name.Replace("%", "").Trim();
                if (name.Length == 0 || !Data.HasArt(land ? (uint)id : EditorData.LandCount + (uint)id))
                {
                    continue;
                }

                _entries.Add(Make(land, id, name));
            }
        }

        _done = _next >= _total;
    }

    private SearchEntry Make(bool land, int id, string name = null)
    {
        uint index = land ? (uint)id : EditorData.LandCount + (uint)id;
        name ??= Data.NameOf(index).Replace("%", "").Trim();
        string kind = land ? "Static" : "Static";
        kind = land ? "Land" : "Static";
        return new SearchEntry
        {
            Kind = kind,
            Title = name.Length > 0 ? name : "(unnamed)",
            Hint = $"{Hex(id)} ({id})",
            Tags = $"{Hex(id).ToLowerInvariant()} {id} art {kind}",
            Key = $"{kind}:{Hex(id)}",
            Bonus = land ? 0 : 10,
            Thumb = () => Data.ArtImage(index),
            Run = () =>
            {
                ArtPanel panel = Ctx.Assets.Panel<ArtPanel>();
                panel?.SelectKind(land);
                Ctx.OpenAsset(panel, Hex(id));
            },
        }.Prepare();
    }

    public override IEnumerable<SearchEntry> Lookup(SearchQuery q)
    {
        if (q.Number is not long id || !DataReady)
        {
            yield break;
        }

        if (InRange(id, EditorData.LandCount) && Data.HasArt((uint)id))
        {
            yield return Make(true, (int)id);
        }

        if (InRange(id, Data.Files.TileData.StaticData.Length) && Data.HasArt(EditorData.LandCount + (uint)id))
        {
            SearchEntry e = Make(false, (int)id);
            e.Bonus += 30;
            yield return e;
        }
    }

    public override SearchEntry Resolve(string key)
    {
        long id = KeyId(key, "Static");
        if (id >= 0 && DataReady && Data.HasArt(EditorData.LandCount + (uint)id))
        {
            return Make(false, (int)id);
        }

        id = KeyId(key, "Land");
        return id >= 0 && DataReady && Data.HasArt((uint)id) ? Make(true, (int)id) : null;
    }
}

/// <summary>Hues by name or id.</summary>
public sealed class UoHueProvider : UoProvider
{
    private readonly List<SearchEntry> _entries = new();
    private bool _done;

    public UoHueProvider(SearchContext ctx) : base(ctx)
    {
    }

    public override string Name => "hues";

    public override bool Done => _done;

    public override IEnumerable<SearchEntry> Entries => _entries;

    private HuePanel Panel => Ctx.Assets.Panel<HuePanel>();

    public override void Step(double ms)
    {
        if (_done || !DataReady || Panel == null)
        {
            return;
        }

        int count = Math.Min(Data.Files.Hues.HuesCount, Data.Files.Hues.HuesRange.Length * 8);
        for (int hue = 1; hue <= count; hue++)
        {
            _entries.Add(Make(hue));
        }

        _done = true;
    }

    private SearchEntry Make(int hue)
    {
        string name = Panel?.HueName(hue) ?? "";
        return new SearchEntry
        {
            Kind = "Hue",
            Title = name.Length > 0 ? name : $"Hue {hue}",
            Hint = $"{Hex(hue)} ({hue})",
            Tags = $"{Hex(hue).ToLowerInvariant()} {hue} hue colour color",
            Key = $"Hue:{Hex(hue)}",
            Bonus = 5,
            Run = () => Ctx.OpenAsset(Panel, Hex(hue)),
        }.Prepare();
    }

    public override IEnumerable<SearchEntry> Lookup(SearchQuery q)
    {
        if (q.Number is long id && DataReady && Panel != null && id >= 1 && id <= Math.Min(Data.Files.Hues.HuesCount, Data.Files.Hues.HuesRange.Length * 8))
        {
            yield return Make((int)id);
        }
    }

    public override SearchEntry Resolve(string key)
    {
        long id = KeyId(key, "Hue");
        return id >= 1 && DataReady && Panel != null ? Make((int)id) : null;
    }
}

/// <summary>Sound effects and music, by name or id.</summary>
public sealed class UoSoundProvider : UoProvider
{
    private const int MaxSound = 0x1000, MaxMusic = 0x100;
    private readonly List<SearchEntry> _entries = new();
    private int _next;
    private bool _done;

    public UoSoundProvider(SearchContext ctx) : base(ctx)
    {
    }

    public override string Name => "sounds";

    public override bool Done => _done;

    public override double Progress => _done ? 1 : (double)_next / (MaxSound + MaxMusic);

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override void Step(double ms)
    {
        if (_done || !DataReady)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        while (_next < MaxSound + MaxMusic && sw.Elapsed.TotalMilliseconds < ms)
        {
            if (_next < MaxSound)
            {
                if (Effect(_next) is SearchEntry e)
                {
                    _entries.Add(e);
                }
            }
            else if (Music(_next - MaxSound) is SearchEntry m)
            {
                _entries.Add(m);
            }

            _next++;
        }

        _done = _next >= MaxSound + MaxMusic;
    }

    private SearchEntry Effect(int id)
    {
        if (!Data.Files.Sounds.TryGetSound(id, out byte[] data, out string name) || data == null || data.Length == 0)
        {
            return null;
        }

        name = (name ?? "").TrimEnd('\0', ' ');
        return new SearchEntry
        {
            Kind = "Sound",
            Title = name.Length > 0 ? name : $"Sound {Hex(id)}",
            Hint = $"{Hex(id)} ({id})",
            Tags = $"{Hex(id).ToLowerInvariant()} {id} sound effect audio",
            Key = $"Sound:{Hex(id)}",
            Run = () =>
            {
                SoundPanel panel = Ctx.Assets.Panel<SoundPanel>();
                panel?.SelectKind(false);
                Ctx.OpenAsset(panel, Hex(id));
            },
        }.Prepare();
    }

    private SearchEntry Music(int id)
    {
        if (!Data.Files.Sounds.TryGetMusicData(id, out string name, out _))
        {
            return null;
        }

        return new SearchEntry
        {
            Kind = "Music",
            Title = string.IsNullOrEmpty(name) ? $"Music {Hex(id)}" : name,
            Hint = $"{Hex(id)} ({id})",
            Tags = $"{Hex(id).ToLowerInvariant()} {id} music track song",
            Key = $"Music:{Hex(id)}",
            Run = () =>
            {
                SoundPanel panel = Ctx.Assets.Panel<SoundPanel>();
                panel?.SelectKind(true);
                Ctx.OpenAsset(panel, Hex(id));
            },
        }.Prepare();
    }

    public override IEnumerable<SearchEntry> Lookup(SearchQuery q)
    {
        if (q.Number is not long id || !DataReady)
        {
            yield break;
        }

        if (InRange(id, MaxSound) && Effect((int)id) is SearchEntry e)
        {
            yield return e;
        }

        if (InRange(id, MaxMusic) && Music((int)id) is SearchEntry m)
        {
            yield return m;
        }
    }

    public override SearchEntry Resolve(string key)
    {
        long id = KeyId(key, "Sound");
        if (id >= 0 && DataReady)
        {
            return Effect((int)id);
        }

        id = KeyId(key, "Music");
        return id >= 0 && DataReady ? Music((int)id) : null;
    }
}

/// <summary>
/// Ids with no names in the data (gumps, multis, animation bodies) and
/// cliloc numbers: found by number, hex or decimal, checked against the
/// loader when typed. The client data has no mobile names, so bodies are
/// found by id only.
/// </summary>
public sealed class UoIdProvider : UoProvider
{
    public UoIdProvider(SearchContext ctx) : base(ctx)
    {
    }

    public override string Name => "ids";

    public override IEnumerable<SearchEntry> Entries => Array.Empty<SearchEntry>();

    private bool GumpExists(long id)
    {
        var file = Data.Files.Gumps.File;
        if (!InRange(id, file.Entries.Length))
        {
            return false;
        }

        ref GUO.IO.UOFileIndex e = ref file.GetValidRefEntry((int)id);
        return e.Length > 0;
    }

    private bool MultiExists(long id)
    {
        try
        {
            return InRange(id, GUO.Assets.MultiLoader.MAX_MULTI_DATA_INDEX_COUNT) && Data.Files.Multis.GetMultis((uint)id).Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool BodyExists(long id)
    {
        try
        {
            return InRange(id, 0x1000) && (Data.Animations.AnimationExists((ushort)id, 0) || Data.Animations.AnimationExists((ushort)id, 4));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private SearchEntry Make(string kind, long id)
    {
        string text = kind == "Cliloc" ? Data.Files.Clilocs.GetString((int)id, "") : "";
        AssetPanel panel = kind switch
        {
            "Gump" => Ctx.Assets.Panel<GumpPanel>(),
            "Multi" => Ctx.Assets.Panel<MultiPanel>(),
            "Body" => Ctx.Assets.Panel<AnimationPanel>(),
            _ => Ctx.Assets.Panel<ClilocPanel>(),
        };
        string search = kind == "Cliloc" ? id.ToString() : Hex(id);
        return new SearchEntry
        {
            Kind = kind,
            Title = kind switch
            {
                "Gump" => $"Gump {Hex(id)}",
                "Multi" => $"Multi {Hex(id)}",
                "Body" => $"Animation body {Hex(id)}",
                _ => text.Length > 70 ? text[..70] + "..." : text,
            },
            Hint = kind == "Cliloc" ? $"cliloc {id}" : $"{kind.ToLowerInvariant()} id ({id})",
            Key = $"{kind}:{(kind == "Cliloc" ? id.ToString() : Hex(id))}",
            Bonus = kind switch { "Gump" => 15, "Multi" => 8, "Body" => 6, _ => 0 },
            Run = () => Ctx.OpenAsset(panel, search),
        }.Prepare();
    }

    public override IEnumerable<SearchEntry> Lookup(SearchQuery q)
    {
        if (q.Number is not long id || !DataReady)
        {
            yield break;
        }

        if (GumpExists(id))
        {
            yield return Make("Gump", id);
        }

        if (MultiExists(id))
        {
            yield return Make("Multi", id);
        }

        if (BodyExists(id))
        {
            yield return Make("Body", id);
        }

        if (InRange(id, 3_200_000) && Data.Files.Clilocs.GetString((int)id, "").Length > 0)
        {
            yield return Make("Cliloc", id);
        }
    }

    public override SearchEntry Resolve(string key)
    {
        if (!DataReady)
        {
            return null;
        }

        foreach (string kind in new[] { "Gump", "Multi", "Body", "Cliloc" })
        {
            long id = KeyId(key, kind);
            if (id >= 0)
            {
                return Make(kind, id);
            }
        }

        return null;
    }
}

/// <summary>
/// Cliloc text, indexed lazily and last: the table is read once, a slice a
/// frame, then searched by substring (a few hundred thousand strings are not
/// fuzzy-matched on every key). Results are capped.
/// </summary>
public sealed class UoClilocProvider : UoProvider
{
    private const int Max = 3_200_000, MatchCap = 200;
    private readonly List<(int Id, string Text)> _strings = new();
    private int _next;
    private bool _done;

    public UoClilocProvider(SearchContext ctx) : base(ctx)
    {
    }

    public override string Name => "cliloc text";

    public override bool Done => _done;

    public override double Progress => _done ? 1 : (double)_next / Max;

    public override IEnumerable<SearchEntry> Entries => Array.Empty<SearchEntry>();

    public override void Step(double ms)
    {
        if (_done || !DataReady)
        {
            return;
        }

        var cl = Data.Files.Clilocs;
        var sw = Stopwatch.StartNew();
        while (_next < Max && sw.Elapsed.TotalMilliseconds < ms)
        {
            for (int n = 0; n < 4096 && _next < Max; n++, _next++)
            {
                string t = cl.GetString(_next, "");
                if (t.Length > 0)
                {
                    _strings.Add((_next, t));
                }
            }
        }

        _done = _next >= Max;
    }

    public override IEnumerable<(SearchEntry Entry, int Score)> Search(SearchQuery q)
    {
        if (q.IsEmpty || q.Number != null || q.IsCoordinates || q.Text.Length < 3)
        {
            yield break;
        }

        var hits = new List<(int Id, string Text, int Score)>();
        foreach (var (id, text) in _strings)
        {
            int score = 0;
            bool all = true;
            foreach (string w in q.Words)
            {
                int at = text.IndexOf(w, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    all = false;
                    break;
                }

                // Whole text, then a start, then the shorter text.
                score += text.Length == w.Length ? 900 : at == 0 ? 700 : 500 - Math.Min(at, 100);
            }

            if (all)
            {
                hits.Add((id, text, score / q.Words.Length - Math.Min(text.Length / 4, 150) - 80));
                if (hits.Count >= MatchCap * 5)
                {
                    break;
                }
            }
        }

        foreach (var (id, text, score) in hits.OrderByDescending(h => h.Score).Take(MatchCap))
        {
            int cliloc = id;
            yield return (new SearchEntry
            {
                Kind = "Cliloc",
                Title = text.Replace('\n', ' ').Replace("[", "(").Length > 80 ? text.Replace('\n', ' ')[..80] + "..." : text.Replace('\n', ' '),
                Hint = $"cliloc {id}",
                Key = $"Cliloc:{id}",
                Run = () => Ctx.OpenAsset(Ctx.Assets.Panel<ClilocPanel>(), cliloc.ToString()),
            }.Prepare(), score);
        }
    }
}

/// <summary>
/// "x y [z] [mapN]" jumps the World tab, and named places come from
/// Search/places.json (the client data has no region or town names).
/// </summary>
public sealed class PlacesProvider : UoProvider
{
    public const string File = "res://addons/guo_editor/Search/places.json";

    private readonly List<SearchEntry> _entries = new();
    private bool _built;

    public PlacesProvider(SearchContext ctx) : base(ctx)
    {
    }

    public override string Name => "places";

    public override bool Done => _built;

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override void Step(double ms)
    {
        if (_built)
        {
            return;
        }

        _built = true;
        try
        {
            using FileAccess f = FileAccess.Open(File, FileAccess.ModeFlags.Read);
            using JsonDocument doc = JsonDocument.Parse(f.GetAsText());
            foreach (JsonElement p in doc.RootElement.GetProperty("places").EnumerateArray())
            {
                string name = p.GetProperty("name").GetString();
                int map = p.GetProperty("map").GetInt32(), x = p.GetProperty("x").GetInt32(), y = p.GetProperty("y").GetInt32();
                string tags = p.TryGetProperty("tags", out JsonElement t) ? t.GetString() : "";
                _entries.Add(Make(name, map, x, y, tags));
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] places.json not read: {ex.Message}");
        }
    }

    private SearchEntry Make(string title, int map, int x, int y, string tags = "") =>
        new SearchEntry
        {
            Kind = "Place",
            Title = title,
            Hint = $"map{map} {x}, {y}",
            Tags = $"go to jump teleport location town map{map} {tags}",
            Key = $"Place:{map}:{x}:{y}",
            Bonus = 25,
            Target = (map, x, y),
            Run = () => Ctx.ShowInWorld(map, x, y),
        }.Prepare();

    public override SearchEntry Resolve(string key)
    {
        SearchEntry e = base.Resolve(key);
        if (e != null)
        {
            return e;
        }

        string[] p = key.Split(':');
        return p.Length == 4 && p[0] == "Place" && int.TryParse(p[1], out int m) && int.TryParse(p[2], out int x) && int.TryParse(p[3], out int y)
            ? Make($"Go to {x}, {y}", m, x, y)
            : null;
    }

    public override IEnumerable<SearchEntry> Lookup(SearchQuery q)
    {
        if (!q.IsCoordinates)
        {
            yield break;
        }

        int facet = q.Facet ?? (Ctx.World != null && Ctx.World.IsBooted ? Math.Max(0, Ctx.World.Host.Facet) : 0);
        if (facet is < 0 or > 5)
        {
            yield break;
        }

        if (DataReady)
        {
            var sizes = Data.Files.Maps.MapsDefaultSize;
            if (facet >= sizes.GetLength(0) || q.X >= sizes[facet, 0] || q.Y >= sizes[facet, 1])
            {
                yield break;
            }
        }

        string z = q.Z.HasValue ? $", z {q.Z}" : "";
        SearchEntry e = Make($"Go to {q.X}, {q.Y}{z} on map{facet}", facet, q.X, q.Y);
        e.Key = $"Place:{facet}:{q.X}:{q.Y}";
        yield return e;
    }
}
#endif
