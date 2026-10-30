// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): ADR-0048 opt-in scene adapter. Native draw/pick lists unchanged.
using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Game.GameObjects;
using GUO.Renderer;
using GUO.Renderer.Lights;
using GUO.Renderer.SpeedMode;
using GUO.Renderer.SpeedMode.Shadows;
using GUO.Renderer.SpeedMode.WindowLight;
using N2=System.Numerics.Vector2;
using N3=System.Numerics.Vector3;
using N4=System.Numerics.Vector4;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    private SpeedShadowInputs _speedShadows;
    private ulong _shadowGridHash;
    private readonly Dictionary<ushort,int> _shadowBottomGap=new();
    internal int SpeedWindowCount=>_speedShadows?.Windows.Count??0;
    internal int SpeedSunStampCount=>_speedShadows?.Sun.Count??0;
    internal string SpeedSunSource { get; private set; }

    // Called after the core has retired its previous ticket and built reaches.
    // Shared GameScene.SpeedMode.cs supplies the profile controls at this seam.
    private void PrepareSpeedShadowInputs(SpeedLightFrame frame,UltimaBatcher2D batcher,Transform2D matrix,
        int lamps=6,bool sun=true,bool windows=true,ShardClockState clock=default)
    {
        _speedShadows??=new SpeedShadowInputs();
        var inputs=_speedShadows; inputs.Begin();
        inputs.LampCount=frame.Count; inputs.Requested=Math.Clamp(lamps,0,10); inputs.SunEnabled=sun;
        inputs.Viewer=new N3(_world.Player.X,_world.Player.Y,_world.Player.Z);
        for(int i=0;i<frame.Count;i++) {
            var s=frame.Lights[i].Source;
            inputs.Lamps[i]=new ShadowLamp(new N3(s.X+s.OffsetX+.5f,s.Y+s.OffsetY+.5f,s.Z+10),s.Radius,(uint)i);
        }
        ref var tiles=ref Client.Game.UO.FileManager.TileData.StaticData;
        // The reach snapshot already has stable chunk membership/content stamps.
        // Rebuild from source, never aggregate BlockTop (which includes trees).
        if(_shadowGridHash!=_speedGridHash || inputs.Grid.Width==0) {
            inputs.Grid.Begin(_rtField.OriginX,_rtField.OriginY,_rtField.Width,_rtField.Height);
            foreach(var chunk in _speedGridChunks) for(int y=0;y<8;y++) for(int x=0;x<8;x++) {
                bool foliage=false;
                for(var o=chunk.Tiles[x,y];o!=null;o=o.TNext)
                    if(o is Static or Multi && o.Graphic<tiles.Length && tiles[o.Graphic].IsFoliage) foliage=true;
                for(var o=chunk.Tiles[x,y];o!=null;o=o.TNext) {
                    if(o is not (Static or Multi) || o.Graphic>=tiles.Length) continue;
                    ref var data=ref tiles[o.Graphic];
                    if(!WallShadowGrid.Accepts(data.Flags,data.Height,data.Name,foliage)) continue;
                    WallFaces faces=data.IsWall ? WallFaces.For(o.Graphic) : default;
                    if(faces.Count==0) inputs.Grid.AddBlocker(o.X,o.Y,o.Z+data.Height,data.Flags,data.Height,data.Name,foliage);
                    else {
                        inputs.Grid.AddEdge(o.X,o.Y,faces.First.AlongX,faces.First.Offset,o.Z+data.Height);
                        if(faces.Count>1) inputs.Grid.AddEdge(o.X,o.Y,faces.Second.AlongX,faces.Second.Offset,o.Z+data.Height);
                    }
                }
            }
            _shadowGridHash=_speedGridHash;
        }
        // Until the lead binds validated packet0x5B, use the named brightness
        // approximation. Never derive server time from the PC clock.
        var sky=SunShadowProjection.Resolve(clock,RealtimeAmbientLevel(),Time.Ticks);
        SpeedSunSource=sky.Source;
        N2 ray=sky.Direction;
        Vector2 direction=new(ray.X-ray.Y,ray.X+ray.Y);
        direction=direction.Normalized();
        Transform2D view=matrix;
        Span<int> selected=stackalloc int[10];
        int selectedCount=PolarShadowGeometry.Select(inputs.Lamps.AsSpan(0,frame.Count),inputs.Viewer,inputs.Requested,selected);
        Span<MobileLampShadow> affecting=stackalloc MobileLampShadow[2];
        foreach(var chunk in _visibleChunks) {
            if(chunk==null || chunk.IsDestroyed) continue;
            for(int y=0;y<8;y++) for(int x=0;x<8;x++) for(var obj=chunk.Tiles[x,y];obj!=null;obj=obj.TNext) {
                if(!obj.AllowedToDraw || obj.Z>=_maxZ) continue;
                Vector2 screen=new((obj.X-obj.Y)*22-_offset.X,(obj.X+obj.Y)*22-obj.Z*4-_offset.Y);
                if(obj is Mobile mobile) {
                    if(mobile.IsDead || mobile.IsHidden) continue;
                    ushort graphic=mobile.GetGraphicForAnimation();
                    byte dir=(byte)((byte)mobile.GetDirectionForAnimation()&7); bool flip=false;
                    var animations=Client.Game.UO.Animations;
                    animations.GetAnimDirection(ref dir,ref flip);
                    var frames=animations.GetAnimationFrames(graphic,Mobile.GetGroupForAnimation(mobile,graphic,true),dir,out _,out _);
                    if(frames.Length==0) continue;
                    ref var art=ref frames[Math.Min(mobile.AnimIndex,frames.Length-1)];
                    if(art.Texture==null) continue;
                    Vector2 anchor=screen+new Vector2(22+mobile.Offset.X,19+mobile.Offset.Y-mobile.Offset.Z);
                    Vector2 pos=anchor-new Vector2(flip ? art.UV.Width-art.Center.X : art.Center.X,art.UV.Height+art.Center.Y);
                    if(sun) inputs.Sun.Add(SpeedShadowInputs.Project(art.Texture,art.UV,pos,anchor,direction*(art.UV.Height*sky.Length),sky.Strength,flip,view));
                    var foot=new N3(mobile.X+(mobile.Offset.X+mobile.Offset.Y)/44f,mobile.Y+(mobile.Offset.Y-mobile.Offset.X)/44f,mobile.Z);
                    int count=MobileLampProjection.Select(inputs.Lamps.AsSpan(0,frame.Count),foot,affecting);
                    for(int j=0;j<count;j++) for(int row=0;row<selectedCount;row++) if(selected[row]==affecting[j].Offer) {
                        var cast=affecting[j]; inputs.Mobile[row]??=new List<SpeedShadowInputs.Stamp>(32);
                        inputs.Mobile[row].Add(SpeedShadowInputs.Project(art.Texture,art.UV,pos,anchor,
                            new Vector2(cast.Direction.X,cast.Direction.Y)*(art.UV.Height*cast.Length),cast.Strength,flip,view));
                    }
                    continue;
                }
                if(obj is not (Static or Multi or Item) || obj.Graphic>=tiles.Length) continue;
                ref var td=ref tiles[obj.Graphic];
                if(windows && WindowLightTable.BuiltIn.TryGetValue(obj.Graphic,out var opening))
                    CollectSpeedWindow(inputs,opening,obj,ray,view);
                if(!sun || !SunShadowProjection.Eligible(td.Flags,td.Height)) continue;
                // Covered props cannot cast through a visible roof/floor.
                short cover=_rtField.CoverZ(obj.X,obj.Y);
                if(cover!=RealtimeLightField.NoBlock && cover<_maxZ && obj.Z<cover && !(_noDrawRoofs && _rtField.CoverIsRoofFlag(obj.X,obj.Y))) continue;
                ref readonly var sprite=ref Client.Game.UO.Arts.GetArt(obj.Graphic);
                if(sprite.Texture==null) continue;
                if(!_shadowBottomGap.TryGetValue(obj.Graphic,out int gap)) {
                    var raw=Client.Game.UO.FileManager.Arts.GetArt((uint)(obj.Graphic+ArtLoader.MAX_LAND_DATA_INDEX_COUNT));
                    gap=0;
                    for(int r=raw.Height-1;r>=0;r--) {
                        bool opaque=false;
                        for(int c=0;c<raw.Width;c++) if(raw.Pixels[r*raw.Width+c]!=0) { opaque=true; break; }
                        if(opaque) break; gap++;
                    }
                    _shadowBottomGap[obj.Graphic]=gap;
                }
                Vector2 contact=screen+new Vector2(22,44-gap);
                Vector2 position=screen+new Vector2(22-sprite.UV.Width/2,44-sprite.UV.Height);
                inputs.Sun.Add(SpeedShadowInputs.Project(sprite.Texture,sprite.UV,position,contact,
                    direction*(sprite.UV.Height*sky.Length),sky.Strength,false,view));
            }
        }
        frame.RenderInputs=inputs.RenderCallback; frame.RenderPatches=inputs.PatchCallback; frame.ReleaseInputs=inputs.ReleaseCallback;
    }

    private void CollectSpeedWindow(SpeedShadowInputs inputs,WindowOpening opening,GameObject obj,N2 ray,Transform2D view)
    {
        if(inputs.Windows.Count>=4096 || opening.Shape==OpeningShape.None) return;
        var faces=WallFaces.For(obj.Graphic);
        if(faces.Count==0) return;
        for(int i=0;i<faces.Count;i++) {
            var face=i==0 ? faces.First : faces.Second;
            N2 tangent=face.AlongX ? N2.UnitX : N2.UnitY;
            N2 normal=face.AlongX ? N2.UnitY : N2.UnitX;
            for(int side=0;side<2;side++) {
                float sign=side==0 ? 1 : -1;
                N2 inward=normal*sign;
                N2 origin=new N2(obj.X,obj.Y)+(face.AlongX ? new N2(.5f,face.Offset) : new N2(face.Offset,.5f));
                N2 inside=origin+inward*.6f,outside=origin-inward*.6f;
                bool covered=_rtField.IsRoofed((int)MathF.Floor(inside.X),(int)MathF.Floor(inside.Y));
                bool open=!_rtField.IsRoofed((int)MathF.Floor(outside.X),(int)MathF.Floor(outside.Y));
                float z=_rtField.GroundZ((int)MathF.Floor(inside.X),(int)MathF.Floor(inside.Y));
                if(!opening.TryProject(origin,tangent,inward,ray,out N2 width,out N2 depth)) continue;
                origin-=width*.5f;
                Vector2 lo=new(float.MaxValue,float.MaxValue),hi=new(float.MinValue,float.MinValue);
                for(int corner=0;corner<4;corner++) for(int upright=0;upright<2;upright++) {
                    N2 p=origin+width*(corner&1)+depth*((corner>>1)&1);
                    Vector2 pixel=view*new Vector2((p.X-p.Y)*22-_offset.X,(p.X+p.Y)*22-(z+upright*4)*4-_offset.Y);
                    lo=lo.Min(pixel); hi=hi.Max(pixel);
                }
                if(WindowLightPatch.TryCreate(opening,origin,z,tangent,inward,ray,covered,open,RealtimeAmbientLevel(),
                    new N4(lo.X-1,lo.Y-1,hi.X+1,hi.Y+1),new N4(1,.88f,.65f,1),out var patch)) inputs.Windows.Add(patch);
            }
        }
    }
}
