// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GUO.IO.Audio
{
    /// <summary>Legacy loose-file voice profile metadata and manual mappings.
    /// Runtime playback consumes prepared packs; this class never synthesizes.</summary>
    internal static partial class VoiceManager
    {
        private static string _profileName =
            System.Environment.GetEnvironmentVariable("GUO_VOICE") ?? "";

        /// <summary>
        /// Voice profile name (GUO_VOICE when set). Otherwise the default
        /// legacy voice mapping. This metadata never triggers synthesis or playback.
        /// </summary>
        public static string ProfileName
        {
            get => string.IsNullOrWhiteSpace(_profileName) ? DefaultVoice() ?? "" : _profileName;
            set => _profileName = value ?? "";
        }

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

        /// <summary>
        /// Which legacy voice profile maps to a mobile: voices.json rules by serial, name
        /// or body, first match wins; else a stable bank pick by serial;
        /// else the "default" voice (or
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
            // and no rule file grows unbounded).
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
        /// Explicit voices.json mappings only (serials/names/bodies).
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

    }
}
