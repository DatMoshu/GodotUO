// GUO-owned (ADR-0023): the pass stack over the world render target.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace GUO.Renderer.PostFx
{
    /// <summary>
    /// Runs a preset's passes over the world texture and hands back the result,
    /// for <see cref="RenderTargets"/> to draw where upstream draws the world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One SubViewport, the world target's size, drawing the world texture and
    /// then, per enabled pass, a <see cref="BackBufferCopy"/> and a full-size
    /// <see cref="ColorRect"/> whose shader reads the copy
    /// (<c>hint_screen_texture</c>) and writes with <c>blend_disabled</c>. The
    /// passes therefore run in draw order inside a single viewport: no ordering
    /// between viewports to rely on, one target of memory whatever the pass
    /// count, and every pass sees exactly the pass before it, this frame.
    /// Nothing is carried between frames.
    /// </para>
    /// <para>
    /// Classic (no enabled pass and no A/B split) never builds the viewport:
    /// <see cref="Process"/> returns the world texture it was given.
    /// </para>
    /// </remarks>
    internal sealed class PostFxStack
    {
        public static PostFxStack Instance { get; } = new();

        public const int MaxPasses = 8;

        /// <summary>A look's pass cap on phones, handhelds and the web (ADR-0023, section 7).</summary>
        public const int MaxPassesMobile = 4;

        /// <summary>The mobile/web tier: weaker GPUs, where a heavy look runs at half resolution.</summary>
        public static bool MobileTier =>
            System.Environment.GetEnvironmentVariable("GUO_POSTFX_TIER") is string t && t.Length > 0
                ? t == "half"
                : OS.HasFeature("mobile") || OS.HasFeature("web");

        private bool? _fullQuality;

        /// <summary>
        /// Run every look at the world's own resolution. Off by default on the
        /// mobile tier, where a look with a heavy pass (a shader marked
        /// "// postfx: heavy") runs at half resolution and is scaled back up,
        /// nearest, by the same draw that scales the world. Remembered in state.json.
        /// </summary>
        public bool FullQuality
        {
            get => _fullQuality ?? !MobileTier;
            set
            {
                _fullQuality = value;
                _dirty = true;
                SaveState();
            }
        }

        /// <summary>The scale the stack runs at now: 1, or 0.5 for a heavy look on the mobile tier.</summary>
        public float Scale { get; private set; } = 1f;

        private PostFxPreset _preset = PostFxPreset.Classic();

        // The look the player chose, which state.json keeps: not a look shown
        // with remember: false. A quality change saved the look on screen, so
        // the postfx probe's tier step (Ink Outline, then full quality) left
        // every later start on Ink Outline (the Thor, found by GUO3, 2026-09-28).
        private string _remembered = PostFxPreset.Classic().Name;

        /// <summary>
        /// --postfx NAME|off: the look for this run only (off is Classic);
        /// state.json is neither read for it nor written. Perf and smoke runs
        /// pass it so a look a player or a probe saved does not time with them.
        /// </summary>
        public static string RunOverride { get; set; }
        private SubViewport _viewport;

        // The object-id buffer (ADR-0023, section 3): only while an enabled pass
        // declares id_tex. The batcher mirrors the world's sprites into it.
        private SubViewport _idViewport;
        private Node2D _idHost;

        /// <summary>The canvas item the batcher mirrors into; invalid when no pass wants ids.</summary>
        internal Rid IdCanvas => _idHost != null && GodotObject.IsInstanceValid(_idHost) ? _idHost.GetCanvasItem() : default;

        /// <summary>The id buffer's texture, for PostFxProbe; null when no pass wants ids.</summary>
        internal Texture2D IdTexture => _idViewport?.GetTexture();

        /// <summary>The world target the id buffer shadows.</summary>
        internal RenderTarget2D IdFor { get; private set; }
        private Sprite2D _base;
        private readonly List<(PostFxPass pass, ColorRect rect, ShaderMaterial material)> _built = new();
        private ColorRect _split;
        private RenderTarget2D _builtFor;
        private bool _dirty = true;
        private double _nextPoll;
        private object _commandsFor;
        private bool _loadedState;

        /// <summary>0 = off; 0..1 = the share of the width (from the left) shown as Classic.</summary>
        public float Split { get; set; }

        public PostFxPreset Preset => _preset;

        /// <summary>The last world target and the texture handed back for it (for PostFxProbe).</summary>
        internal RenderTarget2D LastWorld { get; private set; }

        internal Texture2D LastOutput { get; private set; }

        public event Action PresetChanged;

        /// <summary>The GPU time of the last frame's post-processing, in ms (0 when Classic).</summary>
        public double LastGpuMs =>
            _viewport != null ? RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport.GetViewportRid()) : 0;

        public void Use(PostFxPreset preset, bool remember = true)
        {
            _preset = preset?.Clone() ?? PostFxPreset.Classic();
            int cap = MobileTier ? MaxPassesMobile : MaxPasses;
            if (_preset.Passes.Count > cap)
            {
                GD.PushWarning($"[GUO] postfx: {_preset.Name} has {_preset.Passes.Count} passes; {cap} run on this device");
                _preset.Passes.RemoveRange(cap, _preset.Passes.Count - cap);
            }

            _dirty = true;
            if (remember)
            {
                _remembered = _preset.Name;
                SaveState();
            }

            GD.Print($"[GUO] postfx: {_preset.Name} ({_preset.Passes.FindAll(p => p.Enabled).Count} pass(es))");
            PresetChanged?.Invoke();
        }

        /// <summary>After a pass is toggled, added or removed in place.</summary>
        public void Rebuild() => _dirty = true;

        /// <summary>Sets one uniform of pass <paramref name="index"/>, now and in the preset.</summary>
        public void SetParam(int index, string name, JsonNode value)
        {
            if (index < 0 || index >= _preset.Passes.Count)
            {
                return;
            }

            _preset.Passes[index].Params[name] = value;
            foreach (var b in _built)
            {
                if (ReferenceEquals(b.pass, _preset.Passes[index]))
                {
                    b.material.SetShaderParameter(name, Coerce(b.material.Shader, name, ToVariant(value)));
                }
            }
        }

        /// <summary>
        /// The texture to draw for the world this frame. <paramref name="light"/>
        /// is offered to passes that declare <c>light_tex</c>.
        /// </summary>
        public Texture2D Process(RenderTarget2D world, RenderTarget2D light)
        {
            Housekeeping();
            LastWorld = world;

            if (world == null || world.IsDisposed)
            {
                LastOutput = world;
                return world;
            }

            bool active = Split > 0f || !_preset.IsClassic;
            if (!active)
            {
                if (_viewport != null)
                {
                    Teardown();
                }

                LastOutput = world.Texture;
                return LastOutput;
            }

            // Rebuilt when the world target is replaced (a resize), so the stack
            // is always created after the target it reads.
            if (_dirty || _viewport == null || !ReferenceEquals(_builtFor, world) ||
                _viewport.Size != Scaled(world))
            {
                Build(world);
            }

            _base.Texture = world.Texture;
            ApplyBindings();
            foreach (var b in _built)
            {
                if (b.material.Shader != null && HasUniform(b.material.Shader, "light_tex"))
                {
                    b.material.SetShaderParameter("light_tex", light?.Texture);
                }

                if (_idViewport != null && b.material.Shader != null && HasUniform(b.material.Shader, "id_tex"))
                {
                    b.material.SetShaderParameter("id_tex", _idViewport.GetTexture());
                }
            }

            if (_split != null)
            {
                var m = (ShaderMaterial)_split.Material;
                m.SetShaderParameter("classic_tex", world.Texture);
                m.SetShaderParameter("split", Split);
            }

            LastOutput = _viewport.GetTexture();
            return LastOutput;
        }

        private void Build(RenderTarget2D world)
        {
            Teardown();
            Node host = world.Parent;
            if (host == null)
            {
                return;
            }

            bool heavy = _preset.Passes.Exists(p => p.Enabled && IsHeavy(PostFxLibrary.Shader(p.Shader)));

            // The id buffer, only when a pass reads it; created before the stack's
            // own viewport, so it renders first. Always the world's full size.
            if (_preset.Passes.Exists(p => p.Enabled && HasUniform(PostFxLibrary.Shader(p.Shader), "id_tex")))
            {
                _idViewport = new SubViewport
                {
                    Size = new Vector2I(world.Width, world.Height),
                    TransparentBg = true,
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                    Disable3D = true,
                    CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
                };
                _idHost = new Node2D();
                _idViewport.AddChild(_idHost);
                host.AddChild(_idViewport);
                IdFor = world;
            }
            Scale = !FullQuality && heavy ? 0.5f : 1f;
            Vector2I size = Scaled(world);

            _viewport = new SubViewport
            {
                Size = size,
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                Disable3D = true,
                CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            };
            // Sampled down nearest at half resolution: every other art pixel.
            _base = new Sprite2D
            {
                Centered = false, TextureFilter = CanvasItem.TextureFilterEnum.Nearest, Scale = new Vector2(Scale, Scale),
            };
            _viewport.AddChild(_base);

            foreach (PostFxPass pass in _preset.Passes)
            {
                if (!pass.Enabled)
                {
                    continue;
                }

                Shader shader = PostFxLibrary.Shader(pass.Shader);
                if (shader == null)
                {
                    GD.PushWarning($"[GUO] postfx: no shader \"{pass.Shader}\"; pass skipped");
                    continue;
                }

                var material = new ShaderMaterial { Shader = shader };
                foreach (var kv in pass.Params)
                {
                    material.SetShaderParameter(kv.Key, Coerce(shader, kv.Key, ToVariant(kv.Value)));
                }

                _viewport.AddChild(new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Viewport });
                var rect = FullRect(size, material);
                _viewport.AddChild(rect);
                _built.Add((pass, rect, material));
            }

            if (Split > 0f)
            {
                _viewport.AddChild(new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Viewport });
                _split = FullRect(size, new ShaderMaterial { Shader = SplitShader() });
                _viewport.AddChild(_split);
            }

            host.AddChild(_viewport);
            RenderingServer.ViewportSetMeasureRenderTime(_viewport.GetViewportRid(), true);
            _builtFor = world;
            _dirty = false;
        }

        private Vector2I Scaled(RenderTarget2D world) =>
            Scale >= 1f ? new Vector2I(world.Width, world.Height)
                : new Vector2I(Math.Max(1, (int)Math.Ceiling(world.Width * Scale)), Math.Max(1, (int)Math.Ceiling(world.Height * Scale)));

        internal static bool IsHeavy(Shader shader) => shader != null && shader.Code.Contains("// postfx: heavy");

        private static ColorRect FullRect(Vector2I size, Material material) => new()
        {
            Size = size,
            Material = material,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        private void Teardown()
        {
            _idViewport?.QueueFree();
            _idViewport = null;
            _idHost = null;
            IdFor = null;
            _viewport?.QueueFree();
            _viewport = null;
            _base = null;
            _split = null;
            _built.Clear();
            _builtFor = null;
        }

        private void ApplyBindings()
        {
            foreach (PostFxBinding b in _preset.Bindings)
            {
                if (b.Pass < 0 || b.Pass >= _preset.Passes.Count)
                {
                    continue;
                }

                float? v = PostFxState.Get(b.From);
                if (v == null)
                {
                    continue;
                }

                float value = b.Offset + b.Scale * v.Value;
                foreach (var built in _built)
                {
                    if (ReferenceEquals(built.pass, _preset.Passes[b.Pass]))
                    {
                        var range = PostFxUniforms.Find(built.material.Shader, b.Param);
                        if (range != null)
                        {
                            value = Math.Clamp(value, range.Min, range.Max);
                        }

                        built.material.SetShaderParameter(b.Param, Coerce(built.material.Shader, b.Param, value));
                    }
                }
            }
        }

        /// <summary>
        /// The saved look read in, once: on the first world frame, or sooner
        /// for a screen that shows it before there is a world (the pre-game
        /// Settings' Screen effects line).
        /// </summary>
        public void EnsureLoaded()
        {
            // A World tab can close and reopen without reloading this stack or its preset.
            PostFxMenu.Install();
            if (!_loadedState)
            {
                _loadedState = true;
                if (RunOverride != null)
                {
                    UseRunOverride();
                }
                else
                {
                    LoadState();
                }

                PostFxLibrary.Changed += OnFileChanged;
            }
        }

        private void Housekeeping()
        {
            EnsureLoaded();

            double now = GUO.Time.Ticks / 1000.0;
            if (now >= _nextPoll)
            {
                _nextPoll = now + 1.0;
                PostFxLibrary.Poll();
            }

            var world = GUO.Client.Game?.UO?.World;
            if (world != null && !ReferenceEquals(world, _commandsFor))
            {
                _commandsFor = world;
                world.CommandManager.Register("postfx", PostFxCommand.Run);
            }
        }

        private void OnFileChanged(string path)
        {
            // A changed shader of this preset, or this preset's own file: reload.
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            bool mine = string.Equals(_preset.Source, path, StringComparison.OrdinalIgnoreCase) ||
                        _preset.Passes.Exists(p => string.Equals(p.Shader, name, StringComparison.OrdinalIgnoreCase));
            if (!mine)
            {
                return;
            }

            GD.Print($"[GUO] postfx: {System.IO.Path.GetFileName(path)} changed; reloading");
            if (path.EndsWith(".json"))
            {
                PostFxPreset fresh = PostFxLibrary.Find(_preset.Name);
                if (fresh != null)
                {
                    Use(fresh, remember: false);
                    return;
                }
            }

            _dirty = true;
        }

        private void LoadState()
        {
            string file = PostFxLibrary.StateFile;
            if (file == null || !File.Exists(file))
            {
                return;
            }

            try
            {
                JsonNode state = JsonNode.Parse(File.ReadAllText(file));
                if (state?["full_quality"] is JsonValue fq)
                {
                    _fullQuality = fq.GetValue<bool>();
                }

                string name = state?["active"]?.GetValue<string>();
                PostFxPreset p = name != null ? PostFxLibrary.Find(name) : null;
                if (p != null)
                {
                    _remembered = p.Name;
                }

                if (p != null && !p.IsClassic)
                {
                    Use(p, remember: false);
                }
            }
            catch (Exception e)
            {
                GD.PushWarning($"[GUO] postfx: {file}: {e.Message}");
            }
        }

        private void UseRunOverride()
        {
            bool off = RunOverride.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                       RunOverride.Equals("classic", StringComparison.OrdinalIgnoreCase);
            PostFxPreset p = off ? PostFxPreset.Classic() : PostFxLibrary.Find(RunOverride);
            if (p == null)
            {
                GD.PushWarning($"[GUO] postfx: --postfx {RunOverride}: no such look; Classic");
                p = PostFxPreset.Classic();
            }

            Use(p, remember: false);
            GD.Print($"[GUO] postfx: look for this run: {_preset.Name} (--postfx; state.json not read or written)");
        }

        // --postfx: nothing this run changes is saved over the player's look.
        // A probe that saves into a folder of its own lifts it for its check.
        private static bool SuppressSave => RunOverride != null;

        private void SaveState()
        {
            string file = PostFxLibrary.StateFile;
            if (file == null || SuppressSave)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                var state = new JsonObject { ["active"] = _remembered };
                if (_fullQuality.HasValue)
                {
                    state["full_quality"] = _fullQuality.Value;
                }

                File.WriteAllText(file, state.ToJsonString());
            }
            catch (Exception e)
            {
                GD.PushWarning($"[GUO] postfx: could not save {file}: {e.Message}");
            }
        }

        /// <summary>JSON numbers are floats; an int uniform needs an int (and a bool uniform a bool).</summary>
        internal static Variant Coerce(Shader shader, string name, Variant value)
        {
            PostFxUniform u = PostFxUniforms.Find(shader, name);
            if (u == null || value.VariantType != Variant.Type.Float)
            {
                return value;
            }

            return u.Type switch
            {
                Variant.Type.Int => (int)Math.Round(value.AsSingle()),
                Variant.Type.Bool => value.AsSingle() != 0f,
                _ => value,
            };
        }

        internal static bool HasUniform(Shader shader, string name) => PostFxUniforms.Has(shader, name);

        internal static Variant ToVariant(JsonNode node)
        {
            switch (node)
            {
                case null:
                    return default;
                case JsonArray a when a.Count is 3 or 4:
                    return new Godot.Color(F(a[0]), F(a[1]), F(a[2]), a.Count == 4 ? F(a[3]) : 1f);
                case JsonArray a when a.Count == 2:
                    return new Vector2(F(a[0]), F(a[1]));
                case JsonValue v when v.GetValueKind() == JsonValueKind.True || v.GetValueKind() == JsonValueKind.False:
                    return v.GetValue<bool>();
                case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                    return v.GetValue<float>();
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    string s = v.GetValue<string>();
                    // A texture by path: res:// or a file beside the presets.
                    return LoadTexture(s) is Texture2D t ? t : s;
                default:
                    return default;
            }

            static float F(JsonNode n) => n?.GetValue<float>() ?? 0f;
        }

        private static Texture2D LoadTexture(string path)
        {
            if (path.StartsWith("res://"))
            {
                return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
            }

            // A file beside the presets: the player's folder or an installed pack,
            // and never outside it.
            foreach (string folder in PostFxLibrary.SearchFolders())
            {
                string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, path));
                if (full.StartsWith(System.IO.Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    var img = Image.LoadFromFile(full);
                    return img != null ? ImageTexture.CreateFromImage(img) : null;
                }
            }

            return null;
        }

        private static Shader _splitShader;

        private static Shader SplitShader() => _splitShader ??= new Shader
        {
            Code = @"shader_type canvas_item;
render_mode blend_disabled;
// The A/B split (ADR-0023): Classic left of the line, the effect right of it.
uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D classic_tex : filter_nearest;
uniform float split = 0.5;
void fragment() {
    vec4 fx = texture(source, SCREEN_UV);
    vec4 classic = texture(classic_tex, SCREEN_UV);
    float line = step(abs(SCREEN_UV.x - split), SCREEN_PIXEL_SIZE.x * 0.5);
    COLOR = SCREEN_UV.x < split ? classic : fx;
    COLOR = mix(COLOR, vec4(1.0, 0.85, 0.3, 1.0), line);
}",
        };
    }
}
