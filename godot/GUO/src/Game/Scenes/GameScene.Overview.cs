// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): owner-approved wide-zoom overview, ADR-0045.
using Godot;
using GUO.Game.GameObjects;
using GUO.Renderer;

namespace GUO.Game.Scenes;
internal partial class GameScene
{
    private WorldOverview _overview;
    private bool _wasOverview;
    internal bool OverviewActive => DepthWorldProbe.ZoomMax > 5f && WorldOverview.Enabled(Camera.Zoom);
    internal WorldOverview Overview => _overview;

    private void DrawOverview(UltimaBatcher2D batcher, RenderTargets targets)
    {
        if (!_wasOverview)
        {
            GpuWorld.Destroy();
            _renderLists.Clear(); _visibleChunks.Clear(); _meshChunks.Clear();
            _isSelectionActive = false;
            _world.NameOverHeadManager.Close(); _useObjectHandles = false;
            _wasOverview = true;
        }
        SelectedObject.Object = null;
        _lightCount = 0;
        GetViewPort(); // Camera/follow math only; never load detailed terrain here.
        targets.LightOverride = null;
        targets.SetLightsConfiguration(null, null);
        batcher.SetRenderTarget(targets.LightRenderTarget);
        batcher.Clear(Colors.Transparent);
        batcher.SetRenderTarget(targets.WorldRenderTarget);
        batcher.Clear(new Godot.Color(.025f, .03f, .035f, 1));
        (_overview ??= new WorldOverview()).Draw(_world, Camera, _offset, batcher);
        RenderedObjectsCount = _overview.Cache.Visible.Count;
        batcher.SetRenderTarget(null);
    }

    private void LeaveOverview()
    {
        if (!_wasOverview) return;
        _wasOverview = false;
        UpdateDrawPosition = true;
        UpdateMaxDrawZ(true);
        // Keep compact tiles warm for rapid return; ordinary gather resumes intact.
    }
}
