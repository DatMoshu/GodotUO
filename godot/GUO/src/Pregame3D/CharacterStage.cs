// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: character selection (classic
// CharacterSelectionGump) as a panel of the client's own art over the
// dimmed painting, with the gump's own New, Delete and arrow buttons.

using System.Collections.Generic;
using Godot;
using GUO.Game.Managers;
using GUO.Input.Touch;

namespace GUO.Pregame3D;

internal sealed class CharacterStage : Stage
{
    private readonly List<IOverlayFocusable> _items = new();
    private readonly List<UiFocus> _rows = new();
    private PanelContainer _panel;
    private string[] _shown;
    private int _selected = -1;

    public static bool ProbeCharacterFocused => PregameScreen.Instance?.Stage is CharacterStage s && (s.D.Focus.Current as UiFocus)?.Tag is string t && t.StartsWith("char:");

    public override IEnumerable<IOverlayFocusable> OverlayItems => _items;

    public override string Hints => (D.Focus.Current as UiFocus)?.Tag is string t && t.StartsWith("char:")
        ? "A  Play     Y  Delete     X  New     B  Back"
        : "A  Press     X  New     B  Back";

    public override void Enter()
    {
        D.SetDim(0.45f);
        Build();
    }

    public override void Exit() => Clear();

    public override void Update(double delta)
    {
        // A delete (or a re-sent list) replaces the array: rebuild.
        if (!ReferenceEquals(Login.Characters, _shown))
        {
            Build();
        }

        // UpdateCharacterList leaves the server's message for us (the classic
        // gump showed it as a modal LoadingGump).
        if (!string.IsNullOrWhiteSpace(Login.PopupMessage) && !D.ModalOpen)
        {
            string message = Login.PopupMessage;
            Login.PopupMessage = null;
            D.ShowMessage(message);
        }
    }

    private void Build()
    {
        Clear();
        _shown = Login.Characters;

        if (_shown == null)
        {
            return;
        }

        var clilocs = Client.Game.UO.FileManager.Clilocs;
        _panel = Overlay.Card(0x0A28);
        VBoxContainer col = Overlay.Column(4);
        _panel.AddChild(col);
        Label title = Overlay.Text(clilocs.GetString(3000050, "Character Selection"), UoTheme.Heading);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(title);

        string last = LastCharacterManager.GetLastCharacter(Game.Scenes.LoginScene.Account, Client.Game.UO.World.ServerName);
        UiFocus focus = null;

        for (int i = 0; i < _shown.Length; i++)
        {
            if (string.IsNullOrEmpty(_shown[i]))
            {
                continue;
            }

            int slot = i;
            Label name = Overlay.Text(_shown[i], UoTheme.Ink);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.CustomMinimumSize = new Vector2(260, 0);
            UiFocus row = Overlay.FrameRow(name, name, "char:" + _shown[i]);
            row.Pressed = () => Play(slot);
            row.Shown += on =>
            {
                if (on)
                {
                    _selected = slot;
                }
            };
            col.AddChild(row.Control);
            _items.Add(row);
            _rows.Add(row);

            if (focus == null || _shown[i] == last)
            {
                focus = _shown[i] == last || focus == null ? row : focus;
            }
        }

        if (_rows.Count == 0)
        {
            col.AddChild(Overlay.Text("No characters yet.", UoTheme.Muted));
        }

        // The gump's buttons: New and Delete, then its arrows.
        HBoxContainer buttons = Overlay.Row(10);
        buttons.Alignment = BoxContainer.AlignmentMode.Center;
        var prev = new GumpProp(0x15A1, 0x15A3, 0x15A2, Vector2.Zero) { Tag = "prev", Activated = () => Login.StepBack() };
        var create = new GumpProp(0x159D, 0x159F, 0x159E, Vector2.Zero) { Tag = "new", Activated = New };
        var delete = new GumpProp(0x159A, 0x159C, 0x159B, Vector2.Zero) { Tag = "delete", Activated = Delete };
        var next = new GumpProp(0x15A4, 0x15A6, 0x15A5, Vector2.Zero) { Tag = "next", Activated = () => { if (_selected >= 0) Play(_selected); } };
        var bottom = new List<IFocusable> { prev, create, delete, next };

        foreach (GumpProp b in new[] { prev, create, delete, next })
        {
            buttons.AddChild(b.Control);
            _items.Add(b);
        }

        col.AddChild(buttons);
        delete.Enabled = _rows.Count > 0;
        next.Enabled = _rows.Count > 0;

        var column = new List<IFocusable>(_rows);
        PadFocus.LinkColumn(column);
        PadFocus.LinkRow(bottom);

        if (_rows.Count > 0)
        {
            _rows[^1].Down = create;

            foreach (IFocusable b in bottom)
            {
                b.Up = _rows[^1];
            }
        }

        D.OverlayRoot.AddChild(_panel);
        _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical = Control.GrowDirection.Both;
        D.Focus.Set((IFocusable) focus ?? create);
        D.RefreshHints();
    }

    private void Clear()
    {
        D.Focus.Clear();
        _panel?.QueueFree();
        _panel = null;
        _items.Clear();
        _rows.Clear();
    }

    private void Play(int slot)
    {
        GD.Print($"[GUO] pregame3d: play \"{Login.Characters[slot]}\" (slot {slot})");
        Login.SelectCharacter((uint) slot);
    }

    private void New()
    {
        if (System.Array.Exists(Login.Characters ?? System.Array.Empty<string>(), string.IsNullOrEmpty))
        {
            Login.StartCharCreation();
        }
        else
        {
            D.ShowMessage("Every character slot is in use.");
        }
    }

    private void Delete()
    {
        if (_selected >= 0 && _selected < (Login.Characters?.Length ?? 0) && !string.IsNullOrEmpty(Login.Characters[_selected]))
        {
            int slot = _selected;
            string name = Login.Characters[slot];
            // As CharacterSelectionGump asks before deleting.
            D.Confirm($"Permanently delete {name}?", () => Login.DeleteCharacter((uint) slot));
        }
    }

    public override bool Command(PadCmd cmd)
    {
        switch (cmd)
        {
            case PadCmd.B:
                Login.StepBack();
                return true;

            case PadCmd.Start:
                if (_selected >= 0)
                {
                    Play(_selected);
                }

                return true;

            case PadCmd.X:
                New();
                return true;

            case PadCmd.Y:
                Delete();
                return true;
        }

        return false;
    }
}
