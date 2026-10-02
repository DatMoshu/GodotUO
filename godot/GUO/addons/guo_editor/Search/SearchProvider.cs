#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// One thing F3 can find and do. Providers make them; the index scores them.
/// </summary>
public sealed class SearchEntry
{
    /// <summary>What it is, shown as the small label and used to group: Menu, Setting, World, Static, Place...</summary>
    public string Kind;

    /// <summary>The main text.</summary>
    public string Title;

    /// <summary>A second line: where it lives, its id, its current value.</summary>
    public string Hint = "";

    /// <summary>More words that find it but are not shown ("hex id", "synonym").</summary>
    public string Tags = "";

    /// <summary>
    /// Stable across runs: the history is keyed on it. Kind plus something
    /// that does not change when menus are rebuilt.
    /// </summary>
    public string Key;

    /// <summary>What Enter does. It may open a window; it runs only on Enter.</summary>
    public Action Run;

    /// <summary>Added to the score, for a kind that should win a tie.</summary>
    public int Bonus;

    /// <summary>A small picture for the row, made only for the rows on screen.</summary>
    public Func<Image> Thumb;

    /// <summary>The coordinates a Place entry goes to (the smoke check reads them).</summary>
    public (int Facet, int X, int Y)? Target;

    internal string TitleLower, HintLower, TagsLower;

    internal SearchEntry Prepare()
    {
        TitleLower = Title.ToLowerInvariant();
        HintLower = Hint.ToLowerInvariant();
        TagsLower = Tags.ToLowerInvariant();
        Key ??= Kind + ":" + Title;
        return this;
    }
}

/// <summary>
/// A source of <see cref="SearchEntry"/>. A provider owns its own catalog
/// and, when the catalog is built from the client data, builds it a slice at
/// a time in <see cref="Step"/> on the main thread (the loaders are single
/// threaded), so the editor never waits for it.
/// </summary>
public abstract class SearchProvider
{
    /// <summary>Short name, for the indexing readout and the smoke report.</summary>
    public abstract string Name { get; }

    /// <summary>False while <see cref="Step"/> still has work.</summary>
    public virtual bool Done => true;

    /// <summary>0..1, for the readout.</summary>
    public virtual double Progress => 1;

    /// <summary>Does up to about <paramref name="ms"/> milliseconds of indexing.</summary>
    public virtual void Step(double ms)
    {
    }

    /// <summary>Called each time the popup opens, for catalogs that change (the editor's menus).</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>Everything this provider can find, for the empty query and for fuzzy matching.</summary>
    public abstract IEnumerable<SearchEntry> Entries { get; }

    /// <summary>What to offer before anything is typed, after the recent ones.</summary>
    public virtual IEnumerable<SearchEntry> Suggested => Array.Empty<SearchEntry>();

    /// <summary>The entry a history key stands for, or null. The default looks in the catalog.</summary>
    public virtual SearchEntry Resolve(string key)
    {
        foreach (SearchEntry e in Entries)
        {
            if (e.Key == key)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// Entries made from the query itself: an id, a coordinate. These do not
    /// need an index. The default is none.
    /// </summary>
    public virtual IEnumerable<SearchEntry> Lookup(SearchQuery q) => Array.Empty<SearchEntry>();

    /// <summary>
    /// Scores the catalog against the query. The default is fuzzy matching of
    /// every word against the title, the hint and the tags; a large catalog
    /// (cliloc) overrides it with something cheaper.
    /// </summary>
    public virtual IEnumerable<(SearchEntry Entry, int Score)> Search(SearchQuery q)
    {
        if (q.IsEmpty || q.Number != null || q.IsCoordinates)
        {
            yield break;
        }

        foreach (SearchEntry e in Entries)
        {
            int score = ScoreEntry(q, e);
            if (score > 0)
            {
                yield return (e, score);
            }
        }
    }

    /// <summary>Every word has to match; the score is the mean, so a title hit beats a path hit.</summary>
    protected static int ScoreEntry(SearchQuery q, SearchEntry e)
    {
        int sum = 0;
        foreach (string w in q.Words)
        {
            int s = Fuzzy.Score(w, e.TitleLower);
            if (s < Fuzzy.Exact)
            {
                if (e.HintLower.Length > 0)
                {
                    s = Math.Max(s, Strong(Fuzzy.Score(w, e.HintLower)) * 7 / 10);
                }

                if (e.TagsLower.Length > 0)
                {
                    s = Math.Max(s, Strong(Fuzzy.Score(w, e.TagsLower)) * 8 / 10);
                }
            }

            if (s == 0)
            {
                return 0;
            }

            sum += s;
        }

        return sum / q.Words.Length + e.Bonus;
    }

    /// <summary>Hints and tags count only when the word is in them whole: letters picked from across a path are noise.</summary>
    private static int Strong(int score) => score >= 600 ? score : 0;

    /// <summary>The hex form with the digits UO uses everywhere.</summary>
    public static string Hex(long id) => $"0x{id:X4}";
}
#endif
