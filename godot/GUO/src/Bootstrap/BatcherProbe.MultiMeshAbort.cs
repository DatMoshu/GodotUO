// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Host;

using System;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer;

public partial class BatcherProbe
{
    private async Task VerifyMultiMeshAbort(UltimaBatcher2D batcher, SubViewport viewport)
    {
        bool oldMm = UltimaBatcher2D.MultiMeshSprites, oldRetain = UltimaBatcher2D.RetainCanvas;
        bool oldBatched = UltimaBatcher2D.BatchedWorld, oldGpu = GpuWorld.Enabled;
        bool oldClear = UltimaBatcher2D.ClearUsedCanvas;
        int oldMin = UltimaBatcher2D.MultiMeshMinRun;
        Vector2I oldSize = viewport.Size;
        using var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        using var texture = ImageTexture.CreateFromImage(image);
        Vector3 hue = ShaderHueTranslator.GetHueVector(0);
        async Task<byte[]> Pixels()
        {
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using var pixels = viewport.GetTexture().GetImage();
            return pixels.GetData();
        }
        void Sentinel()
        {
            batcher.Begin();
            batcher.Draw(texture, new Compat.Rectangle(40, 40, 3, 3), null, hue, 0);
            batcher.End();
        }
        try
        {
            viewport.Size = new Vector2I(64, 64);
            UltimaBatcher2D.MultiMeshSprites = true;
            UltimaBatcher2D.MultiMeshMinRun = 8;
            UltimaBatcher2D.RetainCanvas = true;
            UltimaBatcher2D.BatchedWorld = false;
            GpuWorld.Enabled = false;
            batcher.BeginFrame(true); batcher.Begin(); batcher.End(); batcher.EndFrame();
            byte[] empty = await Pixels();
            batcher.BeginFrame(true); Sentinel(); batcher.EndFrame();
            byte[] sentinel = await Pixels();
            CheckThat("MM abort fixture is visible", !empty.AsSpan().SequenceEqual(sentinel), "sentinel differs from empty");
            foreach (bool prefix in new[] { false, true })
            foreach (int count in new[] { 3, 12 })
            {
                UltimaBatcher2D.ClearUsedCanvas = prefix;
                batcher.BeginFrame(true); batcher.Begin();
                for (int i = 0; i < count; i++)
                    batcher.Draw(texture, new Compat.Rectangle(2 + i * 2, 2, 2, 2), null, hue, 0);
                // Deliberately abort before End can flush either a short replay
                // or a long instanced run. The next frame contains no sprites.
                batcher.EndFrame(completed: false);
                byte[] abortedPixels = await Pixels();
                CheckThat($"MM abort empties frame {prefix}/{count}", empty.AsSpan().SequenceEqual(abortedPixels), "abandoned commands invisible");
                batcher.BeginFrame(true); batcher.Begin(); batcher.End(); batcher.EndFrame();
                long submitted = UltimaBatcher2D.MultiMeshInstances + UltimaBatcher2D.MultiMeshReplayed;
                CheckThat($"MM abort discards pending run {prefix}/{count}", submitted == 0,
                    $"next empty frame resubmitted {submitted} aborted sprites");
                byte[] emptyPixels = await Pixels();
                CheckThat($"MM abort next empty pixels {prefix}/{count}", empty.AsSpan().SequenceEqual(emptyPixels), "no resurrected short/long run");
                batcher.BeginFrame(true); Sentinel(); batcher.EndFrame();
                byte[] nextPixels = await Pixels();
                CheckThat($"MM abort next draw recovers {prefix}/{count}", sentinel.AsSpan().SequenceEqual(nextPixels), "fresh sentinel only");
            }
        }
        finally
        {
            batcher.EndFrame(completed: false);
            UltimaBatcher2D.RetainCanvas = false;
            UltimaBatcher2D.ClearUsedCanvas = false;
            batcher.BeginFrame();
            UltimaBatcher2D.MultiMeshSprites = oldMm;
            UltimaBatcher2D.MultiMeshMinRun = oldMin;
            UltimaBatcher2D.RetainCanvas = oldRetain;
            UltimaBatcher2D.BatchedWorld = oldBatched;
            UltimaBatcher2D.ClearUsedCanvas = oldClear;
            GpuWorld.Enabled = oldGpu;
            viewport.Size = oldSize;
        }
    }
}
