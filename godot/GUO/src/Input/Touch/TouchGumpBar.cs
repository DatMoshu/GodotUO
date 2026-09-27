// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Configuration;
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
    ///
    /// With the profile's TouchMacroRow on (a mobile default, v9), a chevron
    /// tab above the bar's right end shows and hides a second row: six of
    /// upstream's own macros (Target next, Attack last, Last target, Last
    /// object, Bandage self, War/Peace), run through the MacroManager as a
    /// macro button gump runs them. The row comes up by itself when the
    /// player enters War mode, unless the player hid it with the chevron
    /// since entering the world.
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

        /// <summary>Every macro a row slot can hold, in the order Options lists them.</summary>
        public static readonly string[] MacroChoices =
        {
            "m:nearest", "m:next", "m:attack", "m:last", "m:object", "m:bandage", "m:war",
        };

        /// <summary>The row a profile starts with: Nearest Hostile first, as the owner asked.</summary>
        public const string DefaultMacroSlots = "m:nearest,m:attack,m:next,m:last,m:bandage,m:war";

        public const int MacroSlotCount = 6;

        /// <summary>
        /// The macro row's buttons, left to right, from the profile
        /// (Options, "Macro row slot N"); MacroFor names the macro each runs.
        /// An unknown or missing slot falls back to the default for that slot.
        /// </summary>
        public static string[] MacroActions
        {
            get
            {
                string[] defaults = DefaultMacroSlots.Split(',');
                string[] saved = (ProfileManager.CurrentProfile?.TouchMacroSlots ?? DefaultMacroSlots).Split(',');
                var slots = new string[MacroSlotCount];

                for (int i = 0; i < MacroSlotCount; i++)
                {
                    string s = i < saved.Length ? saved[i].Trim() : null;
                    slots[i] = System.Array.IndexOf(MacroChoices, s) >= 0 ? s : defaults[i];
                }

                return slots;
            }
        }

        /// <summary>The upstream macro subtype a row action runs with.</summary>
        private static MacroSubType SubFor(string action) =>
            action == "m:nearest" ? MacroSubType.Hostile : MacroSubType.MSC_NONE;

        private static MacroType MacroFor(string action)
        {
            switch (action)
            {
                // Upstream's "Select Nearest" macro with its Hostile scan: the
                // nearest gray, criminal, enemy or murderer becomes the last
                // target (World.FindNearest), as a ClassicUO player's macro does.
                case "m:nearest": return MacroType.SelectNearest;
                case "m:next": return MacroType.TargetNext;
                case "m:attack": return MacroType.AttackLast;
                case "m:last": return MacroType.LastTarget;
                case "m:object": return MacroType.LastObject;
                case "m:bandage": return MacroType.BandageSelf;
                case "m:war": return MacroType.WarPeace;
            }

            return MacroType.None;
        }

        /// <summary>The chevron tab's name, as HitTest reports it.</summary>
        public const string Chevron = "chevron";

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

        /// <summary>Whether the profile offers the chevron and the macro row.</summary>
        public bool ChevronShown => Shown && (ProfileManager.CurrentProfile?.TouchMacroRow ?? false);

        /// <summary>Whether the macro row is up.</summary>
        public bool RowShown => ChevronShown && _rowOpen;

        /// <summary>Whether the player hid the row with the chevron since entering the world.</summary>
        public bool HiddenThisSession { get; private set; }

        private bool _rowOpen;
        private bool _wasWar;

        /// <summary>
        /// Start the row's session state over, as entering the world does:
        /// down, not hidden, and the current stance taken as already seen. For
        /// the touch probe, whose character may log in already at war.
        /// </summary>
        internal void ResetSession()
        {
            _rowOpen = false;
            HiddenThisSession = false;
            _wasWar = Client.Game?.UO?.World?.Player?.InWarMode ?? false;
        }

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

                return band.Size.Y * (RowShown ? 2 : 1) / viewHeight;
            }
        }

        public override void _Ready()
        {
            Layer = 10;
            AddChild(_surface);
        }

        public override void _Process(double delta)
        {
            // The idle screen saver draws in the client's canvas, under this
            // layer; a bar left lit on an OLED panel is what it is there to
            // prevent, so the bar goes with it.
            Visible = !GUO.Game.Managers.ScreenSaver.Active;

            bool inGame = Client.Game?.UO?.World?.InGame ?? false;

            if (inGame != Shown)
            {
                Shown = inGame;
                _surface.QueueRedraw();

                // A new session: the row starts down, and entering War mode
                // may bring it up again.
                _rowOpen = false;
                HiddenThisSession = false;
                _wasWar = false;
            }

            if (Shown)
            {
                bool war = Client.Game.UO.World.Player?.InWarMode ?? false;

                if (war && !_wasWar && ChevronShown && !HiddenThisSession && !_rowOpen)
                {
                    _rowOpen = true;
                    TouchInput.Note("macro row: up on War mode");
                }

                _wasWar = war;

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

        /// <summary>
        /// Which band a button sits in and its place along it: the bar's own
        /// row, or the macro row directly above it. False if it is in neither.
        /// </summary>
        private bool Place(string action, out Rect2 band, out int index, out Vector2 art, out float spacing)
        {
            Layout(out band, out _, out art, out spacing);
            index = System.Array.IndexOf(Current, action);

            if (index >= 0)
            {
                return true;
            }

            index = System.Array.IndexOf(MacroActions, action);

            if (index < 0)
            {
                return false;
            }

            band = RowBand(band);

            return true;
        }

        /// <summary>The macro row's band: the bar's band, moved up by its own height.</summary>
        private static Rect2 RowBand(Rect2 band) => new(band.Position.X, band.Position.Y - band.Size.Y, band.Size.X, band.Size.Y);

        /// <summary>
        /// The chevron tab: two button heights wide and one tall, on top of
        /// the upper row, its right edge flush with the last button's.
        /// </summary>
        public Rect2 ChevronRect()
        {
            Layout(out Rect2 band, out _, out Vector2 art, out float spacing);
            Rect2 top = RowShown ? RowBand(band) : band;
            var size = new Vector2(art.Y * 2, art.Y);

            // The profile's inset, in client px, moves it in from the corner,
            // where a hand holding a handheld rests.
            float inset = (ProfileManager.CurrentProfile?.TouchChevronInset ?? 0) * (Client.Game?.DpiScale ?? 1f);
            float x = System.Math.Max(0f, top.End.X - spacing - size.X - inset);

            return new Rect2(x, top.Position.Y - size.Y, size.X, size.Y);
        }

        /// <summary>The rectangle of one button's art, in viewport pixels.</summary>
        private Rect2 ArtRect(string action)
        {
            if (!Place(action, out Rect2 band, out int index, out Vector2 art, out float spacing))
            {
                return default;
            }

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
            if (action == Chevron)
            {
                return ChevronRect();
            }

            if (!Place(action, out Rect2 band, out int index, out Vector2 art, out float spacing))
            {
                return default;
            }

            float x = spacing / 2 + index * (art.X + spacing);

            return new Rect2(x, band.Position.Y, art.X + spacing, band.Size.Y);
        }

        // --- minimised gumps (GumpMinimise) -----------------------------------

        private int _chipFirst;

        /// <summary>
        /// The chips of minimised gumps: one row on top of the upper row, left
        /// of the chevron. When they do not all fit, the row shows as many as
        /// fit from <see cref="_chipFirst"/> and "‹" / "›" chips page it.
        /// Actions are "chip:N" (N into GumpMinimise.Gumps), "chips:prev", "chips:next".
        /// </summary>
        private List<(string action, Rect2 rect)> ChipRects()
        {
            var result = new List<(string, Rect2)>();
            IReadOnlyList<Game.UI.Gumps.Gump> gumps = GumpMinimise.Gumps;

            if (!Shown || gumps.Count == 0)
            {
                _chipFirst = 0;
                return result;
            }

            Layout(out Rect2 band, out int artScale, out Vector2 art, out float spacing);
            Rect2 top = RowShown ? RowBand(band) : band;
            float h = art.Y, gap = spacing / 3f;
            float y = top.Position.Y - h - gap / 2f;
            float left = spacing / 2f;
            float right = (ChevronShown ? ChevronRect().Position.X : top.End.X) - gap;
            float arrowW = h * 1.2f;

            float Width(int i) => System.Math.Max(h * 2.4f, (LabelTexture("chip:" + i)?.GetWidth() ?? 60) * artScale + h);

            _chipFirst = System.Math.Clamp(_chipFirst, 0, gumps.Count - 1);
            bool before = _chipFirst > 0;
            float x = left + (before ? arrowW + gap : 0);

            if (before)
            {
                result.Add(("chips:prev", new Rect2(left, y, arrowW, h)));
            }

            for (int i = _chipFirst; i < gumps.Count; i++)
            {
                float w = Width(i);
                bool last = i == gumps.Count - 1;
                float limit = last ? right : right - arrowW - gap;

                if (x + w > limit && i > _chipFirst)
                {
                    result.Add(("chips:next", new Rect2(right - arrowW, y, arrowW, h)));
                    break;
                }

                result.Add(("chip:" + i, new Rect2(x, y, w, h)));
                x += w + gap;
            }

            return result;
        }

        /// <summary>For the probe: the rectangle of the chip for this gump, if shown.</summary>
        public Rect2? ChipRect(Game.UI.Gumps.Gump g)
        {
            int index = -1;
            IReadOnlyList<Game.UI.Gumps.Gump> gumps = GumpMinimise.Gumps;

            for (int i = 0; i < gumps.Count; i++)
            {
                if (gumps[i] == g) index = i;
            }

            foreach ((string action, Rect2 rect) in ChipRects())
            {
                if (action == "chip:" + index) return rect;
            }

            return null;
        }

        /// <summary>Which button, if any, a point lands on.</summary>
        public bool HitTest(Vector2 at, out string action)
        {
            action = null;

            if (!Shown)
            {
                return false;
            }

            foreach ((string a, Rect2 r) in ChipRects())
            {
                if (r.HasPoint(at))
                {
                    action = a;

                    return true;
                }
            }

            if (ChevronShown && ChevronRect().HasPoint(at))
            {
                action = Chevron;

                return true;
            }

            foreach (string a in Current)
            {
                if (ButtonRect(a).HasPoint(at))
                {
                    action = a;

                    return true;
                }
            }

            if (RowShown)
            {
                foreach (string a in MacroActions)
                {
                    if (ButtonRect(a).HasPoint(at))
                    {
                        action = a;

                        return true;
                    }
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

            if (action.StartsWith("chip"))
            {
                IReadOnlyList<Game.UI.Gumps.Gump> gumps = GumpMinimise.Gumps;

                if (action == "chips:prev") _chipFirst = System.Math.Max(0, _chipFirst - 1);
                else if (action == "chips:next") _chipFirst = System.Math.Min(gumps.Count - 1, _chipFirst + 1);
                else if (int.TryParse(action.Substring(5), out int i) && i >= 0 && i < gumps.Count)
                {
                    TouchInput.Note($"chip -> restore {gumps[i].GetType().Name}");
                    GumpMinimise.Restore(gumps[i]);
                }

                return;
            }

            if (action == Chevron)
            {
                _rowOpen = !_rowOpen;

                if (!_rowOpen)
                {
                    HiddenThisSession = true;
                }

                return;
            }

            MacroType macro = MacroFor(action);

            if (macro != MacroType.None)
            {
                // As MacroButtonGump.RunMacro runs a macro button.
                Macro m = Macro.CreateFastMacro(action, macro, SubFor(action));
                world.Macros.SetMacroToExecute(m.Items as MacroObject);
                world.Macros.WaitForTargetTimer = 0;
                world.Macros.Update();

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
        /// <summary>A macro action's caption, for Options' slot lists.</summary>
        public static string MacroTitle(string action) => Label(action);

        private static string Label(string action)
        {
            if (action.StartsWith("chip:") && int.TryParse(action.Substring(5), out int ci))
            {
                IReadOnlyList<Game.UI.Gumps.Gump> gumps = GumpMinimise.Gumps;
                return ci >= 0 && ci < gumps.Count ? GumpMinimise.Title(gumps[ci]) : "";
            }

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
                case "chips:prev": return "‹";
                case "chips:next": return "›";
                case "m:nearest": return "Nearest Hostile";
                case "m:next": return "Next Target";
                case "m:attack": return "Attack Last";
                case "m:last": return "Last Target";
                case "m:object": return "Last Object";
                case "m:bandage": return "Bandage Self";
                case "m:war":
                    return Client.Game?.UO?.World?.Player?.InWarMode == true ? "Peace" : "War";
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
            // Cached by caption: War/Peace changes with the stance.
            string caption = Label(action);

            if (_labels.TryGetValue(caption, out Texture2D cached))
            {
                return cached;
            }

            FontsLoader fonts = Client.Game?.UO?.FileManager?.Fonts;

            if (fonts == null)
            {
                return null;
            }

            FontsLoader.FontInfo fi = fonts.GenerateUnicode(
                LabelFont, caption, 0, 30, 0, TEXT_ALIGN_TYPE.TS_LEFT, 0, false, 0
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
            _labels[caption] = texture;

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

            private static bool lit0(TouchGumpBar bar, string action) =>
                bar._pressed == action && Godot.Time.GetTicksMsec() - bar._pressedAt < PressedMs;

            public override void _Draw()
            {
                if (GetParent() is not TouchGumpBar bar || !bar.Shown)
                {
                    return;
                }

                bar.Layout(out Rect2 band, out int artScale, out _, out _);

                // The band: one quiet strip so the buttons read as a bar.
                DrawRect(band, new Color(0f, 0f, 0f, 0.55f));

                if (bar.RowShown)
                {
                    DrawRect(RowBand(band), new Color(0f, 0f, 0f, 0.55f));
                }

                if (bar.ChevronShown)
                {
                    DrawChevron(bar.ChevronRect(), bar.RowShown, artScale);
                }

                // Minimised gumps (GumpMinimise): a chip each, tap to restore.
                foreach ((string chip, Rect2 r) in bar.ChipRects())
                {
                    bool chipLit = lit0(bar, chip);
                    DrawRect(r, new Color(0.08f, 0.10f, 0.09f, 0.92f));
                    DrawRect(r, new Color(0.87f, 0.73f, 0.47f, chipLit ? 1f : 0.7f), false, System.Math.Max(1f, artScale * 0.67f));

                    Texture2D chipLabel = bar.LabelTexture(chip);

                    if (chipLabel != null)
                    {
                        var size = new Vector2(chipLabel.GetWidth() * artScale, chipLabel.GetHeight() * artScale);
                        var at = new Vector2(r.Position.X + (int)((r.Size.X - size.X) / 2), r.Position.Y + (int)((r.Size.Y - size.Y) / 2));
                        DrawTextureRect(chipLabel, new Rect2(at, size), false);
                    }

                    if (chipLit)
                    {
                        DrawRect(r, new Color(1f, 1f, 1f, 0.25f));
                    }
                }

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

                var actions = new List<string>(Current);

                if (bar.RowShown)
                {
                    actions.AddRange(MacroActions);
                }

                foreach (string action in actions)
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

                    // The macro row reads as a row of its own: a warm tint.
                    if (action.StartsWith("m:"))
                    {
                        DrawRect(r, new Color(0.85f, 0.55f, 0.10f, 0.22f));
                    }

                    if (lit && action == bar._pressed)
                    {
                        DrawRect(r, new Color(1f, 1f, 1f, 0.25f));
                    }
                }
            }

            /// <summary>
            /// The chevron tab: a dark tab on the band below it, with a gold
            /// arrow that points up while the row is down (tap to raise it)
            /// and down while it is up.
            /// </summary>
            private void DrawChevron(Rect2 r, bool rowUp, int artScale)
            {
                DrawRect(r, new Color(0f, 0f, 0f, 0.55f));
                DrawRect(r, new Color(0.78f, 0.64f, 0.36f), false, System.Math.Max(1f, artScale));

                Vector2 c = r.GetCenter();
                float w = r.Size.Y * 0.45f;
                float h = r.Size.Y * 0.22f;
                float dir = rowUp ? 1f : -1f;

                DrawColoredPolygon(
                    new[]
                    {
                        new Vector2(c.X - w, c.Y - dir * h),
                        new Vector2(c.X + w, c.Y - dir * h),
                        new Vector2(c.X, c.Y + dir * h),
                    },
                    new Color(0.95f, 0.80f, 0.40f)
                );
            }
        }
    }
}
