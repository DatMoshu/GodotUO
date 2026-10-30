// SPDX-License-Identifier: BSD-2-Clause
using Godot;
using System;
using GUO.Configuration;
using GUO.Renderer;
using GUO.Renderer.Lights;
using GUO.Renderer.SpeedMode;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    private SpeedLightFrame _speedLights;
    private Texture2D _speedLightOverride;
    internal int SpeedLightOfferCount=>_speedLights?.Count??0;
    internal bool UseSpeedLights=>SpeedModeDebug.Active && _world.InGame
        && !(_world.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect);
    // Other slice adapters implement these in their own GameScene partial.
    partial void PrepareSpeedLightInputs(SpeedLightFrame frame);
    partial void PrepareSpeedShadowInputs(SpeedLightFrame frame,UltimaBatcher2D batcher,Transform2D matrix);
    partial void DisposeSpeedShadows();
    partial void BindSpeedLightTarget(UltimaBatcher2D batcher,Texture2D lightTarget,Vector4 targetPixelBounds,Vector3 ambient);
    partial void ClearSpeedLightTarget(UltimaBatcher2D batcher);
    private void OnSpeedLightTarget(UltimaBatcher2D batcher,Texture2D lightTarget,Vector4 bounds,Vector3 ambient)
        =>BindSpeedLightTarget(batcher,lightTarget,bounds,ambient);
    private void OnSpeedLightClear(UltimaBatcher2D batcher)=>ClearSpeedLightTarget(batcher);

    // PORT DEVIATION (GUO): ADR-0048. Match the camera's existing walking and
    // peek offsets, then select the integer tile containing its centre.
    // Snapshot once before capture; every consumer gets that same origin.
    private Vector2I SpeedModeCameraOrigin()
    {
        var player=_world.Player;
        var peek=Camera.Offset;
        return Renderer.SpeedMode.SpeedModeBindings.CameraOrigin(player.X,player.Y,
            player.Offset.X,player.Offset.Y,peek.X,peek.Y,Camera.Zoom);
    }

    // PORT DEVIATION (GUO): ADR-0048. Offers are collected by the existing
    // AddLight path; only reach maps are evaluated here, never the full field.
    private void DrawSpeedLights(UltimaBatcher2D batcher,RenderTargets targets,ref Transform2D matrix)
    {
        if(ReferenceEquals(targets.LightOverride,_speedLightOverride)) targets.LightOverride=null;
        targets.SpeedLightBlend=null;
        if(!UseSpeedLights || !batcher.CanQueueSpeedLight) { if(!UseRealtimeLights) _rtSourceCount=0; return; }
        try
        {
            _rtField ??=new RealtimeLightField();
            long gridStart=System.Diagnostics.Stopwatch.GetTimestamp();
            bool gridChanged=BuildSpeedGrid();
            long reachStart=System.Diagnostics.Stopwatch.GetTimestamp();
            SpeedLightFrame.GridTicks+=reachStart-gridStart;
            _rtField.BeginSpeedReach(gridChanged);
            _rtSources ??=new RealtimeLightField.Source[RtSourceCapacity];
            _rtSourceCount+=RealtimeLightEffects.Collect(Time.Ticks,_rtSources,_rtSourceCount,
                SpellLighting.Enabled(ProfileManager.CurrentProfile));
            _speedLights ??=new SpeedLightFrame { BindLightTarget=OnSpeedLightTarget,ClearLightTarget=OnSpeedLightClear };
            var frame=_speedLights;
            frame.Batcher=batcher;
            frame.LampShadowAtlas=frame.SunMask=default; frame.RenderInputs=null; frame.RenderPatches=null;
            frame.Count=Math.Min(_rtSourceCount,frame.Lights.Length);
            frame.Origin=batcher.SpeedSurfaceOrigin;
            frame.Soften=Math.Clamp(ProfileManager.CurrentProfile.SpeedModeSoften,0,10);
            float level=RealtimeAmbientLevel();
            float floor=Math.Clamp(ProfileManager.CurrentProfile.SpeedModeNightFloor,0,100)*0.01f;
            var ambient=RealtimeLightField.AmbientFor(Math.Max(floor,level));
            frame.Ambient=new Vector3(ambient.R,ambient.G,ambient.B);
            frame.RoofShare=RealtimeLightField.RoofDaylight+(1-RealtimeLightField.RoofDaylight)*(1-Math.Clamp(level,0,1));
            for(int i=0;i<frame.Count;i++)
            {
                var source=_rtSources[i];
                var reach=_rtField.GetSpeedReach(in source);
                var centre=_rtField.LightOrigin(source.X,source.Y);
                // The same wall standoff as ComputeReach, plus walking offsets.
                source=new RealtimeLightField.Source(source.X,source.Y,source.Z,source.Radius,source.Color,
                    source.OffsetX+centre.X-source.X-0.5f,source.OffsetY+centre.Y-source.Y-0.5f,
                    source.Curve,source.Energy,source.CustomCurve);
                float x=source.X+source.OffsetX+0.5f,y=source.Y+source.OffsetY+0.5f;
                var pixel=matrix*new Vector2((x-y)*22-_offset.X,(x+y)*22-(source.Z+10)*4-_offset.Y);
                float flicker=0.975f+0.025f*MathF.Sin(Time.Ticks*0.006f+source.X*7.13f+source.Y*3.17f);
                frame.Lights[i]=new SpeedLightFrame.Entry { Source=source,Reach=reach,PixelCentre=pixel,
                    PixelRadius=1.6f*source.Radius*22*matrix.X.Length(),Flicker=flicker,ShadowRow=-1 };
            }
            PrepareSpeedLightInputs(frame);
            PrepareSpeedShadowInputs(frame,batcher,matrix);
            SpeedLightFrame.ReachTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-reachStart;
            _speedLightOverride=batcher.QueueSpeedLight(frame);
            if(_speedLightOverride!=null) { targets.LightOverride=_speedLightOverride; targets.SpeedLightBlend=_darknessBlend; }
        }
        catch(Exception error) { GD.PrintErr("[lighting-speed] classic lighting this frame: "+error.Message); }
        finally { _rtSourceCount=0; }
    }
}
