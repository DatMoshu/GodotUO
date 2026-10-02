#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using GUO.Assets;

/// <summary>
/// Maps (plan §4.6 panel 8): the facets the install has, a radar image of
/// each, and any cell's land and statics.
/// </summary>
/// <remarks>
/// <para>
/// Read with the same calls <c>MiniMapGump</c> makes: <see cref="MapLoader.GetIndex"/>
/// for a block, a <see cref="MapBlock"/> for its 64 land cells, then
/// <see cref="StaticsBlock"/> entries; the topmost static at or above the land
/// wins, and colours come from <see cref="HuesLoader.GetRadarColorData"/>.
/// Unlike the minimap there is no world here, so multis placed by the server
/// and <c>GameObject.CanBeDrawn</c>'s hidden tiles are not considered.
/// </para>
/// <para>
/// The facet overview samples one cell in <see cref="Stride"/> each way. A
/// click (or "x,y" in the box) inspects that cell and a 64x64 cell radar
/// around it at full resolution. Phase 2 makes the same click jump the World
/// tab.
/// </para>
/// </remarks>
[Tool]
public partial class MapPanel : AssetPanel
{
    private const int Stride = 4;
    private const int Around = 32;

    private OptionButton _facet;
    private LineEdit _coords;
    private RadarView _radar;
    private (int X, int Y)? _lastCell;
    private Label _status;
    private Image _radarImage;
    private int _radarFacet = -1;

    public override string SmokeQuery => "1496,1628";

    /// <summary>Raised to show a cell in the UO World tab: facet, x, y.</summary>
    public event Action<int, int, int> JumpToWorld;

    /// <summary>
    /// Where the map is read from. Set by the plugin to the World tab's
    /// loader while the world runs, so the radar and the cell inspector show
    /// the world project's overlay (ADR-0011); otherwise the Assets dock's own.
    /// </summary>
    public Func<MapLoader> MapSource { get; set; }

    private MapLoader Maps => MapSource?.Invoke() ?? Data.Files.Maps;

    /// <summary>The radar image as drawn, for the smoke check.</summary>
    public Image RadarImage => _radarImage;

    /// <summary>The zoomable radar view (the whole tab), for scripted use.</summary>
    public RadarView Radar => _radar;

    /// <summary>
    /// Repaints the given blocks of the radar from the current map, after an
    /// overlay change. Blocks of another facet are ignored.
    /// </summary>
    public void RefreshBlocks(int facet, List<int> blocks)
    {
        if (_radarImage == null || facet != _radarFacet || blocks == null || blocks.Count == 0)
        {
            return;
        }

        MapLoader maps = Maps;
        int height = maps.MapBlocksSize[facet, 1];
        int per = 8 / Stride;
        var colours = new ushort[64];
        foreach (int number in blocks)
        {
            int bx = number / height, by = number % height;
            if (!ReadBlock(facet, bx, by, colours, null))
            {
                continue;
            }

            for (int sx = 0; sx < per; sx++)
            {
                for (int sy = 0; sy < per; sy++)
                {
                    uint c = GUO.Utility.HuesHelper.Color16To32(colours[(sy * Stride) * 8 + sx * Stride]);
                    _radarImage.SetPixel(bx * per + sx, by * per + sy, Color.Color8((byte)c, (byte)(c >> 8), (byte)(c >> 16)));
                }
            }
        }

        _radar.Texture = ImageTexture.CreateFromImage(_radarImage);
    }

    /// <summary>What the "Show in UO World" button does, for scripted use.</summary>
    public void RequestJump(int facet, int x, int y) => JumpToWorld?.Invoke(facet, x, y);

    /// <summary>The facet the radar shows.</summary>
    public int Facet => _radarFacet < 0 ? 0 : _radarFacet;

    public override void _Ready() => EnsureUi();

    private void EnsureUi()
    {
        if (_radar != null)
        {
            return;
        }

        SizeFlagsVertical = SizeFlags.ExpandFill;
        var bar = new HBoxContainer();
        AddChild(bar);

        _facet = new OptionButton();
        _facet.ItemSelected += _ => Render();
        bar.AddChild(_facet);

        _coords = new LineEdit
        {
            PlaceholderText = "x,y (1496,1628)",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _coords.TextSubmitted += t => Search(t);
        bar.AddChild(_coords);

        var jump = new Button
        {
            Text = "Jump to UO World",
            TooltipText = "Show the last clicked cell in the UO World tab (a double click does it too)",
        };
        jump.Pressed += () =>
        {
            if (_lastCell is { } c)
            {
                JumpToWorld?.Invoke(Facet, c.X, c.Y);
            }
        };
        bar.AddChild(jump);
        var fit = new Button { Text = "Fit", TooltipText = "Zoom to fit (the wheel zooms, a drag pans)" };
        fit.Pressed += () => _radar.ZoomToFit();
        bar.AddChild(fit);

        _radar = new RadarView();
        _radar.Picked += OnRadarPicked;
        AddChild(_radar);

        _status = new Label { Text = "loading client data...", ClipText = true };
        AddChild(_status);
    }

    public override void OnDataLoaded()
    {
        EnsureUi();
        _facet.Clear();
        for (int f = 0; f < MapLoader.MAPS_COUNT; f++)
        {
            _facet.AddItem($"map{f} ({Data.Files.Maps.MapsDefaultSize[f, 0]}x{Data.Files.Maps.MapsDefaultSize[f, 1]})", f);
        }

        _status.Text = "pick a facet; the radar renders when the tab is shown";
        VisibilityChanged += () =>
        {
            if (IsVisibleInTree() && _radarFacet < 0)
            {
                Render();
            }
        };
    }

    private bool EnsureFacet(int facet)
    {
        MapLoader maps = Maps;
        if (maps.BlockData[facet] == null)
        {
            maps.LoadMap(facet);
        }

        return maps.BlockData[facet] != null;
    }

    private void Render()
    {
        if (Data == null || !Data.IsLoaded)
        {
            return;
        }

        int facet = _facet.Selected < 0 ? 0 : _facet.GetItemId(_facet.Selected);
        if (!EnsureFacet(facet))
        {
            _status.Text = $"map{facet}: no map files in the install";
            _radar.Texture = null;
            _radarFacet = facet;
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        MapLoader maps = Maps;
        int bw = maps.MapBlocksSize[facet, 0], bh = maps.MapBlocksSize[facet, 1];
        int per = 8 / Stride;
        var rgba = new byte[bw * per * bh * per * 4];
        int w = bw * per;
        var colours = new ushort[64];

        for (int bx = 0; bx < bw; bx++)
        {
            for (int by = 0; by < bh; by++)
            {
                if (!ReadBlock(facet, bx, by, colours, null))
                {
                    continue;
                }

                for (int sx = 0; sx < per; sx++)
                {
                    for (int sy = 0; sy < per; sy++)
                    {
                        uint c = GUO.Utility.HuesHelper.Color16To32(colours[(sy * Stride) * 8 + sx * Stride]);
                        int o = ((by * per + sy) * w + bx * per + sx) * 4;
                        rgba[o] = (byte)c;
                        rgba[o + 1] = (byte)(c >> 8);
                        rgba[o + 2] = (byte)(c >> 16);
                        rgba[o + 3] = 0xFF;
                    }
                }
            }
        }

        _radarImage = Image.CreateFromData(w, bh * per, false, Image.Format.Rgba8, rgba);
        _radar.Texture = ImageTexture.CreateFromImage(_radarImage);
        _radarFacet = facet;
        _status.Text = $"map{facet}: {bw * 8}x{bh * 8} cells, radar 1:{Stride} in {sw.ElapsedMilliseconds} ms. Click a cell, double-click to jump.";
    }

    /// <summary>
    /// Radar colours for the 64 cells of a block, and optionally what is on
    /// each cell. False when the block is not in the map.
    /// </summary>
    private bool ReadBlock(int facet, int bx, int by, ushort[] colours, List<string>[] detail)
    {
        MapLoader maps = Maps;
        if (bx < 0 || by < 0 || bx >= maps.MapBlocksSize[facet, 0] || by >= maps.MapBlocksSize[facet, 1])
        {
            return false;
        }

        ref IndexMap im = ref maps.GetIndex(facet, bx, by);
        if (!im.IsValid() || im.MapFile == null)
        {
            return false;
        }

        HuesLoader hues = Data.Files.Hues;
        im.MapFile.Seek((long)im.MapAddress, SeekOrigin.Begin);
        MapBlock block = im.MapFile.Read<MapBlock>();

        Span<sbyte> topZ = stackalloc sbyte[64];
        for (int i = 0; i < 64; i++)
        {
            ref MapCells cell = ref block.Cells[i];
            topZ[i] = cell.Z;
            colours[i] = hues.GetRadarColorData(cell.TileID);
            if (detail != null)
            {
                detail[i] = new List<string>
                {
                    $"land 0x{cell.TileID:X4} z {cell.Z}  {Data.NameOf(cell.TileID)}",
                };
            }
        }

        if (im.StaticFile != null && im.StaticCount > 0)
        {
            im.StaticFile.Seek((long)im.StaticAddress, SeekOrigin.Begin);
            for (uint c = 0; c < im.StaticCount; c++)
            {
                StaticsBlock st = im.StaticFile.Read<StaticsBlock>();
                if (st.Color == 0 || st.Color == 0xFFFF || st.X > 7 || st.Y > 7)
                {
                    continue;
                }

                int i = st.Y * 8 + st.X;
                if (st.Z >= topZ[i])
                {
                    topZ[i] = st.Z;
                    colours[i] = hues.GetRadarColorData(st.Color + 0x4000);
                }

                detail?[i].Add(
                    $"static 0x{st.Color:X4} z {st.Z}" + (st.Hue != 0 ? $" hue {st.Hue}" : "")
                    + $"  {Data.NameOf(EditorData.LandCount + st.Color)}"
                );
            }
        }

        return true;
    }

    public override int? Search(string text)
    {
        EnsureUi();
        _coords.Text = text ?? "";
        string[] parts = (text ?? "").Split(',', ' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            _status.Text = "type x,y";
            return null;
        }

        if (_radarFacet < 0)
        {
            Render();
        }

        return InspectCell(x, y) ? x * 65536 + y : null;
    }

    /// <summary>Does what a click (or double click) on a cell of the radar does, for scripted use.</summary>
    public void ScriptedClick(int x, int y, bool doubleClick = false)
    {
        if (_radarImage == null)
        {
            Render();
        }

        OnRadarPicked(new Vector2I(x / Stride, y / Stride), false, doubleClick);
    }

    private void OnRadarPicked(Vector2I px, bool ctrl, bool doubleClick)
    {
        int x = px.X * Stride, y = px.Y * Stride;
        _coords.Text = $"{x},{y}";
        _lastCell = (x, y);
        _radar.Mark = px;
        if (ctrl || doubleClick)
        {
            // Ctrl+click or a double click: straight to the World tab.
            JumpToWorld?.Invoke(Facet, x, y);
            return;
        }

        InspectCell(x, y);
    }

    /// <summary>The radar's image for a facet (rendered on first use), for the World tab's minimap. Null if the install has no such map.</summary>
    public Image RadarFor(int facet)
    {
        if (Data == null || !Data.IsLoaded || _facet == null)
        {
            return null;
        }

        if (_radarFacet != facet || _radarImage == null)
        {
            int at = _facet.GetItemIndex(facet);
            if (at < 0)
            {
                return null;
            }

            _facet.Select(at);
            Render();
        }

        return _radarFacet == facet ? _radarImage : null;
    }

    private bool InspectCell(int x, int y)
    {
        int facet = _radarFacet < 0 ? 0 : _radarFacet;
        if (!EnsureFacet(facet))
        {
            return false;
        }

        // A full-resolution radar of the cells around the point, 4x, with the
        // cell marked, plus what stands on the cell itself.
        const int Zoom = 4;
        int side = Around * 2;
        Image img = Image.CreateEmpty(side * Zoom, side * Zoom, false, Image.Format.Rgba8);
        var colours = new ushort[64];
        var detail = new List<string>[64];
        List<string> here = null;

        for (int bx = (x - Around) >> 3; bx <= (x + Around) >> 3; bx++)
        {
            for (int by = (y - Around) >> 3; by <= (y + Around) >> 3; by++)
            {
                bool hasCell = bx == x >> 3 && by == y >> 3;
                if (!ReadBlock(facet, bx, by, colours, hasCell ? detail : null))
                {
                    continue;
                }

                if (hasCell)
                {
                    here = detail[(y & 7) * 8 + (x & 7)];
                }

                for (int i = 0; i < 64; i++)
                {
                    int cx = bx * 8 + (i & 7) - (x - Around), cy = by * 8 + (i >> 3) - (y - Around);
                    if (cx < 0 || cy < 0 || cx >= side || cy >= side)
                    {
                        continue;
                    }

                    uint c = GUO.Utility.HuesHelper.Color16To32(colours[i]);
                    img.FillRect(new Rect2I(cx * Zoom, cy * Zoom, Zoom, Zoom),
                        Color.Color8((byte)c, (byte)(c >> 8), (byte)(c >> 16)));
                }
            }
        }

        if (here == null)
        {
            _status.Text = $"{x},{y} is outside map{facet}";
            return false;
        }

        // Mark the cell.
        var mark = new Color(1, 0, 1);
        int m = Around * Zoom;
        img.FillRect(new Rect2I(m - 1, m - 6, Zoom + 2, 4), mark);
        img.FillRect(new Rect2I(m - 1, m + Zoom + 2, Zoom + 2, 4), mark);

        var sb = new StringBuilder();
        sb.Append($"[b]map{facet} {x},{y}[/b]   block {x >> 3},{y >> 3}   cell {x & 7},{y & 7}\n");
        foreach (string line in here)
        {
            sb.Append(line).Append('\n');
        }

        sb.Append($"(radar of the {side}x{side} cells around it, {Zoom}x; double-click the radar to jump)\n");
        var inspection = Inspection.Still("Maps", $"{x},{y}", img, sb.ToString());
        inspection.Actions.Add(("Show in UO World", () => JumpToWorld?.Invoke(facet, x, y)));
        Raise(inspection);
        return true;
    }
}
#endif
