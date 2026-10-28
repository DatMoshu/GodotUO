// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Host;

using System;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer;

public partial class BatcherProbe
{
    // An exception in the main DrawFrame on the default (non-retained) path:
    // the original error reaches the caller, nothing of the failed frame is
    // shown, and the next frame draws exactly as if the failed one never ran.
    private async Task VerifyFrameAbort(UltimaBatcher2D batcher, SubViewport viewport)
    {
        bool oldRetain = UltimaBatcher2D.RetainCanvas;
#if GUO_LABS
        bool oldRd = GUO.Renderer.DepthWorld.OrderedWorldCapture.Enabled;
        GUO.Renderer.DepthWorld.OrderedWorldCapture.Enabled = false;
#endif
        UltimaBatcher2D.RetainCanvas = false;
        using var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        using var texture = ImageTexture.CreateFromImage(image);
        using var target = new RenderTarget2D(this, 8, 8);
        Vector3 hue = ShaderHueTranslator.GetHueVector(0);

        // The shape of DrawFrame: an offscreen target, then a composite batch
        // on the screen that samples it.
        void DrawWhole()
        {
            batcher.SetRenderTarget(target);
            batcher.Clear(Colors.Transparent);
            batcher.Begin();
            batcher.Draw(texture, new Compat.Rectangle(0, 0, 3, 3), null, hue, 0);
            batcher.End();
            batcher.SetRenderTarget(null);
            batcher.Begin();
            batcher.Draw(texture, new Compat.Rectangle(5, 5, 2, 2), null, hue, 0);
            batcher.Draw(target.Texture, new Compat.Rectangle(0, 0, 8, 8), null, hue, 0);
            batcher.End();
        }

        async Task<byte[]> Pixels()
        {
            // The offscreen target and the composite that samples it.
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using Image pixels = viewport.GetTexture().GetImage();
            return pixels.GetData();
        }

        // DrawFrame's own try/finally.
        void Frame(Action draw)
        {
            bool complete = false;
            try
            {
                batcher.BeginFrame(deferRetainedHiding: true);
                draw();
                complete = true;
            }
            finally { batcher.EndFrame(complete); }
        }

        try
        {
            Frame(() => { });
            byte[] empty = await Pixels();
            Frame(DrawWhole);
            byte[] whole = await Pixels();
            CheckThat("frame abort fixture draws", !empty.AsSpan().SequenceEqual(whole), "whole frame differs from empty");

            long aborts = UltimaBatcher2D.CanvasFrameAborts, hides = UltimaBatcher2D.CanvasHides;
            Frame(DrawWhole);
            batcher.EndFrame(completed: false);
            byte[] again = await Pixels();
            CheckThat("completed main frame is not aborted", UltimaBatcher2D.CanvasFrameAborts == aborts
                && UltimaBatcher2D.CanvasHides == hides && again.AsSpan().SequenceEqual(whole),
                "normal path: no abort, no hides, same pixels; a late EndFrame(false) is a no-op");

            string[] points = { "after BeginFrame", "inside a batch", "inside a clip on an offscreen target",
                "in End after its flush", "between batches" };
            for (int fault = 0; fault < points.Length; fault++)
            {
                var sentinel = new InvalidOperationException("frame abort fixture");
                Exception observed = null;
                aborts = UltimaBatcher2D.CanvasFrameAborts;
                try
                {
                    Frame(() =>
                    {
                        if (fault == 1)
                        {
                            batcher.Begin();
                            batcher.Draw(texture, new Compat.Rectangle(0, 0, 3, 3), null, hue, 0);
                        }
                        else if (fault == 2)
                        {
                            batcher.SetRenderTarget(target);
                            batcher.Begin();
                            batcher.PushUiTransform(new Transform2D(0f, new Vector2(1, 1)));
                            batcher.ClipBegin(0, 0, 2, 2);
                            batcher.Draw(texture, new Compat.Rectangle(0, 0, 6, 6), null, hue, 0);
                        }
                        else if (fault == 3)
                        {
                            batcher.Begin();
                            batcher.Draw(texture, new Compat.Rectangle(0, 0, 3, 3), null, hue, 0);
                            UltimaBatcher2D.InjectEndFailure = true;
                            batcher.End();
                        }
                        else if (fault == 4)
                        {
                            batcher.Begin();
                            batcher.Draw(texture, new Compat.Rectangle(0, 0, 3, 3), null, hue, 0);
                            batcher.End();
                        }
                        throw sentinel;
                    });
                }
                catch (Exception ex) { observed = ex; }
                finally { UltimaBatcher2D.InjectEndFailure = false; }
                bool original = fault == 3
                    ? observed is InvalidOperationException { Message: "injected End failure" }
                    : ReferenceEquals(observed, sentinel);
                CheckThat($"frame abort keeps the original error ({points[fault]})", original,
                    observed?.Message ?? "no exception");
                CheckThat($"frame abort is counted once ({points[fault]})",
                    UltimaBatcher2D.CanvasFrameAborts == aborts + 1, $"{UltimaBatcher2D.CanvasFrameAborts - aborts}");
                byte[] aborted = await Pixels();
                CheckThat($"aborted frame shows nothing of itself ({points[fault]})",
                    empty.AsSpan().SequenceEqual(aborted), "no partial frame, no previous frame's suffix");
                Exception next = null;
                try { Frame(DrawWhole); }
                catch (Exception ex) { next = ex; }
                CheckThat($"next frame draws after abort ({points[fault]})", next == null,
                    next?.Message ?? "begun batch, stale target or clip reset");
                byte[] recovered = await Pixels();
                CheckThat($"next frame is exact after abort ({points[fault]})", whole.AsSpan().SequenceEqual(recovered),
                    "no inherited clip, target, transform or queued run");
            }
        }
        finally
        {
            UltimaBatcher2D.InjectEndFailure = false;
            batcher.EndFrame(completed: false);
            batcher.SetRenderTarget(null);
            batcher.BeginFrame();
            UltimaBatcher2D.RetainCanvas = oldRetain;
#if GUO_LABS
            GUO.Renderer.DepthWorld.OrderedWorldCapture.Enabled = oldRd;
#endif
        }
    }
}
