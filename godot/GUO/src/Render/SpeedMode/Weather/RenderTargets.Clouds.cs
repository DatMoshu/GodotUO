// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): task1057 frame-scoped foreground cloud publication.
using Godot;
using GUO.Renderer.SpeedMode.Weather;

namespace GUO.Renderer;

public partial class RenderTargets
{
    private readonly CloudPublicationState _cloudPublication = new();
    private UltimaBatcher2D _cloudBatcher;
    private object _cloudOwner;
    internal long CloudFrame => _cloudPublication.Frame;

    // All methods are main-thread only. Targets remain borrowed, never disposed here.
    internal void ClearCloudFrame()
    {
        _cloudPublication.Clear();
        _cloudOwner = null;
        // Must complete before target resize/free or batcher pool reuse.
        _cloudBatcher?.RetireCloudOverlayDraws(null, null, 0);
    }

    internal void BeginCloudFrame(object sceneOwner, UltimaBatcher2D batcher)
    {
        ClearCloudFrame();
        _cloudBatcher = batcher; _cloudOwner = sceneOwner;
        var world = WorldRenderTarget;
        _cloudPublication.Begin(sceneOwner, world, world?.Width ?? 0, world?.Height ?? 0);
    }

    internal bool PublishCloud(object owner, long frame, RenderTarget2D world, RenderTarget2D overlay, long generation)
    {
        if (world == null || world.IsDisposed || overlay == null || overlay.IsDisposed ||
            overlay.Width != world.Width || overlay.Height != world.Height)
        {
            _cloudPublication.Reject(owner, frame); return false;
        }
        return _cloudPublication.Publish(new CloudPublicationState.Ticket(owner, frame,
            world, world.Width, world.Height, overlay, generation));
    }

    internal void RevokeCloud(object owner, RenderTarget2D overlay, long generation)
    {
        _cloudPublication.Revoke(owner, overlay, generation);
        _cloudBatcher?.RetireCloudOverlayDraws(owner, overlay, generation);
    }

    private void DrawCloud(UltimaBatcher2D batcher)
    {
        var world = WorldRenderTarget;
        if (world == null || world.IsDisposed ||
            !_cloudPublication.Take(_cloudOwner, world, world.Width, world.Height, out var ticket)) return;
        var overlay = ticket.Overlay as RenderTarget2D;
        if (overlay == null || overlay.IsDisposed || overlay.Width != world.Width || overlay.Height != world.Height) return;
        batcher.DrawCloudOverlay(ticket.Owner, overlay, ticket.Generation, _gameWorldSceneOnScreen);
    }
}
