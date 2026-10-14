// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): SuperRes 4x lazy request queue.
// ConcurrentQueue + HashSet dedup: every disk miss enqueues once per
// session; toggle-off drains via Clear (called from RefreshAllSR). The
// queued pixel copy is taken on the main thread at enqueue time (the
// .mul/.uop readers seek a shared stream and are not thread-safe), so the
// background worker never touches the loaders or any Godot object.

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace GUO.Renderer.SuperRes
{
    internal enum SrKind
    {
        Land,
        Static,
        Gump,
    }

    internal readonly struct SrRequest
    {
        public readonly SrKind Kind;
        public readonly int Graphic;
        public readonly int Width;
        public readonly int Height;
        public readonly uint[] Pixels;

        public SrRequest(SrKind kind, int graphic, int w, int h, uint[] pixels)
        {
            Kind = kind;
            Graphic = graphic;
            Width = w;
            Height = h;
            Pixels = pixels;
        }

        public string Key => $"{Kind}/{Graphic}";
    }

    internal static class SrRequestQueue
    {
        private static readonly ConcurrentQueue<SrRequest> Pending = new ConcurrentQueue<SrRequest>();
        private static readonly ConcurrentQueue<SrRequest> Completed = new ConcurrentQueue<SrRequest>();
        private static readonly HashSet<string> Queued = new HashSet<string>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private static readonly object Sync = new object();

        public static int PendingCount => Pending.Count;
        public static int CompletedCount => Completed.Count;

        private static string KeyOf(SrKind kind, int graphic) => $"{kind}/{graphic}";

        /// <summary>Enqueue one upscale (main thread; pixels are a copy).
        /// False when already queued, failed this session, or bad input.</summary>
        public static bool Enqueue(SrKind kind, int graphic, int w, int h, uint[] pixels)
        {
            if (w <= 0 || h <= 0 || pixels == null || pixels.Length < w * h)
            {
                return false;
            }

            // Caps: native art is tiny (land 44x44, statics usually <256px,
            // gumps <512px); refuse absurd sizes rather than OOM the worker.
            if (w > 1024 || h > 1024)
            {
                return false;
            }

            string key = KeyOf(kind, graphic);
            lock (Sync)
            {
                if (Queued.Contains(key) || Failed.Contains(key))
                {
                    return false;
                }

                Queued.Add(key);
            }

            Pending.Enqueue(new SrRequest(kind, graphic, w, h, pixels));
            return true;
        }

        public static bool TryDequeue(out SrRequest req)
        {
            return Pending.TryDequeue(out req);
        }

        public static void EnqueueCompleted(SrRequest req)
        {
            Completed.Enqueue(req);
        }

        public static bool TryDequeueCompleted(out SrRequest req)
        {
            return Completed.TryDequeue(out req);
        }

        public static void MarkFailed(SrRequest req)
        {
            lock (Sync)
            {
                Queued.Remove(req.Key);
                Failed.Add(req.Key);
            }
        }

        public static void MarkDone(SrRequest req)
        {
            lock (Sync)
            {
                Queued.Remove(req.Key);
            }
        }

        /// <summary>Drains everything (toggle flips call this via
        /// RefreshAllSR). In-flight work finishes but its completion is
        /// dropped by the committer when the toggle is off.</summary>
        public static void Clear()
        {
            lock (Sync)
            {
                while (Pending.TryDequeue(out _)) { }
                while (Completed.TryDequeue(out _)) { }
                Queued.Clear();
                Failed.Clear();
            }
        }
    }
}
