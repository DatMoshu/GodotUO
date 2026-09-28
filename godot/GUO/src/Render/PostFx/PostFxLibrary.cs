// GUO-owned (ADR-0023): where presets and shaders come from.

using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using FileAccess = Godot.FileAccess;

namespace GUO.Renderer.PostFx
{
    /// <summary>
    /// Built-in presets and shaders in res://postfx/, the player's own in the
    /// client home's postfx/ folder (a same-named user file wins). Shaders
    /// are compiled once and cached; user files are watched for changes by
    /// their write time (<see cref="Poll"/>).
    /// </summary>
    internal static class PostFxLibrary
    {
        public const string BuiltinShaders = "res://postfx/shaders";
        public const string BuiltinPresets = "res://postfx/presets";

        /// <summary>The client home's postfx/ folder; set by the bootstrap. Null means built-ins only.</summary>
        public static string UserFolder { get; set; }

        private static readonly Dictionary<string, (Shader shader, DateTime stamp, string path)> _shaders = new();
        private static readonly Dictionary<string, DateTime> _presetStamps = new();

        public static event Action<string> Changed;

        /// <summary>
        /// Where the player's looks can come from, first match wins: their own
        /// folder, then installed Store packs of kind "postfx" (read only).
        /// </summary>
        public static List<string> SearchFolders()
        {
            var folders = new List<string>();
            if (UserFolder != null)
            {
                folders.Add(UserFolder);
            }

            try
            {
                folders.AddRange(GUO.Store.StoreOptions.InstalledPostFxFolders());
            }
            catch (Exception e)
            {
                GD.PushWarning($"[GUO] postfx: store packs not read: {e.Message}");
            }

            return folders;
        }

        private static readonly Dictionary<string, Shader> _registered = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A shader by name from code, ahead of every file (the probe's identity pass).</summary>
        public static void Register(string name, Shader shader) => _registered[name] = shader;

        public static Shader Shader(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (_registered.TryGetValue(name, out Shader registered))
            {
                return registered;
            }

            string user = null;
            foreach (string folder in SearchFolders())
            {
                string candidate = System.IO.Path.Combine(folder, name + ".gdshader");
                if (File.Exists(candidate))
                {
                    user = candidate;
                    break;
                }
            }

            if (user != null)
            {
                DateTime stamp = File.GetLastWriteTimeUtc(user);
                if (_shaders.TryGetValue(name, out var c) && c.path == user && c.stamp == stamp)
                {
                    return c.shader;
                }

                var shader = new Shader { Code = File.ReadAllText(user) };
                _shaders[name] = (shader, stamp, user);
                return shader;
            }

            string builtin = $"{BuiltinShaders}/{name}.gdshader";
            if (_shaders.TryGetValue(name, out var b) && b.path == builtin)
            {
                return b.shader;
            }

            if (!ResourceLoader.Exists(builtin))
            {
                return null;
            }

            var s = GD.Load<Shader>(builtin);
            _shaders[name] = (s, DateTime.MinValue, builtin);
            return s;
        }

        public static IEnumerable<string> ShaderNames()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in DirAccess.GetFilesAt(BuiltinShaders))
            {
                // An exported pack lists "x.gdshader.remap"; the loader resolves it.
                string n = f.Replace(".remap", "");
                if (n.EndsWith(".gdshader"))
                {
                    names.Add(n[..^".gdshader".Length]);
                }
            }

            foreach (string folder in SearchFolders())
            {
                if (Directory.Exists(folder))
                {
                    foreach (string f in Directory.GetFiles(folder, "*.gdshader"))
                    {
                        names.Add(System.IO.Path.GetFileNameWithoutExtension(f));
                    }
                }
            }

            return names;
        }

        /// <summary>Every preset by name: Classic first, then built-ins, then the player's (which win).</summary>
        public static List<PostFxPreset> Presets()
        {
            var byName = new Dictionary<string, PostFxPreset>(StringComparer.OrdinalIgnoreCase)
            {
                ["Classic"] = PostFxPreset.Classic(),
            };

            foreach (string f in DirAccess.GetFilesAt(BuiltinPresets))
            {
                if (!f.EndsWith(".json"))
                {
                    continue;
                }

                string path = $"{BuiltinPresets}/{f}";
                TryAdd(byName, FileAccess.GetFileAsString(path), path);
            }

            // Store packs first, then the player's own folder, so theirs win.
            List<string> folders = SearchFolders();
            folders.Reverse();
            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (string f in Directory.GetFiles(folder, "*.json"))
                {
                    string file = System.IO.Path.GetFileName(f);
                    if (file.Equals("state.json", StringComparison.OrdinalIgnoreCase) ||
                        file.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TryAdd(byName, File.ReadAllText(f), f);
                }
            }

            var list = new List<PostFxPreset> { byName["Classic"] };
            byName.Remove("Classic");
            var rest = new List<PostFxPreset>(byName.Values);
            rest.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            list.AddRange(rest);
            return list;
        }

        public static PostFxPreset Find(string name)
        {
            return Presets().Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static void TryAdd(Dictionary<string, PostFxPreset> into, string json, string path)
        {
            try
            {
                PostFxPreset p = PostFxPreset.Parse(json, path);
                into[p.Name] = p;
            }
            catch (Exception e)
            {
                GD.PushWarning($"[GUO] postfx: {path} is not a preset: {e.Message}");
            }
        }

        /// <summary>Saves a preset into the user folder as name.json; returns the path.</summary>
        public static string Save(PostFxPreset preset)
        {
            if (UserFolder == null)
            {
                throw new InvalidOperationException("no user folder for presets");
            }

            Directory.CreateDirectory(UserFolder);
            string safe = string.Concat(preset.Name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
            string path = System.IO.Path.Combine(UserFolder, (safe.Length > 0 ? safe : "preset") + ".json");
            File.WriteAllText(path, preset.ToJson());
            preset.Source = path;
            _presetStamps[path] = File.GetLastWriteTimeUtc(path);
            return path;
        }

        /// <summary>
        /// Checks the user folder for changed files; raises <see cref="Changed"/>
        /// with the file name. Cheap: a directory listing and write times.
        /// </summary>
        public static void Poll()
        {
            if (UserFolder == null || !Directory.Exists(UserFolder))
            {
                return;
            }

            foreach (string f in Directory.GetFiles(UserFolder))
            {
                if (!(f.EndsWith(".json") || f.EndsWith(".gdshader")))
                {
                    continue;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(f);
                if (_presetStamps.TryGetValue(f, out DateTime was) && was == stamp)
                {
                    continue;
                }

                bool first = !_presetStamps.ContainsKey(f);
                _presetStamps[f] = stamp;
                if (!first)
                {
                    Changed?.Invoke(f);
                }
            }
        }

        public static string StateFile => UserFolder != null ? System.IO.Path.Combine(UserFolder, "state.json") : null;
    }
}
