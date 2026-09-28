// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Game.UI.Gumps.Login;
using GUO.Input.Touch;

namespace GUO.Host;

/// <summary>
/// C9, the portrait spike (docs/android_portrait_spike.md): turn the screen
/// to portrait at runtime and measure what copes, against landscape, on the
/// same build. Five measures, each with a kill line.
/// </summary>
/// <remarks>
/// Not a portrait mode. The project stays sensor-landscape; this only asks
/// the display to rotate for the length of the probe. Each state is held a
/// few seconds after a "[GUO] portrait: hold NAME" line so the Android tool
/// can photograph the panel itself (screencap); each measure prints one
/// "[GUO] portrait: row" line the tool turns into a table. On the desktop,
/// where there is no display to rotate, the window is resized tall instead.
/// </remarks>
internal static class PortraitProbe
{
    public static bool Passed { get; private set; }

    private const ulong HoldMs = 5000;
    private const int TileWidth = 44;

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        // 1 and 2: rotate at the login gump, before anything else is open.
        LoginGump login = null;

        for (int i = 0; i < 7200 && login == null; i++)
        {
            await InputProbe.Wait(host, 1);
            login = UIManager.GetGump<LoginGump>();
        }

        if (login == null)
        {
            GD.PrintErr("[GUO] portrait probe: FAIL no login gump");

            return;
        }

        await InputProbe.Wait(host, 60);
        string loginL = LoginFit(login);
        int colorsL = await Colours(host);
        await Hold("login_landscape");

        Rotation turn = await Rotate(host, true);
        await InputProbe.Wait(host, 60);
        login = UIManager.GetGump<LoginGump>();
        string loginP = login == null ? "gone" : LoginFit(login);
        int colorsP = await Colours(host);
        await Hold("login_portrait");

        bool turned = turn.Flipped && colorsP * 4 >= colorsL;
        Row(1, "the rotation", $"{colorsL} colours", $"flipped {turn.Flipped} in {turn.Ms} ms, {turn.Events} resize(s), {colorsP} colours", !turned);
        Row(2, "the login gump fits", loginL, loginP, login == null || !loginP.StartsWith("fits"));

        await Rotate(host, false);

        // Logged in as DualProbe does on a device: the touch layer would
        // swallow the probe's clicks, which aim in client pixels.
        bool touch = TouchInput.Enabled;
        TouchInput.Enabled = false;
        InputProbe.PointerScale = Client.Game.DpiScale;
        await InputProbe.EnterTheWorld(host, 200);
        InputProbe.PointerScale = 1f;
        TouchInput.Enabled = touch;
        World world = Client.Game.UO.World;

        if (!world.InGame)
        {
            GD.PrintErr("[GUO] portrait probe: FAIL never got into the world");
            Finish(false);

            return;
        }

        // 3 to 5, in each orientation.
        var land = await Measure(host, world, "landscape");
        await Rotate(host, true);
        await InputProbe.Wait(host, 90);
        var port = await Measure(host, world, "portrait");
        await Rotate(host, false);
        await InputProbe.Wait(host, 30);

        Row(3, "world tiles across x down", land.Tiles, port.Tiles, port.TilesAcross < 9f);
        Row(4, "command bar: slot mm, three rows % of screen", land.Bar, port.Bar, port.SlotMm < 7f || port.RowsShare > 0.30f);
        Row(5, "gumps: scale to fit the width", land.Gumps, port.Gumps, port.EverydayMin < 1f);

        Finish(true);
    }

    private static void Finish(bool ran)
    {
        Passed = ran;
        GD.Print($"[GUO] portrait probe: {(ran ? "done" : "FAILED")}");
    }

    private struct Rotation
    {
        public bool Flipped;
        public int Events;
        public ulong Ms;
    }

    /// <summary>Ask for portrait (or landscape back), and wait for the window to take it.</summary>
    private static async System.Threading.Tasks.Task<Rotation> Rotate(Node host, bool portrait)
    {
        Viewport vp = host.GetViewport();
        var r = new Rotation();
        void Count() => r.Events++;
        vp.SizeChanged += Count;
        ulong start = Godot.Time.GetTicksMsec();

        if (OS.GetName() == "Android")
        {
            DisplayServer.ScreenSetOrientation(portrait
                ? DisplayServer.ScreenOrientation.Portrait
                : DisplayServer.ScreenOrientation.SensorLandscape);
        }
        else
        {
            Vector2I s = DisplayServer.WindowGetSize();
            bool tall = s.Y > s.X;

            if (tall != portrait)
            {
                DisplayServer.WindowSetSize(new Vector2I(s.Y, s.X));
            }
        }

        for (int i = 0; i < 600; i++)
        {
            await InputProbe.Wait(host, 1);
            Vector2 now = vp.GetVisibleRect().Size;

            if (now.Y > now.X == portrait)
            {
                r.Flipped = true;
                r.Ms = Godot.Time.GetTicksMsec() - start;

                break;
            }
        }

        // Late resizes still count: a surface that settles in two steps shows here.
        await InputProbe.Wait(host, 60);
        vp.SizeChanged -= Count;

        return r;
    }

    private static string LoginFit(Gump g)
    {
        Compat.Rectangle b = GumpPresentation.Bounds(g);
        Compat.Rectangle c = Client.Game.ClientBounds;
        bool fits = b.X >= 0 && b.Y >= 0 && b.Right <= c.Width && b.Bottom <= c.Height;

        return $"{(fits ? "fits" : "clips")} {b.Width}x{b.Height} in {c.Width}x{c.Height} at dpi scale {Client.Game.DpiScale:0.##}";
    }

    /// <summary>Distinct colours on a 32x32 grid of the frame: a lost atlas draws flat.</summary>
    private static async System.Threading.Tasks.Task<int> Colours(Node host)
    {
        await InputProbe.Wait(host, 2);
        Image frame = host.GetViewport().GetTexture().GetImage();
        var seen = new System.Collections.Generic.HashSet<Color>();

        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                seen.Add(frame.GetPixel(x * (frame.GetWidth() - 1) / 31, y * (frame.GetHeight() - 1) / 31));
            }
        }

        return seen.Count;
    }

    private struct Measures
    {
        public string Tiles, Bar, Gumps;
        public float TilesAcross, SlotMm, RowsShare, EverydayMin;
    }

    private static async System.Threading.Tasks.Task<Measures> Measure(Node host, World world, string orientation)
    {
        var m = new Measures();

        // 3: the world, edge to edge through the middle of the view, in tiles.
        Renderer.Camera camera = Client.Game.Scene.Camera;
        Compat.Rectangle b = camera.Bounds;
        int midY = b.Y + b.Height / 2, midX = b.X + b.Width / 2;
        Compat.Point l = camera.ScreenToWorld(new Compat.Point(b.X, midY), true);
        Compat.Point r = camera.ScreenToWorld(new Compat.Point(b.Right, midY), true);
        Compat.Point t = camera.ScreenToWorld(new Compat.Point(midX, b.Y), true);
        Compat.Point d = camera.ScreenToWorld(new Compat.Point(midX, b.Bottom), true);
        m.TilesAcross = (r.X - l.X) / (float)TileWidth;
        float down = (d.Y - t.Y) / (float)TileWidth;
        m.Tiles = $"{m.TilesAcross:0.#} x {down:0.#} (view {b.Width}x{b.Height}, zoom {camera.Zoom:0.##})";
        await Hold($"world_{orientation}");

        // 4: one slot of the command bar, and what three rows would take.
        TouchGumpBar bar = TouchInput.Bar;

        if (bar == null)
        {
            m.Bar = "no bar";
            m.SlotMm = 0f;
        }
        else
        {
            Rect2 slot = bar.SlotRect(1, 0);
            float dpi = System.Math.Max(1, DisplayServer.ScreenGetDpi());
            float screen = host.GetViewport().GetVisibleRect().Size.Y;
            m.SlotMm = slot.Size.X / dpi * 25.4f;
            m.RowsShare = slot.Size.Y * TouchGumpBar.RowCount / screen;
            m.Bar = $"{m.SlotMm:0.#} mm ({slot.Size.X:0}x{slot.Size.Y:0} px at {dpi} dpi), three rows {m.RowsShare:P0}";
        }

        // 5: the gumps a player opens every session, as the client opens them.
        var fits = new System.Collections.Generic.List<string>();
        m.EverydayMin = float.MaxValue;
        int width = GumpPresentation.DisplayBounds(false).Width;

        void Note(string name, float w, bool everyday)
        {
            float k = w <= 0 ? 0f : width / w;
            fits.Add($"{name} {k:0.00}");

            if (everyday && w > 0)
            {
                m.EverydayMin = System.Math.Min(m.EverydayMin, k);
            }
        }

        UIManager.GetGump<PaperDollGump>(world.Player.Serial)?.Dispose();
        await InputProbe.Wait(host, 5);
        GameActions.OpenPaperdoll(world, world.Player.Serial);
        GameActions.OpenBackpack(world);
        await InputProbe.Wait(host, 60);
        PaperDollGump doll = UIManager.GetGump<PaperDollGump>(world.Player.Serial);
        ContainerGump pack = UIManager.GetGump<ContainerGump>(world.Player.FindItemByLayer(Game.Data.Layer.Backpack)?.Serial ?? 0);
        Note("paperdoll", doll == null ? 0 : GumpPresentation.Width(doll), true);
        Note("backpack", pack == null ? 0 : GumpPresentation.Width(pack), true);
        await Hold($"gumps_{orientation}");
        doll?.Dispose();
        pack?.Dispose();

        GameActions.OpenWorldMap(world);
        await InputProbe.Wait(host, 60);
        WorldMapGump map = UIManager.GetGump<WorldMapGump>();
        Note("world map", map == null ? 0 : GumpPresentation.Width(map), false);
        map?.Dispose();

        foreach ((string name, System.Action open) in new (string, System.Action)[]
        {
            ("Modern Options", () => GameActions.OpenSettings(world)),
            ("Modern Skills", () => GameActions.OpenSkills(world)),
        })
        {
            open();

            for (int i = 0; i < 120 && !Input.Touch.Modern.ModernGump.IsOpen; i++)
            {
                await InputProbe.Wait(host, 1);
            }

            var view = Input.Touch.Modern.ModernGump.Current;
            Note(name, Input.Touch.Modern.ModernGump.IsOpen ? view.Rect.Size.X : 0, true);

            if (Input.Touch.Modern.ModernGump.IsOpen)
            {
                await Hold($"{name.Replace(' ', '_').ToLowerInvariant()}_{orientation}");
                view.Close();
                await InputProbe.Wait(host, 10);
            }
        }

        if (m.EverydayMin == float.MaxValue)
        {
            m.EverydayMin = 0f;
        }

        m.Gumps = string.Join(", ", fits);

        return m;
    }

    private static void Row(int n, string what, string landscape, string portrait, bool kills) =>
        GD.Print($"[GUO] portrait: row | {n} | {what} | {landscape} | {portrait} | {(kills ? "KILL" : "ok")} |");

    /// <summary>Say what is on screen, then keep it there long enough to be photographed.</summary>
    private static async System.Threading.Tasks.Task Hold(string name)
    {
        GD.Print($"[GUO] portrait: hold {name}");
        ulong until = Godot.Time.GetTicksMsec() + HoldMs;
        var tree = (SceneTree)Engine.GetMainLoop();

        while (Godot.Time.GetTicksMsec() < until)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
}
