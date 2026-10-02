// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System.Collections.Generic;
using Godot;
using GUO.Game.Scenes;

namespace GUO.Pregame3D;

/// <summary>
/// One step of the 3D pregame, following <c>LoginScene.CurrentLoginStep</c>.
/// A stage wires its hotspots' neighbours and actions, moves the camera,
/// and drives the login only through LoginScene's own calls.
/// </summary>
internal abstract class Stage
{
    protected PregameScreen D { get; private set; }

    protected LoginScene Login => D.Login;

    public void Attach(PregameScreen screen) => D = screen;

    public abstract void Enter();

    public virtual void Exit()
    {
    }

    /// <summary>The step moved within the steps this stage shows (connecting to verifying, ...).</summary>
    public virtual void StepChanged(LoginSteps step)
    {
    }

    public virtual void Update(double delta)
    {
    }

    /// <summary>The window changed size (the screen has re-laid itself).</summary>
    public virtual void Resized()
    {
    }

    /// <summary>The step's own handling of a command; false lets focus have it (directions, A).</summary>
    public virtual bool Command(PadCmd cmd) => false;

    /// <summary>A physical key before the default mapping (typing into a focused field).</summary>
    public virtual bool Key(InputEventKey k) => false;

    /// <summary>Whether the hint band sits at the top of the screen for this step.</summary>
    public virtual bool HintsAtTop => false;

    /// <summary>The hint band's text: which button does what here.</summary>
    public abstract string Hints { get; }

    /// <summary>Overlay rows the pointer can pick.</summary>
    public virtual IEnumerable<IOverlayFocusable> OverlayItems => System.Array.Empty<IOverlayFocusable>();

    /// <summary>The right stick's X, when the step wants it (turning the figure); false leaves it to the pointer.</summary>
    public virtual bool RightStick(float x) => false;
}
