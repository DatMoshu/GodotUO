#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The Multi Editor's generator side (ADR-0031, phase 2): one <c>tools/multi serve</c> process shared by the
/// Generate panel and the rotate, mirror, import and export operations; the panel's live preview as a ghost on the
/// canvas; Apply as one undo step; and the canvas tools that tell the panel where to work.
/// </summary>
public partial class MultiEditView
{
    private MultiGenerateClient _gen;
    private GeneratePanel _genPanel;
    private bool _genActive;
    private bool _ghostIsGen;
    private bool _applying;

    /// <summary>The one generator process; null before the view is built.</summary>
    public MultiGenerateClient GenClient => _gen;

    public GeneratePanel GeneratePanel => _genPanel;

    /// <summary>The preview on the canvas right now came from the generator (and not from a paste).</summary>
    public bool GeneratorGhostShown => _ghostIsGen && _canvas.GhostCount > 0;

    private void BuildGenerator()
    {
        _gen = new MultiGenerateClient();
        _genPanel = new GeneratePanel(_gen);
        AddSidePanel("Generate", _genPanel);
        _genPanel.Generated += OnGenerated;
        _genPanel.GeneratorChanged += _ => RefreshToolContext();
        _genPanel.ApplyRequested += replace => _ = ApplyGeneratedAsync(replace);
        _genPanel.PreviewCleared += () =>
        {
            _canvas.ClearGhost();
            _canvas.ClearToolState();
        };
        _tabs.TabChanged += _ => OnTabChanged();
        _canvas.ToolContextChanged += RefreshToolContext;
        _canvas.ToolAction += OnToolAction;
        _canvas.SelectionEdited += () =>
        {
            if (_genActive && _genPanel.Generator == "roof")
            {
                RefreshToolContext();
            }
        };
    }

    private void OnTabChanged()
    {
        bool now = _tabs.GetCurrentTabControl() == _genPanel;
        if (now == _genActive)
        {
            return;
        }

        _genActive = now;
        if (now)
        {
            RefreshToolContext();
        }
        else if (_ghostIsGen)
        {
            _canvas.ClearGhost();
        }
    }

    /// <summary>Brings the Generate tab forward (the placement tools do it).</summary>
    public void ShowGenerateTab()
    {
        _tabs.CurrentTab = _genPanel.GetIndex();
        OnTabChanged();
    }

    private void OnToolAction(string action)
    {
        if (action == "wall-finish")
        {
            _ = ApplyGeneratedAsync(false);
        }
    }

    private void OnGenerated(string op, GenResult res)
    {
        if (!_genActive)
        {
            return;
        }

        if (res.Ok && res.Components.Count > 0)
        {
            _canvas.SetGhost(res.Components.Select(c => new MultiPart { Id = (ushort)c.Item, X = (short)c.X, Y = (short)c.Y, Z = (short)c.Z, Shown = c.Visible }));
            _ghostIsGen = true;
        }
        else if (_ghostIsGen)
        {
            _canvas.ClearGhost();
        }
    }

    /// <summary>The generators' place, from the canvas: a path, a box (or the selection), an anchor and a direction.</summary>
    public JsonObject ToolContext(string op)
    {
        var ctx = new JsonObject();
        switch (op)
        {
            case "autowall":
                if (_canvas.WallPath.Count >= 2)
                {
                    ctx["path"] = new JsonArray(_canvas.WallPath.Select(c => (JsonNode)new JsonArray(c.X, c.Y)).ToArray());
                    ctx["z"] = _canvas.EditZ;
                    if (_canvas.WallClosed)
                    {
                        ctx["closed"] = true;
                    }
                }

                break;
            case "roof":
                if (_canvas.RoofBox is { } b)
                {
                    ctx["boxes"] = new JsonArray((JsonNode)new JsonArray(b.X0, b.Y0, b.X1, b.Y1));
                    ctx["z"] = Stories.ZOf(Math.Max(0, Stories.StoryOf(_canvas.EditZ)) + 1);
                }
                else if (_doc.Selection.Count > 0)
                {
                    List<MultiPart> sel = _doc.Parts.Where(p => _doc.Selection.Contains(p.Uid)).ToList();
                    ctx["boxes"] = new JsonArray((JsonNode)new JsonArray(sel.Min(p => (int)p.X), sel.Min(p => (int)p.Y), sel.Max(p => (int)p.X), sel.Max(p => (int)p.Y)));
                    ctx["z"] = Stories.ZOf(Math.Max(0, Stories.StoryOf(sel.Min(p => (int)p.Z))) + 1);
                }

                break;
            case "stairs":
                if (_canvas.Flight is { } f)
                {
                    ctx["at"] = new JsonArray(f.X, f.Y);
                    ctx["rise"] = f.Rise;
                    ctx["z"] = _canvas.EditZ;
                }

                break;
        }

        return ctx;
    }

    /// <summary>Hands the panel the place its generator needs and regenerates (the house needs none).</summary>
    public void RefreshToolContext()
    {
        if (_genPanel == null || _applying)
        {
            return;
        }

        string op = _genPanel.Generator;
        JsonObject ctx = ToolContext(op);
        _genPanel.Context = ctx;
        if (op == "house" || ctx.Count > 0)
        {
            if (_genActive)
            {
                _genPanel.Regenerate();
            }
        }
        else
        {
            if (_ghostIsGen)
            {
                _canvas.ClearGhost();
            }

            _genPanel.SetHint(op switch
            {
                "autowall" => "Wall tool (W): click the path's points, Enter to finish (click the first point to close)",
                "roof" => "Roof tool (O): drag a box over the walls, or select the walls first",
                _ => "Stairs tool (T): drag from the foot in the direction the flight rises",
            });
        }
    }

    /// <summary>Applies the generator's answer to the document as one undo step; false when there was nothing to apply.</summary>
    public async Task<bool> ApplyGeneratedAsync(bool replace)
    {
        GenResult r = await _genPanel.NowAsync();
        if (r is not { Ok: true } || r.Components.Count == 0)
        {
            _status.Text = r?.Error ?? "nothing generated yet";
            return false;
        }

        string op = _genPanel.Generator;
        var parts = r.Components.Select(c => new GeneratedPart((ushort)c.Item, c.X, c.Y, c.Z, c.Visible)).ToList();
        Action<List<MultiPart>> extra = null;
        if (op == "stairs" && r.Raw["holes"] is JsonArray holes && _genPanel.Context["z"] is JsonNode zn)
        {
            // The flight arrives on the floor above: open that floor where the flight comes up.
            int zAbove = (int)zn + Stories.Height;
            var cells = holes.Select(h => ((int)h[0], (int)h[1])).ToHashSet();
            extra = list => list.RemoveAll(p => p.Z == zAbove && cells.Contains((p.X, p.Y)) && _canvas.IsFloorPart(p));
        }

        string name = op == "house" ? $"house_{_genPanel.StyleKey}" : op;
        PushComponentsWith($"{op} {_genPanel.StyleKey}", name, parts, replace, extra);
        _applying = true;
        _canvas.ClearGhost();
        _ghostIsGen = false;
        _canvas.ClearToolState();
        _applying = false;
        return true;
    }

    private void DisposeGenerator()
    {
        if (_genPanel != null)
        {
            _genPanel.Generated -= OnGenerated;
        }

        _gen?.Dispose();
        _gen = null;
    }
}
#endif
