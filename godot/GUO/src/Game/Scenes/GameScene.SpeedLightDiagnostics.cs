// SPDX-License-Identifier: BSD-2-Clause
// Task1054: cold, disconnected fixture observation; no production call sites.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using GUO.Configuration;
using GUO.Renderer;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode;
using GUO.Renderer.SpeedMode.Shadows;
using N3=System.Numerics.Vector3;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    // Call on the stopped fixture's game thread after its completed draw.
    // Copy JSON before another draw; render callbacks never read live scene objects.
    internal string CaptureSpeedLightDiagnostics(UltimaBatcher2D batcher)
    {
        if(GUO.Network.NetClient.Socket.IsConnected)
            throw new InvalidOperationException("Light attribution is disconnected-fixture only.");
        var capture=batcher.OrderedWorldProbe;
        if(capture==null) throw new InvalidOperationException("Ordered capture is required.");
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        T Read<T>(string name)=>(T)capture.GetType().GetField(name,flags).GetValue(capture);
        var device=Read<OrderedWorldDevice>("_device");
        var occurrences=Read<List<OrderedWorldResidency.Occurrence>>("_occurrences");
        var dynamic=Read<List<OrderedWorldResidency.Quad>>("_dynamic");
        int classic=0;
        foreach(var occurrence in occurrences) {
            ref readonly var q=ref occurrence.Get(CollectionsMarshal.AsSpan(dynamic));
            int mode=((int)MathF.Floor(q.A.PackedHue.Z*255+.5f))&0x3f;
            if(mode==8) classic++;
        }
        var profile=ProfileManager.CurrentProfile;
        var binding=SpeedModeBindings.Current;
        var frame=_speedLights;
        var inputs=_speedShadows;
        var offers=new List<object>();
        var foot=new N3(_world.Player.X+(_world.Player.Offset.X+_world.Player.Offset.Y)/44f,
            _world.Player.Y+(_world.Player.Offset.Y-_world.Player.Offset.X)/44f,_world.Player.Z);
        Span<MobileLampShadow> affecting=stackalloc MobileLampShadow[2];
        int count=inputs==null?0:MobileLampProjection.Select(inputs.Lamps.AsSpan(0,inputs.LampCount),foot,affecting);
        if(frame!=null) for(int i=0;i<frame.Count;i++) {
            var e=frame.Lights[i];var s=e.Source;int row=e.ShadowRow;
            object projection=null;
            for(int j=0;j<count;j++) if(affecting[j].Offer==i) {
                var a=affecting[j];
                projection=new { directionX=a.Direction.X,directionY=a.Direction.Y,a.Length,a.Strength };
            }
            offers.Add(new { offer=i,s.X,s.Y,s.Z,s.OffsetX,s.OffsetY,s.Radius,s.Energy,
                colour=new[]{s.Color.R,s.Color.G,s.Color.B},curve=s.Curve.ToString(),e.Flicker,
                pixelCentre=new[]{e.PixelCentre.X,e.PixelCentre.Y},e.PixelRadius,
                shadowPosition=inputs!=null&&i<inputs.LampCount
                    ?new[]{inputs.Lamps[i].Position.X,inputs.Lamps[i].Position.Y,inputs.Lamps[i].Position.Z}:null,
                row,mobileMask=e.MobileShadow.IsValid,
                stamps=inputs!=null&&row>=0&&row<inputs.Mobile.Length?inputs.Mobile[row]?.Count??0:0,projection });
        }
        var receivers=new List<object>();
        if(_rtField!=null) for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++) {
            int x=_world.Player.X+dx,y=_world.Player.Y+dy;
            receivers.Add(new{x,y,groundZ=_rtField.GroundZ(x,y),coverZ=_rtField.CoverZ(x,y),
                roof=_rtField.CoverIsRoofFlag(x,y)});
        }
        return JsonSerializer.Serialize(new {
            tick=Time.Ticks,player=new{_world.Player.X,_world.Player.Y,_world.Player.Z},
            profile=new{profile.ShadowsEnabled,profile.ShadowsStatics,profile.LightingSpeedMode,
                profile.SpeedModeSunShadows,profile.SpeedModeLampShadows},
            capturedClassicMode8Occurrences=classic, // Capture count, never a pixel/output claim.
            OrderedWorldCapture.LastFrameOnPass,OrderedWorldCapture.LastFallback,
            OrderedWorldCapture.FramesDrawn,OrderedWorldCapture.FramesFallback,
            device.SpeedLightReady,binding.Sequence,surfacePublished=binding.Surface!=null,
            lightPublished=binding.LightTarget!=null,
            origin=new[]{binding.Origin.X,binding.Origin.Y},
            targetBounds=new[]{binding.TargetPixelBounds.X,binding.TargetPixelBounds.Y,binding.TargetPixelBounds.Z,binding.TargetPixelBounds.W},
            roofShare=frame?.RoofShare,ambient=frame==null?null:new[]{frame.Ambient.X,frame.Ambient.Y,frame.Ambient.Z},receivers,
            frameOrigin=frame==null?null:new[]{frame.Origin.X,frame.Origin.Y},
            frameTarget=frame==null?null:new[]{frame.TargetSize.X,frame.TargetSize.Y},
            sunMask=frame?.SunMask.IsValid??false,sunStamps=inputs?.Sun.Count??0,
            lampAtlas=frame?.LampShadowAtlas.IsValid??false,SpeedSunSource,SpeedMobileQuadCount,offers
        });
    }
}
#endif
