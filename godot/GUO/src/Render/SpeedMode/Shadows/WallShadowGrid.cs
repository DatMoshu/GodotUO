// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: immutable-at-submit camera-relative wall data for the compute pass.
using System;
using System.Runtime.InteropServices;
using GUO.Assets;

namespace GUO.Renderer.SpeedMode.Shadows;

internal sealed class WallShadowGrid
{
    // Two RGBA32F layers preserve signed UO Z and exact sub-tile wall offsets.
    // layer 0: solid top, X-running edge top/offset, Y-running edge top.
    // layer 1: Y-running edge offset; remaining channels reserved.
    // Populate during the existing blocker walk, before tree information is
    // lost in its combined per-cell maximum. No sprite cards are submitted.
    public int OriginX { get; private set; }
    public int OriginY { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    internal ulong Version { get; private set; }
    private float[] _cells = Array.Empty<float>();
    public ReadOnlySpan<byte> Bytes => MemoryMarshal.AsBytes(_cells.AsSpan());
    public const float NoBlock = -32768;

    public void Begin(int x,int y,int width,int height)
    {
        if (width is < 1 or > 512 || height is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(width),"Wall grid must fit 512 by 512 tiles.");
        OriginX=x; OriginY=y; Width=width; Height=height;
        Version++;
        int size=checked(width*height*8);
        if (_cells.Length != size) _cells=new float[size];
        Array.Clear(_cells);
        for (int i=0; i<width*height; i++)
        { _cells[i*4]=NoBlock; _cells[i*4+1]=NoBlock; _cells[i*4+3]=NoBlock; }
    }

    private int Index(int x,int y) => x<OriginX || y<OriginY || x>=OriginX+Width || y>=OriginY+Height
        ? -1 : ((y-OriginY)*Width+x-OriginX)*4;

    // The reach field's aggregate BlockTop cannot be copied: its tallest
    // impassable can be a tree. Filter at source while flags are still known.
    public bool AddBlocker(int x,int y,float top,TileFlag flags,int height,string name,bool foliageOnTile)
    {
        if (!Accepts(flags,height,name,foliageOnTile)) return false;
        int i=Index(x,y);
        if (i<0 || !float.IsFinite(top)) return false;
        if (top>_cells[i]) { _cells[i]=top; Version++; }
        return true;
    }

    // Apply the same source filter before submitting exact wall planes.
    internal static bool Accepts(TileFlag flags,int height,string name,bool foliageOnTile)
    {
        if (TreeRule(flags,name,foliageOnTile)!=null) return false;
        const TileFlag excluded=TileFlag.Foliage | TileFlag.Roof | TileFlag.Surface |
            TileFlag.Bridge | TileFlag.Door | TileFlag.Window | TileFlag.LightSource |
            TileFlag.Background | TileFlag.Translucent;
        if ((flags & excluded)!=0 || (flags & TileFlag.Wall)==0 &&
            ((flags & TileFlag.Impassable)==0 || height<8)) return false;
        return true;
    }

    // A trunk is often only Impassable, with foliage as another graphic on
    // the same tile. The scene walk must supply both pieces of source context.
    internal static string TreeRule(TileFlag flags,string name,bool foliageOnTile)
    {
        if ((flags & TileFlag.Foliage)!=0) return "foliage-flag";
        if (name!=null && (name.Contains("tree",StringComparison.OrdinalIgnoreCase) ||
            name.Contains("trunk",StringComparison.OrdinalIgnoreCase))) return "tree-trunk-name";
        return foliageOnTile ? "foliage-on-tile" : null;
    }

    public void AddEdge(int x,int y,bool alongX,float offset,float top)
    {
        int i=Index(x,y);
        if (i<0 || !float.IsFinite(offset) || !float.IsFinite(top)) return;
        int channel=alongX ? 1 : 3;
        if (top<=_cells[i+channel]) return;
        _cells[i+channel]=top;
        _cells[alongX ? i+2 : Width*Height*4+i]=Math.Clamp(offset,0,1);
        Version++;
    }

    internal (float Solid,float Horizontal,float OffsetY,float Vertical,float OffsetX) Cell(int x,int y)
    {
        int i=Index(x,y);
        return i<0 ? (NoBlock,NoBlock,0,NoBlock,0) :
            (_cells[i],_cells[i+1],_cells[i+2],_cells[i+3],_cells[Width*Height*4+i]);
    }
}
