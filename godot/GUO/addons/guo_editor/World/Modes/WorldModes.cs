#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>
/// Hosts the active render mode (ADR-0027) over the World tab: one node,
/// drawn after the world canvas and the guides, so the game's frame is never
/// touched. With no mode chosen it does nothing and costs nothing.
/// </summary>
[Tool]
public partial class WorldModes : Node2D
{
    private WorldHost _host;
    private WorldData _data;
    private readonly IWorldMode[] _modes = WorldModeList.All();
    private IWorldMode _mode;
    private ReachFill _reach;
    private (int X, int Y, sbyte Z)? _origin;
    private HashSet<long> _changed = new();
    private int _changedVersion = -1, _changedFacet = -1;

    /// <summary>Tinted: the mode is translucent over the world. Solid: it hides it.</summary>
    public bool Tinted { get; set; } = true;

    /// <summary>The cell under the pointer, set by the World tab.</summary>
    public (int X, int Y)? Hover { get; set; }

    /// <summary>Raised when the mode, its tint or its legend changes.</summary>
    public event Action Changed;

    internal WorldData Data => _data;
    internal IWorldMode Mode => _mode;
    internal ReachFill Reach => _reach;
    internal MapLayers Layers { get; set; }

    public string ModeName => _mode?.Name ?? "";

    public IReadOnlyList<string> ModeNames => _modes.Select(m => m.Name).ToList();

    internal IWorldMode ModeNamed(string name) => _modes.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    internal void Attach(WorldHost host)
    {
        _host = host;
        _data = new WorldData(host);
        host.OverlayChanged += OnOverlayChanged;
    }

    private void OnOverlayChanged(int facet, List<int> blocks) => Invalidate();

    internal void Detach()
    {
        if (_host != null)
        {
            _host.OverlayChanged -= OnOverlayChanged;
        }
    }

    /// <summary>Drops cached cell data (the world changed under it).</summary>
    public void Invalidate()
    {
        _data?.Invalidate();
        _reach = null;
    }

    /// <summary>Chooses a mode by name; an empty name or "Off" turns modes off. False when there is no such mode.</summary>
    public bool SetMode(string name)
    {
        IWorldMode m = string.IsNullOrEmpty(name) || name == "Off" ? null : ModeNamed(name);
        if (m == null && !string.IsNullOrEmpty(name) && name != "Off")
        {
            return false;
        }

        _mode = m;
        QueueRedraw();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Starts Reachability's flood fill from a cell, on the client's walking rules.</summary>
    public void SetOrigin(int x, int y, sbyte z)
    {
        _origin = (x, y, z);
        _reach = _data == null ? null : new ReachFill(_data, x, y, z);
        Changed?.Invoke();
    }

    internal ModeContext Context(bool live)
    {
        CellGeometry geo = CellGeometry.From(_host, _data, 64);
        if (geo == null)
        {
            return null;
        }

        if (_changedVersion != _data.Version || _changedFacet != geo.Facet)
        {
            _changedVersion = _data.Version;
            _changedFacet = geo.Facet;
            _changed = new HashSet<long>();
            if (_host.Project != null)
            {
                foreach (WorldBlock b in _host.Project.Blocks(geo.Facet))
                {
                    _changed.Add(((long)b.Bx << 20) | (uint)b.By);
                }
            }
        }

        return new ModeContext
        {
            Host = _host,
            Data = _data,
            Geo = geo,
            Tinted = Tinted,
            Origin = _origin,
            Reach = _reach,
            ChangedBlocks = _changed,
            Clock = live ? System.Diagnostics.Stopwatch.StartNew() : null,
        };
    }

    public override void _Process(double delta)
    {
        if (_host == null || !_host.IsBooted)
        {
            return;
        }

        _reach?.Step(6);
        if (_mode != null || (Layers?.AnyOn ?? false))
        {
            QueueRedraw();
        }
    }

    /// <summary>The legend of the active mode, for the chip.</summary>
    internal IReadOnlyList<LegendItem> Legend()
    {
        if (_mode == null || _host == null || !_host.IsBooted)
        {
            return Array.Empty<LegendItem>();
        }

        ModeContext ctx = Context(false);
        return ctx == null ? Array.Empty<LegendItem>() : _mode.Legend(ctx);
    }

    /// <summary>What the mode says about the cell under the pointer.</summary>
    internal string HoverText()
    {
        if (_mode == null || Hover is not { } h || _data == null)
        {
            return "";
        }

        ModeContext ctx = Context(false);
        return ctx == null ? "" : _mode.Describe(ctx, h.X, h.Y);
    }

    public override void _Draw()
    {
        if (_host == null || !_host.IsBooted || (_mode == null && !(Layers?.AnyOn ?? false)))
        {
            return;
        }

        ModeContext ctx = Context(true);
        if (ctx == null)
        {
            return;
        }

        var paint = new CanvasPaint(this);
        _mode?.Draw(paint, ctx);
        Layers?.Draw(paint, ctx);
    }
}
#endif
