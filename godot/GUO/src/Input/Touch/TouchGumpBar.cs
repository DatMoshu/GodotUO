// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Resources;

namespace GUO.Input.Touch
{
    /// <summary>
    /// A row of the client's own menu buttons along the bottom of the
    /// screen, sized for a finger, that open the gumps a player reaches for
    /// most: the top bar's row, moved to where a thumb can reach it.
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
    /// It is drawn from the same pieces as the top bar -- the wide button
    /// gump (0x098D) and the client's unicode font 1, rendered by the same
    /// FontsLoader call RenderedText makes -- at a whole-number scale chosen
    /// so six buttons fill the width, sampled nearest-neighbour. The labels
    /// are the top bar's own clilocs, so the words are the ones the client
    /// already uses. Without the gump (an old client) it falls back to flat
    /// rectangles and the engine's font. It is only shown while the character
    /// is in the world, which is the only time the actions it offers exist.
    /// While a target cursor is up, Chat and Options give way to Self and
    /// Cancel (tinted blue and red): a phone has no Esc to cancel a target.
    /// </remarks>
    internal sealed partial class TouchGumpBar : CanvasLayer
    {
        /// <summary>The buttons, left to right.</summary>
        public static readonly string[] Actions =
        {
            "paperdoll", "backpack", "journal", "map", "chat", "options",
        };

        /// <summary>
        /// While a target cursor is up, the last two buttons answer it: Self
        /// targets the player (who may be under a gump), Cancel is the Esc a
        /// phone does not have. They go back to Chat and Options after.
        /// </summary>
        private static readonly string[] TargetingActions =
        {
            "paperdoll", "backpack", "journal", "map", "self", "cancel",
        };

        /// <summary>The buttons as they are now.</summary>
        public static string[] Current =>
            Client.Game?.UO?.World?.TargetManager?.IsTargeting == true ? TargetingActions : Actions;

        /// <summary>The top bar's wide button, and its size in the 7.0 client.</summary>
        private const ushort ButtonGump = 0x098D;
        private const int FallbackWidth = 100;
        private const int FallbackHeight = 25;

        /// <summary>The top bar's font for its captions.</summary>
        private const byte LabelFont = 1;

        /// <summary>Milliseconds a tapped button stays lit.</summary>
        private const ulong PressedMs = 140;

        private readonly Surface _surface = new();
        private readonly Dictionary<string, Texture2D> _labels = new();
        private string _pressed;
        private ulong _pressedAt;

        /// <summary>Whether the bar is drawn and takes taps.</summary>
        public bool Shown { get; private set; }

        /// <summary>
        /// The share of the window's height the bar covers while shown, so a
        /// gump can be kept above it whatever units it is laid out in.
        /// </summary>
        public float ReservedFraction
        {
            get
            {
                float viewHeight = _surface.GetViewportRect().Size.Y;

                if (!Shown || viewHeight <= 0f)
                {
                    return 0f;
                }

                Layout(out Rect2 band, out _, out _, out _);

                return band.Size.Y / viewHeight;
            }
        }

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

        /// <summary>
        /// The bar's geometry for the current window: the band, the art
        /// scale, and the size of one button's art.
        /// </summary>
        private void Layout(out Rect2 band, out int artScale, out Vector2 art, out float spacing)
        {
            Vector2 view = _surface.GetViewportRect().Size;
            float scale = System.Math.Max(1f, Client.Game?.ScreenScale ?? 1f);
            int n = Actions.Length;

            ArtSize(out int artW, out int artH);

            // A whole-number scale for the art (rule 7: nearest-neighbour,
            // whole pixels), the largest at which six buttons and a gap
            // between each still fit; capped so a tablet does not get a
            // cartoon. A phone's 1920 wide at screen scale 2 lands on 3.
            float gap = 8f * scale;
            int cap = (int)System.Math.Ceiling(scale) * 2;
            int fits = (int)((view.X - (n + 1) * gap) / (n * artW));
            artScale = System.Math.Clamp(fits, 1, System.Math.Max(1, cap));

            art = new Vector2(artW * artScale, artH * artScale);

            // Padding above and below the art makes the band finger-tall:
            // 25 px of art at 3x plus 16 px of padding at 2x is 107 px, or
            // 7 mm at 369 dpi.
            float pad = 8f * scale;
            float height = art.Y + 2 * pad;
            band = new Rect2(0, view.Y - height, view.X, height);

            spacing = (view.X - n * art.X) / (n + 1);
        }

        private static void ArtSize(out int width, out int height)
        {
            width = FallbackWidth;
            height = FallbackHeight;

            if (Client.Game?.UO?.Gumps == null)
            {
                return;
            }

            ref readonly var info = ref Client.Game.UO.Gumps.GetGump(ButtonGump);

            if (info.Texture != null)
            {
                width = info.UV.Width;
                height = info.UV.Height;
            }
        }

        /// <summary>The rectangle of one button's art, in viewport pixels.</summary>
        private Rect2 ArtRect(string action)
        {
            int index = System.Array.IndexOf(Current, action);

            if (index < 0)
            {
                return default;
            }

            Layout(out Rect2 band, out _, out Vector2 art, out float spacing);

            float x = spacing + index * (art.X + spacing);
            float y = band.Position.Y + (band.Size.Y - art.Y) / 2;

            return new Rect2(x, y, art.X, art.Y);
        }

        /// <summary>
        /// The rectangle a finger has to land in for one button: the whole
        /// height of the band and half of each gap beside the art, so the
        /// target is wider than what is drawn.
        /// </summary>
        public Rect2 ButtonRect(string action)
        {
            int index = System.Array.IndexOf(Current, action);

            if (index < 0)
            {
                return default;
            }

            Layout(out Rect2 band, out _, out Vector2 art, out float spacing);

            float x = spacing / 2 + index * (art.X + spacing);

            return new Rect2(x, band.Position.Y, art.X + spacing, band.Size.Y);
        }

        /// <summary>Which button, if any, a point lands on.</summary>
        public bool HitTest(Vector2 at, out string action)
        {
            action = null;

            if (!Shown)
            {
                return false;
            }

            foreach (string a in Current)
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

            _pressed = action;
            _pressedAt = Godot.Time.GetTicksMsec();

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
                    // The say line. It already has the keyboard focus when
                    // nothing else does; what a phone lacks is the keyboard.
                    if (TouchInput.KeyboardShown)
                    {
                        TouchInput.HideKeyboard();
                    }
                    else
                    {
                        UIManager.SystemChat?.TextBoxControl?.SetKeyboardFocus();
                        TouchInput.ShowKeyboard(UIManager.SystemChat?.TextBoxControl?.Text ?? string.Empty, false);
                    }

                    break;

                case "self":
                    if (world.TargetManager.IsTargeting && world.Player != null)
                    {
                        world.TargetManager.Target(world.Player.Serial);
                    }

                    break;

                case "cancel":
                    if (world.TargetManager.IsTargeting)
                    {
                        world.TargetManager.CancelTarget();
                    }

                    break;

                case "options":
                    GameActions.OpenSettings(world);

                    break;
            }
        }

        /// <summary>The caption of a button: the top bar's cliloc, or its resource string.</summary>
        private static string Label(string action)
        {
            ClilocLoader cliloc = Client.Game?.UO?.FileManager?.Clilocs;

            switch (action)
            {
                case "paperdoll": return cliloc?.GetString(3000133, ResGumps.Paperdoll) ?? "Paperdoll";
                case "backpack": return cliloc?.GetString(3000431, ResGumps.Inventory) ?? "Inventory";
                case "journal": return cliloc?.GetString(3000129, ResGumps.Journal) ?? "Journal";
                case "map": return cliloc?.GetString(3000430, ResGumps.Map) ?? "Map";
                case "chat": return cliloc?.GetString(3000131, ResGumps.Chat) ?? "Chat";
                case "options": return "Options";
                case "self": return "Self";
                case "cancel": return "Cancel";
            }

            return action;
        }

        /// <summary>
        /// The caption drawn with the client's unicode font, as a texture:
        /// the same FontsLoader call RenderedText makes, kept as an image
        /// because this layer draws with Godot and not with the batcher.
        /// </summary>
        private Texture2D LabelTexture(string action)
        {
            if (_labels.TryGetValue(action, out Texture2D cached))
            {
                return cached;
            }

            FontsLoader fonts = Client.Game?.UO?.FileManager?.Fonts;

            if (fonts == null)
            {
                return null;
            }

            FontsLoader.FontInfo fi = fonts.GenerateUnicode(
                LabelFont, Label(action), 0, 30, 0, TEXT_ALIGN_TYPE.TS_LEFT, 0, false, 0
            );

            if (fi.Data == null || fi.Width <= 0 || fi.Height <= 0)
            {
                return null;
            }

            // A uint here is 0xAABBGGRR, so its bytes in memory are already
            // R,G,B,A; the same reasoning as TextureAtlas.AddSprite.
            var rgba = new byte[fi.Width * fi.Height * 4];
            System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(new System.ReadOnlySpan<uint>(fi.Data, 0, fi.Width * fi.Height))
                .CopyTo(rgba);

            Image image = Image.CreateFromData(fi.Width, fi.Height, false, Image.Format.Rgba8, rgba);
            Texture2D texture = ImageTexture.CreateFromImage(image);
            _labels[action] = texture;

            return texture;
        }

        public override void _ExitTree()
        {
            foreach (Texture2D t in _labels.Values)
            {
                t.Dispose();
            }

            _labels.Clear();
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

                // Pixel art, scaled by a whole number: never filtered (rule 7).
                TextureFilter = TextureFilterEnum.Nearest;
            }

            public override void _Draw()
            {
                if (GetParent() is not TouchGumpBar bar || !bar.Shown)
                {
                    return;
                }

                bar.Layout(out Rect2 band, out int artScale, out _, out _);

                // The band: one quiet strip so the buttons read as a bar.
                DrawRect(band, new Color(0f, 0f, 0f, 0.55f));

                Texture2D face = null;
                Rect2 faceUv = default;

                if (Client.Game?.UO?.Gumps != null)
                {
                    ref readonly var info = ref Client.Game.UO.Gumps.GetGump(ButtonGump);

                    if (info.Texture != null)
                    {
                        face = info.Texture;
                        faceUv = new Rect2(info.UV.X, info.UV.Y, info.UV.Width, info.UV.Height);
                    }
                }

                bool lit = bar._pressed != null && Godot.Time.GetTicksMsec() - bar._pressedAt < PressedMs;

                foreach (string action in Current)
                {
                    Rect2 r = bar.ArtRect(action);

                    if (face != null)
                    {
                        DrawTextureRectRegion(face, r, faceUv);
                    }
                    else
                    {
                        DrawRect(r, new Color(0.10f, 0.08f, 0.06f, 0.85f));
                        DrawRect(r, new Color(0.78f, 0.64f, 0.36f), false, System.Math.Max(1f, artScale));
                    }

                    Texture2D label = bar.LabelTexture(action);

                    if (label != null)
                    {
                        var size = new Vector2(label.GetWidth() * artScale, label.GetHeight() * artScale);
                        var at = new Vector2(
                            r.Position.X + (int)((r.Size.X - size.X) / 2),
                            r.Position.Y + (int)((r.Size.Y - size.Y) / 2)
                        );

                        DrawTextureRect(label, new Rect2(at, size), false);
                    }
                    else
                    {
                        Font font = ThemeDB.FallbackFont;
                        int fontSize = 11 * artScale;
                        string text = Label(action);
                        Vector2 size = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize);
                        var at = new Vector2(
                            r.Position.X + (r.Size.X - size.X) / 2,
                            r.Position.Y + (r.Size.Y + size.Y) / 2 - font.GetDescent(fontSize)
                        );

                        DrawString(font, at, text, HorizontalAlignment.Left, -1, fontSize, new Color(0.95f, 0.90f, 0.75f));
                    }

                    // The two target buttons are tinted, so the swap is seen.
                    if (action == "self")
                    {
                        DrawRect(r, new Color(0.25f, 0.45f, 1f, 0.30f));
                    }
                    else if (action == "cancel")
                    {
                        DrawRect(r, new Color(1f, 0.20f, 0.15f, 0.35f));
                    }

                    if (lit && action == bar._pressed)
                    {
                        DrawRect(r, new Color(1f, 1f, 1f, 0.25f));
                    }
                }
            }
        }
    }
}
