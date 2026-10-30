// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): opt-in above-cloud view, task1026 / ADR-0045.
using Godot;
using GUO.Configuration;
using GUO.Renderer;
using GUO.Renderer.SpeedMode.Weather;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    private SpeedModeCloudPass _speedCloudPass;
    private RenderTarget2D _speedCloudTarget;
    private Rid _speedCloudItem;

    // The lead calls native while a world batch is active, overview after its
    // batch ends. Native target transform is cancelled; overview uses identity.
    partial void DrawSpeedClouds(UltimaBatcher2D batcher, bool overview)
    {
        var profile=ProfileManager.CurrentProfile;
        var target=batcher.SpeedWeatherTarget;
        if (profile?.ExperimentalWorldOverview!=true || !profile.LightingSpeedMode
            || !_world.InGame || _world.Player==null || !float.IsFinite(Camera.Zoom) || Camera.Zoom<=4
            || target==null || target.IsDisposed)
        {
            ReleaseSpeedClouds();
            return;
        }
        if (_speedCloudTarget!=target) ReleaseSpeedClouds();
        if (_speedCloudPass==null)
        {
            _speedCloudTarget=target;
            _speedCloudPass=new SpeedModeCloudPass();
            target.ViewportNode.AddChild(_speedCloudPass);
            _speedCloudItem=RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(_speedCloudItem,target.CanvasItem);
            RenderingServer.CanvasItemSetZIndex(_speedCloudItem,103);
            RenderingServer.CanvasItemSetDefaultTextureFilter(_speedCloudItem,RenderingServer.CanvasItemTextureFilter.Nearest);
        }
        // Agreed smoothstep altitude contract: 0at4, .5at4.5, 1at5+.
        float altitude=WorldOverview.CloudAltitudeBlend(Camera.Zoom)*300;
        var player=_world.Player;
        var origin=SpeedModeCameraOrigin();
        var size=new Vector2I(target.Width,target.Height);
        _speedCloudPass.Bind(size,new Vector3(origin.X,origin.Y,player.Z),
            new SpeedModeCloudView(true,Camera.Zoom,altitude),
            GUO.Renderer.Water.WaterRender.Wind(_world,profile),Time.Ticks/1000.0);
        if (!_speedCloudPass.Visible || _speedCloudPass.Material==null) { ResetSpeedClouds(); return; }
        if (overview && _world.Map!=null) {
            var maps=Client.Game.UO.FileManager.Maps;
            int map=_world.Map.Index;
            var transform=WorldOverview.MapTransform(Camera,_offset,player.Z,
                maps.MapBlocksSize[map,0]*8,maps.MapBlocksSize[map,1]*8);
            // The overview pin head is 32 pixels above its map ground anchor.
            _speedCloudPass.ProtectPin(transform*new Vector2(player.X+.5f,player.Y+.5f)+new Vector2(0,-32),true);
        } else _speedCloudPass.ProtectPin(new Vector2(-1000,-1000),false);
        // WorldOverview.Draw ends an identity-view batch; no Begin/End here
        // changes its labels, picking or the lead's active native batch.
        RenderingServer.CanvasItemSetTransform(_speedCloudItem,
            overview ? Transform2D.Identity : batcher.SpeedWeatherInverseView);
        RenderingServer.CanvasItemClear(_speedCloudItem);
        RenderingServer.CanvasItemSetMaterial(_speedCloudItem,_speedCloudPass.Material.GetRid());
        RenderingServer.CanvasItemSetVisible(_speedCloudItem,true);
        RenderingServer.CanvasItemAddRect(_speedCloudItem,new Rect2(Vector2.Zero,size),Colors.White);
        _speedCloudPass.Visible=false;
    }

    partial void ResetSpeedClouds()
    {
        if (_speedCloudItem.IsValid) {
            RenderingServer.CanvasItemClear(_speedCloudItem);
            RenderingServer.CanvasItemSetVisible(_speedCloudItem,false);
        }
        if (_speedCloudPass!=null && GodotObject.IsInstanceValid(_speedCloudPass)) _speedCloudPass.HideForFallback();
        var profile=ProfileManager.CurrentProfile;
        if (profile?.ExperimentalWorldOverview!=true || !profile.LightingSpeedMode) ReleaseSpeedClouds();
    }

    private void ReleaseSpeedClouds()
    {
        if (_speedCloudItem.IsValid) RenderingServer.FreeRid(_speedCloudItem);
        _speedCloudItem=default;
        if (_speedCloudPass!=null && GodotObject.IsInstanceValid(_speedCloudPass)) {
            _speedCloudPass.GetParent()?.RemoveChild(_speedCloudPass);
            _speedCloudPass.Free();
        }
        _speedCloudPass=null; _speedCloudTarget=null;
    }
}
