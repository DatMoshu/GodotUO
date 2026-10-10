#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

/// <summary>
/// ED6: when Area to multi opens a World area here, a bar under the toolbar says so and leads back to the World
/// tab, so the Multi Editor is never left open without a word.
/// </summary>
public partial class MultiEditView
{
    private HBoxContainer _fromWorld;
    private Label _fromWorldText;

    /// <summary>Goes back to the World tab (the plugin sets it).</summary>
    public Action BackToWorld { get; set; }

    /// <summary>The bar's text while it shows, else empty.</summary>
    internal string FromWorldNotice => _fromWorld?.Visible == true ? _fromWorldText.Text : "";

    private void BuildFromWorldBar()
    {
        _fromWorld = new HBoxContainer { Visible = false };
        AddChild(_fromWorld);
        // Buttons first: the Multis tab's rows can be wider than the screen, and the way back must stay in view.
        _fromWorld.AddChild(Tip(Btn("Back to World", () => { CloseFromWorld(); BackToWorld?.Invoke(); }), "Return to the World tab; this building stays open here"));
        _fromWorld.AddChild(Tip(Btn("×", CloseFromWorld), "Hide this bar"));
        _fromWorldText = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, Modulate = new Color(1f, 0.9f, 0.5f) };
        _fromWorld.AddChild(_fromWorldText);
    }

    /// <summary>Opens a World area's items as a new building and says so on the bar.</summary>
    public void OpenFromWorld(string name, System.Collections.Generic.IEnumerable<MultiPart> parts)
    {
        OpenParts(name, parts);
        _fromWorldText.Text = $"From the World: {_doc.Parts.Count} items opened here as a new building. Save it, or go back to the World.";
        _fromWorld.Show();
        _status.Text = _fromWorldText.Text;
    }

    internal void CloseFromWorld() => _fromWorld?.Hide();
}
#endif
