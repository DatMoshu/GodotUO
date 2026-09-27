// SPDX-License-Identifier: BSD-2-Clause
//
// GUO addition, no upstream counterpart. ADR-0016.
//
// The canvas background: what the window shows behind the world render
// target and the gumps. Upstream tiles one embedded 50x50 art file across
// the window (RenderTargets.Draw). GUO keeps that as the "builtin-grey"
// mode and lets a profile replace it with a wood tile of its own, a picture,
// a looping video, or a folder of frames.
//
// The node is a CanvasLayer at layer -1, so it paints under everything the
// batcher puts on the controller's canvas (layer 0) without touching the
// batcher, the world draw path or any render target. When it is active,
// RenderTargets skips upstream's tile draw; when the mode is builtin-grey
// it is hidden and upstream's draw runs unchanged, so a desktop profile
// looks exactly as it did.
//
// Filtering: project rule 7 (never filter pixel art) is about UO art. A
// player's photo or video is not UO art and may be sampled linearly, but
// only through this node's own texture filter. default_texture_filter and
// every other canvas item are left alone.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.Configuration;
using GUO.Utility.Logging;
using FileAccess = Godot.FileAccess;

namespace GUO.Renderer
{
    /// <summary>The five modes the profile's CanvasBackgroundMode names.</summary>
    internal enum CanvasBackgroundMode
    {
        BuiltinGrey,
        BuiltinWood,
        Image,
        Video,
        Frames,

        /// <summary>
        /// One of the looping videos GUO ships under assets/backgrounds,
        /// named by the profile as "builtin:&lt;name&gt;" (BuiltinBackground).
        /// </summary>
        BuiltinMedia
    }

    /// <summary>
    /// The backgrounds GUO ships: assets/backgrounds/backgrounds.json lists
    /// them as {"name","title","video","still"} with paths relative to the
    /// folder. The still is the low-power picture and what a device that
    /// cannot play the video shows.
    /// </summary>
    internal sealed class BuiltinBackground
    {
        public const string Folder = "res://assets/backgrounds";
        public const string Manifest = Folder + "/backgrounds.json";
        public const string Prefix = "builtin:";

        public string Name;
        public string Title;
        public string Video;
        public string Still;

        private static BuiltinBackground[] _all;

        public static IReadOnlyList<BuiltinBackground> All => _all ??= Read();

        public static BuiltinBackground Find(string name)
        {
            foreach (BuiltinBackground b in All)
            {
                if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return b;
                }
            }

            return null;
        }

        private static BuiltinBackground[] Read()
        {
            var list = new List<BuiltinBackground>();

            if (!FileAccess.FileExists(Manifest))
            {
                return list.ToArray();
            }

            try
            {
                using FileAccess file = FileAccess.Open(Manifest, FileAccess.ModeFlags.Read);
                Variant parsed = Json.ParseString(file.GetAsText());

                foreach (Variant item in parsed.AsGodotArray())
                {
                    Godot.Collections.Dictionary d = item.AsGodotDictionary();
                    string name = d.ContainsKey("name") ? d["name"].AsString() : "";

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    list.Add(new BuiltinBackground
                    {
                        Name = name.Trim().ToLowerInvariant(),
                        Title = d.ContainsKey("title") ? d["title"].AsString() : name,
                        Video = d.ContainsKey("video") ? Folder + "/" + d["video"].AsString() : null,
                        Still = d.ContainsKey("still") ? Folder + "/" + d["still"].AsString() : null
                    });
                }

                GD.Print($"[GUO] canvas background: {list.Count} built-in background(s) in {Manifest}: {string.Join(", ", list.Select(b => $"{b.Name} \"{b.Title}\""))}");
            }
            catch (Exception ex)
            {
                Log.Warn($"canvas background: could not read {Manifest}: {ex.Message}");
            }

            return list.ToArray();
        }
    }

    /// <summary>
    /// What the background is asked to show: the profile's four keys, as one
    /// value so a change in any of them is one comparison.
    /// </summary>
    internal readonly record struct CanvasBackgroundSettings(CanvasBackgroundMode Mode, string Path, int Fps, bool LowPower)
    {
        public static readonly string[] ModeNames = { "builtin-grey", "builtin-wood", "image", "video", "frames" };

        public static CanvasBackgroundMode ParseMode(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string n = name.Trim().ToLowerInvariant().Replace('_', '-');

                for (int i = 0; i < ModeNames.Length; i++)
                {
                    if (ModeNames[i] == n)
                    {
                        return (CanvasBackgroundMode) i;
                    }
                }

                // The enum names without the dash, so "BuiltinWood" and
                // "wood" both land somewhere sensible.
                if (n == "wood" || n == "builtinwood") return CanvasBackgroundMode.BuiltinWood;
                if (n == "grey" || n == "gray" || n == "builtingrey") return CanvasBackgroundMode.BuiltinGrey;
            }

            return CanvasBackgroundMode.BuiltinGrey;
        }

        public static string ModeName(CanvasBackgroundMode mode) =>
            mode == CanvasBackgroundMode.BuiltinMedia ? "builtin" : ModeNames[(int) mode];

        /// <summary>The profile's CanvasBackgroundMode string for these settings.</summary>
        public string ModeString =>
            Mode == CanvasBackgroundMode.BuiltinMedia ? BuiltinBackground.Prefix + Path : ModeName(Mode);

        public static CanvasBackgroundSettings FromProfile(Profile profile)
        {
            string mode = profile.CanvasBackgroundMode ?? "";
            int fps = Math.Clamp(profile.CanvasBackgroundFps <= 0 ? 12 : profile.CanvasBackgroundFps, 1, 60);

            // "builtin:<name>" names a shipped background; the path field
            // keeps whatever the player typed for image/video/frames.
            if (mode.StartsWith(BuiltinBackground.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return new CanvasBackgroundSettings(CanvasBackgroundMode.BuiltinMedia, mode.Substring(BuiltinBackground.Prefix.Length).Trim().ToLowerInvariant(), fps, profile.CanvasBackgroundLowPower);
            }

            return new CanvasBackgroundSettings(ParseMode(mode), profile.CanvasBackgroundPath ?? "", fps, profile.CanvasBackgroundLowPower);
        }

        /// <summary>
        /// The command-line form: <c>mode[:path]</c>, e.g.
        /// <c>image:C:\pictures\a.png</c>. Fps and low power take the
        /// desktop defaults; <c>@fps</c> and <c>,lowpower</c> may follow the path.
        /// </summary>
        public static CanvasBackgroundSettings Parse(string spec)
        {
            // A launcher's %* can hand the quotes through with the value.
            spec = (spec ?? "").Trim().Trim('"');
            bool lowPower = false;
            int fps = 12;

            if (spec.EndsWith(",lowpower", StringComparison.OrdinalIgnoreCase))
            {
                lowPower = true;
                spec = spec.Substring(0, spec.Length - ",lowpower".Length);
            }

            int at = spec.LastIndexOf('@');

            if (at > 0 && int.TryParse(spec.Substring(at + 1), out int parsedFps))
            {
                fps = Math.Clamp(parsedFps, 1, 60);
                spec = spec.Substring(0, at);
            }

            int colon = spec.IndexOf(':');
            string mode = colon < 0 ? spec : spec.Substring(0, colon);
            string path = colon < 0 ? "" : spec.Substring(colon + 1);

            // "builtin:<name>", as the profile spells it.
            if (mode.Trim().Equals("builtin", StringComparison.OrdinalIgnoreCase))
            {
                return new CanvasBackgroundSettings(CanvasBackgroundMode.BuiltinMedia, path.Trim().ToLowerInvariant(), fps, lowPower);
            }

            return new CanvasBackgroundSettings(ParseMode(mode), path, fps, lowPower);
        }

        public static readonly CanvasBackgroundSettings Default = new(CanvasBackgroundMode.BuiltinGrey, "", 12, false);
    }

    /// <summary>
    /// The one replaceable background node. Created by the GameController,
    /// reads the current profile every frame, and rebuilds itself when the
    /// four keys change (Options -> Apply, or a different character's
    /// profile loading).
    /// </summary>
    internal sealed partial class CanvasBackground : CanvasLayer
    {
        /// <summary>
        /// A run-wide override that beats the profile and is never saved:
        /// what <c>--background mode[:path]</c> sets, so a screenshot run can
        /// drive each mode without touching anybody's profile.
        /// </summary>
        public static CanvasBackgroundSettings? Override { get; set; }

        /// <summary>
        /// True while this node is drawing the background, which is when
        /// RenderTargets must not draw upstream's tile over it.
        /// </summary>
        public bool Active { get; private set; }

        /// <summary>What is on screen now, after any fallback.</summary>
        public CanvasBackgroundSettings Current { get; private set; } = CanvasBackgroundSettings.Default;

        private CanvasBackgroundSettings? _applied;
        private Surface _surface;

        // Before any profile is loaded (the login screen and the character
        // list) the background follows the profile saved most recently on
        // this machine, so a player sees their own backdrop from the first
        // frame. Read once per pre-profile stretch.
        private CanvasBackgroundSettings? _preProfile;
        private bool _preProfileRead;

        public override void _Ready()
        {
            Layer = -1;
            Name = "CanvasBackground";

            _surface = new Surface { Name = "Surface" };
            AddChild(_surface);
        }

        public override void _Process(double delta)
        {
            CanvasBackgroundSettings wanted = Resolve();

            if (_applied == null || _applied.Value != wanted)
            {
                Apply(wanted);
            }
        }

        private CanvasBackgroundSettings Resolve()
        {
            if (Override.HasValue)
            {
                return Override.Value;
            }

            Profile profile = ProfileManager.CurrentProfile;

            if (profile != null)
            {
                _preProfileRead = false;

                return CanvasBackgroundSettings.FromProfile(profile);
            }

            if (!_preProfileRead)
            {
                _preProfileRead = true;
                _preProfile = ReadMostRecentlySavedProfile();
            }

            return _preProfile ?? CanvasBackgroundSettings.Default;
        }

        /// <summary>
        /// The newest profile.json under the profiles root, read only for its
        /// background keys. The root is the one ProfileManager uses: the
        /// settings' profilespath, else Data\Profiles under the client home.
        /// </summary>
        private static CanvasBackgroundSettings? ReadMostRecentlySavedProfile()
        {
            try
            {
                string root = string.IsNullOrWhiteSpace(Settings.GlobalSettings?.ProfilesPath)
                    ? System.IO.Path.Combine(CUOEnviroment.ExecutablePath, "Data", "Profiles")
                    : Settings.GlobalSettings.ProfilesPath;

                if (!Directory.Exists(root))
                {
                    return null;
                }

                string newest = Directory
                    .EnumerateFiles(root, "profile.json", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (newest == null)
                {
                    return null;
                }

                Profile profile = ConfigurationResolver.Load<Profile>(newest, ProfileJsonContext.DefaultToUse.Profile);

                return profile == null ? null : CanvasBackgroundSettings.FromProfile(profile);
            }
            catch (Exception ex)
            {
                Log.Warn($"canvas background: could not read the last profile: {ex.Message}");

                return null;
            }
        }

        private void Apply(CanvasBackgroundSettings wanted)
        {
            _applied = wanted;

            string problem = _surface.Load(wanted);

            if (problem != null)
            {
                GD.Print($"[GUO] canvas background: {CanvasBackgroundSettings.ModeName(wanted.Mode)} '{wanted.Path}' failed: {problem}; showing builtin-grey");
                _surface.Load(CanvasBackgroundSettings.Default);
                Current = CanvasBackgroundSettings.Default;
            }
            else
            {
                Current = wanted;
                GD.Print($"[GUO] canvas background: {CanvasBackgroundSettings.ModeName(wanted.Mode)}"
                         + (wanted.Mode >= CanvasBackgroundMode.Image ? $" '{wanted.Path}' {_surface.ContentSize.X}x{_surface.ContentSize.Y}" : "")
                         + (wanted.Mode >= CanvasBackgroundMode.Video ? $" fps {wanted.Fps}{(wanted.LowPower ? " low-power" : "")}" : ""));
            }

            Active = Current.Mode != CanvasBackgroundMode.BuiltinGrey;
            _surface.Visible = Active;
        }

        /// <summary>
        /// Opens a file picker for a background and hands back the path to
        /// store. On a phone the picked file is copied into user://backgrounds
        /// and that path is returned: scoped storage does not let the client
        /// open the original again later. The Options gump calls this.
        /// </summary>
        public static void Browse(Node host, CanvasBackgroundMode mode, bool copyIntoUserDir, Action<string> picked)
        {
            var dialog = new FileDialog
            {
                Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = true,
                Title = "Canvas background",
                // A folder of frames on the desktop, a single file elsewhere
                // (a sprite sheet stands in for the folder on a phone).
                FileMode = mode == CanvasBackgroundMode.Frames && !copyIntoUserDir
                    ? FileDialog.FileModeEnum.OpenAny
                    : FileDialog.FileModeEnum.OpenFile
            };

            dialog.Filters = mode switch
            {
                CanvasBackgroundMode.Video => new[] { "*.ogv ; Ogg Theora video" },
                CanvasBackgroundMode.Frames => new[] { "*.png, *.jpg, *.jpeg, *.webp ; Frames or a sprite sheet" },
                _ => new[] { "*.png, *.jpg, *.jpeg, *.webp ; Images" }
            };

            void Done(string path)
            {
                dialog.QueueFree();

                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                if (copyIntoUserDir)
                {
                    path = CopyIntoUserDir(path);
                }

                picked?.Invoke(path);
            }

            dialog.FileSelected += Done;
            dialog.DirSelected += Done;
            dialog.Canceled += () => dialog.QueueFree();

            host.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(800, 600));
        }

        private static string CopyIntoUserDir(string source)
        {
            const string dir = "user://backgrounds";

            DirAccess.MakeDirRecursiveAbsolute(dir);

            string name = source.GetFile();

            if (string.IsNullOrWhiteSpace(name) || name.Contains(':'))
            {
                name = $"background_{DateTime.UtcNow:yyyyMMdd_HHmmss}{source.GetExtension()}";
            }

            string target = dir + "/" + name;

            using FileAccess from = FileAccess.Open(source, FileAccess.ModeFlags.Read);

            if (from == null)
            {
                Log.Warn($"canvas background: could not open '{source}': {FileAccess.GetOpenError()}");

                return source;
            }

            using FileAccess to = FileAccess.Open(target, FileAccess.ModeFlags.Write);

            if (to == null)
            {
                Log.Warn($"canvas background: could not write '{target}': {FileAccess.GetOpenError()}");

                return source;
            }

            to.StoreBuffer(from.GetBuffer((long) from.GetLength()));
            GD.Print($"[GUO] canvas background: copied '{source}' to '{target}'");

            return target;
        }

        /// <summary>
        /// The control that fills the window and draws the current mode. Its
        /// own texture filter is the only place a player's picture is
        /// sampled linearly.
        /// </summary>
        private sealed partial class Surface : Control
        {
            private CanvasBackgroundSettings _settings = CanvasBackgroundSettings.Default;

            private Texture2D _tile;                 // builtin-wood
            private Texture2D _image;                // image
            private VideoStreamPlayer _video;        // video
            private readonly List<(Texture2D texture, Rect2 region)> _frames = new(); // frames
            private int _frame;
            private double _frameClock;
            private bool _videoFrozen;
            private double _videoLastPosition;   // to notice the loop wrapping
            private int _videoLoops;

            /// <summary>Pixel size of the content, for cover scaling; zero for a tile.</summary>
            public Vector2 ContentSize { get; private set; }

            public override void _Ready()
            {
                MouseFilter = MouseFilterEnum.Ignore;
                FocusMode = FocusModeEnum.None;
                Resized += Layout;
                FitWindow();
            }

            /// <summary>
            /// Sized to the window in physical pixels, the same rectangle
            /// RenderTargets covers (Window.ClientBounds). Not anchored: a
            /// full-rect control on this layer came up at the window size
            /// divided by the OS scale on a 150% desktop, two thirds of it.
            /// </summary>
            private void FitWindow()
            {
                Vector2 window = DisplayServer.WindowGetSize();

                if (window != Size)
                {
                    Position = Vector2.Zero;
                    Size = window;
                }
            }

            /// <returns>Null on success, else what went wrong.</returns>
            public string Load(CanvasBackgroundSettings settings)
            {
                Clear();
                _settings = settings;

                try
                {
                    switch (settings.Mode)
                    {
                        case CanvasBackgroundMode.BuiltinGrey:
                            TextureFilter = TextureFilterEnum.Nearest;
                            break;

                        case CanvasBackgroundMode.BuiltinWood:
                            TextureFilter = TextureFilterEnum.Nearest;
                            _tile = WoodTile.Create();
                            ContentSize = _tile.GetSize();
                            break;

                        case CanvasBackgroundMode.Image:
                        {
                            TextureFilter = TextureFilterEnum.Linear;
                            Image image = LoadImage(settings.Path, out string error);

                            if (image == null)
                            {
                                return error;
                            }

                            _image = ImageTexture.CreateFromImage(image);
                            ContentSize = image.GetSize();
                            break;
                        }

                        case CanvasBackgroundMode.Video:
                        {
                            TextureFilter = TextureFilterEnum.Linear;

                            if (!FileAccess.FileExists(settings.Path))
                            {
                                return "no such file";
                            }

                            var stream = new VideoStreamTheora { File = settings.Path };

                            _video = new VideoStreamPlayer
                            {
                                Name = "Video",
                                Stream = stream,
                                Loop = true,
                                Expand = true,
                                Volume = 0f,
                                VolumeDb = -80f,
                                TextureFilter = TextureFilterEnum.Linear,
                                MouseFilter = MouseFilterEnum.Ignore
                            };

                            AddChild(_video);
                            _video.Play();

                            if (!_video.IsPlaying())
                            {
                                return "could not start the stream (not an Ogg Theora .ogv?)";
                            }

                            ContentSize = Vector2.Zero; // known once the first frame decodes
                            break;
                        }

                        case CanvasBackgroundMode.BuiltinMedia:
                        {
                            TextureFilter = TextureFilterEnum.Linear;
                            string error = LoadBuiltin(settings);

                            if (error != null)
                            {
                                return error;
                            }

                            break;
                        }

                        case CanvasBackgroundMode.Frames:
                        {
                            TextureFilter = TextureFilterEnum.Linear;
                            string error = LoadFrames(settings.Path);

                            if (error != null)
                            {
                                return error;
                            }

                            ContentSize = _frames[0].region.Size;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Clear();

                    return ex.Message;
                }

                Layout();
                QueueRedraw();

                return null;
            }

            private void Clear()
            {
                _tile = null;
                _image = null;
                _frames.Clear();
                _frame = 0;
                _frameClock = 0;
                _videoFrozen = false;
                ContentSize = Vector2.Zero;

                if (_video != null)
                {
                    _video.Stop();
                    RemoveChild(_video);
                    _video.QueueFree();
                    _video = null;
                }
            }

            /// <summary>
            /// A shipped background: its still when low power is on or the
            /// video cannot be opened, else the video looping over the still.
            /// Both come through Godot's import in an export and straight
            /// off the file in a project run, so each is tried both ways.
            /// </summary>
            private string LoadBuiltin(CanvasBackgroundSettings settings)
            {
                BuiltinBackground b = BuiltinBackground.Find(settings.Path);

                if (b == null)
                {
                    return BuiltinBackground.All.Count == 0 ? "no built-in backgrounds shipped" : "no such built-in background";
                }

                if (b.Still != null)
                {
                    Texture2D still = ResourceLoader.Exists(b.Still) ? GD.Load<Texture2D>(b.Still) : null;

                    if (still == null)
                    {
                        Image image = LoadImage(b.Still, out _);
                        still = image == null ? null : ImageTexture.CreateFromImage(image);
                    }

                    if (still != null)
                    {
                        _image = still;
                        ContentSize = still.GetSize();
                    }
                }

                if (settings.LowPower || b.Video == null)
                {
                    return _image == null ? "no still and low power" : null;
                }

                VideoStream stream = ResourceLoader.Exists(b.Video) ? GD.Load<VideoStream>(b.Video) : null;

                if (stream == null && FileAccess.FileExists(b.Video))
                {
                    stream = new VideoStreamTheora { File = b.Video };
                }

                if (stream == null)
                {
                    return _image == null ? "video and still both missing" : null;
                }

                _video = new VideoStreamPlayer
                {
                    Name = "Video",
                    Stream = stream,
                    Loop = true,
                    Expand = true,
                    Volume = 0f,
                    VolumeDb = -80f,
                    TextureFilter = TextureFilterEnum.Linear,
                    MouseFilter = MouseFilterEnum.Ignore
                };

                AddChild(_video);
                _video.Play();

                if (!_video.IsPlaying())
                {
                    RemoveChild(_video);
                    _video.QueueFree();
                    _video = null;

                    return _image == null ? "could not start the stream" : null;
                }

                return null;
            }

            private static Image LoadImage(string path, out string error)
            {
                error = null;

                if (string.IsNullOrWhiteSpace(path))
                {
                    error = "no path";

                    return null;
                }

                if (!FileAccess.FileExists(path))
                {
                    error = "no such file";

                    return null;
                }

                var image = new Image();
                Error err = image.Load(path);

                if (err != Error.Ok || image.IsEmpty())
                {
                    error = $"could not decode ({err})";

                    return null;
                }

                return image;
            }

            /// <summary>
            /// A folder of images, in natural order; or one sprite sheet
            /// whose name ends in <c>_CxR</c> (columns x rows), else a strip
            /// of square frames.
            /// </summary>
            private string LoadFrames(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return "no path";
                }

                if (DirAccess.DirExistsAbsolute(path))
                {
                    string[] names = DirAccess.GetFilesAt(path)
                        .Where(n => IsImageName(n))
                        .OrderBy(n => n, NaturalOrder.Instance)
                        .ToArray();

                    if (names.Length == 0)
                    {
                        return "no png/jpg/webp files in the folder";
                    }

                    Vector2 size = Vector2.Zero;

                    foreach (string name in names)
                    {
                        Image image = LoadImage(path.TrimEnd('/', '\\') + "/" + name, out string error);

                        if (image == null)
                        {
                            return $"{name}: {error}";
                        }

                        if (size == Vector2.Zero)
                        {
                            size = image.GetSize();
                        }

                        _frames.Add((ImageTexture.CreateFromImage(image), new Rect2(Vector2.Zero, size)));
                    }

                    return null;
                }

                Image sheet = LoadImage(path, out string sheetError);

                if (sheet == null)
                {
                    return sheetError;
                }

                int columns, rows;
                string stem = path.GetFile().GetBaseName();
                int us = stem.LastIndexOf('_');

                if (us >= 0 && TryParseGrid(stem.Substring(us + 1), out columns, out rows))
                {
                }
                else
                {
                    // A strip of square frames.
                    rows = 1;
                    columns = Math.Max(1, sheet.GetWidth() / Math.Max(1, sheet.GetHeight()));
                }

                Texture2D texture = ImageTexture.CreateFromImage(sheet);
                var cell = new Vector2(sheet.GetWidth() / (float) columns, sheet.GetHeight() / (float) rows);

                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < columns; c++)
                    {
                        _frames.Add((texture, new Rect2(c * cell.X, r * cell.Y, cell.X, cell.Y)));
                    }
                }

                return null;
            }

            private static bool TryParseGrid(string s, out int columns, out int rows)
            {
                columns = rows = 0;
                int x = s.IndexOf('x');

                return x > 0
                       && int.TryParse(s.Substring(0, x), out columns)
                       && int.TryParse(s.Substring(x + 1), out rows)
                       && columns > 0 && rows > 0;
            }

            private static bool IsImageName(string name)
            {
                string ext = name.GetExtension().ToLowerInvariant();

                return ext == "png" || ext == "jpg" || ext == "jpeg" || ext == "webp";
            }

            /// <summary>
            /// Where a content of ContentSize goes to cover the window:
            /// scaled up until both axes are filled, aspect kept, centred.
            /// </summary>
            private Rect2 CoverRect()
            {
                Vector2 window = Size;

                if (ContentSize.X <= 0 || ContentSize.Y <= 0 || window.X <= 0 || window.Y <= 0)
                {
                    return new Rect2(Vector2.Zero, window);
                }

                float scale = Math.Max(window.X / ContentSize.X, window.Y / ContentSize.Y);
                var size = ContentSize * scale;

                return new Rect2((window - size) * 0.5f, size);
            }

            private void Layout()
            {
                if (_video != null)
                {
                    Rect2 rect = CoverRect();
                    _video.Position = rect.Position;
                    _video.Size = rect.Size;
                }

                QueueRedraw();
            }

            public override void _Process(double delta)
            {
                FitWindow();

                if (_video != null)
                {
                    // The stream's size is only known once a frame decoded.
                    if (ContentSize == Vector2.Zero)
                    {
                        Texture2D t = _video.GetVideoTexture();

                        if (t != null && t.GetWidth() > 0)
                        {
                            ContentSize = t.GetSize();
                            Layout();
                        }
                    }

                    // Say when the stream wraps: a scripted run can then put
                    // two frames either side of the seam on the record.
                    double pos = _video.StreamPosition;

                    if (pos + 1.0 < _videoLastPosition)
                    {
                        _videoLoops++;
                        GD.Print($"[GUO] canvas background: video looped ({_videoLoops}) at process frame {Engine.GetProcessFrames()}, {_videoLastPosition:F2}s -> {pos:F2}s");
                    }

                    _videoLastPosition = pos;

                    if (_settings.LowPower && !_videoFrozen && pos > 0)
                    {
                        // Low power: the first frame stays.
                        _video.Paused = true;
                        _videoFrozen = true;
                    }
                }

                if (_frames.Count > 1 && !_settings.LowPower)
                {
                    _frameClock += delta;
                    double period = 1.0 / _settings.Fps;

                    if (_frameClock >= period)
                    {
                        int advance = (int) (_frameClock / period);
                        _frameClock -= advance * period;
                        _frame = (_frame + advance) % _frames.Count;
                        QueueRedraw();
                    }
                }
            }

            public override void _Draw()
            {
                if (_tile != null)
                {
                    DrawTextureRect(_tile, new Rect2(Vector2.Zero, Size), tile: true);
                }
                else if (_image != null && _video == null)
                {
                    DrawTextureRect(_image, CoverRect(), tile: false);
                }
                else if (_frames.Count > 0)
                {
                    (Texture2D texture, Rect2 region) = _frames[_frame];
                    DrawTextureRectRegion(texture, CoverRect(), region);
                }
                // video: the VideoStreamPlayer child draws itself.
            }
        }

        /// <summary>
        /// Folder listings in the order a person numbers frames: "frame_2"
        /// before "frame_10".
        /// </summary>
        private sealed class NaturalOrder : IComparer<string>
        {
            public static readonly NaturalOrder Instance = new();

            public int Compare(string a, string b)
            {
                int i = 0, j = 0;

                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        int si = i, sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;

                        string na = a.Substring(si, i - si).TrimStart('0');
                        string nb = b.Substring(sj, j - sj).TrimStart('0');

                        // Shorter (after leading zeros) is smaller; same
                        // length compares digit by digit.
                        int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);

                        if (c != 0) return c;
                    }
                    else
                    {
                        int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));

                        if (c != 0) return c;

                        i++;
                        j++;
                    }
                }

                return (a.Length - i).CompareTo(b.Length - j);
            }
        }
    }

    /// <summary>
    /// The dark wood tile GUO owns: made here from arithmetic, so there is no
    /// art file to licence, import or export. 256x256, tileable both ways.
    /// </summary>
    internal static class WoodTile
    {
        private static ImageTexture _cached;

        public static ImageTexture Create()
        {
            if (_cached != null)
            {
                return _cached;
            }

            const int size = 256;
            const int plank = 64;
            var image = Image.CreateEmpty(size, size, false, Image.Format.Rgb8);

            for (int y = 0; y < size; y++)
            {
                int plankIndex = y / plank;
                int inPlank = y % plank;

                // Each plank is shifted along the grain so the seams do not
                // line up, and gets its own tone.
                float shift = plankIndex * 61f;
                float tone = 0.85f + 0.15f * Hash(plankIndex, 7);

                for (int x = 0; x < size; x++)
                {
                    float gx = (x + shift) * (2f * MathF.PI / size);

                    // Grain runs along the plank: lines that vary with y,
                    // bent by slow waves in x. Every wave is a whole number
                    // of periods across the tile or the plank, which is
                    // what makes the tile seamless both ways.
                    float wobble = MathF.Sin(gx * 2f) * 2.5f + MathF.Sin(gx * 5f + 1.3f) * 1.2f;
                    float gy = (inPlank + wobble) * (2f * MathF.PI / plank);

                    float grain = MathF.Sin(gy * 6f) * 0.5f
                                  + MathF.Sin(gy * 13f + gx * 3f) * 0.3f
                                  + MathF.Sin(gy * 29f) * 0.15f;

                    float noise = (Hash(x, y) - 0.5f) * 0.10f;

                    // A darker line on each plank edge, and a shadow just inside it.
                    float edge = inPlank == 0 || inPlank == plank - 1 ? 0.55f : inPlank == 1 ? 0.8f : 1f;

                    float v = (0.5f + 0.5f * grain) * 0.35f + 0.65f + noise;
                    v *= tone * edge;

                    // Dark walnut: red over green over blue.
                    float r = Math.Clamp(v * 0.30f, 0f, 1f);
                    float g = Math.Clamp(v * 0.20f, 0f, 1f);
                    float b = Math.Clamp(v * 0.13f, 0f, 1f);

                    image.SetPixel(x, y, new Color(r, g, b));
                }
            }

            _cached = ImageTexture.CreateFromImage(image);

            return _cached;
        }

        /// <summary>A fixed pseudo-random value in [0,1) for a lattice point.</summary>
        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint) x * 374761393u + (uint) y * 668265263u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;

                return (h & 0xFFFFFF) / (float) 0x1000000;
            }
        }
    }
}
