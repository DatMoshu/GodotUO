// GUO-owned (ADR-0023): what a pass shader exposes, read from the shader itself.

using System.Collections.Generic;
using System.Globalization;
using Godot;
using Godot.Collections;

namespace GUO.Renderer.PostFx
{
    /// <summary>One uniform the menu can show: a slider, a toggle or a colour.</summary>
    internal sealed class PostFxUniform
    {
        public string Name;
        public Variant.Type Type;
        public float Min, Max = 1f, Step = 0.01f;
        public bool IsRange, IsColor;
        public Variant Default;

        public string Label => Name.Replace('_', ' ');
    }

    internal static class PostFxUniforms
    {
        // Filled by the framework, never shown: the inputs, not the knobs.
        private static readonly HashSet<string> Inputs = new() { "source", "light_tex", "classic_tex", "split" };

        private static readonly System.Collections.Generic.Dictionary<ulong, List<PostFxUniform>> _cache = new();

        /// <summary>
        /// The uniforms of a shader, from <see cref="Shader.GetShaderUniformList"/>:
        /// float/int with <c>hint_range</c> become sliders, bools toggles,
        /// <c>source_color</c> vectors colour pickers. Samplers and uniforms without
        /// a usable hint are not listed.
        /// </summary>
        public static List<PostFxUniform> Of(Shader shader)
        {
            if (shader == null)
            {
                return new List<PostFxUniform>();
            }

            ulong key = shader.GetInstanceId() ^ (ulong)shader.Code.GetHashCode();
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var list = new List<PostFxUniform>();
            foreach (Dictionary d in shader.GetShaderUniformList())
            {
                string name = (string)d["name"];
                if (Inputs.Contains(name))
                {
                    continue;
                }

                var type = (Variant.Type)(int)d["type"];
                var hint = (PropertyHint)(int)d["hint"];
                string hintString = (string)d["hint_string"];
                var u = new PostFxUniform
                {
                    Name = name,
                    Type = type,
                    Default = RenderingServer.ShaderGetParameterDefault(shader.GetRid(), name),
                };

                if ((type == Variant.Type.Float || type == Variant.Type.Int) && hint == PropertyHint.Range)
                {
                    string[] parts = hintString.Split(',');
                    u.IsRange = true;
                    u.Min = Parse(parts, 0, 0f);
                    u.Max = Parse(parts, 1, 1f);
                    u.Step = Parse(parts, 2, type == Variant.Type.Int ? 1f : 0.01f);
                }
                else if (type == Variant.Type.Bool)
                {
                }
                else if ((type == Variant.Type.Color) || (type is Variant.Type.Vector3 or Variant.Type.Vector4 &&
                                                          hint == PropertyHint.ColorNoAlpha))
                {
                    u.IsColor = true;
                }
                else
                {
                    continue;
                }

                list.Add(u);
            }

            _cache[key] = list;
            return list;
        }

        public static bool Has(Shader shader, string name)
        {
            if (shader == null)
            {
                return false;
            }

            foreach (Dictionary d in shader.GetShaderUniformList())
            {
                if ((string)d["name"] == name)
                {
                    return true;
                }
            }

            return false;
        }

        public static PostFxUniform Find(Shader shader, string name) => Of(shader).Find(u => u.Name == name);

        private static float Parse(string[] parts, int i, float fallback) =>
            i < parts.Length && float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v
                : fallback;
    }
}
