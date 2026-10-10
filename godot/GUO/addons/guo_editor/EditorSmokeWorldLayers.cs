#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.Game.GameObjects;

/// <summary>
/// The World toolbar's Layers menu, all eight ticks (ED7): each one, clicked
/// off and on through the menu, hides and restores its layer in the World
/// view; the ticks survive a tab switch, a project reload and a rebuilt World
/// tab for the session; the button is drawn as a button. At map0 1496,1628,
/// with a multi, a spawner and two live markers placed for it.
/// </summary>
public partial class EditorSmoke
{
    private const int LayersX = 1496, LayersY = 1628;
    private const uint LayersHouse = 0x4000_0310;
    private static readonly Color LiveGreen = new(0.3f, 1f, 0.3f);
    private readonly Dictionary<string, object> _layersReport = new();

    private void LayersExpect(bool ok, string what)
    {
        _layersReport[what] = ok;
        Expect(ok, $"layers_{what}");
    }

    // What each layer puts in the World view, counted the way the view draws it.
    private int LandDrawn() => CentreObjects(o => o is Land && o.AllowedToDraw);

    private int StaticsDrawn() => CentreObjects(o => o is Static && o.AllowedToDraw);

    private int HouseDrawn() => _world.Host.World.HouseManager.TryGetHouse(LayersHouse, out var h) ? h.Components.Count(m => m.AllowedToDraw) : -1;

    // Roofs are not switched by AllowedToDraw: the game fades them out by
    // alpha (DrawRoofs off), as it does when the player walks indoors.
    private (int Roofs, int Shown) RoofsNearCentre()
    {
        int roofs = 0, shown = 0;
        for (int x = LayersX - 10; x <= LayersX + 10; x++)
        {
            for (int y = LayersY - 10; y <= LayersY + 10; y++)
            {
                var chunk = _world.Host.World.Map.GetChunk(x, y, load: true);
                for (var o = chunk?.GetHeadObject(x % 8, y % 8); o != null; o = o.TNext)
                {
                    // The placed house's roof is the multi's; Britain's are statics.
                    if ((o is Static s && s.ItemData.IsRoof) || (o is Multi m && m.ItemData.IsRoof))
                    {
                        roofs++;
                        shown += o.AlphaHue > 0 ? 1 : 0;
                    }
                }
            }
        }

        return (roofs, shown);
    }

    private int CentreObjects(Func<GameObject, bool> match)
    {
        var chunk = _world.Host.World.Map.GetChunk(LayersX, LayersY, load: true);
        int n = 0;
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                for (var o = chunk?.GetHeadObject(x, y); o != null; o = o.TNext)
                {
                    n += match(o) ? 1 : 0;
                }
            }
        }

        return n;
    }

    private bool LiveDrawn()
    {
        ModeContext ctx = _world.Modes.Context(false);
        var canvas = new ImagePaint(ctx.Geo.Width, ctx.Geo.Height);
        _world.Layers.Draw(canvas, ctx);
        return Painted(canvas, LiveGreen);
    }

    private IEnumerable<string> LiveLabels() => _world.Layers.Live.On ? _world.Layers.Items("Live", 0).Select(i => i.Label) : Enumerable.Empty<string>();

    private void AddWorldLayerSteps(int wait)
    {
        _editReport["layers_menu"] = _layersReport;
        // Roofs fade out and back in over frames (the game's own alpha), so they get longer.
        int fade = Math.Max(wait, 40);
        MapLayers layers = _world.Layers;
        Func<IEnumerable<LiveMobile>> liveBefore = null;
        var baseline = new Dictionary<string, int>();
        string[] eight = { "Land", "Statics", "Multis", "Roofs", "Objects", "Live", "Live players", "Live mobiles" };

        _steps.Add((1, () =>
        {
            LayersExpect(eight.All(n => _world.GetToggle(n) != null) && _world.ToggleNames.Count(n => eight.Contains(n)) == 8, "has_all_eight");
            // Live needs a shard, so it starts off; everything else starts on.
            var defaults = eight.ToDictionary(n => n, n => _world.GetToggle(n));
            _layersReport["defaults"] = defaults;
            LayersExpect(eight.All(n => defaults[n] == (n != "Live")), "defaults");

            // The button reads as a button: the Button variation's frame, not
            // the editor's flat MenuButton, and an arrow like a dropdown's.
            MenuButton button = _world.LayersMenu;
            StyleBox normal = button.GetThemeStylebox("normal");
            LayersExpect(button.ThemeTypeVariation == "Button" && normal is not StyleBoxEmpty && normal == button.GetThemeStylebox("normal", "Button")
                && button.Icon != null && button.IsVisibleInTree(), "button_looks_like_a_button");

            _world.GoTo(0, LayersX, LayersY);
            if (_world.Objects.Objects == null)
            {
                _world.Objects.Open(_world.Host.Project.Root);
            }

            _world.Host.PlaceServerMulti(LayersHouse, 0x0064, LayersX + 2, LayersY + 4, 0);
            _world.Objects.PlaceSpawner(0, LayersX - 2, LayersY - 1, _world.Modes.Data.LandZ(LayersX - 2, LayersY - 1), "Horse");
            liveBefore = layers.LiveSource;
            layers.LiveSource = () => new[]
            {
                new LiveMobile("Layers player", 0, LayersX + 1, LayersY + 1, 0, true),
                new LiveMobile("Layers horse", 0, LayersX - 2, LayersY + 2, 0, false),
            };
        }));

        _steps.Add((fade, () =>
        {
            baseline["land"] = LandDrawn();
            baseline["statics"] = StaticsDrawn();
            baseline["house"] = HouseDrawn();
            (baseline["roofs"], baseline["roofs_shown"]) = RoofsNearCentre();
            baseline["objects"] = _world.Objects.DrawnCount;
            _layersReport["baseline"] = new Dictionary<string, int>(baseline);
            LayersExpect(baseline["land"] > 0 && baseline["statics"] > 0 && baseline["house"] > 0 && baseline["objects"] > 0
                && baseline["roofs_shown"] > 0, "baseline_drawn");
            LayersExpect(!LiveDrawn(), "live_off_by_default");
        }));

        // Each tick off then on, through the menu as a click would.
        void OffOn(string name, int frames, Func<bool> hidden, Func<bool> restored)
        {
            bool start = _world.GetToggle(name) ?? false;
            _steps.Add((1, () => LayersExpect(_world.SetMenuItem(name, !start) && _world.GetToggle(name) == !start, $"{Slug(name)}_click")));
            _steps.Add((frames, () =>
            {
                LayersExpect(start ? hidden() : restored(), $"{Slug(name)}_{(start ? "hides" : "shows")}");
                _world.SetMenuItem(name, start);
            }));
            _steps.Add((frames, () => LayersExpect(start ? restored() : hidden(), $"{Slug(name)}_{(start ? "restores" : "hides_again")}")));
        }

        OffOn("Land", wait, () => LandDrawn() == 0 && StaticsDrawn() == baseline["statics"], () => LandDrawn() == baseline["land"]);
        OffOn("Statics", wait, () => StaticsDrawn() == 0 && LandDrawn() == baseline["land"], () => StaticsDrawn() == baseline["statics"]);
        OffOn("Multis", wait, () => HouseDrawn() == 0 && StaticsDrawn() == baseline["statics"], () => HouseDrawn() == baseline["house"]);
        OffOn("Roofs", fade,
            () =>
            {
                _layersReport["roofs_shown_when_off"] = RoofsNearCentre().Shown;
                return !_world.Host.ShowRoofs && RoofsNearCentre().Shown == 0 && StaticsDrawn() == baseline["statics"];
            },
            () =>
            {
                _layersReport["roofs_shown_when_on"] = RoofsNearCentre().Shown;
                return _world.Host.ShowRoofs && RoofsNearCentre().Shown == baseline["roofs_shown"];
            });
        OffOn("Objects", wait, () => _world.Objects.DrawnCount == 0, () => _world.Objects.DrawnCount == baseline["objects"]);
        OffOn("Live", 2, () => !LiveDrawn() && !_world.LayerOn("Live"), () => LiveDrawn() && _world.LayerOn("Live"));

        // The kind filters act on a Live layer that is on.
        _steps.Add((1, () => _world.SetMenuItem("Live", true)));
        OffOn("Live players", 2, () => LiveLabels().SequenceEqual(new[] { "Layers horse" }), () => LiveLabels().Count() == 2);
        OffOn("Live mobiles", 2, () => LiveLabels().SequenceEqual(new[] { "Layers player" }), () => LiveLabels().Count() == 2);

        // The ticks last for the session: a mix of non-defaults, then a tab
        // switch, a project reload, and a newly built World tab.
        var mix = new Dictionary<string, bool>
        {
            ["Land"] = true, ["Statics"] = false, ["Multis"] = true, ["Roofs"] = false,
            ["Objects"] = false, ["Live"] = true, ["Live players"] = true, ["Live mobiles"] = false,
        };
        bool Holds(string where)
        {
            var now = eight.ToDictionary(n => n, n => _world.GetToggle(n) ?? false);
            bool ok = mix.All(kv => now[kv.Key] == kv.Value)
                && !_world.Host.ShowStatics && !_world.Host.ShowRoofs && !_world.Objects.Visible
                && _world.LayerOn("Live") && layers.Live.Players && !layers.Live.Mobiles;
            _layersReport[$"ticks_after_{where}"] = now;
            return ok;
        }

        _steps.Add((1, () =>
        {
            foreach (var (n, on) in mix)
            {
                _world.SetMenuItem(n, on);
            }

            EditorInterface.Singleton.SetMainScreenEditor("Script");
        }));
        _steps.Add((wait, () =>
        {
            LayersExpect(!_world.Visible, "tab_switched_away");
            EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
        }));
        _steps.Add((wait, () =>
        {
            LayersExpect(_world.Visible && Holds("tab_switch"), "ticks_survive_tab_switch");
            _world.ReloadOverlay();
            _world.OpenProject(_world.Host.Project.Root);
        }));
        _steps.Add((fade, () =>
        {
            LayersExpect(Holds("project_reload") && StaticsDrawn() == 0 && _world.Objects.DrawnCount == 0, "ticks_survive_project_reload");

            // A rebuilt World tab (a plugin re-enable or an assembly reload makes one) starts with the session's ticks.
            var fresh = new WorldView(_data) { Visible = false };
            AddChild(fresh);
            var ticks = eight.ToDictionary(n => n, n => fresh.GetToggle(n) ?? false);
            _layersReport["ticks_in_new_tab"] = ticks;
            LayersExpect(mix.All(kv => ticks[kv.Key] == kv.Value), "ticks_survive_new_tab");
            fresh.Shutdown();
            RemoveChild(fresh);
            fresh.QueueFree();
        }));

        // Put everything back: the defaults, then the fixtures away.
        _steps.Add((1, () =>
        {
            foreach (string n in eight)
            {
                _world.SetMenuItem(n, n != "Live");
            }
        }));
        _steps.Add((fade, () =>
        {
            LayersExpect(eight.All(n => _world.GetToggle(n) == (n != "Live")) && _world.Host.ShowRoofs && StaticsDrawn() == baseline["statics"]
                && RoofsNearCentre().Shown == baseline["roofs_shown"] && _world.Objects.DrawnCount == baseline["objects"], "back_to_defaults");
            layers.LiveSource = liveBefore;
            _world.Host.RemoveServerObject(LayersHouse);
            foreach (ShardSpawner s in _world.Objects.Objects?.Spawners.ToList() ?? new List<ShardSpawner>())
            {
                _world.Objects.Delete(s.Id);
            }

            _layersReport["ok"] = _layersReport.Where(kv => kv.Value is bool).All(kv => (bool)kv.Value);
        }));
    }

    private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '_');
}
#endif
