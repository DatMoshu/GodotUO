// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (owner request 2026-11-21): one small window with every water
// setting, applied live while you look at the sea. Open it with the "-water"
// chat command or the "Water..." button on the Options GUO page. Writes the
// same profile fields Options and the GUO World panel write; nothing else.
using System;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Renderer.Water;

namespace GUO.Game.UI.Gumps;

internal sealed class WaterQuickGump : Gump
{
    private const int W = 360;
    private readonly Profile _profile;
    private readonly Combobox _style;
    private readonly string[] _ids;
    private readonly Checkbox _ripples, _shallow;
    private readonly HSliderBar _shallowStrength, _speed, _wave, _foam, _glint;
    private readonly Label _note;

    internal static void Toggle(World world)
    {
        var open = UIManager.GetGump<WaterQuickGump>();
        if (open != null) { open.Dispose(); return; }
        UIManager.Add(new WaterQuickGump(world));
    }

    private WaterQuickGump(World world) : base(world, 0, 0)
    {
        _profile = ProfileManager.CurrentProfile ?? new Profile();
        CanMove = true;
        CanCloseWithRightClick = CanCloseWithEsc = true;
        AcceptMouseInput = true;
        WantUpdateSize = false;
        Width = W;
        Height = 372;
        X = Math.Max(0, Client.Game.ClientBounds.Width - W - 20);
        Y = 60;

        Add(new AlphaBlendControl(0.92f) { Width = W, Height = Height });
        Add(new Label("Water", true, 0x0035, font: 1) { X = 14, Y = 10 });
        Add(new Label("Changes apply as you make them.", true, 0x0386, W - 28, font: 1) { X = 70, Y = 12 });

        int y = 40;
        Add(new Label("Style", true, 0xFFFF, font: 1) { X = 14, Y = y + 2 });
        var lists = WaterRender.PlayerChoiceLists(_profile);
        _ids = lists.Ids;
        _style = new Combobox(110, y, W - 130, lists.Names, WaterRender.PlayerChoiceIndex(_profile), font: 1);
        _style.OnOptionSelected += (_, i) =>
        {
            WaterRender.SetPlayerChoice(_profile, i >= 0 && i < _ids.Length ? _ids[i] : "classic");
            WaterRender.Changed(World);
            Refresh();
        };
        Add(_style);
        y += 34;

        _ripples = new Checkbox(0xD2, 0xD3, "Ripples and wakes", 1, 0xFFFF)
            { X = 14, Y = y, IsChecked = UoPlusOptions.Enabled(_profile, UoPlusFeature.WaterRipples) };
        _ripples.ValueChanged += (_, _) => SetFeature(UoPlusFeature.WaterRipples, _ripples.IsChecked);
        Add(_ripples);
        y += 28;

        _shallow = new Checkbox(0xD2, 0xD3, "Shallow shores and caustics", 1, 0xFFFF)
            { X = 14, Y = y, IsChecked = UoPlusOptions.Enabled(_profile, UoPlusFeature.ShallowShores) };
        _shallow.ValueChanged += (_, _) => SetFeature(UoPlusFeature.ShallowShores, _shallow.IsChecked);
        Add(_shallow);
        y += 26;
        _shallowStrength = Slider("Shore strength", ref y, 0, 100, UoPlusOptions.Density(_profile, UoPlusFeature.ShallowShores),
            v => UoPlusOptions.Set(_profile, UoPlusFeature.ShallowShores, _shallow.IsChecked, v));
        y += 6;

        Add(new Label("Tuning (percent of the style's own look)", true, 0x0386, W - 28, font: 1) { X = 14, Y = y });
        y += 24;
        _speed = Slider("Wave speed", ref y, 0, 300, _profile.WaterTuneSpeed, v => _profile.WaterTuneSpeed = v);
        _wave = Slider("Wave height", ref y, 0, 300, _profile.WaterTuneWave, v => _profile.WaterTuneWave = v);
        _foam = Slider("Surf and foam", ref y, 0, 300, _profile.WaterTuneFoam, v => _profile.WaterTuneFoam = v);
        _glint = Slider("Sun glints", ref y, 0, 300, _profile.WaterTuneGlint, v => _profile.WaterTuneGlint = v);

        _note = new Label("", true, 0x0021, W - 28, font: 1) { X = 14, Y = y + 2 };
        Add(_note);

        var reset = new NiceButton(14, Height - 34, 150, 24, ButtonAction.Activate, "Reset tuning") { ButtonParameter = 1 };
        reset.MouseUp += (_, _) =>
        {
            _speed.Value = _wave.Value = _foam.Value = _glint.Value = 100;
        };
        Add(reset);
        var close = new NiceButton(W - 114, Height - 34, 100, 24, ButtonAction.Activate, "Close") { ButtonParameter = 2 };
        close.MouseUp += (_, _) => Dispose();
        Add(close);

        Refresh();
    }

    private HSliderBar Slider(string text, ref int y, int min, int max, int value, Action<int> write)
    {
        Add(new Label(text, true, 0xFFFF, font: 1) { X = 30, Y = y });
        var s = new HSliderBar(150, y + 2, W - 210, min, max, Math.Clamp(value, min, max),
            HSliderBarStyle.MetalWidgetRecessedBar, true, 1, 0xFFFF);
        s.ValueChanged += (_, _) => write(s.Value);
        Add(s);
        y += 26;
        return s;
    }

    private void SetFeature(UoPlusFeature id, bool on)
    {
        int density = id == UoPlusFeature.ShallowShores && _shallowStrength != null
            ? _shallowStrength.Value : UoPlusOptions.Density(_profile, id);
        UoPlusOptions.Set(_profile, id, on, density);
        Refresh();
    }

    // Everything but Style needs a styled (non-Classic) water; say so instead of hiding it.
    private void Refresh()
    {
        bool styled = WaterRender.IsStyled(_profile);
        _ripples.IsEnabled = _shallow.IsEnabled = styled;
        foreach (var s in new[] { _shallowStrength, _speed, _wave, _foam, _glint }) if (s != null) s.IsEnabled = styled;
        if (_shallowStrength != null) _shallowStrength.IsEnabled = styled && _shallow.IsChecked;
        _note.Text = styled ? "" : "Pick a style other than Classic tiles to use the rest.";
    }

    public override void OnButtonClick(int buttonID) { }

    public override void Dispose()
    {
        // Keep what was set across sessions, as Options' Okay does.
        try { if (!IsDisposed && World?.InGame == true) ProfileManager.CurrentProfile?.Save(World, ProfileManager.ProfilePath); }
        catch (Exception e) { Utility.Logging.Log.Warn("[water panel] save failed: " + e.Message); }
        base.Dispose();
    }
}
