// SPDX-License-Identifier: BSD-2-Clause
using Godot;
using GUO.Compat;
using GUO.Renderer.SpeedMode;

namespace GUO.Renderer;

public sealed partial class UltimaBatcher2D
{
    private Rid _speedClassicCompose,_speedCompose;
#if DEBUG
    internal static bool ProbeLegacySpeedCompose;
#endif
    internal bool CanQueueSpeedLight=>_orderedWorld?.CanAttachSpeedLight==true;
    internal Vector2I SpeedSurfaceOrigin=>_orderedWorld.SurfaceOrigin;
    internal Texture2D QueueSpeedLight(SpeedLightFrame frame)
    {
        if(!CanQueueSpeedLight) return null;
        _orderedWorld.SpeedLights=frame; return _orderedWorld.SpeedLightTexture;
    }
    internal bool TryDrawSpeedLight(Texture2D classic,Texture2D light,Rectangle destination,Vector3 classicHue,BlendState speedBlend)
    {
        if(!SpeedModeDebug.Active || _orderedWorld?.SpeedLights==null || !CanQueueSpeedLight || light==null) return false;
        FlushRun();
        int depth=_clipStack.Count;
        _speedClassicCompose=NewItem(CurrentParent,Transform2D.Identity);
        _clipStack.Add(_speedClassicCompose); Cut();
        Draw(classic,destination,classicHue,0f); FlushRun();
        while(_clipStack.Count>depth) _clipStack.RemoveAt(_clipStack.Count-1);
        Cut();
        _speedCompose=NewItem(CurrentParent,Transform2D.Identity);
        RenderingServer.CanvasItemSetVisible(_speedCompose,false);
        _clipStack.Add(_speedCompose); Cut(); SetBlendState(speedBlend);
#if DEBUG
        if(ProbeLegacySpeedCompose) { Draw(light,destination,new Vector3(0,0,1),0f); FlushRun(); }
        else
#endif
        DrawSpeedLightTexture(light,destination);
        while(_clipStack.Count>depth) _clipStack.RemoveAt(_clipStack.Count-1);
        Cut();
        _orderedWorld.SpeedClassicCompose=_speedClassicCompose;
        _orderedWorld.SpeedCompose=_speedCompose;
        return true;
    }
    private void DrawSpeedLightTexture(Texture2D light,Rectangle destination)
    {
        // The RD backing may be recreated later in this frame. A source rect
        // measured here would freeze its currently empty dimensions at 0x0.
        // Resolve the whole texture at draw time, as the ordered presenter does.
        EnsureStarted();
        if(_orderedWorld?.Deferring!=true) { SelectSpriteSampler(light); EnsureSpriteMaterial(light); }
        FlushRun();
        _canvasCommands?.Bypass();
        var texture=light.GetRid();
        RenderingServer.CanvasItemAddTextureRect(_current,
            new Rect2(destination.X,destination.Y,destination.Width,destination.Height),
            texture,false,Encode(new Vector3(0,0,1)));
        Commands++; Count(0,texture);
    }
    private void ResetSpeedComposeItems()
    {
        if(_speedClassicCompose.IsValid) RenderingServer.CanvasItemSetVisible(_speedClassicCompose,true);
        if(_speedCompose.IsValid) RenderingServer.CanvasItemSetVisible(_speedCompose,true);
        _speedClassicCompose=_speedCompose=default;
    }
}
