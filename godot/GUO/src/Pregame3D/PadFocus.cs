// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO has no gamepad.

using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Pregame3D;

/// <summary>What a pad, the keyboard or the pointer asks the 3D pregame to do.</summary>
internal enum PadCmd
{
    Up,
    Down,
    Left,
    Right,

    /// <summary>Printed A, Enter, a left click: press the focused thing.</summary>
    A,

    /// <summary>Printed B, Escape, a right click: back (LoginScene.StepBack) or close.</summary>
    B,

    /// <summary>Printed X: the step's extra (backspace on the keyboard).</summary>
    X,

    /// <summary>Printed Y: the step's other extra (shift on the keyboard).</summary>
    Y,

    /// <summary>Start (Menu): the step's primary action (Login, Play, Create; Done on the keyboard).</summary>
    Start,
    LeftShoulder,
    RightShoulder,
}

/// <summary>A thing the pad's focus can rest on: a 3D <see cref="Hotspot"/> or an overlay row.</summary>
internal interface IFocusable
{
    /// <summary>False while hidden or disabled: focus skips it.</summary>
    bool CanFocus { get; }

    /// <summary>Explicit neighbours, authored per step (null: none that way).</summary>
    IFocusable Up { get; set; }
    IFocusable Down { get; set; }
    IFocusable Left { get; set; }
    IFocusable Right { get; set; }

    /// <summary>Left/right on a row that cycles a value (a hue, a hair style) instead of moving focus.</summary>
    Action<int> Cycle { get; set; }

    void SetFocused(bool focused);

    /// <summary>The press animation and the activation callback.</summary>
    void Press();
}

/// <summary>
/// The pad focus of the 3D pregame: one focused <see cref="IFocusable"/>,
/// moved along the explicit neighbour graph each step authors (never
/// guessed), with a held direction repeating as a keyboard's arrows do.
/// The input routing into it is <see cref="PregameScreen.HandlePad"/> and
/// <see cref="PregameScreen.HandleMainInput"/>, the way WindowMenu takes the
/// D-pad while it is open.
/// </summary>
internal sealed class PadFocus
{
    private const double RepeatDelay = 0.38;
    private const double RepeatEvery = 0.11;

    private readonly bool[] _held = new bool[4];
    private double _repeatAt;
    private int _repeating = -1;

    public IFocusable Current { get; private set; }

    /// <summary>Raised when focus lands somewhere (for the step's hints).</summary>
    public event Action<IFocusable> Moved;

    public void Set(IFocusable f)
    {
        if (ReferenceEquals(f, Current))
        {
            return;
        }

        Current?.SetFocused(false);
        Current = f;
        Current?.SetFocused(true);
        Moved?.Invoke(Current);
    }

    public void Clear() => Set(null);

    /// <summary>
    /// One step along the graph: the neighbour that way, skipping any that
    /// cannot take focus (following the same direction from them). Left and
    /// right on a cycling row change its value instead.
    /// </summary>
    public bool Move(PadCmd dir)
    {
        if (Current == null)
        {
            return false;
        }

        if (Current.Cycle != null && (dir == PadCmd.Left || dir == PadCmd.Right))
        {
            Current.Cycle(dir == PadCmd.Left ? -1 : 1);
            return true;
        }

        IFocusable next = Current;
        var seen = new HashSet<IFocusable>();

        do
        {
            next = dir switch
            {
                PadCmd.Up => next.Up,
                PadCmd.Down => next.Down,
                PadCmd.Left => next.Left,
                PadCmd.Right => next.Right,
                _ => null,
            };
        }
        while (next != null && !next.CanFocus && seen.Add(next));

        if (next == null || !next.CanFocus)
        {
            return false;
        }

        Set(next);
        return true;
    }

    /// <summary>A direction held down (pad or arrow key): the first step now, then repeats from <see cref="Update"/>.</summary>
    public void Hold(int dir, bool down, Action<PadCmd> step)
    {
        if (_held[dir] == down)
        {
            return;
        }

        _held[dir] = down;

        if (down)
        {
            _repeating = dir;
            _repeatAt = Godot.Time.GetTicksMsec() / 1000.0 + RepeatDelay;
            step((PadCmd) dir);
        }
        else if (_repeating == dir)
        {
            _repeating = -1;
        }
    }

    public void ReleaseAll()
    {
        Array.Clear(_held);
        _repeating = -1;
    }

    public void Update(Action<PadCmd> step)
    {
        if (_repeating < 0 || !_held[_repeating])
        {
            return;
        }

        double now = Godot.Time.GetTicksMsec() / 1000.0;

        if (now >= _repeatAt)
        {
            _repeatAt = now + RepeatEvery;
            step((PadCmd) _repeating);
        }
    }

    // --- authoring helpers -------------------------------------------------------

    /// <summary>Left/right neighbours along a row (no wrap).</summary>
    public static void LinkRow(IList<IFocusable> row)
    {
        for (int i = 0; i < row.Count; i++)
        {
            row[i].Left = i > 0 ? row[i - 1] : row[i].Left;
            row[i].Right = i + 1 < row.Count ? row[i + 1] : row[i].Right;
        }
    }

    /// <summary>Up/down neighbours along a column (no wrap).</summary>
    public static void LinkColumn(IList<IFocusable> column)
    {
        for (int i = 0; i < column.Count; i++)
        {
            column[i].Up = i > 0 ? column[i - 1] : column[i].Up;
            column[i].Down = i + 1 < column.Count ? column[i + 1] : column[i].Down;
        }
    }

    /// <summary>
    /// Neighbours from where things are (map pins, laid-out cards): each way,
    /// the nearest one in that direction within 45 degrees.
    /// </summary>
    public static void LinkByPositions(IList<IFocusable> items, IList<Vector2> at)
    {
        for (int i = 0; i < items.Count; i++)
        {
            items[i].Up = Nearest(i, new Vector2(0, -1));
            items[i].Down = Nearest(i, new Vector2(0, 1));
            items[i].Left = Nearest(i, new Vector2(-1, 0));
            items[i].Right = Nearest(i, new Vector2(1, 0));
        }

        IFocusable Nearest(int from, Vector2 dir)
        {
            IFocusable best = null;
            float bestScore = float.MaxValue;

            for (int j = 0; j < items.Count; j++)
            {
                if (j == from)
                {
                    continue;
                }

                Vector2 d = at[j] - at[from];
                float along = d.Dot(dir);
                float across = Math.Abs(d.Dot(new Vector2(dir.Y, -dir.X)));

                if (along <= 0.5f || across > along * 1.2f)
                {
                    continue;
                }

                float score = along + across * 2f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = items[j];
                }
            }

            return best;
        }
    }

    /// <summary>Lays <paramref name="items"/> out as a grid of <paramref name="columns"/> and links all four ways.</summary>
    public static void LinkGrid(IList<IFocusable> items, int columns)
    {
        for (int i = 0; i < items.Count; i++)
        {
            int c = i % columns;
            items[i].Left = c > 0 ? items[i - 1] : null;
            items[i].Right = c + 1 < columns && i + 1 < items.Count ? items[i + 1] : null;
            items[i].Up = i - columns >= 0 ? items[i - columns] : null;
            items[i].Down = i + columns < items.Count ? items[i + columns] : (i + 1 < items.Count && (items.Count - 1) / columns > i / columns ? items[items.Count - 1] : null);
        }
    }
}
