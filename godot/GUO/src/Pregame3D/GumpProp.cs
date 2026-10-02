// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using Godot;
using GUO.Input.Touch;

namespace GUO.Pregame3D;

/// <summary>
/// One of the client's own gump pieces standing over the painting (a button,
/// a checkbox, a field): placed where the classic gump puts it, drawn
/// through the gump shader (xBR, dim, glow). On the pad's focus it lifts a
/// little and glows gold; a hover shows its "over" art, a press its
/// "pressed" art. It is a Control, so the pointer hit-tests it like any
/// overlay row.
/// </summary>
internal sealed class GumpProp : IOverlayFocusable
{
    public static Shader Shader => _shader ??= ResourceLoader.Exists(ShaderPath) ? GD.Load<Shader>(ShaderPath) : null;

    private const string ShaderPath = "res://assets/pregame/shaders/gump.gdshader";
    private static Shader _shader;

    /// <summary>How far a focused prop rises, in painting pixels.</summary>
    private const float Lift = 2f;

    private readonly TextureRect _rect;
    private readonly Control _control;
    private ushort _normal, _over, _pressed;
    private Vector2 _rest;
    private bool _focused;
    private bool _down;

    public ShaderMaterial Material { get; }

    /// <summary>A button from three gump ids (pressed and over may repeat normal).</summary>
    public GumpProp(ushort normal, ushort pressed, ushort over, Vector2 at)
    {
        _normal = normal;
        _pressed = pressed;
        _over = over;
        _rest = at;
        Material = NewMaterial();
        _rect = new TextureRect
        {
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = Material,
            Position = at,
        };
        _control = _rect;
        Show();
    }

    /// <summary>Any control (a field: a parchment frame with its text) as a prop.</summary>
    public GumpProp(Control control, Vector2 at)
    {
        Material = NewMaterial();
        _control = control;
        _control.Material = Material;
        _control.Position = at;
        _rest = at;
    }

    public static ShaderMaterial NewMaterial()
    {
        var m = new ShaderMaterial { Shader = Shader };
        m.SetShaderParameter("glow", 0f);
        m.SetShaderParameter("dim", 1f);
        return m;
    }

    public Control Control => _control;

    public string Tag { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool CanFocus => Enabled && _control.IsVisibleInTree();
    public IFocusable Up { get; set; }
    public IFocusable Down { get; set; }
    public IFocusable Left { get; set; }
    public IFocusable Right { get; set; }
    public Action<int> Cycle { get; set; }
    public Action Activated { get; set; }

    /// <summary>Whether the art is all here (a gump just added to the atlas can read back late).</summary>
    public bool Ready => _rect == null || _rect.Texture != null;

    /// <summary>Swaps the art (a checkbox ticked or not).</summary>
    public void SetArt(ushort normal, ushort pressed, ushort over)
    {
        _normal = normal;
        _pressed = pressed;
        _over = over;
        Show();
    }

    public void SetFocused(bool focused)
    {
        _focused = focused;
        Material.SetShaderParameter("glow", focused ? 1f : 0f);
        _control.Position = _rest - new Vector2(0, focused ? Lift : 0);
        Show();
    }

    public void Press()
    {
        if (!Enabled)
        {
            return;
        }

        _down = true;
        Show();
        _control.GetTree()?.CreateTimer(0.12).Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(() =>
        {
            _down = false;

            if (GodotObject.IsInstanceValid(_control))
            {
                Show();
            }
        }));
        Activated?.Invoke();
    }

    /// <summary>Again, for art that was not in the atlas yet.</summary>
    public void Show()
    {
        if (_rect == null)
        {
            return;
        }

        Texture2D t = PregameAssets.Texture(_down ? _pressed : _focused ? _over : _normal) ?? PregameAssets.Texture(_normal);

        if (t != null && t != _rect.Texture)
        {
            _rect.Texture = t;
            _rect.Size = t.GetSize();
        }
    }
}
