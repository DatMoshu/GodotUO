// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): DLSS5 bake request queue.
// Mirrors SrRequestQueue: ConcurrentQueue + HashSet dedup, one bake per
// graphic per session; the pixel copy is taken on the main thread at
// enqueue time (the .mul/.uop readers seek a shared stream and are not
// thread-safe), so the background worker never touches the loaders.
// Pure .NET, no Godot objects.

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace GUO.Dlss5
{
    internal enum DlssBakeKind
    {
        Land,
        Static,
        Gump,
    }

    internal readonly struct DlssBakeRequest
    {
        public readonly DlssBakeKind Kind;
        public readonly int Graphic;
        public readonly int Width;
        public readonly int Height;
        public readonly uint[] Pixels;
        public readonly float Intensity;
        public readonly int Profile;

        public DlssBakeRequest(DlssBakeKind kind, int graphic, int w, int h, uint[] pixels,
            float intensity, int profile)
        {
            Kind = kind;
            Graphic = graphic;
            Width = w;
            Height = h;
            Pixels = pixels;
            Intensity = intensity;
            Profile = profile;
        }

        public string Key => $"{Kind}/{Graphic}";
    }

    internal static class DlssBakeQueue
    {
        private static readonly ConcurrentQueue<DlssBakeRequest> Pending = new ConcurrentQueue<DlssBakeRequest>();
        private static readonly ConcurrentQueue<DlssBakeRequest> Completed = new ConcurrentQueue<DlssBakeRequest>();
        private static readonly HashSet<string> Queued = new HashSet<string>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private static readonly object Sync = new object();

        public static int PendingCount => Pending.Count;
        public static int CompletedCount => Completed.Count;

        private static string KeyOf(DlssBakeKind kind, int graphic) => $"{kind}/{graphic}";

        /// <summary>Enqueue one bake (main thread; pixels are a copy).
        /// False when already queued, failed this session, bad input, or
        /// over the 1024px cap (large sprites stay native).</summary>
        public static bool Enqueue(DlssBakeKind kind, int graphic, int w, int h, uint[] pixels,
            float intensity = 1.0f, int profile = 0)
        {
            if (w <= 0 || h <= 0 || pixels == null || pixels.Length < w * h)
            {
                return false;
            }

            if (!DlssBakeTiles.IsSizeAllowed(w, h))
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

            Pending.Enqueue(new DlssBakeRequest(kind, graphic, w, h, pixels, intensity, profile));
            return true;
        }

        public static bool TryDequeue(out DlssBakeRequest req)
        {
            return Pending.TryDequeue(out req);
        }

        public static void EnqueueCompleted(DlssBakeRequest req)
        {
            Completed.Enqueue(req);
        }

        public static bool TryDequeueCompleted(out DlssBakeRequest req)
        {
            return Completed.TryDequeue(out req);
        }

        public static void MarkFailed(DlssBakeRequest req)
        {
            lock (Sync)
            {
                Queued.Remove(req.Key);
                Failed.Add(req.Key);
            }
        }

        public static void MarkDone(DlssBakeRequest req)
        {
            lock (Sync)
            {
                Queued.Remove(req.Key);
            }
        }

        /// <summary>Drains everything. In-flight work finishes but its
        /// completion is dropped by the caller when baking is off.</summary>
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
