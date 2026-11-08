// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode;
using RD=Godot.RenderingDevice;

namespace GUO.Host;

public partial class LightingSpeedProbe
{
    private static void CheckShadowInputs(OrderedWorldDevice device,SpeedLightFrame frame,byte[] surface,byte[] lit,int floor,int covered)
    {
        static float Read(byte[] bytes,int pixel,int channel=0)
            =>(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes,pixel*8+channel*2));
        var rd=RenderingServer.GetRenderingDevice();
        Rid atlas=default,sun=default;
        int witness=-1;
        var source=frame.Lights[0].Source;
        for(int i=0;i<960*240;i++) {
            if(((int)Read(surface,i,3)&15)!=1 || Read(lit,i)-frame.Ambient.X<0.3f) continue;
            float dx=Read(surface,i)+frame.Origin.X-(source.X+source.OffsetX+0.5f);
            float dy=Read(surface,i,1)+frame.Origin.Y-(source.Y+source.OffsetY+0.5f);
            if(dx*dx+dy*dy>0.36f) { witness=i; break; }
        }
        if(witness<0) throw new InvalidOperationException("No shadow receiver witness.");
        try
        {
            byte[] polar=new byte[256*64*4];
            ushort top=BitConverter.HalfToUInt16Bits((Half)100);
            for(int i=0;i<256*64;i++) { polar[i*4+2]=(byte)top; polar[i*4+3]=(byte)(top>>8); }
            using var format=new RDTextureFormat { Width=256,Height=64,Depth=1,ArrayLayers=1,Mipmaps=1,
                TextureType=RD.TextureType.Type2D,Format=RD.DataFormat.R16G16Sfloat,UsageBits=RD.TextureUsageBits.SamplingBit };
            using var view=new RDTextureView();
            atlas=rd.TextureCreate(format,view,new Godot.Collections.Array<byte[]> { polar });
            if(!atlas.IsValid) throw new InvalidOperationException("Cannot make synthetic shadow atlas.");
            frame.LampShadowAtlas=atlas; frame.ShadowSoftnessTenths=0; frame.Lights[0].ShadowRow=0;
            device.RenderLightsRT(frame);
            float expected=frame.Ambient.X+(Read(lit,witness)-frame.Ambient.X)*0.15f;
            if(Math.Abs(Read(device.ReadSpeedLightRT(),witness)-expected)>0.01f)
                throw new InvalidOperationException("Per-light shadow multiplier did not retain 15%.");
            frame.Lights[0].ShadowRow=-1; device.RenderLightsRT(frame);
            if(Math.Abs(Read(device.ReadSpeedLightRT(),witness)-Read(lit,witness))>0.001f)
                throw new InvalidOperationException("Unselected shadow row affected a light.");
            var mask=new byte[960*240]; Array.Fill(mask,(byte)128);
            format.Width=960; format.Height=240; format.Format=RD.DataFormat.R8Unorm;
            sun=rd.TextureCreate(format,view,new Godot.Collections.Array<byte[]> { mask });
            if(!sun.IsValid) throw new InvalidOperationException("Cannot make synthetic sun mask.");
            frame.Count=0; frame.SunMask=sun; device.RenderLightsRT(frame);
            byte[] ambient=device.ReadSpeedLightRT();
            if(Math.Abs(Read(ambient,floor)-frame.Ambient.X*(1-128/255f))>0.001f
                || Math.Abs(Read(ambient,covered)-frame.Ambient.X*frame.RoofShare)>0.001f)
                throw new InvalidOperationException("Sun shadow did not affect floor ambient only.");
            frame.Count=1; frame.LampShadowAtlas=frame.SunMask=default;
            device.RenderLightsRT(frame); // Release borrowed descriptors before their textures.
            GD.Print("[lighting-speed-probe] SHADOW INPUT PASS lamp-retains=15% unselected-row=pass sun-floor-only=pass");
        }
        finally { frame.Count=1; frame.LampShadowAtlas=frame.SunMask=default;
            if(atlas.IsValid) rd.FreeRid(atlas); if(sun.IsValid) rd.FreeRid(sun); }
    }
}
