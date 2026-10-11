// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GUO.Comfy
{
    /// <summary>
    /// MoGe sprite-to-splat bridge (tools/moge/moge_to_ply.py): shells out
    /// exactly like IO/Audio/VoiceManager shells tools/comfy/voice.py --
    /// python from GUO_PYTHON, tool from GUO_MOGE_TOOL, model from
    /// GUO_MOGE_MODEL -- and returns the parser-ready PLY bytes. Timeouts
    /// live on both sides like the voice path: --timeout bounds the tool's
    /// stages, the caller holds the hard kill switch (one CUDA kernel cannot
    /// be interrupted, so WaitForExit + Kill enforces the wall clock).
    /// Runs on a worker thread; callers pick the result up in Update, the
    /// way SplatPlacerGump polls its ComfyClient task.
    /// </summary>
    internal static class MogeClient
    {
        /// <summary>Which python runs the tool (GUO_PYTHON when set).</summary>
        public static string Python()
        {
            string py = Environment.GetEnvironmentVariable("GUO_PYTHON");
            return string.IsNullOrWhiteSpace(py) ? "python" : py;
        }

        /// <summary>Path to moge_to_ply.py (GUO_MOGE_TOOL wins), else null.</summary>
        public static string ToolPath()
        {
            string tool = Environment.GetEnvironmentVariable("GUO_MOGE_TOOL");
            if (!string.IsNullOrWhiteSpace(tool))
            {
                return tool;
            }

            string root = FindRepoRoot();
            if (root != null)
            {
                string guess = Path.Combine(root, "tools", "moge", "moge_to_ply.py");
                if (File.Exists(guess))
                {
                    return guess;
                }
            }

            return null;
        }

        /// <summary>MoGe model id or local .pt (GUO_MOGE_MODEL); empty defers to the tool default.</summary>
        public static string ResolveModel()
        {
            return Environment.GetEnvironmentVariable("GUO_MOGE_MODEL") ?? "";
        }

        /// <summary>Scratch dir for sprite inputs and PLY outputs (gitignored build/ when found).</summary>
        public static string ResolveStageDir()
        {
            string root = FindRepoRoot();
            string dir = root != null
                ? Path.Combine(root, "build", "moge")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GUO", "moge");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Graphic-keyed disk cache (build/moge/moge_0xXXXX.ply): one MoGe run
        /// per unique art, reused when present. The python tool itself has no
        /// cache (it only writes the --out path it is given), so the gump
        /// checks this before queuing a generation and writes it after one.
        /// Model-agnostic: a model change needs a manual cache wipe.
        /// </summary>
        public static string CachedPlyPath(ushort graphic)
        {
            return Path.Combine(ResolveStageDir(), $"moge_0x{graphic:X4}.ply");
        }

        /// <summary>True when a non-empty cached PLY exists for the graphic.</summary>
        public static bool TryGetCachedPly(ushort graphic, out byte[] ply)
        {
            ply = null;
            try
            {
                string path = CachedPlyPath(graphic);
                if (!File.Exists(path))
                {
                    return false;
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (bytes == null || bytes.Length == 0)
                {
                    return false;
                }

                ply = bytes;
                return true;
            }
            catch (Exception)
            {
                ply = null;
                return false;
            }
        }

        /// <summary>Best-effort write of a fresh result into the graphic cache.</summary>
        public static void SaveCachedPly(ushort graphic, byte[] ply)
        {
            try
            {
                if (ply == null || ply.Length == 0)
                {
                    return;
                }

                File.WriteAllBytes(CachedPlyPath(graphic), ply);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Sprite PNG bytes to gaussian PLY bytes end to end: write the input
        /// under build/moge/, run the tool with --mask-background, read the
        /// output PLY back. Progress reports go to <paramref name="report"/>
        /// (worker thread; marshal before touching UI). Already hot: the
        /// returned task runs on a worker, so callers assign it directly and
        /// poll it in Update like the SplatPlacerGump generate flow.
        /// </summary>
        public static Task<byte[]> MogeToPlyAsync(
            byte[] png, Action<string> report, CancellationToken ct, int timeoutSeconds = 600)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (png == null || png.Length == 0)
                {
                    throw new ArgumentException("no sprite PNG bytes", nameof(png));
                }

                string tool = ToolPath();
                if (tool == null)
                {
                    throw new InvalidOperationException("no moge tool (GUO_MOGE_TOOL)");
                }

                string dir = ResolveStageDir();
                string stem = $"moge_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
                string inPng = Path.Combine(dir, stem + ".png");
                string outPly = Path.Combine(dir, stem + ".ply");
                report?.Invoke($"Writing sprite ({png.Length / 1024} KiB)...");
                File.WriteAllBytes(inPng, png);

                string model = ResolveModel();
                string args = $"\"{tool}\" \"{inPng}\" --out \"{outPly}\" --mask-background"
                    + (string.IsNullOrWhiteSpace(model) ? "" : $" --model \"{model}\"")
                    + $" --timeout {timeoutSeconds}";
                var psi = new ProcessStartInfo(Python(), args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                report?.Invoke("Running MoGe (minutes on GPU)...");
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    throw new InvalidOperationException("could not start python (GUO_PYTHON)");
                }

                // Slice-wait so cancel kills promptly and the wall clock is
                // enforced here, past the tool's own stage budget (the voice
                // path waits the same way: tool budget inside, Kill outside).
                DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds + 60);
                while (!proc.WaitForExit(500))
                {
                    if (ct.IsCancellationRequested)
                    {
                        Kill(proc);
                        ct.ThrowIfCancellationRequested();
                    }

                    if (DateTime.UtcNow > deadline)
                    {
                        Kill(proc);
                        throw new TimeoutException($"moge tool timed out after {timeoutSeconds}s ({outPly})");
                    }
                }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"moge tool exit {proc.ExitCode}: {Short(stderr.Length > 0 ? stderr : stdout)}");
                }

                if (!File.Exists(outPly))
                {
                    throw new InvalidOperationException($"moge tool produced no PLY ({Short(stdout)})");
                }

                // Inputs and outputs stay under build/moge/ as proof artifacts.
                byte[] ply = File.ReadAllBytes(outPly);
                if (ply.Length == 0)
                {
                    throw new InvalidOperationException("moge tool produced an empty PLY");
                }

                report?.Invoke($"MoGe done ({ply.Length / 1024} KiB): {outPly}");
                return ply;
            }, ct);
        }

        private static string FindRepoRoot()
        {
            string[] bases = { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory };
            foreach (string b in bases)
            {
                try
                {
                    var dir = new DirectoryInfo(b);
                    for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
                    {
                        if (File.Exists(Path.Combine(dir.FullName, "tools", "moge", "moge_to_ply.py")))
                        {
                            return dir.FullName;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        private static void Kill(Process proc)
        {
            try
            {
                proc.Kill();
            }
            catch (Exception)
            {
            }
        }

        private static string Short(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > 200 ? s[..200] + "..." : s).Replace("\n", " ");
    }
}
