// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Linq;
using System.Text.Json;
using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Game.UI.Gumps;
using GUO.Renderer.Particles;
using GUO.Renderer.Lights;
using GUO.Renderer.Water;

namespace GUO.Configuration;

/// <summary>Shared UO+ persistence, off-state and native Options checks.</summary>
public partial class UoPlusProbe : Node
{
    public override void _Ready() => GetTree().Quit(Run() ? 0 : 1);

    internal static bool Run()
    {
        try
        {
            var p = JsonSerializer.Deserialize("{}", ProfileJsonContext.DefaultToUse.Profile);
            foreach (var feature in UoPlusOptions.Features)
                Check(!UoPlusOptions.Enabled(p, feature.Id), feature.Name + " defaults off");
            Check(!AmbientParticles.Enabled(p) && !WaterRender.RipplesEnabled(p), "off paths skip work");
            var legacyWater = JsonSerializer.Deserialize("{\"water_style\":\"uo\"}", ProfileJsonContext.DefaultToUse.Profile);
            Check(WaterRender.RipplesEnabled(legacyWater), "legacy styled water preserves ripples");
            var explicitOff = JsonSerializer.Deserialize("{\"water_style\":\"uo\",\"uo_plus_flags\":0}", ProfileJsonContext.DefaultToUse.Profile);
            Check(!WaterRender.RipplesEnabled(explicitOff), "explicit saved off survives migration");
            var newWater = new Profile { WaterStyle = "uo" };
            Check(!WaterRender.RipplesEnabled(newWater), "new styled water starts off");
            UoPlusOptions.DisableAll(legacyWater);
            var migrated = JsonSerializer.Deserialize(JsonSerializer.Serialize(legacyWater, ProfileJsonContext.DefaultToUse.Profile), ProfileJsonContext.DefaultToUse.Profile);
            Check(!WaterRender.RipplesEnabled(migrated), "disabled migrated profile stays disabled after reload");
            p = JsonSerializer.Deserialize("{\"ambient_fog\":true,\"ambient_particle_density\":31,\"spell_lighting\":true}", ProfileJsonContext.DefaultToUse.Profile);
            Check(UoPlusOptions.Enabled(p, UoPlusFeature.Fog) && UoPlusOptions.Density(p, UoPlusFeature.Fog) == 31,
                "legacy particle fields remain readable");
            Check(UoPlusOptions.Requested(p, UoPlusFeature.SpellLights), "legacy spell checkbox remains authoritative");
            int index = 0;
            foreach (var feature in UoPlusOptions.Features)
                UoPlusOptions.Set(p, feature.Id, true, ++index * 10);
            var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(p, ProfileJsonContext.DefaultToUse.Profile),
                ProfileJsonContext.DefaultToUse.Profile);
            index = 0;
            foreach (var feature in UoPlusOptions.Features)
            {
                index++;
                Check(UoPlusOptions.Enabled(copy, feature.Id) == feature.Available, "availability and saved flags");
                if (feature.Available) Check(UoPlusOptions.Density(copy, feature.Id) == index * 10, "independent densities persist");
            }
            UoPlusOptions.Set(copy, UoPlusFeature.Fog, true, -9);
            UoPlusOptions.Set(copy, UoPlusFeature.Leaves, true, 999);
            Check(!UoPlusOptions.Enabled(copy, UoPlusFeature.Fog) && UoPlusOptions.Requested(copy, UoPlusFeature.Fog), "zero hides without losing checkbox");
            Check(UoPlusOptions.Density(copy, UoPlusFeature.Leaves) == 100, "density clamps");
            copy.WaterStyle = "uo";
            Check(WaterRender.RipplesEnabled(copy), "non-Classic water plus flag enables ripples");
            copy.WaterStyle = WaterRender.Classic;
            Check(!WaterRender.RipplesEnabled(copy), "Classic never simulates ripples");
#if GUO_LABS
            var weather = new Labs.Weather.WeatherSettings();
            Labs.Weather.WeatherLab.SyncProfile(weather, copy);
            Check(weather.WaterDrops && weather.Puddles && weather.WetPlayer && weather.WaterDensity == 40 && weather.PuddleDensity == 50 && weather.WetDensity == 60,
                "Labs reads shared flags and individual densities");
#else
            copy.UoPlusFlags |= UoPlusFeature.RainRings | UoPlusFeature.Sway;
            Check(!UoPlusOptions.Enabled(copy, UoPlusFeature.RainRings) && !UoPlusOptions.Enabled(copy, UoPlusFeature.Sway), "unavailable renderers stay off even in edited profiles");
#endif
            UoPlusOptions.DisableAll(copy);
            Check(UoPlusOptions.Flags(copy) == UoPlusFeature.None && !AmbientParticles.Enabled(copy), "disable all restores off state");
            RealtimeLightEffects.Clear();
            copy.UoPlusFlags = UoPlusFeature.Sway | UoPlusFeature.RainRings;
            copy.AmbientParticleDensity = 13;
            UoPlusOptions.ResetDefaults(copy);
            Check(UoPlusOptions.Flags(copy) == UoPlusFeature.None && UoPlusOptions.Density(copy, UoPlusFeature.Fog) == 50,
                "Defaults also clears flags unavailable in this build");
            RealtimeLightEffects.Add(100u, 1, 1, 0, 3, Colors.White, 1000);
            var lights = new RealtimeLightField.Source[64];
            copy.RealtimeLights = true;
            UoPlusOptions.Set(copy, UoPlusFeature.SpellLights, true, 100);
            for (int i = 0; i < 32; i++) SpellLighting.Emit(copy, 100, Game.Data.GraphicEffectType.Moving, 0x36D4, 2, 2, 0);
            Check(RealtimeLightEffects.Count == 17, "full density preserves spell cap plus generic hook");
            UoPlusOptions.Set(copy, UoPlusFeature.SpellLights, true, 50);
            RealtimeLightEffects.LimitSpells(SpellLighting.Budget(copy));
            Check(RealtimeLightEffects.Count == 9, "lower density immediately trims only spell bursts");
            SpellLighting.Emit(copy, 100, Game.Data.GraphicEffectType.Moving, 0x36D4, 2, 2, 0);
            Check(RealtimeLightEffects.Count == 9 && SpellLighting.Budget(copy) == 8, "density lowers spell-only admission budget");
            UoPlusOptions.Set(copy, UoPlusFeature.SpellLights, true, 0);
            Check(SpellLighting.Emit(copy, 100, Game.Data.GraphicEffectType.Moving, 0x36D4, 2, 2, 0) == 0, "zero density suppresses spell producer");
            Check(RealtimeLightEffects.Collect(200, lights, 0, includeSpells: SpellLighting.Enabled(copy)) == 1
                && lights[0].X == 1, "disable clears spells and preserves generic hooks");
            RealtimeLightEffects.Clear();
            GD.Print("[UO+ options] PASS: defaults, legacy profiles, independent persisted flags/densities, clamps, zero density, water prerequisites, build availability, spell density, disable all");
            return true;
        }
        catch (Exception e) { GD.PrintErr("[UO+ options] FAIL: " + e); return false; }
    }

    internal static async void RunUi(Node host)
    {
        UoPlusOptionsGump panel = null;
        try
        {
            Check(Run(), "contracts");
            // Wait for normal client initialization; never load or save a real player profile.
            for (int i = 0; i < 600 && Client.Game?.UO?.Gumps == null; i++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            for (int i = 0; i < 90; i++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            // Login uses its fixed window size; apply the requested proof size explicitly.
            Client.Game.SetPinnedWindowSize();
            for (int i = 0; i < 10; i++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            var p = new Profile { UoPlusFlags = UoPlusFeature.Sway };
            panel = new UoPlusOptionsGump(Client.Game.UO.World, p);
            UIManager.Add(panel);
            await FilmUi(host);
            var fog = panel.FindControls<ScrollArea>().Single().FindControls<Checkbox>().Single(c => c.Text == "Fog banks");
            fog.IsChecked = true;
            await FilmUi(host);
            Check(!p.AmbientFog, "staging does not mutate profile");
#if GUO_LABS
            var groundIds = panel.FindControls<ScrollArea>().Single().FindControls<StbTextBox>().Single();
            groundIds.SetText("0x4000");
            Check(!panel.ApplyChanges(save: false) && !p.AmbientFog, "invalid Labs ground rejects all staged changes");
            groundIds.SetText("");
#endif
            panel.ApplyChanges(save: false);
            Check(p.AmbientFog, "Apply commits staged flags");
            await FilmUi(host);
            panel.RestoreDefaults();
            await FilmUi(host);
            Check(p.AmbientFog && !fog.IsChecked, "Defaults stages until Apply");
            panel.ApplyChanges(save: false);
            Check(!p.AmbientFog && p.UoPlusFlags == UoPlusFeature.None, "Apply defaults switches all flags off");
            fog.IsChecked = true;
            panel.OnButtonClick(1);
            Check(!p.AmbientFog && panel.IsDisposed, "Cancel discards edits");
            await FilmUi(host);
            panel = new UoPlusOptionsGump(Client.Game.UO.World, p);
            UIManager.Add(panel);
            await FilmUi(host);
            for (int i = 0; i < 8; i++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            string folder = OS.GetEnvironment("GUO_UOPLUS_PROOF");
            Check(!string.IsNullOrEmpty(folder), "set GUO_UOPLUS_PROOF");
            string size = $"{host.GetViewport().GetVisibleRect().Size.X}x{host.GetViewport().GetVisibleRect().Size.Y}";
            using (var image = host.GetViewport().GetTexture().GetImage())
                Check(image.SavePng(System.IO.Path.Combine(folder, $"uoplus-options-{size}-top.png")) == Error.Ok, "save panel");
            var area = panel.FindControls<ScrollArea>().Single();
            GUO.Input.InputMode.Note(new InputEventJoypadButton { ButtonIndex = JoyButton.DpadDown, Pressed = true, Device = 0 });
            Check(GUO.Input.Gamepad.PadGumpNav.HandleButton(JoyButton.DpadDown, true), "controller focus enters panel");
            Check(GUO.Input.Gamepad.PadGumpNav.FocusCount > 4, "controller sees controls");
            for (int i = 0; i < 300; i++)
            {
                GUO.Input.Gamepad.PadGumpNav.HandleCommand(GUO.Input.Gamepad.PadCommand.NextHostile);
                if (i < 40 && i % 4 == 0) await FilmUi(host, 4);
            }
            await FilmUi(host);
            Check(area.ScrollValue == area.ScrollMaxValue && area.ScrollValue > 0, "controller scroll reaches final options");
            for (int i = 0; i < 4; i++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using (var image = host.GetViewport().GetTexture().GetImage())
                Check(image.SavePng(System.IO.Path.Combine(folder, $"uoplus-options-{size}-bottom.png")) == Error.Ok, "save final options");
            GD.Print("[UO+ options UI] PASS: staged edits, Apply, Defaults, Cancel, controller focus and scroll; screenshots captured");
            host.GetTree().Quit(0);
        }
        catch (Exception e) { GD.PrintErr("[UO+ options UI] FAIL: " + e); host.GetTree().Quit(1); }
        finally { panel?.Dispose(); }
    }

    private static int _videoFrame;
    private static async System.Threading.Tasks.Task FilmUi(Node host, int frames = 25)
    {
        string folder = OS.GetEnvironment("GUO_REVIEW_UI_FRAMES");
        if (string.IsNullOrWhiteSpace(folder)) return;
        System.IO.Directory.CreateDirectory(folder);
        for (int i = 0; i < frames; i++)
        {
            await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            using var image = host.GetViewport().GetTexture().GetImage();
            Check(image.SavePng(System.IO.Path.Combine(folder, $"{_videoFrame++:D4}.png")) == Error.Ok, "save UI video frame");
        }
    }

    private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
}
