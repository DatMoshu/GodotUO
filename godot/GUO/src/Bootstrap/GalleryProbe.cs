// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input.Touch;
using GUO.Platform.Android;

namespace GUO.Host;

/// <summary>
/// Open each of GUO's own mobile UIs in turn and save a picture of each: the
/// command bar, the window menu, the minimised-window chips, the companion
/// tabs, the Options touch section, and the command bar's hold popup and slot
/// editor when they exist. For before/after comparisons of the UO style pass
/// (docs/ui/uo_godot_style.md); it checks nothing.
/// </summary>
/// <remarks>
/// Run with <c>--ui-gallery --screenshot-dir D --screenshot-name TAG</c>, and
/// <c>--dual-screen 1240x1080 --window-size 1920,1080</c> for the Thor's
/// shape. Each picture is TAG_NAME.png, with TAG_NAME_second.png for the
/// second screen. Needs the dev shard.
/// </remarks>
internal static class GalleryProbe
{
    public static bool Passed { get; private set; }

    private static string _dir;
    private static string _tag;

    public static async System.Threading.Tasks.Task Run(Node host, string dir, string tag)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? "user://screenshots" : dir;
        _tag = string.IsNullOrWhiteSpace(tag) ? "gallery" : tag;
        DirAccess.MakeDirRecursiveAbsolute(_dir);

        await InputProbe.Wait(host, 200);

        Game.World world = Client.Game.UO.World;

        // With --autologin (a device, whose login gump is not where the
        // probe's desktop clicks land) the client logs itself in; wait for it.
        if (Configuration.Settings.GlobalSettings.AutoLogin)
        {
            for (int i = 0; i < 60 && !world.InGame; i++)
            {
                await InputProbe.Wait(host, 60);
            }
        }
        else
        {
            bool touch = TouchInput.Enabled;
            TouchInput.Enabled = false;
            await InputProbe.EnterTheWorld(host, 0);
            TouchInput.Enabled = touch;
        }

        if (!world.InGame)
        {
            GD.PrintErr("[GUO] gallery: FAIL never got into the world -- is the dev shard running?");
            return;
        }

        Configuration.Profile profile = Configuration.ProfileManager.CurrentProfile;
        profile.TouchMacroRow = true;
        await InputProbe.Wait(host, 120);

        // The last target, so the bar shows its header.
        Game.GameObjects.Mobile nearest = null;

        foreach (Game.GameObjects.Mobile m in world.Mobiles.Values)
        {
            if (m != world.Player && (nearest == null || m.Distance < nearest.Distance))
            {
                nearest = m;
            }
        }

        if (nearest != null)
        {
            world.TargetManager.LastTargetInfo.SetEntity(nearest.Serial);
        }

        // The paperdoll, as a touch player opens it: fitted on one screen (gump index).
        UIManager.GetGump<PaperDollGump>(world.Player.Serial)?.Dispose();
        GumpPresentation.PaperdollScale = 0f;
        await InputProbe.Wait(host, 5);
        Game.GameActions.OpenPaperdoll(world, world.Player);
        await InputProbe.Wait(host, 60);
        PaperDollGump paperdoll = UIManager.GetGump<PaperDollGump>(world.Player.Serial);
        await Save(host, "paperdoll");

        // The bar, three rows open.
        TouchGumpBar bar = TouchInput.Bar;

        if (bar != null)
        {
            bar.ResetSession();
            bar.TapHandle();
            await InputProbe.Wait(host, 30);
            bar.TapHandle();
            await InputProbe.Wait(host, 5);
            bar.TapHandle();
            await InputProbe.Wait(host, 30);
            await Save(host, "bar");

            // The hold popup and the slot editor, where this build has them.
            if (await HoldPopup(host, bar))
            {
                await Save(host, "popup");
            }
        }

        // The window menu, for the paperdoll.
        if (paperdoll != null)
        {
            WindowMenu.Open(paperdoll);
            await InputProbe.Wait(host, 30);
            await Save(host, "window_menu");
            WindowMenu.Close();
            await InputProbe.Wait(host, 5);

            // Minimised: the chips (the tray on one screen, the lower screen on two).
            GumpMinimise.Minimise(paperdoll);
            Game.GameActions.OpenStatusBar(world);
            await InputProbe.Wait(host, 30);
            StatusGumpBase status = StatusGumpBase.GetStatusGump();
            GumpMinimise.Minimise(status);
            await InputProbe.Wait(host, 30);
            await Save(host, "chips");
            GumpMinimise.Restore(paperdoll);
            GumpMinimise.Restore(status);
            await InputProbe.Wait(host, 10);
        }

        // The companion tabs, where there is a second screen.
        if (DualScreen.HasSecondaryDisplay)
        {
            bool tabs = profile.CompanionTabs;
            profile.CompanionTabs = true;
            await InputProbe.Wait(host, 60);
            await Save(host, "companion");
            profile.CompanionTabs = tabs;
            await InputProbe.Wait(host, 30);
        }

        // The slot editor, where this build has one.
        if (await OpenEditor(host))
        {
            await Save(host, "editor");
        }

        // Options, as a touch player gets it: Modern (ADR-0024), then Classic, fitted (C11).
        profile.ModernGumpsOff = false;
        Game.GameActions.OpenSettings(world);
        await InputProbe.Wait(host, 60);
        await Save(host, "options");
        Input.Touch.Modern.ModernGump.Current?.Close();
        profile.ModernGumpsOff = true;
        Game.GameActions.OpenSettings(world);
        await InputProbe.Wait(host, 60);
        await Save(host, "options_classic");
        UIManager.GetGump<OptionsGump>()?.Dispose();
        await InputProbe.Wait(host, 10);

        // Skills, Modern (ADR-0024, gump 3): opened on the server's answer,
        // from no classic skills gump (OpenSkills would only un-minimize one).
        profile.ModernGumpsOff = false;
        UIManager.GetGump<StandardSkillsGump>()?.Dispose();
        UIManager.GetGump<SkillGumpAdvanced>()?.Dispose();
        await InputProbe.Wait(host, 5);
        Game.GameActions.OpenSkills(world);

        for (int i = 0; i < 180 && Input.Touch.Modern.ModernGump.Current is not Input.Touch.Modern.ModernSkills; i++)
        {
            await InputProbe.Wait(host, 1);
        }

        await InputProbe.Wait(host, 20);
        await Save(host, "skills");
        Input.Touch.Modern.ModernGump.Current?.Close();
        profile.ModernGumpsOff = true;
        await InputProbe.Wait(host, 10);

        // Spellbook, Modern (ADR-0024, gump 6): a stand-in Magery book in the pack.
        {
            profile.ModernGumpsOff = false;
            Game.GameObjects.Item pack = world.Player.FindItemByLayer(Game.Data.Layer.Backpack);
            Game.GameObjects.Item book = world.GetOrCreateItem(0x7FFFFF00);
            book.Graphic = 0x0EFA;
            book.Container = pack.Serial;
            pack.PushToBack(book);

            for (int i = 1; i <= 24; i++)
            {
                Game.GameObjects.Item spell = world.GetOrCreateItem(0x7FFFFF00 + (uint)i);
                spell.Amount = (ushort)i;
                spell.Container = book.Serial;
                book.PushToBack(spell);
            }

            UIManager.Add(new SpellbookGump(world, book.Serial));
            await InputProbe.Wait(host, 40);
            await Save(host, "spellbook");
            Input.Touch.Modern.ModernGump.Current?.Close();
            world.RemoveItem(book.Serial, true);
            profile.ModernGumpsOff = true;
            await InputProbe.Wait(host, 10);
        }

        // Party, Modern (ADR-0024, gump 2).
        profile.ModernGumpsOff = false;
        UIManager.Add(new PartyGump(world, 100, 100, world.Party.CanLoot));
        await InputProbe.Wait(host, 30);
        await Save(host, "party");
        Input.Touch.Modern.ModernGump.Current?.Close();
        profile.ModernGumpsOff = true;
        await InputProbe.Wait(host, 10);

        // The abilities book, Modern (ADR-0024, gump 6).
        profile.ModernGumpsOff = false;
        Game.GameActions.OpenAbilitiesBook(world);
        await InputProbe.Wait(host, 40);
        await Save(host, "abilities");
        Input.Touch.Modern.ModernGump.Current?.Close();
        profile.ModernGumpsOff = true;
        await InputProbe.Wait(host, 10);

        // The journal's Modern reader (ADR-0024: a reader, not a replacement).
        Input.Touch.Modern.ModernJournal.OpenReader(world);
        await InputProbe.Wait(host, 40);
        await Save(host, "journal");
        Input.Touch.Modern.ModernGump.Current?.Close();
        await InputProbe.Wait(host, 10);

        // The world map, Classic and fitted below the bar (ADR-0024: Classic + fit
        // + gestures), then its markers manager, Modern (gump index 9).
        UIManager.GetGump<WorldMapGump>()?.Dispose();
        UIManager.GetGump<PaperDollGump>(world.Player.Serial)?.Dispose();
        StatusGumpBase.GetStatusGump()?.Dispose();
        await InputProbe.Wait(host, 5);
        Game.GameActions.OpenWorldMap(world);

        // The map image is built from the client data on first open, which on
        // a handheld takes a while: wait for it (probe only, so by reflection).
        System.Reflection.FieldInfo mapTexture = typeof(WorldMapGump).GetField("_mapTexture",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        for (int i = 0; i < 120 && !(mapTexture?.GetValue(null) is GodotObject t && GodotObject.IsInstanceValid(t)); i++)
        {
            await InputProbe.Wait(host, 30);
        }

        await InputProbe.Wait(host, 60);
        await Save(host, "worldmap");
        profile.ModernGumpsOff = false;
        UIManager.Add(new MarkersManagerGump(world));
        await InputProbe.Wait(host, 40);
        await Save(host, "markers");
        Input.Touch.Modern.ModernGump.Current?.Close();
        profile.ModernGumpsOff = true;
        UIManager.GetGump<WorldMapGump>()?.Dispose();
        await InputProbe.Wait(host, 10);

        await MeasureGumps(host, world);

        Passed = true;
        GD.Print("[GUO] gallery: ok");
    }

    private static int _heldSlot = -1;

    /// <summary>
    /// Hold a finger on a bar button (Attack Last) until its popup opens and
    /// slide onto the first alternate; the picture is taken with the finger
    /// still down. The finger then lets go off the popup: nothing runs.
    /// </summary>
    private static async System.Threading.Tasks.Task<bool> HoldPopup(Node host, TouchGumpBar bar)
    {
        Rect2 attack = bar.ButtonRect("attack");

        if (attack.Size == Vector2.Zero)
        {
            return false;
        }

        Vector2 at = attack.GetCenter();
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = at, Pressed = true });
        ulong until = Godot.Time.GetTicksMsec() + TouchInput.BarPopupMs + 150;

        while (Godot.Time.GetTicksMsec() < until)
        {
            await InputProbe.Wait(host, 1);
        }

        _heldSlot = bar.PopupSlot;
        Vector2 alt = bar.PopupRect(1).GetCenter();
        Godot.Input.ParseInputEvent(new InputEventScreenDrag { Index = 0, Position = alt, Relative = alt - at });
        await InputProbe.Wait(host, 5);
        bool open = bar.PopupSlot >= 0;

        if (open)
        {
            await Save(host, "popup");
        }

        Vector2 away = new(at.X, 40);
        Godot.Input.ParseInputEvent(new InputEventScreenDrag { Index = 0, Position = away, Relative = away - alt });
        await InputProbe.Wait(host, 2);
        Godot.Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Position = away, Pressed = false });
        await InputProbe.Wait(host, 10);

        return false; // pictured already, with the finger down
    }

    /// <summary>Open the slot editor for that slot, on its alternates' picker.</summary>
    private static async System.Threading.Tasks.Task<bool> OpenEditor(Node host)
    {
        BarEditor.Open(_heldSlot >= 0 ? _heldSlot : 7);
        await InputProbe.Wait(host, 20);

        if (!BarEditor.IsOpen)
        {
            return false;
        }

        await Save(host, "editor");
        BarEditor.Close();
        await InputProbe.Wait(host, 5);

        return false;
    }

    /// <summary>
    /// The size of every gump the client can open by itself, and of the
    /// shard's bank box and a vendor's buy list (asked for by speech, as a
    /// player does, near the dev shard's NPCs): "[GUO] gump size: Type WxH".
    /// For docs/ui/tall_gumps.md. Sizes are the gump's own, unscaled.
    /// </summary>
    private static async System.Threading.Tasks.Task MeasureGumps(Node host, Game.World world)
    {
        var seen = new System.Collections.Generic.HashSet<Gump>();

        async System.Threading.Tasks.Task Report(string what, System.Action open, int frames = 60)
        {
            foreach (Gump g in UIManager.Gumps) seen.Add(g);
            open();
            await InputProbe.Wait(host, frames);

            foreach (Gump g in UIManager.Gumps)
            {
                if (!seen.Contains(g) && !g.IsDisposed)
                {
                    GD.Print($"[GUO] gump size: {what}: {g.GetType().Name} {g.Width}x{g.Height}");
                    seen.Add(g);
                }
            }
        }

        Configuration.Profile p = Configuration.ProfileManager.CurrentProfile;
        bool standard = p.StandardSkillsGump;

        await Report("Options", () => Game.GameActions.OpenSettings(world));
        UIManager.GetGump<OptionsGump>()?.Dispose();
        await Report("Paperdoll", () => Game.GameActions.OpenPaperdoll(world, world.Player.Serial));
        await Report("Status", () => Game.GameActions.OpenStatusBar(world));
        await Report("Journal", () => Game.GameActions.OpenJournal(world));
        p.StandardSkillsGump = true;
        await Report("Skills (standard)", () => Game.GameActions.OpenSkills(world));
        UIManager.GetGump<StandardSkillsGump>()?.Dispose();
        p.StandardSkillsGump = false;
        await Report("Skills (advanced)", () => Game.GameActions.OpenSkills(world));
        UIManager.GetGump<SkillGumpAdvanced>()?.Dispose();
        p.StandardSkillsGump = standard;
        await Report("Radar map", () => Game.GameActions.OpenMiniMap(world));
        await Report("World map", () => Game.GameActions.OpenWorldMap(world), 120);
        await Report("Backpack", () => Game.GameActions.OpenBackpack(world));
        await Report("Abilities book", () => Game.GameActions.OpenAbilitiesBook(world));
        await Report("Macro button editor", () => Game.GameActions.OpenMacroGump(world, "probe"));
        await Report("Bank (said 'bank')", () => Game.GameActions.Say("bank"), 120);
        await Report("Vendor (said 'vendor buy')", () => Game.GameActions.Say("vendor buy"), 120);
        await Report("Guild", () => Game.GameActions.OpenGuildGump(world), 90);
        await Report("Party", () => UIManager.Add(new PartyGump(world, 100, 100, world.Party.CanLoot)));
    }

    private static async System.Threading.Tasks.Task Save(Node host, string name)
    {
        await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        Image frame = host.GetViewport().GetTexture().GetImage();
        string path = _dir.PathJoin($"{_tag}_{name}.png");
        frame.SavePng(path);
        GD.Print($"[GUO] gallery: {name} -> {ProjectSettings.GlobalizePath(path)}");

        if (DualScreen.Active)
        {
            await InputProbe.Wait(host, 3);
            string second = _dir.PathJoin($"{_tag}_{name}_second.png");

            if (DualScreen.SaveFrame(second))
            {
                GD.Print($"[GUO] gallery: {name} (second screen) -> {ProjectSettings.GlobalizePath(second)}");
            }
        }
    }
}
