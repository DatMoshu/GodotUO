#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Rotate 90 and mirror (ADR-0031, phase 2): on the selection, or on the whole multi when nothing is selected, through
/// <c>tools/multi</c>'s <c>rotate</c> and <c>mirror</c> (which remap the ids of walls, roofs, stairs and doors so a rotated
/// building still reads right). One undo step each.
/// </summary>
public partial class MultiEditView
{
    private void BuildTransformButtons(Control row)
    {
        row.AddChild(new VSeparator());
        row.AddChild(Tip(Btn("Rot 90", () => _ = TransformAsync("rotate", 1)), "Rotate the selection (or the whole multi) 90 degrees clockwise seen from above"));
        row.AddChild(Tip(Btn("Flip E-W", () => _ = TransformAsync("mirror", axis: "x")), "Mirror the selection (or the whole multi): east becomes west"));
        row.AddChild(Tip(Btn("Flip N-S", () => _ = TransformAsync("mirror", axis: "y")), "Mirror the selection (or the whole multi): north becomes south"));
    }

    /// <summary>
    /// Runs rotate (<paramref name="turns"/> quarter turns clockwise) or mirror (<paramref name="axis"/> x or y) on the
    /// selection or the whole multi. A selection turns about its own centre and keeps its top corner where it was; the
    /// whole multi turns about its centre cell (0,0). Returns whether anything changed.
    /// </summary>
    public async Task<bool> TransformAsync(string op, int turns = 1, string axis = "x")
    {
        bool whole = _doc.Selection.Count == 0;
        List<MultiPart> targets = whole ? _doc.Parts.ToList() : _doc.Parts.Where(p => _doc.Selection.Contains(p.Uid)).ToList();
        if (targets.Count == 0)
        {
            return false;
        }

        int px = 0, py = 0, minX = targets.Min(p => (int)p.X), minY = targets.Min(p => (int)p.Y);
        if (!whole)
        {
            px = (minX + targets.Max(p => (int)p.X)) / 2;
            py = (minY + targets.Max(p => (int)p.Y)) / 2;
        }

        var comps = new JsonArray();
        foreach (MultiPart p in targets)
        {
            comps.Add((JsonNode)new JsonArray(p.Id, p.X - px, p.Y - py, p.Z, p.Shown ? 1 : 0));
        }

        var args = new JsonObject { ["components"] = comps };
        if (op == "rotate")
        {
            args["turns"] = ((turns % 4) + 4) % 4;
        }
        else
        {
            args["axis"] = axis;
        }

        if (op == "rotate" && (int)args["turns"] == 0)
        {
            return false;
        }

        GenResult r = MultiGenerateClient.Parse(await _gen.RequestAsync(op, args));
        if (!r.Ok)
        {
            _status.Text = $"{op}: {r.Error}";
            return false;
        }

        int ox = 0, oy = 0;
        if (!whole && r.Components.Count > 0)
        {
            ox = minX - (r.Components.Min(c => c.X) + px);          // keep the selection's top corner in place
            oy = minY - (r.Components.Min(c => c.Y) + py);
        }

        bool sameCount = r.Components.Count == targets.Count;
        var made = new List<MultiPart>();
        for (int i = 0; i < r.Components.Count; i++)
        {
            GenComponent c = r.Components[i];
            made.Add(_doc.Make((ushort)c.Item, c.X + px + ox, c.Y + py + oy, c.Z, c.Visible, sameCount ? targets[i].Hue : (ushort)0));
        }

        var gone = new HashSet<int>(targets.Select(t => t.Uid));
        string what = op == "rotate" ? $"rotate {turns * 90}" : $"mirror {axis}";
        bool changed = _doc.Do($"{what} {(whole ? "all" : targets.Count + " selected")}", list =>
        {
            int at = list.FindIndex(p => gone.Contains(p.Uid));
            list.RemoveAll(p => gone.Contains(p.Uid));
            list.InsertRange(Math.Clamp(at, 0, list.Count), made);
        });
        if (changed && !whole)
        {
            _doc.Selection.Clear();
            foreach (MultiPart p in made)
            {
                _doc.Selection.Add(p.Uid);
            }
        }

        UpdateSelectionUi();
        return changed;
    }
}
#endif
