#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// What a panel hands the UO Inspector: a title, text, zero or more frames
/// (one for a still, several for an animation) and buttons. Plain data, so
/// every panel speaks to the inspector the same way.
/// </summary>
public sealed class Inspection
{
    /// <summary>The panel it came from, e.g. "Gumps".</summary>
    public string Source;

    /// <summary>The id as the panel names it, e.g. "0x0E75".</summary>
    public string Id;

    /// <summary>BBCode shown under the preview.</summary>
    public string Text = "";

    /// <summary>Frames to show; one for a still image. May be empty.</summary>
    public Image[] Frames = Array.Empty<Image>();

    /// <summary>Playback rate when there is more than one frame.</summary>
    public double Fps = 10;

    /// <summary>Buttons under the preview: label and what it does.</summary>
    public List<(string Label, Action Run)> Actions = new();

    public Image Image => Frames.Length > 0 ? Frames[0] : null;

    public static Inspection Still(string source, string id, Image image, string text) =>
        new()
        {
            Source = source,
            Id = id,
            Frames = image != null ? new[] { image } : Array.Empty<Image>(),
            Text = text,
        };
}
#endif
