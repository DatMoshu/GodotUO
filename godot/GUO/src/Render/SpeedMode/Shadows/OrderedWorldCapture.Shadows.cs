// SPDX-License-Identifier: BSD-2-Clause
// Read only the completed native ticket; never retain its mutable streams.
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace GUO.Renderer.DepthWorld;

internal sealed partial class OrderedWorldCapture
{
    private readonly Dictionary<Rid,Texture2D> _shadowTextureOwners=new();

    internal int BeginSpeedShadowRead()
    {
        _shadowTextureOwners.Clear();
        if(!RenderingServer.IsOnRenderThread() || !CanAttachSpeedLight) return 0;
        foreach(var pair in _textures) _shadowTextureOwners[pair.Value.ArtRs]=pair.Key;
        return _commands.Count;
    }

    internal int SpeedShadowGroup(int index)=>_commands[index].Group;

    internal bool ReadSpeedShadowQuad(int index,Transform2D inverseView,out SpeedMode.Shadows.NativeShadowQuad result)
    {
        result=default;
        var command=_commands[index];
        if(!_shadowTextureOwners.TryGetValue(command.Texture,out var texture)) return false;
        int occurrence=command.FirstCorner/4;
        ref readonly var q=ref _occurrences[occurrence].Get(CollectionsMarshal.AsSpan(_dynamic));
        // Classic shadow pixels are a separate representation, never casters.
        static bool Shadow(in OrderedWorldDevice.Vertex vertex) {
            int mode=((int)System.MathF.Round(vertex.PackedHue.Z*255))&0x3f;
            if(mode>=20) mode-=20;
            return mode==8;
        }
        if(Shadow(q.A) || Shadow(q.B) || Shadow(q.C) || Shadow(q.D)) return false;
        var state=_states[occurrence];
        var transform=inverseView*new Transform2D(new Vector2(state.Xx,state.Xy),
            new Vector2(state.Yx,state.Yy),new Vector2(state.OriginX,state.OriginY));
        var a=q.A.PositionUv; var b=q.B.PositionUv; var c=q.C.PositionUv;
        result=new(texture,transform*new Vector2(a.X,a.Y),transform*new Vector2(b.X,b.Y),
            transform*new Vector2(c.X,c.Y),new Vector2(a.Z,a.W),new Vector2(b.Z,b.W),new Vector2(c.Z,c.W));
        // Native equipment slicing can submit an empty final section. Its
        // zero-area/non-finite primitive draws no pixels and must not poison masks.
        return result.A.IsFinite() && result.B.IsFinite() && result.C.IsFinite()
            && result.U.IsFinite() && result.V.IsFinite() && result.W.IsFinite()
            && System.Math.Abs((result.B-result.A).Cross(result.C-result.A))>0.0001f;

    }

    internal void EndSpeedShadowRead()=>_shadowTextureOwners.Clear();
}
