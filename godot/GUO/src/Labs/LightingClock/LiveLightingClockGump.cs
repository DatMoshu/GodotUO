// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.UI.Controls;
using GUO.Game.UI.Gumps;
namespace GUO.Labs.LightingClock;
// Local testing UI: changes deliberately take effect immediately, not staged.
internal sealed class LiveLightingClockGump : Gump
{
    private readonly Profile _profile;
    private readonly bool _save;
    private readonly Checkbox _enabled,_auto;
    private readonly HSliderBar _minute,_speed;
    private readonly Label _time,_rate;
    private long _nextLabel;
    internal LiveLightingClockGump(World world,Profile profile,bool save=true):base(world,0,0)
    {
        _profile=profile;_save=save;
        Width=360;Height=370;X=Math.Max(0,(Client.Game.ClientBounds.Width-Width)/2);
        Y=Math.Max(0,(Client.Game.ClientBounds.Height-Height)/2);
        CanMove=true;CanCloseWithEsc=CanCloseWithRightClick=true;
        AcceptMouseInput=true;WantUpdateSize=false;
        Add(new AlphaBlendControl(.95f){Width=Width,Height=Height});
        Add(new Label("Live lighting clock",true,0xFFFF,font:1){X=16,Y=12});
        Add(new Label("Changes are live. Closing keeps the cycle running.",true,0x0386,Width-32,1){X=16,Y=38});
        _enabled=new Checkbox(0xD2,0xD3,"Local override (lighting speed mode)",1,0xFFFF)
            {X=16,Y=80,IsChecked=profile.LocalLightingTestTime};Add(_enabled);
        _time=new Label("",true,0xFFFF,font:1){X=16,Y=112};Add(_time);
        _minute=new HSliderBar(16,140,Width-48,0,1439,(int)LightingTestClock.MinuteAt(profile,Environment.TickCount64),
            HSliderBarStyle.MetalWidgetRecessedBar,false,1,0xFFFF);Add(_minute);
        _auto=new Checkbox(0xD2,0xD3,"Auto day / night cycle",1,0xFFFF)
            {X=16,Y=182,IsChecked=profile.LocalLightingTestAuto};Add(_auto);
        _rate=new Label("",true,0xFFFF,Width-32,1){X=16,Y=216};Add(_rate);
        _speed=new HSliderBar(16,244,Width-48,1,1440,profile.LocalLightingTestSpeed,
            HSliderBarStyle.MetalWidgetRecessedBar,false,1,0xFFFF);Add(_speed);
        Add(new Label("1x = real time; 60x = 24 min/day; 1440x = 1 min/day.",true,0x0386,Width-32,1){X=16,Y=278});
        Add(new NiceButton(16,326,Width-32,30,ButtonAction.Activate,"Save and close",font:1)
            {ButtonParameter=1,IsSelectable=false});
        _enabled.ValueChanged+=(_,_)=>{
            LightingTestClock.MinuteAt(_profile,Environment.TickCount64);
            _profile.LocalLightingTestTime=_enabled.IsChecked;
            LightingTestClock.MinuteAt(_profile,Environment.TickCount64);Refresh();};
        _minute.ValueChanged+=(_,_)=>{LightingTestClock.Seek(_profile,_minute.Value);Refresh();};
        _auto.ValueChanged+=(_,_)=>{
            LightingTestClock.MinuteAt(_profile,Environment.TickCount64);
            _profile.LocalLightingTestAuto=_auto.IsChecked;
            LightingTestClock.MinuteAt(_profile,Environment.TickCount64);Refresh();};
        _speed.ValueChanged+=(_,_)=>{
            LightingTestClock.MinuteAt(_profile,Environment.TickCount64);
            _profile.LocalLightingTestSpeed=_speed.Value;
            LightingTestClock.MinuteAt(_profile,Environment.TickCount64);Refresh();};
        Refresh();
    }
    private void Refresh()
    {
        int seconds=(int)(LightingTestClock.MinuteAt(_profile,Environment.TickCount64)*60)%86400;
        _time.Text=$"Local time: {seconds/3600:00}:{seconds/60%60:00}:{seconds%60:00}";
        _rate.Text=$"Speed: {_profile.LocalLightingTestSpeed}x  (day = {1440.0/_profile.LocalLightingTestSpeed:0.##} min)";
        bool active=_profile.LightingSpeedMode&&_profile.LocalLightingTestTime;
        _minute.IsEnabled=_auto.IsEnabled=_speed.IsEnabled=active;
        _nextLabel=Environment.TickCount64+100;
    }
    public override void Update()
    {
        base.Update();if(!IsDisposed&&Environment.TickCount64>=_nextLabel)Refresh();
    }
    public override void OnButtonClick(int buttonID) { if(buttonID==1)Dispose(); }
    public override void Dispose()
    {
        if(IsDisposed)return;
        if(_save&&World.InGame) {
            LightingTestClock.Seek(_profile,(int)LightingTestClock.MinuteAt(_profile,Environment.TickCount64));
            _profile.Save(World,ProfileManager.ProfilePath);
        }
        base.Dispose();
    }
}
