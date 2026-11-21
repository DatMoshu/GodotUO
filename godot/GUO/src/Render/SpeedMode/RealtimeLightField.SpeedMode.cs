// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;

namespace GUO.Renderer.Lights;

// ADR-0048: reuse the existing blocker rays, without accumulating a grid field.
internal sealed partial class RealtimeLightField
{
    internal sealed class SpeedReach
    {
        internal byte[] Pixels;
        internal int Side, Scale, X, Y, Revision;
        internal ulong Blockers;
        internal float[] Scratch;
    }
    private readonly Dictionary<CacheKey,SpeedReach> _speedReach = new();
    private ulong _speedHash;
    private int _speedRevision;
    private int[] _speedPrefix=Array.Empty<int>();
    private int[] _speedBlockerDistance=Array.Empty<int>();

    /// <summary>Penumbra half-width per reach sample of distance from the light (0.12 = about 1 tile at 8 tiles).</summary>
    internal const float PenumbraPerSample=0.12f;
    /// <summary>Largest penumbra half-width, tiles.</summary>
    internal const float PenumbraMaxTiles=1.25f;

    // Chebyshev tile distance to the nearest tile holding a blocker or wall line,
    // over the reach map's tiles (0 on such a tile). Capped; O(tiles * 25).
    private void EnsureBlockerDistance(int ox,int oy,int tiles)
    {
        const int cap=4;
        if(_speedBlockerDistance.Length<tiles*tiles) _speedBlockerDistance=new int[tiles*tiles];
        for(int ty=0;ty<tiles;ty++) for(int tx=0;tx<tiles;tx++)
        {
            int best=cap;
            for(int dy=-cap+1;dy<cap && best>0;dy++) for(int dx=-cap+1;dx<cap;dx++)
            {
                int d=Math.Max(Math.Abs(dx),Math.Abs(dy));
                if(d>=best || !HasBlocker(ox+tx+dx,oy+ty+dy)) continue;
                best=d;
            }
            _speedBlockerDistance[ty*tiles+tx]=best;
        }
    }

    private bool HasBlocker(int x,int y)
    {
        if(!Contains(x,y)) return false;
        int i=Index(x,y);
        return _blockTop[i]!=NoBlock || _hTop[i]!=NoBlock || _vTop[i]!=NoBlock;
    }

    internal void BeginSpeedReach(bool blockersChanged=true)
    {
        if(blockersChanged) {
            _speedHash=HashBlockers();
            _speedHash^=((ulong)(uint)_width<<32)|(uint)_height;
        }
        LastComputed=0;
        if (_speedReach.Count>CacheLimit) _speedReach.Clear();
    }

    internal SpeedReach GetSpeedReach(in Source source)
    {
        var key=new CacheKey(in source);
        const int scale=4; // Fixed quarter-tile rays keep a 1/8-tile feather at every zoom.
        int radius=source.Radius+1;
        int side=(radius*2+1)*scale;
        if (!_speedReach.TryGetValue(key,out var reach))
            _speedReach.Add(key,reach=new SpeedReach());
        if (reach.Pixels!=null && reach.Blockers==_speedHash && reach.Side==side
            && reach.X==source.X-radius && reach.Y==source.Y-radius) return reach;
        // ComputeReach leaves its exact binary rays in _scratchVis. Its existing
        // classic blurred result stays private to this cache and is never summed.
        int classicScale=_fineScale;
        var raySource=new Source(source.X,source.Y,source.Z+10,source.Radius,source.Color);
        try { _fineScale=scale; reach.Scratch=ComputeReach(in raySource,reach.Scratch); }
        finally { _fineScale=classicScale; }
        reach.Pixels ??= new byte[side*side*2];
        if (reach.Pixels.Length!=side*side*2) reach.Pixels=new byte[side*side*2];
        int stride=side+1;
        if(_speedPrefix.Length<stride*stride) _speedPrefix=new int[stride*stride];
        Array.Clear(_speedPrefix,0,stride*stride);
        for(int y=0;y<side;y++) {
            int row=0;
            for(int x=0;x<side;x++) {
                row+=_scratchVis[y*side+x]>0f ? 1 : 0;
                _speedPrefix[(y+1)*stride+x+1]=row+_speedPrefix[y*stride+x+1];
            }
        }
        // Penumbra (owner 2026-11-21: no hard light/dark wedges). Each sample
        // averages the exact rays over a box whose half-width grows with its
        // distance from the light and from the nearest blocker tile, so a
        // shadow is sharp where it touches its wall (contact) and widens across
        // the street, and light never bleeds through a wall onto its far face.
        int tiles=side/scale;
        EnsureBlockerDistance(source.X-radius,source.Y-radius,tiles);
        float lightX=(source.X-(source.X-radius)+0.5f)*scale,lightY=(source.Y-(source.Y-radius)+0.5f)*scale;
        for (int y=0;y<side;y++) for(int x=0;x<side;x++)
        {
            int index=y*side+x;
            float dl=MathF.Sqrt((x+0.5f-lightX)*(x+0.5f-lightX)+(y+0.5f-lightY)*(y+0.5f-lightY));
            int db=_speedBlockerDistance[(y/scale)*tiles+x/scale];
            int k=(int)MathF.Round(Math.Min(Math.Min(dl*PenumbraPerSample,db*scale*0.5f),PenumbraMaxTiles*scale));
            int x0=Math.Max(0,x-k),x1=Math.Min(side,x+k+1);
            int y0=Math.Max(0,y-k),y1=Math.Min(side,y+k+1);
            int visible=_speedPrefix[y1*stride+x1]-_speedPrefix[y0*stride+x1]
                -_speedPrefix[y1*stride+x0]+_speedPrefix[y0*stride+x0];
            float soft=k==0 ? (_scratchVis[index]>0f ? 1f : 0f) : visible/(float)((x1-x0)*(y1-y0));
            reach.Pixels[index*2]=(byte)MathF.Round(soft*255f);
            // One tile of support; a summed-area table keeps dilation O(samples).
            x0=Math.Max(0,x-scale); x1=Math.Min(side,x+scale+1);
            y0=Math.Max(0,y-scale); y1=Math.Min(side,y+scale+1);
            int count=_speedPrefix[y1*stride+x1]-_speedPrefix[y0*stride+x1]
                -_speedPrefix[y1*stride+x0]+_speedPrefix[y0*stride+x0];
            reach.Pixels[index*2+1]=count>0 || soft>0f ? (byte)255 : (byte)0;
        }
        reach.Side=side; reach.Scale=scale; reach.X=source.X-radius; reach.Y=source.Y-radius;
        reach.Blockers=_speedHash; reach.Revision=++_speedRevision;
        LastComputed++;
        return reach;
    }
}
