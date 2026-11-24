// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): ADR-0044 + ADR-0037. Styled water inside the Depth World.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

namespace GUO.Renderer.DepthWorld;

/// <summary>
/// The Depth World's water variant of the ordered fragment shader, generated
/// from the canvas path's own <c>uo_water.gdshaderinc</c> so the two cannot
/// drift: every function body is the canvas text, compiled as GLSL. Only the
/// declarations change. Each non-sampler <c>uniform</c> becomes a global the
/// water branch loads from one vec4 slot of a uniform buffer (set 2, binding
/// 0); the five samplers become set 2 bindings 1 to 5. Classic frames never
/// use this program: they keep the unmodified shader and pipeline.
/// </summary>
internal static class OrderedWorldWater
{
    internal const int Slots = 64; // vec4 slots in the std140 uniform block: 1024 bytes.
    internal const int Bytes = Slots * 16;

    internal enum Kind { Bool, Int, Float, Vec2, Vec3, Vec4 }

    internal readonly record struct Uniform(string Name, Kind Type, int Slot, float[] Default);

    // Samplers, in binding order 1..5. Index 0 of the set is the uniform block.
    internal static readonly string[] Samplers =
    {
        "water_mask", "water_ripples", "water_light_target", "water_surface_buffer", "water_scene_color",
    };

    // Replaces textureSize(water_mask, 0): ImageQuery is outside the RD program
    // contract (Shader capability only). The C# side fills it from the texture.
    internal const string MaskSize = "water_mask_n";

    private static string _fragment;
    private static Uniform[] _uniforms;
    private static Dictionary<string, Uniform> _byName;

    internal static IReadOnlyList<Uniform> Uniforms { get { Build(); return _uniforms; } }

    internal static bool TryGet(string name, out Uniform uniform)
    {
        Build();
        return _byName.TryGetValue(name, out uniform);
    }

    /// <summary>The water fragment for <paramref name="baseFragment"/> (ordered_world_fragment).</summary>
    internal static string Fragment(string baseFragment)
    {
        Build();
        return _fragment ??= Compose(baseFragment);
    }

    private static readonly Regex UniformLine = new(
        @"^\s*uniform\s+(\w+)\s+(\w+)\s*(?::[^=;]*)?(?:=\s*([^;]+))?;\s*(//.*)?$", RegexOptions.Compiled);

    private static string _body;

    private static void Build()
    {
        if (_uniforms != null) return;
        string water = OrderedWorldCapture.ShaderSource("uo_water.gdshaderinc");
        string reflection = OrderedWorldCapture.ShaderSource("speed_reflection.gdshaderinc");
        var uniforms = new List<Uniform>();
        var body = new StringBuilder();
        var samplers = new HashSet<string>(Samplers);
        foreach (string raw in water.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw;
            if (line.TrimStart().StartsWith("#include", StringComparison.Ordinal))
            {
                if (!line.Contains("speed_reflection.gdshaderinc", StringComparison.Ordinal))
                    throw new InvalidOperationException("Depth World water: unexpected include " + line.Trim());
                body.Append(reflection.Replace("// SPDX-License-Identifier: BSD-2-Clause", "")).Append('\n');
                continue;
            }
            Match m = UniformLine.Match(line);
            if (m.Success)
            {
                string type = m.Groups[1].Value, name = m.Groups[2].Value;
                if (type == "sampler2D")
                {
                    if (!samplers.Contains(name)) throw new InvalidOperationException("Depth World water: unknown sampler " + name);
                    continue; // Declared by the generated set-2 bindings.
                }
                Kind kind = type switch
                {
                    "bool" => Kind.Bool, "int" => Kind.Int, "float" => Kind.Float,
                    "vec2" => Kind.Vec2, "vec3" => Kind.Vec3, "vec4" => Kind.Vec4,
                    _ => throw new InvalidOperationException("Depth World water: unsupported uniform type " + type),
                };
                uniforms.Add(new Uniform(name, kind, uniforms.Count, Defaults(kind, m.Groups[3].Value)));
                body.Append(type).Append(' ').Append(name).Append(";\n");
                continue;
            }
            if (line.Contains("uniform ", StringComparison.Ordinal) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                throw new InvalidOperationException("Depth World water: unparsed uniform " + line.Trim());
            body.Append(line).Append('\n');
        }
        uniforms.Add(new Uniform(MaskSize, Kind.Float, uniforms.Count, new[] { (float)GUO.Renderer.Water.WaterMask.Size, 0, 0, 0 }));
        if (uniforms.Count > Slots) throw new InvalidOperationException("Depth World water: uniform block too small.");
        string text = body.ToString();
        const string sizeQuery = "float(textureSize(water_mask, 0).x)";
        if (CountOf(text, sizeQuery) != 1) throw new InvalidOperationException("Depth World water: mask size query changed.");
        text = text.Replace(sizeQuery, MaskSize);
        if (text.Contains("textureSize", StringComparison.Ordinal))
            throw new InvalidOperationException("Depth World water: textureSize is outside the RD program contract.");
        // Godot's shading language allows identifiers GLSL 4.50 reserves (a local named `patch`).
        text = Regex.Replace(text, @"\b(patch|sample|filter|input|output|common|partition|active|subroutine|buffer|shared|resource|superp|noise)\b",
            "water_kw_$1");
        var byName = new Dictionary<string, Uniform>();
        foreach (var u in uniforms) byName.Add(u.Name, u);
        _body = text;
        _byName = byName;
        _uniforms = uniforms.ToArray();
    }

    private static float[] Defaults(Kind kind, string text)
    {
        var values = new float[4];
        text = text.Trim();
        if (text.Length == 0) return values;
        if (text == "true") { values[0] = 1; return values; }
        if (text == "false") return values;
        var numbers = new List<float>();
        foreach (Match n in Regex.Matches(text, @"-?\d+(?:\.\d*)?(?:[eE][-+]?\d+)?"))
            numbers.Add(float.Parse(n.Value, CultureInfo.InvariantCulture));
        if (numbers.Count == 1 && kind is Kind.Vec2 or Kind.Vec3 or Kind.Vec4)
            for (int i = 0; i < 4; i++) values[i] = numbers[0];
        else
            for (int i = 0; i < Math.Min(4, numbers.Count); i++) values[i] = numbers[i];
        return values;
    }

    private static int CountOf(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Replace1(string text, string what, string with)
    {
        if (CountOf(text, what) != 1) throw new InvalidOperationException("Depth World water: anchor not found once: " + what.Trim());
        return text.Replace(what, with);
    }

    private static string Compose(string baseFragment)
    {
        var decl = new StringBuilder();
        decl.Append("// ---- generated from uo_water.gdshaderinc (ADR-0037), Depth World water variant ----\n");
        decl.Append("#define CURRENT_RENDERER 1\n#define RENDERER_COMPATIBILITY 2\n");
        decl.Append("layout(set = 2, binding = 0, std140) uniform WaterBlock { vec4 water_u[").Append(Slots).Append("]; } water_block;\n");
        decl.Append("layout(set = 2, binding = 1) uniform sampler2D water_mask;\n");
        decl.Append("layout(set = 2, binding = 2) uniform sampler2D water_ripples;\n");
        decl.Append("layout(set = 2, binding = 3) uniform sampler2D water_light_target;\n");
        decl.Append("layout(set = 2, binding = 4) uniform sampler2D water_surface_buffer;\n");
        decl.Append("layout(set = 2, binding = 5) uniform sampler2D water_scene_color;\n");
        decl.Append("float ").Append(MaskSize).Append(";\n");
        decl.Append(_body);
        decl.Append("void water_load() {\n");
        foreach (var u in _uniforms)
        {
            string slot = "water_block.water_u[" + u.Slot.ToString(CultureInfo.InvariantCulture) + "]";
            decl.Append("    ").Append(u.Name).Append(" = ").Append(u.Type switch
            {
                Kind.Bool => slot + ".x > 0.5",
                Kind.Int => "int(round(" + slot + ".x))",
                Kind.Float => slot + ".x",
                Kind.Vec2 => slot + ".xy",
                Kind.Vec3 => slot + ".xyz",
                _ => slot,
            }).Append(";\n");
        }
        decl.Append("}\n// ---- end generated ----\n");

        string text = baseFragment.Replace("\r\n", "\n");
        text = Replace1(text, "layout(location = 0) in vec2 uv;", decl + "layout(location = 0) in vec2 uv;");
        // The canvas core's water decode (uo_hue_core.gdshaderinc, uo_shade), same order.
        text = Replace1(text, "    int mode = raw_mode & 0x3F;\n",
            "    int mode = raw_mode & 0x3F;\n"
            + "    bool water = (raw_mode & WATER_BIT) != 0;\n"
            + "    bool water_floor = water && (raw_mode & WATER_FLOOR_BIT) != 0;\n"
            + "    if (water_floor) mode -= WATER_FLOOR_BIT;\n");
        // The canvas evaluates SCREEN_UV * (1 / SCREEN_PIXEL_SIZE); same expression here.
        text = Replace1(text, "    float radius = pc.row1.w;\n",
            "    if (water) {\n"
            + "        water_load();\n"
            + "        vec2 water_pixel_size = 1.0 / pc.screen.xy;\n"
            + "        vec2 water_px = (gl_FragCoord.xy * water_pixel_size) * (1.0 / water_pixel_size);\n"
            + "        if (water_floor) {\n"
            + "            if (water_model > 0) color.rgb = water_floor_shade(color.rgb, water_px);\n"
            + "        } else if (water_model > 0) {\n"
            + "            color.rgb = water_shade(color.rgb, water_px);\n"
            + "            alpha = water_alpha(water_seam_alpha(alpha, water_px), water_px);\n"
            + "        }\n"
            + "    }\n"
            + "    float radius = pc.row1.w;\n");
        return text;
    }

    /// <summary>
    /// Packs the batcher's water uniforms (UltimaBatcher2D.SetWaterUniform's
    /// record) into the std140 block. Values never set keep the include's own
    /// defaults, exactly as a canvas material would.
    /// </summary>
    internal static void Pack(Dictionary<string, Variant> values, float[] block, Texture2D mask)
    {
        Build();
        Array.Clear(block);
        foreach (var u in _uniforms)
        {
            int at = u.Slot * 4;
            float[] d = u.Default;
            block[at] = d[0]; block[at + 1] = d[1]; block[at + 2] = d[2]; block[at + 3] = d[3];
            if (!values.TryGetValue(u.Name, out Variant v)) continue;
            switch (v.VariantType)
            {
                case Variant.Type.Bool: block[at] = v.AsBool() ? 1f : 0f; break;
                case Variant.Type.Int: block[at] = v.AsInt32(); break;
                case Variant.Type.Float: block[at] = v.AsSingle(); break;
                case Variant.Type.Vector2: { var x = v.AsVector2(); block[at] = x.X; block[at + 1] = x.Y; break; }
                case Variant.Type.Vector3: { var x = v.AsVector3(); block[at] = x.X; block[at + 1] = x.Y; block[at + 2] = x.Z; break; }
                case Variant.Type.Vector4: { var x = v.AsVector4(); block[at] = x.X; block[at + 1] = x.Y; block[at + 2] = x.Z; block[at + 3] = x.W; break; }
                case Variant.Type.Color: { var x = v.AsColor(); block[at] = x.R; block[at + 1] = x.G; block[at + 2] = x.B; block[at + 3] = x.A; break; }
                case Variant.Type.Nil: break;
                default: throw new InvalidOperationException("Depth World water: unsupported value for " + u.Name + ": " + v.VariantType);
            }
        }
        if (mask != null) block[_byName[MaskSize].Slot * 4] = mask.GetWidth();
    }

    internal static void Set(float[] block, string name, float x, float y = 0, float z = 0, float w = 0)
    {
        int at = _byName[name].Slot * 4;
        block[at] = x; block[at + 1] = y; block[at + 2] = z; block[at + 3] = w;
    }
}
