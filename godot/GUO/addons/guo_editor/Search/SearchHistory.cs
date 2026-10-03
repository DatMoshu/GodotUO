#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

/// <summary>
/// What F3 ran lately, so it ranks higher next time. Kept in the editor's
/// per-project metadata (EditorSettings.SetProjectMetadata, which lands in
/// .godot/editor, not in a tracked file).
/// </summary>
public sealed class SearchHistory
{
    private const string Section = "guo_editor";
    private const string KeyName = "search_history";
    private const int Limit = 200;

    private readonly Dictionary<string, (int Count, long Last)> _uses = new();
    private bool _loaded;

    private static EditorSettings Settings => EditorInterface.Singleton?.GetEditorSettings();

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            string json = Settings?.GetProjectMetadata(Section, KeyName, "").AsString() ?? "";
            if (json.Length == 0)
            {
                return;
            }

            foreach (var (key, v) in JsonSerializer.Deserialize<Dictionary<string, long[]>>(json))
            {
                if (v.Length == 2)
                {
                    _uses[key] = ((int)v[0], v[1]);
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] search history unreadable, starting fresh: {ex.Message}");
        }
    }

    /// <summary>Notes that an entry was run.</summary>
    public void Record(string key)
    {
        Load();
        _uses.TryGetValue(key, out var u);
        _uses[key] = (u.Count + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (_uses.Count > Limit)
        {
            foreach (string old in _uses.OrderBy(kv => kv.Value.Last).Take(_uses.Count - Limit).Select(kv => kv.Key).ToList())
            {
                _uses.Remove(old);
            }
        }

        Save();
    }

    private void Save()
    {
        try
        {
            var plain = _uses.ToDictionary(kv => kv.Key, kv => new[] { (long)kv.Value.Count, kv.Value.Last });
            Settings?.SetProjectMetadata(Section, KeyName, JsonSerializer.Serialize(plain));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] search history not saved: {ex.Message}");
        }
    }

    /// <summary>Score added to a match that was used before: more for a recent and a frequent one.</summary>
    public int Boost(string key)
    {
        Load();
        if (!_uses.TryGetValue(key, out var u))
        {
            return 0;
        }

        double days = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - u.Last) / 86400.0;
        double fresh = days < 1 ? 1.0 : days < 14 ? 0.7 : 0.4;
        return (int)((140 + 25 * Math.Min(u.Count, 6)) * fresh);
    }

    /// <summary>Keys, most recent first.</summary>
    public IEnumerable<string> Recent(int n)
    {
        Load();
        return _uses.OrderByDescending(kv => kv.Value.Last).ThenByDescending(kv => kv.Value.Count).Take(n).Select(kv => kv.Key).ToList();
    }

    public int Count
    {
        get
        {
            Load();
            return _uses.Count;
        }
    }
}
#endif
