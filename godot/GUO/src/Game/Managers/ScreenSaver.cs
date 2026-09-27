// GUO addition, not a port: upstream has no screen saver.

using System;
using GUO.Configuration;
using GUO.Assets;
using GUO.Game.GameObjects;
using GUO.Renderer;
using GUO.Compat;

namespace GUO.Game.Managers
{
    /// <summary>
    /// A screen saver for OLED panels left on through long runs: after the
    /// profile's idle minutes with no input, the whole window goes black and
    /// a few of UO's own spell effects drift about on it, with the words
    /// "Screen saver", so nothing stays lit in one place. The game keeps
    /// running underneath. The first input only wakes it and reaches nothing
    /// else, so a tap to wake cannot walk the character or press a button.
    /// </summary>
    /// <remarks>
    /// Profile.ScreenSaver and Profile.ScreenSaverMinutes, under Options >
    /// Video; on by default on phones (PlatformDefaults v10). Needs a
    /// profile, so it never runs on the login screens.
    /// </remarks>
    internal static class ScreenSaver
    {
        // Explosions, fire, sparkles and smoke: effect art with animdata.
        private static readonly ushort[] Effects =
        {
            0x36B0, 0x36BD, 0x36D4, 0x3709, 0x3728, 0x373A, 0x374A, 0x376A
        };

        private const int SpriteCount = 3;
        private const int Scale = 3;
        private const uint MoveEveryMs = 6000;

        private sealed class Sprite
        {
            public ushort Graphic;
            public float X, Y, Dx, Dy;
            public uint Until;
        }

        private static readonly Sprite[] _sprites = new Sprite[SpriteCount];
        private static readonly Random _random = new Random();
        private static uint _lastInput = Time.Ticks;
        private static uint _lastDraw;
        private static RenderedText _label;
        private static float _labelX, _labelY, _labelDx = 0.02f, _labelDy = 0.015f;

        /// <summary>True while the screen saver is showing.</summary>
        public static bool Active { get; private set; }

        private static bool Enabled
        {
            get
            {
                Profile profile = ProfileManager.CurrentProfile;

                return profile != null && profile.ScreenSaver;
            }
        }

        /// <summary>
        /// Called for every input event. Returns true when the event woke the
        /// screen saver and must go no further.
        /// </summary>
        public static bool NoteInput()
        {
            _lastInput = Time.Ticks;

            if (Active)
            {
                Active = false;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Draw over everything already in the frame, inside
        /// <paramref name="area"/> (the batcher's own coordinates). Called by
        /// GameController.DrawFrame for the main window and by
        /// DualScreen.Draw for the second screen.
        /// </summary>
        public static void Draw(UltimaBatcher2D batcher, Rectangle area)
        {
            if (!Enabled)
            {
                Active = false;

                return;
            }

            uint idleMs = (uint) Math.Max(1, ProfileManager.CurrentProfile.ScreenSaverMinutes) * 60_000;

            if (!Active)
            {
                if (Time.Ticks - _lastInput < idleMs)
                {
                    return;
                }

                Active = true;
                Array.Clear(_sprites);
                _lastDraw = Time.Ticks;
            }

            if (area.Width <= 0 || area.Height <= 0)
            {
                return;
            }

            uint now = Time.Ticks;
            float elapsed = Math.Min(100, now - _lastDraw);

            if (now != _lastDraw)
            {
                _lastDraw = now;
            }

            Vector3 black = ShaderHueTranslator.GetHueVector(0, false, 1f);
            batcher.Draw(SolidColorTextureCache.GetTexture(Color.Black), area, black, 0f);

            for (int i = 0; i < _sprites.Length; i++)
            {
                Sprite s = _sprites[i] ??= new Sprite();

                if (s.Graphic == 0 || now >= s.Until)
                {
                    s.Graphic = Effects[_random.Next(Effects.Length)];
                    s.X = _random.Next(area.Width);
                    s.Y = _random.Next(area.Height);
                    s.Dx = (float) (_random.NextDouble() - 0.5) * 0.08f;
                    s.Dy = (float) (_random.NextDouble() - 0.5) * 0.08f;
                    s.Until = now + MoveEveryMs + (uint) _random.Next(3000);
                }

                s.X += s.Dx * elapsed;
                s.Y += s.Dy * elapsed;

                DrawEffect(batcher, s.Graphic, area.X + (int) s.X, area.Y + (int) s.Y, now);
            }

            DrawLabel(batcher, area, elapsed);
        }

        private static unsafe void DrawEffect(UltimaBatcher2D batcher, ushort graphic, int x, int y, uint now)
        {
            AnimDataFrame anim = Client.Game.UO.FileManager.AnimData.CalculateCurrentGraphic(graphic);
            ushort frame = graphic;

            if (anim.FrameCount > 0)
            {
                uint interval = Math.Max(1u, anim.FrameInterval) * 50u;
                frame = (ushort) (graphic + anim.FrameData[(now / interval) % anim.FrameCount]);
            }

            ref readonly var art = ref Client.Game.UO.Arts.GetArt(frame);

            if (art.Texture == null)
            {
                return;
            }

            int w = art.UV.Width * Scale;
            int h = art.UV.Height * Scale;

            batcher.Draw
            (
                art.Texture,
                new Rectangle(x - (w >> 1), y - (h >> 1), w, h),
                art.UV,
                ShaderHueTranslator.GetHueVector(0, false, 1f),
                0f,
                Vector2.Zero,
                SpriteEffects.None,
                0f
            );
        }

        private static void DrawLabel(UltimaBatcher2D batcher, Rectangle area, float elapsed)
        {
            _label ??= RenderedText.Create("Screen saver", 0x0481, 1, true, FontStyle.BlackBorder);

            int maxX = Math.Max(1, area.Width - _label.Width * Scale);
            int maxY = Math.Max(1, area.Height - _label.Height * Scale);

            // Bounce off the edges, slowly, so the words never sit still.
            _labelX += _labelDx * elapsed;
            _labelY += _labelDy * elapsed;

            if (_labelX < 0 || _labelX > maxX)
            {
                _labelDx = -_labelDx;
                _labelX = Math.Clamp(_labelX, 0, maxX);
            }

            if (_labelY < 0 || _labelY > maxY)
            {
                _labelDy = -_labelDy;
                _labelY = Math.Clamp(_labelY, 0, maxY);
            }

            _label.Draw(batcher, area.X + (int) _labelX, area.Y + (int) _labelY, 0f, 1f, 0, Scale);
        }
    }
}
