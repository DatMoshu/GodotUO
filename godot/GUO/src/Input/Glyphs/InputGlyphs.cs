// GUO addition, not a port: upstream ClassicUO has no gamepad, so it never
// shows a button prompt.

using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace GUO.Input.Glyphs
{
    /// <summary>
    /// The glyph for a job, for the input the player is using now
    /// (<see cref="InputMode"/>, ADR-0025): a pad's printed button in its
    /// family's style, or the key or mouse button that does it. Touch has
    /// none: a finger taps what it sees.
    /// </summary>
    /// <remarks>
    /// The art is Kenney's "Input Prompts Pixel" (CC0), 16x16, copied by
    /// tools/glyphs into Resources/embedded/glyphs and embedded in the
    /// assembly, so the desktop, the Android build and the web build read the
    /// same bytes. Lettered pads (Xbox, Nintendo, Steam Deck, any other) show
    /// the printed letter, which <see cref="InputMode.ButtonFor"/> already
    /// resolves through the pad's layout; a PlayStation pad shows its
    /// symbols. The light neutral set is used for every family: it sits on the
    /// client's stone like its marble plates, and colour stays for state
    /// (docs/ui/uo_godot_style.md). Drawn at a whole-number scale, nearest.
    /// </remarks>
    internal static class InputGlyphs
    {
        public const int Size = 16; // art pixels

        private static readonly Dictionary<string, Texture2D> _cache = new();

        /// <summary>For probes: the glyph name last asked for by each job.</summary>
        public static readonly Dictionary<PadAction, string> LastShown = new();

        /// <summary>The glyph for a job with the input in use now; null when there is none (touch, an unknown pad).</summary>
        public static Texture2D For(PadAction action)
        {
            string name = NameFor(action, InputMode.Current, InputMode.PadFamily, InputMode.ButtonFor(action));
            LastShown[action] = name;
            return name == null ? null : Load(name);
        }

        /// <summary>
        /// The pad glyph for a job, whatever the input in use (the Options
        /// legend). With no pad yet, or one whose layout is unknown, the face
        /// buttons show their printed letter: the legend says what the
        /// printed A does.
        /// </summary>
        public static Texture2D Pad(PadAction action)
        {
            JoyButton? printed = InputMode.ButtonFor(action) ?? action switch
            {
                PadAction.Confirm => JoyButton.A,
                PadAction.Cancel => JoyButton.B,
                PadAction.WindowMenu => JoyButton.X,
                PadAction.Y => JoyButton.Y,
                _ => null,
            };

            return Load(NameFor(action, InputKind.Gamepad, InputMode.PadFamily, printed));
        }

        /// <summary>
        /// The glyph's file name (without ".png") for a job: pure, for the
        /// probe. <paramref name="printed"/> is the printed pad button that
        /// does it (<see cref="InputMode.ButtonFor"/>).
        /// </summary>
        public static string NameFor(PadAction action, InputKind kind, PadFamily family, JoyButton? printed)
        {
            switch (kind)
            {
                case InputKind.KeyboardMouse:
                    return action switch
                    {
                        PadAction.Confirm => "mouse_left",
                        PadAction.Cancel => "key_esc",
                        PadAction.Walk => "mouse_right", // the client walks toward the held right button
                        PadAction.Pointer => "mouse",
                        _ => null,
                    };

                case InputKind.Gamepad:
                    if (action == PadAction.Walk)
                    {
                        return "dpad";
                    }

                    if (action == PadAction.Pointer)
                    {
                        return "stick_r";
                    }

                    // Back / Select / View: one glyph for every family, as the D-pad and sticks are.
                    if (action == PadAction.Back)
                    {
                        return "pad_back";
                    }

                    if (printed == null)
                    {
                        return null;
                    }

                    if (family == PadFamily.PlayStation)
                    {
                        return printed.Value switch
                        {
                            JoyButton.A => "ps_cross",
                            JoyButton.B => "ps_circle",
                            JoyButton.X => "ps_square",
                            JoyButton.Y => "ps_triangle",
                            _ => null,
                        };
                    }

                    return printed.Value switch
                    {
                        JoyButton.A => "pad_a",
                        JoyButton.B => "pad_b",
                        JoyButton.X => "pad_x",
                        JoyButton.Y => "pad_y",
                        JoyButton.LeftShoulder => "pad_lb",
                        JoyButton.RightShoulder => "pad_rb",
                        JoyButton.LeftStick => "pad_l3",
                        JoyButton.RightStick => "pad_r3",
                        JoyButton.Start => "pad_start",
                        JoyButton.Back => "pad_back",
                        _ => null,
                    };

                default:
                    return null;
            }
        }

        /// <summary>A job's button by name, for a tooltip ("Esc", "B", "Circle"); null where there is none.</summary>
        public static string Words(PadAction action)
        {
            string name = NameFor(action, InputMode.Current, InputMode.PadFamily, InputMode.ButtonFor(action));

            return name switch
            {
                "key_esc" => "Esc",
                "mouse_left" => "Left click",
                "mouse_right" => "Right mouse",
                "pad_a" => "A",
                "pad_b" => "B",
                "pad_x" => "X",
                "pad_y" => "Y",
                "pad_back" => "Back",
                "pad_lb" => "LB",
                "pad_rb" => "RB",
                "pad_lt" => "LT",
                "pad_rt" => "RT",
                "pad_l3" => "L3",
                "pad_r3" => "R3",
                "pad_start" => "Start",
                "ps_cross" => "Cross",
                "ps_circle" => "Circle",
                "ps_square" => "Square",
                "ps_triangle" => "Triangle",
                "dpad" => "D-pad",
                "stick_r" => "Right stick",
                _ => null,
            };
        }

        /// <summary>
        /// A control's tooltip names the button that does its job in the
        /// input in use, worked out when the pointer comes over it (a mouse,
        /// or the pad's right stick): "Close (Esc)", "Close (B)", "Close (Circle)".
        /// </summary>
        /// <param name="padOnly">Name the button only for a pad: no key does the job.</param>
        public static void TooltipFollows(Control control, string words, PadAction action, bool padOnly = false)
        {
            void Set() => control.TooltipText =
                (!padOnly || InputMode.Current == InputKind.Gamepad) && Words(action) is string key ? $"{words} ({key})" : words;

            Set();
            control.MouseEntered += Set;
        }


        /// <summary>Glyph file name for a bound pad input (button or trigger); null when none.</summary>
        public static string GlyphName(GUO.Input.Gamepad.PadInput input)
        {
            if (input.IsAxis)
            {
                return (JoyAxis)input.Index switch
                {
                    JoyAxis.TriggerLeft => "pad_lt",
                    JoyAxis.TriggerRight => "pad_rt",
                    JoyAxis.LeftX or JoyAxis.LeftY => "stick_l",
                    JoyAxis.RightX or JoyAxis.RightY => "stick_r",
                    _ => null,
                };
            }

            return NameFor(PadAction.Confirm, InputKind.Gamepad, InputMode.PadFamily, (JoyButton)input.Index);
        }

        internal static Texture2D Load(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            if (_cache.TryGetValue(name, out Texture2D cached))
            {
                return cached;
            }

            Texture2D texture = null;
            using Stream stream = typeof(InputGlyphs).Assembly.GetManifestResourceStream($"glyphs/{name}.png");

            if (stream != null)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var image = new Image();

                if (image.LoadPngFromBuffer(buffer.ToArray()) == Error.Ok)
                {
                    texture = ImageTexture.CreateFromImage(image);
                }
            }

            if (texture == null)
            {
                GD.PushWarning($"[GUO] glyphs: {name}.png is not embedded");
            }

            _cache[name] = texture;
            return texture;
        }
    }
}
