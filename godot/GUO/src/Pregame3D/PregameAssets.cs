// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;
using GUO.Game.Data;

namespace GUO.Pregame3D;

/// <summary>
/// Everything the pregame shows, loaded before it is shown (docs/ui/pregame_3d.md,
/// "Loading"): a smooth start, a short wait at the beginning or after the
/// login, never a half-styled screen.
/// <list type="bullet">
/// <item><b>At boot</b> (behind the splash and the painting's own "Loading"):
/// every gump the pregame can draw — the painting's layers, the login gump's
/// buttons in all their states, the checkboxes, every ResizePic frame, the
/// arrows and buttons of the server and character panels, the profession
/// icons — decoded on a worker thread by a private gump reader (its own file
/// handles, so the client's reader is never shared across threads), straight
/// to images: no atlas read-back. The UO font is built then too.</item>
/// <item><b>After the login</b> (while the server and character lists are up):
/// the figure's animation frames (every body, hair, beard and clothing piece
/// the creation offers, in all directions), and the map for Home once the
/// shard has sent its cities.</item>
/// </list>
/// Timings go to the log ("pregame assets ready in N ms", ...).
/// </summary>
internal static class PregameAssets
{
    private static readonly ConcurrentDictionary<ushort, Image> _images = new();
    private static readonly Dictionary<ushort, Texture2D> _textures = new();
    private static Task _boot;
    private static readonly Stopwatch _clock = new();
    private static readonly Queue<Action> _afterLogin = new();
    private static bool _afterLoginStarted;
    private static int _afterLoginTotal;

    public static readonly Dictionary<string, long> Timings = new();

    /// <summary>The boot set is in: the pregame may build its steps.</summary>
    public static bool Ready => _boot != null && _boot.IsCompleted;

    /// <summary>0..1 of the boot set, for the loading line.</summary>
    public static float Progress => _progress;

    // Written by the worker thread, read by the main one every frame.
    private static volatile float _progress;

    /// <summary>The figure's frames are in the atlas.</summary>
    public static bool FiguresReady => _afterLoginStarted && _afterLogin.Count == 0;

    // --- boot -----------------------------------------------------------------------

    public static void Start(Painting art)
    {
        if (_boot != null)
        {
            return;
        }

        _clock.Restart();
        var ids = new List<ushort>(art.GumpIds);

        foreach (ushort first in Overlay.FrameIds)
        {
            for (int i = 0; i < 9; i++)
            {
                ids.Add((ushort) (first + i));
            }
        }

        // The server and character panels' buttons: arrows, New, Delete.
        for (ushort id = 0x15A1; id <= 0x15A6; id++)
        {
            ids.Add(id);
        }

        for (ushort id = 0x159A; id <= 0x159F; id++)
        {
            ids.Add(id);
        }

        ids.Add(Painting.CheckboxOff);
        ids.Add(Painting.CheckboxOn);

        foreach (KeyValuePair<ProfessionInfo, List<ProfessionInfo>> kv in Client.Game.UO.FileManager.Professions.Professions)
        {
            ids.Add(kv.Key.Graphic);

            foreach (ProfessionInfo child in kv.Value ?? new List<ProfessionInfo>())
            {
                ids.Add(child.Graphic);
            }
        }

        _ = Input.Touch.UoTheme.Font; // the font atlas, on the main thread

        _boot = Task.Run(() =>
        {
            var reader = new GumpsLoader(Client.Game.UO.FileManager);
            reader.Load();
            int done = 0;

            foreach (ushort id in ids)
            {
                if (id != 0 && !_images.ContainsKey(id))
                {
                    GumpInfo g = reader.GetGump(id);

                    if (g.Width > 0 && g.Height > 0 && !g.Pixels.IsEmpty)
                    {
                        byte[] bytes = new byte[g.Width * g.Height * 4];
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(g.Pixels).Slice(0, bytes.Length).CopyTo(bytes);
                        _images[id] = Image.CreateFromData(g.Width, g.Height, false, Image.Format.Rgba8, bytes);
                    }
                }

                _progress = ++done / (float) ids.Count;
            }

            reader.File?.Dispose();
            Note("pregame assets", _clock.ElapsedMilliseconds, $"{_images.Count} gumps");
        });

        // A worker that throws would otherwise end silently (Ready is true for a faulted task too).
        _ = _boot.ContinueWith(
            t => GD.PrintErr($"[GUO] pregame3d: the asset worker failed: {t.Exception?.GetBaseException()}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public static void Note(string what, long ms, string detail = "")
    {
        lock (Timings)
        {
            Timings[what] = ms;
        }

        GD.Print($"[GUO] pregame3d: {what} ready in {ms} ms{(detail.Length > 0 ? " (" + detail + ")" : "")}");
    }

    /// <summary>A gump as an image: from the boot set, else (anything outside it) read now.</summary>
    public static Image Gump(ushort id)
    {
        if (_images.TryGetValue(id, out Image image))
        {
            return image;
        }

        image = Input.Touch.UoTheme.GumpImage(id);

        if (image != null)
        {
            _images[id] = image;
        }

        return image;
    }

    /// <summary>A gump as a texture of its own (main thread).</summary>
    public static Texture2D Texture(ushort id)
    {
        if (_textures.TryGetValue(id, out Texture2D t))
        {
            return t;
        }

        Image image = Gump(id);

        if (image == null)
        {
            return null;
        }

        t = ImageTexture.CreateFromImage(image);
        _textures[id] = t;
        return t;
    }

    // --- after the login ---------------------------------------------------------------

    /// <summary>Queues the figure's frames: every body, hair, beard and garment of creation, in all directions.</summary>
    public static void StartAfterLogin()
    {
        if (_afterLoginStarted)
        {
            return;
        }

        _afterLoginStarted = true;
        _clock.Restart();
        var graphics = new List<(ushort graphic, bool equip)>();

        foreach (ushort body in new ushort[] { 0x0190, 0x0191, 0x025D, 0x025E, 0x029A, 0x029B })
        {
            graphics.Add((body, false));
        }

        var items = new List<int> { 0x1710, 0x152F, 0x1531, 0x1518, 0x4001 };

        foreach (RaceType race in new[] { RaceType.HUMAN, RaceType.ELF, RaceType.GARGOYLE })
        {
            foreach (bool female in new[] { false, true })
            {
                var hairs = CharacterCreationValues.GetHairComboContent(female, race);

                for (int i = 0; i < hairs.Labels.Length; i++)
                {
                    items.Add(hairs.GetGraphic(i));
                }
            }

            var beards = CharacterCreationValues.GetFacialHairComboContent(race);

            for (int i = 0; i < beards.Labels.Length; i++)
            {
                items.Add(beards.GetGraphic(i));
            }
        }

        foreach (int item in items)
        {
            if (item > 0 && item < Client.Game.UO.FileManager.TileData.StaticData.Length)
            {
                ushort anim = Client.Game.UO.FileManager.TileData.StaticData[item].AnimID;

                if (anim != 0)
                {
                    graphics.Add((anim, true));
                }
            }
        }

        foreach ((ushort graphic, bool equip) in graphics)
        {
            for (byte dir = 0; dir < 5; dir++)
            {
                ushort g = graphic;
                byte d = dir;
                bool e = equip;
                _afterLogin.Enqueue(() => Client.Game.UO.Animations.GetAnimationFrames(g, (byte) PeopleAnimationGroup.Stand, d, out _, out _, e));
            }
        }

        _afterLoginTotal = _afterLogin.Count;
    }

    /// <summary>Once a frame from the pregame: some of the after-login work, within a few milliseconds.</summary>
    public static void Step(double budgetMs = 3.0)
    {
        if (_afterLogin.Count == 0)
        {
            return;
        }

        var watch = Stopwatch.StartNew();

        while (_afterLogin.Count > 0 && watch.Elapsed.TotalMilliseconds < budgetMs)
        {
            _afterLogin.Dequeue()();
        }

        if (_afterLogin.Count == 0)
        {
            Note("figure frames", _clock.ElapsedMilliseconds, $"{_afterLoginTotal} sets");
        }
    }

    // --- the Home map ---------------------------------------------------------------------

    private static readonly Dictionary<string, Task<Image>> _maps = new();

    /// <summary>The map image for a plan, started now (or already); the key names it.</summary>
    public static Task<Image> Map(int facet, Rect2I region, Vector2I size)
    {
        string key = $"{facet}:{region}:{size}";

        lock (_maps)
        {
            if (!_maps.TryGetValue(key, out Task<Image> task))
            {
                var watch = Stopwatch.StartNew();
                task = WorldMapImage.Build(facet, region, size).ContinueWith(t =>
                {
                    Note("map", watch.ElapsedMilliseconds, $"{size.X}x{size.Y} of facet {facet}");
                    return t.Result;
                });
                _maps[key] = task;
            }

            return task;
        }
    }
}
