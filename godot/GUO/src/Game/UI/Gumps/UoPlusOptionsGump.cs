// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned, shared desktop/touch/controller surface (ADR-uoplus-settings).
// Task 1136: one gump class, focused by GuoSection. Each section builds, applies
// and resets only its own controls; GuoSection.All is every section (legacy UO+).
using System;
using System.Collections.Generic;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Renderer.Water;

namespace GUO.Game.UI.Gumps;

internal sealed class UoPlusOptionsGump : Gump
{
    private readonly Profile _profile;
    private readonly GuoSection _section;
    private readonly bool _lighting, _weather, _worldSection;
    private Checkbox _speedMode; // PORT DEVIATION (GUO): ADR-0048
    private HSliderBar _speedNightFloor,_speedSoften;
    private Checkbox _realtimeLights;
#if GUO_LABS
    private Checkbox _localClock;
    private HSliderBar _localClockHour;
#endif
    // PORT DEVIATION (GUO): ADR-0048; staged shadow/window children.
    private Combobox _speedLampShadows;
    private Checkbox _speedSunShadows,_speedWindowLight;
    // PORT DEVIATION (GUO): ADR-0048 weather sub-options.
    private Checkbox _speedWeather,_speedReflections,_speedFog;
    private HSliderBar _speedHaze,_speedWind;
    private readonly Dictionary<UoPlusFeature, (Checkbox Toggle, HSliderBar Density)> _rows = new();
    private HSliderBar _cap;
    private Checkbox _overview;
    private Combobox _overviewStyle;
    private Combobox _touchUi;
    private bool _defaultsStaged;
    private Combobox _water;
    private string[] _waterIds;
    private Checkbox _shardWater;
    private bool _shardAcceptedAtOpen;
    private Action _refreshDependents = () => { };
#if GUO_LABS
    private StbTextBox _tiles;
    private Checkbox _cobblestone;
    private Label _error;
#endif

    internal static void Open(World world) => Open(world, GuoSection.All);

    internal static void Open(World world, GuoSection section)
    {
        UIManager.GetGump<UoPlusOptionsGump>()?.Dispose();
        UIManager.Add(new UoPlusOptionsGump(world, ProfileManager.CurrentProfile, section));
    }

    private static string TitleOf(GuoSection section) => section switch
    {
        GuoSection.Lighting => "GUO: Lighting and shadows",
        GuoSection.Weather => "GUO: Weather and ambience",
        GuoSection.World => "GUO: World map and water",
        _ => "GUO",
    };

    internal UoPlusOptionsGump(World world, Profile profile, GuoSection section = GuoSection.All) : base(world, 0, 0)
    {
        _profile = profile ?? new Profile();
        _section = section;
        _lighting = section is GuoSection.All or GuoSection.Lighting;
        _weather = section is GuoSection.All or GuoSection.Weather;
        _worldSection = section is GuoSection.All or GuoSection.World;
        Width = System.Math.Clamp(Client.Game.ClientBounds.Width - 16, 320, 580);
        Height = System.Math.Min(480, System.Math.Max(320, Client.Game.ClientBounds.Height - 16));
        X = System.Math.Max(0, (Client.Game.ClientBounds.Width - Width) / 2);
        Y = System.Math.Max(0, (Client.Game.ClientBounds.Height - Height) / 2);
        CanMove = true; CanCloseWithEsc = CanCloseWithRightClick = true;
        AcceptMouseInput = true; WantUpdateSize = false;
        Add(new AlphaBlendControl(0.95f) { Width = Width, Height = Height });
        Add(new Label(TitleOf(section), true, 0xFFFF, font: 1) { X = 18, Y = 12 });
        if (_worldSection)
            Add(new Label("Reopen inventory after applying Touch UI.", true, 0xFFFF, Width - 36, font: 1) { X = 18, Y = 38 });
        var area = new ScrollArea(16, 66, Width - 32, Height - 130, true);
        Add(area);
        int y = 0;
        if (_lighting) BuildLighting(area, ref y);
        if (_weather) BuildWeather(area, ref y);
        if (_worldSection) BuildWorld(area, ref y);
        _refreshDependents();
        AddButton(1, 18, "Cancel"); AddButton(2, 154, "Apply");
        AddButton(3, 290, "Defaults"); AddButton(4, 426, "Okay");
    }

    // Weather-only panels follow the saved parent; panels that own the parent follow its staged state.
    private bool EnhancedStaged => _speedMode != null
        ? _speedMode.IsEnabled && _speedMode.IsChecked
        : Renderer.SpeedMode.SpeedModeDebug.Supported && _profile.LightingSpeedMode;

    private void AddFeatureRows(ScrollArea area, ref int y, UoPlusFeature mask)
    {
        foreach (var feature in UoPlusOptions.Features)
        {
            if ((feature.Id & mask) == 0) continue;
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
    }

    private void BuildLighting(ScrollArea area, ref int y)
    {
        // PORT DEVIATION (GUO): ADR-0048; same profile field as Classic Options.
        _speedMode = new Checkbox(0xD2, 0xD3, "Lighting speed mode (GPU per-pixel lights)", 1, 0xFFFF)
            { X=4, Y=y, IsChecked=_profile.LightingSpeedMode, IsEnabled=Renderer.SpeedMode.SpeedModeDebug.Supported };
        area.Add(_speedMode); y += 30;
#if GUO_LABS
        _localClock=new Checkbox(0xD2,0xD3,"Local test time (ignore shard clock)",1,0xFFFF)
            { X=4,Y=y,IsChecked=_profile.LocalLightingTestTime };
        area.Add(_localClock); y+=30;
        var clockLabel=new Label($"Local lighting time: {_profile.LocalLightingTestHour:00}:00",true,0xFFFF,font:1) { X=4,Y=y };
        area.Add(clockLabel);
        _localClockHour=new HSliderBar(4,y+22,Width-110,0,23,_profile.LocalLightingTestHour,HSliderBarStyle.MetalWidgetRecessedBar,true,1,0xFFFF);
        _localClockHour.ValueChanged+=(_,_)=>clockLabel.Text=$"Local lighting time: {_localClockHour.Value:00}:00";
        area.Add(_localClockHour);y+=54;
        area.Add(new Label("Apply freezes this hour; Live clock adds scrubbing and cycling.",true,0x0386,Width-70,1) { X=4,Y=y });y+=40;
        area.Add(new NiceButton(4,y,Width-100,32,ButtonAction.Activate,"Open live clock (applies settings)",font:1)
            { ButtonParameter=5,IsSelectable=false });y+=42;
#endif
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
        // PORT DEVIATION (GUO): ADR-0048; preserve existing night/soften controls.
        area.Add(new Label("Lamp shadows (lights)",true,0xFFFF,font:1) { X=4,Y=y }); y+=24;
        _speedLampShadows=new Combobox(4,y,Width-100,
            new[] { "0","1","2","3","4","5","6","7","8","9","10" },_profile.SpeedModeLampShadows,font:1);
        area.Add(_speedLampShadows); y+=42;
        _speedSunShadows=new Checkbox(0xD2,0xD3,"Sun and moon shadows",1,0xFFFF)
            { X=4,Y=y,IsChecked=_profile.SpeedModeSunShadows };
        area.Add(_speedSunShadows); y+=30;
        _speedWindowLight=new Checkbox(0xD2,0xD3,"Window light by day",1,0xFFFF)
            { X=4,Y=y,IsChecked=_profile.SpeedModeWindowLight };
        area.Add(_speedWindowLight); y+=30;
        // Classic shadows stay a native UO option; this is only their effective status.
        string shadows = GuoEffectPolicy.ClassicShadowsStatus(_profile);
        if (!string.IsNullOrEmpty(shadows))
        {
            var status = new Label(shadows, true, 0x0386, Width - 70, 1) { X = 4, Y = y };
            area.Add(status); y += status.Height + 12;
        }
        // PORT DEVIATION (GUO): moved here from Classic/Modern Options; same Profile.RealtimeLights.
        _realtimeLights = new Checkbox(0xD2, 0xD3, "Classic realtime lights (CPU)", 1, 0xFFFF)
            { X = 4, Y = y, IsChecked = _profile.RealtimeLights };
        area.Add(_realtimeLights);
        area.Add(new Label("Used only when enhanced lighting is off or unavailable.", true, 0x0386, Width - 70, 1) { X = 4, Y = y + 24 });
        y += 54;
        AddFeatureRows(area, ref y, GuoOptions.LightingFeatures);
        void SpeedControls() {
            bool enabled=_speedMode.IsEnabled && _speedMode.IsChecked;
            _speedNightFloor.IsEnabled=_speedSoften.IsEnabled=enabled;
#if GUO_LABS
            _localClock.IsEnabled=enabled;
            _localClockHour.IsEnabled=enabled&&_localClock.IsChecked;
#endif
            _speedLampShadows.IsEnabled=_speedSunShadows.IsEnabled=_speedWindowLight.IsEnabled=enabled;
        }
#if GUO_LABS
        _localClock.ValueChanged+=(_,_)=>_refreshDependents();
#endif
        _speedMode.ValueChanged+=(_,_)=>_refreshDependents();
        var previous = _refreshDependents;
        _refreshDependents = () => { previous(); SpeedControls(); };
    }

    private void BuildWeather(ScrollArea area, ref int y)
    {
        if (_speedMode == null)
        {
            string note = Renderer.SpeedMode.SpeedModeDebug.Supported
                ? "Rain, reflections, fog, haze and wind need enhanced lighting (GUO: Lighting and shadows)."
                : "These need enhanced lighting, which is unavailable: " + Renderer.SpeedMode.SpeedModeDebug.UnavailableReason;
            var label = new Label(note, true, 0x0386, Width - 70, 1) { X = 4, Y = y };
            area.Add(label); y += label.Height + 12;
        }
        _speedWeather = new Checkbox(0xD2,0xD3,"Rain, puddles and wet ground",1,0xFFFF)
            { X=4,Y=y,IsChecked=_profile.SpeedModeWeather };
        area.Add(_speedWeather); y+=30;
        _speedReflections = new Checkbox(0xD2,0xD3,"Reflections (approximate)",1,0xFFFF)
            { X=4,Y=y,IsChecked=_profile.SpeedModeReflections };
        area.Add(_speedReflections); y+=30;
        _speedFog = new Checkbox(0xD2,0xD3,"Weather fog",1,0xFFFF)
            { X=4,Y=y,IsChecked=_profile.SpeedModeFog };
        area.Add(_speedFog); y+=30;
        area.Add(new Label("Morning haze (%)",true,0xFFFF,font:1) { X=4,Y=y });
        _speedHaze = new HSliderBar(4,y+22,Width-110,0,100,_profile.SpeedModeMorningHaze,
            HSliderBarStyle.MetalWidgetRecessedBar,true,1,0xFFFF);
        area.Add(_speedHaze); y+=54;
        area.Add(new Label("Lighting water wind (%)",true,0xFFFF,font:1) { X=4,Y=y });
        _speedWind = new HSliderBar(4,y+22,Width-110,0,100,(int)System.MathF.Round(_profile.WindStrength*100),
            HSliderBarStyle.MetalWidgetRecessedBar,true,1,0xFFFF);
        area.Add(_speedWind); y+=54;
        void WeatherControls() {
            bool enabled=EnhancedStaged;
            _speedWeather.IsEnabled=_speedReflections.IsEnabled=_speedFog.IsEnabled=enabled;
            _speedWind.IsEnabled=enabled;
            _speedHaze.IsEnabled=enabled && _speedFog.IsChecked;
        }
        _speedFog.ValueChanged+=(_,_)=>_refreshDependents();
        var previous = _refreshDependents;
        _refreshDependents = () => { previous(); WeatherControls(); };
        AddFeatureRows(area, ref y, GuoOptions.WeatherFeatures);
        area.Add(new Label("Ambient particles per frame (shared cap)", true, 0xFFFF, font: 1) { X = 4, Y = y });
        _cap = new HSliderBar(4, y + 26, Width - 110, 0, 512, System.Math.Clamp(_profile.AmbientParticleCap, 0, 512),
            HSliderBarStyle.MetalWidgetRecessedBar, true, 1, 0xFFFF);
        area.Add(_cap);
        y += 66;
#if GUO_LABS
        var settings = Labs.Weather.WeatherLab.For(World).Settings;
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
        y += 112;
#endif
    }

    private void BuildWorld(ScrollArea area, ref int y)
    {
        area.Add(new Label("Touch UI", true, 0xFFFF, font: 1) { X = 4, Y = y });
        _touchUi = new Combobox(100, y, Width - 154, new[] { "Automatic (touch pointer)", "On", "Off" },
            _profile.TouchUi.HasValue ? (_profile.TouchUi.Value ? 1 : 2) : 0);
        area.Add(_touchUi);
        y += 50;
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
        AddFeatureRows(area, ref y, GuoOptions.WorldFeatures);
    }

    private void AddButton(int id, int x, string text) => Add(new NiceButton(18 + (id - 1) * ((Width - 36) / 4), Height - 50, (Width - 36) / 4 - 6, 44,
        ButtonAction.Activate, text, font: 1) { ButtonParameter = id, IsSelectable = false });

    internal bool ApplyChanges(bool save = true)
    {
#if GUO_LABS
        if (_weather)
        {
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
        }
#endif
        // A staged Defaults resets this section only, never its neighbours.
        if (_defaultsStaged) GuoOptions.ResetSection(_profile, _section);
        if (_lighting)
        {
            _profile.LightingSpeedMode = _speedMode.IsChecked;
            _profile.RealtimeLights = _realtimeLights.IsChecked;
#if GUO_LABS
            if(_defaultsStaged) {
                _profile.LocalLightingTestAuto=false;_profile.LocalLightingTestSpeed=60;
                Labs.LightingClock.LightingTestClock.Seek(_profile,_localClockHour.Value*60);
            }
            _profile.LocalLightingTestTime=_localClock.IsChecked;
            if(_profile.LocalLightingTestHour!=_localClockHour.Value)
                Labs.LightingClock.LightingTestClock.Seek(_profile,_localClockHour.Value*60);
#endif
            _profile.SpeedModeNightFloor=_speedNightFloor.Value;
            _profile.SpeedModeSoften=_speedSoften.Value;
            // PORT DEVIATION (GUO): ADR-0048; parent off leaves these inert.
            _profile.SpeedModeLampShadows=_speedLampShadows.SelectedIndex;
            _profile.SpeedModeSunShadows=_speedSunShadows.IsChecked;
            _profile.SpeedModeWindowLight=_speedWindowLight.IsChecked;
        }
        if (_weather)
        {
            _profile.SpeedModeWeather=_speedWeather.IsChecked;
            _profile.SpeedModeReflections=_speedReflections.IsChecked;
            _profile.SpeedModeFog=_speedFog.IsChecked;
            _profile.SpeedModeMorningHaze=_speedHaze.Value;
            _profile.WindStrength=_speedWind.Value/100f;
            _profile.AmbientParticleCap = _cap.Value;
        }
        if (_worldSection)
        {
            _profile.ExperimentalWorldOverview = _overview.IsChecked;
            _profile.WorldOverviewStyle = System.Math.Clamp(_overviewStyle.SelectedIndex, 0, 3);
            _profile.TouchUi = _touchUi.SelectedIndex == 0 ? null : _touchUi.SelectedIndex == 1;
            bool acceptShard = _shardWater?.IsChecked == true && !_defaultsStaged;
            WaterRender.AcceptShard(_profile, acceptShard);
            if (acceptShard == _shardAcceptedAtOpen)
                WaterRender.SetPlayerChoice(_profile, _waterIds[System.Math.Clamp(_water.SelectedIndex, 0, _waterIds.Length - 1)]);
            if (_defaultsStaged) { _profile.WaterStyle = WaterRender.Classic; _profile.ShardWaterStyle = ""; }
        }
        // Feature rows exist only for this section's features.
        foreach (var (id, row) in _rows) UoPlusOptions.Set(_profile, id, row.Toggle.IsChecked, row.Density.Value);
        _defaultsStaged = false;
        if (save && World.InGame) _profile.Save(World, ProfileManager.ProfilePath);
        GuoOptions.RaiseApplied(_section);
        return true;
    }

    internal void RestoreDefaults()
    {
        _defaultsStaged = true;
        if (_lighting)
        {
            _speedMode.IsChecked = false;
            _realtimeLights.IsChecked = false;
#if GUO_LABS
            _localClock.IsChecked=false;_localClockHour.Value=12;
#endif
            _speedNightFloor.Value=_speedSoften.Value=3;
            _speedLampShadows.SelectedIndex=6;
            _speedSunShadows.IsChecked=_speedWindowLight.IsChecked=true;
        }
        if (_weather)
        {
            _speedWeather.IsChecked=_speedReflections.IsChecked=_speedFog.IsChecked=true;
            _speedHaze.Value=_speedWind.Value=0;
            _cap.Value = 128;
#if GUO_LABS
            _cobblestone.IsChecked = true; _tiles.SetText(""); _error.Text = "";
#endif
        }
        if (_worldSection)
        {
            _touchUi.SelectedIndex = 0;
            _overview.IsChecked = false;
            _overviewStyle.SelectedIndex = 0;
            _overviewStyle.IsEnabled = false;
            if (_shardWater != null) _shardWater.IsChecked = false;
            _water.SelectedIndex = 0;
        }
        foreach (var row in _rows.Values) { row.Toggle.IsChecked = false; row.Density.Value = 50; }
        _refreshDependents();
    }

    public override void OnButtonClick(int buttonID)
    {
        switch (buttonID)
        {
#if GUO_LABS
            case 5:
                if(_lighting && ApplyChanges()) {
                    Dispose();
                    UIManager.GetGump<Labs.LightingClock.LiveLightingClockGump>()?.Dispose();
                    UIManager.Add(new Labs.LightingClock.LiveLightingClockGump(World,_profile));
                }
                break;
#endif
            case 1: Dispose(); break;
            case 2: ApplyChanges(); break;
            case 3: RestoreDefaults(); break;
            case 4: if (ApplyChanges()) Dispose(); break;
        }
    }
}
