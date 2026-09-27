// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Compat;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Game.UI.Gumps;
using GUO.Platform.Android;
using GUO.Renderer;

namespace GUO.Input.Touch;

/// <summary>What a flick in one direction does to the lifted gump (Options, "Hold and flick").</summary>
internal enum FlickAction
{
    Nothing = 0,
    ToTopScreen = 1,
    ToBottomScreen = 2,
    Close = 3,
    Reset = 4,
    SizeMenu = 5,
    ToggleLock = 6,
    ShelfAutoSlot = 7,
}

/// <summary>
/// Hold and flick a gump (sprint story S12). A finger held still on a
/// supported gump's frame or background for <see cref="HoldMs"/> lifts it:
/// drawn a little larger with an outline glow, and four chips around it name
/// what a flick each way will do. A flick past <see cref="ThresholdDp"/> picks
/// that chip (a haptic tick as each threshold is crossed); letting go inside
/// the threshold does nothing. The actions are GumpPresentation's own
/// (Transfer, SetScale, Reset, the size menu), not a second implementation.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO; the touch layer only. The hold
/// never presses a mouse button, so it cannot start an item drag; a hold on an
/// item slot is still a pick-up (TouchInput checks that first).
/// </remarks>
internal static class GumpFlick
{
    public const int HoldMs = 350;
    public const float LiftScale = 1.04f;
    private const float ThresholdDp = 48f;

    /// <summary>The lifted gump, or null.</summary>
    public static Gump Lifted { get; private set; }

    /// <summary>The direction currently picked: -1 none, else 0 up, 1 down, 2 left, 3 right.</summary>
    public static int Direction { get; private set; } = -1;

    /// <summary>What the last finished flick did, for the probe and the trace.</summary>
    public static string LastResult { get; private set; } = "";

    private static Godot.Vector2 _origin;
    private static float _threshold;
    private static readonly Dictionary<string, RenderedText> _labels = new();

    public static readonly string[] DirectionNames = { "up", "down", "left", "right" };

    public static readonly string[] ActionTitles =
    {
        "Do nothing", "Send to top screen", "Send to bottom screen", "Close (with Reopen)",
        "Reset size", "Size menu", "Lock / unlock size", "Shelf's own slot",
    };

    /// <summary>
    /// Whether a hold at this point may lift a gump: a supported gump, under
    /// its frame or background rather than a control that does something.
    /// </summary>
    public static bool CanStart(out Gump gump)
    {
        gump = null;

        // All four set to "Do nothing" turns hold-and-flick off: a long press
        // on a gump is then the right click it always was.
        if (!Enabled)
        {
            return false;
        }

        Control over = UIManager.MouseOverControl;
        Gump root = GumpPresentation.Root(over);

        if (!GumpPresentation.Supports(root) || UIManager.IsModalOpen || UIManager.IsDragging
            || Client.Game.UO.GameCursor.ItemHold.Enabled || Client.Game.UO.World.TargetManager.IsTargeting)
        {
            return false;
        }

        if (over != root && (over is Button || over is ItemGump || over is StbTextBox || over is Checkbox
            || over is ScrollBarBase || over is HSliderBar || over is Combobox || over is HitBox
            || over is ExpandableScroll))
        {
            return false;
        }

        gump = root;
        return true;
    }

    /// <summary>Lift <paramref name="g"/>; <paramref name="window"/> is the finger in window pixels.</summary>
    public static void Begin(Gump g, Point logical, Godot.Vector2 window)
    {
        Lifted = g;
        Direction = -1;
        _origin = window;
        int dpi = Godot.DisplayServer.ScreenGetDpi();
        _threshold = Math.Max(24f, ThresholdDp * Math.Max(dpi, 96) / 160f);
        g.BringOnTop();
        Haptic(25);
    }

    /// <summary>Follow the finger; a tick when a threshold is crossed into a direction.</summary>
    public static void Move(Godot.Vector2 at)
    {
        if (Lifted == null)
        {
            return;
        }

        Godot.Vector2 d = at - _origin;
        int next = -1;

        if (d.Length() >= _threshold)
        {
            next = Math.Abs(d.Y) >= Math.Abs(d.X) ? (d.Y < 0 ? 0 : 1) : (d.X < 0 ? 2 : 3);
        }

        if (next != Direction)
        {
            Direction = next;

            if (next >= 0)
            {
                Haptic(12);
            }
        }
    }

    /// <summary>The finger lifted: do the picked direction's action, or nothing.</summary>
    public static string End(Godot.Vector2 at)
    {
        Move(at);
        Gump g = Lifted;
        int direction = Direction;
        Lifted = null;
        Direction = -1;

        if (g == null || g.IsDisposed)
        {
            return LastResult = "flick: gump gone";
        }

        if (direction < 0)
        {
            return LastResult = "flick: released inside the threshold -> nothing";
        }

        FlickAction action = ActionFor(direction);
        Perform(g, action);

        return LastResult = $"flick {DirectionNames[direction]} -> {action}";
    }

    public static void Cancel()
    {
        Lifted = null;
        Direction = -1;
    }

    /// <summary>Whether any direction does something.</summary>
    public static bool Enabled => ActionFor(0) != FlickAction.Nothing || ActionFor(1) != FlickAction.Nothing
        || ActionFor(2) != FlickAction.Nothing || ActionFor(3) != FlickAction.Nothing;

    public static FlickAction ActionFor(int direction)
    {
        Profile p = ProfileManager.CurrentProfile;

        int value = direction switch
        {
            0 => p?.FlickUp ?? (int)FlickAction.ToTopScreen,
            1 => p?.FlickDown ?? (int)FlickAction.ToBottomScreen,
            2 => p?.FlickLeft ?? (int)FlickAction.Close,
            _ => p?.FlickRight ?? (int)FlickAction.Reset,
        };

        return Enum.IsDefined(typeof(FlickAction), value) ? (FlickAction)value : FlickAction.Nothing;
    }

    public static void Perform(Gump g, FlickAction action)
    {
        switch (action)
        {
            case FlickAction.ToTopScreen:
                if (GumpPresentation.OnSecond(g)) GumpPresentation.Transfer(g);
                else Fit(g);
                break;

            case FlickAction.ToBottomScreen:
                if (!GumpPresentation.OnSecond(g) && DualScreen.ShelfOn) GumpPresentation.Transfer(g);
                else Fit(g);
                break;

            case FlickAction.Close:
                UndoToast.Show(g);
                g.Dispose();
                break;

            case FlickAction.Reset:
                GumpPresentation.Reset(g);

                // Away from its home screen (the shelf's, for a gump the shelf
                // takes; else the main one): back home, to where it was there.
                if (DualScreen.ShelfOn && GumpPresentation.OnSecond(g) != DualScreen.WantsShelf(g))
                {
                    GumpPresentation.Transfer(g);
                }
                break;

            case FlickAction.SizeMenu:
                UIManager.GetGump<GumpLayoutGump>()?.Dispose();
                UIManager.Add(new GumpLayoutGump(g));
                break;

            case FlickAction.ToggleLock:
                g.PresentationLocked = !g.PresentationLocked;
                break;

            case FlickAction.ShelfAutoSlot:
                DualScreen.Reshelve(g);
                break;
        }
    }

    /// <summary>As large as fits the screen it is on, centred: "fit to screen".</summary>
    public static void Fit(Gump g)
    {
        Rectangle b = GumpPresentation.DisplayBounds(GumpPresentation.OnSecond(g));

        if (g.Width <= 0 || g.Height <= 0)
        {
            return;
        }

        bool locked = g.PresentationLocked;
        g.PresentationLocked = false;
        float fit = Math.Min(b.Width / (float)g.Width, b.Height / (float)g.Height);
        GumpPresentation.SetScale(g, fit, new Point(g.X, g.Y));
        g.X = b.X + Math.Max(0, (b.Width - GumpPresentation.Width(g)) / 2);
        g.Y = Math.Max(0, (b.Height - GumpPresentation.Height(g)) / 2);
        GumpPresentation.Clamp(g);
        g.PresentationLocked = locked;
    }

    private static void Haptic(int ms)
    {
        try
        {
            Godot.Input.VibrateHandheld(ms);
        }
        catch (Exception)
        {
            // No vibrator, or no permission: the tick is a nicety.
        }
    }

    // --- drawing -----------------------------------------------------------

    /// <summary>
    /// The lifted gump's transform: GumpPresentation's scale about the gump's
    /// origin, then <see cref="LiftScale"/> about the presented centre.
    /// </summary>
    public static Godot.Transform2D LiftTransform(Gump g, float s)
    {
        float k = LiftScale;
        float cx = g.X + g.Width * s / 2f, cy = g.Y + g.Height * s / 2f;

        return new Godot.Transform2D(
            new Godot.Vector2(k * s, 0), new Godot.Vector2(0, k * s),
            new Godot.Vector2(cx * (1 - k) + k * g.X * (1 - s), cy * (1 - k) + k * g.Y * (1 - s)));
    }

    /// <summary>The lifted gump's bounds as drawn.</summary>
    public static Rectangle LiftedBounds(Gump g)
    {
        int w = GumpPresentation.Width(g), h = GumpPresentation.Height(g);
        int lw = (int)Math.Round(w * LiftScale), lh = (int)Math.Round(h * LiftScale);

        return new Rectangle(g.X - (lw - w) / 2, g.Y - (lh - h) / 2, lw, lh);
    }

    /// <summary>The glow outline and the four chips, drawn after the lifted gump.</summary>
    public static void DrawOverlay(UltimaBatcher2D b, Gump g)
    {
        Rectangle r = LiftedBounds(g);
        var gold = SolidColorTextureCache.GetTexture(new Color(223, 187, 119));

        for (int i = 1; i <= 3; i++)
        {
            float alpha = i == 1 ? 0.95f : i == 2 ? 0.55f : 0.25f;
            b.DrawRectangle(gold, r.X - i, r.Y - i, r.Width + 2 * i - 1, r.Height + 2 * i - 1,
                ShaderHueTranslator.GetHueVector(0, false, alpha), 0);
        }

        Rectangle display = GumpPresentation.DisplayBounds(GumpPresentation.OnSecond(g));

        for (int dir = 0; dir < 4; dir++)
        {
            DrawChip(b, g, r, display, dir);
        }
    }

    private static string ChipLabel(Gump g, FlickAction action)
    {
        bool second = GumpPresentation.OnSecond(g);

        return action switch
        {
            FlickAction.ToTopScreen => second ? "To top screen" : "Fit to screen",
            FlickAction.ToBottomScreen => !second && DualScreen.ShelfOn ? "To bottom screen" : "Fit to screen",
            FlickAction.Close => "Close",
            FlickAction.Reset => "Reset size",
            FlickAction.SizeMenu => "Size menu",
            FlickAction.ToggleLock => g.PresentationLocked ? "Unlock size" : "Lock size",
            FlickAction.ShelfAutoSlot => "To its shelf slot",
            _ => "Nothing",
        };
    }

    private static void DrawChip(UltimaBatcher2D b, Gump g, Rectangle r, Rectangle display, int dir)
    {
        string label = ChipLabel(g, ActionFor(dir));

        if (!_labels.TryGetValue(label, out RenderedText text) || text.IsDestroyed)
        {
            _labels[label] = text = RenderedText.Create(label, 0x0481, 1, true);
        }

        const int pad = 6, arrow = 9, gap = 6;
        int w = pad + arrow + 4 + text.Width + pad, h = Math.Max(22, text.Height + 8);
        int x, y;

        switch (dir)
        {
            case 0: x = r.X + (r.Width - w) / 2; y = r.Y - h - gap; break;
            case 1: x = r.X + (r.Width - w) / 2; y = r.Bottom + gap; break;
            case 2: x = r.X - w - gap; y = r.Y + (r.Height - h) / 2; break;
            default: x = r.Right + gap; y = r.Y + (r.Height - h) / 2; break;
        }

        // Kept on the screen the gump is on, even for a gump that fills it.
        x = Math.Clamp(x, display.X, display.X + Math.Max(0, display.Width - w));
        y = Math.Clamp(y, 0, Math.Max(0, display.Height - h));

        bool picked = Direction == dir;
        var fill = SolidColorTextureCache.GetTexture(picked ? new Color(223, 187, 119) : new Color(20, 25, 23));
        var edge = SolidColorTextureCache.GetTexture(new Color(223, 187, 119));
        b.Draw(fill, new Rectangle(x, y, w, h), ShaderHueTranslator.GetHueVector(0, false, picked ? 1f : 0.85f), 0);
        b.DrawRectangle(edge, x, y, w - 1, h - 1, ShaderHueTranslator.GetHueVector(0), 0);

        // The arrow: a pixel triangle pointing the chip's way, nearest-sampled.
        var ink = SolidColorTextureCache.GetTexture(picked ? new Color(20, 25, 23) : new Color(223, 187, 119));
        int ax = x + pad, ay = y + (h - arrow) / 2;

        for (int i = 0; i < 5; i++)
        {
            Rectangle row = dir switch
            {
                0 => new Rectangle(ax + 4 - i, ay + 2 + i, 1 + 2 * i, 1),
                1 => new Rectangle(ax + 4 - i, ay + 6 - i, 1 + 2 * i, 1),
                2 => new Rectangle(ax + 2 + i, ay + 4 - i, 1, 1 + 2 * i),
                _ => new Rectangle(ax + 6 - i, ay + 4 - i, 1, 1 + 2 * i),
            };
            b.Draw(ink, row, ShaderHueTranslator.GetHueVector(0), 0);
        }

        text.Draw(b, x + pad + arrow + 4, y + (h - text.Height) / 2, 0);
    }
}

/// <summary>"Closed. Reopen" for three seconds after a flick closes a gump.</summary>
internal sealed class UndoToast : Gump
{
    private const ulong LifeMs = 3000;
    private readonly ulong _until;
    private readonly Action _reopen;

    public static void Show(Gump closed)
    {
        UIManager.GetGump<UndoToast>()?.Dispose();

        World world = closed.World;
        uint serial = closed.LocalSerial;
        Action reopen = closed switch
        {
            PaperDollGump => () => GameActions.OpenPaperdoll(world, serial),
            ContainerGump or GridContainerGump => () => GameActions.DoubleClick(world, serial),
            StatusGumpBase => () => GameActions.OpenStatusBar(world),
            JournalGump or ResizableJournal => () => GameActions.OpenJournal(world),
            _ => null,
        };

        if (reopen == null)
        {
            return;
        }

        bool second = GumpPresentation.OnSecond(closed);
        UIManager.Add(new UndoToast(world, reopen, second));
    }

    private UndoToast(World world, Action reopen, bool second) : base(world, 0, 0)
    {
        _reopen = reopen;
        _until = (ulong)Godot.Time.GetTicksMsec() + LifeMs;
        CanMove = false;
        AcceptMouseInput = true;
        CanCloseWithRightClick = true;
        Width = 220;
        Height = 44;

        Add(new AlphaBlendControl(0.9f) { Width = Width, Height = Height });
        Add(new Label("Closed.", true, 0x0481, 0, 1) { X = 12, Y = 13 });
        Add(new NiceButton(110, 6, 100, 32, ButtonAction.Activate, "Reopen", 1) { ButtonParameter = 1, IsSelectable = false });

        Rectangle b = GumpPresentation.DisplayBounds(second);
        X = b.X + (b.Width - Width) / 2;
        Y = Math.Max(0, b.Height - Height - 12);
    }

    public override void Update()
    {
        base.Update();

        if (!IsDisposed && (ulong)Godot.Time.GetTicksMsec() >= _until)
        {
            Dispose();
        }
    }

    public override void OnButtonClick(int buttonID)
    {
        if (buttonID == 1)
        {
            _reopen?.Invoke();
            Dispose();
        }
    }
}
