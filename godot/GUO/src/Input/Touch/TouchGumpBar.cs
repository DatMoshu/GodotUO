// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Game;
using GUO.Game.Managers;

namespace GUO.Input.Touch
{
    /// <summary>
    /// A row of buttons along the bottom of the screen that open the gumps a
    /// player reaches for most, mirroring the top bar, plus the on-screen
    /// keyboard.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): there is no such bar upstream; the top bar gump
    /// is its desktop equivalent and stays exactly as it is. This is a Godot
    /// <see cref="CanvasLayer"/> drawn above the client's canvas item, and
    /// not a gump, for two reasons. It never enters the world render path:
    /// the client composites its frame on one canvas item and knows nothing
    /// about a layer above it, so nothing in the batcher, the render targets
    /// or the draw order changes when it is on, and nothing at all exists
    /// when it is off. And it takes no input of its own: GameController
    /// marks every event handled before a Godot Control could see it, so the
    /// touch layer hit-tests the bar itself and calls <see cref="Invoke"/>,
    /// which calls the same <c>GameActions</c> the top bar's buttons call.
    ///
    /// It is drawn with flat rectangles and the engine's fallback font -- no
    /// texture, so nothing to filter -- and only while the character is in
    /// the world, which is the only time the actions it offers exist.
    /// </remarks>
    internal sealed partial class TouchGumpBar : CanvasLayer
    {
        /// <summary>The buttons, left to right.</summary>
        public static readonly string[] Actions =
        {
            "paperdoll", "backpack", "journal", "map", "chat", "options", "keyboard",
        };

        private static readonly Dictionary<string, string> Labels = new()
        {
            { "paperdoll", "Paper" },
            { "backpack", "Pack" },
            { "journal", "Journal" },
            { "map", "Map" },
            { "chat", "Chat" },
            { "options", "Options" },
            { "keyboard", "Keys" },
        };

        private readonly Surface _surface = new();

        /// <summary>Whether the bar is drawn and takes taps.</summary>
        public bool Shown { get; private set; }

        public override void _Ready()
        {
            Layer = 10;
            AddChild(_surface);
        }

        public override void _Process(double delta)
        {
            bool inGame = Client.Game?.UO?.World?.InGame ?? false;

            if (inGame != Shown)
            {
                Shown = inGame;
                _surface.QueueRedraw();
            }
            else if (Shown)
            {
                // The window can change size under it, and so can the scale.
                _surface.QueueRedraw();
            }
        }

        /// <summary>The rectangle of one button, in viewport pixels.</summary>
        public Rect2 ButtonRect(string action)
        {
            int index = System.Array.IndexOf(Actions, action);

            if (index < 0)
            {
                return default;
            }

            Vector2 view = _surface.GetViewportRect().Size;
            float scale = Client.Game?.ScreenScale ?? 1f;
            float width = 64 * scale;
            float height = 40 * scale;
            float gap = 4 * scale;
            float total = Actions.Length * width + (Actions.Length - 1) * gap;
            float x = view.X - total - gap;
            float y = view.Y - height - gap;

            return new Rect2(x + index * (width + gap), y, width, height);
        }

        /// <summary>Which button, if any, a point lands on.</summary>
        public bool HitTest(Vector2 at, out string action)
        {
            action = null;

            if (!Shown)
            {
                return false;
            }

            foreach (string a in Actions)
            {
                if (ButtonRect(a).HasPoint(at))
                {
                    action = a;

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// What a button does: the top bar's own calls, by name.
        /// </summary>
        public void Invoke(string action)
        {
            World world = Client.Game?.UO?.World;

            if (world == null || !world.InGame)
            {
                return;
            }

            switch (action)
            {
                case "paperdoll":
                    GameActions.OpenPaperdoll(world, world.Player);

                    break;

                case "backpack":
                    GameActions.OpenBackpack(world);

                    break;

                case "journal":
                    GameActions.OpenJournal(world);

                    break;

                case "map":
                    GameActions.OpenMiniMap(world);

                    break;

                case "chat":
                    GameActions.OpenChat(world);

                    break;

                case "options":
                    GameActions.OpenSettings(world);

                    break;

                case "keyboard":
                    if (DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
                    {
                        DisplayServer.VirtualKeyboardShow("");
                    }
                    else
                    {
                        GD.Print("[GUO] touch input: no virtual keyboard on this platform");
                    }

                    break;
            }
        }

        /// <summary>The control that paints the buttons.</summary>
        private sealed partial class Surface : Control
        {
            public override void _Ready()
            {
                // Never a Godot input target: the touch layer routes to the
                // bar itself, and GameController swallows events before any
                // Control anyway.
                MouseFilter = MouseFilterEnum.Ignore;
                SetAnchorsPreset(LayoutPreset.FullRect);
            }

            public override void _Draw()
            {
                if (GetParent() is not TouchGumpBar bar || !bar.Shown)
                {
                    return;
                }

                Font font = ThemeDB.FallbackFont;
                float scale = Client.Game?.ScreenScale ?? 1f;
                int fontSize = (int)(11 * scale);

                foreach (string action in Actions)
                {
                    Rect2 r = bar.ButtonRect(action);

                    DrawRect(r, new Color(0.10f, 0.08f, 0.06f, 0.85f));
                    DrawRect(r, new Color(0.78f, 0.64f, 0.36f), false, System.Math.Max(1f, scale));

                    string label = Labels[action];
                    Vector2 size = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
                    var at = new Vector2(
                        r.Position.X + (r.Size.X - size.X) / 2,
                        r.Position.Y + (r.Size.Y + size.Y) / 2 - font.GetDescent(fontSize)
                    );

                    DrawString(font, at, label, HorizontalAlignment.Left, -1, fontSize, new Color(0.95f, 0.90f, 0.75f));
                }
            }
        }
    }
}
