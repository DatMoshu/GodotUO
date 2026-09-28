// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Assets;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Platform.Android;
using GUO.Renderer;
using GUO.Resources;
using Rectangle = GUO.Compat.Rectangle;

namespace GUO.Input.Touch
{
    /// <summary>
    /// The command bar: up to three rows of ten finger-sized buttons along
    /// the bottom of the screen, under a handle strip that holds the arrow,
    /// the last target and the minimised-window chips. Row 1 is always up;
    /// the arrow opens rows 2 and 3 above it. The design is
    /// docs/ui/command_bar.md.
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
    /// touch layer hit-tests the bar itself, runs a button with
    /// <see cref="Invoke"/> when the finger lifts on it, and hands a finger on
    /// the handle to <see cref="BeginDrag"/>.
    ///
    /// Every button runs what upstream already has: the top bar's
    /// <c>GameActions</c> calls, or one of upstream's macros, run through the
    /// MacroManager as a macro button gump runs it. The slots are the
    /// profile's (Options, "Command bar"), saved by action name.
    ///
    /// It is drawn from the client's own pieces: the top bar's button gump
    /// (0x098D) cropped to a cell, the small button (0x098B) for the arrow
    /// tab and the chips, the scroll arrows (0x0983, 0x0985), and the
    /// client's unicode font 1, all at whole-number scales and sampled
    /// nearest-neighbour. Without the gumps (an old client) it falls back
    /// to flat plates in the same proportions.
    /// </remarks>
    internal sealed partial class TouchGumpBar : CanvasLayer
    {
        // --- the slots ----------------------------------------------------

        /// <summary>Every action a slot can hold, in the order Options lists them.</summary>
        public static readonly string[] Choices =
        {
            "paperdoll", "backpack", "journal", "map", "chat", "war", "nearest", "attack", "last", "bandage",
            "next", "object", "heal", "cure", "ability1", "ability2", "lastspell", "lastskill", "armdisarm", "status",
            "skills", "spellbook", "allnames", "door", "follow", "stop", "bank", "guards", "party", "options",
        };

        /// <summary>
        /// The slots a profile starts with, row 1 first: the director's
        /// proposal, taken from docs/second-screen-ui-research.md (P0 first).
        /// </summary>
        public const string DefaultSlots =
            "paperdoll,backpack,journal,map,chat,war,nearest,attack,last,bandage,"
            + "next,object,heal,cure,ability1,ability2,lastspell,lastskill,armdisarm,status,"
            + "skills,spellbook,allnames,door,follow,stop,bank,guards,party,options";

        public const int RowCount = 3;
        public const int PerRow = 10;

        /// <summary>The handle's name, as HitTest reports it.</summary>
        public const string Handle = "handle";

        /// <summary>
        /// The thirty slots from the profile, row 1 first. An unknown or
        /// missing slot falls back to the default for that slot.
        /// </summary>
        public static string[] Slots
        {
            get
            {
                // Parsed again only when the profile's string changes: the
                // bar asks for its slots several times a frame.
                string source = ProfileManager.CurrentProfile?.TouchBarSlots ?? DefaultSlots;

                if (ReferenceEquals(source, _slotsSource) && _slots != null)
                {
                    return _slots;
                }

                string[] defaults = DefaultSlots.Split(',');
                string[] saved = source.Split(',');
                var slots = new string[RowCount * PerRow];

                for (int i = 0; i < slots.Length; i++)
                {
                    string s = i < saved.Length ? saved[i].Trim() : null;
                    slots[i] = System.Array.IndexOf(Choices, s) >= 0 ? s : defaults[i];
                }

                _slotsSource = source;
                _slots = slots;
                _rowsCache = null;

                return slots;
            }
        }

        private static string _slotsSource;
        private static string[] _slots;
        private static string[][] _rowsCache;
        private static bool _rowsTargeting;

        /// <summary>
        /// The buttons of row <paramref name="row"/> (1 to 3) as they are now.
        /// While a target cursor is up, Chat and War/Peace give way in place
        /// to Self and Cancel, so nothing moves under a thumb: a phone has no
        /// Esc to cancel a target.
        /// </summary>
        public static string[] Row(int row)
        {
            string[] slots = Slots;
            bool targeting = Client.Game?.UO?.World?.TargetManager?.IsTargeting == true;

            if (_rowsCache == null || _rowsTargeting != targeting)
            {
                _rowsCache = new string[RowCount][];
                _rowsTargeting = targeting;

                for (int r = 0; r < RowCount; r++)
                {
                    var buttons = new string[PerRow];

                    for (int i = 0; i < PerRow; i++)
                    {
                        string a = slots[r * PerRow + i];
                        buttons[i] = !targeting ? a : a == "chat" ? "self" : a == "war" ? "cancel" : a;
                    }

                    _rowsCache[r] = buttons;
                }
            }

            return _rowsCache[row - 1];
        }

        // --- the art ------------------------------------------------------

        /// <summary>The top bar's wide button: every command's plate, cropped to its cell.</summary>
        private const ushort PlateGump = 0x098D;

        /// <summary>The small button: the arrow tab and the chips.</summary>
        private const ushort SmallPlateGump = 0x098B;

        /// <summary>The scroll arrows, up and down, for the arrow tab.</summary>
        private const ushort ArrowUpGump = 0x0983;
        private const ushort ArrowDownGump = 0x0985;

        /// <summary>The plate's height in art pixels, and a row's (plate plus padding).</summary>
        private const int PlateArt = 23;
        private const int RowArt = 34;

        /// <summary>The handle strip's height in art pixels.</summary>
        private const int StripArt = 24;

        /// <summary>The arrow tab's width in art pixels (the small plate, cropped).</summary>
        private const int TabArt = 42;

        /// <summary>The gap between two plates, in art pixels.</summary>
        private const int GapArt = 5;

        /// <summary>The top bar's font for its captions, and the scale captions are drawn at.</summary>
        private const byte LabelFont = 1;
        private const int CaptionScale = 2;

        /// <summary>Milliseconds a run button stays lit.</summary>
        private const ulong PressedMs = 140;

        // The palette (docs/ui/command_bar.md). Band and plates come from the
        // art; these are the only colours the bar adds.
        private static readonly Color Band = new(0f, 0f, 0f, 0.55f);
        private static readonly Color Gold = new Color("e0b050");
        private static readonly Color InnocentBlue = new Color("3c8cf0");
        private static readonly Color MurdererRed = new Color("e6281e");
        private static readonly Color HitsColour = new Color("b8483e");
        private static readonly Color ManaColour = new Color("3f6fc4");
        private static readonly Color StaminaColour = new Color("c9a23b");
        private static readonly Color PoisonColour = new Color("3cc83c");
        private static readonly Color YellowHitsColour = new Color("f0e61e");
        private static readonly Color StripEmpty = new(0.12f, 0.12f, 0.12f, 0.85f);

        // --- state --------------------------------------------------------

        private readonly Surface _surface = new();
        private readonly RowsSurface _rowsSurface = new();

        /// <summary>Caption textures, by caption and ink (null: the font's own).</summary>
        private readonly Dictionary<(string caption, Color? ink), Texture2D> _labels = new();

        /// <summary>
        /// Captions kept before the cache starts over: chips and the target's
        /// name come and go, so it would only grow.
        /// </summary>
        private const int LabelCacheLimit = 64;

        private string _pressed;
        private ulong _pressedAt;
        private string _held;

        /// <summary>Whether the bar is drawn and takes taps.</summary>
        public bool Shown { get; private set; }

        /// <summary>Whether the profile offers the handle and rows 2 and 3.</summary>
        public bool HandleShown => Shown && (ProfileManager.CurrentProfile?.TouchMacroRow ?? false);

        /// <summary>The rows the bar is open to, or settling to: 1 to 3.</summary>
        public int RowsOpen => HandleShown ? System.Math.Clamp((int)System.Math.Round(_target), 1, RowCount) : 1;

        /// <summary>The bar's height in rows as drawn now: fractional while it moves.</summary>
        public float Height => HandleShown ? _height : 1f;

        /// <summary>Whether the bar is still moving: a drag, or the settle after one.</summary>
        public bool Moving => _dragging || _height != _target;

        /// <summary>Whether the player closed the rows since entering the world.</summary>
        public bool HiddenThisSession { get; private set; }

        private float _height = 1f;
        private float _target = 1f;
        private bool _wasWar;

        /// <summary>
        /// For the touch probe: the time the bar has spent in its own
        /// _Process and _Draw, in Stopwatch ticks, and how many times it drew.
        /// </summary>
        internal static long CostTicks;
        internal static int DrawCount;

        /// <summary>
        /// Start the rows' session state over, as entering the world does:
        /// one row, not hidden, and the current stance taken as already seen.
        /// For the probes, whose character may log in already at war.
        /// </summary>
        internal void ResetSession()
        {
            _height = _target = 1f;
            _dragging = false;
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

                return (viewHeight - TopEdge()) / viewHeight;
            }
        }

        public override void _Ready()
        {
            Layer = 10;
            AddChild(_surface);
            _surface.AddChild(_rowsSurface);
        }

        public override void _Process(double delta)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ProcessBar();
            CostTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        private void ProcessBar()
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
                _rowsSurface.QueueRedraw();

                // A new session: one row, and entering War mode may open two.
                _height = _target = 1f;
                _dragging = false;
                HiddenThisSession = false;
                _wasWar = false;
            }

            if (!Shown)
            {
                return;
            }

            bool war = Client.Game.UO.World.Player?.InWarMode ?? false;

            if (war && !_wasWar && HandleShown && !HiddenThisSession && RowsOpen < 2)
            {
                SettleTo(2, false);
                TouchInput.Note("bar: two rows on War mode");
            }

            _wasWar = war;

            Settle();

            // The rows' clip follows the handle: rows are revealed under it.
            Layout(out Geometry geo);
            _rowsSurface.Position = new Vector2(0, geo.StripBottom);
            _rowsSurface.Size = new Vector2(geo.View.X, System.Math.Max(0f, geo.View.Y - geo.StripBottom));

            // Drawn again only when something it shows has changed: the
            // window or its scale, the height, a caption, a chip, the target,
            // a lit button.
            int look = Look();

            if (look != _drawnLook)
            {
                _drawnLook = look;
                _surface.QueueRedraw();
                _rowsSurface.QueueRedraw();
            }

            // The sweep runs when a gump could have come under the bar:
            // the bar grew, a gump opened, a finger let go of one (a drag,
            // a pinch, a flick), and every half second for what moves a
            // gump without a finger (the server, the window menu).
            float top = TopEdge();
            int lifts = TouchInput.Lifts;
            int count = UIManager.Gumps.Count;

            if (top != _sweptTop || lifts != _sweptLifts || count != _sweptCount || --_sweepIn <= 0)
            {
                _sweptTop = top;
                _sweptLifts = lifts;
                _sweptCount = count;
                _sweepIn = SweepFrames;
                KeepGumpsAboveBar();
            }
        }

        /// <summary>Frames between the gump sweeps nothing asked for.</summary>
        private const int SweepFrames = 30;

        private int _drawnLook;
        private float _sweptTop = -1f;
        private int _sweptLifts = -1;
        private int _sweptCount = -1;
        private int _sweepIn;

        /// <summary>
        /// The top of the open rows, in viewport pixels: what gumps are kept
        /// above. The handle strip is not reserved: it has no band, only the
        /// arrow tab, the target and the chips, so a gump may sit under it
        /// (seen on the Odin: Options' Cancel/Apply row fits under the strip
        /// and not above it, as it did under the old bar).
        /// </summary>
        private float TopEdge()
        {
            Layout(out Geometry geo);
            return geo.StripBottom;
        }

        /// <summary>
        /// Everything the bar's picture depends on, folded into one number:
        /// when it changes, the bar is drawn again.
        /// </summary>
        private int Look()
        {
            Layout(out Geometry geo);
            World world = Client.Game.UO.World;
            var h = new System.HashCode();
            h.Add(geo.View);
            h.Add(geo.Scale);
            h.Add(geo.StripTop);
            h.Add(HandleShown);
            h.Add(_dragging);
            h.Add(world.TargetManager?.IsTargeting ?? false);
            h.Add(world.Player?.InWarMode ?? false);
            h.Add(ProfileManager.CurrentProfile?.TouchBarSlots);
            h.Add(ProfileManager.CurrentProfile?.TouchChevronInset ?? 0);
            h.Add(_held);
            h.Add(_pressed != null && Godot.Time.GetTicksMsec() - _pressedAt < PressedMs ? _pressed : null);
            h.Add(_chipFirst);
            h.Add(Client.Game.UO.FileManager?.Fonts != null);

            IReadOnlyList<Game.UI.Gumps.Gump> gumps = GumpMinimise.Gumps;
            h.Add(gumps.Count);

            for (int i = 0; i < gumps.Count; i++)
            {
                h.Add(gumps[i]);
            }

            if (Target(out Mobile m))
            {
                h.Add(m.Serial);
                h.Add(m.Name);
                h.Add(m.NotorietyFlag);
                h.Add(m.Hits);
                h.Add(m.HitsMax);
                h.Add(m.Mana);
                h.Add(m.ManaMax);
                h.Add(m.Stamina);
                h.Add(m.StaminaMax);
                h.Add(m.IsPoisoned);
                h.Add(m.IsYellowHits);
            }

            return h.ToHashCode();
        }

        /// <summary>
        /// Keep every gump on the main screen clear of the bar: a gump whose
        /// bottom edge runs under it moves up (to the top, if it is taller than
        /// the room). Covers a gump's first open, a drag, and the rows opening
        /// under a gump. Seen on the Odin: the Options gump's
        /// Cancel/Apply/Default/Okay row sat under the bar. The world view and
        /// the top bar are left alone, as are hidden gumps and anything on the
        /// second screen, which has no bar.
        /// </summary>
        private static void KeepGumpsAboveBar()
        {
            if (Client.Game == null)
            {
                return;
            }

            int bottom = GumpPresentation.DisplayBounds(false).Height;
            int mainWidth = GUO.Platform.Android.DualScreen.ShelfOn ? GUO.Platform.Android.DualScreen.MainWidth : int.MaxValue;

            foreach (Game.UI.Gumps.Gump g in UIManager.Gumps)
            {
                if (g.IsDisposed || !g.IsVisible || g.Height <= 0 || g.X >= mainWidth
                    || g is Game.UI.Gumps.WorldViewportGump || g is Game.UI.Gumps.TopBarGump)
                {
                    continue;
                }

                int h = GumpPresentation.Supports(g) ? GumpPresentation.Height(g) : g.Height;

                if (g.Y + h > bottom)
                {
                    g.Y = System.Math.Max(0, bottom - h);
                }
            }
        }

        // --- geometry -----------------------------------------------------

        /// <summary>The bar's geometry for the current window and height.</summary>
        private struct Geometry
        {
            public Vector2 View;

            /// <summary>The art scale: whole pixels, nearest-neighbour (rule 7).</summary>
            public int Scale;

            /// <summary>One cell's width, a row's height, a plate's size.</summary>
            public float Cell;
            public float RowHeight;
            public Vector2 Plate;

            /// <summary>The handle strip, above the open rows.</summary>
            public float StripTop;
            public float StripBottom;
        }

        private ulong _layoutFrame = ulong.MaxValue;
        private float _layoutHeight = -1f;
        private Geometry _geo;

        /// <summary>The geometry, worked out once a frame (and again if the height moved).</summary>
        private void Layout(out Geometry geo)
        {
            ulong frame = Engine.GetProcessFrames();
            float height = Height;

            if (frame != _layoutFrame || height != _layoutHeight)
            {
                _layoutFrame = frame;
                _layoutHeight = height;
                _geo = ComputeLayout(height);
            }

            geo = _geo;
        }

        private Geometry ComputeLayout(float height)
        {
            var g = new Geometry { View = _surface.GetViewportRect().Size };

            // The largest whole-number scale at which ten plates fit across,
            // each at least 64 art px of cell (the plate's 58 plus the gap);
            // capped so a tablet does not get a cartoon. 1920 wide lands on 3.
            float screen = System.Math.Max(1f, Client.Game?.ScreenScale ?? 1f);
            int cap = (int)System.Math.Ceiling(screen) * 2;
            g.Cell = g.View.X / PerRow;
            g.Scale = System.Math.Clamp((int)(g.Cell / 64f), 1, System.Math.Max(1, cap));

            int s = g.Scale;
            g.RowHeight = RowArt * s;

            // The plate: the cell less the gap, in whole art pixels, and no
            // wider than the gump itself (it is cropped, never stretched).
            int plateArt = System.Math.Max(8, (int)((g.Cell - GapArt * s) / s));

            if (Art(PlateGump, out _, out Rect2 plateUv))
            {
                plateArt = System.Math.Min(plateArt, (int)plateUv.Size.X);
            }

            g.Plate = new Vector2(plateArt * s, PlateArt * s);

            g.StripBottom = g.View.Y - height * g.RowHeight;
            g.StripTop = HandleShown ? g.StripBottom - StripArt * s : g.StripBottom;

            return g;
        }

        /// <summary>The top edge of row <paramref name="row"/> (1 is the bottom row).</summary>
        private static float RowTop(in Geometry geo, int row) => geo.View.Y - row * geo.RowHeight;

        /// <summary>
        /// The rectangle a finger has to land in for one slot: the whole cell,
        /// so the target is wider and taller than the plate drawn in it.
        /// </summary>
        public Rect2 SlotRect(int row, int index)
        {
            Layout(out Geometry geo);
            return new Rect2(index * geo.Cell, RowTop(geo, row), geo.Cell, geo.RowHeight);
        }

        /// <summary>The plate drawn in a slot's cell, in viewport pixels.</summary>
        private static Rect2 PlateRect(in Geometry geo, int row, int index)
        {
            float x = index * geo.Cell + (int)((geo.Cell - geo.Plate.X) / 2);
            float y = RowTop(geo, row) + (int)((geo.RowHeight - geo.Plate.Y) / 2);

            return new Rect2(x, y, geo.Plate.X, geo.Plate.Y);
        }

        /// <summary>Whether a row is wholly revealed, so its buttons take taps.</summary>
        private bool RowUp(in Geometry geo, int row) => row == 1 || HandleShown && RowTop(geo, row) >= geo.StripBottom - 0.5f;

        /// <summary>
        /// The rectangle of the first slot holding <paramref name="action"/> in
        /// an open row, or of the handle; empty if it is not on the bar.
        /// </summary>
        public Rect2 ButtonRect(string action)
        {
            if (action == Handle)
            {
                return HandleRect();
            }

            Layout(out Geometry geo);

            for (int r = 1; r <= RowCount; r++)
            {
                if (!RowUp(geo, r))
                {
                    continue;
                }

                int i = System.Array.IndexOf(Row(r), action);

                if (i >= 0)
                {
                    return SlotRect(r, i);
                }
            }

            return default;
        }

        /// <summary>
        /// The arrow tab: the small plate on the handle strip's right end,
        /// over the last column. The profile's inset (client px) moves it in
        /// from the corner, where a hand holding a handheld rests.
        /// </summary>
        public Rect2 HandleRect()
        {
            Layout(out Geometry geo);
            int s = geo.Scale;
            var size = new Vector2(TabArt * s, PlateArt * s);
            float inset = (ProfileManager.CurrentProfile?.TouchChevronInset ?? 0) * (Client.Game?.DpiScale ?? 1f);
            float x = System.Math.Max(0f, geo.View.X - geo.Cell / 2 - size.X / 2 - inset);
            float y = geo.StripTop + (int)((geo.StripBottom - geo.StripTop - size.Y) / 2);

            return new Rect2((int)x, y, size.X, size.Y);
        }

        /// <summary>Where a finger takes the handle: the arrow tab, a gap wider each side, the strip's full height.</summary>
        private Rect2 GrabRect()
        {
            Layout(out Geometry geo);
            Rect2 tab = HandleRect();
            float margin = GapArt * geo.Scale;

            return new Rect2(tab.Position.X - margin, geo.StripTop, tab.Size.X + 2 * margin, geo.StripBottom - geo.StripTop);
        }

        // --- the handle: tap, drag, settle ----------------------------------

        /// <summary>Milliseconds the bar takes to settle, and the overshoot's strength.</summary>
        private const float SettleMs = 200f;
        private const float Overshoot = 1.2f;

        /// <summary>A release faster than this, in px per ms, is a flick: all the way.</summary>
        private const float FlickSpeed = 0.5f;

        /// <summary>Past one or three rows the bar moves at this share of the finger, up to RubberMax px.</summary>
        private const float Rubber = 0.3f;
        private const float RubberMax = 30f;

        /// <summary>Milliseconds of a snap's tick, when vibration is on.</summary>
        private const int SnapVibrateMs = 15;

        private bool _dragging;
        private float _dragFromY;
        private float _dragFromHeight;
        private readonly Queue<(ulong ms, float y)> _dragSamples = new();

        private float _settleFrom;
        private ulong _settleAt;
        private bool _settleTicks;

        /// <summary>
        /// A tap on the handle. At one row it opens two; at two or three it
        /// closes back to one.
        /// </summary>
        public void TapHandle()
        {
            if (!HandleShown)
            {
                return;
            }

            int to = RowsOpen == 1 ? 2 : 1;
            SettleTo(to, true);
            TouchInput.Note($"handle tap -> {to} rows");
        }

        /// <summary>A finger took the handle and has moved: the bar follows it from here.</summary>
        public void BeginDrag(Vector2 at)
        {
            if (!HandleShown)
            {
                return;
            }

            _dragging = true;
            _dragFromY = at.Y;
            _dragFromHeight = _height;
            _dragSamples.Clear();
            _dragSamples.Enqueue((Godot.Time.GetTicksMsec(), at.Y));
        }

        /// <summary>The finger on the handle moved: the height follows it 1:1, with a rubber band at the ends.</summary>
        public void Drag(Vector2 at)
        {
            if (!_dragging)
            {
                return;
            }

            Layout(out Geometry geo);
            float raw = _dragFromHeight + (_dragFromY - at.Y) / geo.RowHeight;
            float band = RubberMax / geo.RowHeight;
            float h = raw > RowCount ? RowCount + System.Math.Min(band, (raw - RowCount) * Rubber)
                : raw < 1f ? 1f - System.Math.Min(band, (1f - raw) * Rubber)
                : raw;

            // A tick for each row the handle passes, as a detent.
            if ((int)System.Math.Floor(h + 0.0001f) != (int)System.Math.Floor(_height + 0.0001f))
            {
                Vibrate();
            }

            _height = _target = h;

            ulong now = Godot.Time.GetTicksMsec();
            _dragSamples.Enqueue((now, at.Y));

            while (_dragSamples.Count > 2 && now - _dragSamples.Peek().ms > 100)
            {
                _dragSamples.Dequeue();
            }
        }

        /// <summary>
        /// The finger let go of the handle: a flick goes all the way (up to
        /// three rows, down to one); otherwise the bar settles on the nearest
        /// row count.
        /// </summary>
        public void EndDrag(Vector2 at)
        {
            if (!_dragging)
            {
                return;
            }

            Drag(at);
            _dragging = false;

            (ulong ms, float y) first = _dragSamples.Peek();
            ulong dt = Godot.Time.GetTicksMsec() - first.ms;
            float speed = dt > 0 ? (first.y - at.Y) / dt : 0f; // px/ms, up is positive

            int to = speed > FlickSpeed ? RowCount
                : speed < -FlickSpeed ? 1
                : System.Math.Clamp((int)System.Math.Round(_height), 1, RowCount);

            SettleTo(to, true);
            TouchInput.Note($"handle drag -> {to} rows ({speed:F2} px/ms)");
        }

        /// <summary>Settle on a row count: animated, or at once with "Reduce motion".</summary>
        private void SettleTo(int rows, bool byPlayer)
        {
            if (byPlayer && rows == 1 && (RowsOpen > 1 || _height > 1.5f))
            {
                HiddenThisSession = true;
            }

            _dragging = false;
            _settleFrom = _height;
            _target = rows;
            _settleAt = Godot.Time.GetTicksMsec();
            _settleTicks = byPlayer;

            if (ProfileManager.CurrentProfile?.TouchReduceMotion ?? false)
            {
                _height = _target;
                if (_settleTicks) Vibrate();
            }
        }

        /// <summary>The settle, a frame at a time: ease-out with a slight overshoot.</summary>
        private void Settle()
        {
            if (_dragging || _height == _target)
            {
                return;
            }

            float t = System.Math.Min(1f, (Godot.Time.GetTicksMsec() - _settleAt) / SettleMs);

            if (t >= 1f)
            {
                _height = _target;

                if (_settleTicks)
                {
                    Vibrate();
                }

                return;
            }

            // Ease-out-back: c3 (t-1)^3 + c1 (t-1)^2 + 1.
            float u = t - 1f;
            float e = 1f + (Overshoot + 1f) * u * u * u + Overshoot * u * u;
            _height = _settleFrom + (_target - _settleFrom) * e;
        }

        /// <summary>A light tick, when the profile asks for it (Options, "Vibrate on snap").</summary>
        private static void Vibrate()
        {
            if (!(ProfileManager.CurrentProfile?.TouchVibrate ?? false))
            {
                return;
            }

            try
            {
                Godot.Input.VibrateHandheld(SnapVibrateMs);
            }
            catch (System.Exception)
            {
                // No vibrator, or no permission: the tick is a nicety.
            }
        }

        // --- the target -----------------------------------------------------

        /// <summary>The last target, if it is a mobile the client knows.</summary>
        private static bool Target(out Mobile mobile)
        {
            mobile = null;
            World world = Client.Game?.UO?.World;
            uint serial = world?.TargetManager?.LastTargetInfo.Serial ?? 0;

            if (world == null || !SerialHelper.IsMobile(serial))
            {
                return false;
            }

            mobile = world.Mobiles.Get(serial);

            return mobile != null && !mobile.IsDestroyed;
        }

        /// <summary>A name's colour: the notoriety hue it has in the world.</summary>
        private static Color NotorietyColour(NotorietyFlag flag) => flag switch
        {
            NotorietyFlag.Innocent => InnocentBlue,
            NotorietyFlag.Ally => new Color("3cc83c"),
            NotorietyFlag.Gray or NotorietyFlag.Criminal => new Color("a0a0a0"),
            NotorietyFlag.Enemy => new Color("f0961e"),
            NotorietyFlag.Murderer => MurdererRed,
            NotorietyFlag.Invulnerable => new Color("f0e61e"),
            _ => new Color("dcdcdc"),
        };

        // --- minimised gumps (GumpMinimise) -----------------------------------

        private int _chipFirst;

        /// <summary>
        /// The chips of minimised gumps: one row on the handle strip's left,
        /// up to the target's name. When they do not all fit, the row shows
        /// as many as fit from <see cref="_chipFirst"/> and "‹" / "›" chips
        /// page it. Actions are "chip:N" (N into GumpMinimise.Gumps),
        /// "chips:prev", "chips:next".
        /// </summary>
        private List<(string action, Rect2 rect)> ChipRects()
        {
            // Laid out once a frame, like the rest of the bar; paging moves
            // _chipFirst, and that lays them out again.
            ulong frame = Engine.GetProcessFrames();

            if (frame == _chipsFrame && _chipsFirst == _chipFirst)
            {
                return _chips;
            }

            _chipsFrame = frame;
            _chips.Clear();
            List<(string, Rect2)> result = _chips;
            IReadOnlyList<Game.UI.Gumps.Gump> gumps = GumpMinimise.Gumps;

            bool shelf = OnShelf;

            if (!Shown || !shelf && !HandleShown || gumps.Count == 0)
            {
                _chipFirst = _chipsFirst = 0;
                return result;
            }

            float s, h, gap, y, left, right, arrowW, captionScale;

            if (shelf)
            {
                // The Thor: along the bottom of the lower screen, above the
                // companion tabs' strip, at the shelf's own scale (one client
                // pixel each); in window pixels, which is what a touch on
                // that screen arrives in (DualScreen.ToWindow).
                float dpi = Client.Game.DpiScale;
                s = ShelfChipScale * dpi;
                h = PlateArt * s;
                gap = GapArt * s;
                y = (DualScreen.LogicalHeight - DualScreen.BottomReserve - GapArt) * dpi - h;
                left = (DualScreen.MainWidth + GapArt) * dpi;
                right = (DualScreen.MainWidth + DualScreen.LogicalWidth - GapArt) * dpi;
                arrowW = 16 * s;
                captionScale = s;
            }
            else
            {
                // The Odin's tray: the handle strip's left, up to the target's
                // name, which has the strip's middle.
                Layout(out Geometry geo);
                s = geo.Scale;
                h = PlateArt * s;
                gap = GapArt * s;
                y = geo.StripTop + (int)((geo.StripBottom - geo.StripTop - h) / 2);
                left = gap;
                right = geo.View.X / 2 - 60 * s;
                arrowW = 16 * s;
                captionScale = CaptionScale;
            }

            float Width(int i) => System.Math.Max(30 * s, (LabelTexture(Label("chip:" + i), null)?.GetWidth() ?? 40) * captionScale + 8 * s);

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

            _chipsFirst = _chipFirst;

            return result;
        }

        /// <summary>
        /// Whether the chips sit on the lower screen: on a two-screen device
        /// with the shelf on (the Thor). The Odin keeps them in the handle
        /// strip, its tray, which shows only while something is minimised.
        /// </summary>
        private static bool OnShelf => DualScreen.ShelfOn;

        /// <summary>
        /// The lower screen's chips are drawn at 2x its client pixels, like
        /// the main bar's captions: at 1x a chip is 2 mm tall on the Thor.
        /// </summary>
        private const int ShelfChipScale = 2;

        /// <summary>
        /// The chips on the lower screen, drawn into its target with the
        /// client's batcher: the small plate and the caption in font 1, both
        /// at the shelf's scale, as its gumps are drawn. Called by
        /// DualScreen.Draw.
        /// </summary>
        public static void DrawShelfChips(UltimaBatcher2D batcher)
        {
            TouchGumpBar bar = TouchInput.Bar;

            if (bar == null || !bar.Shown || !OnShelf || Client.Game?.UO?.Gumps == null)
            {
                return;
            }

            float dpi = Client.Game.DpiScale;
            ref readonly var plate = ref Client.Game.UO.Gumps.GetGump(SmallPlateGump);

            foreach ((string chip, Rect2 r) in bar.ChipRects())
            {
                var dest = new Rectangle((int)(r.Position.X / dpi), (int)(r.Position.Y / dpi), (int)(r.Size.X / dpi), (int)(r.Size.Y / dpi));
                bool lit = bar.Lit(chip) || bar._held == chip;
                var hue = ShaderHueTranslator.GetHueVector(0, false, lit ? 0.7f : 1f);

                if (plate.Texture != null)
                {
                    // Cropped from the middle, as the main screen's plates are,
                    // at ShelfChipScale.
                    const int k = ShelfChipScale;
                    int artW = dest.Width / k;
                    int leftW = System.Math.Min(plate.UV.Width, (artW + 1) / 2);
                    int rightW = System.Math.Min(plate.UV.Width - leftW, artW - leftW);
                    batcher.Draw(plate.Texture, new Rectangle(dest.X, dest.Y, leftW * k, dest.Height),
                        new Rectangle(plate.UV.X, plate.UV.Y, leftW, plate.UV.Height), hue, 0f);
                    batcher.Draw(plate.Texture, new Rectangle(dest.X + leftW * k, dest.Y, rightW * k, dest.Height),
                        new Rectangle(plate.UV.X + plate.UV.Width - rightW, plate.UV.Y, rightW, plate.UV.Height), hue, 0f);
                }
                else
                {
                    batcher.Draw(SolidColorTextureCache.GetTexture(GUO.Compat.Color.Black), dest, hue, 0f);
                }

                RenderedText text = bar.ShelfCaption(Label(chip));
                if (text != null)
                {
                    int tw = text.Width * ShelfChipScale, th = text.Height * ShelfChipScale;
                    text.Draw(batcher, dest.X + (dest.Width - tw) / 2, dest.Y + (dest.Height - th) / 2, 0f, 1f, 0, ShelfChipScale);
                }
            }
        }

        /// <summary>Captions for the lower screen's chips, kept until the cache starts over.</summary>
        private readonly Dictionary<string, RenderedText> _shelfCaptions = new();

        private RenderedText ShelfCaption(string caption)
        {
            if (!_shelfCaptions.TryGetValue(caption, out RenderedText text))
            {
                if (_shelfCaptions.Count > LabelCacheLimit)
                {
                    foreach (RenderedText t in _shelfCaptions.Values) t.Destroy();
                    _shelfCaptions.Clear();
                }

                text = RenderedText.Create(caption, 0, LabelFont, true);
                _shelfCaptions[caption] = text;
            }

            return text;
        }

        private readonly List<(string action, Rect2 rect)> _chips = new();
        private ulong _chipsFrame = ulong.MaxValue;
        private int _chipsFirst;

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

        // --- hit testing and running ----------------------------------------

        /// <summary>
        /// Which button, if any, a point lands on: a chip, the handle (the
        /// arrow tab or the strip's empty space), or a slot in an open row.
        /// </summary>
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

            // The arrow tab is the grab zone, widened by a gap each side and
            // to the strip's height: a thumb finds it without aiming, and it
            // covers no command. The strip itself is left to the gumps under it.
            if (HandleShown && GrabRect().HasPoint(at))
            {
                action = Handle;

                return true;
            }

            Layout(out Geometry geo);

            for (int r = 1; r <= RowCount; r++)
            {
                if (!RowUp(geo, r))
                {
                    continue;
                }

                string[] row = Row(r);

                for (int i = 0; i < PerRow; i++)
                {
                    if (SlotRect(r, i).HasPoint(at))
                    {
                        action = row[i];

                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Light a button while a finger is on it, before it runs; null lets
        /// go. The touch layer runs the button when the finger lifts on it.
        /// </summary>
        public void Hold(string action)
        {
            _held = action;
        }

        /// <summary>
        /// What a button does, by name: the top bar's own calls, or one of
        /// upstream's macros.
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

            if (action == Handle)
            {
                TapHandle();

                return;
            }

            if (MacroFor(action, out MacroType type, out MacroSubType sub))
            {
                RunMacro(world, action, type, sub);

                return;
            }

            if (SpeechFor(action) is string words)
            {
                // Upstream's Say macro, with the profile's words for this shard.
                var m = new Macro(action);
                m.PushToBack(new MacroObjectString(MacroType.Say, MacroSubType.MSC_NONE, words));
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

        /// <summary>As MacroButtonGump.RunMacro runs a macro button.</summary>
        private static void RunMacro(World world, string name, MacroType type, MacroSubType sub)
        {
            Macro m = Macro.CreateFastMacro(name, type, sub);
            world.Macros.SetMacroToExecute(m.Items as MacroObject);
            world.Macros.WaitForTargetTimer = 0;
            world.Macros.Update();
        }

        /// <summary>The upstream macro an action runs, if it is one.</summary>
        private static bool MacroFor(string action, out MacroType type, out MacroSubType sub)
        {
            type = action switch
            {
                // Upstream's "Select Nearest" macro with its Hostile scan: the
                // nearest gray, criminal, enemy or murderer becomes the last
                // target (World.FindNearest), as a ClassicUO player's macro does.
                "nearest" => MacroType.SelectNearest,
                "war" => MacroType.WarPeace,
                "attack" => MacroType.AttackLast,
                "last" => MacroType.LastTarget,
                "bandage" => MacroType.BandageSelf,
                "next" => MacroType.TargetNext,
                "object" => MacroType.LastObject,
                "heal" or "cure" => MacroType.UsePotion,
                "ability1" => MacroType.PrimaryAbility,
                "ability2" => MacroType.SecondaryAbility,
                "lastspell" => MacroType.LastSpell,
                "lastskill" => MacroType.LastSkill,
                "armdisarm" => MacroType.ArmDisarm,
                "allnames" => MacroType.AllNames,
                "door" => MacroType.OpenDoor,
                "status" or "skills" or "spellbook" or "party" => MacroType.Open,
                _ => MacroType.None,
            };

            sub = action switch
            {
                "nearest" => MacroSubType.Hostile,
                "heal" => MacroSubType.BestHealPotion,
                "cure" => MacroSubType.BestCurePotion,
                // The weapon hand, as upstream's "Arm/Disarm Right Hand".
                "armdisarm" => MacroSubType.RightHand,
                "status" => MacroSubType.Status,
                "skills" => MacroSubType.Skills,
                "spellbook" => MacroSubType.MageSpellbook,
                "party" => MacroSubType.PartyManifest,
                _ => MacroSubType.MSC_NONE,
            };

            return type != MacroType.None;
        }

        /// <summary>
        /// What a speech slot says: the profile's words, so a shard that
        /// answers to other words can have them. Null for other actions.
        /// </summary>
        private static string SpeechFor(string action)
        {
            Profile p = ProfileManager.CurrentProfile;

            return action switch
            {
                "follow" => p?.TouchSayFollow ?? "all follow me",
                "stop" => p?.TouchSayStop ?? "all stop",
                "bank" => p?.TouchSayBank ?? "bank",
                "guards" => p?.TouchSayGuards ?? "guards",
                _ => null,
            };
        }

        // --- captions -------------------------------------------------------

        /// <summary>An action's caption, for Options' slot lists.</summary>
        public static string Title(string action) => Label(action);

        /// <summary>The caption of a button: the top bar's cliloc, or its own words.</summary>
        private static string Label(string action)
        {
            if (action.StartsWith("chip:") && int.TryParse(action.Substring(5), out int ci))
            {
                IReadOnlyList<Game.UI.Gumps.Gump> gumps = GumpMinimise.Gumps;
                return ci >= 0 && ci < gumps.Count ? GumpMinimise.Title(gumps[ci]) : "";
            }

            ClilocLoader cliloc = Client.Game?.UO?.FileManager?.Clilocs;

            // Every caption fits one line of font 1 at 2x in a plate at 3x
            // (158 px); measured in docs/ui/command_bar.md.
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
                case "nearest": return "Nearest Foe";
                case "attack": return "Attack Last";
                case "last": return "Last Target";
                case "bandage": return "Bandage Self";
                case "next": return "Next Target";
                case "object": return "Last Object";
                case "heal": return "Heal Potion";
                case "cure": return "Cure Potion";
                case "ability1": return "Ability 1";
                case "ability2": return "Ability 2";
                case "lastspell": return "Last Spell";
                case "lastskill": return "Last Skill";
                case "armdisarm": return "Arm/Disarm";
                case "status": return "Status";
                case "skills": return "Skills";
                case "spellbook": return "Spellbook";
                case "allnames": return "All Names";
                case "door": return "Open Door";
                case "follow": return "All Follow Me";
                case "stop": return "All Stop";
                case "bank": return "Bank";
                case "guards": return "Guards";
                case "party": return "Party";
                case "war":
                    return Client.Game?.UO?.World?.Player?.InWarMode == true ? "Peace" : "War";
            }

            return action;
        }

        /// <summary>
        /// A caption drawn with the client's unicode font, as a texture: the
        /// same FontsLoader call RenderedText makes, kept as an image because
        /// this layer draws with Godot and not with the batcher. With an ink,
        /// every inked pixel takes that colour (a lit caption, a target's
        /// notoriety); without one, the font's own near-black.
        /// </summary>
        private Texture2D LabelTexture(string caption, Color? ink)
        {
            var key = (caption, ink);

            if (_labels.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            FontsLoader fonts = Client.Game?.UO?.FileManager?.Fonts;

            if (fonts == null || string.IsNullOrEmpty(caption))
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

            if (ink is Color c)
            {
                byte r = (byte)(c.R * 255), g = (byte)(c.G * 255), b = (byte)(c.B * 255);

                for (int i = 0; i < rgba.Length; i += 4)
                {
                    if (rgba[i + 3] != 0)
                    {
                        rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b;
                    }
                }
            }

            Image image = Image.CreateFromData(fi.Width, fi.Height, false, Image.Format.Rgba8, rgba);
            Texture2D texture = ImageTexture.CreateFromImage(image);
            _labels[key] = texture;

            return texture;
        }

        public override void _ExitTree() => ClearLabels();

        private void ClearLabels()
        {
            foreach (Texture2D t in _labels.Values)
            {
                t.Dispose();
            }

            _labels.Clear();
        }

        /// <summary>A gump's texture and its region, or false without it (an old client).</summary>
        private static bool Art(ushort gump, out Texture2D texture, out Rect2 uv)
        {
            texture = null;
            uv = default;

            if (Client.Game?.UO?.Gumps == null)
            {
                return false;
            }

            ref readonly var info = ref Client.Game.UO.Gumps.GetGump(gump);

            if (info.Texture == null)
            {
                return false;
            }

            texture = info.Texture;
            uv = new Rect2(info.UV.X, info.UV.Y, info.UV.Width, info.UV.Height);

            return true;
        }

        // --- drawing --------------------------------------------------------

        /// <summary>
        /// A plate at <paramref name="r"/>, cropped from the middle of the
        /// gump to the rectangle's width (never stretched): its left half from
        /// the gump's left edge, its right half from its right edge.
        /// </summary>
        private static void DrawPlate(CanvasItem canvas, ushort gump, Rect2 r, int s, Color modulate)
        {
            if (!Art(gump, out Texture2D tex, out Rect2 uv))
            {
                canvas.DrawRect(r, new Color(0.10f, 0.08f, 0.06f, 0.85f) * modulate);
                canvas.DrawRect(r, new Color(0.78f, 0.64f, 0.36f) * modulate, false, s);
                return;
            }

            int artW = (int)(r.Size.X / s);
            int leftW = System.Math.Min((int)uv.Size.X, (artW + 1) / 2);
            int rightW = System.Math.Min((int)uv.Size.X - leftW, artW - leftW);

            canvas.DrawTextureRectRegion(tex, new Rect2(r.Position, new Vector2(leftW * s, r.Size.Y)),
                new Rect2(uv.Position, new Vector2(leftW, uv.Size.Y)), modulate);
            canvas.DrawTextureRectRegion(tex, new Rect2(r.Position + new Vector2(leftW * s, 0), new Vector2(rightW * s, r.Size.Y)),
                new Rect2(uv.Position + new Vector2(uv.Size.X - rightW, 0), new Vector2(rightW, uv.Size.Y)), modulate);
        }

        /// <summary>A caption centred in a rectangle, at the caption scale.</summary>
        private void DrawCaption(CanvasItem canvas, string caption, Color? ink, Rect2 r)
        {
            Texture2D label = LabelTexture(caption, ink);

            if (label != null)
            {
                var size = new Vector2(label.GetWidth() * CaptionScale, label.GetHeight() * CaptionScale);
                var at = new Vector2(
                    r.Position.X + (int)((r.Size.X - size.X) / 2),
                    r.Position.Y + (int)((r.Size.Y - size.Y) / 2)
                );

                canvas.DrawTextureRect(label, new Rect2(at, size), false);

                return;
            }

            Font font = ThemeDB.FallbackFont;
            int fontSize = 11 * CaptionScale;
            Vector2 textSize = font.GetStringSize(caption, HorizontalAlignment.Left, -1, fontSize);
            var textAt = new Vector2(
                r.Position.X + (r.Size.X - textSize.X) / 2,
                r.Position.Y + (r.Size.Y + textSize.Y) / 2 - font.GetDescent(fontSize)
            );

            canvas.DrawString(font, textAt, caption, HorizontalAlignment.Left, -1, fontSize, ink ?? new Color(0.1f, 0.08f, 0.06f));
        }

        private static readonly List<(string, Rect2)> _noChips = new();

        private bool Lit(string action) =>
            _pressed == action && Godot.Time.GetTicksMsec() - _pressedAt < PressedMs;

        /// <summary>The handle strip: its band, the chips, the target and the arrow tab.</summary>
        private void DrawStrip(CanvasItem canvas)
        {
            Layout(out Geometry geo);
            int s = geo.Scale;

            if (!HandleShown)
            {
                return;
            }

            // Minimised gumps (GumpMinimise): a chip each, tap to restore. On
            // the Thor they are on the lower screen (DrawShelfChips).
            foreach ((string chip, Rect2 r) in OnShelf ? _noChips : ChipRects())
            {
                bool lit = Lit(chip) || _held == chip;
                DrawPlate(canvas, SmallPlateGump, r, s, lit ? new Color(0.7f, 0.7f, 0.7f) : Colors.White);
                DrawCaption(canvas, Label(chip), lit ? Gold : null, r);
            }

            DrawTarget(canvas, geo);

            // The arrow tab: up while one row is open (more to come), down at
            // two or three (a tap closes them).
            Rect2 tab = HandleRect();
            bool held = _held == Handle || _dragging;
            DrawPlate(canvas, SmallPlateGump, tab, s, held ? new Color(0.7f, 0.7f, 0.7f) : Colors.White);

            ushort arrow = RowsOpen == 1 ? ArrowUpGump : ArrowDownGump;

            if (Art(arrow, out Texture2D tex, out Rect2 uv))
            {
                Vector2 size = uv.Size * s;
                Vector2 at = tab.Position + ((tab.Size - size) / 2).Floor();
                canvas.DrawTextureRectRegion(tex, new Rect2(at, size), uv);
            }
        }

        /// <summary>
        /// The last target, centred on the strip: its name in its notoriety
        /// colour, and under it a strip each for hits, mana and stamina (the
        /// last two only when the server sends them: a pet, a party member).
        /// </summary>
        private void DrawTarget(CanvasItem canvas, in Geometry geo)
        {
            if (!Target(out Mobile m))
            {
                return;
            }

            int s = geo.Scale;
            string name = string.IsNullOrEmpty(m.Name) ? "?" : m.Name;
            Texture2D label = LabelTexture(name, NotorietyColour(m.NotorietyFlag));
            float nameW = (label?.GetWidth() ?? 40) * CaptionScale;
            float nameH = (label?.GetHeight() ?? 14) * CaptionScale;

            var bars = new List<(float value, Color colour)>(3);
            Color hits = m.IsPoisoned ? PoisonColour : m.IsYellowHits ? YellowHitsColour : HitsColour;
            bars.Add((m.HitsMax > 0 ? (float)m.Hits / m.HitsMax : 0f, hits));

            if (m.ManaMax > 0)
            {
                bars.Add(((float)m.Mana / m.ManaMax, ManaColour));
            }

            if (m.StaminaMax > 0)
            {
                bars.Add(((float)m.Stamina / m.StaminaMax, StaminaColour));
            }

            float barH = 2 * s, gap = s;
            float barsH = bars.Count * barH + (bars.Count - 1) * gap;
            float stripH = geo.StripBottom - geo.StripTop;
            float top = geo.StripTop + (int)((stripH - nameH - gap - barsH) / 2);
            float cx = geo.View.X / 2;
            float w = System.Math.Clamp(nameW, 40 * s, 80 * s);

            // Its own backing, as the strip has no band: legible over the world
            // or over a gump's edge.
            float boxW = System.Math.Max(nameW, w) + 8 * s;
            canvas.DrawRect(new Rect2((int)(cx - boxW / 2), top - s, boxW, nameH + gap + barsH + 3 * s), Band);

            if (label != null)
            {
                canvas.DrawTextureRect(label, new Rect2((int)(cx - nameW / 2), top, nameW, nameH), false);
            }

            float x = (int)(cx - w / 2);
            float y = top + nameH + gap;

            // At war, the strips' frame is red: the fight is on.
            bool war = Client.Game.UO.World.Player?.InWarMode ?? false;
            canvas.DrawRect(new Rect2(x - s, y - s, w + 2 * s, barsH + 2 * s), war ? MurdererRed : Colors.Black);

            foreach ((float value, Color colour) in bars)
            {
                canvas.DrawRect(new Rect2(x, y, w, barH), StripEmpty);
                canvas.DrawRect(new Rect2(x, y, (int)(w * System.Math.Clamp(value, 0f, 1f) / s) * s, barH), colour);
                y += barH + gap;
            }
        }

        /// <summary>The rows, in the clip under the strip: a band and ten plates each.</summary>
        private void DrawRows(CanvasItem canvas, Vector2 origin)
        {
            Layout(out Geometry geo);
            int s = geo.Scale;
            int shown = HandleShown ? (int)System.Math.Ceiling(Height - 0.001f) : 1;

            for (int r = 1; r <= System.Math.Min(shown, RowCount); r++)
            {
                float top = RowTop(geo, r);
                canvas.DrawRect(new Rect2(new Vector2(0, top) - origin, new Vector2(geo.View.X, geo.RowHeight)), Band);

                string[] row = Row(r);

                for (int i = 0; i < PerRow; i++)
                {
                    string action = row[i];
                    Rect2 plate = PlateRect(geo, r, i);
                    plate.Position -= origin;

                    // Held: pressed in, one art pixel down and darker; the
                    // caption goes gold, as the top bar's does under a mouse.
                    bool held = _held == action;

                    if (held)
                    {
                        plate.Position += new Vector2(0, s);
                    }

                    DrawPlate(canvas, PlateGump, plate, s, held ? new Color(0.7f, 0.7f, 0.7f) : Colors.White);

                    Color? ink = held || Lit(action) ? Gold
                        : action == "self" ? InnocentBlue
                        : action == "cancel" ? MurdererRed
                        : null;

                    DrawCaption(canvas, Label(action), ink, plate);
                }
            }
        }

        /// <summary>The control that paints the handle strip.</summary>
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
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

                if (GetParent() is TouchGumpBar bar && bar.Shown)
                {
                    // Starting the cache over here, before anything is drawn,
                    // is safe: this draw replaces the one that used the old
                    // textures, and the rows draw after it.
                    if (bar._labels.Count > LabelCacheLimit)
                    {
                        bar.ClearLabels();
                    }

                    bar.DrawStrip(this);
                }

                CostTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                DrawCount++;
            }
        }

        /// <summary>
        /// The control that paints the rows, clipped to the space under the
        /// handle strip, so a row slides in from under the handle as it rises.
        /// </summary>
        private sealed partial class RowsSurface : Control
        {
            public override void _Ready()
            {
                MouseFilter = MouseFilterEnum.Ignore;
                ClipContents = true;
                TextureFilter = TextureFilterEnum.Nearest;
            }

            public override void _Draw()
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

                if (GetParent()?.GetParent() is TouchGumpBar bar && bar.Shown)
                {
                    bar.DrawRows(this, Position);
                }

                CostTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            }
        }
    }
}
