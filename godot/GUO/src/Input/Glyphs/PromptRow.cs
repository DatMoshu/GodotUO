// GUO addition, not a port: upstream ClassicUO has no button prompts.

using System;
using Godot;
using GUO.Input.Touch;

namespace GUO.Input.Glyphs
{
    /// <summary>
    /// A row of button prompts for a Godot card ("[A] Press  [B] Close"): a
    /// glyph and a short caption for each job, in the input the player is
    /// using now. It rebuilds the moment that input changes
    /// (<see cref="InputMode.Changed"/>): pad glyphs after a pad button, the
    /// key or mouse button after a key or a click. A job with no glyph for the
    /// input in use is left out, and with none left the row hides (touch).
    /// </summary>
    internal sealed partial class PromptRow : HBoxContainer
    {
        private readonly (PadAction action, string caption, bool padOnly)[] _items;

        /// <summary>After a rebuild, for the card to size itself again.</summary>
        public event Action Rebuilt;

        /// <param name="items">Each job, its caption, and whether it is a pad's
        /// job only (the window menu's D-pad choosing, which no key does).</param>
        public PromptRow(params (PadAction action, string caption, bool padOnly)[] items)
        {
            _items = items;
            MouseFilter = MouseFilterEnum.Ignore;
            AddThemeConstantOverride("separation", 10);
            Rebuild();
        }

        public override void _EnterTree()
        {
            InputMode.Changed += OnChanged;
            Rebuild();
        }

        public override void _ExitTree()
        {
            InputMode.Changed -= OnChanged;
        }

        private void OnChanged(InputKind kind)
        {
            Rebuild();
            Rebuilt?.Invoke();
        }

        /// <summary>For the probe: the glyph names shown now, in order.</summary>
        public string Shown { get; private set; } = "";

        private void Rebuild()
        {
            foreach (Node n in GetChildren())
            {
                RemoveChild(n);
                n.QueueFree();
            }

            var shown = new System.Collections.Generic.List<string>();

            foreach ((PadAction action, string caption, bool padOnly) in _items)
            {
                Texture2D glyph = padOnly && InputMode.Current != InputKind.Gamepad ? null : InputGlyphs.For(action);

                if (glyph == null)
                {
                    continue;
                }

                shown.Add(InputGlyphs.LastShown[action]);
                var pair = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                pair.AddThemeConstantOverride("separation", 4);
                pair.AddChild(new TextureRect
                {
                    Texture = glyph,
                    StretchMode = TextureRect.StretchModeEnum.KeepCentered,
                    CustomMinimumSize = new Vector2(InputGlyphs.Size, InputGlyphs.Size),
                    TextureFilter = TextureFilterEnum.Nearest,
                    MouseFilter = MouseFilterEnum.Ignore,
                });
                Label words = UoTheme.Label(caption, UoTheme.Muted);
                words.MouseFilter = MouseFilterEnum.Ignore;
                pair.AddChild(words);
                AddChild(pair);
            }

            Shown = string.Join(" ", shown);
            Visible = shown.Count > 0;
        }
    }
}
