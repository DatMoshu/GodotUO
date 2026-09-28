// SPDX-License-Identifier: BSD-2-Clause
using System.Collections.Generic;
using Godot;
using GUO.Configuration;
using GUO.Platform.Android;
using GUO.Renderer;

namespace GUO.Input.Touch;

/// <summary>
/// A debug overlay that shows every finger the touch layer sees: a soft circle
/// at each one in contact, and a short trail that fades. It covers both
/// screens and the probes' synthesised fingers, which Android's own "Show
/// taps" never sees. Off by default; <c>--show-touches</c> or Options turn it on.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): debug only, not in ClassicUO. The circles are drawn
/// shapes, not art, so nothing is filtered. The main window's fingers are drawn
/// by a CanvasLayer above the touch bar; the second screen's by the batcher in
/// DualScreen.Draw, since that screen is a separate render target.
/// </remarks>
internal sealed partial class TouchOverlay : CanvasLayer
{
    private const ulong TrailMs = 450;
    private const float Radius = 26f; // window pixels on the main screen

    /// <summary>Set by <c>--show-touches</c>; the profile's option is the other way on.</summary>
    public static bool Forced { get; set; }

    public static bool On => Forced || (ProfileManager.CurrentProfile?.DebugShowTouches ?? false);

    private sealed class Finger
    {
        public Vector2 At;
        public bool Down;
        public ulong UpAt;
        public readonly List<(Vector2 at, ulong t)> Trail = new();
    }

    private static readonly Dictionary<int, Finger> _fingers = new();
    private static TouchOverlay _instance;
    private static Texture2D _dot;

    private Surface _surface;

    public static void Setup(Node host)
    {
        if (_instance != null)
        {
            return;
        }

        _instance = new TouchOverlay();
        host.AddChild(_instance);
    }

    public override void _Ready()
    {
        Layer = 120;
        _surface = new Surface();
        AddChild(_surface);
    }

    public override void _Process(double delta)
    {
        Prune();
        _surface.QueueRedraw();
    }

    /// <summary>Every touch event the touch layer receives, in window pixels.</summary>
    public static void Note(InputEvent e)
    {
        if (!On)
        {
            return;
        }

        ulong now = Godot.Time.GetTicksMsec();

        switch (e)
        {
            case InputEventScreenTouch t:
                if (!_fingers.TryGetValue(t.Index, out Finger f))
                {
                    _fingers[t.Index] = f = new Finger();
                }

                f.At = t.Position;
                f.Down = t.Pressed && !t.Canceled;
                f.UpAt = f.Down ? 0 : now;

                if (f.Down)
                {
                    f.Trail.Clear();
                }

                f.Trail.Add((t.Position, now));
                break;

            case InputEventScreenDrag d:
                if (_fingers.TryGetValue(d.Index, out Finger g))
                {
                    g.At = d.Position;
                    g.Trail.Add((d.Position, now));
                }
                break;
        }
    }

    private static void Prune()
    {
        ulong now = Godot.Time.GetTicksMsec();
        var gone = new List<int>();

        foreach ((int id, Finger f) in _fingers)
        {
            f.Trail.RemoveAll(p => now - p.t > TrailMs);

            if (!f.Down && now - f.UpAt > TrailMs)
            {
                gone.Add(id);
            }
        }

        foreach (int id in gone)
        {
            _fingers.Remove(id);
        }
    }

    /// <summary>Whether a window-pixel point is on the second screen.</summary>
    private static bool OnShelf(Vector2 at) =>
        DualScreen.ShelfOn && at.X / (Client.Game?.DpiScale ?? 1f) >= DualScreen.MainWidth;

    /// <summary>The second screen's fingers, drawn by DualScreen.Draw in client pixels.</summary>
    public static void DrawShelf(UltimaBatcher2D b)
    {
        if (!On || _fingers.Count == 0)
        {
            return;
        }

        _dot ??= MakeDot();
        float dpi = Client.Game?.DpiScale ?? 1f;
        ulong now = Godot.Time.GetTicksMsec();
        int r = (int)(Radius / dpi);

        foreach (Finger f in _fingers.Values)
        {
            foreach ((Vector2 at, ulong t) in f.Trail)
            {
                if (!OnShelf(at)) continue;
                float life = 1f - (now - t) / (float)TrailMs;
                int tr = (int)(r * 0.45f);
                b.Draw(_dot, new Compat.Rectangle((int)(at.X / dpi) - tr, (int)(at.Y / dpi) - tr, tr * 2, tr * 2),
                    ShaderHueTranslator.GetHueVector(0, false, 0.45f * life), 0);
            }

            if (OnShelf(f.At))
            {
                float alpha = f.Down ? 0.7f : 0.7f * (1f - (now - f.UpAt) / (float)TrailMs);
                b.Draw(_dot, new Compat.Rectangle((int)(f.At.X / dpi) - r, (int)(f.At.Y / dpi) - r, r * 2, r * 2),
                    ShaderHueTranslator.GetHueVector(0, false, alpha), 0);
            }
        }
    }

    /// <summary>A white disc with a soft edge, as a texture for the batcher.</summary>
    private static Texture2D MakeDot()
    {
        const int size = 64;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x + 0.5f - size / 2f, y + 0.5f - size / 2f).Length() / (size / 2f);
                // Solid inside, a soft rim over the outer fifth.
                float a = d > 1f ? 0f : d > 0.8f ? (1f - d) / 0.2f : 1f;
                img.SetPixel(x, y, new Color(1f, 0.85f, 0.35f, System.Math.Clamp(a, 0f, 1f)));
            }
        }

        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The main window's fingers, above the touch bar.</summary>
    private sealed partial class Surface : Control
    {
        public override void _Ready()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            SetAnchorsPreset(LayoutPreset.FullRect);
        }

        public override void _Draw()
        {
            if (!On)
            {
                return;
            }

            ulong now = Godot.Time.GetTicksMsec();
            var gold = new Color(1f, 0.85f, 0.35f);

            foreach (Finger f in _fingers.Values)
            {
                Vector2? prev = null;

                foreach ((Vector2 at, ulong t) in f.Trail)
                {
                    if (OnShelf(at)) { prev = null; continue; }
                    float life = 1f - (now - t) / (float)TrailMs;

                    if (prev is Vector2 p)
                    {
                        DrawLine(p, at, new Color(gold, 0.5f * life), Radius * 0.5f);
                    }

                    prev = at;
                }

                if (!OnShelf(f.At))
                {
                    float alpha = f.Down ? 1f : 1f - (now - f.UpAt) / (float)TrailMs;
                    DrawCircle(f.At, Radius, new Color(gold, 0.45f * alpha));
                    DrawArc(f.At, Radius, 0f, Mathf.Tau, 32, new Color(1f, 1f, 1f, 0.9f * alpha), 3f);
                }
            }
        }
    }
}
