// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using GUO.Configuration;
using GUO.Renderer;
using GUO.Renderer.SpeedMode;

namespace GUO.Host;

public partial class LightingSpeedProbe
{
    internal static async Task<Dictionary<string,object>> CheckLiveOff(Node host,string output,string label)
    {
        var profile=ProfileManager.CurrentProfile;
        bool previous=profile.LightingSpeedMode;
        bool previousDebug=SpeedModeDebug.GBuffer,previousCustom=profile.UseCustomLightLevel;
        byte previousLevel=profile.LightLevel; int previousType=profile.LightLevelType;
        async Task DrawnFrames(int count)
        {
            long target=UltimaBatcher2D.FramesBegun+count;
            ulong deadline=Godot.Time.GetTicksMsec()+10000;
            while(UltimaBatcher2D.FramesBegun<target) {
                if(Godot.Time.GetTicksMsec()>deadline) throw new InvalidOperationException("Live lighting proof did not draw.");
                await host.ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            }
        }
        try
        {
            profile.LightingSpeedMode=false; await DrawnFrames(4);
            using var before=host.GetViewport().GetTexture().GetImage(); before.Convert(Image.Format.Rgba8);
            var counts=UltimaBatcher2D.LastFrame;
            double draws=Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            profile.LightingSpeedMode=true; await DrawnFrames(4);
            if(SpeedModeBindings.Current.Surface==null || SpeedModeBindings.Current.LightTarget==null)
                throw new InvalidOperationException("Live lighting proof never published a speed light target.");
            using var on=host.GetViewport().GetTexture().GetImage();
            SpeedModeDebug.GBuffer=true; await DrawnFrames(4);
            using(var gbuf=host.GetViewport().GetTexture().GetImage())
                if(gbuf.SavePng(Path.Combine(output,$"speed-live-{label}-gbuf.png"))!=Error.Ok) throw new IOException("Cannot save live surface classes.");
            SpeedModeDebug.GBuffer=false; await DrawnFrames(4);
            profile.LightingSpeedMode=false; await DrawnFrames(4);
            using var after=host.GetViewport().GetTexture().GetImage(); after.Convert(Image.Format.Rgba8);
            byte[] a=before.GetData(),b=after.GetData(); int diff=0;
            for(int i=0;i<a.Length;i+=4)
                if(a[i]!=b[i] || a[i+1]!=b[i+1] || a[i+2]!=b[i+2] || a[i+3]!=b[i+3]) diff++;
            bool sameCounts=counts==UltimaBatcher2D.LastFrame;
            double afterDraws=Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
            bool sameDraws=draws==afterDraws;
            bool cleared=SpeedModeBindings.Current.Surface==null && SpeedModeBindings.Current.LightTarget==null;
            foreach(var pair in new[] { ("off-before",before),("on",on),("off-after",after) })
                if(pair.Item2.SavePng(Path.Combine(output,$"speed-live-{label}-{pair.Item1}.png"))!=Error.Ok)
                    throw new IOException("Cannot save live lighting proof.");
            var result=new Dictionary<string,object> { ["off_diff_px"]=diff,["counts_equal"]=sameCounts,
                ["off_counts"]=new[] {counts.TextureSwitches,counts.Items,counts.Commands},["off_binding_cleared"]=cleared,
                ["draw_calls_equal"]=sameDraws,["off_draw_calls"]=new[] {draws,afterDraws} };
            GD.Print($"[lighting-speed-live] off-diff={diff} counts-equal={sameCounts} binding-cleared={cleared} draw-calls={draws}/{afterDraws}");
            if(diff!=0 || !sameCounts || !sameDraws || !cleared) throw new InvalidOperationException("Live speed off parity failed; inspect the saved frames.");
            // Client-only existing light-level option; no shard packet or world edit.
            profile.LightingSpeedMode=true; profile.UseCustomLightLevel=true; profile.LightLevel=30; profile.LightLevelType=0;
            await DrawnFrames(4);
            using(var night=host.GetViewport().GetTexture().GetImage())
                if(night.SavePng(Path.Combine(output,$"speed-live-{label}-night-client-override.png"))!=Error.Ok) throw new IOException("Cannot save client night fixture.");
            int offers=Client.Game.GetScene<GUO.Game.Scenes.GameScene>()?.SpeedLightOfferCount??0;
            result["night_light_offers"]=offers;
            File.WriteAllText(Path.Combine(output,$"speed-live-{label}.json"),System.Text.Json.JsonSerializer.Serialize(result));
            GD.Print("[lighting-speed-live] client-night-override light-offers="+offers);
            return result;
        }
        finally { profile.LightingSpeedMode=previous; SpeedModeDebug.GBuffer=previousDebug;
            profile.UseCustomLightLevel=previousCustom; profile.LightLevel=previousLevel; profile.LightLevelType=previousType; }
    }
}
