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
    private Checkbox _speedMode; // PORT DEVIATION (GUO): ADR-0048
    private HSliderBar _speedNightFloor,_speedSoften;
    private readonly Dictionary<UoPlusFeature, (Checkbox Toggle, HSliderBar Density)> _rows = new();
    private readonly HSliderBar _cap;
    private readonly Checkbox _overview;
    private readonly Combobox _overviewStyle;
    private readonly Combobox _touchUi;
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
        Width = System.Math.Clamp(Client.Game.ClientBounds.Width - 16, 320, 580);
        Height = System.Math.Min(480, System.Math.Max(320, Client.Game.ClientBounds.Height - 16));
        X = System.Math.Max(0, (Client.Game.ClientBounds.Width - Width) / 2);
        Y = System.Math.Max(0, (Client.Game.ClientBounds.Height - Height) / 2);
        CanMove = true; CanCloseWithEsc = CanCloseWithRightClick = true;
        AcceptMouseInput = true; WantUpdateSize = false;
        Add(new AlphaBlendControl(0.95f) { Width = Width, Height = Height });
        Add(new Label("UO+", true, 0xFFFF, font: 1) { X = 18, Y = 12 });
        Add(new Label("Reopen inventory after applying Touch UI.", true, 0xFFFF, Width - 36, font: 1) { X = 18, Y = 38 });
        var area = new ScrollArea(16, 66, Width - 32, Height - 130, true);
        Add(area);
        area.Add(new Label("Touch UI", true, 0xFFFF, font: 1) { X = 4, Y = 0 });
        _touchUi = new Combobox(100, 0, Width - 154, new[] { "Automatic (touch pointer)", "On", "Off" },
            _profile.TouchUi.HasValue ? (_profile.TouchUi.Value ? 1 : 2) : 0);
        area.Add(_touchUi);
        int y = 50;
        // PORT DEVIATION (GUO): ADR-0048; same profile field as Classic Options.
        _speedMode = new Checkbox(0xD2, 0xD3, "Lighting speed mode (GPU per-pixel lights)", 1, 0xFFFF)
            { X=4, Y=y, IsChecked=_profile.LightingSpeedMode, IsEnabled=Renderer.SpeedMode.SpeedModeDebug.Supported };
        area.Add(_speedMode); y += 30;
        if (!Renderer.SpeedMode.SpeedModeDebug.Supported) {
            area.Add(new Label(Renderer.SpeedMode.SpeedModeDebug.UnavailableReason, true, 0x0386, Width-70, 1) { X=4, Y=y }); y += 40;
        }
        area.Add(new Label("Night floor (%)",true,0xFFFF,font:1) { X=4,Y=y });
        _speedNightFloor=new HSliderBar(4,y+22,Width-110,0,100,System.Math.Clamp(_profile.SpeedModeNightFloor,0,100),
            HSliderBarStyle.MetalWidgetRecessedBar,true,1,0xFFFF);
        area.Add(_speedNightFloor); y+=54;
        area.Add(new Label("Soften (approximate), steps",true,0xFFFF,font:1) { X=4,Y=y });
        _speedSoften=new HSliderBar(4,y+22,Width-110,0,10,System.Math.Clamp(_profile.SpeedModeSoften,0,10),
            HSliderBarStyle.MetalWidgetRecessedBar,true,1,0xFFFF);
        area.Add(_speedSoften); y+=54;
        void SpeedControls() { _speedNightFloor.IsEnabled=_speedSoften.IsEnabled=_speedMode.IsEnabled && _speedMode.IsChecked; }
        _speedMode.ValueChanged+=(_,_)=>SpeedControls(); SpeedControls();
        _overview = new Checkbox(0xD2, 0xD3, "Experimental zoom + world map", 1, 0xFFFF)
            { X = 4, Y = y, IsChecked = _profile.ExperimentalWorldOverview };
        area.Add(_overview);
        y += 30;
        area.Add(new Label("Ctrl + wheel: native to 5x, then cached map.", true, 0x0386, Width - 70, 1) { X = 4, Y = y });
        y += 30;
        _overviewStyle = new Combobox(4, y, Width - 100,
            new[] { "Natural map", "Parchment", "Relief edges", "Moonlight" },
            System.Math.Clamp(_profile.WorldOverviewStyle, 0, 3), font: 1)
            { IsEnabled = _overview.IsChecked };
        _overview.ValueChanged += (_, _) => _overviewStyle.IsEnabled = _overview.IsChecked;
        area.Add(_overviewStyle);
        y += 42;
        area.Add(new Label("Water style", true, 0xFFFF, font: 1) { X = 4, Y = y });
        var waterChoices = WaterRender.PlayerChoiceLists(_profile);
        _waterIds = waterChoices.Ids;
        _water = new Combobox(150, y, Width - 230, waterChoices.Names, WaterRender.PlayerChoiceIndex(_profile), font: 1);
        area.Add(_water);
        y += 34;
        if (WaterRender.ShardPolicy != null)
        {
            _shardAcceptedAtOpen = WaterRender.ShardAccepted(_profile);
            var shardDescription = new Label(WaterRender.ShardDescription, true, 0x0386, Width - 90, 1) { X = 4, Y = y };
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
            area.Add(new Label(feature.Hint, true, available ? (ushort)0x0386 : (ushort)0x0021, Width - 70, 1)
                { X = 4, Y = y + 24 });
            area.Add(new Label("Density (%)", true, 0xFFFF, font: 1) { X = 4, Y = y + 46 });
            var density = new HSliderBar(128, y + 46, Width - 210, 0, 100, UoPlusOptions.Density(_profile, feature.Id),
                HSliderBarStyle.MetalWidgetRecessedBar, true, 1, 0xFFFF) { IsEnabled = available };
            area.Add(density);
            _rows[feature.Id] = (toggle, density);
            y += 80;
        }
        area.Add(new Label("Ambient particles per frame (shared cap)", true, 0xFFFF, font: 1) { X = 4, Y = y });
        _cap = new HSliderBar(4, y + 26, Width - 110, 0, 512, System.Math.Clamp(_profile.AmbientParticleCap, 0, 512),
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

    private void AddButton(int id, int x, string text) => Add(new NiceButton(18 + (id - 1) * ((Width - 36) / 4), Height - 50, (Width - 36) / 4 - 6, 44,
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
        _profile.LightingSpeedMode = _speedMode.IsChecked;
        _profile.SpeedModeNightFloor=_speedNightFloor.Value;
        _profile.SpeedModeSoften=_speedSoften.Value;
        _profile.ExperimentalWorldOverview = _overview.IsChecked;
        _profile.WorldOverviewStyle = System.Math.Clamp(_overviewStyle.SelectedIndex, 0, 3);
        _profile.TouchUi = _touchUi.SelectedIndex == 0 ? null : _touchUi.SelectedIndex == 1;
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
        _speedMode.IsChecked = false;
        _speedNightFloor.Value=_speedSoften.Value=3;
        _speedNightFloor.IsEnabled=_speedSoften.IsEnabled=false;
        _touchUi.SelectedIndex = 0;
        _overview.IsChecked = false;
        _overviewStyle.SelectedIndex = 0;
        _overviewStyle.IsEnabled = false;
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
