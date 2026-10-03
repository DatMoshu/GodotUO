#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

/// <summary>Results of one kind, in score order.</summary>
public sealed class SearchGroup
{
    public string Kind;
    public int Best;
    public readonly List<(SearchEntry Entry, int Score)> Items = new();
}

/// <summary>
/// The providers, the history, and the query. Indexing is cooperative:
/// <see cref="Step"/> is called a few milliseconds at a time from the popup's
/// frame loop, because the UO loaders may only be touched on the main thread.
/// </summary>
public sealed class SearchIndex
{
    private static readonly Dictionary<string, int> PerKind = new()
    {
        ["Menu"] = 8, ["Setting"] = 6, ["World"] = 8, ["GUO"] = 8, ["Cliloc"] = 5,
    };

    private readonly List<SearchProvider> _providers = new();

    public SearchHistory History { get; } = new();

    public IReadOnlyList<SearchProvider> Providers => _providers;

    public void Add(SearchProvider provider) => _providers.Add(provider);

    public T Provider<T>() where T : SearchProvider => _providers.OfType<T>().FirstOrDefault();

    /// <summary>True once every provider has finished building its catalog.</summary>
    public bool Ready => _providers.All(p => p.Done);

    public double Progress => _providers.Count == 0 ? 1 : _providers.Average(p => p.Progress);

    /// <summary>The providers still building, for the readout.</summary>
    public string Pending => string.Join(", ", _providers.Where(p => !p.Done).Select(p => p.Name));

    /// <summary>Indexes for up to <paramref name="ms"/> milliseconds. Cheap when there is nothing to do.</summary>
    public void Step(double ms)
    {
        var sw = Stopwatch.StartNew();
        foreach (SearchProvider p in _providers)
        {
            while (!p.Done && sw.Elapsed.TotalMilliseconds < ms)
            {
                p.Step(Math.Max(0.5, ms - sw.Elapsed.TotalMilliseconds));
            }

            if (sw.Elapsed.TotalMilliseconds >= ms)
            {
                return;
            }
        }
    }

    public void Refresh()
    {
        foreach (SearchProvider p in _providers)
        {
            p.Refresh();
        }
    }

    /// <summary>Builds everything now. For the smoke check, never from the editor's frame loop.</summary>
    public void FinishNow()
    {
        var sw = Stopwatch.StartNew();
        foreach (SearchProvider p in _providers)
        {
            while (!p.Done && sw.Elapsed.TotalSeconds < 120)
            {
                p.Step(50);
            }
        }
    }

    /// <summary>
    /// The groups for a query, best group first: each kind's own best matches
    /// (a few of each), so one big source cannot bury the rest. An empty
    /// query gives what was run lately, then the providers' suggestions.
    /// </summary>
    public List<SearchGroup> Query(string text)
    {
        var q = new SearchQuery(text);
        var best = new Dictionary<string, (SearchEntry Entry, int Score)>();

        void Put(SearchEntry e, int score)
        {
            if (!best.TryGetValue(e.Key, out var old) || old.Score < score)
            {
                best[e.Key] = (e, score);
            }
        }

        if (q.IsEmpty)
        {
            int rank = 2000;
            foreach (string key in History.Recent(8))
            {
                SearchEntry e = _providers.Select(p => p.Resolve(key)).FirstOrDefault(r => r != null);
                if (e != null)
                {
                    Put(e, rank--);
                }
            }

            rank = 1000;
            foreach (SearchEntry e in _providers.SelectMany(p => p.Suggested))
            {
                Put(e.Prepare(), rank--);
            }
        }
        else
        {
            foreach (SearchProvider p in _providers)
            {
                foreach (SearchEntry e in p.Lookup(q))
                {
                    if (q.Allows(e.Kind))
                    {
                        Put(e.Prepare(), 1000 + e.Bonus + History.Boost(e.Key));
                    }
                }

                foreach (var (e, score) in p.Search(q))
                {
                    Put(e, score + History.Boost(e.Key));
                }
            }
        }

        var groups = new Dictionary<string, SearchGroup>();
        foreach (var (entry, score) in best.Values.OrderByDescending(v => v.Score).ThenBy(v => v.Entry.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (!groups.TryGetValue(entry.Kind, out SearchGroup g))
            {
                groups[entry.Kind] = g = new SearchGroup { Kind = entry.Kind, Best = score };
            }

            int cap = q.IsEmpty ? 8 : PerKind.GetValueOrDefault(entry.Kind, 6);
            if (g.Items.Count < cap)
            {
                g.Items.Add((entry, score));
            }
        }

        return groups.Values.OrderByDescending(g => g.Best).ToList();
    }

    /// <summary>The best result overall, for the smoke check and for Enter with nothing selected.</summary>
    public static SearchEntry Top(List<SearchGroup> groups) => groups.Count > 0 ? groups[0].Items[0].Entry : null;
}
#endif
