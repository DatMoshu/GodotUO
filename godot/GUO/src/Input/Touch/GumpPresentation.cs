// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Game;
using GUO.Compat;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Controls;
using GUO.Game.UI.Gumps;
using GUO.Platform.Android;
using GUO.Renderer;

namespace GUO.Input.Touch;

/// <summary>Opt-in presentation of legacy gumps. Server coordinates and reply IDs stay unchanged.</summary>
internal static class GumpPresentation
{
    public const float MinScale = 0.75f, MaxScale = 3f;
    private const int GemSize = 28;
    private static Gump _mouseSpace;
    private static RenderedText _gemText;
    private static bool _gemMouseDown;

    /// <summary>
    /// Whether gump presentation (per-gump size, the window menu, hold and
    /// flick, screen transfer) is on at all. It is a mobile feature: on for
    /// the touch layer (a device, or --touch / the probes), for a second
    /// screen (a device, or the --dual-screen simulator), and for the desktop
    /// dev toggle "Mobile window controls". Otherwise the client is exactly
    /// ClassicUO: no handles, and a scale saved on mobile is drawn at 100%.
    /// </summary>
    public static bool Active => TouchInput.Enabled || DualScreen.ShelfOn
        || (Configuration.ProfileManager.CurrentProfile?.MobileWindowControls ?? false);

    // Keep arbitrary shard dialogs and content-zoom maps on their existing paths.
    public static bool Supports(Gump g) => Active && g != null && !g.IsDisposed
        && (_followers.Contains(g)
            // A full-height gump is scaled, a shard one included: only how it
            // is drawn and hit changes, never what is sent back. A resizable
            // one (the world map) is sized instead (FitFullHeight).
            || IsFullHeight(g) && g is not ResizableGump
            || !g.IsFromServer && !g.IsModal && g is PaperDollGump or ContainerGump or GridContainerGump
                or StatusGumpBase or JournalGump or ResizableJournal or ShopGump);

    /// <summary>
    /// A full-height gump (C11): one that on touch takes the whole main screen,
    /// drawn over the command bar, which steps aside while it is up. Fitted to
    /// the screen when it opens (<see cref="FitFullHeight"/>). Options, whose
    /// mobile mode this is (docs/ui/tall_gumps.md lists the other tall gumps).
    /// </summary>
    public static bool IsFullHeight(Gump g) =>
        g is OptionsGump or WorldMapGump || g.IsFromServer && FullHeightServerGumps.Contains(g.ServerSerial);

    /// <summary>
    /// Shard gumps that are full-height (docs/ui/gump_index.md: Classic plus
    /// fit), by the type ID the shard sends. ModernUO's is the xxHash32 of the
    /// gump class's full name (BaseGump.GetTypeId: seed 665738807, the name
    /// as UTF-16), so it is the same on every ModernUO shard. ServUO and RunUO
    /// number gumps differently; there these stay ordinary gumps.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<uint> FullHeightServerGumps = new()
    {
        0x7510FA8F, // Server.Engines.Help.HelpGump (page a GM)
        0xE37B54FE, // Server.Gumps.AdminGump (staff)
    };

    /// <summary>
    /// The top of the room a full-height gump is fitted into: below
    /// ClassicUO's top bar when it is showing (it is always drawn on top, so
    /// it would cover the gump's first row), else the top of the screen.
    /// </summary>
    public static int FullHeightTop()
    {
        TopBarGump bar = UIManager.GetGump<TopBarGump>();
        return bar != null && !bar.IsDisposed && bar.IsVisible && bar.Y < 40 ? bar.Y + bar.Height : 0;
    }

    /// <summary>Whether a full-height gump is up on the main screen, so the command bar steps aside.</summary>
    public static bool FullHeightOpen()
    {
        if (!TouchInput.Enabled)
        {
            return false;
        }

        foreach (Gump g in UIManager.Gumps)
        {
            if (!g.IsDisposed && g.IsVisible && IsFullHeight(g) && !OnSecond(g) && !_restored.Contains(g))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly System.Collections.Generic.HashSet<Gump> _restored = new();
    private static ulong _restoreUntil;
    private const ulong RestoreMs = 3000;

    /// <summary>
    /// Gumps the client reopens at login are not full-height: a player who
    /// saved one open would otherwise log in to it over the whole screen and
    /// no command bar. Called as the bar comes up for a session; any that
    /// opens in its first seconds counts as restored (the client restores
    /// saved gumps over the first frames in the world).
    /// </summary>
    public static void ExemptRestoredGumps()
    {
        _restored.Clear();
        _restoreUntil = Godot.Time.GetTicksMsec() + RestoreMs;

        foreach (Gump g in UIManager.Gumps)
        {
            if (!g.IsDisposed)
            {
                _restored.Add(g);
            }
        }
    }

    /// <summary>
    /// The margin above and below a fitted full-height gump, in client
    /// pixels: its edges stay clear of the screen's and of the top bar, and
    /// its scroll areas take what no longer fits (the owner, on the C11 photos).
    /// </summary>
    private const int FullHeightPad = 12;

    private static readonly System.Collections.Generic.HashSet<Gump> _fitted = new();

    /// <summary>
    /// Fit each full-height gump that has just opened to the main screen, as
    /// large as it goes, centred: on a handheld that is about twice its size,
    /// finger-sized, with its own Cancel/Apply/Okay row always on screen.
    /// Once per gump, so a pinch or the window menu can size it after.
    /// </summary>
    public static void FitFullHeight()
    {
        if (!TouchInput.Enabled)
        {
            return;
        }

        _fitted.RemoveWhere(g => g.IsDisposed);
        _restored.RemoveWhere(g => g.IsDisposed);

        foreach (Gump g in UIManager.Gumps)
        {
            if (g.IsDisposed || !IsFullHeight(g) || g.Width <= 0 || _fitted.Contains(g) || _restored.Contains(g))
            {
                continue;
            }

            if (Godot.Time.GetTicksMsec() < _restoreUntil)
            {
                _restored.Add(g);
                continue;
            }

            _fitted.Add(g);

            // The whole main screen, the bar having stepped aside, less the top
            // bar and a margin above and below.
            Rectangle b = new(0, 0, Client.Game.ClientBounds.Width, Client.Game.ClientBounds.Height);
            int top = FullHeightTop() + FullHeightPad;
            int room = Math.Max(1, b.Height - top - FullHeightPad);

            if (g is ResizableGump resizable)
            {
                // The world map (docs/ui/gump_index.md: Classic + fit + gestures):
                // sized, not scaled, so it draws more map at its own zoom; a
                // pinch zooms it and, in its Free view, a drag pans it.
                Point size = resizable.ResizeWindow(new Point(b.Width, room));
                g.X = b.X + Math.Max(0, (b.Width - size.X) / 2);
                g.Y = top;
                TouchInput.Note($"full-height: {g.GetType().Name} sized to {size.X}x{size.Y}");
                continue;
            }

            bool locked = g.PresentationLocked;
            g.PresentationLocked = false;
            float fit = Math.Min(b.Width / (float)g.Width, room / (float)g.Height);
            SetScale(g, fit, new Point(g.X, g.Y));
            g.X = b.X + Math.Max(0, (b.Width - Width(g)) / 2);
            g.Y = top + Math.Max(0, (room - Height(g)) / 2);
            g.PresentationLocked = locked;
            TouchInput.Note($"full-height: {g.GetType().Name} fitted at {g.PresentationScale:0.00}x below {top}");
        }
    }

    // Each paperdoll seen, with the size it was given on opening: a size that
    // differs from it later is the player's.
    private static readonly System.Collections.Generic.Dictionary<Gump, float> _sized = new();
    private static float _paperdollScale;

    /// <summary>The size the next paperdoll opens at, 0 for the fit; the probe clears it.</summary>
    internal static float PaperdollScale { get => _paperdollScale; set => _paperdollScale = value; }

    /// <summary>
    /// A paperdoll opened during play on one touch screen starts at a size a
    /// finger can use (gump index: "the paperdoll's touch fit"): up to 2x, and
    /// no taller than 85% of the room above the command bar. Once the player
    /// pinches or resizes one, later paperdolls this session open at that size
    /// instead; the fit alone is not remembered, so it follows the screen.
    /// Not on the Thor, whose lower screen shelves it, and not for one reopened
    /// at login, which keeps its saved size.
    /// </summary>
    public static void FitPaperdolls()
    {
        if (!TouchInput.Enabled || DualScreen.ShelfOn)
        {
            return;
        }

        foreach (Gump gone in System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(_sized.Keys, k => k.IsDisposed)))
        {
            _sized.Remove(gone);
        }

        foreach (Gump g in UIManager.Gumps)
        {
            if (g is not PaperDollGump || g.IsDisposed || g.Height <= 0)
            {
                continue;
            }

            if (_sized.TryGetValue(g, out float given))
            {
                // Remember a size the player chose, for the next one.
                if (GumpFlick.Lifted != g && Math.Abs(g.PresentationScale - given) > 0.001f)
                {
                    _paperdollScale = g.PresentationScale;
                    _sized[g] = g.PresentationScale;
                }

                continue;
            }

            _sized[g] = g.PresentationScale;

            if (_restored.Contains(g) || Godot.Time.GetTicksMsec() < _restoreUntil || g.PresentationScale != 1f)
            {
                continue;
            }

            Rectangle room = DisplayBounds(false);
            float fit = Math.Min(2f, room.Height * 0.85f / g.Height);
            float scale = _paperdollScale > 0f ? _paperdollScale : fit;

            if (scale > 1.01f)
            {
                SetScale(g, scale, new Point(g.X, g.Y));
                _sized[g] = g.PresentationScale;
                TouchInput.Note($"paperdoll: opened at {g.PresentationScale:0.00}x");
            }
        }
    }

    private static readonly System.Collections.Generic.HashSet<Gump> _followers = new();

    /// <summary>
    /// A popup a control of a scaled gump opens (a combobox's list) takes its
    /// owner's scale: drawn, and hit, at the same size as what opened it.
    /// </summary>
    public static void Follow(Gump popup, Control owner)
    {
        float s = Scale(owner);

        if (popup == null || s == 1f)
        {
            return;
        }

        _followers.RemoveWhere(g => g.IsDisposed);
        _followers.Add(popup);
        popup.PresentationScale = s;
    }
    // The handle is drawn only when asked for (Options, "Show window handles");
    // on touch the menu opens from a hold-and-release on the gump.
    private static bool GemVisible(Gump g) => Supports(g) && GemAlpha(g) > 0f;

    private static bool AlwaysShown => Configuration.ProfileManager.CurrentProfile?.ShowWindowHandles ?? false;

    /// <summary>The handle's opacity: 1 when "Show window handles" is on, else hidden.</summary>
    public static float GemAlpha(Gump g) => Active && AlwaysShown ? 1f : 0f;

    /// <summary>
    /// Open the size and screen menu for a gump, beside it. The one entry
    /// point: the handle, a hold-and-release on touch (GumpFlick), and a
    /// controller's "window menu" button (<see cref="OpenMenuForTop"/>).
    /// </summary>
    public static bool OpenMenu(Gump g)
    {
        if (!Supports(g) || UIManager.IsModalOpen) return false;
        UIManager.GetGump<GumpLayoutGump>()?.Dispose();
        WindowMenu.Open(g);
        return true;
    }

    /// <summary>Hook for a controller "window menu" button: the menu for the topmost supported gump.</summary>
    public static bool OpenMenuForTop()
    {
        foreach (Gump g in UIManager.Gumps)
        {
            if (g.IsDisposed || !g.IsVisible || g is GumpLayoutGump) continue;
            if (Supports(g)) return OpenMenu(g);
        }
        return false;
    }

    public static Gump Root(Control c) => c as Gump ?? c?.RootParent as Gump;
    public static float Scale(Control c) => Supports(Root(c)) ? Root(c).PresentationScale : 1f;
    public static Point ToLocal(Control c, Point p)
    {
        Gump g = Root(c);
        float s = Scale(c);
        return s == 1f ? p : new Point(g.X + (int)Math.Floor((p.X - g.X) / s),
            g.Y + (int)Math.Floor((p.Y - g.Y) / s));
    }

    public static Point ToScreen(Control c, Point p)
    {
        Gump g = Root(c);
        float s = Scale(c);
        return s == 1f ? p : new Point(g.X + (int)Math.Round((p.X - g.X) * s),
            g.Y + (int)Math.Round((p.Y - g.Y) * s));
    }

    public static int Width(Gump g) => (int)Math.Ceiling(g.Width * Scale(g));
    public static int Height(Gump g) => (int)Math.Ceiling(g.Height * Scale(g));
    public static Rectangle Bounds(Gump g) => new(g.X, g.Y, Width(g), Height(g));

    public static Rectangle DisplayBounds(bool second)
    {
        if (second && DualScreen.ShelfOn)
            return new Rectangle(DualScreen.MainWidth, 0, DualScreen.LogicalWidth, DualScreen.LogicalHeight - DualScreen.BottomReserve);
        Rectangle main = Client.Game?.ClientBounds ?? new Rectangle(0, 0, 640, 480);
        main.Height -= (int)(main.Height * (TouchInput.Bar?.ReservedFraction ?? 0f));
        return main;
    }

    public static bool OnSecond(Gump g) => DualScreen.ShelfOn && g.X >= DualScreen.MainWidth;

    // The renderer stores the shelf to the right; players see it below the main screen.
    // Keep that storage detail out of window dragging, including pointer jumps between panels.
    public static bool MoveDragged(Gump g, Point previous, Point pointer)
    {
        if (!DualScreen.ShelfOn || g == null) return false;
        bool second = OnSecond(g);
        bool pointerSecond = pointer.X >= DualScreen.MainWidth;
        bool previousSecond = previous.X >= DualScreen.MainWidth;
        int dx = pointer.X - previous.X;
        int dy = pointer.Y - previous.Y;
        if (pointerSecond != previousSecond && Math.Abs(dx) > DualScreen.MainWidth / 2)
        {
            dx -= ((pointerSecond ? 1 : 0) - (previousSecond ? 1 : 0)) * DualScreen.MainWidth;
            dy += (pointerSecond ? 1 : -1) * Client.Game.ClientBounds.Height;
        }
        Rectangle source = DisplayBounds(second);
        g.X = source.X + Math.Clamp(g.X - source.X + dx, 0, Math.Max(0, source.Width - Width(g)));
        g.Y += dy;
        bool crossed = !g.IsModal && g is not WorldViewportGump && pointerSecond == second && (second
            ? pointer.Y <= 8 && dy < 0
            : pointer.Y >= source.Height - 8 && dy > 0);
        if (crossed)
        {
            Rectangle target = DisplayBounds(!second);
            g.X = target.X + Math.Clamp(g.X - source.X, 0, Math.Max(0, target.Width - Width(g)));
            g.Y = second ? Math.Max(0, target.Height - Height(g)) : 0;
        }
        g.PresentationPlaced = true;
        Clamp(g);
        return true;
    }

    public static void Clamp(Gump g) => Clamp(g, OnSecond(g));

    /// <summary>Keep the gump inside the given screen, whichever side of the seam its origin is on now.</summary>
    public static void Clamp(Gump g, bool second)
    {
        Rectangle b = DisplayBounds(second);
        g.X = Math.Clamp(g.X, b.X, b.X + Math.Max(0, b.Width - Width(g)));
        g.Y = Math.Clamp(g.Y, 0, Math.Max(0, b.Height - Height(g)));
    }

    public static bool SetScale(Gump g, float requested, Point anchor)
    {
        if (!Supports(g) || g.PresentationLocked || !float.IsFinite(requested) || g.Width <= 0 || g.Height <= 0)
            return false;
        // Which screen it is on is decided before the origin moves: scaling
        // about a pinch centre can carry a shelf gump's origin left of the
        // seam, and Clamp would then have put it on the main screen (seen on
        // the Thor).
        bool second = OnSecond(g);
        Rectangle b = DisplayBounds(second);
        float fit = Math.Min(b.Width / (float)g.Width, b.Height / (float)g.Height);
        // If the original gump is too large even at minimum, retain a reachable origin/reset.
        float next = Math.Clamp(requested, MinScale, Math.Max(MinScale, Math.Min(MaxScale, fit)));
        float ratio = next / g.PresentationScale;
        g.X = (int)Math.Round(anchor.X - (anchor.X - g.X) * ratio);
        g.Y = (int)Math.Round(anchor.Y - (anchor.Y - g.Y) * ratio);
        g.PresentationScale = next;
        Clamp(g, second);
        return true;
    }

    public static void Reset(Gump g)
    {
        if (!Supports(g)) return;
        bool second = OnSecond(g);
        g.PresentationLocked = false;
        g.PresentationScale = 1f;
        Clamp(g, second);
    }

    public static bool Transfer(Gump g)
    {
        if (!Supports(g) || !DualScreen.ShelfOn || UIManager.IsDragging
            || Client.Game.UO.GameCursor.ItemHold.Enabled) return false;
        bool second = OnSecond(g);
        // Remember positions relative to a display, not the width of yesterday's main window.
        Point old = new(g.X - (second ? DualScreen.MainWidth : 0), g.Y);
        Point? previous = second ? g.MainPresentationPosition : g.SecondPresentationPosition;
        if (second) g.SecondPresentationPosition = old;
        else g.MainPresentationPosition = old;
        Rectangle destination = DisplayBounds(!second);
        g.X = destination.X + (previous?.X ?? 20);
        g.Y = previous?.Y ?? 40;
        g.PresentationPlaced = true;
        Clamp(g);
        return true;
    }

    /// <summary>Physical virtual-canvas coordinates, including shelf offset; never screen-local pixels.</summary>
    public static Gump At(Point p)
    {
        foreach (Gump g in UIManager.Gumps)
        {
            if (g.IsDisposed || !g.IsVisible || !g.IsEnabled) continue;
            Control hit = null;
            g.HitTest(p, ref hit);
            if (hit != null) return Root(hit);
        }
        return null;
    }

    public static Rectangle GemRect(Gump g)
    {
        Rectangle display = DisplayBounds(OnSecond(g));
        TopBarGump top = OnSecond(g) ? null : UIManager.GetGump<TopBarGump>();
        int topEdge = top is { IsVisible: true, IsDisposed: false } ? top.Y + top.Height : 0;
        int x = g.X + Width(g) - GemSize, y = g.Y - GemSize;
        // Above the window when there is room; beside it near the top edge. Never hide
        // a window's only Reset control behind the permanent top bar.
        if (y < topEdge) { x = g.X + Width(g); y = Math.Max(topEdge, g.Y); }
        return new Rectangle(Math.Clamp(x, display.X, display.X + Math.Max(0, display.Width - GemSize)),
            Math.Clamp(y, 0, Math.Max(0, display.Height - GemSize)), GemSize, GemSize);
    }

    public static bool OpenGem(Point p)
    {
        if (UIManager.IsModalOpen
            || UIManager.IsDragging || Client.Game.UO.GameCursor.ItemHold.Enabled) return false;
        foreach (Gump g in UIManager.Gumps)
        {
            if (g.IsDisposed || !g.IsVisible || !g.IsEnabled) continue;
            if (GemVisible(g) && GemRect(g).Contains(p))
            {
                return OpenMenu(g);
            }
            Control hit = null;
            g.HitTest(p, ref hit);
            if (hit != null) return false; // A foreground window occludes gems behind it.
        }
        return false;
    }

    public static bool HandleMouse(Godot.InputEventMouseButton e)
    {
        if (e.ButtonIndex != Godot.MouseButton.Left) return false;
        if (!e.Pressed && _gemMouseDown) { _gemMouseDown = false; return true; }
        if (!e.Pressed) return false;
        _gemMouseDown = false;
        float dpi = Client.Game.DpiScale;
        if (!OpenGem(new Point((int)(e.Position.X / dpi), (int)(e.Position.Y / dpi)))) return false;
        _gemMouseDown = true;
        return true;
    }

    public static void Queue(Gump g, RenderLists lists, ref float depth)
    {
        float s = Scale(g);
        // A gump lifted for a flick (GumpFlick) is drawn a little larger
        // about its centre, still nearest-sampled, with its outline and chips.
        bool lifted = GumpFlick.Lifted == g;
        if (s != 1f || lifted)
        {
            var transform = lifted ? GumpFlick.LiftTransform(g, s) : new Godot.Transform2D(new Godot.Vector2(s, 0), new Godot.Vector2(0, s),
                new Godot.Vector2(g.X * (1 - s), g.Y * (1 - s)));
            lists.AddGumpNoAtlas(b => { b.PushUiTransform(transform); return true; });
        }
        g.AddToRenderLists(lists, g.X, g.Y, ref depth);
        if (s != 1f || lifted) lists.AddGumpNoAtlas(b => { b.ClipEnd(); return true; });
        if (lifted) lists.AddGumpNoAtlas(b => { GumpFlick.DrawOverlay(b, g); return true; });
        if (GemVisible(g) && g.IsVisible && g.Width > 0)
        {
            Rectangle r = GemRect(g);
            float alpha = GemAlpha(g);
            lists.AddGumpNoAtlas(b =>
            {
                b.Draw(SolidColorTextureCache.GetTexture(new Color(35, 65, 75)), r,
                    ShaderHueTranslator.GetHueVector(0, false, alpha), 0);
                _gemText ??= RenderedText.Create("UI", 0x03b2, 1, true);
                _gemText.Draw(b, r.X + 5, r.Y + 4, 0, alpha);
                return true;
            });
        }
    }

    // Some legacy Update/Contains/event handlers read Mouse directly. Scope ALL its positions
    // to the same inverse transform as the explicit event coordinates, restoring even on failure.
    public readonly struct MouseScope : IDisposable
    {
        private readonly Gump _previous;
        private readonly bool _changed;
        private readonly Point _position, _left, _right, _middle;
        public MouseScope(Control c)
        {
            _previous = _mouseSpace;
            _position = Mouse.Position; _left = Mouse.LClickPosition;
            _right = Mouse.RClickPosition; _middle = Mouse.MClickPosition;
            Gump root = Root(c);
            _changed = root != _mouseSpace && Scale(c) != 1f;
            if (!_changed) return;
            _mouseSpace = root;
            Mouse.Position = ToLocal(c, _position);
            Mouse.LClickPosition = ToLocal(c, _left);
            Mouse.RClickPosition = ToLocal(c, _right);
            Mouse.MClickPosition = ToLocal(c, _middle);
        }
        public void Dispose()
        {
            if (!_changed) return;
            Mouse.Position = _position; Mouse.LClickPosition = _left;
            Mouse.RClickPosition = _right; Mouse.MClickPosition = _middle;
            _mouseSpace = _previous;
        }
    }
}
