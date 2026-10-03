#if TOOLS
namespace GUO.Editor;

using System;

/// <summary>
/// The matcher behind F3. A score of 0 is no match; higher is better. In
/// order of strength: the whole text, a prefix, a word start, a substring,
/// a subsequence that likes word starts and runs ("bp" finds "Backpack" and
/// "Bulk Pack" alike), and last one typo in a word ("bakcpack").
/// </summary>
public static class Fuzzy
{
    public const int Exact = 1000;

    /// <summary>
    /// Scores one lower-case word against lower-case text. The text is the
    /// thing searched (a title, a path); the word has no spaces.
    /// </summary>
    public static int Score(string q, string text)
    {
        if (q.Length == 0 || text.Length == 0)
        {
            return 0;
        }

        if (text == q)
        {
            return Exact;
        }

        if (text.StartsWith(q, StringComparison.Ordinal))
        {
            return 900 - Math.Min(text.Length - q.Length, 60);
        }

        int at = text.IndexOf(q, StringComparison.Ordinal);
        if (at >= 0)
        {
            // Prefer a match that starts a word, and the earlier one.
            bool wordStart = !char.IsLetterOrDigit(text[at - 1]);
            for (int from = at; !wordStart && from >= 0;)
            {
                from = from + 1 < text.Length ? text.IndexOf(q, from + 1, StringComparison.Ordinal) : -1;
                if (from > 0 && !char.IsLetterOrDigit(text[from - 1]))
                {
                    at = from;
                    wordStart = true;
                }
            }

            return (wordStart ? 800 : 600) - Math.Min(at, 80);
        }

        int sub = Subsequence(q, text);
        if (sub > 0)
        {
            return sub;
        }

        return q.Length >= 4 ? Typo(q, text) : 0;
    }

    /// <summary>Best alignment of q as a subsequence of text, or 0.</summary>
    private static int Subsequence(string q, string text)
    {
        if (q.Length < 2 || q.Length > text.Length)
        {
            return 0;
        }

        // Cheap rejection first: nearly every candidate stops here.
        int pos = 0;
        foreach (char ch in q)
        {
            pos = text.IndexOf(ch, pos);
            if (pos < 0)
            {
                return 0;
            }

            pos++;
        }

        const int gap = 3;          // per skipped character
        const int word = 30;        // a character that starts a word
        const int run = 18;         // a character right after its predecessor
        const int NoScore = int.MinValue / 2;

        int n = text.Length, m = q.Length;
        // prev[i]: best score matching q[0..j) with q[j-1] at text[i].
        var prev = new int[n];
        var cur = new int[n];
        for (int i = 0; i < n; i++)
        {
            prev[i] = text[i] == q[0] ? 10 + (IsStart(text, i) ? word : 0) - Math.Min(i, 12) : NoScore;
        }

        for (int j = 1; j < m; j++)
        {
            int bestEarlier = NoScore;   // max over k <= i - 2 of prev[k] + gap * k
            for (int i = 0; i < n; i++)
            {
                cur[i] = NoScore;
                if (i >= 2 && prev[i - 2] > NoScore)
                {
                    bestEarlier = Math.Max(bestEarlier, prev[i - 2] + gap * (i - 2));
                }

                if (text[i] != q[j])
                {
                    continue;
                }

                int s = NoScore;
                if (i >= 1 && prev[i - 1] > NoScore)
                {
                    s = prev[i - 1] + run;
                }

                if (bestEarlier > NoScore)
                {
                    s = Math.Max(s, bestEarlier - gap * (i - 1));
                }

                if (s > NoScore)
                {
                    cur[i] = s + 10 + (IsStart(text, i) ? word : 0);
                }
            }

            (prev, cur) = (cur, prev);
        }

        int best = NoScore;
        foreach (int s in prev)
        {
            best = Math.Max(best, s);
        }

        if (best <= NoScore)
        {
            return 0;
        }

        // An abbreviation of one word ("bp" for backpack) beats letters
        // picked from several, and shorter text beats longer.
        int firstWord = 0;
        while (firstWord < n && char.IsLetterOrDigit(text[firstWord]))
        {
            firstWord++;
        }

        int p = 0;
        foreach (char ch in q)
        {
            p = p >= firstWord ? -1 : text.IndexOf(ch, p, firstWord - p);
            if (p < 0)
            {
                break;
            }

            p++;
        }

        int inWord = p >= 0 ? 60 : 0;
        return Math.Clamp(200 + best + inWord - Math.Min(n, 60) / 2, 120, 590);
    }

    private static bool IsStart(string text, int i) => i == 0 || !char.IsLetterOrDigit(text[i - 1]);

    /// <summary>One typo, in a word of the text, or in the start of one.</summary>
    private static int Typo(string q, string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            int start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            int len = i - start;
            if (len < q.Length - 1)
            {
                continue;
            }

            if (len <= q.Length + 1 && WithinOneEdit(q, text.AsSpan(start, len)))
            {
                return 340;
            }

            // A typo inside what was typed of a longer word ("bakcpa" for backpack).
            if (len > q.Length && WithinOneEdit(q, text.AsSpan(start, q.Length)))
            {
                return 300;
            }
        }

        return 0;
    }

    /// <summary>True when a and b differ by one insertion, deletion, substitution or adjacent swap.</summary>
    public static bool WithinOneEdit(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        if (i == a.Length && i == b.Length)
        {
            return true;
        }

        if (a.Length == b.Length)
        {
            if (a[(i + 1)..].SequenceEqual(b[(i + 1)..]))
            {
                return true;                      // substitution
            }

            return i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a[(i + 2)..].SequenceEqual(b[(i + 2)..]);
        }

        ReadOnlySpan<char> longer = a.Length > b.Length ? a : b;
        ReadOnlySpan<char> shorter = a.Length > b.Length ? b : a;
        return longer[(i + 1)..].SequenceEqual(shorter[i..]);
    }
}
#endif
