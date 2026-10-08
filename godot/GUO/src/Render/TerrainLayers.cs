// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace GUO.Renderer
{
    /// <summary>Which side of the terrain a layer paints.</summary>
    internal enum LayerKind
    {
        Underlay,
        Overlay,
    }

    /// <summary>
    /// One generated terrain layer: an image registered to a tile rect.
    /// Underlays draw below everything (the black around dungeons); overlays
    /// draw above the land and below the statics, like a decal.
    /// </summary>
    internal sealed class TerrainLayer
    {
        public string Id = "";
        public string Name = "";
        public LayerKind Kind = LayerKind.Overlay;
        public int Facet;
        public int X0, Y0, X1, Y1;
        public string Image = "";
        public string Prompt = "";
        public float Opacity = 1f;

        public Texture2D Texture;
        public ArrayMesh Mesh;
        public int CenterX, CenterY, CenterZ;
    }

    /// <summary>
    /// All generation defaults in one place (the editor dock reads and writes
    /// these, so the manifest is the settings store, not a second file).
    /// </summary>
    internal sealed class LayerDefaults
    {
        public string ComfyUrl = "";
        public string Checkpoint = "DreamShaper_8_pruned.safetensors";
        public string Prompt = "";
        public string Negative = "blurry, watermark, text, deformed";
        public int Seed = -1;
        public int Steps = 20;
        public float Cfg = 7f;
        public float Denoise = 0.85f;
        public string Sampler = "euler";
        public string Scheduler = "normal";
        public float Tolerance = 12f;
        public float Feather = 2f;
        public float Opacity = 1f;
    }

    /// <summary>
    /// Terrain underlays/overlays staged on disk (GUO_LAYER_DIR, else
    /// %APPDATA%/GUO/layers): layers.json plus underlays/ and overlays/
    /// PNGs. The game reads at boot; the editor dock writes.
    /// </summary>
    internal static class TerrainLayers
    {
        public static string LayerDir()
        {
            string dir = System.Environment.GetEnvironmentVariable("GUO_LAYER_DIR");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }

            return Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "GUO", "layers");
        }

        /// <summary>Tile corner to tile-space pixels, exactly like ChunkMesh land.</summary>
        public static Vector2 TilePx(int x, int y, int z)
        {
            return new Vector2((x - y) * 22 - 22, (x + y) * 22 - (z << 2) - 22);
        }

        /// <summary>TilePx for fractional tile-space coords (the underlay quad's half-integer bounds).</summary>
        public static Vector2 TilePxF(float x, float y, int z)
        {
            return new Vector2((x - y) * 22f - 22f, (x + y) * 22f - (z << 2) - 22f);
        }

        public static List<TerrainLayer> Load(out LayerDefaults defaults)
        {
            defaults = new LayerDefaults();
            var layers = new List<TerrainLayer>();
            try
            {
                string manifest = Path.Combine(LayerDir(), "layers.json");
                if (!File.Exists(manifest))
                {
                    return layers;
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("defaults", out JsonElement d))
                {
                    ReadDefaults(d, defaults);
                }

                if (!root.TryGetProperty("layers", out JsonElement list))
                {
                    return layers;
                }

                foreach (JsonElement e in list.EnumerateArray())
                {
                    var layer = new TerrainLayer
                    {
                        Id = e.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? "" : "",
                        Name = e.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                        Facet = e.TryGetProperty("facet", out JsonElement f) ? f.GetInt32() : 0,
                        X0 = e.TryGetProperty("x0", out JsonElement x0) ? x0.GetInt32() : 0,
                        Y0 = e.TryGetProperty("y0", out JsonElement y0) ? y0.GetInt32() : 0,
                        X1 = e.TryGetProperty("x1", out JsonElement x1) ? x1.GetInt32() : 0,
                        Y1 = e.TryGetProperty("y1", out JsonElement y1) ? y1.GetInt32() : 0,
                        Image = e.TryGetProperty("image", out JsonElement img) ? img.GetString() ?? "" : "",
                        Prompt = e.TryGetProperty("prompt", out JsonElement p) ? p.GetString() ?? "" : "",
                        Opacity = e.TryGetProperty("opacity", out JsonElement o) && o.TryGetSingle(out float op) ? op : 1f,
                    };
                    if (e.TryGetProperty("kind", out JsonElement k)
                        && string.Equals(k.GetString(), "underlay", StringComparison.OrdinalIgnoreCase))
                    {
                        layer.Kind = LayerKind.Underlay;
                    }

                    if (string.IsNullOrEmpty(layer.Id) || string.IsNullOrEmpty(layer.Image))
                    {
                        continue;
                    }

                    layers.Add(layer);
                }
            }
            catch (Exception)
            {
            }

            return layers;
        }

        public static void ReadDefaults(JsonElement d, LayerDefaults into)
        {
            if (d.TryGetProperty("comfyUrl", out JsonElement u)) into.ComfyUrl = u.GetString() ?? "";
            if (d.TryGetProperty("checkpoint", out JsonElement c)) into.Checkpoint = c.GetString() ?? into.Checkpoint;
            if (d.TryGetProperty("prompt", out JsonElement p)) into.Prompt = p.GetString() ?? "";
            if (d.TryGetProperty("negative", out JsonElement n)) into.Negative = n.GetString() ?? into.Negative;
            if (d.TryGetProperty("seed", out JsonElement s) && s.TryGetInt32(out int seed)) into.Seed = seed;
            if (d.TryGetProperty("steps", out JsonElement st) && st.TryGetInt32(out int steps)) into.Steps = steps;
            if (d.TryGetProperty("cfg", out JsonElement cf) && cf.TryGetSingle(out float cfg)) into.Cfg = cfg;
            if (d.TryGetProperty("denoise", out JsonElement dn) && dn.TryGetSingle(out float den)) into.Denoise = den;
            if (d.TryGetProperty("sampler", out JsonElement sm)) into.Sampler = sm.GetString() ?? into.Sampler;
            if (d.TryGetProperty("scheduler", out JsonElement sc)) into.Scheduler = sc.GetString() ?? into.Scheduler;
            if (d.TryGetProperty("tolerance", out JsonElement t) && t.TryGetSingle(out float tol)) into.Tolerance = tol;
            if (d.TryGetProperty("feather", out JsonElement fe) && fe.TryGetSingle(out float fea)) into.Feather = fea;
            if (d.TryGetProperty("opacity", out JsonElement o) && o.TryGetSingle(out float op)) into.Opacity = op;
        }

        public static bool Save(List<TerrainLayer> layers, LayerDefaults defaults)
        {
            try
            {
                string dir = LayerDir();
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(dir, "underlays"));
                Directory.CreateDirectory(Path.Combine(dir, "overlays"));
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("format", 1);
                    writer.WriteStartObject("defaults");
                    writer.WriteString("comfyUrl", defaults.ComfyUrl);
                    writer.WriteString("checkpoint", defaults.Checkpoint);
                    writer.WriteString("prompt", defaults.Prompt);
                    writer.WriteString("negative", defaults.Negative);
                    writer.WriteNumber("seed", defaults.Seed);
                    writer.WriteNumber("steps", defaults.Steps);
                    writer.WriteNumber("cfg", defaults.Cfg);
                    writer.WriteNumber("denoise", defaults.Denoise);
                    writer.WriteString("sampler", defaults.Sampler);
                    writer.WriteString("scheduler", defaults.Scheduler);
                    writer.WriteNumber("tolerance", defaults.Tolerance);
                    writer.WriteNumber("feather", defaults.Feather);
                    writer.WriteNumber("opacity", defaults.Opacity);
                    writer.WriteEndObject();
                    writer.WriteStartArray("layers");
                    foreach (TerrainLayer l in layers)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", l.Id);
                        writer.WriteString("name", l.Name);
                        writer.WriteString("kind", l.Kind == LayerKind.Underlay ? "underlay" : "overlay");
                        writer.WriteNumber("facet", l.Facet);
                        writer.WriteNumber("x0", l.X0);
                        writer.WriteNumber("y0", l.Y0);
                        writer.WriteNumber("x1", l.X1);
                        writer.WriteNumber("y1", l.Y1);
                        writer.WriteString("image", l.Image);
                        writer.WriteString("prompt", l.Prompt);
                        writer.WriteNumber("opacity", l.Opacity);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                File.WriteAllText(Path.Combine(dir, "layers.json"),
                    System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Loads the texture (nearest comes from the project default, which
        /// the pixel art needs). Layer images are AI-generated photos shown
        /// minified, so they also get mipmaps: without them nearest sampling
        /// sparkles along the tile diagonals and reads as gaps. Mipmaps are
        /// per-texture; nothing else changes filter.
        /// </summary>
        public static Texture2D LoadTexture(string dir, TerrainLayer layer)
        {
            try
            {
                string path = Path.Combine(dir, layer.Image);
                if (!File.Exists(path))
                {
                    return null;
                }

                using var image = Image.LoadFromFile(path);
                if (image == null || image.IsEmpty())
                {
                    return null;
                }

                if (image.GetWidth() > 1 && image.GetHeight() > 1)
                {
                    image.GenerateMipmaps();
                }

                return ImageTexture.CreateFromImage(image);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Subtracts a capture from its img2img repaint to make the decal:
        /// pixels the painter left alone go transparent, changed pixels keep
        /// the repaint's colour with an alpha ramp (tolerance..tolerance+feather,
        /// in 0..255 channel units). Sizes must match; same-size captures do.
        /// </summary>
        public static Image ExtractDecal(Image input, Image output, float tolerance, float feather)
        {
            int w = input.GetWidth(), h = input.GetHeight();
            var decal = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
            float ramp = System.Math.Max(0.01f, feather);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color a = input.GetPixel(x, y);
                    Color b = output.GetPixel(x, y);
                    float d = System.Math.Max(
                        System.Math.Max(System.Math.Abs(a.R - b.R), System.Math.Abs(a.G - b.G)),
                        System.Math.Max(System.Math.Abs(a.B - b.B), System.Math.Abs(a.A - b.A))) * 255f;
                    float alpha = System.Math.Min(1f, System.Math.Max(0f, (d - tolerance) / ramp));
                    decal.SetPixel(x, y, new Color(b.R, b.G, b.B, alpha));
                }
            }

            return decal;
        }

        /// <summary>
        /// One quad over the whole tile rect: the union of the tiles'
        /// diamonds, which in tile space is X in [x0+0.5, x1+1.5], Y in
        /// [y0-0.5, y1+0.5]. Corners take 0..1 exactly. A single image on a
        /// single quad: no per-tile cutting, so stitching cannot tear.
        /// Underlays lie flat at the lowest corner (below everything);
        /// overlays lie flat at the highest corner (above the bumps).
        /// Verts come out camera-relative (minus the view offset), exactly
        /// like every other mesh the queue draws.
        /// </summary>
        public static void RefreshMesh(TerrainLayer layer, Func<int, int, int> tileZ, float ox, float oy)
        {
            int x0 = Math.Min(layer.X0, layer.X1), x1 = Math.Max(layer.X0, layer.X1);
            int y0 = Math.Min(layer.Y0, layer.Y1), y1 = System.Math.Max(layer.Y0, layer.Y1);
            int z00 = tileZ(x0, y0), z10 = tileZ(x1, y0), z01 = tileZ(x0, y1), z11 = tileZ(x1, y1);
            int ztop = layer.Kind == LayerKind.Underlay
                ? Math.Min(Math.Min(z00, z10), Math.Min(z01, z11))
                : Math.Max(Math.Max(z00, z10), Math.Max(z01, z11));

            layer.CenterX = (x0 + x1) / 2;
            layer.CenterY = (y0 + y1) / 2;
            layer.CenterZ = ztop;

            Vector2 off = new(ox, oy);
            Vector2 a = TilePxF(x0 + 0.5f, y0 - 0.5f, ztop) - off;
            Vector2 b = TilePxF(x1 + 1.5f, y0 - 0.5f, ztop) - off;
            Vector2 c = TilePxF(x0 + 0.5f, y1 + 0.5f, ztop) - off;
            Vector2 d = TilePxF(x1 + 1.5f, y1 + 0.5f, ztop) - off;
            var color = new Color(1f, 1f, 1f, Math.Max(0f, Math.Min(1f, layer.Opacity)));
            var verts = new Vector3[6];
            var colors = new Color[6];
            var uvs = new Vector2[6];
            verts[0] = new Vector3(a.X, a.Y, 0f);
            verts[1] = new Vector3(b.X, b.Y, 0f);
            verts[2] = new Vector3(c.X, c.Y, 0f);
            verts[3] = new Vector3(b.X, b.Y, 0f);
            verts[4] = new Vector3(d.X, d.Y, 0f);
            verts[5] = new Vector3(c.X, c.Y, 0f);
            for (int i = 0; i < 6; i++)
            {
                colors[i] = color;
            }

            uvs[0] = new Vector2(0f, 0f);
            uvs[1] = new Vector2(1f, 0f);
            uvs[2] = new Vector2(0f, 1f);
            uvs[3] = new Vector2(1f, 0f);
            uvs[4] = new Vector2(1f, 1f);
            uvs[5] = new Vector2(0f, 1f);
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Color] = colors;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            layer.Mesh ??= new ArrayMesh();
            layer.Mesh.ClearSurfaces();
            layer.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }

    }
}
