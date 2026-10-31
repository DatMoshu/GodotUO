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

    // Screen Y grows downward. Keep the part of an upright card at or above
    // yCut (the cover plane). A fully sheltered or non-upright partial span
    // returns false so the indoor pixels are not stamped.
    internal bool TryKeepAbove(float yCut,out NativeShadowQuad clipped)
    {
        clipped=this;
        Vector2 d=B+C-A;
        float top=Mathf.Min(Mathf.Min(A.Y,B.Y),Mathf.Min(C.Y,d.Y));
        float bottom=Mathf.Max(Mathf.Max(A.Y,B.Y),Mathf.Max(C.Y,d.Y));
        if(!float.IsFinite(yCut)||!float.IsFinite(top)||!float.IsFinite(bottom)) return false;
        if(yCut>=bottom-0.5f) return true;
        if(yCut<=top+0.5f) return false;
        bool upright=Mathf.Abs(A.Y-B.Y)<0.01f && Mathf.Abs(A.X-C.X)<0.01f && C.Y>A.Y+0.5f;
        if(!upright) return false;
        float t=(yCut-A.Y)/(C.Y-A.Y);
        if(t<=0f||t>=1f) return false;
        clipped=this with { C=A+(C-A)*t, W=U+(W-U)*t };
        return float.IsFinite(clipped.C.X)&&float.IsFinite(clipped.C.Y);
    }
}
