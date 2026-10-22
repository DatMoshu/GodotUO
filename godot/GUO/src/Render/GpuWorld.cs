// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.Renderer
{
    /// <summary>
    /// The <c>--gpu-world</c> switch (ADR-0007, amendment 2026-10-19): the
    /// world's statics kept on the GPU and the whole sorted pass ordered by
    /// the world depth buffer. Off by default until the owner sets the
    /// default from the numbers; Forward+ only. This is a fallback and parity
    /// switch inside the one world pass, like <c>--merged-land</c>, not a
    /// second presenter (ADR-0001 amendment).
    /// </summary>
    public static class GpuWorld
    {
        /// <summary>Asked for on the command line or by the parity probe.</summary>
        public static bool Enabled;

        /// <summary>The experimental raw-bytes output (WorldDepthPass.RawOutput), <c>--gpu-world-raw</c>.</summary>
        public static bool RawOutput;

        /// <summary>The pass of the running scene, while one exists.</summary>
        public static WorldDepthPass Pass { get; private set; }

        /// <summary>Counts up each time a pass is created; a chunk built for an older pass rebuilds.</summary>
        public static int Generation { get; private set; }

        /// <summary>Frames the pass drew, frames it fell back on, and the last reason, for the perf probe and the log.</summary>
        public static long FramesDrawn, FramesFallback;
        public static string LastFallback;

        /// <summary>
        /// True when the current world frame's sorted pass went through the
        /// pass. GameScene.PrepareGpuWorld clears it as the world frame
        /// starts; RenderLists.DrawWorld sets it when the pass drew; nothing
        /// drawn after the world (the UI's lists) writes it, so a capture
        /// taken after the frame reads the world's answer.
        /// </summary>
        public static bool LastFrameOnPass;

        private static bool _unavailableLogged;
        private static string _lastLogged;

        /// <summary>True on a renderer the pass runs on: Forward+ (the exact encoding is proven there only).</summary>
        public static bool Available => RenderingServer.GetCurrentRenderingMethod() == "forward_plus";

        /// <summary>Enabled, on a renderer that can run it, with a pass to draw into.</summary>
        public static bool Active => Enabled && Pass != null;

        /// <summary>Chunks build their instances while this is true; a chunk built without them queues its statics as before.</summary>
        public static bool Building => Enabled && Pass != null && !Suspended;

        /// <summary>
        /// GPU-03: world frames left before the pass is tried again after a
        /// frame it could not carry. While it counts down the chunks hold
        /// their statics as sprites again, so a canvas frame draws them all.
        /// </summary>
        public static int SuspendedFrames { get; private set; }

        public static bool Suspended => SuspendedFrames > 0;

        /// <summary>How many world frames a suspension lasts (two seconds at 60).</summary>
        public const int SuspendFrames = 120;

        /// <summary>Instanced chunk sets the walk showed this world frame (GameScene counts them).</summary>
        public static int InstancesShown;

        /// <summary>
        /// Set by RenderLists.DrawWorld when a frame fell back after the walk
        /// had already left its statics to the pass: the canvas has not got
        /// them, so GameScene suspends the pass and walks and draws the frame
        /// again on the canvas.
        /// </summary>
        public static bool RedrawOnCanvas;

        /// <summary>A world frame starts: a suspension counts down, the frame's counters reset.</summary>
        public static void FrameStarts()
        {
            if (SuspendedFrames > 0)
            {
                SuspendedFrames--;
            }

            InstancesShown = 0;
            RedrawOnCanvas = false;
            LastFrameOnPass = false;
        }

        /// <summary>The pass stays off for SuspendFrames world frames; said once per reason.</summary>
        public static void Suspend(string why)
        {
            SuspendedFrames = SuspendFrames;
            FramesSuspended++;
            if (why != _lastSuspended)
            {
                _lastSuspended = why;
                GD.Print($"[GUO] --gpu-world: the pass is suspended for {SuspendFrames} frames: {why}");
            }
        }

        /// <summary>Suspensions so far, for the perf probe.</summary>
        public static long FramesSuspended;
        private static string _lastSuspended;

        public static WorldDepthPass Create(Node parent, Vector2I size)
        {
            if (!Enabled)
            {
                return null;
            }

            if (!Available)
            {
                if (!_unavailableLogged)
                {
                    _unavailableLogged = true;
                    GD.Print($"[GUO] --gpu-world: the world depth pass needs Forward+; {RenderingServer.GetCurrentRenderingMethod()} keeps the canvas path");
                }

                return null;
            }

            Pass?.Dispose();
            Pass = new WorldDepthPass(parent, size) { RawOutput = RawOutput };
            Generation++;
            return Pass;
        }

        public static void Destroy()
        {
            Pass?.Dispose();
            Pass = null;
            Generation++;
        }

        /// <summary>The pass went with the scene's world target: forget it, so chunks built for it rebuild.</summary>
        public static void DropIfDead()
        {
            if (Pass != null && !Pass.Alive)
            {
                Destroy();
            }
        }

        /// <summary>A frame that could not go through the pass; said once per reason.</summary>
        public static void FellBack(string why)
        {
            FramesFallback++;
            LastFallback = why;
            if (why != _lastLogged)
            {
                _lastLogged = why;
                GD.Print($"[GUO] --gpu-world: frame drawn on the canvas: {why}");
            }
        }
    }
}
