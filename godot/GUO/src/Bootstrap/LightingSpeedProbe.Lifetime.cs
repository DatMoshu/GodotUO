// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode;

namespace GUO.Host;

// Regression for the light target's RID ownership (idea from MeesaJarJar fork 495f077): after soften the
// SpeedLightTexture wrapper borrows the presentation target, so size changes and speed-mode toggles must
// never leave it on a freed RID or leave the published light black. Needs a rendering device (not headless).
public partial class LightingSpeedProbe
{
    private static void CheckLightLifetime(OrderedWorldDevice device,SpeedLightFrame frame,Vector2I origin,
        Func<Vector2I,Rid> resize)
    {
        static void Lit(OrderedWorldDevice d,string stage)
        {
            if(!d.SpeedLightReady || !d.SpeedLightPublishedLive)
                throw new InvalidOperationException($"Light lifetime [{stage}]: wrapper RID is not a live texture.");
            if(SpeedModeBindings.Current.LightTarget!=d.SpeedLightTexture)
                throw new InvalidOperationException($"Light lifetime [{stage}]: light target not published.");
            byte[] data=d.ReadSpeedLightPublishedRT();
            float peak=0;
            for(int i=0;i+1<data.Length;i+=8) peak=Math.Max(peak,(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data,i)));
            if(!(peak>0.01f)) throw new InvalidOperationException($"Light lifetime [{stage}]: published light is black.");
        }
        int[] softens={ 3,0,10,3 };
        for(int i=0;i<8;i++)
        {
            var size=i%2==0 ? new Vector2I(800,200) : new Vector2I(960,240);
            frame.Soften=softens[i%softens.Length];
            Rid heldAcrossResize=resize(size);
            if(heldAcrossResize.IsValid && device.SpeedLightPublishedRid==heldAcrossResize && !device.SpeedLightPublishedLive)
                throw new InvalidOperationException($"Light lifetime [{i} resize]: wrapper kept a freed RID {heldAcrossResize.Id}.");
            device.RenderLightsRT(frame); device.RenderSoftenRT(frame);
            Lit(device,$"{i} resize soften={frame.Soften}");
            device.DisableLightRT(); // speed-mode light toggled off, then on again
            device.RenderLightsRT(frame); device.RenderSoftenRT(frame);
            Lit(device,$"{i} toggle soften={frame.Soften}");
        }
        GD.Print("[lighting-speed-probe] LIGHT LIFETIME PASS resize+soften+toggle x8 wrapper-live=pass non-black=pass");
    }
}
