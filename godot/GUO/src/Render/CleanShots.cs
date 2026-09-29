// GUO addition, not a port: screenshots and films of the world without the UI.

using System.Collections.Generic;
using Godot;
using GUO.Game.UI.Gumps;

namespace GUO.Renderer;

/// <summary>
/// Hides every gump and every GUO UI layer while the world keeps drawing:
/// mobiles, items, overhead names and speech stay (they are the world's),
/// the paperdoll, status, journal, top bar, command bar, Modern views,
/// window menu, pre-game card and cursor go. <c>--hide-gumps</c> starts a
/// run that way (a <c>--shot-after</c> or <c>--screenshot</c> frame included);
/// Ctrl+Shift+H toggles it by hand. Nothing is closed or moved: hiding and
/// showing again leave every gump and layer as it was.
/// </summary>
internal sealed partial class CleanShots : Node
{
    /// <summary>Whether the UI is hidden now.</summary>
    public static bool Hidden { get; private set; }

    // Each UI layer hidden, and whether it was visible before.
    private static readonly Dictionary<CanvasLayer, bool> _was = new();

    public static void Set(bool hide)
    {
        if (hide == Hidden)
        {
            return;
        }

        Hidden = hide;
        GD.Print($"[GUO] clean shots: UI {(hide ? "hidden" : "shown")}");

        if (!hide)
        {
            foreach (KeyValuePair<CanvasLayer, bool> kv in _was)
            {
                if (IsInstanceValid(kv.Key))
                {
                    kv.Key.Visible = kv.Value;
                }
            }

            _was.Clear();
        }
    }

    /// <summary>Whether a gump is drawn: all of them normally; only the world's viewport while hidden.</summary>
    public static bool Draws(Gump g) => !Hidden || g is WorldViewportGump;

    // Last in the frame: a layer that sets its own visibility each frame is hidden after it.
    public override void _Ready() => ProcessPriority = int.MaxValue;

    public override void _Process(double delta)
    {
        if (Hidden)
        {
            HideLayers(GetTree().Root);
        }
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.H, CtrlPressed: true, ShiftPressed: true })
        {
            Set(!Hidden);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// Every UI layer (the command bar is layer 10; the Modern views, window
    /// menu, card, store and touch dots are above it). The canvas background
    /// is part of the picture and stays.
    /// </summary>
    private static void HideLayers(Node n)
    {
        foreach (Node c in n.GetChildren())
        {
            if (c is CanvasLayer layer && layer.Layer >= 10 && layer is not CanvasBackground)
            {
                if (!_was.ContainsKey(layer))
                {
                    _was[layer] = layer.Visible;
                }

                layer.Visible = false;
                continue;
            }

            HideLayers(c);
        }
    }
}
