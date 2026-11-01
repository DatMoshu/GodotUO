// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): task1057 tracked final cloud texture borrow.
using Godot;
using GUO.Compat;

namespace GUO.Renderer;

public sealed partial class UltimaBatcher2D
{
    private object _cloudDrawOwner;
    private RenderTarget2D _cloudDrawTarget;
    private long _cloudDrawGeneration;
    private int _cloudFirstItem, _cloudEndItem;

    internal void DrawCloudOverlay(object owner, RenderTarget2D overlay, long generation, Rectangle destination)
    {
        EnsureStarted();
        FlushRun();
        FinishCanvasCommands();
        RetireCloudOverlayDraws(null, null, 0);
        var sampler = _sampler;
        _cloudDrawOwner = owner; _cloudDrawTarget = overlay; _cloudDrawGeneration = generation;
        _cloudFirstItem = _itemCount;
        // Cut establishes a dedicated pooled scope. No cloud texture command
        // can be appended to the preceding world/light draw's canvas item.
        try
        {
            SetBlendState(BlendState.AlphaBlend);
            SetSampler(SamplerState.PointClamp);
            Cut();
            Draw(overlay.Texture, destination, new Vector3(0, 0, 1), 0f);
            FlushRun();
            FinishCanvasCommands();
        }
        finally
        {
            _cloudEndItem = _itemCount;
            SetBlendState(null); // RenderTargets enters/exits this seam at default AlphaBlend.
            SetSampler(sampler);
            // UI commands must not enter the tracked cloud scope.
            Cut();
        }
    }

    internal void RetireCloudOverlayDraws(object owner, RenderTarget2D overlay, long generation)
    {
        if (_cloudDrawOwner == null || (owner != null &&
            (!ReferenceEquals(owner, _cloudDrawOwner) || !ReferenceEquals(overlay, _cloudDrawTarget) ||
             generation != _cloudDrawGeneration))) return;
        // BeginFrame calls this BEFORE pooled items may change roles. Commands
        // hold texture RID borrows, so reset both retained replay and native lists.
        // If native cleanup throws, keep the borrow and target alive for retry.
        for (int i = _cloudFirstItem; i < _cloudEndItem; i++)
        {
            RenderingServer.CanvasItemClear(_items[i]);
            _itemStates[i].Commands?.Reset();
        }
        if (ReferenceEquals(_sizedTexture, _cloudDrawTarget.Texture)) _sizedTexture = null;
        _cloudDrawOwner = null; _cloudDrawTarget = null; _cloudDrawGeneration = 0;
        _cloudFirstItem = _cloudEndItem = 0;
    }
}
