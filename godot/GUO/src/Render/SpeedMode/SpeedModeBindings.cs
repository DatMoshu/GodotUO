// SPDX-License-Identifier: BSD-2-Clause
using Godot;

namespace GUO.Renderer.SpeedMode;

/// <summary>
/// Borrowed, same-frame surface/light bindings (ADR-0048). Example: consumers
/// take Current once, then reconstruct world XY as surface.xy + Origin.
/// Access only on the admitted Depth World's shared main/render thread.
/// Never retain a texture or origin beyond this frame or after Clear.
/// </summary>
internal static class SpeedModeBindings
{
    internal readonly record struct Frame(Texture2D Surface, Texture2D LightTarget,
        Vector2I Origin, Vector4 TargetPixelBounds, Vector3 Ambient, long Sequence);
    internal static Frame Current { get; private set; }
    internal static event System.Action<Frame> LightReady;
    internal static event System.Action<Frame> Invalidated;

    internal static Vector2I CameraOrigin(int tileX,int tileY,float walkX,float walkY,float peekX,float peekY,float zoom)
        => new((int)System.MathF.Floor(tileX+(walkX+walkY+zoom*(peekX+peekY))/44f),
            (int)System.MathF.Floor(tileY+(walkY-walkX+zoom*(peekY-peekX))/44f));
    private static object _owner;
    private static long _sequence;

    internal static void PublishSurface(object owner, Texture2D surface, Vector2I origin, Vector2I size)
    {
        _owner=owner;
        Current=new Frame(surface,null,origin,new Vector4(0,0,size.X,size.Y),default,++_sequence);
    }

    // The light producer calls after its target renders and before compose.
    // Origin and Surface are retained from the same surface ticket. Weather's
    // WaterRender.BindLightTarget(batcher, texture, bounds, ambient) can consume
    // Current.Origin without changing its agreed four-argument signature.
    internal static void PublishLight(object owner, Texture2D texture, Vector4 bounds, Vector3 ambient)
    {
        if (!ReferenceEquals(owner,_owner) || Current.Surface==null)
            throw new System.InvalidOperationException("Light target has no matching surface frame.");
        Current=Current with { LightTarget=texture,TargetPixelBounds=bounds,Ambient=ambient };
        LightReady?.Invoke(Current);
    }

    internal static void Clear(object owner)
    {
        if (!ReferenceEquals(owner,_owner)) return;
        var previous=Current;
        Current=default; _owner=null;
        Invalidated?.Invoke(previous);
    }
}
