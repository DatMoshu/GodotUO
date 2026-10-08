// GUO addition, not a port. ADR-0034 / docs/data_formats.md section 36: the extracted art set, read lazily.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using Environment = System.Environment;

namespace GUO.Assets.Extracted;

/// <summary>
/// Answers the same (class, id) image questions the store overlay answers, from the atlas pages an
/// <c>art_extract export</c> wrote of the user's own install. A page is decoded on first use and the coldest pages
/// are dropped under a memory cap. Anything it cannot answer (an id not in the set, a damaged page, a set that does
/// not match the install) returns false and the loader carries on to the original files.
///
/// It sits behind every override the loaders have of their own: <c>Art/Statics</c>, <c>Art/Land</c> and
/// <c>Gumps</c> files in the client folder correct art the archive has, and ArtLoader/GumpsLoader ask the content
/// seam before those files. This source therefore never answers for an id one of those files holds, so the
/// loader falls through to its own file, exactly as with the set off. The set is built from the pure install.
/// </summary>
internal sealed class ExtractedArtSource
{
    private readonly record struct Entry(int Page, int X, int Y, int W, int H);

    private sealed class ClassIndex
    {
        public string Name;
        public string[] PageFiles;
        public string[] PageSha;
        public bool[] PageBad;
        public Dictionary<int, Entry> Entries = new();
    }

    private const int PageSize = 2048;

    private readonly string _root;
    private readonly Dictionary<string, ClassIndex> _classes = new();
    private readonly HashSet<(string, int)> _overridden = new();
    private readonly Dictionary<(string, int), byte[]> _pages = new();
    private readonly LinkedList<(string, int)> _lru = new();
    private readonly long _capBytes;
    private readonly object _gate = new();

    public int ClassCount => _classes.Count;
    public int EntryCount { get; private set; }
    public int PagesLoaded { get; private set; }

    private ExtractedArtSource(string root, long capBytes)
    {
        _root = root;
        _capBytes = capBytes;
    }

    /// <summary>Is the set switched on? --no-art-set beats --art-set beats UO_ART_SET; off by default.</summary>
    internal static bool Enabled(string[] args)
    {
        bool on = Environment.GetEnvironmentVariable("UO_ART_SET") is "1" or "true" or "TRUE" or "True";
        foreach (string a in args)
        {
            if (a == "--art-set") on = true;
            else if (a == "--no-art-set") return false;
        }
        return on;
    }

    internal static string SetFolder()
    {
        string dir = Environment.GetEnvironmentVariable("UO_ART_EXTRACT_DIR");
        if (!string.IsNullOrWhiteSpace(dir) && Path.IsPathFullyQualified(dir)) return Path.GetFullPath(dir);
        return Path.Combine(GUO.Workspace.Workspace.Root, "art_extract");
    }

    /// <summary>Mount the configured set if it is switched on and matches the install; null otherwise (one warning).</summary>
    internal static ExtractedArtSource MountConfigured(string clientData)
    {
        string[] args;
        try { args = OS.GetCmdlineUserArgs(); } catch { args = Array.Empty<string>(); }
        if (!Enabled(args)) return null;
        return Mount(SetFolder(), clientData);
    }

    internal static ExtractedArtSource Mount(string folder, string clientData, long? capBytes = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string setPath = Path.Combine(folder, "set.json");
            if (!File.Exists(setPath)) return Refuse(folder, "there is no set.json (run tools/art_extract export)");
            using var set = JsonDocument.Parse(File.ReadAllBytes(setPath));
            var root = set.RootElement;
            if (root.GetProperty("schema").GetString() != "guo/art_set@1" || root.GetProperty("version").GetInt32() != 1)
                return Refuse(folder, "the set is a format version this client does not know");
            var fingerprint = root.GetProperty("fingerprint");
            foreach (var file in fingerprint.GetProperty("files").EnumerateArray())
            {
                string name = file.GetProperty("name").GetString();
                var info = new FileInfo(Path.Combine(clientData, name));
                if (!info.Exists) return Refuse(folder, $"{name} is not in the install any more");
                if (info.Length != file.GetProperty("size").GetInt64())
                    return Refuse(folder, $"{name} has a different size than when the set was made");
            }
            string setId = fingerprint.GetProperty("set_id").GetString();

            long cap = capBytes ?? CapFromEnvironment();
            var source = new ExtractedArtSource(folder, cap);
            foreach (var cls in root.GetProperty("classes").EnumerateObject())
            {
                string indexPath = Path.Combine(folder, cls.Name, "index.json");
                using var doc = JsonDocument.Parse(File.ReadAllBytes(indexPath));
                var idx = doc.RootElement;
                if (idx.GetProperty("schema").GetString() != "guo/art_index@1"
                    || idx.GetProperty("set_id").GetString() != setId
                    || idx.GetProperty("page_size").GetInt32() != PageSize
                    || idx.GetProperty("pixel_format").GetString() != "rgba8")
                    return Refuse(folder, $"{cls.Name}/index.json does not belong to this set");
                var pages = idx.GetProperty("pages");
                var ci = new ClassIndex { Name = cls.Name, PageFiles = new string[pages.GetArrayLength()], PageSha = new string[pages.GetArrayLength()] };
                ci.PageBad = new bool[ci.PageFiles.Length];
                int p = 0;
                foreach (var page in pages.EnumerateArray())
                {
                    ci.PageFiles[p] = page.GetProperty("file").GetString();
                    ci.PageSha[p++] = page.GetProperty("sha256").GetString();
                }
                foreach (var e in idx.GetProperty("entries").EnumerateObject())
                {
                    if (!int.TryParse(e.Name, out int id)) continue;
                    var v = e.Value;
                    int page = v.GetProperty("page").GetInt32(), x = v.GetProperty("x").GetInt32(), y = v.GetProperty("y").GetInt32(),
                        w = v.GetProperty("w").GetInt32(), h = v.GetProperty("h").GetInt32();
                    if (page < 0 || page >= ci.PageFiles.Length || w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > PageSize || y + h > PageSize) continue;
                    ci.Entries[id] = new Entry(page, x, y, w, h);
                }
                source.EntryCount += ci.Entries.Count;
                source._classes[cls.Name] = ci;
            }
            source.ScanOverrides(clientData);
            GD.Print($"[GUO] art set mounted: {source.EntryCount} images in {source.ClassCount} classes from the extracted set " +
                     $"({started.ElapsedMilliseconds} ms, {source._overridden.Count} ids left to the client's own override files)");
            return source;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            return Refuse(folder, ex.Message);
        }
    }

    private static ExtractedArtSource Refuse(string folder, string why)
    {
        GD.PushWarning($"[GUO] art set not used ({why}); reading the original files. Set folder: {folder}");
        return null;
    }

    private static long CapFromEnvironment()
    {
        long mb = 256;
        if (long.TryParse(Environment.GetEnvironmentVariable("UO_ART_SET_CACHE_MB"), out long v) && v >= 16) mb = v;
        return mb * 1024 * 1024;
    }

    /// <summary>The ids the loaders' own override files hold, found the way ArtLoader and GumpsLoader find them.</summary>
    private void ScanOverrides(string clientData)
    {
        Scan(Path.Combine(clientData, "Art", "Statics"), "*.art", "static");
        Scan(Path.Combine(clientData, "Art", "Land"), "*.art", "land");
        Scan(Path.Combine(clientData, "Gumps"), "*.gump", "gump");
    }

    private void Scan(string folder, string pattern, string cls)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string path in Directory.EnumerateFiles(folder, pattern))
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int id) && id >= 0) _overridden.Add((cls, id));
    }

    public bool TryImage(string type, int id, out GUO.Store.StoreRuntimeContent.Pixels pixels)
    {
        pixels = null;
        if (!_classes.TryGetValue(type, out var ci) || !ci.Entries.TryGetValue(id, out var e)) return false;
        if (_overridden.Contains((type, id)) || ci.PageBad[e.Page]) return false;
        byte[] page = Page(ci, e.Page);
        if (page == null) return false;
        var data = new uint[e.W * e.H];
        var source = MemoryMarshal.Cast<byte, uint>(page.AsSpan());
        for (int row = 0; row < e.H; row++)
            source.Slice((e.Y + row) * PageSize + e.X, e.W).CopyTo(data.AsSpan(row * e.W, e.W));
        pixels = new GUO.Store.StoreRuntimeContent.Pixels(data, e.W, e.H);
        return true;
    }

    private byte[] Page(ClassIndex ci, int page)
    {
        var key = (ci.Name, page);
        lock (_gate)
        {
            if (_pages.TryGetValue(key, out var hit))
            {
                _lru.Remove(key);
                _lru.AddFirst(key);
                return hit;
            }
            byte[] rgba = Load(ci, page);
            if (rgba == null) { ci.PageBad[page] = true; return null; }
            _pages[key] = rgba;
            _lru.AddFirst(key);
            PagesLoaded++;
            while (_lru.Count > 1 && (long)_pages.Count * PageSize * PageSize * 4 > _capBytes)
            {
                var cold = _lru.Last.Value;
                _lru.RemoveLast();
                _pages.Remove(cold);
            }
            return rgba;
        }
    }

    private byte[] Load(ClassIndex ci, int page)
    {
        string path = Path.Combine(_root, ci.Name, ci.PageFiles[page]);
        try
        {
            byte[] png = File.ReadAllBytes(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(png)), ci.PageSha[page], StringComparison.OrdinalIgnoreCase))
                return Damaged(path, "its checksum differs from the index");
            using var image = new Image();
            if (image.LoadPngFromBuffer(png) != Error.Ok) return Damaged(path, "it is not a readable PNG");
            if (image.GetWidth() != PageSize || image.GetHeight() != PageSize) return Damaged(path, "it is not 2048 x 2048");
            image.Convert(Image.Format.Rgba8);
            return image.GetData();
        }
        catch (IOException ex) { return Damaged(path, ex.Message); }
    }

    private static byte[] Damaged(string path, string why)
    {
        GD.PushWarning($"[GUO] art set page {path} not used ({why}); its images come from the original files");
        return null;
    }
}
