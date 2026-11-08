// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer.DepthWorld;
using GUO.Renderer;
using Rectangle = GUO.Compat.Rectangle;

namespace GUO.Host;

public partial class BatcherProbe
{
    // The main frame throws while the Depth World is recording, or after its
    // ticket is queued: the ticket must never render, the error must reach the
    // caller, and the next frame must be admitted to the pass and draw exactly.
    private async Task VerifyOrderedFrameAbort(UltimaBatcher2D batcher, SubViewport viewport, Texture2D palette)
    {
        bool old = OrderedWorldCapture.Enabled;
        using var black = BuildSolid(new byte[] { 0, 0, 0 });
        using var background = BuildSolid(new byte[] { 3, 80, 137 });
        using var gray = BuildSolid(new byte[] { 123, 123, 123 });
        batcher.HueTexture = batcher.LightTexture = palette;

        // The world as GameScene submits it, ending the world batch or not.
        void World(int stop, bool reject = false)
        {
            batcher.Begin();
            batcher.Draw(black, new Rectangle(-16384, -16384, 32768, 32768), new Rectangle(0, 0, 1, 1), new Vector3(0, 0, 1), 0);
            batcher.BeginOrderedWorld(true, viewport.Size);
            batcher.Draw(background, new Rectangle(-4, -4, 24, 24), new Rectangle(0, 0, 1, 1), new Vector3(0, 0, 1), 0);
            batcher.Draw(gray, new Rectangle(1, 1, 5, 6), new Rectangle(0, 0, 1, 1), new Vector3(0, 0, 0.5f), 0);
            if (reject)
            {
                // A clip inside the world rejects the pass and replays the
                // recorded prefix into the capture's canvas fallback.
                batcher.ClipBegin(1, 1, 4, 4);
                batcher.Draw(gray, new Rectangle(0, 0, 8, 8), new Rectangle(0, 0, 1, 1), new Vector3(0, 0, 1), 0);
                batcher.ClipEnd();
            }
            if (stop == 0) return; // recording still open
            batcher.EndOrderedWorld();
            if (stop == 1) return; // ticket queued, world batch open
            batcher.End();
        }

        async Task<byte[]> Pixels()
        {
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using Image image = viewport.GetTexture().GetImage();
            return image.GetData();
        }

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
            OrderedWorldCapture.Enabled = true;
            Frame(() => { });
            byte[] empty = await Pixels();
            long drawn = OrderedWorldCapture.FramesDrawn;
            Frame(() => World(2));
            byte[] whole = await Pixels();
            CheckThat("ordered abort fixture renders on the pass", OrderedWorldCapture.FramesDrawn == drawn + 1
                && !empty.AsSpan().SequenceEqual(whole), $"fallback={OrderedWorldCapture.LastFallback}");

            string[] points = { "while recording", "with the ticket queued", "after the world batch ended" };
            for (int fault = 0; fault < points.Length; fault++)
            {
                var sentinel = new InvalidOperationException("ordered frame abort fixture");
                Exception observed = null;
                long aborts = UltimaBatcher2D.CanvasFrameAborts;
                drawn = OrderedWorldCapture.FramesDrawn;
                try { Frame(() => { World(fault); throw sentinel; }); }
                catch (Exception ex) { observed = ex; }
                CheckThat($"ordered abort keeps the original error ({points[fault]})", ReferenceEquals(observed, sentinel),
                    observed?.Message ?? "no exception");
                CheckThat($"ordered abort closes the recording ({points[fault]})", !batcher.OrderedWorldRecording
                    && UltimaBatcher2D.CanvasFrameAborts == aborts + 1, "recording left open or abort not counted");
                byte[] aborted = await Pixels();
                CheckThat($"ordered abort submits no ticket ({points[fault]})", OrderedWorldCapture.FramesDrawn == drawn,
                    $"RD delta={OrderedWorldCapture.FramesDrawn - drawn}");
                CheckThat($"ordered aborted frame shows nothing of itself ({points[fault]})",
                    empty.AsSpan().SequenceEqual(aborted), "partial world or previous presenter shown");
                Frame(() => World(2));
                byte[] recovered = await Pixels();
                CheckThat($"ordered next frame is admitted after abort ({points[fault]})",
                    OrderedWorldCapture.FramesDrawn == drawn + 1, $"fallback={OrderedWorldCapture.LastFallback}");
                CheckThat($"ordered next frame is exact after abort ({points[fault]})", whole.AsSpan().SequenceEqual(recovered),
                    "stale presenter, clip or raw group");
            }

            // Synchronisation: admission requires the main thread to be the
            // render thread, so a submitted ticket has run by the next frame.
            Frame(() => World(2));
            await Pixels();
            OrderedWorldCapture capture = batcher.OrderedWorldProbe;
            CheckThat("ordered ticket has finished before the next frame", capture != null && capture.CanBegin,
                "a submitted ticket is still pending at the next frame");

            // A failing abort cleanup: the draw's error still reaches the
            // caller, recovery stays pending and is retried before the next frame.
            {
                var sentinel = new InvalidOperationException("ordered cleanup failure fixture");
                Exception observed = null;
                long failures = UltimaBatcher2D.CanvasRecoveryFailures;
                drawn = OrderedWorldCapture.FramesDrawn;
                OrderedWorldCapture.InjectAbortFailure = true;
                try { Frame(() => { World(0); throw sentinel; }); }
                catch (Exception ex) { observed = ex; }
                finally { OrderedWorldCapture.InjectAbortFailure = false; }
                CheckThat("ordered cleanup failure keeps the original error", ReferenceEquals(observed, sentinel),
                    observed?.Message ?? "no exception");
                CheckThat("ordered cleanup failure leaves recovery pending",
                    UltimaBatcher2D.CanvasRecoveryFailures == failures + 1 && !batcher.OrderedWorldRecording,
                    $"failures +{UltimaBatcher2D.CanvasRecoveryFailures - failures}");
                byte[] aborted = await Pixels();
                CheckThat("ordered cleanup failure submits no ticket and shows nothing",
                    OrderedWorldCapture.FramesDrawn == drawn && empty.AsSpan().SequenceEqual(aborted),
                    $"RD delta={OrderedWorldCapture.FramesDrawn - drawn}");
                Exception next = null;
                try { Frame(() => World(2)); }
                catch (Exception ex) { next = ex; }
                byte[] recovered = await Pixels();
                CheckThat("ordered next frame retries the cleanup and draws", next == null
                    && OrderedWorldCapture.FramesDrawn == drawn + 1 && whole.AsSpan().SequenceEqual(recovered),
                    next?.Message ?? $"fallback={OrderedWorldCapture.LastFallback}");
            }

            // An abort while a submitted ticket is pending leaves that ticket's
            // canvas fallback alone: its render-thread failure replays into it.
            {
                var sentinel = new InvalidOperationException("ordered pending ticket fixture");
                Exception observed = null;
                int held = -1;
                try
                {
                    Frame(() =>
                    {
                        World(1, reject: true);
                        held = capture.FallbackItems;
                        capture.ProbeHoldTicket(true);
                        throw sentinel;
                    });
                }
                catch (Exception ex) { observed = ex; }
                int after = capture.FallbackItems;
                capture.ProbeHoldTicket(false);
                CheckThat("ordered pending ticket fixture fills the fallback", held > 0, $"{held} fallback items");
                CheckThat("ordered abort keeps a pending ticket's fallback", ReferenceEquals(observed, sentinel) && after == held,
                    $"{held} -> {after} fallback items; {observed?.Message}");
                drawn = OrderedWorldCapture.FramesDrawn;
                Frame(() => World(2));
                byte[] recovered = await Pixels();
                CheckThat("ordered next frame is exact after the pending ticket", OrderedWorldCapture.FramesDrawn == drawn + 1
                    && whole.AsSpan().SequenceEqual(recovered), $"fallback={OrderedWorldCapture.LastFallback}");
            }
        }
        finally
        {
            OrderedWorldCapture.InjectAbortFailure = false;
            batcher.OrderedWorldProbe?.ProbeHoldTicket(false);
            batcher.EndFrame(completed: false);
            batcher.BeginFrame();
            OrderedWorldCapture.Enabled = old;
        }
    }
}
