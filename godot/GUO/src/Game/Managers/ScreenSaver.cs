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
    ///
    /// Profile.ScreenSaverChoice (v11) picks what shows: "effects" (the
    /// drifting UO effects), "builtin:NAME" (a loop listed in
    /// assets/screensavers/screensavers.json) or the user:// path of an
    /// installed store screensaver's .ogv. A loop is scaled to overfill the
    /// screen and the whole frame drifts, so no pixel of it stays put; a loop
    /// that cannot play falls back to the effects.
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

        public const string EffectsChoice = "effects";
        public const string BuiltinPrefix = "builtin:";

        private static Godot.VideoStreamPlayer _player;
        private static string _playing;
        private static string _failed;

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
                StopLoop();

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
                if (Active)
                {
                    Active = false;
                    StopLoop();
                }

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
                Godot.GD.Print($"[GUO] screen saver: on after {ProfileManager.CurrentProfile.ScreenSaverMinutes} min idle, showing \"{ProfileManager.CurrentProfile.ScreenSaverChoice}\"; {System.Linq.Enumerable.Count(Choices())} choice(s) offered");
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

            Godot.Texture2D loop = LoopTexture(ProfileManager.CurrentProfile.ScreenSaverChoice);

            if (loop != null)
            {
                DrawLoop(batcher, area, loop, now);
                DrawLabel(batcher, area, elapsed);

                return;
            }

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

        /// <summary>
        /// The loop's current frame, starting it if needed; null for the
        /// effects, or for a loop that is missing or will not play.
        /// </summary>
        private static Godot.Texture2D LoopTexture(string choice)
        {
            string path = LoopPath(choice);

            if (path == null || path == _failed)
            {
                StopLoop();

                return null;
            }

            if (_playing != path)
            {
                StopLoop();

                try
                {
                    Godot.VideoStream stream = Godot.ResourceLoader.Exists(path)
                        ? Godot.GD.Load<Godot.VideoStream>(path)
                        : new Godot.VideoStreamTheora { File = path };

                    // Decodes off screen: the frames are drawn through the
                    // batcher, on both screens, never by the node itself.
                    _player = new Godot.VideoStreamPlayer
                    {
                        Stream = stream,
                        Loop = true,
                        Size = new Godot.Vector2(1, 1),
                        Modulate = new Godot.Color(1, 1, 1, 0),
                        MouseFilter = Godot.Control.MouseFilterEnum.Ignore
                    };
                    Client.Game.AddChild(_player);
                    _player.Play();
                }
                catch (Exception ex)
                {
                    Godot.GD.Print($"[GUO] screen saver: {path} will not play ({ex.Message})");
                }

                if (_player == null || !_player.IsPlaying())
                {
                    Godot.GD.Print($"[GUO] screen saver: {path} will not play; showing the effects");
                    StopLoop();
                    _failed = path;

                    return null;
                }

                _playing = path;
                Godot.GD.Print($"[GUO] screen saver: playing {path}");
            }

            return _player.GetVideoTexture();
        }

        /// <summary>
        /// Every choice Options offers, as (profile value, title): the
        /// effects, the built-in loops, then installed store screensavers.
        /// </summary>
        public static System.Collections.Generic.IEnumerable<(string, string)> Choices()
        {
            yield return (EffectsChoice, "UO effects, drifting");

            foreach (BuiltinScreensaver b in BuiltinScreensaver.All)
            {
                yield return (BuiltinPrefix + b.Name, b.Title);
            }

            foreach ((string path, string title) in GUO.Store.StoreOptions.InstalledScreensavers())
            {
                yield return (path, title);
            }
        }

        /// <summary>The loop a choice plays, or null for the effects.</summary>
        public static string LoopPath(string choice)
        {
            if (string.IsNullOrEmpty(choice) || choice == EffectsChoice)
            {
                return null;
            }

            if (choice.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
            {
                return BuiltinScreensaver.Find(choice.Substring(BuiltinPrefix.Length))?.Video;
            }

            return choice.StartsWith("user://store/", StringComparison.Ordinal) ? choice : null;
        }

        private static void StopLoop()
        {
            if (_player != null)
            {
                _player.Stop();
                _player.QueueFree();
                _player = null;
            }

            _playing = null;
        }

        /// <summary>
        /// The loop, scaled to cover the area with a margin, on a slow
        /// Lissajous path through that margin: the whole frame drifts.
        /// </summary>
        private static void DrawLoop(UltimaBatcher2D batcher, Rectangle area, Godot.Texture2D texture, uint now)
        {
            int tw = texture.GetWidth(), th = texture.GetHeight();

            if (tw <= 0 || th <= 0)
            {
                return;
            }

            const float Margin = 0.06f;
            float scale = Math.Max(area.Width * (1 + 2 * Margin) / tw, area.Height * (1 + 2 * Margin) / th);
            int w = (int) (tw * scale), h = (int) (th * scale);
            double t = now / 1000.0;
            float dx = (float) Math.Sin(t * 2 * Math.PI / 97) * (w - area.Width) / 2f;
            float dy = (float) Math.Sin(t * 2 * Math.PI / 71) * (h - area.Height) / 2f;
            int x = area.X + (area.Width - w) / 2 + (int) dx;
            int y = area.Y + (area.Height - h) / 2 + (int) dy;

            batcher.Draw(texture, new Rectangle(x, y, w, h), ShaderHueTranslator.GetHueVector(0, false, 1f), 0f);
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

    /// <summary>
    /// The screensaver loops GUO ships: assets/screensavers/screensavers.json
    /// lists them as {"name","title","video","still"}, paths relative to the
    /// folder. An entry with "store_only": true is published to the store
    /// instead (tools/asset_store/seed.py) and is not offered here.
    /// </summary>
    internal sealed class BuiltinScreensaver
    {
        public const string Folder = "res://assets/screensavers";
        public const string Manifest = Folder + "/screensavers.json";

        public string Name;
        public string Title;
        public string Video;
        public string Still;

        private static BuiltinScreensaver[] _all;

        public static System.Collections.Generic.IReadOnlyList<BuiltinScreensaver> All => _all ??= Read();

        public static BuiltinScreensaver Find(string name)
        {
            foreach (BuiltinScreensaver b in All)
            {
                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return b;
                }
            }

            return null;
        }

        private static BuiltinScreensaver[] Read()
        {
            var list = new System.Collections.Generic.List<BuiltinScreensaver>();

            if (!Godot.FileAccess.FileExists(Manifest))
            {
                return list.ToArray();
            }

            try
            {
                using Godot.FileAccess file = Godot.FileAccess.Open(Manifest, Godot.FileAccess.ModeFlags.Read);

                foreach (Godot.Variant item in Godot.Json.ParseString(file.GetAsText()).AsGodotArray())
                {
                    Godot.Collections.Dictionary d = item.AsGodotDictionary();
                    string name = d.ContainsKey("name") ? d["name"].AsString().Trim().ToLowerInvariant() : "";

                    if (name.Length == 0 || !d.ContainsKey("video") || (d.ContainsKey("store_only") && d["store_only"].AsBool()))
                    {
                        continue;
                    }

                    list.Add(new BuiltinScreensaver
                    {
                        Name = name,
                        Title = d.ContainsKey("title") ? d["title"].AsString() : name,
                        Video = Folder + "/" + d["video"].AsString(),
                        Still = d.ContainsKey("still") ? Folder + "/" + d["still"].AsString() : null
                    });
                }
            }
            catch (Exception ex)
            {
                Godot.GD.Print($"[GUO] screen saver: {Manifest} unreadable ({ex.Message})");
            }

            return list.ToArray();
        }
    }
}
