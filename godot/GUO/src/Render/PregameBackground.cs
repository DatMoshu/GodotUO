// SPDX-License-Identifier: BSD-2-Clause
//
// GUO addition, no upstream counterpart. The login background: what the
// canvas background (ADR-0016) shows before a profile is loaded, chosen in
// the pre-game card's Settings, Screen group. By default it follows the last
// character's profile, as ADR-0016 has it; a choice here replaces that for
// the login screen and the character list only. In the world the profile's
// own background applies, as before.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using GUO.Configuration;

namespace GUO.Renderer
{
    internal static class PregameBackground
    {
        /// <summary>The choice that keeps ADR-0016's behaviour: the last character's background.</summary>
        public const string Follow = "";

        /// <summary>One entry of the list: what the file keeps, and what the player reads.</summary>
        public sealed record Choice(string Key, string Title);

        private sealed class File_
        {
            [JsonPropertyName("login_background")] public string LoginBackground { get; set; } = Follow;
        }

        private const string EmbeddedPrefix = "backgrounds/";

        private static string _key;
        private static List<Choice> _all;

        /// <summary>For the probe: a file of its own instead of the one beside settings.json.</summary>
        public static string PathOverride { get; set; }

        public static string FilePath => PathOverride ?? Path.Combine(Path.GetDirectoryName(Settings.GetSettingsFilepath()) ?? "", "pregame.json");

        /// <summary>
        /// What exists: the last character's (the default), upstream's grey
        /// tile, GUO's wood, every background GUO ships (assets/backgrounds),
        /// and every picture compiled in under Resources/embedded/backgrounds.
        /// </summary>
        public static IReadOnlyList<Choice> All => _all ??= Build();

        private static List<Choice> Build()
        {
            var all = new List<Choice>
            {
                new(Follow, "Your last character's"),
                new("builtin-grey", "Classic grey"),
                new("builtin-wood", "Wood"),
            };

            foreach (BuiltinBackground b in BuiltinBackground.All)
            {
                all.Add(new Choice(BuiltinBackground.Prefix + b.Name, string.IsNullOrWhiteSpace(b.Title) ? b.Name : b.Title));
            }

            foreach (string name in Assembly.GetExecutingAssembly().GetManifestResourceNames()
                         .Where(n => n.StartsWith(EmbeddedPrefix, StringComparison.Ordinal) && n.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                all.Add(new Choice("embedded:" + name.Substring(EmbeddedPrefix.Length), Title(name)));
            }

            return all;
        }

        /// <summary>"britannia-twilight-harbor-hd.png" reads "Britannia twilight harbor".</summary>
        private static string Title(string resource)
        {
            string stem = Path.GetFileNameWithoutExtension(resource.Substring(EmbeddedPrefix.Length));

            if (stem.EndsWith("-hd", StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring(0, stem.Length - 3);
            }

            stem = stem.Replace('-', ' ').Replace('_', ' ').Trim();

            return stem.Length == 0 ? resource : char.ToUpperInvariant(stem[0]) + stem.Substring(1);
        }

        /// <summary>The choice in force (its key); <see cref="Follow"/> when none, or when it no longer exists.</summary>
        public static string Key
        {
            get
            {
                if (_key == null)
                {
                    _key = Read();

                    if (All.All(c => c.Key != _key))
                    {
                        _key = Follow;
                    }
                }

                return _key;
            }
        }

        public static Choice Current => All.FirstOrDefault(c => c.Key == Key) ?? All[0];

        public static void Set(string key)
        {
            _key = All.Any(c => c.Key == key) ? key : Follow;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
                File.WriteAllText(FilePath, JsonSerializer.Serialize(new File_ { LoginBackground = _key }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GUO] login background: could not save {FilePath}: {ex.Message}");
            }

            GD.Print($"[GUO] login background: {Current.Title}");
        }

        /// <summary>The next (or, with -1, the previous) choice, round the list.</summary>
        public static void Step(int by)
        {
            int i = All.ToList().FindIndex(c => c.Key == Key);
            Set(All[((i < 0 ? 0 : i) + by + All.Count) % All.Count].Key);
        }

        /// <summary>
        /// What the canvas background shows before a profile is loaded: null
        /// for "Your last character's" (ADR-0016's own rule applies).
        /// </summary>
        public static CanvasBackgroundSettings? ForLogin()
        {
            string key = Key;

            if (key != _resolvedFor)
            {
                _resolvedFor = key;
                _resolved = Resolve(key);
            }

            return _resolved;
        }

        private static string _resolvedFor;
        private static CanvasBackgroundSettings? _resolved;

        private static CanvasBackgroundSettings? Resolve(string key)
        {
            if (key == Follow)
            {
                return null;
            }

            if (key.StartsWith("embedded:", StringComparison.Ordinal))
            {
                string file = Extract(key.Substring("embedded:".Length));

                return file == null ? null : new CanvasBackgroundSettings(CanvasBackgroundMode.Image, file, 12, false);
            }

            return CanvasBackgroundSettings.Parse(key);
        }

        /// <summary>A compiled-in picture, written once to the user folder, where the image mode reads files.</summary>
        private static string Extract(string name)
        {
            try
            {
                string dir = ProjectSettings.GlobalizePath("user://backgrounds");
                string path = Path.Combine(dir, name);

                if (!File.Exists(path))
                {
                    using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPrefix + name);

                    if (s == null)
                    {
                        return null;
                    }

                    Directory.CreateDirectory(dir);
                    using FileStream f = File.Create(path);
                    s.CopyTo(f);
                }

                return path;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GUO] login background: {name} couldn't be unpacked: {ex.Message}");

                return null;
            }
        }

        private static string Read()
        {
            try
            {
                return File.Exists(FilePath) ? JsonSerializer.Deserialize<File_>(File.ReadAllText(FilePath))?.LoginBackground ?? Follow : Follow;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GUO] login background: {FilePath} unreadable: {ex.Message}");

                return Follow;
            }
        }

        /// <summary>For the probe: forget what was read, so the next use reads the file again.</summary>
        public static void Reload()
        {
            _key = null;
        }
    }
}
