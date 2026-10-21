// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): the light profile file behind the Realtime lights option.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Godot;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// The shape a light's brightness takes from its source to its radius
    /// (t = distance / radius, 0..1; every curve is 1 at the source and 0 at the radius).
    /// </summary>
    internal enum LightCurve : byte
    {
        /// <summary>A: linear, 1 - t. Even, a little flat.</summary>
        A = 0,

        /// <summary>B: smoothstep. The default; what Realtime lights shipped with.</summary>
        B = 1,

        /// <summary>C: inverse square, windowed to the radius. A hot core: a candle, a brazier.</summary>
        C = 2,

        /// <summary>D: quadratic, 1 - t². Broad and even: a lantern, a lamp post.</summary>
        D = 3,

        /// <summary>E: a plateau to 0.6 of the radius, then a soft edge. Magic, a spell's glow.</summary>
        E = 4,

        /// <summary>A curve the profile file defines by its samples.</summary>
        Custom = 255,
    }

    /// <summary>
    /// What a profile entry says about one light. Every field is optional; an
    /// unset one keeps what the offer computed (radius from the light.mul
    /// sprite, the classic tint, energy 2.2, curve B).
    /// </summary>
    internal readonly struct LightProfile
    {
        public readonly int Radius;           // tiles, 0 = keep
        public readonly float RadiusScale;    // 0 = keep
        public readonly Color? Color;
        public readonly float Energy;         // 0 = keep
        public readonly LightCurve? Curve;
        public readonly int CustomCurve;      // index into the file's custom curves, when Curve is Custom

        public LightProfile(int radius, float radiusScale, Color? color, float energy, LightCurve? curve, int customCurve)
        {
            Radius = radius;
            RadiusScale = radiusScale;
            Color = color;
            Energy = energy;
            Curve = curve;
            CustomCurve = customCurve;
        }

        /// <summary>This entry laid over <paramref name="under"/>: set fields win.</summary>
        public LightProfile Over(in LightProfile under) => new LightProfile(
            Radius > 0 ? Radius : under.Radius,
            RadiusScale > 0 ? RadiusScale : under.RadiusScale,
            Color ?? under.Color,
            Energy > 0 ? Energy : under.Energy,
            Curve ?? under.Curve,
            Curve.HasValue ? CustomCurve : under.CustomCurve);
    }

    /// <summary>
    /// The light profile file (docs/data_formats.md section 45): item graphic and
    /// light id to radius, tint, energy and falloff curve. Read once at start
    /// from <c>GUO_LIGHT_PROFILES</c> or <c>&lt;workspace&gt;/lights/light_profiles.json</c>;
    /// a missing or broken file is the built-in default, which changes nothing.
    /// </summary>
    internal sealed class RealtimeLightProfiles
    {
        public const string EnvKey = "GUO_LIGHT_PROFILES";
        public const string FileName = "light_profiles.json";

        /// <summary>Samples a custom curve keeps (evenly over t = 0..1).</summary>
        public const int CurveSamples = 33;

        private static RealtimeLightProfiles _current;

        private readonly Dictionary<ushort, LightProfile> _items = new();
        private readonly Dictionary<byte, LightProfile> _lights = new();
        private readonly List<float[]> _customCurves = new();
        private LightProfile _default;

        /// <summary>Where the profiles came from: a path, or "built-in".</summary>
        public string Source { get; private set; } = "built-in";

        /// <summary>Why the file was not used, when it was not (missing is not an error).</summary>
        public string Error { get; private set; }

        public int ItemCount => _items.Count;
        public int LightCount => _lights.Count;
        public LightProfile Default => _default;

        /// <summary>The built-in default: nothing overridden.</summary>
        public static RealtimeLightProfiles BuiltIn() => new RealtimeLightProfiles();

        /// <summary>The profiles in use, loaded on first use.</summary>
        public static RealtimeLightProfiles Current
        {
            get
            {
                if (_current == null)
                {
                    _current = LoadDefault();
                }

                return _current;
            }
            set => _current = value;
        }

        /// <summary>Re-read the file (the next frame uses it).</summary>
        public static RealtimeLightProfiles Reload() => _current = LoadDefault();

        /// <summary>The configured file path, or null when none is configured and none sits in the workspace.</summary>
        public static string ResolvePath()
        {
            string env = System.Environment.GetEnvironmentVariable(EnvKey);
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            try
            {
                return Path.Combine(GUO.Workspace.Workspace.Root, "lights", FileName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static RealtimeLightProfiles LoadDefault()
        {
            string path = ResolvePath();
            var p = path != null && File.Exists(path) ? Load(path) : BuiltIn();
            if (p.Error != null)
            {
                GUO.Utility.Logging.Log.Warn($"[realtime-lights] light profiles not used ({p.Error}); built-in default");
            }
            else if (p.Source != "built-in")
            {
                GUO.Utility.Logging.Log.Trace($"[realtime-lights] light profiles {p.Source}: items={p.ItemCount} lights={p.LightCount}");
            }

            return p;
        }

        /// <summary>Read a profile file; any failure is the built-in default with <see cref="Error"/> set.</summary>
        public static RealtimeLightProfiles Load(string path)
        {
            try
            {
                var p = Parse(File.ReadAllText(path));
                p.Source = path;
                return p;
            }
            catch (Exception e)
            {
                var p = BuiltIn();
                p.Error = $"{Path.GetFileName(path)}: {e.Message}";
                return p;
            }
        }

        /// <summary>Parse the JSON; throws on a malformed file (the caller falls back).</summary>
        public static RealtimeLightProfiles Parse(string json)
        {
            var p = new RealtimeLightProfiles();
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("the root is not an object");
            }

            if (root.TryGetProperty("version", out JsonElement version) && version.GetInt32() != 1)
            {
                throw new FormatException($"version {version.GetInt32()} is not 1");
            }

            var curveNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("curves", out JsonElement curves))
            {
                foreach (JsonProperty c in curves.EnumerateObject())
                {
                    curveNames[c.Name] = p._customCurves.Count;
                    p._customCurves.Add(ResampleCurve(c.Value, c.Name));
                }
            }

            if (root.TryGetProperty("default", out JsonElement def))
            {
                p._default = ParseEntry(def, curveNames, "default");
            }

            if (root.TryGetProperty("lights", out JsonElement lights))
            {
                foreach (JsonProperty l in lights.EnumerateObject())
                {
                    int id = ParseId(l.Name);
                    if (id < 0 || id > 255) throw new FormatException($"light id {l.Name} is not 0..255");
                    p._lights[(byte)id] = ParseEntry(l.Value, curveNames, l.Name);
                }
            }

            if (root.TryGetProperty("items", out JsonElement items))
            {
                foreach (JsonProperty it in items.EnumerateObject())
                {
                    int id = ParseId(it.Name);
                    if (id < 0 || id > 0xFFFF) throw new FormatException($"item graphic {it.Name} is not 0..0xFFFF");
                    p._items[(ushort)id] = ParseEntry(it.Value, curveNames, it.Name);
                }
            }

            return p;
        }

        /// <summary>
        /// The profile for a light: the item's entry over the light id's entry
        /// over the file's default. Unset fields stay unset (keep the offer's).
        /// </summary>
        public LightProfile Resolve(ushort itemGraphic, byte lightId)
        {
            LightProfile r = _default;
            if (_lights.TryGetValue(lightId, out LightProfile l)) r = l.Over(in r);
            if (_items.TryGetValue(itemGraphic, out LightProfile i)) r = i.Over(in r);
            return r;
        }

        /// <summary>A custom curve's samples, or null.</summary>
        public float[] CustomCurve(int index) => index >= 0 && index < _customCurves.Count ? _customCurves[index] : null;

        /// <summary>
        /// The curve at t = distance / radius. 1 at the source, 0 at and past the radius.
        /// </summary>
        public static float Evaluate(LightCurve curve, float t, float[] custom = null)
        {
            if (t >= 1f) return 0f;
            if (t <= 0f) return 1f;

            switch (curve)
            {
                case LightCurve.A:
                    return 1f - t;
                case LightCurve.C:
                {
                    // 1 / (1 + 8t²), windowed by (1 - t²)² so it reaches 0 at the radius.
                    float w = 1f - t * t;
                    return w * w / (1f + 8f * t * t);
                }
                case LightCurve.D:
                    return 1f - t * t;
                case LightCurve.E:
                {
                    if (t <= 0.6f) return 1f;
                    float k = 1f - (t - 0.6f) / 0.4f;
                    return k * k * (3f - 2f * k);
                }
                case LightCurve.Custom when custom != null && custom.Length > 1:
                {
                    float f = t * (custom.Length - 1);
                    int i = (int)f;
                    if (i >= custom.Length - 1) return custom[^1];
                    return custom[i] + (custom[i + 1] - custom[i]) * (f - i);
                }
                default:
                {
                    float k = 1f - t;
                    return k * k * (3f - 2f * k);
                }
            }
        }

        private static LightProfile ParseEntry(JsonElement e, Dictionary<string, int> curveNames, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new FormatException($"{name}: entry is not an object");

            int radius = 0;
            float scale = 0f, energy = 0f;
            Color? color = null;
            LightCurve? curve = null;
            int custom = -1;

            foreach (JsonProperty f in e.EnumerateObject())
            {
                switch (f.Name)
                {
                    case "radius":
                        radius = Math.Clamp(f.Value.GetInt32(), 1, RealtimeLightField.MaxRadius);
                        break;
                    case "radiusScale":
                        scale = Math.Clamp(f.Value.GetSingle(), 0.1f, 4f);
                        break;
                    case "energy":
                        energy = Math.Clamp(f.Value.GetSingle(), 0.05f, 8f);
                        break;
                    case "color":
                        color = ParseColor(f.Value.GetString(), name);
                        break;
                    case "curve":
                    {
                        string c = f.Value.GetString() ?? "";
                        if (c.Length == 1 && c[0] is >= 'A' and <= 'E' or >= 'a' and <= 'e')
                        {
                            curve = (LightCurve)(char.ToUpperInvariant(c[0]) - 'A');
                        }
                        else if (curveNames.TryGetValue(c, out custom))
                        {
                            curve = LightCurve.Custom;
                        }
                        else
                        {
                            throw new FormatException($"{name}: curve \"{c}\" is not A..E nor a defined curve");
                        }
                        break;
                    }
                    case "note":
                        break;
                    default:
                        throw new FormatException($"{name}: unknown field \"{f.Name}\"");
                }
            }

            return new LightProfile(radius, scale, color, energy, curve, custom);
        }

        private static float[] ResampleCurve(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2)
            {
                throw new FormatException($"curve {name}: wants an array of at least two samples");
            }

            var src = new float[e.GetArrayLength()];
            int n = 0;
            foreach (JsonElement v in e.EnumerateArray()) src[n++] = Math.Clamp(v.GetSingle(), 0f, 4f);

            // Evenly resampled; the radius end is always 0 (the reach map stops there).
            var dst = new float[CurveSamples];
            for (int i = 0; i < CurveSamples; i++)
            {
                float f = i / (float)(CurveSamples - 1) * (src.Length - 1);
                int k = Math.Min((int)f, src.Length - 2);
                dst[i] = src[k] + (src[k + 1] - src[k]) * (f - k);
            }

            dst[^1] = 0f;
            return dst;
        }

        private static int ParseId(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int h) ? h : -1;
            }

            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? d : -1;
        }

        private static Color ParseColor(string s, string name)
        {
            s = (s ?? "").Trim().TrimStart('#');
            if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                throw new FormatException($"{name}: color wants #RRGGBB");
            }

            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
        }
    }
}
