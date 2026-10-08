#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;
using GUO.Host;
using GUO.IO;

/// <summary>
/// The editor's one handle on the UO client install: resolves where it is
/// the same way the launchers do, opens it through the ported loaders, and
/// hands decoded art to the docks.
/// </summary>
/// <remarks>
/// <para>
/// Read only, always. Nothing here writes to <c>UO_CLIENT_DATA</c>; edits
/// live in a world project overlay (docs/editor_plan.md §2), which does not
/// exist yet.
/// </para>
/// <para>
/// The loaders are not thread safe. <see cref="LoadAsync"/> runs
/// <see cref="UOFileManager.Load"/> on a worker so the editor does not
/// freeze while the archives open, and nothing touches the loaders until it
/// has finished; after that every call is on the main thread.
/// </para>
/// </remarks>
public sealed class EditorData : IDisposable
{
    public const uint LandCount = ArtLoader.MAX_LAND_DATA_INDEX_COUNT;

    private UOFileManager _files;
    private Task _loading;
    private readonly object _loadGate = new();
    private volatile bool _retired;
    // Internal deterministic lifecycle controls; never configured by production.
    internal Action AfterLoadControl;
    internal TimeSpan RetirementWait = TimeSpan.FromSeconds(30);
    internal Task LoadingTask => _loading;
    private GUO.Renderer.Animations.Animations _animations;
    private GUO.Renderer.Sounds.Sound _sounds;

    public bool IsLoaded { get; private set; }
    public string Error { get; private set; }
    public string ClientData { get; private set; }
    public string ClientVersion { get; private set; }
    public long LoadMilliseconds { get; private set; }
    internal string ClientLanguage { get; private set; }
    private string _stageRoot = "", _loadWorkingDirectory;
    private Dictionary<string, string> _stageOverrides;
    private readonly List<FileStream> _stageReadLeases = new();
    internal bool UseStageVerdata => _stageRoot.Length > 0;
    internal bool IsSelectedInputStage(string path)
    {
        if (_retired || _stageRoot.Length == 0) return false;
        string output = Path.GetFullPath(path).TrimEnd('\\', '/');
        return output.Equals(_stageRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || _stageOverrides.Values.Any(target => Path.GetFullPath(target)
                .StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public UOFileManager Files => IsLoaded ? _files : null;

    /// <summary>
    /// The game's own animation reader (<c>Renderer.Animations</c>): body
    /// conversion, UOP group replacement and frame decoding exactly as the
    /// client does them. Made on first use.
    /// </summary>
    public GUO.Renderer.Animations.Animations Animations =>
        IsLoaded ? _animations ??= new GUO.Renderer.Animations.Animations(_files.Animations) : null;

    /// <summary>The game's own sound and music cache (<c>Renderer.Sounds.Sound</c>).</summary>
    public GUO.Renderer.Sounds.Sound Sounds =>
        IsLoaded ? _sounds ??= new GUO.Renderer.Sounds.Sound(_files.Sounds) : null;

    /// <summary>The art index last picked in the Art panel; the Hues panel previews on it.</summary>
    public uint CurrentArt { get; set; } = LandCount + 0x1F03;

    /// <summary>Raised on the main thread once loading has finished, well or badly.</summary>
    public event Action Loaded;

    /// <summary>
    /// The asset half of the world project (ADR-0020): replaced art, gumps
    /// and hues, laid over <see cref="Files"/> once loaded. Null until then.
    /// </summary>
    public AssetOverlay Assets { get; private set; }

    private AssetOverlay.Applied _assetsApplied;

    /// <summary>
    /// Raised on the main thread after every application of the asset
    /// overlay: when a project is opened, and after each import or revert.
    /// </summary>
    public event Action AssetsApplied;

    /// <summary>The world project folder: UO_WORLD_PROJECT, or build\world\default.</summary>
    public static string ProjectRoot()
    {
        string root = Setting("UO_WORLD_PROJECT", "");
        return root.Length > 0 ? root : Path.Combine(RepoRoot, "build", "world", "default");
    }

    /// <summary>
    /// Starts loading the install unless that is already under way. Safe to
    /// call more than once.
    /// </summary>
    public void LoadAsync()
    {
        if (_retired || _loading != null)
        {
            return;
        }

        ClientData = Setting("UO_CLIENT_DATA", "");
        ClientVersion = Setting("UO_CLIENT_VERSION", "7.0.107.76");
        string lang = Setting("UO_LANGUAGE", "enu");
        ClientLanguage = lang;

        if (string.IsNullOrWhiteSpace(ClientData) || !Directory.Exists(ClientData))
        {
            Error = $"UO_CLIENT_DATA is not a folder: '{ClientData}'. Set it in launchers\\_shared\\config.bat.";
            GD.PrintErr($"[GUO editor] {Error}");
            _loading = Task.CompletedTask;
            Callable.From(Finish).CallDeferred();
            return;
        }

        string stage = EditorSmoke.ArgValue("--guo-editor-data-stage") ?? "";
        UOFilesOverrideMap overrideMap;
        try
        {
            overrideMap = AcquireStageReadLeases(stage);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            Error = $"Explicit editor data stage '{stage}' is invalid: {ex.Message}";
            foreach (FileStream lease in _stageReadLeases) lease.Dispose();
            _stageReadLeases.Clear();
            _loading = Task.CompletedTask;
            Callable.From(Finish).CallDeferred();
            return;
        }
        var files = new UOFileManager(UoDataProbe.ParseVersion(ClientVersion), ClientData, overrideMap);
        _loading = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (_retired)
                {
                    files.Dispose();
                    return;
                }
                files.Load(useVerdata: !string.IsNullOrWhiteSpace(stage), lang: lang);
                AfterLoadControl?.Invoke();
                lock (_loadGate)
                {
                    if (_retired)
                    {
                        files.Dispose();
                        return;
                    }
                    _files = files;
                }
            }
            catch (Exception ex)
            {
                Error = string.IsNullOrWhiteSpace(stage)
                    ? $"loading client data failed: {ex.GetType().Name}: {ex.Message}"
                    : $"Explicit editor data stage '{stage}' failed to load: {ex.GetType().Name}: {ex.Message}";
                files.Dispose();
            }

            LoadMilliseconds = sw.ElapsedMilliseconds;
            lock (_loadGate)
            {
                if (!_retired) Callable.From(Finish).CallDeferred();
            }
        });
    }

    internal UOFilesOverrideMap AcquireStageReadLeases(string stage)
    {
        if (!string.IsNullOrWhiteSpace(stage))
        {
            if (!File.Exists(Path.Combine(stage, "files_override.txt")))
                throw new IOException("files_override.txt is missing; select a stage exported by GUO.");
            _stageReadLeases.Add(File.Open(Path.Combine(stage, "files_override.txt"), FileMode.Open, System.IO.FileAccess.Read, FileShare.Read));
        }
        UOFilesOverrideMap map = LoadStageOverrides(stage);
        _stageRoot = string.IsNullOrWhiteSpace(stage) ? "" : Path.GetFullPath(stage);
        _loadWorkingDirectory = System.Environment.CurrentDirectory;
        _stageOverrides = new Dictionary<string, string>(map, StringComparer.Ordinal);
        foreach (string target in _stageOverrides.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            _stageReadLeases.Add(File.Open(Path.GetFullPath(target), FileMode.Open, System.IO.FileAccess.Read, FileShare.Read));
        UOFilesOverrideMap pinned = LoadStageOverrides(stage);
        if (pinned.Count != _stageOverrides.Count || _stageOverrides.Any(pair =>
                !pinned.TryGetValue(pair.Key, out string target) || target != pair.Value))
            throw new IOException("files_override.txt changed while acquiring the stage read leases.");
        return map;
    }

    /// <summary>Revalidates the selected mapping before the independent World loader opens it.</summary>
    internal string PrepareWorldOverrideFile()
    {
        if (!IsLoaded)
        {
            throw new InvalidOperationException(Error ?? "Editor data is not loaded.");
        }

        try
        {
            if (System.Environment.CurrentDirectory != _loadWorkingDirectory)
            {
                throw new IOException("The editor working directory changed; reload editor data before starting World.");
            }

            UOFilesOverrideMap current = LoadStageOverrides(_stageRoot);
            if (current.Count != _stageOverrides.Count || _stageOverrides.Any(pair =>
                    !current.TryGetValue(pair.Key, out string target) || target != pair.Value))
            {
                throw new IOException("files_override.txt changed after Assets loaded; reload editor data before starting World.");
            }

            return _stageRoot.Length == 0 ? "" : Path.Combine(_stageRoot, "files_override.txt");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            throw new IOException(_stageRoot.Length == 0
                ? $"Editor data cannot start World: {ex.Message}"
                : $"Explicit editor data stage '{_stageRoot}' cannot start World: {ex.Message}", ex);
        }
    }

    private static UOFilesOverrideMap LoadStageOverrides(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return new UOFilesOverrideMap();
        }

        string overrides = Path.Combine(stage, "files_override.txt");
        if (!File.Exists(overrides))
        {
            throw new IOException("files_override.txt is missing; select a stage exported by GUO.");
        }

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string line in File.ReadLines(overrides))
        {
            lineNumber++;
            // Match the shared parser's comments, key casing and untrimmed values.
            string comment = line.TrimStart(' ');
            if (string.IsNullOrWhiteSpace(line) || comment.StartsWith(';') || comment.StartsWith('#'))
            {
                continue;
            }

            string[] pair = line.Split('=');
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || string.IsNullOrWhiteSpace(pair[1]))
            {
                throw new IOException($"files_override.txt line {lineNumber}: expected one nonempty logical-file=existing-file mapping.");
            }

            string key = pair[0].ToLowerInvariant();
            string[] names = key.Split(new[] { '/', '\\' });
            if (key != key.Trim() || Path.IsPathRooted(key) || names.Any(n => n.Length == 0 || n == "." || n == ".." || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new IOException($"files_override.txt line {lineNumber}: '{pair[0]}' must be a relative logical UO file name without traversal or surrounding whitespace.");
            }

            if (!entries.TryAdd(key, pair[1]))
            {
                throw new IOException($"files_override.txt line {lineNumber}: duplicate logical file '{key}'. Keep one mapping per file.");
            }

            try
            {
                // Preserve the shared parser's working-directory-relative path semantics.
                // Targets may be external overlays; validation never writes or copies them.
                string target = Path.GetFullPath(pair[1]);
                if (!File.Exists(target))
                {
                    throw new IOException("The target does not exist or is not a file.");
                }
                using FileStream readable = File.Open(target, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new IOException($"files_override.txt line {lineNumber}: target for '{key}' is not a readable file ('{pair[1]}'): {ex.Message}", ex);
            }
        }

        if (entries.Count == 0)
        {
            throw new IOException("files_override.txt has no mappings; select a nonempty stage or omit --guo-editor-data-stage to use base data.");
        }

        var map = new UOFilesOverrideMap(overrides);
        map.Load();
        if (map.Count != entries.Count || entries.Any(entry => !map.TryGetValue(entry.Key, out string target) || target != entry.Value))
        {
            throw new IOException("files_override.txt changed or could not be parsed completely; export it again and retry.");
        }

        return map;
    }

    internal void Finish()
    {
        if (_retired) return;
        IsLoaded = _files != null;
        if (IsLoaded)
        {
            GD.Print($"[GUO editor] client data {ClientData} ({ClientVersion}) loaded in {LoadMilliseconds} ms");
            try
            {
                OpenAssets(ProjectRoot());
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GUO editor] asset overlay: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            GD.PrintErr($"[GUO editor] {Error}");
        }

        Loaded?.Invoke();
    }

    /// <summary>True if the install has art at this index. Land is 0..0x3FFF, statics follow.</summary>
    public bool HasArt(uint index)
    {
        if (!IsLoaded)
        {
            return false;
        }

        ref UOFileIndex entry = ref _files.Arts.File.GetValidRefEntry((int)index);
        return entry.Length > 0;
    }

    /// <summary>Tiledata name for an art index, or "".</summary>
    public string NameOf(uint index)
    {
        if (!IsLoaded)
        {
            return "";
        }

        if (index < LandCount)
        {
            LandTiles[] land = _files.TileData.LandData;
            return index < land.Length ? land[index].Name ?? "" : "";
        }

        StaticTiles[] statics = _files.TileData.StaticData;
        uint s = index - LandCount;
        return s < statics.Length ? statics[s].Name ?? "" : "";
    }

    /// <summary>
    /// Decodes one piece of art through <see cref="ArtLoader.GetArt"/>, the
    /// call the game's own art atlas makes. Null when the id is empty, which
    /// is normal: UO's id space is sparse.
    /// </summary>
    public Image ArtImage(uint index)
    {
        if (!IsLoaded || !HasArt(index))
        {
            return null;
        }

        try
        {
            // ArtInfo holds a span over the loader's scratch buffer: copy the
            // pixels out before anything else touches the loader.
            ArtInfo art = _files.Arts.GetArt(index);
            return FromPixels(art.Pixels, art.Width, art.Height);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] art 0x{index:X4} failed to decode: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Decodes a gump through <see cref="GumpsLoader.GetGump"/>. Null when empty.</summary>
    public Image GumpImage(int index)
    {
        if (!IsLoaded || index < 0 || index >= _files.Gumps.File.Entries.Length)
        {
            return null;
        }

        try
        {
            GumpInfo gump = _files.Gumps.GetGump((uint)index);
            return FromPixels(gump.Pixels, gump.Width, gump.Height);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] gump 0x{index:X4} failed to decode: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loader pixels to an image. The loaders pack Color16To32 as
    /// R | G&lt;&lt;8 | B&lt;&lt;16, which is Rgba8 byte order on little endian;
    /// zero is UO's transparent pixel. Null for an empty or short buffer.
    /// </summary>
    public static Image FromPixels(ReadOnlySpan<uint> pixels, int w, int h)
    {
        if (w <= 0 || h <= 0 || pixels.Length < w * h)
        {
            return null;
        }

        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            uint px = pixels[i];
            int o = i * 4;
            rgba[o + 0] = (byte)(px & 0xFF);
            rgba[o + 1] = (byte)((px >> 8) & 0xFF);
            rgba[o + 2] = (byte)((px >> 16) & 0xFF);
            rgba[o + 3] = (byte)(px == 0 ? 0 : 0xFF);
        }

        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba);
    }

    /// <summary>Opens the asset overlay of a world project and lays it over the loaders.</summary>
    public AssetOverlay OpenAssets(string projectRoot)
    {
        _assetsApplied?.Dispose();
        _assetsApplied = null;
        Assets = new AssetOverlay(projectRoot);
        ReapplyAssets();
        return Assets;
    }

    /// <summary>
    /// Re-reads the asset overlay from disk and lays it over the loaders
    /// again; call after an import or a revert. Returns how many replacements
    /// are applied.
    /// </summary>
    public int ReapplyAssets(AssetKind? kind = null, int id = -1)
    {
        if (!IsLoaded || Assets == null)
        {
            return 0;
        }

        _assetsApplied?.Dispose();
        _assetsApplied = Assets.Apply(_files, "editor");
        int n = _assetsApplied.ArtIndices.Count() + _assetsApplied.GumpIds.Count() + _assetsApplied.HueIds.Count();
        if (n > 0)
        {
            GD.Print($"[GUO editor] asset overlay: {n} replacement(s) from {Assets.Root}");
        }

        AssetsApplied?.Invoke();
        return n;
    }

    public void Dispose()
    {
        lock (_loadGate)
        {
            if (_retired) return;
            _retired = true;
        }
        _assetsApplied?.Dispose();
        _assetsApplied = null;
        Assets = null;
        AssetsApplied = null;
        // A load still running owns the files; let it finish so the handles
        // close, or the next assembly reload finds them mapped.
        try
        {
            _loading?.Wait(RetirementWait);
        }
        catch (AggregateException)
        {
        }

        _animations = null;
        _sounds = null;
        lock (_loadGate)
        {
            _files?.Dispose();
            _files = null;
        }
        foreach (FileStream lease in _stageReadLeases) lease.Dispose();
        _stageReadLeases.Clear();
        IsLoaded = false;
        Loaded = null;
    }

    // --- configuration ----------------------------------------------------

    private static readonly Regex SetLine = new(
        @"^\s*(?:if\s+not\s+defined\s+\w+\s+)?set\s+""(?<key>[A-Za-z_][A-Za-z0-9_]*)=(?<val>[^""]*)""",
        RegexOptions.IgnoreCase
    );

    private static readonly Regex VarRef = new(@"%([A-Za-z_][A-Za-z0-9_]*)%");

    private static Dictionary<string, string> _configBat;

    /// <summary>The repository root: the Godot project is godot/GUO under it.</summary>
    public static string RepoRoot =>
        Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", ".."));

    /// <summary>
    /// One setting, resolved as every launcher and tools/guo/config.py
    /// resolve it: the environment first (common.bat has set it when the
    /// editor came from a launcher), then launchers\_shared\config.bat parsed
    /// directly (when Godot was opened some other way).
    /// </summary>
    public static string Setting(string key, string fallback)
    {
        string env = System.Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        _configBat ??= ParseConfigBat();
        return _configBat.TryGetValue(key, out string v) && v.Length > 0 ? v : fallback;
    }

    private static Dictionary<string, string> ParseConfigBat()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string shared = Path.Combine(RepoRoot, "launchers", "_shared");

        // common.bat sets UO_ROOT before it calls config.bat, and config.bat
        // builds paths on it; outside a launcher it has to come from here.
        values["UO_ROOT"] = RepoRoot;

        // config.bat calls config.local.bat (gitignored, the user's own
        // paths) before its defaults, and every default is "if not defined",
        // so a key the local file sets wins. tools/guo/config.py reads the
        // two files the same way.
        Dictionary<string, string> local = ParseBat(Path.Combine(shared, "config.local.bat"), values);
        foreach (var (k, v) in ParseBat(Path.Combine(shared, "config.bat"), new Dictionary<string, string>(local, StringComparer.OrdinalIgnoreCase)))
        {
            values[k] = v;
        }

        foreach (var (k, v) in local)
        {
            values[k] = v;
        }

        return values;
    }

    private static Dictionary<string, string> ParseBat(string path, Dictionary<string, string> seed)
    {
        var values = new Dictionary<string, string>(seed, StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            Match m = SetLine.Match(line);
            if (!m.Success)
            {
                continue;
            }

            string val = VarRef.Replace(m.Groups["val"].Value, r =>
            {
                string name = r.Groups[1].Value;
                string e = System.Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(e))
                {
                    return e;
                }

                return values.TryGetValue(name, out string earlier) ? earlier : r.Value;
            });
            values[m.Groups["key"].Value] = val;
        }

        return values;
    }
}
#endif
