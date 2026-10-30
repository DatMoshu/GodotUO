// SPDX-License-Identifier: BSD-2-Clause
// ADR-0048: consume native body-pass pixels without replaying Mobile.Draw.
using System;
using System.Collections.Generic;
using Godot;
using GUO.Game.GameObjects;
using GUO.Renderer;
using GUO.Renderer.SpeedMode.Shadows;
using N3=System.Numerics.Vector3;

namespace GUO.Game.Scenes;

internal partial class RenderLists
{
    // Group is the immutable queue sequence + 1, not its sorted position.
    internal Mobile SpeedShadowMobile(int group)
    {
        int index=group-1;
        return (uint)index<(uint)_world.Count && _world[index].Pass==Mobile.DrawPass.Body
            ? _world[index].Object as Mobile : null;
    }
}

internal partial class GameScene
{
    private readonly List<(Mobile Owner,NativeShadowQuad Quad)> _speedMobileQuads=new(256);
    private readonly Dictionary<Mobile,float> _speedMobileTops=new();
    internal int SpeedMobileQuadCount { get; private set; }
    internal Action<Mobile,NativeShadowQuad> SpeedShadowObserver; // Disconnected fixture only.

    private void CollectSpeedMobileShadows(SpeedShadowInputs inputs,UltimaBatcher2D batcher,
        Transform2D view,Vector2 direction,float length,float strength,ReadOnlySpan<int> selected)
    {
        _speedMobileQuads.Clear(); _speedMobileTops.Clear(); SpeedMobileQuadCount=0;
        var capture=batcher.OrderedWorldProbe;
        if(capture==null) return;
        int count=capture.BeginSpeedShadowRead();
        try {
            var inverse=view.AffineInverse();
            for(int i=0;i<count;i++) {
                var mobile=_renderLists.SpeedShadowMobile(capture.SpeedShadowGroup(i));
                if(mobile==null || mobile.IsDestroyed || !mobile.AllowedToDraw || mobile.IsDead || mobile.IsHidden || mobile.Z>=_maxZ) continue;
                if(!capture.ReadSpeedShadowQuad(i,inverse,out var quad)) continue;
                _speedMobileQuads.Add((mobile,quad));
                SpeedShadowObserver?.Invoke(mobile,quad);
                float top=Math.Min(Math.Min(quad.A.Y,quad.B.Y),Math.Min(quad.C.Y,(quad.B+quad.C-quad.A).Y));
                if(!_speedMobileTops.TryGetValue(mobile,out float old) || top<old) _speedMobileTops[mobile]=top;
            }
        }
        finally { capture.EndSpeedShadowRead(); }

        Span<MobileLampShadow> affecting=stackalloc MobileLampShadow[2];
        foreach(var entry in _speedMobileQuads) {
            var mobile=entry.Owner; var quad=entry.Quad;
            // Exactly MobileView's integer draw origin, before sprite-centre,
            // mount-offset and sitting deformations (already in the captured quad).
            Vector2 anchor=new(mobile.RealScreenPosition.X+(int)mobile.Offset.X+22,
                mobile.RealScreenPosition.Y-3+(int)(mobile.Offset.Y-mobile.Offset.Z)+22);
            float height=Math.Max(1,anchor.Y-_speedMobileTops[mobile]);
            if(inputs.SunEnabled) inputs.Sun.Add(quad.Project(anchor,direction*length,height,strength,view));
            var foot=new N3(mobile.X+(mobile.Offset.X+mobile.Offset.Y)/44f,
                mobile.Y+(mobile.Offset.Y-mobile.Offset.X)/44f,mobile.Z);
            int lamps=MobileLampProjection.Select(inputs.Lamps.AsSpan(0,inputs.LampCount),foot,affecting);
            for(int j=0;j<lamps;j++) for(int row=0;row<selected.Length;row++) if(selected[row]==affecting[j].Offer) {
                var cast=affecting[j]; inputs.Mobile[row]??=new List<SpeedShadowInputs.Stamp>(32);
                inputs.Mobile[row].Add(quad.Project(anchor,new Vector2(cast.Direction.X,cast.Direction.Y)*cast.Length,
                    height,cast.Strength,view));
            }
        }
        SpeedMobileQuadCount=_speedMobileQuads.Count;
        // Immutable stamps retain borrowed textures until the light ticket retires.
        // No mobile or command stream is read by the render-thread callback.
        _speedMobileQuads.Clear(); _speedMobileTops.Clear();
    }
}
