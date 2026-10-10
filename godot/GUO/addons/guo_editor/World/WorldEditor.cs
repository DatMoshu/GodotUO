#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using GUO.Assets;

/// <summary>The World tab's tools (plan §5 phase 3).</summary>
public enum WorldTool
{
    Select,
    Stamp,
    Erase,
    Raise,
    Lower,
    Hue,

    // World objects (ADR-0014): the project's shard/objects.json.
    PlaceItem,
    PlaceSpawner,
    MoveObject,
    DeleteObject,

    // Map layers' tools (ADR-0027).
    Measure,
    Route,
    Pin,

    // The Multi Editor's World selection (ADR-0031): two corner clicks.
    Area,
    Brush,

    // The Regions dock: click a shard region on the map to edit it.
    PickRegion,
}

/// <summary>
/// Edits to the world project, block by block, with undo. Every edit reads
/// the block (from the project, or from the map when the project does not
/// have it yet), changes it, writes it, and lays the project over the map
/// again, so the view shows the edit at once. The block file is the save:
/// there is no unsaved state to lose.
/// </summary>
/// <remarks>
/// Undo is the plan's block ring buffer (§4.3): each entry holds the block
/// file's text before and after, null meaning "the install's block". Undo
/// writes the before text back (or removes the file), redo the after text.
/// The ring keeps the last <see cref="Depth"/> edits.
/// </remarks>
internal sealed class WorldEditor
{
    public const int Depth = 200;

    private readonly WorldHost _host;
    private readonly LinkedList<List<Change>> _undo = new();
    private readonly Stack<List<Change>> _redo = new();

    private sealed record Change(int Facet, int Bx, int By, string Before, string After, string What);

    public WorldEditor(WorldHost host)
    {
        _host = host;
    }

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public string LastWhat => _undo.Last?.Value.FirstOrDefault()?.What;

    /// <summary>Raised after any edit, undo or redo, with a line describing it.</summary>
    public event Action<string> Changed;

    /// <summary>
    /// Raised after a local edit, undo or redo with the block as it now is
    /// (the project's, or the install's when undo removed it): what the live
    /// tier sends to the shard. Not raised for remote blocks.
    /// </summary>
    public event Action<WorldBlock> BlockWritten;

    private void Written(int facet, int bx, int by)
    {
        if (BlockWritten == null || _host.Project == null)
        {
            return;
        }

        WorldBlock now = _host.Project.BlockText(facet, bx, by) != null
            ? WorldProject.ReadBlock(_host.Project.BlockPath(facet, bx, by))
            : WorldProject.Capture(Maps, facet, bx, by);
        if (now != null)
        {
            BlockWritten(now);
        }
    }

    /// <summary>
    /// Another editor's block, from the shard: written to this project and
    /// laid over the map, as the last write wins. Kept out of undo (it is not
    /// this editor's to take back) and not sent on again.
    /// </summary>
    public bool ApplyRemote(WorldBlock block, string from)
    {
        if (_host.Project == null)
        {
            return false;
        }

        _host.Project.WriteBlock(block);
        _host.ApplyOverlay();
        Changed?.Invoke($"remote: {from} changed block {block.Bx},{block.By}");
        return true;
    }

    private MapLoader Maps => Client.Game.UO.FileManager.Maps;

    /// <summary>
    /// Changes one block. False when there is no project, the block is not
    /// in the map, or the change left the block as it was.
    /// </summary>
    public bool Edit(int facet, int bx, int by, Action<WorldBlock> change, string what)
        => EditBatch(facet, new Dictionary<(int, int), Action<WorldBlock>> { [(bx, by)] = change }, what);

    /// <summary>A brush stroke is one undo entry, even when it crosses block boundaries.</summary>
    internal bool EditBatch(int facet, IReadOnlyDictionary<(int X, int Y), Action<WorldBlock>> edits, string what)
    {
        WorldProject project = _host.Project;
        if (project == null)
        {
            return false;
        }

        var changes = new List<Change>();
        try
        {
            foreach (var edit in edits)
            {
                int bx = edit.Key.X, by = edit.Key.Y;
                string before = project.BlockText(facet, bx, by);
                WorldBlock block = before != null
                    ? WorldProject.ReadBlock(project.BlockPath(facet, bx, by))
                    : WorldProject.Capture(Maps, facet, bx, by);
                if (block == null) continue;

                // A filtered/no-op stroke must not create an overlay or consume undo history.
                ushort[] ids = block.LandId.ToArray();
                sbyte[] heights = block.LandZ.ToArray();
                WorldStatic[] statics = block.Statics.ToArray();
                edit.Value(block);
                if (ids.SequenceEqual(block.LandId) && heights.SequenceEqual(block.LandZ) && statics.SequenceEqual(block.Statics)) continue;
                changes.Add(new Change(facet, bx, by, before, null, what));
                project.WriteBlock(block);
                string after = project.BlockText(facet, bx, by);
                changes[^1] = new Change(facet, bx, by, before, after, what);
            }
        }
        catch
        {
            foreach (Change c in changes) project.SetBlockText(c.Facet, c.Bx, c.By, c.Before);
            _host.ApplyOverlay();
            throw;
        }
        if (changes.Count == 0) return false;
        _undo.AddLast(changes);
        while (_undo.Count > Depth)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
        _host.ApplyOverlay();
        Changed?.Invoke(what);
        foreach (Change c in changes) Written(c.Facet, c.Bx, c.By);
        return true;
    }

    public bool Undo()
    {
        if (_undo.Last == null || _host.Project == null)
        {
            return false;
        }

        List<Change> changes = _undo.Last.Value;
        _undo.RemoveLast();
        foreach (Change c in changes) _host.Project.SetBlockText(c.Facet, c.Bx, c.By, c.Before);
        _redo.Push(changes);
        _host.ApplyOverlay();
        Changed?.Invoke($"undo: {changes[0].What}");
        foreach (Change c in changes) Written(c.Facet, c.Bx, c.By);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0 || _host.Project == null)
        {
            return false;
        }

        List<Change> changes = _redo.Pop();
        foreach (Change c in changes) _host.Project.SetBlockText(c.Facet, c.Bx, c.By, c.After);
        _undo.AddLast(changes);
        _host.ApplyOverlay();
        Changed?.Invoke($"redo: {changes[0].What}");
        foreach (Change c in changes) Written(c.Facet, c.Bx, c.By);
        return true;
    }

    /// <summary>Forgets the history (a different project was opened).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    // --- tools ----------------------------------------------------------

    public bool Stamp(int facet, int x, int y, sbyte z, ushort id, ushort hue) =>
        Edit(facet, x >> 3, y >> 3, b => b.Statics.Add(new WorldStatic
        {
            Id = id,
            X = (byte)(x & 7),
            Y = (byte)(y & 7),
            Z = z,
            Hue = hue,
        }), $"stamp 0x{id:X4} at {x},{y} z {z}");

    public bool Erase(int facet, int x, int y, sbyte z, ushort id) =>
        Edit(facet, x >> 3, y >> 3, b =>
        {
            int i = b.Statics.FindIndex(s => s.Id == id && s.X == (x & 7) && s.Y == (y & 7) && s.Z == z);
            if (i >= 0)
            {
                b.Statics.RemoveAt(i);
            }
        }, $"erase 0x{id:X4} at {x},{y} z {z}");

    public bool Altitude(int facet, int x, int y, int delta) =>
        Edit(facet, x >> 3, y >> 3, b =>
        {
            int i = (y & 7) * 8 + (x & 7);
            b.LandZ[i] = (sbyte)Math.Clamp(b.LandZ[i] + delta, sbyte.MinValue, sbyte.MaxValue);
        }, $"altitude {(delta > 0 ? "+" : "")}{delta} at {x},{y}");

    public bool SetHue(int facet, int x, int y, sbyte z, ushort id, ushort hue) =>
        Edit(facet, x >> 3, y >> 3, b =>
        {
            int i = b.Statics.FindIndex(s => s.Id == id && s.X == (x & 7) && s.Y == (y & 7) && s.Z == z);
            if (i >= 0)
            {
                WorldStatic s = b.Statics[i];
                s.Hue = hue;
                b.Statics[i] = s;
            }
        }, $"hue 0x{hue:X4} on 0x{id:X4} at {x},{y} z {z}");

    /// <summary>
    /// Every static in a rectangle of cells, as the project (or the install) has them: x, y (world cells) and
    /// the static. Each block is read once.
    /// </summary>
    public List<(int X, int Y, WorldStatic S)> StaticsInRect(int facet, int x0, int y0, int x1, int y1)
    {
        var list = new List<(int, int, WorldStatic)>();
        WorldProject project = _host.Project;
        for (int bx = x0 >> 3; bx <= x1 >> 3; bx++)
        {
            for (int by = y0 >> 3; by <= y1 >> 3; by++)
            {
                WorldBlock b = project?.BlockText(facet, bx, by) != null
                    ? WorldProject.ReadBlock(project.BlockPath(facet, bx, by))
                    : WorldProject.Capture(Maps, facet, bx, by);
                if (b == null)
                {
                    continue;
                }

                foreach (WorldStatic s in b.Statics)
                {
                    int x = (bx << 3) + s.X, y = (by << 3) + s.Y;
                    if (x >= x0 && x <= x1 && y >= y0 && y <= y1)
                    {
                        list.Add((x, y, s));
                    }
                }
            }
        }

        return list;
    }

    /// <summary>Statics on a cell as the project (or the install) has them, bottom first.</summary>
    public List<WorldStatic> StaticsAt(int facet, int x, int y)
    {
        WorldProject project = _host.Project;
        int bx = x >> 3, by = y >> 3;
        WorldBlock b = project?.BlockText(facet, bx, by) != null
            ? WorldProject.ReadBlock(project.BlockPath(facet, bx, by))
            : WorldProject.Capture(Maps, facet, bx, by);
        return b?.Statics.Where(s => s.X == (x & 7) && s.Y == (y & 7)).OrderBy(s => s.Z).ToList() ?? new List<WorldStatic>();
    }
}
#endif
