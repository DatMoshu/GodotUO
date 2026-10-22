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

        private static bool _unavailableLogged;
        private static string _lastLogged;

        /// <summary>True on a renderer the pass runs on: Forward+ (the exact encoding is proven there only).</summary>
        public static bool Available => RenderingServer.GetCurrentRenderingMethod() == "forward_plus";

        /// <summary>Enabled, on a renderer that can run it, with a pass to draw into.</summary>
        public static bool Active => Enabled && Pass != null;

        /// <summary>Chunks build their instances while this is true; a chunk built without them queues its statics as before.</summary>
        public static bool Building => Enabled && Pass != null;

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
