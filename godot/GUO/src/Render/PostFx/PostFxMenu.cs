// GUO-owned (ADR-0023): the effects menu, a Godot card over the game.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;

namespace GUO.Renderer.PostFx
{
    /// <summary>
    /// Screen effects: a preset picker, the passes with a toggle each, sliders
    /// made from each shader's own uniform hints, an A/B split against Classic,
    /// and "save as preset". Opened from Options (Video) and with Ctrl+Shift+E.
    /// </summary>
    /// <remarks>
    /// A card of Godot Controls on its own CanvasLayer, above the game and
    /// never under an effect (effects only touch the world target). While it is
    /// open, <see cref="OwnsInput"/> tells the game controller to leave the
    /// events over it to the card, as it does for the Store window.
    /// </remarks>
    internal sealed partial class PostFxMenu : Node
    {
        private static readonly Color Gold = new("dfbb77"), Text = new("eeeade"), Muted = new("abb5ac");

        // Text that sits straight on the card: dark ink on UO's light stone
        // gump (as the client's own gumps write on stone), light text on the
        // plain dark fallback. Text inside the dark boxes stays parchment.
        private static bool _stone;
        private static Color OnCard => _stone ? new Color("2a1f12") : Gold;
        private static Color OnCardMuted => _stone ? new Color("4a4336") : Muted;
        private const float Width = 360f;

        private static PostFxMenu _instance;

        private CanvasLayer _layer;
        private PanelContainer _card;
        private VBoxContainer _passes;
        private OptionButton _presets;
        private CheckButton _compare, _fullQuality;
        private HSlider _split;
        private LineEdit _saveName;
        private Label _subtitle, _cost;
        private List<PostFxPreset> _presetList = new();
        private bool _syncing;
        private double _costTimer;

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
            if (e is InputEventMouse mouse)
            {
                return me._card.GetGlobalRect().HasPoint(mouse.Position);
            }

            if (e is InputEventKey)
            {
                // Typing a preset name goes to the text box.
                return me._saveName.HasFocus();
            }

            return false;
        }

        public override void _Ready()
        {
            _layer = new CanvasLayer { Layer = 95, Visible = false };
            AddChild(_layer);
            _card = new PanelContainer
            {
                Theme = BuildTheme(), CustomMinimumSize = new Vector2(Width, 0),
                // Rule 7: the gump frame is pixel art, drawn nearest.
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            };
            StyleBox frame = CardStyle();
            _stone = frame is StyleBoxTexture;
            _card.AddThemeStyleboxOverride("panel", frame);
            _layer.AddChild(_card);
            Build();
            PostFxStack.Instance.PresetChanged += () => { if (IsOpen) CallDeferred(MethodName.Refresh); };
        }

        public override void _Input(InputEvent e)
        {
            if (e is InputEventKey { Pressed: true, Echo: false } k && k.Keycode == Key.E && k.CtrlPressed && k.ShiftPressed)
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

        public override void _Process(double delta)
        {
            if (!IsOpen)
            {
                return;
            }

            // Keep the card at the top right and within the window.
            Vector2 size = GetViewport().GetVisibleRect().Size;
            float maxH = Math.Max(240f, size.Y - 32f);
            Vector2 min = _card.GetCombinedMinimumSize();
            _card.Size = new Vector2(Math.Max(Width, min.X), Math.Min(Math.Max(min.Y, maxH * 0.8f), maxH));
            // Placed by its real width, so a wide control never pushes it off screen.
            _card.Position = new Vector2(Math.Max(8f, size.X - _card.Size.X - 16f), 16f);

            _costTimer -= delta;
            if (_costTimer <= 0)
            {
                _costTimer = 0.5;
                double ms = PostFxStack.Instance.LastGpuMs;
                _cost.Text = PostFxStack.Instance.Preset.IsClassic && PostFxStack.Instance.Split <= 0f
                    ? "Classic: nothing added to the frame"
                    : $"{ms:0.00} ms of GPU a frame";
            }
        }

        private void Open()
        {
            Refresh();
            _layer.Visible = true;
        }

        private void Close()
        {
            _layer.Visible = false;
            _saveName.ReleaseFocus();
        }

        // --- building -----------------------------------------------------------------

        private void Build()
        {
            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 10);
            _card.AddChild(col);

            var head = new HBoxContainer();
            var titles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            titles.AddThemeConstantOverride("separation", 0);
            titles.AddChild(Lbl("Screen effects", 19, OnCard));
            _subtitle = Lbl("", 12, OnCardMuted);
            titles.AddChild(_subtitle);
            head.AddChild(titles);
            var close = new Button { Text = "✕", Flat = true, TooltipText = "Close (Esc)" };
            close.AddThemeColorOverride("font_color", OnCard);
            close.AddThemeColorOverride("font_hover_color", OnCardMuted);
            close.Pressed += Close;
            head.AddChild(close);
            col.AddChild(head);
            col.AddChild(new HSeparator { Modulate = new Color(Gold, 0.35f) });

            _presets = new OptionButton { FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(Width - 60f, 0) };
            _presets.ItemSelected += i =>
            {
                if (!_syncing && i >= 0 && i < _presetList.Count)
                {
                    PostFxStack.Instance.Use(_presetList[(int)i]);
                }
            };
            col.AddChild(_presets);

            var compareRow = new HBoxContainer();
            _compare = new CheckButton { Text = "Compare with Classic", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _compare.AddThemeColorOverride("font_color", OnCard);
            _compare.AddThemeColorOverride("font_hover_color", OnCard);
            _compare.AddThemeColorOverride("font_pressed_color", OnCard);
            _compare.AddThemeColorOverride("font_hover_pressed_color", OnCard);
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
            compareRow.AddChild(_compare);
            col.AddChild(compareRow);
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

            _fullQuality = new CheckButton
            {
                Text = "Full resolution",
                TooltipText = "Off: looks with a heavy pass (glow, outline) run at half resolution, for weaker GPUs",
            };
            foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color" })
            {
                _fullQuality.AddThemeColorOverride(c, OnCard);
            }

            _fullQuality.Toggled += on =>
            {
                if (!_syncing)
                {
                    PostFxStack.Instance.FullQuality = on;
                }
            };
            col.AddChild(_fullQuality);

            col.AddChild(new HSeparator { Modulate = new Color(Gold, 0.2f) });
            var scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(0, 260), SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            _passes = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _passes.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(_passes);
            col.AddChild(scroll);

            col.AddChild(new HSeparator { Modulate = new Color(Gold, 0.2f) });
            var saveRow = new HBoxContainer();
            _saveName = new LineEdit
            {
                PlaceholderText = "Name your look", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(120, 0),
            };
            saveRow.AddChild(_saveName);
            var save = new Button { Text = "Save as preset" };
            save.AddThemeStyleboxOverride("normal", Box("b58e42", "dfbb77"));
            save.AddThemeStyleboxOverride("hover", Box("c79d4d", "f0d08a"));
            save.AddThemeColorOverride("font_color", new Color("141917"));
            save.AddThemeColorOverride("font_hover_color", new Color("141917"));
            save.Pressed += SaveAs;
            saveRow.AddChild(save);
            col.AddChild(saveRow);

            _cost = Lbl("", 11, OnCardMuted);
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
                _passes.AddChild(Lbl("Classic draws the world exactly as ClassicUO does. Pick a look above.", 12, OnCardMuted, wrap: true));
                return;
            }

            for (int index = 0; index < preset.Passes.Count; index++)
            {
                int passIndex = index;
                PostFxPass pass = preset.Passes[index];
                Shader shader = PostFxLibrary.Shader(pass.Shader);

                var box = new PanelContainer();
                box.AddThemeStyleboxOverride("panel", Box("141a17", "2c352f"));
                var inner = new VBoxContainer();
                inner.AddThemeConstantOverride("separation", 4);
                box.AddChild(inner);

                var toggle = new CheckButton { Text = Title(pass.Shader), ButtonPressed = pass.Enabled };
                toggle.AddThemeColorOverride("font_color", Gold);
                toggle.Toggled += on =>
                {
                    pass.Enabled = on;
                    PostFxStack.Instance.Rebuild();
                };
                inner.AddChild(toggle);

                if (shader == null)
                {
                    inner.AddChild(Lbl($"No shader \"{pass.Shader}\".", 11, new Color("d98a7a")));
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
                row.AddChild(Lbl(u.Label, 12, Text, expand: true));
                var pick = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
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
                label.AddChild(Lbl(u.Label, 12, Text, expand: true));
                double value = current.VariantType is Variant.Type.Float or Variant.Type.Int ? current.AsDouble() : u.Min;
                var shown = Lbl(Format(value, u), 12, Muted);
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
                row.AddChild(Lbl(u.Label, 12, Text, expand: true));
                var picker = new ColorPickerButton
                {
                    Color = current.VariantType == Variant.Type.Color ? current.AsColor() : Colors.White,
                    CustomMinimumSize = new Vector2(64, 24),
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

        // --- style (the window menu's and the Store's) -----------------------------------

        private static string Title(string shader) =>
            shader.Length == 0 ? "?" : char.ToUpperInvariant(shader[0]) + shader[1..].Replace('_', ' ');

        private static string Format(double v, PostFxUniform u) =>
            u.Type == Variant.Type.Int || u.Step >= 1f ? $"{v:0}" : u.Step >= 0.1f ? $"{v:0.0}" : $"{v:0.00}";

        private static HSlider Slider(double min, double max, double step, double value)
        {
            var s = new HSlider
            {
                MinValue = min, MaxValue = max, Step = step, Value = value,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 22),
            };
            s.AddThemeStyleboxOverride("slider", new StyleBoxFlat
            {
                BgColor = new Color("2c352f"), ContentMarginTop = 2, ContentMarginBottom = 2,
            });
            s.AddThemeStyleboxOverride("grabber_area", new StyleBoxFlat
            {
                BgColor = new Color(Gold, 0.8f), ContentMarginTop = 2, ContentMarginBottom = 2,
            });
            s.AddThemeStyleboxOverride("grabber_area_highlight", new StyleBoxFlat { BgColor = Gold });
            return s;
        }

        /// <summary>
        /// The card's frame: UO's own dark stone ResizePic (0x0A28, the one the
        /// character screen uses), its nine pieces composed into one texture and
        /// tiled like the client tiles them. Without client data, a square frame
        /// in the same colours, with a hard 90s shadow and no rounding.
        /// </summary>
        private static StyleBox CardStyle()
        {
            StyleBoxTexture gump = GumpFrame(0x0A28);
            if (gump != null)
            {
                return gump;
            }

            return new StyleBoxFlat
            {
                BgColor = new Color(0.078f, 0.098f, 0.090f, 0.97f),
                BorderColor = new Color(Gold, 0.55f),
                BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
                ShadowColor = new Color(0, 0, 0, 0.6f), ShadowSize = 0, ShadowOffset = new Vector2(3, 3),
                ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 14,
                AntiAliasing = false,
            };
        }

        private static StyleBoxTexture GumpFrame(ushort graphic)
        {
            try
            {
                var gumps = GUO.Client.Game?.UO?.FileManager?.Gumps;
                if (gumps == null)
                {
                    return null;
                }

                var pieces = new Image[9];
                for (int i = 0; i < 9; i++)
                {
                    var info = gumps.GetGump((uint)(graphic + i));
                    if (info.Width <= 0 || info.Height <= 0)
                    {
                        return null;
                    }

                    // Pixels are 0xAABBGGRR, i.e. RGBA8 bytes in memory.
                    byte[] bytes = System.Runtime.InteropServices.MemoryMarshal
                        .AsBytes(info.Pixels.Slice(0, info.Width * info.Height)).ToArray();
                    pieces[i] = Image.CreateFromData(info.Width, info.Height, false, Image.Format.Rgba8, bytes);
                }

                int left = pieces[0].GetWidth(), right = pieces[2].GetWidth();
                int top = pieces[0].GetHeight(), bottom = pieces[6].GetHeight();
                int midW = pieces[4].GetWidth(), midH = pieces[4].GetHeight();
                var sheet = Image.CreateEmpty(left + midW + right, top + midH + bottom, false, Image.Format.Rgba8);
                void Put(int i, int x, int y) => sheet.BlitRect(pieces[i], new Rect2I(0, 0, pieces[i].GetSize()), new Vector2I(x, y));
                Put(0, 0, 0);
                Put(1, left, 0);
                Put(2, left + midW, 0);
                Put(3, 0, top);
                Put(4, left, top);
                Put(5, left + midW, top);
                Put(6, 0, top + midH);
                Put(7, left, top + midH);
                Put(8, left + midW, top + midH);

                return new StyleBoxTexture
                {
                    Texture = ImageTexture.CreateFromImage(sheet),
                    TextureMarginLeft = left, TextureMarginRight = right, TextureMarginTop = top, TextureMarginBottom = bottom,
                    AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                    AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
                    ContentMarginLeft = left + 8, ContentMarginRight = right + 8,
                    ContentMarginTop = top + 4, ContentMarginBottom = bottom + 6,
                };
            }
            catch (Exception e)
            {
                GD.PushWarning($"[GUO] postfx menu: no gump frame: {e.Message}");
                return null;
            }
        }

        // Square, 1 px, no anti-aliasing: pixel-art boxes, not rounded cards.
        private static StyleBoxFlat Box(string bg, string border) => new()
        {
            BgColor = new Color(bg), BorderColor = new Color(border),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            AntiAliasing = false,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6,
        };

        private static Theme BuildTheme()
        {
            var theme = new Theme { DefaultFontSize = 14 };
            foreach (string control in new[] { "Button", "CheckButton", "CheckBox", "OptionButton", "ColorPickerButton" })
            {
                theme.SetStylebox("normal", control, Box("202922", "455342"));
                theme.SetStylebox("hover", control, Box("303d2e", "b4a16b"));
                theme.SetStylebox("pressed", control, Box("3f4931", "dfbb77"));
                theme.SetStylebox("disabled", control, Box("181d1a", "2c332e"));
                theme.SetStylebox("focus", control, new StyleBoxEmpty());
                theme.SetColor("font_color", control, Text);
                theme.SetColor("font_hover_color", control, Text);
                theme.SetColor("font_pressed_color", control, Text);
            }

            foreach (string control in new[] { "CheckButton", "CheckBox" })
            {
                // Toggles read as rows, not boxed buttons.
                theme.SetStylebox("normal", control, new StyleBoxEmpty());
                theme.SetStylebox("hover", control, new StyleBoxEmpty());
                theme.SetStylebox("pressed", control, new StyleBoxEmpty());
                theme.SetStylebox("hover_pressed", control, new StyleBoxEmpty());
            }

            theme.SetStylebox("normal", "LineEdit", Box("141a17", "455342"));
            theme.SetStylebox("focus", "LineEdit", Box("141a17", "dfbb77"));
            theme.SetColor("font_color", "LineEdit", Text);
            theme.SetColor("font_color", "Label", Text);
            return theme;
        }

        private static Label Lbl(string text, int size, Color color, bool expand = false, bool wrap = false)
        {
            var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
            l.AddThemeFontSizeOverride("font_size", size);
            l.AddThemeColorOverride("font_color", color);
            if (expand)
            {
                l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            }

            if (wrap)
            {
                l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                l.CustomMinimumSize = new Vector2(Width - 40f, 0);
            }

            return l;
        }
    }
}
