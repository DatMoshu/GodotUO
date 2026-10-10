// The god view's change-only updates (Admin tab, AD2a): what changed since the
// last push to one editor, and the Find match. GodView.cs reads the world into
// entries; this file only compares them.
//
// Each entry carries a signature: a hash of every field its row shows. Only an
// entry whose signature moved is built into JSON, so a quiet facet of 15,000
// mobiles costs a hash each per push, not a JSON row each.
//
// Plain .NET, no ModernUO types: tests/ compiles this file on its own.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace GUO.EditorBridge;

/// <summary>One player, NPC or spawner as the diff sees it: its serial, the hash of its row, and what builds the row.</summary>
public readonly record struct GodViewEntry(uint Serial, int Signature, object Source);

public sealed class GodViewDiff
{
    // Each row the editor holds, by serial, as the signature it was last sent with.
    private readonly Dictionary<uint, int> _sent = new();

    /// <summary>The push number: 1 for the full list, then one more for each push that changed something.</summary>
    public long Seq { get; private set; }

    /// <summary>Rows the editor holds.</summary>
    public int Held => _sent.Count;

    /// <summary>
    /// The rows that are new or changed since the last call (built with
    /// <paramref name="build"/>), and the serials that are gone. The first call
    /// (and the first after <see cref="Reset"/>) returns every row.
    /// </summary>
    public (JsonArray Upsert, JsonArray Removed) Next(IReadOnlyList<GodViewEntry> entries, Func<object, JsonObject> build)
    {
        var upsert = new JsonArray();
        var removed = new JsonArray();
        var seen = new HashSet<uint>();
        foreach (GodViewEntry e in entries)
        {
            if (!seen.Add(e.Serial))
            {
                continue;
            }

            if (!_sent.TryGetValue(e.Serial, out int was) || was != e.Signature)
            {
                _sent[e.Serial] = e.Signature;
                upsert.Add(build(e.Source));
            }
        }

        if (_sent.Count > seen.Count)
        {
            var gone = new List<uint>();
            foreach (uint serial in _sent.Keys)
            {
                if (!seen.Contains(serial))
                {
                    gone.Add(serial);
                }
            }

            gone.Sort();
            foreach (uint serial in gone)
            {
                _sent.Remove(serial);
                removed.Add(serial);
            }
        }

        if (Seq == 0 || upsert.Count > 0 || removed.Count > 0)
        {
            Seq++;
        }

        return (upsert, removed);
    }

    /// <summary>Forgets what was sent: the next call returns every row.</summary>
    public void Reset()
    {
        _sent.Clear();
        Seq = 0;
    }

    /// <summary>
    /// Keeps the first <paramref name="cap"/> entries by serial, so a facet over
    /// the cap sends the same rows each push instead of a different slice.
    /// </summary>
    public static List<GodViewEntry> Cap(List<GodViewEntry> entries, int cap, out bool truncated)
    {
        truncated = entries.Count > cap;
        if (!truncated)
        {
            return entries;
        }

        entries.Sort((a, b) => a.Serial.CompareTo(b.Serial));
        return entries.GetRange(0, cap);
    }

    /// <summary>
    /// The Find match: a serial ("0x1A2B" or decimal) matches that serial; any
    /// other text matches when a name contains it, ignoring case.
    /// </summary>
    public static bool Matches(string query, uint serial, params string[] names)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        if (ParseSerial(query) is uint wanted)
        {
            return wanted == serial;
        }

        foreach (string name in names)
        {
            if (name != null && name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A serial typed as "0x1A2B" or as digits only, or null.</summary>
    public static uint? ParseSerial(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex) ? hex : null;
        }

        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return null;
            }
        }

        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint dec) ? dec : null;
    }
}
