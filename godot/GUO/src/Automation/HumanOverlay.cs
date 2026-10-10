// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Game.Managers;

namespace GUO.Automation;

/// <summary>
/// What the human driver (tools/scenario_run, <c>--driver human</c>) draws over the game: a caption card (step number
/// and the step's text) and a yellow outline round the control the step names. Driven by the <c>guo_overlay</c> tool
/// of <see cref="McpHost"/>, so it exists only in a run that has the automation MCP on. It also watches for Space
/// (skip the step) and Esc (abort the run) while a step is shown, and hands them to the next <c>guo_overlay</c> call.
/// </summary>
/// <remarks>
/// A client copy of the editor tour's TourOverlay drawing (godot/GUO/addons/guo_editor/TourOverlay.cs): that class is
/// <c>[Tool]</c> code compiled only in the editor, so it cannot be reused in the game. The look is the same card and
/// outline, sized for the game's 1280x720 viewport. It never takes the mouse, and its texture filter is nearest
/// (docs/port_plan.md, rule 7), though it draws no textures.
/// </remarks>
public partial class HumanOverlay : Control
{
    private const string StepLabelName = "HumanOverlayStep";   // tools/scenario_run/ghost_human.py reads this label from guo_ui

    private PanelContainer _card;
    private Label _step, _text;
    private Rect2? _outline;
    private string _outlineLabel = "";
    private bool _shown, _hidden, _skip, _abort;

    public override void _Ready()
    {
        Name = "HumanOverlay";
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.16f, 0.11f, 0.06f, 0.94f),
            BorderColor = new Color(0.78f, 0.62f, 0.3f),
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 8,
        };
        style.SetBorderWidthAll(2);
        _card = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _card.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 2);
        _card.AddChild(box);
        _step = new Label { Name = StepLabelName };
        _step.AddThemeColorOverride("font_color", new Color(0.78f, 0.62f, 0.3f));
        box.AddChild(_step);
        _text = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _text.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.7f));
        box.AddChild(_text);
        AddChild(_card);
    }

    /// <summary>Shows a step's caption. The outline of the previous step goes.</summary>
    public void SetCaption(string step, string text)
    {
        _step.Text = step ?? "";
        _text.Text = text ?? "";
        _shown = true;
        _outline = null;
        ApplyVisibility();
        QueueRedraw();
    }

    public void SetOutline(Rect2 rect, string label)
    {
        _outline = rect;
        _outlineLabel = label ?? "";
        QueueRedraw();
    }

    /// <summary>Removes the card and the outline; Space and Esc stop counting until the next caption.</summary>
    public void Clear()
    {
        _shown = false;
        _outline = null;
        ApplyVisibility();
        QueueRedraw();
    }

    /// <summary>Hidden draws nothing (a clean recording) but keeps Space and Esc working.</summary>
    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        ApplyVisibility();
        QueueRedraw();
    }

    /// <summary>Space and Esc pressed since the last call; reading clears them.</summary>
    public (bool Skip, bool Abort) TakeKeys()
    {
        var keys = (_skip, _abort);
        _skip = _abort = false;
        return keys;
    }

    private void ApplyVisibility() => _card.Visible = _shown && !_hidden;

    public override void _Input(InputEvent e)
    {
        if (!_shown || e is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.Escape) _abort = true;
        // A Space typed into a text field is a character, not a skip. Text a script inserts has no keycode.
        else if (key.Keycode == Key.Space && !TypingInAField()) _skip = true;
    }

    private bool TypingInAField() =>
        UIManager.KeyboardFocusControl != null || GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;

    public override void _Process(double delta)
    {
        if (!_card.Visible) return;
        Vector2 area = GetViewportRect().Size;
        float k = Math.Clamp(area.X / 1280f, 0.75f, 3f);
        _step.AddThemeFontSizeOverride("font_size", (int)(13 * k));
        _text.AddThemeFontSizeOverride("font_size", (int)(20 * k));
        _text.CustomMinimumSize = new Vector2(Math.Min(area.X * 0.7f, 900 * k), 0);
        _card.ResetSize();
        Vector2 size = _card.GetCombinedMinimumSize();
        _card.Size = size;
        _card.Position = new Vector2((area.X - size.X) / 2, area.Y - size.Y - 14 * k);
    }

    public override void _Draw()
    {
        if (_hidden || !_shown || _outline is not Rect2 rect) return;
        var yellow = new Color(1f, 0.85f, 0.2f);
        Rect2 r = rect.Grow(4);
        DrawRect(r.Grow(2), new Color(0, 0, 0, 0.85f), false, 2f);
        DrawRect(r, yellow, false, 3f);
        if (string.IsNullOrEmpty(_outlineLabel)) return;
        Font font = GetThemeDefaultFont();
        Vector2 size = font.GetStringSize(_outlineLabel, HorizontalAlignment.Left, -1, 16);
        var tag = new Rect2(r.Position.X, r.Position.Y - size.Y - 8, size.X + 12, size.Y + 6);
        if (tag.Position.Y < 0) tag.Position = new Vector2(r.Position.X, r.End.Y + 2);
        DrawRect(tag, yellow);
        DrawString(font, tag.Position + new Vector2(6, size.Y - 3), _outlineLabel, HorizontalAlignment.Left, -1, 16, new Color(0.1f, 0.07f, 0.02f));
    }
}
