// GUO-owned (ADR-0023): the effects menu, a Godot card over the game.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;
using UoTheme = GUO.Input.Touch.UoTheme;

namespace GUO.Renderer.PostFx
{
    /// <summary>
    /// Screen effects: a preset picker, the passes with a toggle each, sliders
    /// made from each shader's own uniform hints, an A/B split against Classic,
    /// and "save as preset". Opened from Options (Video) and with Ctrl+Shift+E
    /// (unless a macro has that key).
    /// </summary>
    /// <remarks>
    /// A card of Godot Controls on its own CanvasLayer, above the game and never
    /// under an effect (effects only touch the world target). It is built in the
    /// UO style (docs/ui/uo_godot_style.md) from <see cref="UoTheme"/>: the stone
    /// frame, the parchment for each pass, marble plates, UO's own check boxes,
    /// slider and font, in art pixels, scaled by a whole number
    /// (<see cref="UoTheme.PixelScale"/>) and sampled nearest.
    ///
    /// The guide's cards draw into a SubViewport and push pointer events in by
    /// hand; this one scales its control tree directly on the CanvasLayer, which
    /// gives the same whole-pixel result and lets Godot deliver its own input.
    /// While it is open, <see cref="OwnsInput"/> tells the game controller to
    /// leave the events over it to the card, as it does for the Store window.
    /// </remarks>
    internal sealed partial class PostFxMenu : Node
    {
        /// <summary>The card's width in art pixels.</summary>
        private const float Width = 300f;

        private static PostFxMenu _instance;

        private CanvasLayer _layer;
        private PanelContainer _card;
        private bool _artTheme;
        private VBoxContainer _passes;
        private OptionButton _presets;
        private CheckBox _compare, _fullQuality;
        private HSlider _split;
        private LineEdit _saveName;
        private Label _subtitle, _cost;
        private List<PostFxPreset> _presetList = new();
        private bool _syncing;
        private double _costTimer;
        private int _scale = 1;

        public static bool IsOpen => _instance != null && GodotObject.IsInstanceValid(_instance) && _instance._layer.Visible;

        /// <summary>Makes sure the menu node exists (for the hotkey); cheap after the first call.</summary>
        public static void Install()
        {
            if (_instance != null && GodotObject.IsInstanceValid(_instance))
            {
                return;
            }

            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                return;
            }

            _instance = new PostFxMenu { Name = "PostFxMenu" };
            tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        }

        public static void Toggle()
        {
            Install();
            if (_instance == null)
            {
                return;
            }

            if (IsOpen)
            {
                _instance.Close();
            }
            else
            {
                _instance.CallDeferred(MethodName.Open);
            }
        }

        /// <summary>For GameController._Input: an event the card should have, not the game.</summary>
        public static bool OwnsInput(InputEvent e)
        {
            if (!IsOpen)
            {
                return false;
            }

            var me = _instance;

            // Our own copy of a finger, being pushed in below: the card's.
            if (me._finger.Pushing)
            {
                return true;
            }

            // A finger on the card is the left button (FingerAsMouse): its
            // controls take the mouse only, and a tap fell through to the game.
            if (e is InputEventScreenTouch or InputEventScreenDrag)
            {
                return me._finger.Take(e, new Rect2(me._card.GlobalPosition, me._card.Size * me._card.Scale), me.GetViewport());
            }

            if (e is InputEventMouse mouse)
            {
                // The card is scaled: its rect on screen is its size times the scale.
                return new Rect2(me._card.GlobalPosition, me._card.Size * me._card.Scale).HasPoint(mouse.Position);
            }

            if (e is InputEventKey)
            {
                // Typing a preset name goes to the text box.
                return me._saveName.HasFocus();
            }

            return false;
        }

        private readonly GUO.Input.Touch.FingerAsMouse _finger = new();

        public override void _Ready()
        {
            _layer = new CanvasLayer { Layer = 95, Visible = false };
            AddChild(_layer);
            _card = new PanelContainer
            {
                Theme = UoTheme.Theme,
                CustomMinimumSize = new Vector2(Width, 0),
                // Rule 7: gump art and the bitmap font, drawn nearest.
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            };
            _layer.AddChild(_card);
            Build();
            PostFxStack.Instance.PresetChanged += () => { if (IsOpen) CallDeferred(MethodName.Refresh); };
        }

        public override void _Input(InputEvent e)
        {
            if (e is InputEventKey { Pressed: true, Echo: false } k && k.Keycode == Key.E && k.CtrlPressed && k.ShiftPressed
                && (IsOpen || !MacroOwnsHotkey(k.AltPressed)))
            {
                Toggle();
                GetViewport().SetInputAsHandled();
            }
            else if (IsOpen && e is InputEventKey { Pressed: true, Keycode: Key.Escape } && !_saveName.HasFocus())
            {
                Close();
                GetViewport().SetInputAsHandled();
            }
        }

        /// <summary>
        /// Whether the player has a macro on Ctrl+Shift+E: the key is then the
        /// macro's, as it is in ClassicUO, and the menu opens from Options only
        /// (review P3). The open menu still takes it, to close.
        /// </summary>
        internal static bool MacroOwnsHotkey(bool alt) =>
            GUO.Client.Game?.UO?.World?.Macros?.FindMacro(GUO.Platform.Sdl.SDL.SDL_Keycode.SDLK_E, alt, true, true) != null;

        public override void _Process(double delta)
        {
            if (!IsOpen)
            {
                return;
            }

            // Top right, within the window; the layout is in art pixels.
            Vector2 screen = GetViewport().GetVisibleRect().Size / _scale;
            float maxH = Math.Max(160f, screen.Y - 16f);
            Vector2 min = _card.GetCombinedMinimumSize();
            _card.Size = new Vector2(Math.Max(Width, min.X), Math.Min(Math.Max(min.Y, maxH * 0.8f), maxH));
            _card.Position = new Vector2(Math.Max(4f, screen.X - _card.Size.X - 8f), 8f) * _scale;

            _costTimer -= delta;
            if (_costTimer <= 0)
            {
                _costTimer = 0.5;

                // Opened before the gump art was ready: swap the flat stand-in for the art.
                if (!_artTheme && UoTheme.Ready)
                {
                    _card.Theme = UoTheme.Theme;
                    _artTheme = true;
                }

                double ms = PostFxStack.Instance.LastGpuMs;
                _cost.Text = PostFxStack.Instance.Preset.IsClassic && PostFxStack.Instance.Split <= 0f
                    ? "Classic: nothing added to the frame"
                    : $"{ms:0.00} ms of GPU a frame";
            }
        }

        private void Open()
        {
            // The theme is the art once the gumps are loaded; pick it up and the scale now.
            _artTheme = UoTheme.Ready;
            _card.Theme = UoTheme.Theme;
            _scale = UoTheme.PixelScale;
            _card.Scale = new Vector2(_scale, _scale);
            Refresh();
            _layer.Visible = true;
        }

        private void Close()
        {
            _finger.Reset();
            _layer.Visible = false;
            _saveName.ReleaseFocus();
        }

        // --- building -----------------------------------------------------------------

        private void Build()
        {
            // Spacing from the guide: 4 art px between controls, 6 between groups.
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 6);
            _card.AddChild(col);

            var head = new HBoxContainer();
            var titles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            titles.AddThemeConstantOverride("separation", 0);
            titles.AddChild(UoTheme.Label("Screen effects", UoTheme.Heading, 2));
            _subtitle = Lbl("", UoTheme.Muted, wrap: true);
            titles.AddChild(_subtitle);
            head.AddChild(titles);
            var close = UoTheme.Button("Close");
            GUO.Input.Glyphs.InputGlyphs.TooltipFollows(close, "Close", GUO.Input.PadAction.Cancel);
            close.Pressed += Close;
            head.AddChild(close);
            col.AddChild(head);
            col.AddChild(new HSeparator());

            _presets = new OptionButton
            {
                FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(Width - 32f, UoTheme.ButtonHeight),
            };
            _presets.ItemSelected += i =>
            {
                if (!_syncing && i >= 0 && i < _presetList.Count)
                {
                    PostFxStack.Instance.Use(_presetList[(int)i]);
                }
            };
            col.AddChild(_presets);

            _compare = new CheckBox { Text = "Compare with Classic" };
            _compare.Toggled += on =>
            {
                if (_syncing)
                {
                    return;
                }

                PostFxStack.Instance.Split = on ? (float)Math.Max(0.05, _split.Value) : 0f;
                PostFxStack.Instance.Rebuild();
                _split.Editable = on;
            };
            col.AddChild(_compare);
            _split = Slider(0.05, 0.95, 0.01, 0.5);
            _split.TooltipText = "Classic is left of the line, the effect right of it";
            _split.ValueChanged += v =>
            {
                if (!_syncing && _compare.ButtonPressed)
                {
                    PostFxStack.Instance.Split = (float)v;
                }
            };
            col.AddChild(_split);

            _fullQuality = new CheckBox
            {
                Text = "Full resolution",
                TooltipText = "Off: looks with a heavy pass (glow, outline) run at half resolution, for weaker GPUs",
            };
            _fullQuality.Toggled += on =>
            {
                if (!_syncing)
                {
                    PostFxStack.Instance.FullQuality = on;
                }
            };
            col.AddChild(_fullQuality);

            col.AddChild(new HSeparator());
            var scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, 200), SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            _passes = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _passes.AddThemeConstantOverride("separation", 6);
            scroll.AddChild(_passes);
            col.AddChild(scroll);

            col.AddChild(new HSeparator());
            var saveRow = new HBoxContainer();
            saveRow.AddThemeConstantOverride("separation", 4);
            _saveName = new LineEdit
            {
                PlaceholderText = "Name your look", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(100, UoTheme.ButtonHeight),
            };
            saveRow.AddChild(_saveName);
            var save = UoTheme.Button("Save look");
            save.Pressed += SaveAs;
            saveRow.AddChild(save);
            col.AddChild(saveRow);

            _cost = Lbl("", UoTheme.Muted);
            _cost.TooltipText = "Ctrl+Shift+E opens and closes this; -postfx in chat does it by command";
            col.AddChild(_cost);
        }

        private void Refresh()
        {
            _syncing = true;
            try
            {
                PostFxStack stack = PostFxStack.Instance;
                _presetList = PostFxLibrary.Presets();
                _presets.Clear();
                int selected = 0;
                for (int i = 0; i < _presetList.Count; i++)
                {
                    _presets.AddItem(_presetList[i].Name);
                    if (string.Equals(_presetList[i].Name, stack.Preset.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = i;
                    }
                }

                if (!_presetList.Exists(p => string.Equals(p.Name, stack.Preset.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _presets.AddItem(stack.Preset.Name + " (unsaved)");
                    selected = _presets.ItemCount - 1;
                    _presetList.Add(stack.Preset);
                }

                _presets.Select(selected);
                _subtitle.Text = string.IsNullOrEmpty(stack.Preset.Description) ? stack.Preset.Name : stack.Preset.Description;
                _compare.ButtonPressed = stack.Split > 0f;
                _fullQuality.ButtonPressed = stack.FullQuality;
                _split.Editable = stack.Split > 0f;
                if (stack.Split > 0f)
                {
                    _split.Value = stack.Split;
                }

                BuildPasses(stack.Preset);
            }
            finally
            {
                _syncing = false;
            }
        }

        private void BuildPasses(PostFxPreset preset)
        {
            foreach (Node n in _passes.GetChildren())
            {
                n.QueueFree();
            }

            if (preset.Passes.Count == 0)
            {
                _passes.AddChild(Lbl("Classic draws the world exactly as ClassicUO does. Pick a look above.", UoTheme.Muted, wrap: true));
                return;
            }

            for (int index = 0; index < preset.Passes.Count; index++)
            {
                int passIndex = index;
                PostFxPass pass = preset.Passes[index];
                Shader shader = PostFxLibrary.Shader(pass.Shader);

                // Each pass on parchment, the guide's frame for a list's entries.
                var box = new PanelContainer();
                box.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 5));
                var inner = new VBoxContainer();
                inner.AddThemeConstantOverride("separation", 4);
                box.AddChild(inner);

                var toggle = new CheckBox { Text = Title(pass.Shader), ButtonPressed = pass.Enabled };
                // Colour means state: the heading colour marks the pass's own switch.
                foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" })
                {
                    toggle.AddThemeColorOverride(c, UoTheme.Heading);
                }

                toggle.Toggled += on =>
                {
                    pass.Enabled = on;
                    PostFxStack.Instance.Rebuild();
                };
                inner.AddChild(toggle);

                if (shader == null)
                {
                    inner.AddChild(Lbl($"No shader \"{pass.Shader}\".", UoTheme.Danger));
                }
                else
                {
                    foreach (PostFxUniform u in PostFxUniforms.Of(shader))
                    {
                        inner.AddChild(Knob(passIndex, pass, u));
                    }
                }

                _passes.AddChild(box);
            }
        }

        private Control Knob(int passIndex, PostFxPass pass, PostFxUniform u)
        {
            Variant current = pass.Params.TryGetValue(u.Name, out JsonNode set)
                ? PostFxStack.Coerce(PostFxLibrary.Shader(pass.Shader), u.Name, PostFxStack.ToVariant(set))
                : u.Default;

            if (u.IsEnum)
            {
                var row = new HBoxContainer();
                row.AddChild(Lbl(u.Label, UoTheme.Ink, expand: true));
                var pick = new OptionButton { CustomMinimumSize = new Vector2(120, UoTheme.ButtonHeight), ClipText = true };
                foreach (string option in u.Options)
                {
                    pick.AddItem(option.Contains(':') ? option[..option.IndexOf(':')] : option);
                }

                int at = current.VariantType is Variant.Type.Int or Variant.Type.Float ? (int)current.AsDouble() : 0;
                pick.Select(Math.Clamp(at, 0, Math.Max(0, u.Options.Length - 1)));
                pick.ItemSelected += i => PostFxStack.Instance.SetParam(passIndex, u.Name, JsonValue.Create((float)i));
                row.AddChild(pick);
                return row;
            }

            if (u.IsRange)
            {
                var row = new VBoxContainer();
                row.AddThemeConstantOverride("separation", 0);
                var label = new HBoxContainer();
                label.AddChild(Lbl(u.Label, UoTheme.Ink, expand: true));
                double value = current.VariantType is Variant.Type.Float or Variant.Type.Int ? current.AsDouble() : u.Min;
                var shown = Lbl(Format(value, u), UoTheme.Muted);
                label.AddChild(shown);
                row.AddChild(label);
                var slider = Slider(u.Min, u.Max, u.Step, value);
                slider.ValueChanged += v =>
                {
                    shown.Text = Format(v, u);
                    PostFxStack.Instance.SetParam(passIndex, u.Name, JsonValue.Create((float)v));
                };
                row.AddChild(slider);
                return row;
            }

            if (u.IsColor)
            {
                var row = new HBoxContainer();
                row.AddChild(Lbl(u.Label, UoTheme.Ink, expand: true));
                var picker = new ColorPickerButton
                {
                    Color = current.VariantType == Variant.Type.Color ? current.AsColor() : Colors.White,
                    CustomMinimumSize = new Vector2(48, UoTheme.ButtonHeight),
                };
                picker.ColorChanged += c =>
                    PostFxStack.Instance.SetParam(passIndex, u.Name, new JsonArray(c.R, c.G, c.B, c.A));
                row.AddChild(picker);
                return row;
            }

            var check = new CheckBox { Text = u.Label, ButtonPressed = current.VariantType == Variant.Type.Bool && current.AsBool() };
            check.Toggled += on => PostFxStack.Instance.SetParam(passIndex, u.Name, JsonValue.Create(on));
            return check;
        }

        private void SaveAs()
        {
            string name = _saveName.Text.Trim();
            if (name.Length == 0)
            {
                _saveName.PlaceholderText = "Type a name first";
                return;
            }

            PostFxPreset copy = PostFxStack.Instance.Preset.Clone();
            copy.Name = name;
            copy.Description = "Saved from the effects menu.";
            try
            {
                string path = PostFxLibrary.Save(copy);
                PostFxStack.Instance.Use(copy);
                _saveName.Text = "";
                _cost.Text = "Saved " + System.IO.Path.GetFileName(path);
                _costTimer = 3;
            }
            catch (Exception e)
            {
                _cost.Text = "Could not save: " + e.Message;
                _costTimer = 5;
            }
        }

        // --- small helpers ------------------------------------------------------------

        private static string Title(string shader) =>
            shader.Length == 0 ? "?" : char.ToUpperInvariant(shader[0]) + shader[1..].Replace('_', ' ');

        private static string Format(double v, PostFxUniform u) =>
            u.Type == Variant.Type.Int || u.Step >= 1f ? $"{v:0}" : u.Step >= 0.1f ? $"{v:0.0}" : $"{v:0.00}";

        // The theme draws UO's bar and knob; only the range is set here.
        private static HSlider Slider(double min, double max, double step, double value) => new()
        {
            MinValue = min, MaxValue = max, Step = step, Value = value,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        private static Label Lbl(string text, Color color, bool expand = false, bool wrap = false)
        {
            Label l = UoTheme.Label(text, color);
            if (expand)
            {
                l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            }

            if (wrap)
            {
                l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                l.CustomMinimumSize = new Vector2(Width - 60f, 0);
            }

            return l;
        }
    }
}
