// GUO addition, not a port: photographs of the button glyphs (ADR-0025).

using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input;

namespace GUO.Host;

/// <summary>
/// --glyph-shots: in the world, the window menu open on the backpack over
/// the command bar, photographed once per input: an Xbox pad, a PlayStation
/// pad and the keyboard and mouse. The pads are stand-ins
/// (<see cref="InputMode.StandIn"/>), so no pad is needed. The prompts, the
/// window menu's tooltips and the command bar tab's badge follow each one.
/// Run it with --touch for the command bar, and --screenshot-dir for where
/// the photos go (glyphs_xbox.png, glyphs_playstation.png, glyphs_keyboard.png).
/// </summary>
internal static class GlyphShots
{
    public static async System.Threading.Tasks.Task<bool> Run(Node host, string dir)
    {
        Game.World world = null;

        for (ulong until = Godot.Time.GetTicksMsec() + 120000; Godot.Time.GetTicksMsec() < until; )
        {
            world = Client.Game?.UO?.World;

            if (world?.InGame == true && world.Player != null)
            {
                break;
            }

            await InputProbe.Wait(host, 10);
        }

        if (world?.InGame != true)
        {
            GD.PrintErr("[GUO] glyph shots: never got into the world -- is the shard running, and --autologin given?");
            return false;
        }

        // The gumps a login restores settle over its first seconds.
        await Seconds(host, 4);

        // Only what the photos are about: the backpack, its window menu, the command bar.
        UIManager.GetGump<PaperDollGump>()?.Dispose();
        UIManager.GetGump<JournalGump>()?.Dispose();
        UIManager.GetGump<StatusGumpBase>()?.Dispose();
        UIManager.GetGump<WorldMapGump>()?.Dispose();

        if (UIManager.GetGump<ContainerGump>(world.Player.FindItemByLayer(Game.Data.Layer.Backpack)?.Serial ?? 0) == null)
        {
            Game.GameActions.OpenBackpack(world);
            await Seconds(host, 2);
        }

        Gump pack = UIManager.GetGump<ContainerGump>(world.Player.FindItemByLayer(Game.Data.Layer.Backpack)?.Serial ?? 0);

        if (pack == null)
        {
            GD.PrintErr("[GUO] glyph shots: no backpack to open the window menu on");
            return false;
        }

        pack.X = 200;
        pack.Y = 120;
        Input.Touch.WindowMenu.Open(pack);
        await Seconds(host, 1);

        string to = string.IsNullOrWhiteSpace(dir) ? "user://screenshots" : dir;
        System.IO.Directory.CreateDirectory(ProjectSettings.GlobalizePath(to));
        bool ok = true;

        foreach ((string name, System.Action use) in new (string, System.Action)[]
        {
            ("xbox", () => InputMode.StandIn(PadFamily.Xbox, "Xbox Series X Controller")),
            ("playstation", () => InputMode.StandIn(PadFamily.PlayStation, "DualSense Wireless Controller")),
            ("keyboard", () => InputMode.Switch(InputKind.KeyboardMouse)),
        })
        {
            use();

            if (!Input.Touch.WindowMenu.IsOpen)
            {
                Input.Touch.WindowMenu.Open(pack);
            }

            // A pad's pointer hides after 4 s idle, as it does in play.
            await Seconds(host, name == "keyboard" ? 1 : 5);
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            string file = System.IO.Path.Combine(ProjectSettings.GlobalizePath(to), $"glyphs_{name}.png");
            Error saved = host.GetViewport().GetTexture().GetImage().SavePng(file);
            GD.Print($"[GUO] glyph shots: {name} -> {file} ({saved}); tab badge {Input.Touch.TouchGumpBar.BadgeDrawn ?? "none"}, "
                + $"confirm {(Input.Glyphs.InputGlyphs.LastShown.TryGetValue(PadAction.Confirm, out string c) ? c : null) ?? "none"}");
            ok &= saved == Error.Ok;
        }

        return ok;
    }

    private static async System.Threading.Tasks.Task Seconds(Node host, double s)
    {
        for (ulong until = Godot.Time.GetTicksMsec() + (ulong)(s * 1000); Godot.Time.GetTicksMsec() < until; )
        {
            await InputProbe.Wait(host, 1);
        }
    }
}
