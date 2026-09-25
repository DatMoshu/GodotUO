// SPDX-License-Identifier: BSD-2-Clause

using System.Text.RegularExpressions;

namespace GUO.AssetMcp;

/// <summary>
/// Resolves settings the way every GUO tool does (see tools/guo/config.py):
/// environment variable, then launchers/_shared/config.local.bat, then
/// launchers/_shared/config.bat. The .bat files are parsed, never run.
/// </summary>
internal static partial class Settings
{
    [GeneratedRegex(@"^\s*(?:if\s+not\s+defined\s+\w+\s+)?set\s+""(?<key>[A-Za-z_][A-Za-z0-9_]*)=(?<val>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex SetLine();

    [GeneratedRegex("%([A-Za-z_][A-Za-z0-9_]*)%")]
    private static partial Regex VarRef();

    private static readonly Lazy<string?> _root = new(FindRepoRoot);
    private static readonly Lazy<Dictionary<string, string>> _fromBat = new(ReadBatFiles);

    /// <summary>The GUO checkout this server was built in, if it can be found.</summary>
    public static string? RepoRoot => _root.Value;

    public static string? Get(string key)
    {
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        return _fromBat.Value.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    /// <summary>Where renders go when the caller names no folder: build\guoasset\out.</summary>
    public static string DefaultOutDir(string sub)
    {
        var baseDir = RepoRoot is { } root
            ? Path.Combine(root, "build", "guoasset", "out")
            : Path.Combine(Path.GetTempPath(), "guoasset");
        return Path.Combine(baseDir, sub);
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "launchers", "_shared", "config.bat")))
                    return dir.FullName;
            }
        }
        return null;
    }

    private static Dictionary<string, string> ReadBatFiles()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (RepoRoot is not { } root)
            return values;

        var shared = Path.Combine(root, "launchers", "_shared");
        // config.bat first, then the local file over it: config.bat calls the
        // local file before its own guarded defaults, so the local value wins.
        foreach (var name in new[] { "config.bat", "config.local.bat" })
        {
            var path = Path.Combine(shared, name);
            if (!File.Exists(path))
                continue;

            foreach (var line in File.ReadLines(path))
            {
                var m = SetLine().Match(line);
                if (!m.Success)
                    continue;
                var raw = m.Groups["val"].Value;
                values[m.Groups["key"].Value] = VarRef().Replace(raw, r =>
                {
                    var name2 = r.Groups[1].Value;
                    return Environment.GetEnvironmentVariable(name2)
                        ?? (values.TryGetValue(name2, out var earlier) ? earlier : r.Value);
                });
            }
        }
        return values;
    }
}
