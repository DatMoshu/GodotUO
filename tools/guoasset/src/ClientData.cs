// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Utility;

namespace GUO.AssetMcp;

internal enum Kind
{
    Land,
    Static,
    Gump,
}

/// <summary>A decoded image, copied out of the loader's buffers. RGBA8888, R in the low byte.</summary>
internal sealed record Pixels(uint[] Data, int Width, int Height)
{
    public bool IsEmpty => Width == 0 || Height == 0;
    public static readonly Pixels Empty = new([], 0, 0);
}

/// <summary>
/// One open client install, read through upstream ClassicUO's UOFileManager.
/// Opened on first use and kept for the life of the server: loading the
/// indexes is the slow part, and the MCP host calls tools many times.
/// Strictly read-only; nothing here opens a file for writing.
/// </summary>
internal sealed class ClientData : IDisposable
{
    private static readonly object _gate = new();
    private static ClientData? _current;

    private readonly UOFileManager _files;

    public string Path { get; }
    public string VersionText { get; }
    public ClientVersion Version { get; }

    private ClientData(string path, string versionText, ClientVersion version)
    {
        Path = path;
        VersionText = versionText;
        Version = version;
        _files = new UOFileManager(version, path);
        _files.Load(useVerdata: false, lang: "enu");
    }

    /// <summary>
    /// Runs <paramref name="use"/> against the install at <paramref name="clientPath"/>,
    /// or at UO_CLIENT_DATA when it is empty. Calls are serialised: the loaders
    /// share reader state and are not safe to use from two threads.
    /// </summary>
    public static T With<T>(string? clientPath, Func<ClientData, T> use)
    {
        var path = !string.IsNullOrWhiteSpace(clientPath) ? clientPath : Settings.Get("UO_CLIENT_DATA");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                "No client folder. Pass clientPath, or set UO_CLIENT_DATA in launchers\\_shared\\config.local.bat.");
        path = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Client folder not found: {path}");

        lock (_gate)
        {
            if (_current is null || !string.Equals(_current.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _current?.Dispose();
                var (text, version) = ResolveVersion(path);
                _current = new ClientData(path, text, version);
            }
            return use(_current);
        }
    }

    /// <summary>UO_CLIENT_VERSION if set, else the version stamped in client.exe, as upstream reads it.</summary>
    private static (string, ClientVersion) ResolveVersion(string path)
    {
        var text = Settings.Get("UO_CLIENT_VERSION");
        if (string.IsNullOrWhiteSpace(text))
            ClientVersionHelper.TryParseFromFile(System.IO.Path.Combine(path, "client.exe"), out text);
        if (!string.IsNullOrWhiteSpace(text) && ClientVersionHelper.IsClientVersionValid(text, out var version))
            return (text, version);
        throw new InvalidOperationException(
            $"Cannot tell the client version of {path}. Set UO_CLIENT_VERSION in launchers\\_shared\\config.local.bat.");
    }

    public int MaxId(Kind kind) => kind switch
    {
        Kind.Land => ArtLoader.MAX_LAND_DATA_INDEX_COUNT,
        Kind.Static => ArtLoader.MAX_STATIC_DATA_INDEX_COUNT,
        _ => GumpsLoader.MAX_GUMP_DATA_INDEX_COUNT,
    };

    public Pixels Get(Kind kind, int id)
    {
        if (id < 0 || id >= MaxId(kind))
            return Pixels.Empty;

        if (kind == Kind.Gump)
        {
            var gump = _files.Gumps.GetGump((uint)id);
            return Copy(gump.Pixels, gump.Width, gump.Height);
        }

        uint index = kind == Kind.Land ? (uint)id : (uint)(ArtLoader.MAX_LAND_DATA_INDEX_COUNT + id);
        var art = _files.Arts.GetArt(index);
        return Copy(art.Pixels, art.Width, art.Height);
    }

    private static Pixels Copy(Span<uint> src, int width, int height)
    {
        if (width <= 0 || height <= 0 || src.Length < width * height)
            return Pixels.Empty;
        var data = new uint[width * height];
        src[..data.Length].CopyTo(data);
        return new Pixels(data, width, height);
    }

    public IReadOnlyList<MultiInfo> Multi(int id)
    {
        if (id < 0 || id >= MultiLoader.MAX_MULTI_DATA_INDEX_COUNT)
            return [];
        try
        {
            return _files.Multis.GetMultis((uint)id);
        }
        catch (Exception)
        {
            // An index entry pointing past the end of the file reads as empty, as upstream treats it.
            return [];
        }
    }

    public int MaxMultiId => MultiLoader.MAX_MULTI_DATA_INDEX_COUNT;

    public IEnumerable<(Kind kind, int id, string name)> TileNames()
    {
        var statics = _files.TileData.StaticData;
        for (int i = 0; i < statics.Length; i++)
            yield return (Kind.Static, i, statics[i].Name ?? "");

        var lands = _files.TileData.LandData;
        for (int i = 0; i < lands.Length; i++)
            yield return (Kind.Land, i, lands[i].Name ?? "");
    }

    public void Dispose() => _files.Dispose();
}
