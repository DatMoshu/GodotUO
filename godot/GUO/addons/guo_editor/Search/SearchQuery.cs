#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// What the F3 field holds, taken apart once so every provider reads the same
/// thing: lower-case words, an id if the text is one number (hex "0x0E75" or
/// decimal "3701", optionally after a kind word: "gump 100"), and a map
/// position if it reads "x y [z] [mapN]".
/// </summary>
public sealed class SearchQuery
{
    private static readonly Regex Coordinates = new(
        @"^\s*(?:\[?go\s+)?(?<x>\d{1,5})[\s,]+(?<y>\d{1,5})(?:[\s,]+(?<z>-?\d{1,3}))?(?:[\s,]*(?:map|facet|m)\s*(?<m>\d))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MapOnly = new(@"^(?:map|facet|m)\s*(\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Words that narrow an id to one kind of asset: "gump 100", "hue 0x21".</summary>
    private static readonly Dictionary<string, string[]> KindWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["art"] = new[] { "Static", "Land" },
        ["static"] = new[] { "Static" },
        ["statics"] = new[] { "Static" },
        ["item"] = new[] { "Static" },
        ["land"] = new[] { "Land" },
        ["gump"] = new[] { "Gump" },
        ["hue"] = new[] { "Hue" },
        ["multi"] = new[] { "Multi" },
        ["house"] = new[] { "Multi" },
        ["body"] = new[] { "Body" },
        ["anim"] = new[] { "Body" },
        ["mobile"] = new[] { "Body" },
        ["sound"] = new[] { "Sound" },
        ["music"] = new[] { "Music" },
        ["cliloc"] = new[] { "Cliloc" },
    };

    public string Raw { get; }

    /// <summary>The lower-case words, without a leading kind word when an id follows it.</summary>
    public string[] Words { get; }

    /// <summary>The words joined with spaces.</summary>
    public string Text { get; }

    /// <summary>The id, when the query is one number (optionally after a kind word).</summary>
    public long? Number { get; }

    /// <summary>The kinds a kind word restricts an id to; null when none was given.</summary>
    public string[] KindFilter { get; }

    public bool IsCoordinates { get; }
    public int X { get; }
    public int Y { get; }
    public int? Z { get; }

    /// <summary>The map, 0..5; null when not given.</summary>
    public int? Facet { get; }

    public bool IsEmpty => Words.Length == 0;

    public SearchQuery(string raw)
    {
        Raw = raw ?? "";
        string trimmed = Raw.Trim();

        Match c = Coordinates.Match(trimmed);
        if (c.Success)
        {
            IsCoordinates = true;
            X = int.Parse(c.Groups["x"].Value, CultureInfo.InvariantCulture);
            Y = int.Parse(c.Groups["y"].Value, CultureInfo.InvariantCulture);
            if (c.Groups["z"].Success)
            {
                Z = int.Parse(c.Groups["z"].Value, CultureInfo.InvariantCulture);
            }

            if (c.Groups["m"].Success)
            {
                Facet = int.Parse(c.Groups["m"].Value, CultureInfo.InvariantCulture);
            }
        }

        var words = new List<string>();
        foreach (string w in trimmed.ToLowerInvariant().Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(w);
        }

        // "gump 100": a kind word then one number.
        if (words.Count == 2 && KindWords.TryGetValue(words[0], out string[] kinds) && TryNumber(words[1], out long kn))
        {
            KindFilter = kinds;
            Number = kn;
            words.RemoveAt(0);
        }
        else if (words.Count == 1 && TryNumber(words[0], out long n))
        {
            Number = n;
        }

        Words = words.ToArray();
        Text = string.Join(' ', Words);
    }

    public static bool TryNumber(string word, out long value)
    {
        value = 0;
        if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return word.Length > 2 && long.TryParse(word.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return word.Length > 0 && word.Length <= 9 && long.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>"map1", "m2": a bare facet, for "go to Britain on map1" style queries.</summary>
    public static int? MapWord(string word)
    {
        Match m = MapOnly.Match(word);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public bool Allows(string kind) => KindFilter == null || Array.IndexOf(KindFilter, kind) >= 0;
}
#endif
