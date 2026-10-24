// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;

namespace GUO.Renderer;

public sealed partial class UltimaBatcher2D
{
    // PORT DEVIATION (GUO): main-thread frame transaction for the existing
    // RetainCanvas experiment. Default BeginFrame callers remain eager.
    private bool _deferredCanvasFrameOpen, _canvasRecoveryPending;
    internal static long CanvasFrameAborts, CanvasRecoveryFailures;

    /// <summary>Completes main-frame retention, or abandons a failed draw.
    /// Abort is best effort and never throws over the original exception;
    /// a native cleanup failure prevents the next BeginFrame from drawing
    /// until cleanup succeeds. Repeated finalization is harmless.</summary>
    public void EndFrame(bool completed = true)
    {
        if (!_deferredCanvasFrameOpen) return;
        if (!completed)
        {
            RecoverDeferredCanvasFrame(throwOnFailure: false);
            return;
        }
        try
        {
            if (_started)
                throw new InvalidOperationException("UltimaBatcher2D: End() must precede EndFrame().");
            FinishCanvasCommands();
            // NewItem's indices are monotonic across batches and targets.
            // Used clip/transform ancestors therefore precede used children.
            for (int i = _itemCount; i < _items.Count; i++)
            {
                ref ItemState state = ref _itemStates[i];
                if (state.Commands?.HasCommands != true || state.RetainedHidden) continue;
                RenderingServer.CanvasItemSetVisible(_items[i], false);
                CanvasHides++;
                state.RetainedHidden = true;
            }
            _deferredCanvasFrameOpen = false;
        }
        catch
        {
            RecoverDeferredCanvasFrame(throwOnFailure: false);
            throw;
        }
    }

    private void RecoverDeferredCanvasFrame(bool throwOnFailure)
    {
        if (_deferredCanvasFrameOpen) CanvasFrameAborts++;
        _deferredCanvasFrameOpen = false;
        _canvasRecoveryPending = true;

        // Do not End/Flush: a matching prefix can still have an old native
        // suffix, and queued raw/cover runs do not constitute a complete frame.
        _canvasCommands = null;
        _runPoints.Clear(); _runUVs.Clear(); _runColors.Clear(); _runIndices.Clear();
        _runTexture = null; _runItem = default;
        _cover.Clear();
        _capture = null; _captureSuspended = false;
        _clipStack.Clear();
        _started = false;
        _current = default; _lastItem = -1;
        _worldOffset = _itemOffset = Vector2.Zero;
        _sizedTexture = null;
        _target = _parent; _currentTarget = null;
        _mirroring = false;

        bool recovered = true;
        for (int i = 0; i < _items.Count; i++)
        {
            ref ItemState state = ref _itemStates[i];
            // Clear ALL owners, including raw/overflowed streams and unused
            // ancestors; never infer native emptiness from HasCommands alone.
            try { RenderingServer.CanvasItemClear(_items[i]); CanvasOwnerClears++; }
            catch { recovered = false; }
            state.Commands?.Reset();
            // The copy flag is native state separate from canvas commands.
            if (state.CopyToBackbuffer)
            {
                try
                {
                    RenderingServer.CanvasItemSetCopyToBackbuffer(_items[i], false, new Rect2());
                    state.CopyToBackbuffer = false;
                }
                catch { recovered = false; }
            }
            if (!state.RetainedHidden)
            {
                try
                {
                    RenderingServer.CanvasItemSetVisible(_items[i], false);
                    CanvasHides++;
                    state.RetainedHidden = true;
                }
                catch { recovered = false; }
            }
        }
        // The mirror is owned separately from the pool; do not leave a
        // partially drawn id image behind an aborted ordinary canvas frame.
        if (_idItem.IsValid)
        {
            try { RenderingServer.CanvasItemClear(_idItem); }
            catch { recovered = false; }
        }
        _canvasRecoveryPending = !recovered;
        if (!recovered)
        {
            CanvasRecoveryFailures++;
            if (throwOnFailure)
                throw new InvalidOperationException("Deferred canvas abort cleanup failed; frame was not opened.");
        }
    }
}
