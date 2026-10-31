// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Host;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer;

public partial class BatcherProbe
{
    // Parent registration: await VerifyDeferredVisibility(batcher, viewport).
    // Deliberately separate from existing eager-default retained tests.
    private async Task VerifyDeferredVisibility(UltimaBatcher2D batcher, SubViewport viewport)
    {
        bool oldRetain = UltimaBatcher2D.RetainCanvas;
        bool oldBatched = UltimaBatcher2D.BatchedWorld;
        bool oldGpu = GpuWorld.Enabled;
#if GUO_LABS
        bool oldRd = GUO.Renderer.DepthWorld.OrderedWorldCapture.Enabled;
        GUO.Renderer.DepthWorld.OrderedWorldCapture.Enabled = false;
#endif
        using var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        using var texture = ImageTexture.CreateFromImage(image);
        var reference = new List<byte[]>();
        int[] counts = { 6, 2, 2, 5, 0, 0, 6, 6, 3, 3, 3, 3, 3, 3, 0 };
        Vector3 hue = ShaderHueTranslator.GetHueVector(0);

        void DrawItems(int count)
        {
            for (int i = 0; i < count; i++)
            {
                batcher.Begin();
                batcher.Draw(texture, new Compat.Rectangle(i, i, 2, 2), null, hue, 0);
                batcher.End();
            }
        }

        try
        {
            UltimaBatcher2D.BatchedWorld = false;
            GpuWorld.Enabled = false;
            for (int mode = 0; mode < 3; mode++)
            {
                UltimaBatcher2D.RetainCanvas = false;
                batcher.BeginFrame(); // isolate modes without assuming pool size
                UltimaBatcher2D.RetainCanvas = mode != 0;
                for (int step = 0; step < counts.Length; step++)
                {
                    long hides = UltimaBatcher2D.CanvasHides, reveals = UltimaBatcher2D.CanvasReveals;
                    long submitted = RetainedCanvasCommands.Submitted;
                    bool complete = false;
                    try
                    {
                        batcher.BeginFrame(deferRetainedHiding: mode == 2);
                        if (step is 8 or 9)
                        {
                            batcher.Begin();
                            batcher.PushUiTransform(new Transform2D(0f, new Vector2(1, 1)));
                            bool clip = batcher.ClipBegin(0, 0, 4, 4);
                            CheckThat("deferred clip fixture opens", clip, "nested retained ancestor");
                            if (clip)
                            {
                                batcher.Draw(texture, new Compat.Rectangle(0, 0, 6, 6), null, hue, 0);
                                batcher.ClipEnd();
                            }
                            batcher.ClipEnd();
                            batcher.Draw(texture, new Compat.Rectangle(6, 6, 1, 1), null, hue, 0);
                            batcher.End();
                        }
                        else if (step is 10 or 11)
                        {
                            batcher.Begin();
                            batcher.Draw(texture, new Compat.Rectangle(0, 0, 7, 7), null, hue, 0);
                            batcher.SetBlendState(new BlendState { ColorSourceBlend = Blend.DestinationColor,
                                ColorDestinationBlend = Blend.InverseSourceAlpha });
                            batcher.Draw(texture, new Compat.Rectangle(1, 1, 4, 4), null, new Vector3(0, 0, 0.5f), 0);
                            batcher.SetBlendState(null);
                            batcher.End();
                        }
                        else if (step is 12 or 13)
                        {
                            batcher.Begin();
                            batcher.Draw(texture, new Compat.Rectangle(0, 0, 7, 7), null, hue, 0);
                            UltimaBatcher2D.BatchedWorld = true;
                            batcher.Draw(texture, new Compat.Rectangle(1, 1, 4, 4), null, new Vector3(0, 0, 0.5f), 0);
                            UltimaBatcher2D.BatchedWorld = false;
                            batcher.Draw(texture, new Compat.Rectangle(6, 6, 1, 1), null, hue, 0);
                            batcher.End();
                        }
                        else DrawItems(counts[step]);
                        complete = true;
                    }
                    finally { batcher.EndFrame(complete); }
                    long finalizedHides = UltimaBatcher2D.CanvasHides;
                    batcher.EndFrame();
                    CheckThat("deferred finalization is idempotent", UltimaBatcher2D.CanvasHides == finalizedHides,
                        $"mode {mode}, step {step}");
                    await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                    using Image pixels = viewport.GetTexture().GetImage();
                    byte[] actual = pixels.GetData();
                    if (mode == 0) reference.Add(actual);
                    else if (!CheckThat($"deferred pixels mode {mode} step {step}", reference[step].AsSpan().SequenceEqual(actual),
                        "exact raw baseline: shrink/grow/empty, clip ancestors, backbuffer blend, raw prefix/tail"))
                    {
                        for (int p = 0, shown = 0; p < actual.Length && shown < 12; p++)
                            if (reference[step][p] != actual[p])
                            {
                                GD.Print($"[batcher-probe] pixel byte {p}: expected {reference[step][p]}, actual {actual[p]}");
                                shown++;
                            }
                    }

                    if (mode == 2 && step is 2 or 5 or 7 or 9)
                        CheckThat($"stable deferred prefix visibility {step}", UltimaBatcher2D.CanvasHides == hides
                            && UltimaBatcher2D.CanvasReveals == reveals, "zero redundant hide/reveal calls");
                    if (mode == 2 && step == 7)
                        CheckThat("stable deferred stream reuses commands", RetainedCanvasCommands.Submitted == submitted,
                            "visibility optimization preserves native command reuse");
                    if (mode == 2 && step == 1)
                        CheckThat("deferred shrink hides suffix once", UltimaBatcher2D.CanvasHides - hides == 4
                            && UltimaBatcher2D.CanvasReveals == reveals, "six independent retained items to two");
                    if (mode == 2 && step == 3)
                        CheckThat("deferred growth reveals only reused tail", UltimaBatcher2D.CanvasReveals - reveals == 3
                            && UltimaBatcher2D.CanvasHides == hides, "two retained items to five");
                }
            }

            UltimaBatcher2D.RetainCanvas = true;
            // Capture known complete output for recovery. Both commands share
            // one owner so aborting after the first is a matching-shorter stream.
            void DrawPair(bool end = true)
            {
                batcher.Begin();
                batcher.Draw(texture, new Compat.Rectangle(0, 0, 3, 3), null, hue, 0);
                batcher.Draw(texture, new Compat.Rectangle(5, 5, 2, 2), null, hue, 0);
                if (end) batcher.End();
            }
            batcher.BeginFrame(true); DrawPair(); batcher.EndFrame();
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            byte[] pairPixels;
            using (Image pixels = viewport.GetTexture().GetImage()) pairPixels = pixels.GetData();

            for (int fault = 0; fault < 4; fault++)
            {
                batcher.BeginFrame(true); DrawPair(); batcher.EndFrame();
                var sentinel = new InvalidOperationException("deferred draw fixture");
                Exception observed = null;
                try
                {
                    try
                    {
                        batcher.BeginFrame(true);
                        if (fault != 0)
                        {
                            batcher.Begin();
                            batcher.Draw(texture, new Compat.Rectangle(0, 0, 3, 3), null, hue, 0);
                            if (fault == 2)
                            {
                                UltimaBatcher2D.BatchedWorld = true;
                                batcher.Draw(texture, new Compat.Rectangle(1, 1, 4, 4), null, hue, 0);
                            }
                            if (fault == 3) batcher.End();
                        }
                        throw sentinel;
                    }
                    finally { batcher.EndFrame(completed: false); }
                }
                catch (Exception ex) { observed = ex; }
                finally { UltimaBatcher2D.BatchedWorld = false; }
                CheckThat($"abort preserves original exception {fault}", ReferenceEquals(observed, sentinel),
                    "before items, matched prefix, queued raw run, after batch End");
                await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                using (Image pixels = viewport.GetTexture().GetImage())
                    CheckThat($"abort removes stale native suffix {fault}", reference[4].AsSpan().SequenceEqual(pixels.GetData()),
                        "aborted pool is empty rather than old prefix/suffix");
                batcher.BeginFrame(true); DrawPair(); batcher.EndFrame();
                await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                using (Image pixels = viewport.GetTexture().GetImage())
                    CheckThat($"next frame recovers after abort {fault}", pairPixels.AsSpan().SequenceEqual(pixels.GetData()),
                        "no inherited clip, raw run, hidden ancestor or partial stream");
            }

            // An old visible command list exceeds the per-owner budget, then
            // shrinks and becomes retainable again. Deferred visibility must
            // not treat the uncached native stream as a reusable retained tail.
            batcher.BeginFrame(true); batcher.Begin();
            for (int i = 0; i < 8193; i++)
                batcher.Draw(texture, new Compat.Rectangle(0, 0, 1, 1), null, hue, 0);
            batcher.End(); batcher.EndFrame();
            batcher.BeginFrame(true); DrawPair(); batcher.EndFrame();
            long recoveredSubmitted = RetainedCanvasCommands.Submitted;
            batcher.BeginFrame(true); DrawPair(); batcher.EndFrame();
            CheckThat("deferred oversized owner resumes retention after shrink",
                RetainedCanvasCommands.Submitted == recoveredSubmitted, "raw owner clear plus fresh small admission");

            // A repeated frame reads the live texture RID, even when neither
            // native commands nor active-prefix visibility are resubmitted.
            image.Fill(Colors.Green); texture.Update(image);
            batcher.BeginFrame(true); DrawPair(); batcher.EndFrame();
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using (Image pixels = viewport.GetTexture().GetImage())
                CheckThat("deferred retained commands observe texture updates", !pairPixels.AsSpan().SequenceEqual(pixels.GetData()),
                    "same texture RID, visibly changed pixels");
            image.Fill(Colors.Red); texture.Update(image);

            // Missing finalizer, including an unclosed batch: next BeginFrame
            // recovers BEFORE resetting the old active count, even in Release.
            batcher.BeginFrame(true); DrawPair(end: false);
            long aborts = UltimaBatcher2D.CanvasFrameAborts;
            batcher.BeginFrame(); DrawPair();
            CheckThat("missing deferred EndFrame recovers for eager caller", UltimaBatcher2D.CanvasFrameAborts == aborts + 1,
                "real runtime recovery, not a Debug assertion");
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using (Image pixels = viewport.GetTexture().GetImage())
                CheckThat("missing finalizer has exact next-frame pixels", pairPixels.AsSpan().SequenceEqual(pixels.GetData()),
                    "eager and deferred lifecycle interoperability");

            batcher.BeginFrame(true); DrawPair(end: false);
            bool rejected = false;
            try { batcher.EndFrame(); }
            catch (InvalidOperationException) { rejected = true; }
            CheckThat("successful finalizer rejects unclosed batch", rejected, "also aborts the broken frame");
            batcher.BeginFrame(true); DrawPair(); batcher.EndFrame();

            await VerifyDeferredTargets(batcher, viewport, texture, hue);

            // Legacy GPU mode is excluded even when the caller opts in.
            GpuWorld.Enabled = true;
            batcher.BeginFrame(true); DrawPair();
            aborts = UltimaBatcher2D.CanvasFrameAborts;
            batcher.BeginFrame();
            CheckThat("legacy GPU retains eager lifecycle", UltimaBatcher2D.CanvasFrameAborts == aborts,
                "no deferred frame marker or ownership competition");
            GpuWorld.Enabled = false;
#if GUO_LABS
            GUO.Renderer.DepthWorld.OrderedWorldCapture.Enabled = true;
            batcher.BeginFrame(true); DrawPair();
            aborts = UltimaBatcher2D.CanvasFrameAborts;
            batcher.BeginFrame();
            CheckThat("RD retains eager lifecycle", UltimaBatcher2D.CanvasFrameAborts == aborts,
                "late presenter election remains outside deferred ownership");
            GUO.Renderer.DepthWorld.OrderedWorldCapture.Enabled = false;
#endif
        }
        finally
        {
            batcher.EndFrame(completed: false);
            UltimaBatcher2D.BatchedWorld = oldBatched;
            GpuWorld.Enabled = oldGpu;
#if GUO_LABS
            GUO.Renderer.DepthWorld.OrderedWorldCapture.Enabled = oldRd;
#endif
            // Actually clear test commands while their texture still lives.
            UltimaBatcher2D.RetainCanvas = false;
            batcher.BeginFrame();
            UltimaBatcher2D.RetainCanvas = oldRetain;
        }
    }

    private async Task VerifyDeferredTargets(UltimaBatcher2D batcher, SubViewport viewport,
        Texture2D texture, Vector3 hue)
    {
        // Reuse the same item indices across world/UI targets, a resized
        // replacement target and the final composite. Compare every target,
        // not just the composite, so a hidden stale source cannot escape.
        var reference = new List<byte[]>();
        for (int mode = 0; mode < 3; mode++)
        {
            UltimaBatcher2D.RetainCanvas = false;
            batcher.BeginFrame();
            UltimaBatcher2D.RetainCanvas = mode != 0;
            RenderTarget2D first = new(this, 8, 8);
            using var second = new RenderTarget2D(this, 8, 8);
            try
            {
                for (int step = 0; step < 6; step++)
                {
                    if (step == 3)
                    {
                        first.Dispose();
                        first = new RenderTarget2D(this, 12, 10);
                    }
                    bool complete = false;
                    try
                    {
                        batcher.BeginFrame(deferRetainedHiding: mode == 2);
                        for (int pass = 0; pass < 2; pass++)
                        {
                            // Reverse submission order on alternate frames.
                            RenderTarget2D target = (pass == (step & 1)) ? first : second;
                            batcher.SetRenderTarget(target);
                            batcher.Clear(ReferenceEquals(target, first) ? Colors.Black : Colors.Transparent);
                            if (step == 2 && ReferenceEquals(target, second)) continue;
                            batcher.Begin(new Transform2D(0f, new Vector2(step & 1, 0)));
                            batcher.PushUiTransform(new Transform2D(0f, new Vector2(1, 1)));
                            batcher.ClipBegin(0, 0, step >= 3 ? 5 : 3, 4);
                            batcher.Draw(texture, new Compat.Rectangle(0, 0, 7, 7), null, hue, 0);
                            batcher.ClipEnd();
                            batcher.ClipEnd();
                            batcher.End();
                        }
                        batcher.SetRenderTarget(null);
                        batcher.Begin();
                        batcher.Draw(first.Texture, new Compat.Rectangle(0, 0, 5, 5), null, hue, 0);
                        batcher.Draw(second.Texture, new Compat.Rectangle(3, 3, 5, 5), null, hue, 0);
                        batcher.End();
                        complete = true;
                    }
                    finally { batcher.EndFrame(complete); }
                    // Source viewport updates and their dependent composite
                    // must both have reached the GPU before reading pixels.
                    await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                    await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
                    for (int surface = 0; surface < 3; surface++)
                    {
                        Texture2D source = surface == 0 ? first.Texture : surface == 1 ? second.Texture : viewport.GetTexture();
                        using Image pixels = source.GetImage();
                        byte[] actual = pixels.GetData();
                        if (mode == 0) reference.Add(actual);
                        else CheckThat($"deferred target pixels mode {mode} step {step} surface {surface}",
                            reference[step * 3 + surface].AsSpan().SequenceEqual(actual),
                            "target reparent, omitted pass, replacement size, transform, clip and composite match raw");
                    }
                }
            }
            finally
            {
                batcher.EndFrame(completed: false);
                UltimaBatcher2D.RetainCanvas = false;
                batcher.BeginFrame();
                batcher.SetRenderTarget(null);
                first.Dispose();
            }
        }
        UltimaBatcher2D.RetainCanvas = true;
    }
}
