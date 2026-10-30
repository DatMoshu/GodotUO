// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
namespace GUO.Platform;
public partial class WindowPlacementProbe : Node
{
    private static void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);}
    public override async void _Ready()
    {
        try
        {
            var screen=new Rect2I(0,40,1920,1040);
            var valid=new Rect2I(120,100,816,639);
            Check(WindowPlacement.Recover(new(128,131),valid,new[]{screen},0)==new Vector2I(128,131),"valid saved position unchanged");
            var clipped=new Rect2I(120,9,816,639);
            var result=WindowPlacement.Recover(new(128,40),clipped,new[]{screen},0);
            var repaired=new Rect2I(clipped.Position+result-new Vector2I(128,40),clipped.Size);
            Check(screen.Encloses(repaired)&&result!=new Vector2I(128,40),"titlebar clipped while most client visible");
            Check(WindowPlacement.Recover(result,repaired,new[]{screen},0)==result,"recovery idempotent");
            var left=new Rect2I(-1920,0,1920,1080);var leftWindow=new Rect2I(-1500,90,816,639);
            Check(WindowPlacement.Recover(new(-1492,121),leftWindow,new[]{screen,left},0)==new Vector2I(-1492,121),"negative monitor preserved");
            var gone=new Rect2I(6000,-3000,816,639);
            var fromGone=WindowPlacement.Recover(new(6008,-2969),gone,new[]{screen,left},1);
            Check(left.Encloses(new Rect2I(gone.Position+fromGone-new Vector2I(6008,-2969),gone.Size)),"removed monitor recovers primary");
            var right=new Rect2I(1920,40,1280,984);var partial=new Rect2I(3000,70,816,639);
            var fromPartial=WindowPlacement.Recover(new(3008,101),partial,new[]{screen,right},0);
            Check(right.Encloses(new Rect2I(partial.Position+fromPartial-new Vector2I(3008,101),partial.Size)),"largest overlap screen chosen");
            var huge=new Rect2I(-100,-100,2400,1500);
            var largeResult=WindowPlacement.Recover(new(-92,-69),huge,new[]{screen},0);
            Check(largeResult==screen.Position+new Vector2I(8,31),"oversize title bar reachable without resize");
            Check(WindowPlacement.Recover(new(7,9),valid,Array.Empty<Rect2I>(),0)==new Vector2I(7,9),"headless/no screens unchanged");
            Check(WindowPlacement.Recover(new(7,9),new Rect2I(7,9,0,0),new[]{screen},0)==new Vector2I(7,9),"invalid size unchanged");
            bool native=OS.GetCmdlineUserArgs().Contains("--native-window-placement-probe");
            object measurements=null;
            if(native)
            {
                Check(DisplayServer.GetName()!="headless","native display required");
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus,true);
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(800,600));
                for(int i=0;i<3;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                int monitor=DisplayServer.WindowGetCurrentScreen();var work=DisplayServer.ScreenGetUsableRect(monitor);
                // Reproduce the reported client-at-workarea-top/titlebar-above-screen state.
                DisplayServer.WindowSetPosition(work.Position+new Vector2I(40,0));
                for(int i=0;i<3;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                var before=new Rect2I(DisplayServer.WindowGetPositionWithDecorations(),DisplayServer.WindowGetSizeWithDecorations());
                Check(before.Position.Y<work.Position.Y,"native clipped-titlebar baseline witness");
                Check(WindowPlacement.RecoverCurrent(),"actual native recovery performed");
                for(int i=0;i<3;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                var after=new Rect2I(DisplayServer.WindowGetPositionWithDecorations(),DisplayServer.WindowGetSizeWithDecorations());
                Check(work.Encloses(after),"actual decorated window entirely inside usable screen");
                Check(!WindowPlacement.RecoverCurrent(),"actual recovery idempotent");
                measurements=new{work=work.ToString(),before=before.ToString(),after=after.ToString()};
            }
            var output=OS.GetEnvironment("GUO_WINDOW_PLACEMENT_OUT");
            if(!string.IsNullOrWhiteSpace(output))File.WriteAllText(output,JsonSerializer.Serialize(new{passed=true,native,measurements},new JsonSerializerOptions{WriteIndented=true}));
            GD.Print("[window placement] PASS: clipped titlebar, saved/negative/removed monitors, taskbar, overlap, oversize, idempotence, headless; native="+native);
            GetTree().Quit(0);
        }
        catch(Exception e){GD.PrintErr("[window placement] FAIL "+e);GetTree().Quit(1);}
    }
}
