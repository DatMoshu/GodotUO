// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
namespace GUO.Platform;
/// <summary>Desktop placement recovery in native pixels; includes OS decorations.</summary>
internal static class WindowPlacement
{
    internal static Vector2I Recover(Vector2I clientPosition, Rect2I decorated, ReadOnlySpan<Rect2I> screens, int primary)
    {
        if(decorated.Size.X<=0 || decorated.Size.Y<=0) return clientPosition;
        int target=-1;long best=-1;
        for(int i=0;i<screens.Length;i++)
        {
            var usable=screens[i];
            if(usable.Size.X<=0 || usable.Size.Y<=0) continue;
            if(usable.Encloses(decorated)) return clientPosition;
            var overlap=usable.Intersection(decorated);
            long area=(long)Math.Max(0,overlap.Size.X)*Math.Max(0,overlap.Size.Y);
            if(area>best || (area==best && i==primary)){best=area;target=i;}
        }
        if(target<0) return clientPosition;
        var home=screens[target];
        // Oversized windows keep their size; at least the title bar is reachable.
        var centered=home.Position+new Vector2I(Math.Max(0,(home.Size.X-decorated.Size.X)/2),Math.Max(0,(home.Size.Y-decorated.Size.Y)/2));
        return clientPosition+centered-decorated.Position;
    }
    internal static bool RecoverCurrent()
    {
        if(OS.HasFeature("mobile") || DisplayServer.GetName()=="headless" || DisplayServer.WindowGetMode()!=DisplayServer.WindowMode.Windowed) return false;
        int count=DisplayServer.GetScreenCount();
        Span<Rect2I> screens=count<=16 ? stackalloc Rect2I[count] : new Rect2I[count];
        for(int i=0;i<count;i++) screens[i]=DisplayServer.ScreenGetUsableRect(i);
        var client=DisplayServer.WindowGetPosition();
        var outer=new Rect2I(DisplayServer.WindowGetPositionWithDecorations(),DisplayServer.WindowGetSizeWithDecorations());
        var recovered=Recover(client,outer,screens,DisplayServer.GetPrimaryScreen());
        if(recovered==client) return false;
        DisplayServer.WindowSetPosition(recovered);return true;
    }
}
