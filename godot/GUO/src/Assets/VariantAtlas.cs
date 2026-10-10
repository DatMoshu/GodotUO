// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Workspace;

namespace GUO.Assets
{
    /// <summary>Which archive a variant stands in for (filenames carry it).</summary>
    internal enum VariantKind
    {
        Static,
        Land,
    }

    /// <summary>
    /// GUO's own variant art atlas (PNG files, no mul/uop, no id slots):
    /// <c>&lt;dir&gt;/&lt;theme&gt;.theme.json</c> maps graphics to images and
    /// <c>&lt;dir&gt;/&lt;theme&gt;/static_0x0E77.png</c> holds the pixels.
    /// Originals are never touched: objects keep their graphic and draw the
    /// variant texture instead while a themed zone covers them.
    /// Main thread only: PNG decoding makes Godot textures.
    /// </summary>
    internal static class VariantAtlas
    {
        /// <summary>One loaded variant: the texture and its pixel size.</summary>
        internal sealed class VariantImage
        {
            public Godot.Texture2D Texture;
            public int Width;
            public int Height;
        }

        /// <summary>Env override, else the shared workspace's variants folder.</summary>
        public static string ResolveDir()
        {
            string env = Environment.GetEnvironmentVariable("GUO_VARIANT_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            try
            {
                return Path.Combine(GUO.Workspace.Workspace.Root, "variants");
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string ThemePath(string dir, string theme) =>
            Path.Combine(dir ?? "", theme + ".theme.json");

        public static string ImageFile(VariantKind kind, int id) =>
            $"{(kind == VariantKind.Land ? "land" : "static")}_0x{id:X4}.png";

        public static VariantKind KindFromFileName(string file)
        {
            string name = Path.GetFileName(file ?? "").ToLowerInvariant();
            return name.StartsWith("land_") ? VariantKind.Land : VariantKind.Static;
        }

        private static readonly Dictionary<string, Dictionary<(VariantKind, int), VariantImage>> _themes = new();

        /// <summary>
        /// Transient preview images (the StaticStudio "preview in world"
        /// toggle): in-memory only, never saved, winning over themed files
        /// everywhere while set. Main thread only.
        /// </summary>
        private static readonly Dictionary<(VariantKind, int), VariantImage> _preview = new();

        /// <summary>Show an image on a graphic everywhere until cleared. Null image removes it.</summary>
        public static void SetPreview(VariantKind kind, int id, Godot.Image image)
        {
            _preview.Remove((kind, id));
            if (image == null || image.GetWidth() <= 0 || image.GetHeight() <= 0)
            {
                return;
            }

            if (image.GetFormat() != Godot.Image.Format.Rgba8)
            {
                image.Convert(Godot.Image.Format.Rgba8);
            }

            int w = image.GetWidth(), h = image.GetHeight();
            _preview[(kind, id)] = new VariantImage
            {
                Texture = Godot.ImageTexture.CreateFromImage(image),
                Width = w,
                Height = h,
            };
        }

        public static void ClearPreview()
        {
            _preview.Clear();
        }

        /// <summary>Forget everything (tests, world switches).</summary>
        public static void Clear()
        {
            _themes.Clear();
        }

        /// <summary>Load (or reload) one theme's PNGs. Missing files are skipped, never fatal.</summary>
        public static int LoadTheme(string dir, string theme)
        {
            var map = new Dictionary<(VariantKind, int), VariantImage>();
            _themes[theme] = map;
            string path = ThemePath(dir, theme);
            if (!File.Exists(path))
            {
                return 0;
            }

            Theme data;
            try
            {
                data = Theme.Load(path);
            }
            catch (Exception)
            {
                return 0;
            }

            // The file's "name" field may differ from its filename; the
            // resolver looks themes up by the loaded name.
            if (!string.IsNullOrWhiteSpace(data.Name))
            {
                _themes[data.Name] = map;
            }

            string folder = Path.Combine(dir ?? "", theme);
            foreach (ThemeEntry e in data.Entries)
            {
                if (string.IsNullOrWhiteSpace(e.Image))
                {
                    continue;
                }

                string file = Path.Combine(folder, e.Image);
                if (!File.Exists(file))
                {
                    continue;
                }

                VariantImage img = LoadPng(file);
                if (img == null)
                {
                    continue;
                }

                VariantKind kind = KindFromFileName(e.Image);
                foreach (ushort m in e.Match)
                {
                    map[(kind, m)] = img;
                }
            }

            return map.Count;
        }

        /// <summary>Load every theme file in the folder.</summary>
        public static void LoadAll(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return;
            }

            foreach (string path in Directory.GetFiles(dir, "*.theme.json"))
            {
                LoadTheme(dir, Path.GetFileNameWithoutExtension(path));
            }
        }

        private static VariantImage LoadPng(string file)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(file);
                using var image = new Godot.Image();
                if (image.LoadPngFromBuffer(bytes) != Godot.Error.Ok
                    || image.GetWidth() <= 0 || image.GetHeight() <= 0)
                {
                    return null;
                }

                if (image.GetFormat() != Godot.Image.Format.Rgba8)
                {
                    image.Convert(Godot.Image.Format.Rgba8);
                }

                int w = image.GetWidth(), h = image.GetHeight();
                return new VariantImage
                {
                    Texture = Godot.ImageTexture.CreateFromImage(image),
                    Width = w,
                    Height = h,
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The variant for a graphic at its position, or null: first active
        /// theme (in activation order) whose zone covers the spot and whose
        /// images hold the graphic wins.
        /// </summary>
        public static VariantImage Resolve(World world, int x, int y, VariantKind kind, ushort graphic)
        {
            // Previews win over everything (and ignore zones: they are a
            // what-if look at the graphic, wherever it stands).
            if (_preview.TryGetValue((kind, graphic), out VariantImage preview))
            {
                return preview;
            }

            int facet = world?.MapIndex ?? -1;
            foreach (ThemeManager.ActiveTheme a in ThemeManager.Active)
            {
                if (a?.Theme == null || !_themes.TryGetValue(a.Theme.Name, out var map))
                {
                    continue;
                }

                bool inside = false;
                foreach (ThemeZone z in a.Zones)
                {
                    if (z.Contains(facet, x, y))
                    {
                        inside = true;
                        break;
                    }
                }

                if (!inside)
                {
                    continue;
                }

                if (map.TryGetValue((kind, graphic), out VariantImage img))
                {
                    return img;
                }
            }

            return null;
        }

        /// <summary>Persist the active theme/zone list so the client boots with it.</summary>
        public static void SaveActive(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(dir);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartArray();
                    foreach (ThemeManager.ActiveTheme a in ThemeManager.Active)
                    {
                        if (a?.Theme == null || a.Theme.Name == ThemeManager.SkinThemeName)
                        {
                            continue;
                        }

                        // Only atlas themes persist here: legacy world-project
                        // themes (numeric overlay variants) live with their
                        // project and would resolve to nothing on another
                        // machine's client.
                        if (!File.Exists(ThemePath(dir, a.Theme.Name)))
                        {
                            continue;
                        }

                        writer.WriteStartObject();
                        writer.WriteString("theme", a.Theme.Name);
                        writer.WriteStartArray("zones");
                        foreach (ThemeZone z in a.Zones)
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("facet", z.Facet);
                            writer.WriteNumber("x1", z.X1);
                            writer.WriteNumber("y1", z.Y1);
                            writer.WriteNumber("x2", z.X2);
                            writer.WriteNumber("y2", z.Y2);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                File.WriteAllText(Path.Combine(dir, "active.json"),
                    System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Read back what <see cref="SaveActive"/> wrote (null entries skipped).</summary>
        public static List<(string Theme, List<ThemeZone> Zones)> LoadActive(string dir)
        {
            var out_ = new List<(string, List<ThemeZone>)>();
            try
            {
                string path = Path.Combine(dir ?? "", "active.json");
                if (!File.Exists(path))
                {
                    return out_;
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (JsonElement a in doc.RootElement.EnumerateArray())
                {
                    string name = a.TryGetProperty("theme", out JsonElement n) ? n.GetString() ?? "" : "";
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    var zones = new List<ThemeZone>();
                    if (a.TryGetProperty("zones", out JsonElement zs))
                    {
                        foreach (JsonElement z in zs.EnumerateArray())
                        {
                            zones.Add(new ThemeZone
                            {
                                Facet = z.TryGetProperty("facet", out JsonElement f) ? f.GetInt32() : -1,
                                X1 = z.TryGetProperty("x1", out JsonElement x1) ? x1.GetInt32() : 0,
                                Y1 = z.TryGetProperty("y1", out JsonElement y1) ? y1.GetInt32() : 0,
                                X2 = z.TryGetProperty("x2", out JsonElement x2) ? x2.GetInt32() : 0,
                                Y2 = z.TryGetProperty("y2", out JsonElement y2) ? y2.GetInt32() : 0,
                            });
                        }
                    }

                    out_.Add((name, zones));
                }
            }
            catch (Exception)
            {
            }

            return out_;
        }
    }
}
