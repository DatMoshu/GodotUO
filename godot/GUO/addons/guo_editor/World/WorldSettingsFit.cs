#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// ED5: the settings panel stays readable at any size. Tools gets the rows its settings need before the inspector
/// below it takes the rest; neither panel draws over the other; a setting below a fold is announced by a cue
/// at the bottom of its scroll box; the rail names its tools in words.
/// </summary>
public partial class WorldView
{
    private ScrollContainer _advancedTools;
    private readonly List<ScrollCue> _scrollCues = new();

    /// <summary>A tool's name in words, for the rail's hover text and the label on the map.</summary>
    internal static string ToolName(WorldTool tool) => tool switch
    {
        WorldTool.PlaceItem => "Place item",
        WorldTool.PlaceSpawner => "Place spawner",
        WorldTool.MoveObject => "Move object",
        WorldTool.DeleteObject => "Delete object",
        WorldTool.Area => "Area (select for a multi)",
        _ => tool.ToString(),
    };

    /// <summary>
    /// The settings tabs ask for the Tools tab's full height (tab bar included), as long as the inspector keeps its
    /// own minimum; the split cannot then squeeze Tools below a fold. Clipping keeps each panel inside its rect, so
    /// at a size too small for both nothing is drawn over the other.
    /// </summary>
    private void FitSettingsRows()
    {
        if (_leftWorkspace == null || _settings == null || _detailTabs == null || _commonTools == null) return;
        var tabs = (TabContainer)_settings;
        float content = _commonTools.GetChild<Control>(0).GetCombinedMinimumSize().Y;
        float bar = tabs.GetTabBar().GetCombinedMinimumSize().Y;
        float room = _leftWorkspace.Size.Y - _detailTabs.GetCombinedMinimumSize().Y - _leftWorkspace.GetThemeConstant("separation");
        float want = MathF.Floor(Math.Clamp(content + bar + 8, 160, Math.Max(160, room)));
        if (Math.Abs(tabs.CustomMinimumSize.Y - want) > 0.5f) tabs.CustomMinimumSize = new Vector2(0, want);
    }

    private void AddScrollCues()
    {
        _leftWorkspace.ClipContents = true; _settings.ClipContents = true; _detailTabs.ClipContents = true;
        foreach (ScrollContainer scroll in new[] { _commonTools, _advancedTools })
        {
            var cue = new ScrollCue(scroll) { Name = "ScrollCue" };
            scroll.AddChild(cue); _scrollCues.Add(cue);
        }
    }

    // Smoke and tour access.
    internal ScrollContainer AdvancedTools => _advancedTools;
    internal ScrollContainer CommonTools => _commonTools;
    internal AssetField ItemBox => _brush;
    internal Label MapHint => _previewLabel;
    internal TabContainer SettingsTabs => (TabContainer)_settings;
    internal TabContainer DetailTabs => _detailTabs;
    internal IReadOnlyList<ScrollCue> ScrollCues => _scrollCues;
    internal IEnumerable<(WorldTool Tool, Button Button)> RailButtons { get { foreach (var p in _toolButtons) yield return (p.Key, p.Value); } }

    /// <summary>True when every setting in <paramref name="scroll"/> is in view, or the cue says there is more below.</summary>
    internal static bool FoldAnnounced(ScrollContainer scroll, ScrollCue cue)
    {
        bool more = scroll.GetVScrollBar().MaxValue - scroll.ScrollVertical > scroll.Size.Y + 2;
        return !more || (cue.IsVisibleInTree() && scroll.GetVScrollBar().Visible);
    }
}

/// <summary>
/// A bar at the bottom of a settings scroll box while settings lie below its fold: "More below", and a click
/// scrolls down a page. Top-level, so the scroll box does not lay it out; it follows the box every frame.
/// </summary>
[Tool]
public partial class ScrollCue : Control
{
    private readonly ScrollContainer _scroll;

    public ScrollCue() { }

    internal ScrollCue(ScrollContainer scroll)
    {
        _scroll = scroll;
        TopLevel = true;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        TooltipText = "More settings below: scroll, or click here";
        ZIndex = 1;
    }

    /// <summary>Settings sit below the fold and the cue is up.</summary>
    internal bool Showing => Visible && _scroll != null;

    public override void _Process(double delta)
    {
        if (_scroll == null) return;
        if (!_scroll.IsVisibleInTree()) { Visible = false; return; }
        bool more = _scroll.GetVScrollBar().MaxValue - _scroll.ScrollVertical > _scroll.Size.Y + 2;
        float h = MathF.Round(22 * EditorInterface.Singleton.GetEditorScale());
        float bar = _scroll.GetVScrollBar().Visible ? _scroll.GetVScrollBar().Size.X : 0;
        Visible = more;
        if (!more) return;
        Rect2 box = _scroll.GetGlobalRect();
        Vector2 position = new(box.Position.X, box.End.Y - h), size = new(box.Size.X - bar, h);
        if (position != GlobalPosition || size != Size) { GlobalPosition = position; Size = size; QueueRedraw(); }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            _scroll.ScrollVertical += (int)(_scroll.Size.Y * 0.8f);
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        Color back = GetThemeColor("base_color", "Editor");
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(back, 0.94f));
        DrawLine(Vector2.Zero, new Vector2(Size.X, 0), GetThemeColor("accent_color", "Editor"), 1f);
        Font font = GetThemeFont("font", "Label");
        int fontSize = GetThemeFontSize("font_size", "Label");
        const string text = "▼ More below";
        Vector2 measured = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize);
        DrawString(font, new Vector2((Size.X - measured.X) / 2, (Size.Y + font.GetAscent(fontSize) - font.GetDescent(fontSize)) / 2),
            text, HorizontalAlignment.Left, -1, fontSize, GetThemeColor("accent_color", "Editor"));
    }
}
#endif
