// SPDX-License-Identifier: BSD-2-Clause
// Headless flow test for the GUO options integration (task 1136): Classic Options
// Apply vs the focused GUO panels, section scope, construction, summaries, Modern rows.
// Uses scratch Profile objects only; ProfileManager's current profile and path are
// pointed at a temp folder for the run and restored. No network, nothing of the
// owner's profile is touched.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Renderer.Lights;
using GUO.Renderer.SpeedMode;
using GUO.Renderer.Water;
using Control = GUO.Game.UI.Controls.Control;
using Label = GUO.Game.UI.Controls.Label;

namespace GUO.Game.UI.Gumps;

public partial class GuoOptionsFlowProbe : Node
{
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private int _failures;
    private int _checks;
    private readonly List<string> _notes = new();

    public override async void _Ready()
    {
        bool ok = false;
        try { ok = await RunAll(); }
        catch (Exception e) { GD.PrintErr("[guo-options-flow] FAIL: unhandled " + e); }
        GetTree().Quit(ok ? 0 : 1);
    }

    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failures++;
        GD.PrintErr("[guo-options-flow] FAIL: " + what);
    }

    private static T F<T>(object o, string name)
    {
        var f = o.GetType().GetField(name, Inst);
        if (f == null) throw new InvalidOperationException("no field " + name + " on " + o.GetType().Name);
        return (T)f.GetValue(o);
    }

    private static void Invoke(object o, string name)
    {
        var m = o.GetType().GetMethod(name, Inst);
        if (m == null) throw new InvalidOperationException("no method " + name);
        try { m.Invoke(o, null); }
        catch (TargetInvocationException e) { throw e.InnerException ?? e; }
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control
    {
        foreach (var c in root.Children)
        {
            if (c is T t) yield return t;
            foreach (var d in All<T>(c)) yield return d;
        }
    }

    private static OptionsGump NewOptions(World world)
    {
        // OptionsGump reads ProfileManager.GlobalProfile at construction; give it a scratch one only for
        // that moment so ProfileManager.Save never writes a global profile file.
        var prop = typeof(ProfileManager).GetProperty("GlobalProfile");
        var saved = prop.GetValue(null);
        prop.SetValue(null, new GlobalProfile());
        try { return new OptionsGump(world); }
        finally { prop.SetValue(null, saved); }
    }

    private static Checkbox Box(Control root, string text) => All<Checkbox>(root).FirstOrDefault(c => c.Text == text);
    private static bool Has(Control root, string text) => Box(root, text) != null;

    private static IEnumerable<Checkbox> PageBoxes(OptionsGump g, int page) =>
        g.Children.OfType<ScrollArea>().Where(a => a.Page == page).SelectMany(a => All<Checkbox>(a));

    // ---- section snapshots: every profile field a section owns, as one string ----
    private static string Feat(Profile p, UoPlusFeature mask) =>
        string.Join(",", UoPlusOptions.Features.Where(f => (f.Id & mask) != 0)
            .Select(f => $"{f.Id}:{UoPlusOptions.Requested(p, f.Id)}/{UoPlusOptions.Density(p, f.Id)}"));

    private static string LightingSnap(Profile p) =>
        $"speed={p.LightingSpeedMode} rt={p.RealtimeLights} night={p.SpeedModeNightFloor} soften={p.SpeedModeSoften} lamp={p.SpeedModeLampShadows} sun={p.SpeedModeSunShadows} win={p.SpeedModeWindowLight} spellLegacy={p.SpellLighting} [{Feat(p, GuoOptions.LightingFeatures)}]";

    private static string WeatherSnap(Profile p) =>
        $"rain={p.SpeedModeWeather} refl={p.SpeedModeReflections} fog={p.SpeedModeFog} haze={p.SpeedModeMorningHaze} wind={p.WindStrength:0.00} cap={p.AmbientParticleCap} dens={p.AmbientParticleDensity} legacy={p.AmbientFog}/{p.AmbientLeaves}/{p.AmbientDust} [{Feat(p, GuoOptions.WeatherFeatures)}]";

    private static string WorldSnap(Profile p) =>
        $"water={p.WaterStyle}/{p.ShardWaterStyle} overview={p.ExperimentalWorldOverview}/{p.WorldOverviewStyle} touch={p.TouchUi} [{Feat(p, GuoOptions.WorldFeatures)}]";

    private static Profile NonDefault()
    {
        var p = new Profile();
        p.LightingSpeedMode = true; p.RealtimeLights = true;
        p.SpeedModeNightFloor = 41; p.SpeedModeSoften = 7; p.SpeedModeLampShadows = 2;
        p.SpeedModeSunShadows = false; p.SpeedModeWindowLight = false;
        UoPlusOptions.Set(p, UoPlusFeature.SpellLights, true, 77);
        p.SpeedModeWeather = false; p.SpeedModeReflections = false; p.SpeedModeFog = false;
        p.SpeedModeMorningHaze = 33; p.WindStrength = 0.5f; p.AmbientParticleCap = 200;
        UoPlusOptions.Set(p, UoPlusFeature.Fog, true, 66);
        UoPlusOptions.Set(p, UoPlusFeature.Leaves, true, 44);
        var ids = WaterRender.PlayerChoiceLists(p).Ids;
        if (ids.Length > 1) WaterRender.SetPlayerChoice(p, ids[1]);
        p.ExperimentalWorldOverview = true; p.WorldOverviewStyle = 2; p.TouchUi = true;
        return p;
    }

    private async System.Threading.Tasks.Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void SetCurrent(Profile p, string path)
    {
        typeof(ProfileManager).GetProperty("CurrentProfile").SetValue(null, p);
        typeof(ProfileManager).GetProperty("ProfilePath").SetValue(null, path);
    }

    private async System.Threading.Tasks.Task<bool> RunAll()
    {
        long t0 = System.Environment.TickCount64;
        while (Client.Game?.UO?.Gumps == null && System.Environment.TickCount64 - t0 < 180000) await Frames(1);
        Check(Client.Game?.UO?.Gumps != null, "client initialised");
        if (Client.Game?.UO?.Gumps == null) return false;
        await Frames(60);
        var world = Client.Game.UO.World;

        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guo-options-flow-" + System.Environment.ProcessId);
        System.IO.Directory.CreateDirectory(tmp);
        var savedProfile = ProfileManager.CurrentProfile;
        var savedPath = ProfileManager.ProfilePath;
        bool savedProbe = SpeedModeDebug.Probe;
        var applied = new List<GuoSection>();
        Action<GuoSection> onApplied = s => applied.Add(s);
        GuoOptions.Applied += onApplied;
        try
        {
            await Step("stale overwrite (lighting + water)", () => StaleOverwrite(world, tmp));
            await Step("reverse order", () => ReverseOrder(world, tmp));
            await Step("section scope", () => SectionScope(world, tmp));
            await Step("section construction", () => Construction(world, tmp));
            await Step("summary + Applied events", () => Summary(world, tmp, applied));
            await Step("classic pages", () => ClassicPages(world, tmp));
            await Step("modern options", () => Modern(world, tmp));
            await Step("effect policy", EffectPolicy);
        }
        finally
        {
            GuoOptions.Applied -= onApplied;
            SpeedModeDebug.Probe = savedProbe;
            SetCurrent(savedProfile, savedPath);
            try { System.IO.Directory.Delete(tmp, true); } catch { }
        }

        foreach (var n in _notes) GD.Print("[guo-options-flow] note: " + n);
        if (_failures == 0) GD.Print($"[guo-options-flow] PASS: {_checks} checks (stale-overwrite, reverse order, section scope, construction, summary, classic pages, modern, effect policy)");
        else GD.PrintErr($"[guo-options-flow] FAIL: {_failures} of {_checks} checks failed");
        return _failures == 0;
    }

    private async System.Threading.Tasks.Task Step(string name, Action body)
    {
        try { body(); }
        catch (Exception e) { Check(false, name + " threw " + e); }
        await Frames(2);
    }

    private static UoPlusOptionsGump Panel(World w, Profile p, GuoSection s) => new UoPlusOptionsGump(w, p, s);

    // 1
    private void StaleOverwrite(World world, string tmp)
    {
        var p = new Profile();
        SetCurrent(p, tmp);
        var options = NewOptions(world);
        try
        {
            var lighting = Panel(world, p, GuoSection.Lighting);
            Box(lighting, "Lighting speed mode (GPU per-pixel lights)").IsChecked = true;
            Box(lighting, "Classic realtime lights (CPU)").IsChecked = true;
            F<HSliderBar>(lighting, "_speedNightFloor").Value = 47;
            F<HSliderBar>(lighting, "_speedSoften").Value = 8;
            F<Combobox>(lighting, "_speedLampShadows").SelectedIndex = 3;
            F<Checkbox>(lighting, "_speedSunShadows").IsChecked = false;
            Check(lighting.ApplyChanges(save: false), "lighting panel applies");
            string before = LightingSnap(p);
            Check(p.LightingSpeedMode && p.RealtimeLights && p.SpeedModeNightFloor == 47 && p.SpeedModeSoften == 8 && p.SpeedModeLampShadows == 3 && !p.SpeedModeSunShadows,
                "panel wrote lighting values: " + before);
            try { Invoke(options, "Apply"); }
            catch (Exception e) { Check(false, "OptionsGump.Apply threw (cannot test stale overwrite): " + e); return; }
            Check(LightingSnap(p) == before, $"lighting survives Options Apply: before [{before}] after [{LightingSnap(p)}]");
            lighting.Dispose();

            var choices = WaterRender.PlayerChoiceLists(p);
            if (choices.Ids.Length > 1)
            {
                var wp = Panel(world, p, GuoSection.World);
                F<Combobox>(wp, "_water").SelectedIndex = 1;
                Check(wp.ApplyChanges(save: false), "world panel applies");
                string water = p.WaterStyle;
                Check(WaterRender.PlayerChoiceIndex(p) == 1, "world panel set water index 1, got style " + water);
                Invoke(options, "Apply");
                Check(p.WaterStyle == water && WaterRender.PlayerChoiceIndex(p) == 1, $"water survives Options Apply ({water} -> {p.WaterStyle})");
                wp.Dispose();
            }
            else _notes.Add("water: only one player choice available; water stale-overwrite not exercised");
        }
        finally { options.Dispose(); }
    }

    // 2
    private void ReverseOrder(World world, string tmp)
    {
        var p = new Profile();
        SetCurrent(p, tmp);
        var options = NewOptions(world);
        try
        {
            bool grid0 = p.GridContainers;
            var gridBox = F<Checkbox>(options, "_gridContainers");
            var slot = F<HSliderBar>(options, "_gridSlotSize");
            gridBox.IsChecked = !grid0;
            int newSlot = slot.Value >= slot.MaxValue - 4 ? slot.Value - 4 : slot.Value + 4;
            slot.Value = newSlot;
            var lighting = Panel(world, p, GuoSection.Lighting);
            Box(lighting, "Lighting speed mode (GPU per-pixel lights)").IsChecked = true;
            F<HSliderBar>(lighting, "_speedNightFloor").Value = 29;
            Box(lighting, "Classic realtime lights (CPU)").IsChecked = true;
            lighting.ApplyChanges(save: false);
            string snap = LightingSnap(p);
            try { Invoke(options, "Apply"); }
            catch (Exception e) { Check(false, "OptionsGump.Apply threw: " + e); return; }
            Check(p.GridContainers == !grid0 && p.GridContainerSlotSize == newSlot,
                $"grid edit applied (grid {p.GridContainers}, slot {p.GridContainerSlotSize}, wanted {newSlot})");
            Check(LightingSnap(p) == snap, $"lighting survives after grid edit: [{snap}] vs [{LightingSnap(p)}]");
            lighting.Dispose();
        }
        finally { options.Dispose(); }
    }

    // 3
    private void SectionScope(World world, string tmp)
    {
        SetCurrent(new Profile(), tmp);
        foreach (var which in new[] { GuoSection.Lighting, GuoSection.Weather, GuoSection.World })
        {
            var p = NonDefault();
            string l = LightingSnap(p), w = WeatherSnap(p), o = WorldSnap(p);
            var panel = Panel(world, p, which);
            panel.RestoreDefaults();
            Check(LightingSnap(p) == l && WeatherSnap(p) == w && WorldSnap(p) == o, which + " Defaults stages only (profile unchanged before Apply)");
            Check(panel.ApplyChanges(save: false), which + " defaults apply");
            string l2 = LightingSnap(p), w2 = WeatherSnap(p), o2 = WorldSnap(p);
            Check((l2 == l) == (which != GuoSection.Lighting), $"{which} defaults: lighting {(which == GuoSection.Lighting ? "reset" : "untouched")}: [{l}] -> [{l2}]");
            Check((w2 == w) == (which != GuoSection.Weather), $"{which} defaults: weather {(which == GuoSection.Weather ? "reset" : "untouched")}: [{w}] -> [{w2}]");
            Check((o2 == o) == (which != GuoSection.World), $"{which} defaults: world {(which == GuoSection.World ? "reset" : "untouched")}: [{o}] -> [{o2}]");
            panel.Dispose();

            // Cancel writes nothing
            var q = NonDefault();
            string all = LightingSnap(q) + "|" + WeatherSnap(q) + "|" + WorldSnap(q);
            var cp = Panel(world, q, which);
            switch (which)
            {
                case GuoSection.Lighting: F<HSliderBar>(cp, "_speedNightFloor").Value = 5; Box(cp, "Classic realtime lights (CPU)").IsChecked = false; break;
                case GuoSection.Weather: F<HSliderBar>(cp, "_speedHaze").Value = 5; F<Checkbox>(cp, "_speedWeather").IsChecked = true; break;
                case GuoSection.World: F<Checkbox>(cp, "_overview").IsChecked = false; F<Combobox>(cp, "_touchUi").SelectedIndex = 2; break;
            }
            cp.RestoreDefaults();
            cp.OnButtonClick(1);
            Check(cp.IsDisposed, which + " Cancel closes");
            Check(all == LightingSnap(q) + "|" + WeatherSnap(q) + "|" + WorldSnap(q), which + " Cancel writes nothing");
        }
    }

    // 4
    private void Construction(World world, string tmp)
    {
        var p = new Profile();
        SetCurrent(p, tmp);
        const string Speed = "Lighting speed mode (GPU per-pixel lights)", Real = "Classic realtime lights (CPU)", Fog = "Fog banks", WFog = "Weather fog";
        var l = Panel(world, p, GuoSection.Lighting);
        Check(Has(l, Real) && Has(l, Speed) && !Has(l, Fog) && !Has(l, WFog), "Lighting: realtime+speed, no fog");
        Check(F<Combobox>(l, "_water") == null && F<Checkbox>(l, "_overview") == null, "Lighting: no water/overview");
        Check(F<HSliderBar>(l, "_speedNightFloor") != null && F<HSliderBar>(l, "_cap") == null, "Lighting: night floor, no ambient cap");
        var w = Panel(world, p, GuoSection.Weather);
        Check(Has(w, Fog) && Has(w, WFog) && !Has(w, Speed) && !Has(w, Real), "Weather: fog, no speed/realtime");
        Check(F<Combobox>(w, "_water") == null && F<Combobox>(w, "_speedLampShadows") == null, "Weather: no water/lamp shadows");
        var o = Panel(world, p, GuoSection.World);
        Check(F<Combobox>(o, "_water") != null && F<Checkbox>(o, "_overview") != null && !Has(o, Fog) && !Has(o, Speed) && !Has(o, Real), "World: water+overview only");
        var a = Panel(world, p, GuoSection.All);
        Check(Has(a, Speed) && Has(a, Real) && Has(a, Fog) && Has(a, WFog) && F<Combobox>(a, "_water") != null && F<Checkbox>(a, "_overview") != null, "All: everything");
        l.Dispose(); w.Dispose(); o.Dispose(); a.Dispose();
    }

    // 5
    private void Summary(World world, string tmp, List<GuoSection> applied)
    {
        var p = new Profile();
        SetCurrent(p, tmp);
        var options = NewOptions(world);
        try
        {
            var labels = F<Dictionary<GuoSection, Label>>(options, "_guoSummaries");
            Check(labels.Count == 3, "three summary labels");
            string before = labels[GuoSection.Lighting].Text;
            Check(before == "Classic lighting", "initial lighting summary: " + before);
            applied.Clear();
            var panel = Panel(world, p, GuoSection.Lighting);
            Box(panel, "Lighting speed mode (GPU per-pixel lights)").IsChecked = true;
            panel.ApplyChanges(save: false);
            Check(applied.Count == 1 && applied[0] == GuoSection.Lighting, $"Applied fired once for Lighting (got {string.Join(",", applied)})");
            string after = labels[GuoSection.Lighting].Text;
            Check(after != before, $"summary changed: [{before}] -> [{after}]");
            if (SpeedModeDebug.Supported) Check(after.StartsWith("Enhanced lighting on"), "supported text: " + after);
            else { Check(after.StartsWith("Enhanced lighting unavailable"), "unavailable text: " + after); _notes.Add("SpeedModeDebug.Supported false headless: asserted unavailable text"); }
            Check(labels[GuoSection.Weather].Text == GuoOptions.Summary(p, GuoSection.Weather), "weather summary refreshed with profile");
            applied.Clear();
            var wp = Panel(world, p, GuoSection.Weather); wp.ApplyChanges(save: false);
            var op = Panel(world, p, GuoSection.World); op.RestoreDefaults(); op.ApplyChanges(save: false);
            Check(applied.Count == 2 && applied[0] == GuoSection.Weather && applied[1] == GuoSection.World, $"Applied sections Weather,World (got {string.Join(",", applied)})");
            applied.Clear();
            panel.OnButtonClick(4); // Okay = Apply + close
            Check(applied.Count == 1 && panel.IsDisposed, "Okay applies once and closes");
            applied.Clear();
            var cancel = Panel(world, p, GuoSection.Lighting); cancel.OnButtonClick(1);
            Check(applied.Count == 0, "Cancel raises no Applied");
            wp.Dispose(); op.Dispose();
        }
        finally { options.Dispose(); }
        var stale = NewOptions(world);
        var staleLabels = F<Dictionary<GuoSection, Label>>(stale, "_guoSummaries");
        string frozen = staleLabels[GuoSection.World].Text;
        stale.Dispose();
        p.ExperimentalWorldOverview = !p.ExperimentalWorldOverview;
        var q = Panel(world, p, GuoSection.World); q.ApplyChanges(save: false);
        Check(staleLabels[GuoSection.World].Text == frozen, "disposed OptionsGump no longer receives Applied");
        q.Dispose();
    }

    // 6
    private void ClassicPages(World world, string tmp)
    {
        SetCurrent(new Profile(), tmp);
        var options = NewOptions(world);
        try
        {
            var video = PageBoxes(options, 3).Select(c => c.Text).ToList();
            Check(video.Count > 0, "video page has checkboxes (" + video.Count + ")");
            Check(!video.Any(t => t.IndexOf("Realtime lights", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("speed mode", StringComparison.OrdinalIgnoreCase) >= 0),
                "Video page has no Realtime lights / speed mode checkbox");
            var anywhere = All<Checkbox>(options).Select(c => c.Text).Where(t => t != null &&
                (t.IndexOf("Realtime lights", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("speed mode", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            Check(anywhere.Count == 0, "no such checkbox on any Options page: " + string.Join(";", anywhere));
            var guo = options.Children.OfType<ScrollArea>().Where(a => a.Page == 13).ToList();
            Check(guo.Count == 1, "page 13 exists");
            var texts = guo.SelectMany(a => All<NiceButton>(a)).Select(b => b.TextLabel.Text).ToList();
            foreach (var t in new[] { "Lighting and shadows...", "Weather and ambience...", "World map and water..." })
                Check(texts.Contains(t), "page 13 button: " + t + " (have " + string.Join("|", texts) + ")");
            foreach (var f in new[] { "_gridContainers", "_gridSlotSize", "_enhancedHD" })
                Check(F<Control>(options, f) != null, "relocated control built: " + f);
        }
        finally { options.Dispose(); }
    }

    // 7
    private void Modern(World world, string tmp)
    {
        SetCurrent(new Profile(), tmp);
        GUO.Input.Touch.Modern.ModernOptions view = null;
        try { view = new GUO.Input.Touch.Modern.ModernOptions(world); }
        catch (Exception e) { _notes.Add("ModernOptions could not be constructed headless: " + e.GetType().Name + ": " + e.Message); Check(false, "ModernOptions construct: " + e); return; }
        try
        {
            var list = (System.Collections.IList)typeof(GUO.Input.Touch.Modern.ModernOptions).GetField("_settings", Inst).GetValue(view);
            var rows = new List<(string Page, string Label, string Kind)>();
            foreach (var s in list)
            {
                var t = s.GetType();
                rows.Add(((string)t.GetField("Page").GetValue(s), (string)t.GetField("Label").GetValue(s), t.GetField("Kind").GetValue(s).ToString()));
            }
            foreach (var l in new[] { "Lighting and shadows...", "Weather and ambience...", "World map and water..." })
                Check(rows.Any(r => r.Page == "GUO" && r.Label == l && r.Kind == "Action"), "Modern GUO Action row: " + l);
            Check(!rows.Any(r => r.Label.StartsWith("Realtime lights") || r.Label.StartsWith("UO+ effects")), "no Realtime lights / UO+ effects rows remain");
            Check(!rows.Any(r => r.Label == "Water"), "no Water row on Modern (moved into World panel)");
            _notes.Add("ModernOptions constructed headless; rows read via reflection (" + rows.Count + " rows, GUO page " + rows.Count(r => r.Page == "GUO") + ")");
        }
        finally { view?.QueueFree(); }
    }

    // 8
    private void EffectPolicy()
    {
        var p = new Profile { RealtimeLights = false, LightingSpeedMode = true };
        UoPlusOptions.Set(p, UoPlusFeature.SpellLights, true, 100);
        SpeedModeDebug.Probe = true;
        Check(SpellLighting.Enabled(p), "speed-only profile + speed mode active: spell lights enabled");
        SpeedModeDebug.Probe = false;
        Check(!SpellLighting.Enabled(p), "speed-only profile + speed mode inactive: spell lights off");
        p.RealtimeLights = true;
        Check(SpellLighting.Enabled(p), "realtime-only: spell lights enabled");
    }
}
