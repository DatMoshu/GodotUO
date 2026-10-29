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
        for (int y=0;y<side;y++) for(int x=0;x<side;x++)
        {
            int index=y*side+x;
            reach.Pixels[index*2]=_scratchVis[index]>0f ? (byte)255 : (byte)0;
            // One tile of support; the fragment feathers the exact edge 1/8 tile.
            // A summed-area table makes dilation O(samples), including large
            // dark regions; no radius-squared scan for each pixel.
            int x0=Math.Max(0,x-scale),x1=Math.Min(side,x+scale+1);
            int y0=Math.Max(0,y-scale),y1=Math.Min(side,y+scale+1);
            int count=_speedPrefix[y1*stride+x1]-_speedPrefix[y0*stride+x1]
                -_speedPrefix[y1*stride+x0]+_speedPrefix[y0*stride+x0];
            reach.Pixels[index*2+1]=count>0 ? (byte)255 : (byte)0;
        }
        reach.Side=side; reach.Scale=scale; reach.X=source.X-radius; reach.Y=source.Y-radius;
        reach.Blockers=_speedHash; reach.Revision=++_speedRevision;
        LastComputed++;
        return reach;
    }
}
