// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;

namespace GUO.IO.Audio
{
    /// <summary>
    /// Text-to-speech in the player's own voice, client-side and local.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A voice is a profile (tools/comfy/voice.py: sample + auto-transcript
    /// from TEXT2SPEACHWORKINGFASTQWEN) living in GUO_VOICES (else
    /// %APPDATA%/GUO/voices). Anything the player types is spoken in that
    /// voice after it goes out (GameActions.Say hook); distance attenuates the
    /// volume the way footsteps do, so future voices will sit in the world.
    /// The player's own lines play full (distance 0).
    /// </para>
    /// <para>
    /// Threading: synthesis (child process + files) runs on a worker, but
    /// every Godot call happens in <see cref="Pump"/>._Process on the scene
    /// thread. v1 shells tools/comfy/voice.py, which owns the ComfyUI queue
    /// and the ffmpeg conversion to the 22050 Hz mono 16-bit the client plays;
    /// a native HTTP client is the follow-up.
    /// </para>
    /// </remarks>
    internal static partial class VoiceManager
    {
        private sealed class Job
        {
            public string Text = "";
            public int Distance;
            public string Voice = "";
            public int Seed = -1;
        }

        private sealed class Result
        {
            public Job Job;
            public byte[] Pcm;
            public string Error = "";
            public bool Designed;
            public string DesignedVoice = "";
            /// <summary>True when <see cref="Pcm"/> came from the disk cache, not ComfyUI.</summary>
            public bool Cached;
        }

        /// <summary>
        /// Text-to-audio disk cache: repeated lines never regenerate.
        /// Layout: ProfilesDir()/cache/&lt;voice&gt;/&lt;sha256&gt;.wav, shared by
        /// player speech (<see cref="SpeakOwn"/>) and mobile speech
        /// (<see cref="SpeakMob"/>) since both funnel through
        /// <see cref="Synthesize"/>. Keyed by voice-id + clone seed +
        /// profile mtime (epoch seconds) + normalized text, the same
        /// construction as voice.py voice_cache_path so both sides share
        /// entries. A re-enrolled/edited profile changes mtime and orphans
        /// old slots; <see cref="EnforceVoiceCacheLimits"/> evicts them.
        /// </summary>
        private const long VoiceCacheMaxBytes = 500L * 1024 * 1024;
        private const int VoiceCacheMaxFiles = 1000;

        /// <summary>Scene-thread pump: starts synthesis workers, plays finished clips.</summary>
        private sealed partial class Pump : Node
        {
            private Task<Result> _running;

            public override void _Process(double delta)
            {
                if (_running == null)
                {
                    lock (_queue)
                    {
                        if (_queue.Count > 0)
                        {
                            Job job = _queue.Dequeue();
                            if (job.Voice.StartsWith("design:"))
                            {
                                Status = $"designing '{job.Voice}'";
                                _running = Task.Run(() => Design(job));
                            }
                            else
                            {
                                Status = $"working '{Short(job.Text)}'";
                                _running = Task.Run(() => Synthesize(job));
                            }
                        }
                    }
                }
                else if (_running.IsCompleted)
                {
                    Result r = _running.Result;
                    _running = null;
                    if (r.Error.Length == 0 && r.Pcm != null)
                    {
                        Play(r);
                    }
                    else if (r.Error.Length == 0 && r.Designed)
                    {
                        string voice = string.IsNullOrWhiteSpace(r.DesignedVoice) ? r.Job.Voice : r.DesignedVoice;
                        Status = $"designed '{voice}'";
                        GD.Print($"[GUO] voice: {Status}");
                    }
                    else
                    {
                        Status = r.Error;
                    }
                }
            }
        }

        private static readonly Queue<Job> _queue = new();
        private static Pump _pump;

        private static string _profileName =
            System.Environment.GetEnvironmentVariable("GUO_VOICE") ?? "";

        /// <summary>
        /// Voice profile name (GUO_VOICE when set). Otherwise the default
        /// voice, so typed lines speak as soon as any voice is configured
        /// instead of staying silent for an unset variable.
        /// </summary>
        public static string ProfileName
        {
            get => string.IsNullOrWhiteSpace(_profileName) ? DefaultVoice() ?? "" : _profileName;
            set => _profileName = value ?? "";
        }

        /// <summary>What the driver is doing, for probes and the log.</summary>
        public static string Status { get; private set; } = "idle";

        /// <summary>The last spoken line (never a key).</summary>
        public static string LastText { get; private set; } = "";

        /// <summary>Mobiles voiced since startup (the hear-probe reads this).</summary>
        public static int MobSpeaks { get; private set; }

        /// <summary>
        /// Speak a mobile's line (counted separately from the player's). The
        /// serial stabilizes delivery: the same NPC reads the same way every
        /// line, and different NPCs read differently, all from one sample.
        /// </summary>
        public static void SpeakMob(string text, int distance, string voice, uint serial = 0)
        {
            MobSpeaks++;
            Speak(text, distance, voice, serial == 0 ? -1 : StableSeed(serial));
        }

        /// <summary>Deterministic clone seed from a mobile serial (Knuth hash).</summary>
        public static int StableSeed(uint serial)
        {
            return (int)((serial * 2654435761u) % 2147483647u) + 1;
        }

        public static bool Enabled => !string.IsNullOrWhiteSpace(ProfileName);

        public static string ProfilesDir()
        {
            string dir = System.Environment.GetEnvironmentVariable("GUO_VOICES");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }

            return Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "GUO", "voices");
        }

        private static string ToolPath()
        {
            string tool = System.Environment.GetEnvironmentVariable("GUO_VOICE_TOOL");
            if (!string.IsNullOrWhiteSpace(tool))
            {
                return tool;
            }

            string guess = Path.GetFullPath(Path.Combine(ProfilesDir(), "..", "comfy", "voice.py"));
            return File.Exists(guess) ? guess : null;
        }

        private static string Python()
        {
            string py = System.Environment.GetEnvironmentVariable("GUO_PYTHON");
            return string.IsNullOrWhiteSpace(py) ? "python" : py;
        }

        private static void EnsurePump()
        {
            if (_pump != null && GodotObject.IsInstanceValid(_pump))
            {
                return;
            }

            _pump = new Pump { Name = "Voice" };
            AudioHost.RootNode.AddChild(_pump);
        }

        /// <summary>
        /// Design a voice for an NPC (local LLM description + voice-design
        /// workflow sample) and map it to the NPC's serial going forward.
        /// Fire-and-forget; Status tracks it ("designing" then "designed").
        /// The optional prompt is the user's free-text voice type ("gravelly
        /// old blacksmith"): passed to voice.py as --prompt and used as the
        /// design description directly instead of the LLM fallback.
        /// </summary>
        public static void DesignVoice(int serial, string name, int body, string typeName, string prompt = null)
        {
            EnsurePump();
            lock (_queue)
            {
                while (_queue.Count >= 3)
                {
                    _queue.Dequeue();
                }

                _queue.Enqueue(new Job
                {
                    // Job.Text carries the user's free-text design prompt
                    // (kept out of Voice so '|' in the text cannot break the
                    // design:serial|name|body|type parse in Design()).
                    Text = prompt ?? "",
                    Distance = 0,
                    Voice = $"design:{serial}|{name}|{body}|{typeName}",
                });
            }

            Status = $"designing '{name}'";
        }

        /// <summary>Speak the player's own line at full volume. Fire-and-forget.</summary>
        public static void SpeakOwn(string text)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // Shard commands are not speech.
            if (text.StartsWith("["))
            {
                return;
            }

            Enqueue(text, 0, ProfileName);
        }

        /// <summary>Speak a line at a distance in tiles (volume fades past 2, silent past 20).</summary>
        public static void Speak(string text, int distanceTiles, string voice, int seed = -1)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(voice))
            {
                return;
            }

            Enqueue(text, distanceTiles, voice, seed);
        }

        private static void Enqueue(string text, int distance, string voice, int seed = -1)
        {
            EnsurePump();
            lock (_queue)
            {
                while (_queue.Count >= 3)
                {
                    _queue.Dequeue();
                }

                _queue.Enqueue(new Job { Text = text, Distance = distance, Voice = voice, Seed = seed });
            }

            LastText = text;
        }

        /// <summary>
        /// Which voice speaks for a mobile: voices.json rules by serial, name
        /// or body, first match wins; else a stable bank pick by serial with
        /// a per-NPC clone seed; else the "default" voice (or
        /// GUO_DEFAULT_VOICE). Null only when no voices exist at all.
        /// voices.json: {"format":1,"default":"name","rules":[{"serials":[..],
        /// "bodies":[..],"names":[..],"voice":"name"}]} beside the profiles.
        /// </summary>
        public static string ResolveVoice(uint serial, string name, ushort body)
        {
            string rule = ResolveRule(serial, name, body);
            if (!string.IsNullOrWhiteSpace(rule))
            {
                return rule;
            }

            // No rule: deal every unmapped mobile a stable voice from the
            // enrolled bank (deterministic per serial, so relogs keep voices
            // and no rule file grows unbounded). Same sample, distinct seeds.
            try
            {
                List<string> bank = EnrolledVoices();
                if (bank.Count > 0)
                {
                    return bank[(int)(serial % (uint)bank.Count)];
                }
            }
            catch (Exception)
            {
            }

            return DefaultVoice();
        }

        /// <summary>
        /// Explicit voices.json rules only (serials/names/bodies): the design
        /// flow uses this to find mobiles still needing a designed voice.
        /// </summary>
        public static string ResolveRule(uint serial, string name, ushort body)
        {
            try
            {
                string path = Path.Combine(ProfilesDir(), "voices.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("rules", out JsonElement rules))
                {
                    return null;
                }

                foreach (JsonElement rule in rules.EnumerateArray())
                {
                    if (!rule.TryGetProperty("voice", out JsonElement v)
                        || string.IsNullOrWhiteSpace(v.GetString()))
                    {
                        continue;
                    }

                    if (rule.TryGetProperty("serials", out JsonElement ss))
                    {
                        foreach (JsonElement s in ss.EnumerateArray())
                        {
                            if ((uint)s.GetInt64() == serial)
                            {
                                return v.GetString();
                            }
                        }
                    }

                    if (rule.TryGetProperty("bodies", out JsonElement bb))
                    {
                        foreach (JsonElement b in bb.EnumerateArray())
                        {
                            if ((ushort)b.GetInt32() == body)
                            {
                                return v.GetString();
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(name) && rule.TryGetProperty("names", out JsonElement nn))
                    {
                        foreach (JsonElement n in nn.EnumerateArray())
                        {
                            if (string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                            {
                                return v.GetString();
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        /// <summary>
        /// The fallback voice: GUO_DEFAULT_VOICE, else voices.json "default",
        /// else the first enrolled voice (something speaks rather than
        /// nothing). Null only when no voices exist at all.
        /// </summary>
        public static string DefaultVoice()
        {
            string env = System.Environment.GetEnvironmentVariable("GUO_DEFAULT_VOICE");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            try
            {
                string path = Path.Combine(ProfilesDir(), "voices.json");
                if (File.Exists(path))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("default", out JsonElement d)
                        && !string.IsNullOrWhiteSpace(d.GetString()))
                    {
                        return d.GetString();
                    }
                }
            }
            catch (Exception)
            {
            }

            List<string> bank = EnrolledVoices();
            return bank.Count > 0 ? bank[0] : null;
        }

        /// <summary>Enrolled voice names (profiles on disk, minus rules and prompts).</summary>
        public static List<string> EnrolledVoices()
        {
            var names = new List<string>();
            try
            {
                foreach (string path in Directory.EnumerateFiles(ProfilesDir(), "*.json"))
                {
                    string stem = Path.GetFileNameWithoutExtension(path);
                    if (stem == "voices" || stem.EndsWith(".prompt"))
                    {
                        continue;
                    }

                    names.Add(stem);
                }

                names.Sort(StringComparer.Ordinal);
            }
            catch (Exception)
            {
            }

            return names;
        }

        /// <summary>
        /// Maps one mobile serial to a voice, first match wins (replaces any
        /// older rule holding that serial). Null voice clears the mapping.
        /// </summary>
        public static bool AssignVoice(uint serial, string voice)
        {
            try
            {
                string path = Path.Combine(ProfilesDir(), "voices.json");
                JsonDocument doc = null;
                try
                {
                    if (File.Exists(path))
                    {
                        doc = JsonDocument.Parse(File.ReadAllText(path));
                    }
                }
                catch (Exception)
                {
                }

                var rules = new List<Dictionary<string, object>>();
                if (doc != null && doc.RootElement.TryGetProperty("rules", out JsonElement existing))
                {
                    foreach (JsonElement r in existing.EnumerateArray())
                    {
                        bool holds = false;
                        if (r.TryGetProperty("serials", out JsonElement ss))
                        {
                            foreach (JsonElement s in ss.EnumerateArray())
                            {
                                if ((uint)s.GetInt64() == serial)
                                {
                                    holds = true;
                                    break;
                                }
                            }
                        }

                        if (!holds)
                        {
                            var keep = new Dictionary<string, object>();
                            foreach (JsonProperty p in r.EnumerateObject())
                            {
                                keep[p.Name] = ReadJson(p.Value);
                            }

                            rules.Add(keep);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(voice))
                {
                    rules.Insert(0, new Dictionary<string, object>
                    {
                        ["serials"] = new List<object> { serial },
                        ["voice"] = voice,
                    });
                }

                var root = new Dictionary<string, object> { ["format"] = 1, ["rules"] = rules };
                if (doc != null && doc.RootElement.TryGetProperty("default", out JsonElement d))
                {
                    root["default"] = d.GetString();
                }

                Directory.CreateDirectory(ProfilesDir());
                File.WriteAllText(path, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                doc?.Dispose();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Sets the fallback voice for unmapped mobiles (null clears).</summary>
        public static bool SetDefaultVoice(string voice)
        {
            try
            {
                string path = Path.Combine(ProfilesDir(), "voices.json");
                var rules = new List<object>();
                if (File.Exists(path))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                        if (doc.RootElement.TryGetProperty("rules", out JsonElement existing))
                        {
                            foreach (JsonElement r in existing.EnumerateArray())
                            {
                                var keep = new Dictionary<string, object>();
                                foreach (JsonProperty p in r.EnumerateObject())
                                {
                                    keep[p.Name] = ReadJson(p.Value);
                                }

                                rules.Add(keep);
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                var root = new Dictionary<string, object> { ["format"] = 1, ["rules"] = rules };
                if (!string.IsNullOrWhiteSpace(voice))
                {
                    root["default"] = voice;
                }

                Directory.CreateDirectory(ProfilesDir());
                File.WriteAllText(path, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static object ReadJson(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    if (value.TryGetInt64(out long l))
                    {
                        return l;
                    }

                    return value.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Array:
                    {
                        var list = new List<object>();
                        foreach (JsonElement item in value.EnumerateArray())
                        {
                            list.Add(ReadJson(item));
                        }

                        return list;
                    }
                default:
                    return value.GetRawText();
            }
        }

        public static float VolumeFor(int distanceTiles)
        {
            if (distanceTiles <= 2)
            {
                return 1f;
            }

            float v = 1f - (distanceTiles - 2) / 18f;
            return v < 0f ? 0f : v;
        }

        /// <summary>Runs voice.py design for "design:serial|name|body|type" jobs.</summary>
        private static Result Design(Job job)
        {
            var r = new Result { Job = job };
            try
            {
                string[] parts = job.Voice.Split('|');
                if (parts.Length < 4 || !int.TryParse(parts[0].Substring("design:".Length), out int serial))
                {
                    r.Error = $"error: bad design job '{job.Voice}'";
                    return r;
                }

                string tool = ToolPath();
                if (tool == null)
                {
                    r.Error = "error: no voice tool (GUO_VOICE_TOOL)";
                    return r;
                }

                // Design is one fast pass (PERFECT_VOICE_DESIGN): the whole
                // job holds a hard ~2 min ceiling while speaks keep theirs.
                // Timeouts live here AND in the tool call: whichever fires
                // first reports, the other cleans up. Job.Text is the user's
                // free-text voice type; quotes/newlines stripped so the CLI
                // stays one argument, passed as --prompt (voice.py uses it as
                // the design description directly).
                string promptArg = "";
                string cleanPrompt = (job.Text ?? "").Replace("\"", "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (cleanPrompt.Length > 200)
                {
                    cleanPrompt = cleanPrompt[..200];
                }

                if (!string.IsNullOrWhiteSpace(cleanPrompt))
                {
                    promptArg = $" --prompt \"{cleanPrompt}\"";
                }

                var psi = new ProcessStartInfo(Python(),
                    $"\"{tool}\" design --serial {serial} --name \"{parts[1].Replace("\"", "")}\" --body {parts[2]} --type \"{parts[3].Replace("\"", "")}\"{promptArg} --timeout 110")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    r.Error = "error: could not start python (GUO_PYTHON)";
                    return r;
                }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(3 * 60 * 1000))
                {
                    try
                    {
                        proc.Kill();
                    }
                    catch (Exception)
                    {
                    }

                    r.Error = "error: voice design timed out";
                    return r;
                }

                if (proc.ExitCode != 0)
                {
                    r.Error = $"error: voice design exit {proc.ExitCode}: {Short(stderr.Length > 0 ? stderr : stdout)}";
                    return r;
                }

                // The enrolled profile defaults to npc_<serial> (voice.py
                // --voice override is never passed from here); prefer the
                // name the tool actually reports so the gump can auto-assign
                // the exact voice without guessing.
                r.DesignedVoice = $"npc_{serial}";
                string hay = stdout ?? "";
                int di = hay.IndexOf("designed '", StringComparison.Ordinal);
                if (di >= 0)
                {
                    int qs = di + "designed '".Length;
                    int qe = hay.IndexOf('\'', qs);
                    if (qe > qs && qe - qs < 128)
                    {
                        r.DesignedVoice = hay.Substring(qs, qe - qs).Trim();
                    }
                }

                r.Designed = true;
                return r;
            }
            catch (Exception ex)
            {
                r.Error = $"error: {ex.GetType().Name}: {ex.Message}";
                return r;
            }
        }

        private static Result Synthesize(Job job)
        {
            var r = new Result { Job = job };
            try
            {
                string tool = ToolPath();
                if (tool == null)
                {
                    r.Error = "error: no voice tool (GUO_VOICE_TOOL)";
                    return r;
                }

                string profile = Path.Combine(ProfilesDir(), job.Voice + ".json");
                if (!File.Exists(profile))
                {
                    r.Error = $"error: no voice profile '{job.Voice}' in {ProfilesDir()}";
                    return r;
                }

                // Disk cache lookup BEFORE any ComfyUI call: a hit plays
                // immediately with status "cached". Seed -1 (player/random)
                // shares one slot per line: the first delivery wins after that.
                string cachePath = null;
                string normalized = NormalizeSpeech(job.Text);
                if (normalized.Length > 0)
                {
                    cachePath = CachePathFor(job.Voice,
                        CacheKeyFor(job.Voice, normalized, job.Seed, ProfileMtimeSec(profile)));
                    if (File.Exists(cachePath))
                    {
                        try
                        {
                            byte[] hit = ReadMono22050(cachePath);
                            if (hit != null && hit.Length > 0)
                            {
                                try
                                {
                                    File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
                                }
                                catch (Exception)
                                {
                                }

                                r.Pcm = hit;
                                r.Cached = true;
                                return r;
                            }

                            try
                            {
                                File.Delete(cachePath);
                            }
                            catch (Exception)
                            {
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }

                string tmp = Path.Combine(Path.GetTempPath(), $"guo_voice_{Guid.NewGuid():N}");
                var psi = new ProcessStartInfo(Python(),
                    $"\"{tool}\" speak --voice \"{job.Voice}\" --text \"{job.Text.Replace("\"", "")}\" --seed {job.Seed} --out \"{tmp}.mp3\" --wav")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    r.Error = "error: could not start python (GUO_PYTHON)";
                    return r;
                }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(2 * 60 * 1000))
                {
                    try
                    {
                        proc.Kill();
                    }
                    catch (Exception)
                    {
                    }

                    r.Error = "error: voice synthesis timed out";
                    return r;
                }

                if (proc.ExitCode != 0)
                {
                    r.Error = $"error: voice tool exit {proc.ExitCode}: {Short(stderr.Length > 0 ? stderr : stdout)}";
                    return r;
                }

                string wav = tmp + ".wav";
                if (!File.Exists(wav))
                {
                    r.Error = "error: voice tool produced no wav";
                    return r;
                }

                r.Pcm = ReadMono22050(wav);
                try
                {
                    File.Delete(tmp + ".mp3");
                }
                catch (Exception)
                {
                }

                try
                {
                    File.Delete(wav);
                }
                catch (Exception)
                {
                }

                if (r.Pcm == null || r.Pcm.Length == 0)
                {
                    r.Error = "error: voice wav unreadable (want 22050 mono 16-bit)";
                }
                else if (cachePath != null)
                {
                    // Save-after on miss: file the exact playable bytes once
                    // they have passed the 22050Hz-mono gate. Never fatal.
                    r.Cached = false;
                    try
                    {
                        SaveVoiceCache(cachePath, r.Pcm);
                    }
                    catch (Exception)
                    {
                    }
                }

                return r;
            }
            catch (Exception ex)
            {
                r.Error = $"error: {ex.GetType().Name}: {ex.Message}";
                return r;
            }
        }

        /// <summary>Collapse whitespace for cache keys (mirrors voice.py normalize_speech).</summary>
        private static string NormalizeSpeech(string text)
        {
            string t = (text ?? "").Trim();
            return t.Length == 0 ? "" : Regex.Replace(t, @"\s+", " ");
        }

        private static string SanitizeVoice(string voice)
        {
            var sb = new StringBuilder(voice ?? "");
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                sb.Replace(c, '_');
            }

            sb.Replace('/', '_').Replace('\\', '_');
            string s = sb.ToString().Trim();
            return s.Length > 0 ? s : "default";
        }

        /// <summary>Profile mtime in epoch seconds; 0 when unreadable. A rewrite invalidates old slots.</summary>
        private static long ProfileMtimeSec(string profilePath)
        {
            try
            {
                return (long)File.GetLastWriteTimeUtc(profilePath).Subtract(DateTime.UnixEpoch).TotalSeconds;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>SHA-256 over voice + seed + profile-mtime + normalized text (same as voice.py).</summary>
        private static string CacheKeyFor(string voice, string normalized, int seed, long mtimeSec)
        {
            string key = voice + "\x00" + seed + "\x00" + mtimeSec + "\x00" + normalized;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private static string CachePathFor(string voice, string hash) =>
            Path.Combine(ProfilesDir(), "cache", SanitizeVoice(voice), hash + ".wav");

        /// <summary>File the exact playable bytes (44-byte header identical to voice.py wav_voice).</summary>
        private static void SaveVoiceCache(string path, byte[] pcm)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                byte[] header = new byte[44];
                Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
                BitConverter.GetBytes(36 + pcm.Length).CopyTo(header, 4);
                Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
                Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
                BitConverter.GetBytes(16).CopyTo(header, 16);
                BitConverter.GetBytes((short)1).CopyTo(header, 20);
                BitConverter.GetBytes((short)1).CopyTo(header, 22);
                BitConverter.GetBytes(22050).CopyTo(header, 24);
                BitConverter.GetBytes(22050 * 2).CopyTo(header, 28);
                BitConverter.GetBytes((short)2).CopyTo(header, 32);
                BitConverter.GetBytes((short)16).CopyTo(header, 34);
                Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
                BitConverter.GetBytes(pcm.Length).CopyTo(header, 40);
                using var fs = new FileStream(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.None);
                fs.Write(header, 0, header.Length);
                fs.Write(pcm, 0, pcm.Length);
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                EnforceVoiceCacheLimits();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Oldest-first eviction past 500MB / 1000 files. Never fatal.</summary>
        private static void EnforceVoiceCacheLimits()
        {
            try
            {
                string root = Path.Combine(ProfilesDir(), "cache");
                if (!Directory.Exists(root))
                {
                    return;
                }

                List<FileInfo> files = Directory.EnumerateFiles(root, "*.wav", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();
                long total = files.Sum(f => f.Length);
                int i = 0;
                while ((total > VoiceCacheMaxBytes || files.Count - i > VoiceCacheMaxFiles) && i < files.Count)
                {
                    total -= files[i].Length;
                    try
                    {
                        files[i].Delete();
                    }
                    catch (Exception)
                    {
                    }

                    i++;
                }
            }
            catch (Exception)
            {
            }
        }

        private static byte[] ReadMono22050(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length < 44 || raw[0] != 'R' || raw[1] != 'I' || raw[2] != 'F' || raw[3] != 'F')
            {
                return null;
            }

            int at = 12, rate = 0, channels = 0, bits = 0;
            while (at + 8 <= raw.Length)
            {
                string kind = System.Text.Encoding.ASCII.GetString(raw, at, 4);
                int size = BitConverter.ToInt32(raw, at + 4);
                if (size < 0 || at + 8 + size > raw.Length)
                {
                    break;
                }

                if (kind == "fmt " && size >= 16)
                {
                    channels = BitConverter.ToInt16(raw, at + 10);
                    rate = BitConverter.ToInt32(raw, at + 12);
                    bits = BitConverter.ToInt16(raw, at + 22);
                }
                else if (kind == "data")
                {
                    if (channels != 1 || rate != 22050 || bits != 16)
                    {
                        return null;
                    }

                    byte[] body = new byte[size];
                    Buffer.BlockCopy(raw, at + 8, body, 0, size);
                    return body;
                }

                at += 8 + size + (size & 1);
            }

            return null;
        }

        private static AudioStreamPlayer _player;

        private static void Play(Result r)
        {
            float vol = VolumeFor(r.Job.Distance);
            if (vol <= 0f)
            {
                Status = $"done (out of earshot, {r.Job.Distance} tiles)";
                return;
            }

            if (_player == null || !GodotObject.IsInstanceValid(_player))
            {
                _player = AudioHost.CreatePlayer();
            }

            if (_player == null)
            {
                Status = $"ready '{r.Job.Voice}' ({r.Pcm.Length / 2} samples, no audio host)";
                return;
            }

            _player.Stream = new AudioStreamWav
            {
                Data = r.Pcm,
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = 22050,
                Stereo = false,
            };
            _player.VolumeDb = vol >= 1f ? 0f : 20f * MathF.Log10(Math.Max(0.001f, vol));
            _player.Play();
            Status = r.Cached
                ? $"cached '{r.Job.Voice}' ({r.Pcm.Length / 2} samples)"
                : $"playing '{r.Job.Voice}' ({r.Pcm.Length / 2} samples)";
            GD.Print($"[GUO] voice: {Status} -- \"{Short(r.Job.Text)}\"");
        }

        private static string Short(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > 120 ? s[..120] + "..." : s).Replace("\n", " ");
    }
}
