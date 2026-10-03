namespace CentrED.MapGen.Data;

// Resolves a path that's logically relative to the repo root. CentrED.exe runs
// from tools/CentrED/output/ so plain "tools/mapgen/MapGen/presets" can't
// be opened directly. Walks up from CWD and AppContext.BaseDirectory looking for a
// repo marker (.git, CLAUDE.md, or tools/CentrED/), then resolves the relative path.
public static class RepoRootResolver
{
    private static string? _cachedRepoRoot;

    /// <summary>Environment variable naming the repo root; checked before walking up from CWD / the binary.</summary>
    public const string RepoRootEnvVar = "MAPGEN_REPO_ROOT";

    public static string? FindRepoRoot()
    {
        if (_cachedRepoRoot is not null) return _cachedRepoRoot;
        // Explicit override first: builds/tests whose output lives outside the repo
        // (dotnet --artifacts-path) cannot find it by walking up from their own folder.
        var env = Environment.GetEnvironmentVariable(RepoRootEnvVar);
        var seeds = string.IsNullOrWhiteSpace(env)
            ? new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
            : new[] { env, Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach (var seed in seeds)
        {
            var dir = new DirectoryInfo(seed);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                    || File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))
                    || Directory.Exists(Path.Combine(dir.FullName, "tools", "CentrED")))
                {
                    _cachedRepoRoot = dir.FullName;
                    return _cachedRepoRoot;
                }
                dir = dir.Parent;
            }
        }
        return null;
    }

    // Resolve a path that may be (a) absolute, (b) relative to current directory, or
    // (c) relative to the repo root. Returns the first form that exists, or the
    // repo-rooted form even if missing (so the caller can log a useful error).
    /// <summary>
    /// Environment variable naming the generator data folder: everything mined from a user's
    /// client data (stamps, coast atlas, decor frequencies, tree statics, validation output).
    /// GUO never ships that data; it lives per user, outside the repo.
    /// </summary>
    public const string DataDirEnvVar = "MAPGEN_DATA_DIR";

    // Repo-relative prefixes that name mined (client-derived) data, and where each one lives
    // under the data folder.
    private static readonly (string Prefix, string Sub)[] MinedPrefixes =
    {
        ("Data/map-mining/", "map-mining/"),
        ("mined/", ""),
        ("client/ClassicUO/Data/", ""),
    };

    /// <summary>The generator data folder: <see cref="DataDirEnvVar"/>, else %LOCALAPPDATA%/GUO/mapgen.</summary>
    public static string DataDir()
    {
        var env = Environment.GetEnvironmentVariable(DataDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GUO", "mapgen");
    }

    /// <summary>Maps a mined-data path onto the data folder; null when <paramref name="path"/> is not mined data.</summary>
    public static string? ResolveMined(string path)
    {
        var p = path.Replace('\\', '/');
        foreach (var (prefix, sub) in MinedPrefixes)
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(DataDir(), sub + p[prefix.Length..].TrimStart('/'));
        return null;
    }

    public static string Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (Path.IsPathRooted(path) && (Directory.Exists(path) || File.Exists(path))) return path;
        if (!Path.IsPathRooted(path) && ResolveMined(path) is { } mined) return mined;
        if (Directory.Exists(path) || File.Exists(path)) return Path.GetFullPath(path);
        var root = FindRepoRoot();
        if (root is null) return path;
        var combined = Path.Combine(root, path);
        return combined;
    }
}
