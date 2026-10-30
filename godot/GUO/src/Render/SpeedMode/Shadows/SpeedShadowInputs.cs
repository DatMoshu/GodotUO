// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned, ADR-0048: game-thread snapshots, render-thread resource ownership.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using GUO.Renderer.Lights;
using GUO.Renderer.SpeedMode.WindowLight;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

internal sealed class SpeedShadowInputs
{
    internal readonly Action<RenderingDevice,SpeedLightFrame> RenderCallback;
    internal readonly Action<RenderingDevice,Rid,Rid,Vector2I,Vector2I> PatchCallback;
    internal readonly Action ReleaseCallback;
    internal SpeedShadowInputs() { RenderCallback=Render; PatchCallback=RenderWindows; ReleaseCallback=Release; }
    internal readonly WallShadowGrid Grid = new();
    internal readonly ShadowLamp[] Lamps = new ShadowLamp[RealtimeLightEffects.FrameLightCap];
    internal readonly List<Stamp> Sun = new(1024);
    internal readonly List<Stamp>[] Mobile = new List<Stamp>[10];
    internal readonly List<WindowLightPatch> Windows = new(64);
    internal readonly List<WindowRayPatch> Rays=new(WindowRayPatch.Limit);
    private WindowRayPassRd _rays;
    private WindowRayGate _rayGate;
    private Rid _rayMask;
    private int _generation;
    internal int LampCount, Requested;
    internal N3 Viewer;
    internal bool SunEnabled;
    private LampShadowFrame _polar;
    private ProjectedShadowMaskRd _sun;
    private readonly ProjectedShadowMaskRd[] _mobile = new ProjectedShadowMaskRd[10];
    private WindowLightPassRd _windows;

    internal readonly record struct Stamp(Texture2D Texture,Vector2 A,Vector2 B,Vector2 C,
        Vector2 U,Vector2 V,Vector2 W,float Opacity,float From,float To);

    internal void Begin()
    {
        Sun.Clear(); Windows.Clear(); Rays.Clear();
        _generation++; _rayGate.Clear(); _rayMask=default;
        foreach(var list in Mobile) list?.Clear();
    }

    // Called only inside the core's admitted RD transaction. No scene access.
    internal void Render(RenderingDevice rd,SpeedLightFrame frame)
    {
        _rayGate.Clear(); _rayMask=default;
        frame.LampShadowAtlas=frame.SunMask=default;
        for(int i=0;i<frame.Count;i++) { frame.Lights[i].ShadowRow=-1; frame.Lights[i].MobileShadow=default; }
        if(Requested>0 && LampCount>0)
        {
            _polar??=new LampShadowFrame();
            if(_polar.Render(rd,Grid,Lamps.AsSpan(0,LampCount),Viewer,Requested)) {
                frame.LampShadowAtlas=_polar.Atlas;
                for(int i=0;i<frame.Count;i++) {
                    int row=_polar.RowForOffer(i); frame.Lights[i].ShadowRow=row;
                    if(row>=0 && Mobile[row]?.Count>0) {
                        _mobile[row]??=new ProjectedShadowMaskRd(rd,publish:false);
                        frame.Lights[i].MobileShadow=Draw(_mobile[row],Mobile[row],frame.TargetSize);
                    }
                }
            }
        }
        if(SunEnabled && Sun.Count>0) {
            _sun??=new ProjectedShadowMaskRd(rd,publish:false);
            frame.SunMask=Draw(_sun,Sun,frame.TargetSize);
        }
        _rayMask=frame.SunMask;
        bool rayReady=SunEnabled && Rays.Count>0 && _rayMask.IsValid;
        _rayGate.Prepare(_generation,rayReady,frame.Origin,frame.TargetSize);
        if(!rayReady) { _rays?.Dispose(); _rays=null; }
    }

    private static Rid Draw(ProjectedShadowMaskRd mask,List<Stamp> stamps,Vector2I size)
    {
        mask.Begin(size.X,size.Y);
        foreach(var s in stamps) mask.Stamp(s.Texture,s.A,s.B,s.C,s.U,s.V,s.W,s.Opacity,s.From,s.To);
        return mask.Finish()!=null && !mask.Failed ? mask.ShadowRid : default;
    }

    internal void RenderWindows(RenderingDevice rd,Rid framebuffer,Rid surface,Vector2I origin,Vector2I size)
    {
        if(_rayGate.Consume(_generation,origin,size)) {
            _rays??=new WindowRayPassRd();
            if(!_rays.Draw(rd,framebuffer,surface,_rayMask,new N2(origin.X,origin.Y),size.X,size.Y,CollectionsMarshal.AsSpan(Rays)))
                // Core discards the entire light transaction and restores classic
                // lighting; a partial optional draw must never be published.
                throw new InvalidOperationException("Window receiver shafts failed: "+_rays.Failure);
        }
        _rayMask=default;
        if(Windows.Count==0) return;
        _windows??=new WindowLightPassRd();
        _windows.Draw(rd,framebuffer,surface,new N2(origin.X,origin.Y),size.X,size.Y,CollectionsMarshal.AsSpan(Windows));
    }

    // Core invokes this before dropping the light target on off, failure,
    // resize or shutdown. Objects may be recreated on a later valid ticket.
    internal void Release()
    {
        _polar?.Dispose(); _polar=null;
        _sun?.DisposeAll(); _sun=null;
        for(int i=0;i<_mobile.Length;i++) { _mobile[i]?.DisposeAll(); _mobile[i]=null; }
        _windows?.Dispose(); _windows=null;
        _rays?.Dispose(); _rays=null; _rayGate.Clear(); _rayMask=default;
    }

    internal static Stamp Project(Texture2D texture,GUO.Compat.Rectangle uv,Vector2 position,
        Vector2 anchor,Vector2 projection,float opacity,bool flip,Transform2D view)
    {
        float height=Math.Max(1,anchor.Y-position.Y);
        Vector2 across=new Vector2(projection.Y,-projection.X).Normalized();
        Vector2 Point(float x,float y)=>view*(anchor+across*(x-anchor.X)+projection*y);
        float from=(anchor.Y-position.Y)/height,to=(anchor.Y-position.Y-uv.Height)/height;
        float tw=texture.GetWidth(),th=texture.GetHeight();
        float u0=(uv.X+.5f)/tw,u1=(uv.Right-.5f)/tw;
        if(flip) (u0,u1)=(u1,u0);
        return new Stamp(texture,Point(position.X,from),Point(position.X+uv.Width,from),Point(position.X,to),
            new Vector2(u0,(uv.Y+.5f)/th),new Vector2(u1,(uv.Y+.5f)/th),new Vector2(u0,(uv.Bottom-.5f)/th),opacity,from,to);
    }
}
