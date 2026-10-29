// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GUO.Game.Map;
using GUO.Renderer.Lights;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    private readonly List<Chunk> _speedGridChunks=new(1024);
    private RealtimeLightField _speedGridOwner;
    private ulong _speedGridHash;
    internal static long SpeedGridBuilds,SpeedGridHits;

    // Same loaded-chunk bounds and fill as BuildRealtimeGrid. Conservative
    // membership/content dependencies include moving objects too; they may
    // cause an extra rebuild but cannot hide a changed wall or upper floor.
    private bool BuildSpeedGrid()
    {
        int minX=int.MaxValue,minY=int.MaxValue,maxX=int.MinValue,maxY=int.MinValue;
        foreach(var chunk in _visibleChunks) {
            if(chunk==null || chunk.IsDestroyed) continue;
            minX=Math.Min(minX,chunk.X); minY=Math.Min(minY,chunk.Y);
            maxX=Math.Max(maxX,chunk.X); maxY=Math.Max(maxY,chunk.Y);
        }
        if(minX==int.MaxValue) throw new InvalidOperationException("No loaded chunks for lighting.");
        int ox=minX*8-RtMargin,oy=minY*8-RtMargin;
        int width=Math.Min(RtMaxSide,(maxX-minX+1)*8+RtMargin*2);
        int height=Math.Min(RtMaxSide,(maxY-minY+1)*8+RtMargin*2);
        static ulong Mix(ulong hash,int value)=>unchecked((hash^(uint)value)*1099511628211UL);
        ulong hash=14695981039346656037UL;
        hash=Mix(Mix(Mix(Mix(Mix(hash,_world.MapIndex),ox),oy),width),height);
        _speedGridChunks.Clear();
        for(int cy=oy>>3;cy<=(oy+height-1)>>3;cy++)
            for(int cx=ox>>3;cx<=(ox+width-1)>>3;cx++)
            {
                var chunk=_world.Map.GetChunk(cx*8,cy*8,false);
                if(chunk==null || chunk.IsDestroyed) continue;
                _speedGridChunks.Add(chunk);
                hash=Mix(Mix(Mix(Mix(Mix(Mix(hash,RuntimeHelpers.GetHashCode(chunk)),chunk.X),chunk.Y),
                    chunk.ObjectsVersion),chunk.Mesh.Land.ContentStamp),chunk.Mesh.Statics.ContentStamp);
            }
        if(ReferenceEquals(_speedGridOwner,_rtField) && _speedGridHash==hash) { SpeedGridHits++; return false; }
        _rtField.Begin(ox,oy,width,height);
        foreach(var chunk in _speedGridChunks) FillRealtimeChunk(chunk);
        _speedGridHash=hash; _speedGridOwner=_rtField; SpeedGridBuilds++;
        return true;
    }
}
