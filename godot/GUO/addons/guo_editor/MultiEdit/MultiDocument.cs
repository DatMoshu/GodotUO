#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>One component of a multi in the editor (ADR-0031). <see cref="Uid"/> is its identity for selection.</summary>
public struct MultiPart
{
    public int Uid;
    public ushort Id;
    public short X, Y, Z;
    public bool Shown;
    public ushort Hue;

    public readonly bool SameAs(in MultiPart o) =>
        Id == o.Id && X == o.X && Y == o.Y && Z == o.Z && Shown == o.Shown && Hue == o.Hue;
}

/// <summary>The client's story heights: floor n is at z 7 + 20 n, the foundation below 7.</summary>
public static class Stories
{
    public const int FloorZ = 7;
    public const int Height = 20;
    public const int Max = 8;

    public static int ZOf(int story) => FloorZ + Height * story;

    /// <summary>-1 for the foundation (z below 7), else the story index (HouseCustomizationManager.GenerateFloorPlace).</summary>
    public static int StoryOf(int z) => z < FloorZ ? -1 : Math.Min(Max - 1, (z - FloorZ) / Height);
}

/// <summary>
/// The parts of one multi with grouped undo (ADR-0031). Every user action is one <see cref="Do"/>: the
/// history keeps a snapshot per action, so the list can jump to any state and redo survives a jump back.
/// Selection is by part identity and is not history.
/// </summary>
public sealed class MultiDocument
{
    public const int HistoryCap = 500;

    private sealed record Snap(string Name, MultiPart[] Parts);

    private readonly List<Snap> _history = new();
    private int _cursor;
    private int _nextUid = 1;

    public List<MultiPart> Parts { get; private set; } = new();
    public HashSet<int> Selection { get; } = new();
    public string Name { get; set; } = "new multi";
    public int? Source { get; set; }

    /// <summary>Raised after any change of the parts (an action, undo, redo, a jump).</summary>
    public event Action Changed;

    public MultiDocument()
    {
        Reset(Array.Empty<MultiPart>(), "new");
    }

    public int Cursor => _cursor;
    public int HistoryCount => _history.Count;
    public IReadOnlyList<string> HistoryNames => _history.Select(h => h.Name).ToList();
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _history.Count - 1;

    /// <summary>Starts again from these parts with a fresh history.</summary>
    public void Reset(IEnumerable<MultiPart> parts, string what)
    {
        Parts = new List<MultiPart>();
        foreach (MultiPart p in parts)
        {
            MultiPart q = p;
            q.Uid = _nextUid++;
            Parts.Add(q);
        }

        Selection.Clear();
        _history.Clear();
        _history.Add(new Snap(what, Parts.ToArray()));
        _cursor = 0;
        Changed?.Invoke();
    }

    public MultiPart Make(ushort id, int x, int y, int z, bool shown = true, ushort hue = 0) =>
        new() { Uid = _nextUid++, Id = id, X = (short)x, Y = (short)y, Z = (short)z, Shown = shown, Hue = hue };

    /// <summary>
    /// One named transaction. <paramref name="change"/> edits <see cref="Parts"/>; nothing is recorded when
    /// it changed nothing. Returns whether it did.
    /// </summary>
    public bool Do(string name, Action<List<MultiPart>> change)
    {
        MultiPart[] before = Parts.ToArray();
        change(Parts);
        if (SameParts(before, Parts))
        {
            return false;
        }

        if (_cursor < _history.Count - 1)
        {
            _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
        }

        _history.Add(new Snap(name, Parts.ToArray()));
        if (_history.Count > HistoryCap)
        {
            _history.RemoveAt(0);
        }

        _cursor = _history.Count - 1;
        PruneSelection();
        Changed?.Invoke();
        return true;
    }

    public bool Undo() => JumpTo(_cursor - 1);

    public bool Redo() => JumpTo(_cursor + 1);

    /// <summary>Goes to history entry <paramref name="index"/> (0 is the state the document was opened in).</summary>
    public bool JumpTo(int index)
    {
        if (index < 0 || index >= _history.Count || index == _cursor)
        {
            return false;
        }

        _cursor = index;
        Parts = _history[index].Parts.ToList();
        PruneSelection();
        Changed?.Invoke();
        return true;
    }

    private void PruneSelection()
    {
        if (Selection.Count == 0)
        {
            return;
        }

        var live = new HashSet<int>(Parts.Select(p => p.Uid));
        Selection.RemoveWhere(u => !live.Contains(u));
    }

    private static bool SameParts(MultiPart[] a, List<MultiPart> b)
    {
        if (a.Length != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Uid != b[i].Uid || !a[i].SameAs(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    // --- operations (each is one transaction) --------------------------------

    public bool Place(ushort id, int x, int y, int z, ushort hue = 0, bool replaceCell = false) =>
        PlaceMany($"draw 0x{id:X4} at {x},{y},{z}", id, new[] { (x, y) }, z, hue, replaceCell);

    /// <summary>Places the same item on several cells at z; a cell that already holds it at that z is left alone.</summary>
    public bool PlaceMany(string name, ushort id, IEnumerable<(int X, int Y)> cells, int z, ushort hue = 0, bool replaceCell = false)
    {
        var list = cells.Distinct().ToList();
        return Do(name, parts =>
        {
            foreach ((int x, int y) in list)
            {
                if (replaceCell)
                {
                    parts.RemoveAll(p => p.X == x && p.Y == y && p.Z == z);
                }
                else if (parts.Any(p => p.X == x && p.Y == y && p.Z == z && p.Id == id))
                {
                    continue;
                }

                parts.Add(Make(id, x, y, z, true, hue));
            }
        });
    }

    public bool Remove(IEnumerable<int> uids, string name = null)
    {
        var set = new HashSet<int>(uids);
        return Do(name ?? $"erase {set.Count} component(s)", parts => parts.RemoveAll(p => set.Contains(p.Uid)));
    }

    public bool Move(IEnumerable<int> uids, int dx, int dy, int dz, string name = null)
    {
        var set = new HashSet<int>(uids);
        return Do(name ?? $"move {set.Count} by {dx},{dy},{dz}", parts =>
        {
            for (int i = 0; i < parts.Count; i++)
            {
                if (set.Contains(parts[i].Uid))
                {
                    MultiPart p = parts[i];
                    p.X = (short)(p.X + dx);
                    p.Y = (short)(p.Y + dy);
                    p.Z = (short)Math.Clamp(p.Z + dz, short.MinValue, short.MaxValue);
                    parts[i] = p;
                }
            }
        });
    }

    public bool SetHue(IEnumerable<int> uids, ushort hue)
    {
        var set = new HashSet<int>(uids);
        return Do($"hue 0x{hue:X4} on {set.Count}", parts =>
        {
            for (int i = 0; i < parts.Count; i++)
            {
                if (set.Contains(parts[i].Uid))
                {
                    MultiPart p = parts[i];
                    p.Hue = hue;
                    parts[i] = p;
                }
            }
        });
    }

    public bool SetShown(IEnumerable<int> uids, bool shown)
    {
        var set = new HashSet<int>(uids);
        return Do($"{(shown ? "show" : "hide")} {set.Count}", parts =>
        {
            for (int i = 0; i < parts.Count; i++)
            {
                if (set.Contains(parts[i].Uid))
                {
                    MultiPart p = parts[i];
                    p.Shown = shown;
                    parts[i] = p;
                }
            }
        });
    }

    /// <summary>The corners of the parts' bounding box, or null when empty.</summary>
    public (int X0, int Y0, int X1, int Y1)? Bounds() =>
        Parts.Count == 0 ? null : (Parts.Min(p => p.X), Parts.Min(p => p.Y), Parts.Max(p => p.X), Parts.Max(p => p.Y));
}
#endif
