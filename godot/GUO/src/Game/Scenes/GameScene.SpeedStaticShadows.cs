// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): ADR-0048 reads the completed native static geometry.
using System;
using Godot;
using GUO.Game.GameObjects;
using GUO.Renderer;
using GUO.Renderer.Lights;
using GUO.Renderer.SpeedMode.Shadows;

namespace GUO.Game.Scenes;

internal partial class RenderLists
{
    internal GameObject SpeedShadowStatic(int group)
    {
        int index=group-1;
        return (uint)index<(uint)_world.Count && _world[index].Object is Static or Multi or Item
            ? _world[index].Object : null;
    }
}

internal partial class GameScene
{
    private readonly NativeStaticShadow _speedStaticContact=new();
    internal int SpeedStaticQuadCount { get; private set; }
    internal Action<GameObject,NativeShadowQuad,Vector2,bool> SpeedStaticShadowObserver;

    private void CollectSpeedStaticShadows(SpeedShadowInputs inputs,UltimaBatcher2D batcher,
        Transform2D view,Vector2 direction,float length,float strength)
    {
        SpeedStaticQuadCount=0;
        var capture=batcher.OrderedWorldProbe;
        if(!inputs.SunEnabled || capture==null || !capture.CanAttachSpeedLight) return;
        var tiles=Client.Game.UO.FileManager.TileData.StaticData;
        bool Eligible(GameObject obj) {
            if(obj is not (Static or Multi or Item) || obj.IsDestroyed || !obj.AllowedToDraw || obj.Z>=_maxZ || obj.Graphic>=tiles.Length) return false;
            ref var data=ref Client.Game.UO.FileManager.TileData.StaticData[obj.Graphic];
            if(!SunShadowProjection.Eligible(data.Flags,data.Height)) return false;
            // Preserve existing policy here; the receiver/height lane owns its repair.
            short cover=_rtField.CoverZ(obj.X,obj.Y);
            return cover==RealtimeLightField.NoBlock || cover>=_maxZ || obj.Z>=cover
                || (_noDrawRoofs && _rtField.CoverIsRoofFlag(obj.X,obj.Y));
        }
        void Add(GameObject owner,NativeShadowQuad quad,bool resident) {
            if(!_speedStaticContact.TryContact(quad,out var contact)) return;
            float top=Math.Min(Math.Min(quad.A.Y,quad.B.Y),Math.Min(quad.C.Y,(quad.B+quad.C-quad.A).Y));
            float height=Math.Max(1,contact.Y-top);
            inputs.Sun.Add(quad.Project(contact,direction*length,height,strength,view));
            SpeedStaticQuadCount++;
            SpeedStaticShadowObserver?.Invoke(owner,quad,contact,resident);
        }
        int count=capture.BeginSpeedShadowRead();
        try {
            var inverse=view.AffineInverse();
            for(int i=0;i<count;i++) {
                var owner=_renderLists.SpeedShadowStatic(capture.SpeedShadowGroup(i));
                if(Eligible(owner) && capture.ReadSpeedShadowQuad(i,inverse,out var quad)) Add(owner,quad,false);
            }
            // Resident statics bypass the ordered occurrence array. Use their
            // exact native mesh/visibility, with the same integer camera offset.
            // CanAttachSpeedLight guarantees resident transaction admission.
            foreach(var chunk in _meshChunks) {
                if(chunk==null || chunk.IsDestroyed || chunk.Mesh==null) continue;
                var layer=chunk.Mesh.Statics;
                for(int y=0;y<8;y++) for(int x=0;x<8;x++)
                    for(var owner=chunk.Tiles[x,y];owner!=null;owner=owner.TNext) {
                        int index=owner.MeshSpriteIndex;
                        if(!owner.InChunkMesh || owner.Z>_maxGroundZ || (uint)index>=(uint)layer.Count || !layer.Visible[index]
                            || !Eligible(owner)) continue;
                        Add(owner,NativeStaticShadow.FromMesh(layer.Textures[index],layer.Vertices[index],
                            new Vector2(-_offset.X,-_offset.Y)),true);
                    }
            }
        }
        finally { capture.EndSpeedShadowRead(); }
    }
}
