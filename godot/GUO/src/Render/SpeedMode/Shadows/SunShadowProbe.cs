// SPDX-License-Identifier: BSD-2-Clause
// Synthetic sun projection and RD max-mask fixture; no shard or game art.
using System;
using System.IO;
using Godot;
using GUO.Assets;
using GUO.Renderer.Lights;
using N2 = System.Numerics.Vector2;

namespace GUO.Renderer.SpeedMode.Shadows;

public partial class SunShadowProbe : Node
{
    public override void _Ready()
    {
        try
        {
            Run(Array.IndexOf(OS.GetCmdlineUserArgs(),"--gpu") >= 0);
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("[sun-shadow-probe] FAIL " + error);
            GetTree().Quit(1);
        }
    }

    internal static void Run(bool gpu = false)
    {
        var profile = System.Text.Json.JsonSerializer.Deserialize("{}",GUO.Configuration.ProfileJsonContext.DefaultToUse.Profile);
        Check(!profile.LightingSpeedMode && profile.SpeedModeLampShadows == 6 && profile.SpeedModeSunShadows && profile.SpeedModeWindowLight,
            "Classic/off parent and shadow/window defaults");
        profile.SpeedModeLampShadows = 99; profile.SpeedModeSunShadows = false; profile.SpeedModeWindowLight = false;
        profile = System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(profile,GUO.Configuration.ProfileJsonContext.DefaultToUse.Profile),
            GUO.Configuration.ProfileJsonContext.DefaultToUse.Profile);
        Check(profile.SpeedModeLampShadows == 10 && !profile.SpeedModeSunShadows && !profile.SpeedModeWindowLight,"clamped persistence");
        profile.SpeedModeLampShadows = -1;
        Check(profile.SpeedModeLampShadows == 0,"negative lamp count clamps to off");
        N2 screen = new(100,200);
        Check(SunShadowProjection.ContactAnchor(screen,10,false,N2.Zero,0) == new N2(122,234), "contact-row anchor");
        Check(SunShadowProjection.ContactAnchor(screen,0,true,new N2(3.5f,1.25f),2.5f) == new N2(125.5f,217.75f), "interpolated mobile anchor");
        Check(!SunShadowProjection.Eligible(TileFlag.Bridge | TileFlag.Wall,20), "bridges cast nothing");
        Check(SunShadowProjection.Eligible(TileFlag.Foliage,0), "zero-height foliage retains its native sun silhouette");
        Check(!SunShadowProjection.Eligible(TileFlag.Wall,20), "walls do not stamp floor-shaped sun silhouettes");
        var noon = SunShadowProjection.FromShardMinute(720);
        var midnight = SunShadowProjection.FromShardMinute(0);
        Check(N2.Dot(noon.Direction,midnight.Direction) < -.99f,"opposite lunar arc");
        Check(noon.Strength > midnight.Strength && noon.Length < 1.1f,"clock-driven direction and height");
        Check(SunShadowProjection.FromShardMinute(1440) == midnight,"clock wraps at midnight");
        ShardClockState clock = default;
        Check(SunShadowProjection.Resolve(clock,1,1234).Source == "shard-brightness-approximation","missing-clock source labelled");
        Check(clock.Set(12,34,56,1234) && clock.MinuteOfDay == 754 && clock.ReceivedTicks == 1234,"validated shard clock stored");
        Check(SunShadowProjection.Resolve(clock,0,1234).Source == "shard-clock","valid time overrides brightness proxy");
        Check(clock.MinuteAt(51234) == 755,"shard stamp advances by monotonic elapsed ticks");
        Check(clock.Set(23,59,59,uint.MaxValue-499) && clock.MinuteAt(500) == 0,"midnight and monotonic rollover");
        Check(!clock.Set(24,0,0,1235) && !clock.Valid,"invalid shard time rejected");
        if (gpu) Gpu();
        GD.Print($"[sun-shadow-probe] PASS backend={(gpu ? "gpu-max-mask" : "geometry")} contact=opaque-row mobile=interpolated bridge=excluded sources=shard-clock,shard-brightness-approximation");
    }

    private static void Gpu()
    {
        RenderingDevice rd = RenderingServer.CreateLocalRenderingDevice()
            ?? throw new NotSupportedException("GPU fixture needs Forward+/Mobile and a Godot slot.");
        var mask = new ProjectedShadowMaskRd(rd,publish:false);
        try
        {
            mask.Begin(1280,720);
            Check(mask.Supported,"GPU mask supported");
            // Far corner of the full 720p target: old player-centred clipping
            // would lose this stamp. Repeated silhouettes must max, never add.
            for (int i = 0; i < 3; i++)
                mask.StampOpaque(new Vector2(1150,650),new Vector2(1260,650),new Vector2(1150,710),.4f);
            Check(mask.Finish() != null,"GPU mask draw");
            rd.Submit(); rd.Sync();
            byte[] bytes = rd.TextureGetData(mask.ShadowRid,0);
            Check(bytes[680*1280+1200] is >= 101 and <= 103,"max overlap does not stack");
            Check(bytes[0] == 0,"empty target stays clear");
            string folder = System.Environment.GetEnvironmentVariable("GUO_SHADOW_PROBE_OUT");
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
                using Image image = Image.CreateFromData(1280,720,false,Image.Format.R8,bytes);
                Check(image.SavePng(Path.Combine(folder,"sun-max-gpu.png")) == Error.Ok,"GPU mask PNG");
            }
        }
        finally { mask.DisposeAll(); rd.Free(); }
    }

    private static void Check(bool value,string message) { if (!value) throw new InvalidOperationException(message); }
}
