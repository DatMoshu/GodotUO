// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned, shared desktop/touch/controller surface (ADR-uoplus-settings).
using System.Collections.Generic;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Renderer.Water;

namespace GUO.Game.UI.Gumps;

internal sealed class UoPlusOptionsGump : Gump
{
    private readonly Profile _profile;
    private readonly Dictionary<UoPlusFeature, (Checkbox Toggle, HSliderBar Density)> _rows = new();
    private readonly HSliderBar _cap;
    private bool _defaultsStaged;
    private readonly Combobox _water;
    private readonly string[] _waterIds;
    private readonly Checkbox _shardWater;
    private readonly bool _shardAcceptedAtOpen;
#if GUO_LABS
    private readonly StbTextBox _tiles;
    private readonly Checkbox _cobblestone;
    private readonly Label _error;
#endif

    internal static void Open(World world)
    {
        UIManager.GetGump<UoPlusOptionsGump>()?.Dispose();
        UIManager.Add(new UoPlusOptionsGump(world, ProfileManager.CurrentProfile));
    }

    internal UoPlusOptionsGump(World world, Profile profile) : base(world, 0, 0)
    {
        _profile = profile ?? new Profile();
        Width = 580;
        Height = System.Math.Min(480, System.Math.Max(320, Client.Game.ClientBounds.Height - 16));
        X = System.Math.Max(0, (Client.Game.ClientBounds.Width - Width) / 2);
        Y = System.Math.Max(0, (Client.Game.ClientBounds.Height - Height) / 2);
        CanMove = true; CanCloseWithEsc = CanCloseWithRightClick = true;
        AcceptMouseInput = true; WantUpdateSize = false;
        Add(new AlphaBlendControl(0.95f) { Width = Width, Height = Height });
        Add(new Label("UO+ effects", true, 0xFFFF, font: 1) { X = 18, Y = 12 });
        Add(new Label("Optional visual effects. All start off. Density 0 hides an effect.", true, 0xFFFF, font: 1) { X = 18, Y = 38 });
        var area = new ScrollArea(16, 66, 548, Height - 130, true);
        Add(area);
        int y = 0;
        area.Add(new Label("Water style", true, 0xFFFF, font: 1) { X = 4, Y = y });
        var waterChoices = WaterRender.PlayerChoiceLists(_profile);
        _waterIds = waterChoices.Ids;
        _water = new Combobox(150, y, 350, waterChoices.Names, WaterRender.PlayerChoiceIndex(_profile), font: 1);
        area.Add(_water);
        y += 34;
        if (WaterRender.ShardPolicy != null)
        {
            _shardAcceptedAtOpen = WaterRender.ShardAccepted(_profile);
            var shardDescription = new Label(WaterRender.ShardDescription, true, 0x0386, 490, 1) { X = 4, Y = y };
            area.Add(shardDescription);
            y += shardDescription.Height + 12;
            _shardWater = new Checkbox(0xD2, 0xD3, "Use this shard's water settings (consent)", 1, 0xFFFF)
                { X = 4, Y = y, IsChecked = _shardAcceptedAtOpen };
            area.Add(_shardWater);
            // A newly accepted policy starts at its default. Reopen to choose
            // another permitted style; no staged edit changes the live profile.
            _shardWater.ValueChanged += (_, _) => _water.IsEnabled = _shardWater.IsChecked == _shardAcceptedAtOpen;
            y += 34;
        }
        foreach (var feature in UoPlusOptions.Features)
        {
            bool available = feature.Available;
            var toggle = new Checkbox(0xD2, 0xD3, feature.Name, 1, 0xFFFF)
            {
                X = 4, Y = y, IsChecked = available && UoPlusOptions.Requested(_profile, feature.Id),
                IsEnabled = available
            };
            area.Add(toggle);
            area.Add(new Label(feature.Hint, true, available ? (ushort)0x0386 : (ushort)0x0021, 490, 1)
                { X = 4, Y = y + 24 });
            area.Add(new Label("Density (%)", true, 0xFFFF, font: 1) { X = 4, Y = y + 46 });
            var density = new HSliderBar(128, y + 46, 330, 0, 100, UoPlusOptions.Density(_profile, feature.Id),
                HSliderBarStyle.MetalWidgetRecessedBar, true, 1, 0xFFFF) { IsEnabled = available };
            area.Add(density);
            _rows[feature.Id] = (toggle, density);
            y += 80;
        }
        area.Add(new Label("Ambient particles per frame (shared cap)", true, 0xFFFF, font: 1) { X = 4, Y = y });
        _cap = new HSliderBar(4, y + 26, 430, 0, 512, System.Math.Clamp(_profile.AmbientParticleCap, 0, 512),
            HSliderBarStyle.MetalWidgetRecessedBar, true, 1, 0xFFFF);
        area.Add(_cap);
#if GUO_LABS
        y += 66;
        var settings = Labs.Weather.WeatherLab.For(world).Settings;
        _cobblestone = new Checkbox(0xD2, 0xD3, "Puddles: include ground named cobblestone", 1, 0xFFFF)
            { X = 4, Y = y, IsChecked = settings.Cobblestone };
        area.Add(_cobblestone);
        area.Add(new Label("Puddle ground IDs (comma separated, decimal or 0x hex)", true, 0xFFFF, font: 1) { X = 4, Y = y + 28 });
        area.Add(new AlphaBlendControl(0.5f) { X = 4, Y = y + 52, Width = 490, Height = 26 });
        _tiles = new StbTextBox(1, 1024, 490, true, hue: 0xFFFF) { X = 4, Y = y + 52, Width = 490, Height = 26 };
        _tiles.SetText(string.Join(",", settings.PuddleTiles));
        area.Add(_tiles);
        _error = new Label("", true, 0x0021, 490, 1) { X = 4, Y = y + 82 };
        area.Add(_error);
#endif
        AddButton(1, 18, "Cancel"); AddButton(2, 154, "Apply");
        AddButton(3, 290, "Defaults"); AddButton(4, 426, "Okay");
    }

    private void AddButton(int id, int x, string text) => Add(new NiceButton(x, Height - 42, 118, 28,
        ButtonAction.Activate, text, font: 1) { ButtonParameter = id, IsSelectable = false });

    internal bool ApplyChanges(bool save = true)
    {
#if GUO_LABS
        if (!Labs.Weather.WeatherLab.TryTiles(_tiles.Text, out var tiles))
        {
            _error.Text = "Use ground IDs from 0 to 16383; no changes applied.";
            return false;
        }
        var settings = Labs.Weather.WeatherLab.For(World).Settings;
        settings.PuddleTiles.Clear();
        foreach (ushort id in tiles) settings.PuddleTiles.Add(id);
        settings.Cobblestone = _cobblestone.IsChecked;
        _error.Text = "";
#endif
        if (_defaultsStaged) UoPlusOptions.ResetDefaults(_profile);
        bool acceptShard = _shardWater?.IsChecked == true && !_defaultsStaged;
        WaterRender.AcceptShard(_profile, acceptShard);
        if (acceptShard == _shardAcceptedAtOpen)
            WaterRender.SetPlayerChoice(_profile, _waterIds[System.Math.Clamp(_water.SelectedIndex, 0, _waterIds.Length - 1)]);
        if (_defaultsStaged) { _profile.WaterStyle = WaterRender.Classic; _profile.ShardWaterStyle = ""; }
        foreach (var (id, row) in _rows) UoPlusOptions.Set(_profile, id, row.Toggle.IsChecked, row.Density.Value);
        _profile.AmbientParticleCap = _cap.Value;
        _defaultsStaged = false;
        if (save && World.InGame) _profile.Save(World, ProfileManager.ProfilePath);
        return true;
    }

    internal void RestoreDefaults()
    {
        _defaultsStaged = true;
        foreach (var row in _rows.Values) { row.Toggle.IsChecked = false; row.Density.Value = 50; }
        _cap.Value = 128;
        if (_shardWater != null) _shardWater.IsChecked = false;
        _water.SelectedIndex = 0;
#if GUO_LABS
        _cobblestone.IsChecked = true; _tiles.SetText(""); _error.Text = "";
#endif
    }

    public override void OnButtonClick(int buttonID)
    {
        switch (buttonID)
        {
            case 1: Dispose(); break;
            case 2: ApplyChanges(); break;
            case 3: RestoreDefaults(); break;
            case 4: if (ApplyChanges()) Dispose(); break;
        }
    }
}
