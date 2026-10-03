using System.Collections.Concurrent;
using System.Text.Json;
using CentrED.MapGen.Data;

namespace CentrED.MapGen.Stamps;

/// <summary>
/// The one stamp loader shared by every consumer (MapGen passes, StampCompose,
/// MapGenDump, the CentrED editor). Libraries are cached per root and reused until a
/// stamp file or index.json changes on disk.
///
/// <para>index.json is used when it is present and consistent with the files on disk
/// (same set of *.stamp.json paths). Stamps are then parsed lazily per kind, so a pass
/// that wants road stamps never parses the 1,700 town stamps. When the index is
/// missing or stale every file is parsed once and the rows are rebuilt from the files.</para>
///
/// <para>Z normalisation: version-1 files store ABSOLUTE source Z. The loader rewrites
/// them to Z relative to the anchor tile's ground (see <see cref="LoadedStamp"/>).
/// Version 2+ files carry <c>"z_mode": "relative"</c> and are taken as-is. Nothing is
/// written back to the library; migrating files is an explicit MapMiner command.</para>
/// </summary>
public static class StampLoader
{
    /// <summary>Schema version written by current tools: Z relative to the anchor ground.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Default library location, relative to the repo root.</summary>
    public const string DefaultRoot = "mined/stamps";

    /// <summary>Diagnostics sink for corrupt files and stale indexes. Defaults to stderr.</summary>
    public static Action<string>? Log { get; set; } = m => Console.Error.WriteLine(m);

    private static readonly ConcurrentDictionary<string, (string Signature, StampLibrary Library)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads (or returns the cached) library at <paramref name="root"/>. Relative roots
    /// resolve against the working directory first, then the repo root.
    /// </summary>
    public static StampLibrary Load(string? root = null)
    {
        string resolved = ResolveRoot(root);
        if (!Directory.Exists(resolved))
            return StampLibrary.Missing(resolved);

        var files = EnumerateStampFiles(resolved, out string signature);
        if (Cache.TryGetValue(resolved, out var hit) && hit.Signature == signature)
            return hit.Library;

        var lib = Build(resolved, files);
        Cache[resolved] = (signature, lib);
        return lib;
    }

    /// <summary>Drops cached libraries (all, or just <paramref name="root"/>).</summary>
    public static void InvalidateCache(string? root = null)
    {
        if (root is null) { Cache.Clear(); return; }
        Cache.TryRemove(ResolveRoot(root), out _);
    }

    /// <summary>Resolves a library root the same way <see cref="Load"/> does.</summary>
    public static string ResolveRoot(string? root)
    {
        string r = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;
        return Path.GetFullPath(RepoRootResolver.Resolve(r));
    }

    /// <summary>Loads and normalises a single stamp file.</summary>
    public static LoadedStamp LoadFile(string path, string? relPath = null)
    {
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        return Parse(doc.RootElement, relPath ?? Path.GetFileName(path));
    }

    /// <summary>Parses and normalises a stamp from its JSON (schema: _schema/stamp.schema.json).</summary>
    public static LoadedStamp Parse(JsonElement root, string relPath = "")
    {
        var s = new LoadedStamp
        {
            Path = relPath,
            Id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
            Kind = root.TryGetProperty("kind", out var kEl) ? kEl.GetString() ?? "other" : "other",
            Subtype = root.TryGetProperty("subtype", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() : null,
            Version = root.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.Number ? vEl.GetInt32() : 1,
            Weight = root.TryGetProperty("weight", out var wEl) && wEl.ValueKind == JsonValueKind.Number ? wEl.GetDouble() : 1.0,
        };
        if (root.TryGetProperty("bounds", out var bEl) && bEl.ValueKind == JsonValueKind.Object)
        {
            s.BoundsW = bEl.TryGetProperty("w", out var bw) ? bw.GetInt32() : 0;
            s.BoundsH = bEl.TryGetProperty("h", out var bh) ? bh.GetInt32() : 0;
            if (bEl.TryGetProperty("size_class", out var sc) && sc.ValueKind == JsonValueKind.String) s.SizeClass = sc.GetString();
        }
        if (root.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
            s.Tags = tg.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToArray();

        s.SourceZMode = ReadZMode(root, s.Version);

        var landZ = new List<int>();
        if (root.TryGetProperty("land", out var landEl) && landEl.ValueKind == JsonValueKind.Array)
        {
            int n = landEl.GetArrayLength();
            s.LandRx = new int[n]; s.LandRy = new int[n]; s.LandIds = new ushort[n];
            int i = 0;
            foreach (var l in landEl.EnumerateArray())
            {
                s.LandRx[i] = l.GetProperty("rx").GetInt32();
                s.LandRy[i] = l.GetProperty("ry").GetInt32();
                s.LandIds[i] = (ushort)l.GetProperty("id").GetInt32();
                landZ.Add(l.TryGetProperty("z", out var z) ? z.GetInt32() : 0);
                i++;
            }
        }
        var staticZ = new List<int>();
        if (root.TryGetProperty("statics", out var stsEl) && stsEl.ValueKind == JsonValueKind.Array)
        {
            int n = stsEl.GetArrayLength();
            s.StaticRx = new int[n]; s.StaticRy = new int[n]; s.StaticIds = new ushort[n]; s.StaticHues = new ushort[n];
            int i = 0;
            foreach (var st in stsEl.EnumerateArray())
            {
                s.StaticRx[i] = st.GetProperty("rx").GetInt32();
                s.StaticRy[i] = st.GetProperty("ry").GetInt32();
                s.StaticIds[i] = (ushort)st.GetProperty("id").GetInt32();
                staticZ.Add(st.TryGetProperty("z", out var z) ? z.GetInt32() : 0);
                s.StaticHues[i] = st.TryGetProperty("hue", out var hEl) && hEl.ValueKind == JsonValueKind.Number ? (ushort)hEl.GetInt32() : (ushort)0;
                i++;
            }
        }

        // ---- Z normalisation (absolute -> relative to anchor ground) ----
        int refZ = 0;
        if (s.SourceZMode == StampZMode.Absolute)
            refZ = ComputeReferenceZ(s.LandRx, s.LandRy, landZ, staticZ);
        s.ReferenceZ = refZ;
        if (s.SourceZMode == StampZMode.Absolute) s.SourceGroundZ = refZ;
        else if (root.TryGetProperty("reference_z", out var rzEl) && rzEl.ValueKind == JsonValueKind.Number) s.SourceGroundZ = rzEl.GetInt32();
        s.LandZRel = landZ.Select(z => (short)(z - refZ)).ToArray();
        s.StaticZRel = staticZ.Select(z => (short)(z - refZ)).ToArray();

        // ---- Clusters ----
        s.StaticClusterAnchor = new int[s.StaticRx.Length];
        Array.Fill(s.StaticClusterAnchor, -1);
        if (root.TryGetProperty("clusters", out var clEl) && clEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in clEl.EnumerateArray())
            {
                if (!c.TryGetProperty("anchor", out var aEl) || aEl.ValueKind != JsonValueKind.Number) continue;
                int anchor = aEl.GetInt32();
                if ((uint)anchor >= (uint)s.StaticRx.Length) continue;
                s.StaticClusterAnchor[anchor] = anchor;
                if (!c.TryGetProperty("members", out var mEl) || mEl.ValueKind != JsonValueKind.Array) continue;
                foreach (var m in mEl.EnumerateArray())
                {
                    int mi = m.GetInt32();
                    if ((uint)mi < (uint)s.StaticRx.Length && s.StaticClusterAnchor[mi] < 0)
                        s.StaticClusterAnchor[mi] = anchor;
                }
            }
        }
        return s;
    }

    /// <summary>
    /// The absolute Z that becomes relative 0: the anchor tile's land Z, else the lowest
    /// land Z, else the lowest static Z (a statics-only stamp rests its lowest piece on
    /// the ground), else 0.
    /// </summary>
    public static int ComputeReferenceZ(IReadOnlyList<int> landRx, IReadOnlyList<int> landRy,
        IReadOnlyList<int> landZ, IReadOnlyList<int> staticZ)
    {
        for (int i = 0; i < landZ.Count; i++)
            if (landRx[i] == 0 && landRy[i] == 0) return landZ[i];
        if (landZ.Count > 0) return landZ.Min();
        if (staticZ.Count > 0) return staticZ.Min();
        return 0;
    }

    private static StampZMode ReadZMode(JsonElement root, int version)
    {
        if (root.TryGetProperty("z_mode", out var zm) && zm.ValueKind == JsonValueKind.String)
        {
            var v = zm.GetString();
            if (string.Equals(v, "relative", StringComparison.OrdinalIgnoreCase)) return StampZMode.Relative;
            if (string.Equals(v, "absolute", StringComparison.OrdinalIgnoreCase)) return StampZMode.Absolute;
        }
        return version >= CurrentVersion ? StampZMode.Relative : StampZMode.Absolute;
    }

    // ------------------------------------------------------------------ library build

    private static List<(string Rel, string Full)> EnumerateStampFiles(string root, out string signature)
    {
        var list = new List<(string, string)>();
        long newest = 0;
        foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*.stamp.json", SearchOption.AllDirectories))
        {
            // Skip "_"-prefixed folders (_trash, _schema, _transitions, ...) at any depth.
            if (IsReservedPath(Path.GetRelativePath(root, fi.FullName))) continue;
            list.Add((Path.GetRelativePath(root, fi.FullName).Replace('\\', '/'), fi.FullName));
            newest = Math.Max(newest, fi.LastWriteTimeUtc.Ticks);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        var idx = new FileInfo(Path.Combine(root, "index.json"));
        signature = $"{list.Count}|{newest}|{(idx.Exists ? idx.LastWriteTimeUtc.Ticks : 0)}|{(idx.Exists ? idx.Length : 0)}";
        return list;
    }

    /// <summary>
    /// True when any folder in <paramref name="relativePath"/> starts with '_' (e.g. the
    /// editor's _trash, _schema, _transitions). Such files are never stamps of the library.
    /// </summary>
    public static bool IsReservedPath(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].StartsWith('_')) return true;
        return false;
    }

    private static StampLibrary Build(string root, List<(string Rel, string Full)> files)
    {
        var lib = new StampLibrary(root);
        var rows = TryReadIndex(root, files, out string? why);
        if (rows is not null)
        {
            lib.SetRows(rows, fromIndex: true);
            return lib;
        }
        if (why is not null) lib.AddWarning(why);
        // Stale or missing index: parse everything once, rows come from the files.
        var built = new List<StampIndexRow>(files.Count);
        foreach (var (rel, full) in files)
        {
            var s = lib.ParseAndCache(rel, full);
            if (s is null) continue;
            built.Add(new StampIndexRow(s.Id, s.Kind, s.Subtype, rel, s.BoundsW, s.BoundsH,
                s.StaticCount, s.LandCount, s.Tags, s.Weight));
        }
        lib.SetRows(built, fromIndex: false);
        return lib;
    }

    private static List<StampIndexRow>? TryReadIndex(string root, List<(string Rel, string Full)> files, out string? why)
    {
        why = null;
        string path = Path.Combine(root, "index.json");
        if (!File.Exists(path)) { why = $"stamps: no index.json under {root}; parsing every stamp file"; return null; }
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("Stamps", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                why = $"stamps: index.json has no Stamps array; parsing every stamp file";
                return null;
            }
            var onDisk = new HashSet<string>(files.Select(f => f.Rel), StringComparer.OrdinalIgnoreCase);
            var rows = new List<StampIndexRow>(arr.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in arr.EnumerateArray())
            {
                string rel = (e.TryGetProperty("Path", out var p) ? p.GetString() ?? "" : "").Replace('\\', '/');
                if (!onDisk.Contains(rel) || !seen.Add(rel))
                {
                    why = $"stamps: index.json is stale ({rel} missing on disk or duplicated); parsing every stamp file";
                    return null;
                }
                IReadOnlyList<string> tags = e.TryGetProperty("Tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                rows.Add(new StampIndexRow(
                    e.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : "",
                    e.TryGetProperty("Kind", out var k) ? k.GetString() ?? "other" : "other",
                    e.TryGetProperty("Subtype", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null,
                    rel,
                    e.TryGetProperty("Width", out var w) ? w.GetInt32() : 0,
                    e.TryGetProperty("Height", out var h) ? h.GetInt32() : 0,
                    e.TryGetProperty("StaticCount", out var sc) ? sc.GetInt32() : 0,
                    e.TryGetProperty("LandCount", out var lc) ? lc.GetInt32() : 0,
                    tags,
                    e.TryGetProperty("Weight", out var wt) && wt.ValueKind == JsonValueKind.Number ? wt.GetDouble() : 1.0));
            }
            if (rows.Count != files.Count)
            {
                why = $"stamps: index.json lists {rows.Count} stamps but {files.Count} are on disk; parsing every stamp file";
                return null;
            }
            rows.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return rows;
        }
        catch (Exception ex)
        {
            why = $"stamps: index.json unreadable ({ex.Message}); parsing every stamp file";
            return null;
        }
    }
}

/// <summary>A stamp library rooted at one directory. Thread-safe; stamps parse lazily per kind.</summary>
public sealed class StampLibrary
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LoadedStamp> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();
    private List<StampIndexRow> _rows = new();
    private Dictionary<string, List<StampIndexRow>> _rowsByKind = new();

    public string Root { get; }
    /// <summary>True when the rows came from a consistent index.json.</summary>
    public bool FromIndex { get; private set; }
    public bool Exists { get; private set; } = true;

    internal StampLibrary(string root) { Root = root; }

    internal static StampLibrary Missing(string root)
    {
        var lib = new StampLibrary(root) { Exists = false };
        lib._warnings.Add($"stamps: library root not found: {root}");
        return lib;
    }

    public IReadOnlyList<StampIndexRow> Rows => _rows;
    public int TotalCount => _rows.Count;
    public IEnumerable<string> Kinds => _rowsByKind.Keys.OrderBy(k => k, StringComparer.Ordinal);

    /// <summary>Corrupt or unreadable files met so far (one line each).</summary>
    public IReadOnlyList<string> Errors { get { lock (_lock) return _errors.ToArray(); } }
    /// <summary>Non-fatal notes: stale index, kind mismatches.</summary>
    public IReadOnlyList<string> Warnings { get { lock (_lock) return _warnings.ToArray(); } }

    /// <summary>Index rows of one kind (cheap; no file parsing).</summary>
    public IReadOnlyList<StampIndexRow> RowsOfKind(string kind) =>
        _rowsByKind.TryGetValue(kind, out var l) ? l : (IReadOnlyList<StampIndexRow>)Array.Empty<StampIndexRow>();

    /// <summary>
    /// All stamps of <paramref name="kind"/>, parsed on first use. <paramref name="filter"/>
    /// runs on the index rows first, so e.g. a static-count cap never parses the big stamps.
    /// Stamps whose file says a different kind than the index are skipped and logged.
    /// Order is stable (sorted by path).
    /// </summary>
    public IReadOnlyList<LoadedStamp> GetKind(string kind, Func<StampIndexRow, bool>? filter = null)
    {
        var result = new List<LoadedStamp>();
        foreach (var row in RowsOfKind(kind))
        {
            if (filter is not null && !filter(row)) continue;
            var s = GetByPath(row.Path);
            if (s is null) continue;
            if (!string.Equals(s.Kind, kind, StringComparison.Ordinal))
            {
                AddWarning($"stamps: {row.Path} is kind '{s.Kind}' in the file but '{kind}' in index.json; skipped");
                continue;
            }
            result.Add(s);
        }
        return result;
    }

    /// <summary>Stamp by library-relative path ('/' or '\' separators), parsed on first use.</summary>
    public LoadedStamp? GetByPath(string relPath)
    {
        relPath = relPath.Replace('\\', '/');
        lock (_lock)
        {
            if (_byPath.TryGetValue(relPath, out var s)) return s;
            if (_failed.Contains(relPath)) return null;
        }
        return ParseAndCache(relPath, Path.Combine(Root, relPath));
    }

    /// <summary>Stamp by id (first match by path order).</summary>
    public LoadedStamp? GetById(string id)
    {
        foreach (var r in _rows)
            if (string.Equals(r.Id, id, StringComparison.Ordinal))
                return GetByPath(r.Path);
        return null;
    }

    internal LoadedStamp? ParseAndCache(string rel, string full)
    {
        try
        {
            var s = StampLoader.LoadFile(full, rel);
            lock (_lock) _byPath[rel] = s;
            return s;
        }
        catch (Exception ex)
        {
            string msg = $"stamps: corrupt stamp {rel}: {ex.Message}";
            lock (_lock)
            {
                _failed.Add(rel);
                _errors.Add(msg);
            }
            StampLoader.Log?.Invoke(msg);
            return null;
        }
    }

    internal void SetRows(List<StampIndexRow> rows, bool fromIndex)
    {
        _rows = rows;
        FromIndex = fromIndex;
        var byKind = new Dictionary<string, List<StampIndexRow>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!byKind.TryGetValue(r.Kind, out var l)) byKind[r.Kind] = l = new List<StampIndexRow>();
            l.Add(r);
        }
        _rowsByKind = byKind;
    }

    internal void AddWarning(string msg)
    {
        lock (_lock)
        {
            if (_warnings.Contains(msg)) return;
            _warnings.Add(msg);
        }
        StampLoader.Log?.Invoke(msg);
    }
}
