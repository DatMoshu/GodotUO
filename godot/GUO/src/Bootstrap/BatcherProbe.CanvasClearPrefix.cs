// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Host;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer;

public partial class BatcherProbe
{
    private async Task VerifyCanvasClearPrefix(UltimaBatcher2D batcher, SubViewport viewport)
    {
        bool oldClear = UltimaBatcher2D.ClearUsedCanvas;
        bool oldRetain = UltimaBatcher2D.RetainCanvas;
        bool oldGpu = GpuWorld.Enabled;
        Vector2I oldSize = viewport.Size;
        using var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        using var texture = ImageTexture.CreateFromImage(image);
        var reference = new List<byte[]>();
        var referenceClears = new List<long>();
        int[] counts = { 12, 2, 2, 0, 0, 7, 1, 1, 0, 0, 12 };
        bool[] retained = { false, false, false, false, false, true, true, false, false, false, false };
        long avoided = 0;
        long fullClears = 0, prefixClears = 0;
        try
        {
            viewport.Size = new Vector2I(64, 64);
            GpuWorld.Enabled = false;
            foreach (bool enabled in new[] { false, true })
            {
                UltimaBatcher2D.ClearUsedCanvas = false;
                UltimaBatcher2D.RetainCanvas = false;
                batcher.BeginFrame(); // empty reference state, independent of pool capacity
                UltimaBatcher2D.ClearUsedCanvas = enabled;
                for (int step = 0; step < counts.Length; step++)
                {
                    UltimaBatcher2D.RetainCanvas = retained[step];
                    long before = UltimaBatcher2D.CanvasOwnerClears;
                    batcher.BeginFrame();
                    long clears = UltimaBatcher2D.CanvasOwnerClears - before;
                    if (!enabled) referenceClears.Add(clears);
                    if (enabled && !retained[step])
                    {
                        bool transition = step > 0 && retained[step - 1];
                        int expected = transition ? UltimaBatcher2D.LastItemsAllocated : UltimaBatcher2D.LastFrame.Items;
                        CheckThat($"canvas clear prefix count {step}", clears == expected,
                            $"cleared {clears}, expected {expected}; retained transition clears full pool");
                        if (!transition)
                        {
                            fullClears += referenceClears[step];
                            prefixClears += clears;
                            avoided += referenceClears[step] - clears;
                        }
                    }
                    for (int i = 0; i < counts[step]; i++)
                    {
                        batcher.Begin();
                        batcher.Draw(texture, new Compat.Rectangle(i * 3, 1, 2, 2), null,
                            ShaderHueTranslator.GetHueVector(0), 0);
                        batcher.End();
                    }
                    await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                    using var pixels = viewport.GetTexture().GetImage();
                    byte[] actual = pixels.GetData();
                    if (!enabled) reference.Add(actual);
                    else CheckThat($"canvas clear prefix pixels {step}", reference[step].AsSpan().SequenceEqual(actual),
                        "grow/shrink/empty/reuse and retained transitions match full-clear reference");
                }
            }
            CheckThat("canvas clear prefix avoids native clears", avoided > 0, $"avoided {avoided} empty-tail calls");
            GD.Print($"[batcher-probe] ordinary clear calls full={fullClears} prefix={prefixClears} avoided={avoided}");
            CheckThat("canvas clear fixture visibly changes", !reference[0].AsSpan().SequenceEqual(reference[3]),
                "drawn frame differs from empty frame");
            // Existing mutation, abort/recovery and deferred visibility oracles
            // also run with the candidate on; retained paths must stay intact.
            await VerifyRetainedCanvas(batcher, viewport);
            await VerifyDeferredVisibility(batcher, viewport);
        }
        finally
        {
            UltimaBatcher2D.ClearUsedCanvas = false;
            UltimaBatcher2D.RetainCanvas = false;
            batcher.BeginFrame();
            UltimaBatcher2D.ClearUsedCanvas = oldClear;
            UltimaBatcher2D.RetainCanvas = oldRetain;
            GpuWorld.Enabled = oldGpu;
            viewport.Size = oldSize;
        }
    }
}
