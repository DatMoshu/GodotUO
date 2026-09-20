// Real platform behaviour that upstream reached SDL for, implemented on Godot.
//
// This is the other half of the SDL story, and it is deliberately a separate
// file from SdlKeys.cs. That file holds enums -- pure values, no behaviour,
// safe to grow. This file holds actual side effects, and every member here is
// a decision about how GUO talks to the operating system.
//
// It exists so ported files stay byte-faithful. ClassicUO calls
// SDL.SDL_SetClipboardText(...) in five places; we could edit those five call
// sites, but every such edit has to be reconciled by hand on each future
// upstream merge. Matching the signature instead costs nothing and keeps the
// diff against upstream empty.
//
// KEEP THIS FILE SMALL. It is a bridge, not a platform layer. If something
// here starts needing state, a lifecycle, or more than a direct Godot call,
// it belongs in the area that owns it (Input, Render, Client) rather than in
// a compatibility shim.
//
// Audited surface, measured against upstream at pin 007ef8c3:
//   SDL_SetClipboardText   3 call sites
//   SDL_GetClipboardText   2 call sites
//   SDL_HasClipboardText   1 call site
// Everything else SDL-shaped (surfaces, windows, cursors) is rewrite-tier and
// is NOT bridged here.

using Godot;

namespace GUO.Platform.Sdl;

public static partial class SDL
{
    // Godot's DisplayServer is unavailable in a headless run and throws
    // rather than returning empty. Clipboard access is never important enough
    // to take the process down, so each call degrades to "no clipboard".

    public static bool SDL_HasClipboardText()
    {
        try
        {
            return DisplayServer.ClipboardHas();
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    public static string SDL_GetClipboardText()
    {
        try
        {
            return DisplayServer.ClipboardGet();
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Upstream's SDL3 binding returns a success flag here. Callers in
    /// ClassicUO ignore it, but the signature is kept so the call sites do
    /// not have to change.
    /// </summary>
    public static bool SDL_SetClipboardText(string text)
    {
        try
        {
            DisplayServer.ClipboardSet(text ?? string.Empty);
            return true;
        }
        catch (System.Exception)
        {
            return false;
        }
    }
}
