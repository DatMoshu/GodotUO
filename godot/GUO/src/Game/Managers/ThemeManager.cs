// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GUO.Game.Managers
{
    /// <summary>A zone a theme paints: one facet rectangle. Facet -1 is every facet.</summary>
    internal sealed class ThemeZone
    {
        public int Facet = -1;
        public int X1, Y1, X2, Y2;

        public bool Contains(int facet, int x, int y) =>
            (Facet < 0 || Facet == facet)
            && x >= Math.Min(X1, X2) && x <= Math.Max(X1, X2)
            && y >= Math.Min(Y1, Y2) && y <= Math.Max(Y1, Y2);
    }

    /// <summary>
    /// One themed look: the graphics it replaces and what replaces them --
    /// either a variant art id (legacy overlay slots) or a variant-atlas PNG
    /// (preferred: plain files under the theme's folder, no id needed).
    /// </summary>
    internal sealed class ThemeEntry
    {
        public List<ushort> Match = new();
        public ushort Variant;
        public string Image = "";
        public string Splat;
    }

    /// <summary>
    /// A theme file (themes/&lt;name&gt;.theme.json): named looks for graphics,
    /// painted onto zones and multi types. The art itself lives in the world
    /// project's asset overlay at the variant ids (free static slots), exactly
    /// like JarJarGM's variant slots; this file only maps.
    /// </summary>
    internal sealed class Theme
    {
        public const int Format = 1;

        public string Name = "";
        public List<ThemeEntry> Entries = new();
        public List<ushort> Multis = new();
        public string MultiSplat = "";

        public static Theme Load(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            var theme = new Theme { Name = root.TryGetProperty("name", out JsonElement n) ? n.GetString() : Path.GetFileNameWithoutExtension(path) };
            if (root.TryGetProperty("entries", out JsonElement entries))
            {
                foreach (JsonElement e in entries.EnumerateArray())
                {
                    var entry = new ThemeEntry();
                    if (e.TryGetProperty("match", out JsonElement match))
                    {
                        foreach (JsonElement m in match.EnumerateArray())
                        {
                            entry.Match.Add((ushort)m.GetInt32());
                        }
                    }

                    entry.Variant = e.TryGetProperty("variant", out JsonElement v) ? (ushort)v.GetInt32() : (ushort)0;
                    entry.Image = e.TryGetProperty("image", out JsonElement im) ? im.GetString() ?? "" : "";
                    entry.Splat = e.TryGetProperty("splat", out JsonElement s) ? s.GetString() : null;
                    theme.Entries.Add(entry);
                }
            }

            if (root.TryGetProperty("multis", out JsonElement multis))
            {
                foreach (JsonElement m in multis.EnumerateArray())
                {
                    theme.Multis.Add((ushort)m.GetInt32());
                }
            }

            theme.MultiSplat = root.TryGetProperty("multi_splat", out JsonElement ms) ? ms.GetString() : "";
            return theme;
        }

        /// <summary>Write the theme back (the editor's variant saves go through here).</summary>
        public static void Save(string path, Theme theme)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("format", Format);
                writer.WriteString("name", theme?.Name ?? "");
                writer.WriteStartArray("entries");
                if (theme != null)
                {
                    foreach (ThemeEntry e in theme.Entries)
                    {
                        writer.WriteStartObject();
                        writer.WriteStartArray("match");
                        foreach (ushort m in e.Match)
                        {
                            writer.WriteNumberValue(m);
                        }

                        writer.WriteEndArray();
                        if (e.Variant != 0)
                        {
                            writer.WriteNumber("variant", e.Variant);
                        }

                        if (!string.IsNullOrWhiteSpace(e.Image))
                        {
                            writer.WriteString("image", e.Image);
                        }

                        if (!string.IsNullOrEmpty(e.Splat))
                        {
                            writer.WriteString("splat", e.Splat);
                        }

                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndArray();
                writer.WriteStartArray("multis");
                if (theme != null)
                {
                    foreach (ushort m in theme.Multis)
                    {
                        writer.WriteNumberValue(m);
                    }
                }

                writer.WriteEndArray();
                if (!string.IsNullOrEmpty(theme?.MultiSplat))
                {
                    writer.WriteString("multi_splat", theme.MultiSplat);
                }

                writer.WriteEndObject();
            }

            File.WriteAllText(path, System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
        }
    }

    /// <summary>
    /// Client-side themes (Seasons' dynamic sibling): active themes repaint
    /// matching statics and land inside their zones through the same
    /// UpdateGraphicBySeason hook, and skin listed multis with splats.
    /// Everything here is local render state -- graphic swaps and splat
    /// placements -- so no shard traffic is involved, and toggling off
    /// restores every original through that same hook.
    /// </summary>
    internal static class ThemeManager
    {
        internal sealed class ActiveTheme
        {
            public Theme Theme;
            public List<ThemeZone> Zones = new();
        }

        private static readonly List<ActiveTheme> _active = new();

        public static IReadOnlyList<ActiveTheme> Active => _active;

        /// <summary>
        /// Bumped on every activate/deactivate/clear. Objects resolve their
        /// themed variant lazily against it (see GameObject.ResolveTheme),
        /// so arrivals after a theme change still pick it up.
        /// </summary>
        public static int Revision { get; private set; }

        public static void Activate(Theme theme, List<ThemeZone> zones)
        {
            Deactivate(theme.Name);
            _active.Add(new ActiveTheme { Theme = theme, Zones = zones ?? new List<ThemeZone>() });
            Revision++;
        }

        public static void Deactivate(string name)
        {
            _active.RemoveAll(a => a.Theme.Name == name);
            Revision++;
        }

        public static void Clear()
        {
            _active.Clear();
            Revision++;
        }

        /// <summary>
        /// Bump the revision without changing activations (preview toggles).
        /// </summary>
        public static void Touch()
        {
            Revision++;
        }

        /// <summary>
        /// Re-resolves every loaded object (season changes call the same
        /// walk) and dirties the chunk meshes, whose baked quads still show
        /// the pre-theme art otherwise.
        /// </summary>
        public static void Reapply(World world)
        {
            if (world?.Map == null)
            {
                return;
            }

            foreach (var chunk in world.Map.GetUsedChunks())
            {
                if (chunk == null)
                {
                    continue;
                }

                for (int x = 0; x < 8; x++)
                {
                    for (int y = 0; y < 8; y++)
                    {
                        for (GameObjects.GameObject o = chunk.GetHeadObject(x, y); o != null; o = o.TNext)
                        {
                            o.UpdateGraphicBySeason();
                        }
                    }
                }

                chunk.Mesh.IsDirty = true;
            }
        }

        /// <summary>Whether any active theme skins statics with splats (the per-frame walk skips without it).</summary>
        public static bool HasSplatSkins
        {
            get
            {
                foreach (ActiveTheme a in _active)
                {
                    if (a.Theme == null)
                    {
                        continue;
                    }

                    foreach (ThemeEntry e in a.Theme.Entries)
                    {
                        if (!string.IsNullOrEmpty(e.Splat))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        /// <summary>The splat skin for a static at its position, or null.</summary>
        public static string StaticSplat(World world, int x, int y, ushort original)
        {
            int facet = world?.MapIndex ?? -1;
            foreach (ActiveTheme a in _active)
            {
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

                foreach (ThemeEntry e in a.Theme.Entries)
                {
                    if (e.Match.Contains(original) && !string.IsNullOrEmpty(e.Splat))
                    {
                        return e.Splat;
                    }
                }
            }

            return null;
        }

        /// <summary>The themed variant for a static at its position, or null.</summary>
        public static ushort? StaticVariant(World world, int x, int y, ushort original)
        {
            int facet = world?.MapIndex ?? -1;
            foreach (ActiveTheme a in _active)
            {
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

                foreach (ThemeEntry e in a.Theme.Entries)
                {
                    if (e.Match.Contains(original) && e.Variant != 0)
                    {
                        return e.Variant;
                    }
                }
            }

            return null;
        }

        /// <summary>The themed variant for a land tile at its position, or null.</summary>
        public static ushort? LandVariant(World world, int x, int y, ushort original)
        {
            ushort? v = StaticVariant(world, x, y, original);
            return v;
        }

        /// <summary>
        /// The placer gump's replace-all theme: graphic-to-splat skin rules
        /// the user authored in game. Global zone, persisted beside the
        /// staged splats and reloaded on boot.
        /// </summary>
        public const string SkinThemeName = "generated-skins";
        public const string SkinThemeFile = "splat-skins.theme.json";

        /// <summary>Upserts a graphic-to-splat skin rule, globally zoned.</summary>
        public static void SetSplatSkin(ushort graphic, string splat)
        {
            ActiveTheme skin = null;
            foreach (ActiveTheme a in _active)
            {
                if (a.Theme != null && a.Theme.Name == SkinThemeName)
                {
                    skin = a;
                    break;
                }
            }

            if (skin == null)
            {
                skin = new ActiveTheme
                {
                    Theme = new Theme { Name = SkinThemeName },
                    Zones = new List<ThemeZone>
                    {
                        new ThemeZone { Facet = -1, X1 = 0, Y1 = 0, X2 = 16383, Y2 = 16383 },
                    },
                };
                _active.Add(skin);
            }

            foreach (ThemeEntry e in skin.Theme.Entries)
            {
                if (e.Match.Contains(graphic))
                {
                    e.Splat = splat;
                    return;
                }
            }

            skin.Theme.Entries.Add(new ThemeEntry
            {
                Match = new List<ushort> { graphic },
                Splat = splat,
            });
        }

        /// <summary>Removes the skin rule for a graphic, if any.</summary>
        public static void ClearSplatSkin(ushort graphic)
        {
            foreach (ActiveTheme a in _active)
            {
                if (a.Theme == null || a.Theme.Name != SkinThemeName)
                {
                    continue;
                }

                a.Theme.Entries.RemoveAll(e => e.Match.Contains(graphic));
            }
        }

        /// <summary>Writes the skin rules beside the staged splats (null theme = nothing to write).</summary>
        public static bool SaveSkinTheme(string stageDir)
        {
            try
            {
                Theme skin = null;
                foreach (ActiveTheme a in _active)
                {
                    if (a.Theme != null && a.Theme.Name == SkinThemeName && a.Theme.Entries.Count > 0)
                    {
                        skin = a.Theme;
                        break;
                    }
                }

                if (skin == null)
                {
                    return false;
                }

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("format", Theme.Format);
                    writer.WriteString("name", skin.Name);
                    writer.WriteStartArray("entries");
                    foreach (ThemeEntry e in skin.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Splat))
                        {
                            continue;
                        }

                        writer.WriteStartObject();
                        writer.WriteStartArray("match");
                        foreach (ushort m in e.Match)
                        {
                            writer.WriteNumberValue(m);
                        }

                        writer.WriteEndArray();
                        writer.WriteString("splat", e.Splat);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteStartArray("multis");
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                File.WriteAllText(Path.Combine(stageDir, SkinThemeFile),
                    System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Reloads persisted skin rules (no-op when the file is absent).</summary>
        public static void LoadSkinTheme(string stageDir)
        {
            try
            {
                string path = Path.Combine(stageDir ?? "", SkinThemeFile);
                if (!File.Exists(path))
                {
                    return;
                }

                Theme theme = Theme.Load(path);
                if (theme.Entries.Count == 0)
                {
                    return;
                }

                theme.Name = SkinThemeName;
                Activate(theme, new List<ThemeZone>
                {
                    new ThemeZone { Facet = -1, X1 = 0, Y1 = 0, X2 = 16383, Y2 = 16383 },
                });
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Whether a multi of this graphic is skinned in this zone, and by which splat.</summary>
        public static string MultiSplat(World world, int x, int y, ushort graphic)
        {
            int facet = world?.MapIndex ?? -1;
            foreach (ActiveTheme a in _active)
            {
                if (!a.Theme.Multis.Contains(graphic) || string.IsNullOrEmpty(a.Theme.MultiSplat))
                {
                    continue;
                }

                foreach (ThemeZone z in a.Zones)
                {
                    if (z.Contains(facet, x, y))
                    {
                        return a.Theme.MultiSplat;
                    }
                }
            }

            return null;
        }
    }
}
