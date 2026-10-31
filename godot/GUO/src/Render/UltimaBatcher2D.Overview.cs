// SPDX-License-Identifier: BSD-2-Clause
using Godot;
namespace GUO.Renderer;
public partial class UltimaBatcher2D
{
    // ADR-0045: bounded overview-only texture draw. Labels use the normal batcher.
    internal void DrawOverviewTexture(Texture2D texture, Rect2 destination, ShaderMaterial material)
    {
        EnsureStarted();
        FlushRun();
        Rid item = NewItem(CurrentParent, Transform2D.Identity);
        _canvasCommands?.Bypass();
        ItemSetMaterial(item, material);
        RenderingServer.CanvasItemAddTextureRect(item, destination, texture.GetRid());
        Commands++;
        Cut();
    }
}
