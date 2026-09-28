// GUO-owned (ADR-0023): a look, as text.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GUO.Renderer.PostFx
{
    internal sealed class PostFxPass
    {
        public string Shader = "";
        public bool Enabled = true;
        public Dictionary<string, JsonNode> Params = new();
    }

    internal sealed class PostFxBinding
    {
        public int Pass;
        public string Param = "";
        public string From = "";
        public float Scale = 1f;
        public float Offset;
    }

    /// <summary>
    /// { "name", "passes": [ { "shader", "enabled"?, "params": {...} } ],
    ///   "bindings": [ { "pass", "param", "from", "scale"?, "offset"? } ] }.
    /// A param is a number, a bool, or [r, g, b, a] for a colour. The empty
    /// preset is Classic.
    /// </summary>
    internal sealed class PostFxPreset
    {
        public string Name = "Classic";
        public string Description = "";
        public List<PostFxPass> Passes = new();
        public List<PostFxBinding> Bindings = new();

        /// <summary>Where it came from: "res://..." or a file in the user folder; null when unsaved.</summary>
        public string Source;

        public bool IsClassic => Passes.TrueForAll(p => !p.Enabled);

        public static PostFxPreset Classic() => new() { Name = "Classic", Description = "Everything off: 1:1 with ClassicUO." };

        public static PostFxPreset Parse(string json, string source)
        {
            JsonNode root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var preset = new PostFxPreset
            {
                Name = root?["name"]?.GetValue<string>() ?? "Unnamed",
                Description = root?["description"]?.GetValue<string>() ?? "",
                Source = source,
            };

            if (root?["passes"] is JsonArray passes)
            {
                foreach (JsonNode node in passes)
                {
                    var pass = new PostFxPass
                    {
                        Shader = node?["shader"]?.GetValue<string>() ?? "",
                        Enabled = node?["enabled"]?.GetValue<bool>() ?? true,
                    };

                    if (node?["params"] is JsonObject ps)
                    {
                        foreach (var kv in ps)
                        {
                            pass.Params[kv.Key] = kv.Value?.DeepClone();
                        }
                    }

                    preset.Passes.Add(pass);
                }
            }

            if (root?["bindings"] is JsonArray bindings)
            {
                foreach (JsonNode node in bindings)
                {
                    preset.Bindings.Add(new PostFxBinding
                    {
                        Pass = node?["pass"]?.GetValue<int>() ?? 0,
                        Param = node?["param"]?.GetValue<string>() ?? "",
                        From = node?["from"]?.GetValue<string>() ?? "",
                        Scale = node?["scale"]?.GetValue<float>() ?? 1f,
                        Offset = node?["offset"]?.GetValue<float>() ?? 0f,
                    });
                }
            }

            return preset;
        }

        public string ToJson()
        {
            var passes = new JsonArray();
            foreach (PostFxPass p in Passes)
            {
                var ps = new JsonObject();
                foreach (var kv in p.Params)
                {
                    ps[kv.Key] = kv.Value?.DeepClone();
                }

                passes.Add(new JsonObject { ["shader"] = p.Shader, ["enabled"] = p.Enabled, ["params"] = ps });
            }

            var bindings = new JsonArray();
            foreach (PostFxBinding b in Bindings)
            {
                bindings.Add(new JsonObject
                {
                    ["pass"] = b.Pass, ["param"] = b.Param, ["from"] = b.From, ["scale"] = b.Scale, ["offset"] = b.Offset,
                });
            }

            var root = new JsonObject
            {
                ["name"] = Name, ["description"] = Description, ["passes"] = passes, ["bindings"] = bindings,
            };
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        public PostFxPreset Clone() => Parse(ToJson(), Source);
    }
}
