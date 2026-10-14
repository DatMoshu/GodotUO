// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): SuperRes 4x main-thread committer.
// Warms the SR LRU from the worker's PNG files (at most 2 per frame) via
// Image.LoadPngFromBuffer -> Rgba8 -> GenerateMipmaps -> CreateFromImage,
// then dirties the chunk meshes so the seamless native->SR swap shows in
// the native footprint (draws stay LinearClamp via IsSrTexture). Called
// from GameController.Update; drops completions while the toggle is off.

using System;
using GUO.Compat;
using GUO.Utility.Logging;

namespace GUO.Renderer.SuperRes
{
    internal static class SrCommit
    {
        public const int MaxPerFrame = 2;

        /// <summary>Commits up to 2 finished upscales. Returns how many.</summary>
        public static int Poll()
        {
            int done = 0;
            try
            {
                bool on;
                try
                {
                    on = Configuration.ProfileManager.CurrentProfile?.SuperRes4x == true;
                }
                catch
                {
                    return 0;
                }

                var uo = Client.Game?.UO;
                if (uo?.Arts == null || uo?.Gumps == null)
                {
                    // No world yet: drop nothing, just wait.
                    return 0;
                }

                for (int i = 0; i < MaxPerFrame; i++)
                {
                    if (!SrRequestQueue.TryDequeueCompleted(out SrRequest req))
                    {
                        break;
                    }

                    if (!on)
                    {
                        SrRequestQueue.MarkDone(req);
                        continue;
                    }

                    bool ok = Warm(req, uo);
                    if (ok)
                    {
                        SrRequestQueue.MarkDone(req);
                        DirtyChunks(uo);
                        done++;
                    }
                    else
                    {
                        SrRequestQueue.MarkFailed(req);
                    }
                }
            }
            catch (Exception e)
            {
                try { Log.Warn($"[SR4x] commit failed ({e.Message})"); } catch { }
            }

            return done;
        }

        private static bool Warm(SrRequest req, UltimaOnline uo)
        {
            try
            {
                if (req.Kind == SrKind.Gump)
                {
                    return uo.Gumps.TryGetSr(
                        (uint)req.Graphic,
                        out Godot.Texture2D _, out Rectangle _, out _, out _);
                }

                return uo.Arts.TryGetSr(
                    req.Kind == SrKind.Land,
                    (ushort)req.Graphic,
                    out Godot.Texture2D _, out Rectangle _, out _, out _);
            }
            catch
            {
                return false;
            }
        }

        private static void DirtyChunks(UltimaOnline uo)
        {
            try
            {
                var map = uo.World?.Map;
                if (map == null)
                {
                    return;
                }

                foreach (var chunk in map.GetUsedChunks())
                {
                    try
                    {
                        if (chunk?.Mesh != null)
                        {
                            chunk.Mesh.IsDirty = true;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
    }
}
