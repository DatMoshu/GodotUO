// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GUO.Renderer
{
    /// <summary>
    /// A staged splat placement: which LOD chain, where on which facet, and the
    /// footprint draft MultiEdit finishes with real floors/walls/doors.
    /// </summary>
    public sealed class SplatPlacement
    {
        public string Name = "";
        public SplatLodChain Chain;
        public int Facet;
        public int X, Y, Z;

        /// <summary>
        /// Model yaw in degrees (default 180: pipeline fronts face the
        /// camera, per the reference mapping).
        /// </summary>
        public float Yaw = 180f;

        /// <summary>
        /// Screen px per model unit (reference default 22: a 2-unit model
        /// covers one 44px tile). Staged per splat from its footprint.
        /// </summary>
        public float Scale = 22f;
        public string Footprint = "";
        public int FootprintCells;

        /// <summary>
        /// Map tiles per splat unit (legacy estimator input; Scale wins when present).
        /// </summary>
        public float TilesPerUnit = 2f;

        /// <summary>
        /// Keep the staged Z instead of grounding to the map height (the
        /// placer gump sets this: explicit placement wins over terrain).
        /// Manifest entries without it ground exactly like before.
        /// </summary>
        public bool LockZ;
    }

    /// <summary>
    /// The staged splats (tools/comfy/stage.py): GUO_SPLAT_STAGE/splats.json
    /// plus the LOD PLYs beside it. CPU only; the layer draws from these.
    /// </summary>
    public static class SplatStage
    {
        private static readonly Dictionary<string, (SplatLodChain Chain, float Scale)> _byName = new();

        /// <summary>
        /// Registers an in-memory chain (in-client generations): same lookup
        /// as manifest entries, gone on relog unless saved.
        /// </summary>
        public static void Register(string name, SplatLodChain chain, float scale)
        {
            if (!string.IsNullOrEmpty(name) && chain != null)
            {
                _byName[name] = (chain, scale);
            }
        }

        /// <summary>Manifest names with a loaded chain, sorted (the placer gump's model list).</summary>
        public static List<string> StagedNames()
        {
            var names = new List<string>(_byName.Keys);
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>
        /// Stages a generated splat: LOD PLY bytes land in splats/ and the
        /// manifest gains the entry (same shape stage.py writes), so the next
        /// Rescan lists it like any staged model. Returns the entry name.
        /// </summary>
        public static string SaveNewSplat(
            string stageDir, string baseName, System.Collections.Generic.List<byte[]> lods,
            SplatPlacement p, string prompt)
        {
            var splatsDir = Path.Combine(stageDir, "splats");
            Directory.CreateDirectory(splatsDir);
            var rel = new System.Collections.Generic.List<string>();
            for (int i = 0; i < lods.Count; i++)
            {
                string file = $"{baseName}_lod{i}.ply";
                File.WriteAllBytes(Path.Combine(splatsDir, file), lods[i]);
                rel.Add($"splats/{file}");
            }

            Assets.SplatSet full = Assets.SplatPlyParser.Parse(lods[0], baseName);
            string manifest = Path.Combine(stageDir, "splats.json");
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest));
            var entry = new System.Text.Json.Nodes.JsonObject
            {
                ["lods"] = new System.Text.Json.Nodes.JsonArray(rel.ConvertAll(r =>
                    (System.Text.Json.Nodes.JsonNode)r).ToArray()),
                ["count"] = full.Gaussians.Length,
                ["bounds_min"] = new System.Text.Json.Nodes.JsonArray(
                    full.BoundsMin.X, full.BoundsMin.Y, full.BoundsMin.Z),
                ["bounds_max"] = new System.Text.Json.Nodes.JsonArray(
                    full.BoundsMax.X, full.BoundsMax.Y, full.BoundsMax.Z),
                ["tool"] = "guo-client",
                ["workflow"] = "MultisMaker1",
                ["licence"] = "CC0-1.0",
                ["note"] = "in-client generated splat from a static's artwork plus a prompt; no client art input",
                ["placement"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["facet"] = p.Facet,
                    ["x"] = p.X,
                    ["y"] = p.Y,
                    ["z"] = p.Z,
                    ["yaw"] = p.Yaw,
                    ["lock_z"] = true,
                },
                ["scale"] = p.Scale,
            };
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                entry["prompt"] = prompt;
            }

            root["splats"][baseName] = entry;
            File.WriteAllText(manifest, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return baseName;
        }

        /// <summary>
        /// Writes a gump placement back into the manifest (placement + yaw +
        /// scale), so it survives relog. Only the named entry is touched.
        /// </summary>
        public static bool SavePlacement(string stageDir, string name, SplatPlacement p)
        {
            try
            {
                string manifest = Path.Combine(stageDir, "splats.json");
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!doc.RootElement.TryGetProperty("splats", out JsonElement splats)
                    || !splats.TryGetProperty(name, out _))
                {
                    return false;
                }

                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest));
                var entry = root["splats"][name];
                var at = new System.Text.Json.Nodes.JsonObject
                {
                    ["facet"] = p.Facet,
                    ["x"] = p.X,
                    ["y"] = p.Y,
                    ["z"] = p.Z,
                    ["yaw"] = p.Yaw,
                    ["lock_z"] = p.LockZ,
                };
                entry["placement"] = at;
                entry["scale"] = p.Scale;
                File.WriteAllText(manifest, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A staged splat's chain and px/unit scale by its manifest name (themes use this).</summary>
        public static bool TryGetSplat(string name, out SplatLodChain chain, out float scale)
        {
            if (name != null && _byName.TryGetValue(name, out var e))
            {
                chain = e.Chain;
                scale = e.Scale;
                return true;
            }

            chain = null;
            scale = 22f;
            return false;
        }
        public static string ResolveDir()
        {
            string env = Environment.GetEnvironmentVariable("GUO_SPLAT_STAGE");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            {
                return env;
            }

            return null;
        }

        public static List<SplatPlacement> Load(string stageDir)
        {
            _byName.Clear();
            var out_ = new List<SplatPlacement>();
            if (string.IsNullOrWhiteSpace(stageDir))
            {
                return out_;
            }

            string manifest = Path.Combine(stageDir, "splats.json");
            if (!File.Exists(manifest))
            {
                return out_;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("splats", out JsonElement splats))
            {
                return out_;
            }

            foreach (JsonProperty splat in splats.EnumerateObject())
            {
                var paths = new List<string>();
                foreach (JsonElement lod in splat.Value.GetProperty("lods").EnumerateArray())
                {
                    string rel = lod.GetString().Replace('/', Path.DirectorySeparatorChar);
                    string full = Path.Combine(stageDir, rel);
                    if (File.Exists(full))
                    {
                        paths.Add(full);
                    }
                }

                if (paths.Count == 0)
                {
                    continue;
                }

                var chain = new SplatLodChain { Levels = new Assets.SplatSet[paths.Count] };
                for (int i = 0; i < paths.Count; i++)
                {
                    chain.Levels[i] = Assets.SplatPlyParser.Parse(paths[i]);
                    if (i == 0)
                    {
                        chain.BoundsMin = chain.Levels[0].BoundsMin;
                        chain.BoundsMax = chain.Levels[0].BoundsMax;
                    }
                }

                var p = new SplatPlacement { Name = splat.Name, Chain = chain };
                if (splat.Value.TryGetProperty("placement", out JsonElement at))
                {
                    p.Facet = at.GetProperty("facet").GetInt32();
                    p.X = at.GetProperty("x").GetInt32();
                    p.Y = at.GetProperty("y").GetInt32();
                    p.Z = at.GetProperty("z").GetInt32();
                    if (at.TryGetProperty("yaw", out JsonElement yw) && yw.TryGetSingle(out float yaw))
                    {
                        p.Yaw = yaw;
                    }

                    if (at.TryGetProperty("lock_z", out JsonElement lz) && lz.ValueKind == JsonValueKind.True)
                    {
                        p.LockZ = true;
                    }
                }
                else
                {
                    p.Facet = -1;
                }

                // Legacy tiles_per_unit (tiles per model unit) converts at
                // 11 px/tile; manifests written by stage.py carry "scale"
                // (px per model unit) directly.
                if (splat.Value.TryGetProperty("tiles_per_unit", out JsonElement tpu)
                    && tpu.TryGetSingle(out float k) && k > 0f)
                {
                    p.TilesPerUnit = k;
                    p.Scale = k * 11f;
                }

                if (splat.Value.TryGetProperty("scale", out JsonElement sc)
                    && sc.TryGetSingle(out float s) && s > 0f)
                {
                    p.Scale = s;
                }

                if (splat.Value.TryGetProperty("footprint", out JsonElement fp))
                {
                    string rel = fp.GetString().Replace('/', Path.DirectorySeparatorChar);
                    p.Footprint = Path.Combine(stageDir, rel);
                    p.FootprintCells = CountFootprintCells(p.Footprint);
                    if (!splat.Value.TryGetProperty("scale", out _)
                        && !splat.Value.TryGetProperty("tiles_per_unit", out _))
                    {
                        p.Scale = EstimateScale(p.Footprint, chain);
                    }
                }

                _byName[splat.Name] = (chain, p.Scale);
                out_.Add(p);
            }

            return out_;
        }

        private static int CountFootprintCells(string draft)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(draft));
                return doc.RootElement.GetProperty("components").GetArrayLength();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static float EstimateScale(string draft, SplatLodChain chain)
        {
            // Screen px per model unit: footprint tiles * 11 (a 2-unit
            // model covers one 44px tile at the reference default).
            // Kept for manifests with neither scale nor tiles_per_unit.
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(draft));
                int tiles = 4;
                if (doc.RootElement.TryGetProperty("size", out JsonElement size)
                    && size.GetArrayLength() == 2)
                {
                    tiles = Math.Max(size[0].GetInt32(), size[1].GetInt32());
                }

                return Math.Max(11f, tiles * 11f);
            }
            catch (Exception)
            {
                return 22f;
            }
        }
    }
}
