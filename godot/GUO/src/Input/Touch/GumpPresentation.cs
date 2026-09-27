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

    // Keep arbitrary shard dialogs and content-zoom maps on their existing paths.
    public static bool Supports(Gump g) => g != null && !g.IsDisposed && !g.IsFromServer && !g.IsModal
        && g is PaperDollGump or ContainerGump or GridContainerGump or StatusGumpBase
            or JournalGump or ResizableJournal;
    private static bool GemVisible(Gump g) => Supports(g)
        && (TouchInput.Enabled || DualScreen.ShelfOn || g.PresentationScale != 1f || g.PresentationLocked);

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
            return new Rectangle(DualScreen.MainWidth, 0, DualScreen.LogicalWidth, DualScreen.LogicalHeight);
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
                UIManager.GetGump<GumpLayoutGump>()?.Dispose();
                UIManager.Add(new GumpLayoutGump(g));
                return true;
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
        if (s != 1f)
        {
            var transform = new Godot.Transform2D(new Godot.Vector2(s, 0), new Godot.Vector2(0, s),
                new Godot.Vector2(g.X * (1 - s), g.Y * (1 - s)));
            lists.AddGumpNoAtlas(b => { b.PushUiTransform(transform); return true; });
        }
        g.AddToRenderLists(lists, g.X, g.Y, ref depth);
        if (s != 1f) lists.AddGumpNoAtlas(b => { b.ClipEnd(); return true; });
        if (GemVisible(g) && g.IsVisible && g.Width > 0)
        {
            Rectangle r = GemRect(g);
            lists.AddGumpNoAtlas(b =>
            {
                b.Draw(SolidColorTextureCache.GetTexture(new Color(35, 65, 75)), r,
                    ShaderHueTranslator.GetHueVector(0), 0);
                _gemText ??= RenderedText.Create("UI", 0x03b2, 1, true);
                _gemText.Draw(b, r.X + 5, r.Y + 4, 0);
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
