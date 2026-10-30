// SPDX-License-Identifier: BSD-2-Clause
using Godot;

namespace GUO.Renderer.SpeedMode.Shadows;

// Native sitting sections are parallelograms, including their shortened V range.
internal readonly record struct NativeShadowQuad(Texture2D Texture,Vector2 A,Vector2 B,Vector2 C,
    Vector2 U,Vector2 V,Vector2 W)
{
    internal SpeedShadowInputs.Stamp Project(Vector2 anchor,Vector2 slope,float height,float opacity,Transform2D view)
    {
        Vector2 across=new Vector2(slope.Y,-slope.X).Normalized();
        Vector2 Point(Vector2 p)=>view*(anchor+across*(p.X-anchor.X)+slope*(anchor.Y-p.Y));
        return new(Texture,Point(A),Point(B),Point(C),U,V,W,opacity,
            (anchor.Y-A.Y)/height,(anchor.Y-C.Y)/height);
    }
}
