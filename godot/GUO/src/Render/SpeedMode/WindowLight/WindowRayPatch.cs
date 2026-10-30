// SPDX-License-Identifier: BSD-2-Clause
// Screen-space receiver shafts: a bounded lighting approximation, not volume/GI.
using System;
using System.Runtime.InteropServices;
using Godot;
using N4=System.Numerics.Vector4;

namespace GUO.Renderer.SpeedMode.WindowLight;

[StructLayout(LayoutKind.Sequential,Pack=4)]
internal struct WindowRayPatch
{
    internal WindowLightPatch Window;
    internal N4 SourcePixel;
    internal const int Bytes=160,Limit=16;

    internal static bool TryCreate(in WindowLightPatch window,Vector2 source,bool sunEnabled,
        bool insideCovered,bool outsideOpen,out WindowRayPatch result)
    {
        result=default;
        if(!sunEnabled || !insideCovered || !outsideOpen || !source.IsFinite()
            || !Finite(window.Origin) || !Finite(window.Basis) || !Finite(window.Screen) || !Finite(window.Colour)
            || !Finite(window.Opening) || !Finite(window.Rows0) || window.Rows0.X<1 || window.Rows0.X>16
            || window.Opening.W!=4 || window.Origin.W<=0 || window.Origin.W>1
            || window.Opening.X<1 || window.Opening.X>4 || window.Opening.Z<1 || window.Opening.Z>16
            || window.Screen.Z<=window.Screen.X || window.Screen.W<=window.Screen.Y
            || Math.Abs(window.Basis.X*window.Basis.W-window.Basis.Y*window.Basis.Z)<.000001f) return false;
        result.Window=window;
        result.Window.Colour=System.Numerics.Vector4.Clamp(window.Colour,N4.Zero,N4.One);
        result.SourcePixel=new N4(source.X,source.Y,0,0);
        return true;
    }

    private static bool Finite(N4 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z)&&float.IsFinite(v.W);
}

// One callback consumption, tied to the exact snapshot and target. No history.
internal struct WindowRayGate
{
    private int _generation;
    private Vector2I _origin,_size;
    private bool _ready;
    internal void Clear()=>_ready=false;
    internal void Prepare(int generation,bool eligible,Vector2I origin,Vector2I size)
    {
        _generation=generation;_origin=origin;_size=size;
        _ready=eligible && size.X>0 && size.Y>0;
    }
    internal bool Consume(int generation,Vector2I origin,Vector2I size)
    {
        bool admitted=_ready && generation==_generation && origin==_origin && size==_size;
        _ready=false;
        return admitted;
    }
}
