// SPDX-License-Identifier: BSD-2-Clause
using Godot;

namespace GUO.Renderer;

public sealed partial class UltimaBatcher2D
{
    // Diagnostic opt-in during the parity/performance experiment. The RD Labs
    // path remains independent and disables this ordinary-canvas optimization.
    internal static bool RetainCanvas = System.Environment.GetEnvironmentVariable("GUO_RETAIN_CANVAS") == "1";
    // Main-thread scalar diagnostics; distinguish ownership clears from retained rewrites.
    internal static long CanvasOwnerClears, CanvasHides, CanvasReveals;
    private bool _retainCanvasFrame;
    private RetainedCanvasCommands.Budget _canvasBudget;
    private RetainedCanvasCommands _canvasCommands;

    private void FinishCanvasCommands()
    {
        _canvasCommands?.End();
        _canvasCommands = null;
    }

    private void StartCanvasCommands(int index, Rid item)
    {
        ref ItemState state = ref _itemStates[index];
        // RD clears only its previous active prefix. An older retained tail
        // can still own hidden commands, so remove those before raw reuse.
        if (!_retainCanvasFrame && state.Commands?.HasCommands == true)
        {
            RenderingServer.CanvasItemClear(item);
            CanvasOwnerClears++;
            state.Commands.Reset();
        }
        if (state.RetainedHidden)
        {
            RenderingServer.CanvasItemSetVisible(item, true);
            CanvasReveals++;
            state.RetainedHidden = false;
        }
        if (!_retainCanvasFrame) return;
        _canvasBudget ??= new RetainedCanvasCommands.Budget();
        _canvasCommands = state.Commands ??= new RetainedCanvasCommands(_canvasBudget, RetainedCanvasCommands.Native, item);
        _canvasCommands.Begin();
    }

    private void CanvasRect(Rid item, Rect2 destination, Rid texture, Rect2 source, Color color)
    {
        if (_canvasCommands == null)
            RenderingServer.CanvasItemAddTextureRectRegion(item, destination, texture, source, color, false, false);
        else
            _canvasCommands.Add(new RetainedCanvasCommands.Command(RetainedCanvasCommands.Kind.Rect,
                texture, default, destination, source, color, default));
    }

    private void CanvasMesh(Rid item, Rid mesh, Transform2D transform, Color color, Rid texture)
    {
        if (_canvasCommands == null)
            RenderingServer.CanvasItemAddMesh(item, mesh, transform, color, texture);
        else
            _canvasCommands.Add(new RetainedCanvasCommands.Command(RetainedCanvasCommands.Kind.Mesh,
                texture, mesh, default, default, color, transform));
    }

    private void CanvasAffine(Rect2 region, Rid texture, Color color, Transform2D transform)
    {
        if (_canvasCommands == null)
        {
            RenderingServer.CanvasItemAddSetTransform(_current, transform);
            RenderingServer.CanvasItemAddTextureRectRegion(_current, region, texture, region, color, false, false);
            RenderingServer.CanvasItemAddSetTransform(_current, Transform2D.Identity);
        }
        else
            _canvasCommands.Add(new RetainedCanvasCommands.Command(RetainedCanvasCommands.Kind.Affine,
                texture, default, region, region, color, transform));
    }
}
