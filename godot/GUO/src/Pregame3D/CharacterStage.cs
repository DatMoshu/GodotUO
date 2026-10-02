// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: character selection (classic
// CharacterSelectionGump) as plinths in the chest.

using System.Collections.Generic;
using Godot;
using GUO.Game.Managers;

namespace GUO.Pregame3D;

internal sealed class CharacterStage : Stage
{
    private readonly List<Hotspot> _plinths = new();
    private readonly List<int> _slots = new();
    private string[] _shown;

    public override string Hints
    {
        get
        {
            int slot = FocusedSlot;
            bool named = slot >= 0 && !string.IsNullOrEmpty(Login.Characters?[slot]);

            return named ? "A  Play     Y  Delete     X  New     B  Back" : "A  New character     B  Back";
        }
    }

    private int FocusedSlot
    {
        get
        {
            int i = _plinths.IndexOf(D.Focus.Current as Hotspot);
            return i >= 0 ? _slots[i] : -1;
        }
    }

    public override void Enter()
    {
        D.LidTo(1f, 0.6);
        D.Frame(D.StepPose("characters"), null, 0.8); // aimed first: the labels size for where it looks from
        Build();
        D.Frame(D.StepPose("characters"), _plinths, 0.8);
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

        List<Placement> slots = D.Scene.Layout.PlinthSlots;
        string last = LastCharacterManager.GetLastCharacter(Game.Scenes.LoginScene.Account, Client.Game.UO.World.ServerName);
        Hotspot focus = null, firstNamed = null;

        if (_shown.Length > slots.Count)
        {
            GD.Print($"[GUO] pregame3d: {_shown.Length} character slots, {slots.Count} plinths; the rest are not shown");
        }

        for (int i = 0; i < _shown.Length && i < slots.Count; i++)
        {
            int slot = i;
            string name = _shown[i];
            bool named = !string.IsNullOrEmpty(name);
            Node3D visual = D.Scene.Plinth();
            visual.Transform = slots[i].Transform * visual.Transform;
            D.Scene.Root.AddChild(visual);
            Hotspot h = Hotspot.Wrap(visual, "Character" + i, D.Scene.Root);
            h.Activated = () => Play(slot);
            D.AddPickable(h);
            D.TextAbove(visual, named ? name : "New", 1, named ? new Color("e0b050") : new Color("eeeade"));
            _plinths.Add(h);
            _slots.Add(slot);

            if (named && firstNamed == null)
            {
                firstNamed = h;
            }

            if (named && name == last)
            {
                focus = h;
            }
        }

        D.LinkBySpace(_plinths);
        D.Focus.Set(focus ?? firstNamed ?? (_plinths.Count > 0 ? _plinths[0] : null));
        D.RefreshHints();
    }

    private void Clear()
    {
        D.Focus.Clear();

        foreach (Hotspot h in _plinths)
        {
            D.RemovePickable(h);
            h.QueueFree();
        }

        _plinths.Clear();
        _slots.Clear();
    }

    private void Play(int slot)
    {
        if (string.IsNullOrEmpty(Login.Characters?[slot]))
        {
            Login.StartCharCreation();
            return;
        }

        GD.Print($"[GUO] pregame3d: play \"{Login.Characters[slot]}\" (slot {slot})");
        Login.SelectCharacter((uint) slot);
    }

    public override bool Command(PadCmd cmd)
    {
        int slot = FocusedSlot;

        switch (cmd)
        {
            case PadCmd.B:
                Login.StepBack();
                return true;

            case PadCmd.Start:
                if (slot >= 0)
                {
                    Play(slot);
                }

                return true;

            case PadCmd.X:
                if (System.Array.Exists(Login.Characters ?? System.Array.Empty<string>(), string.IsNullOrEmpty))
                {
                    Login.StartCharCreation();
                }
                else
                {
                    D.ShowMessage("Every character slot is in use.");
                }

                return true;

            case PadCmd.Y:
                if (slot >= 0 && !string.IsNullOrEmpty(Login.Characters?[slot]))
                {
                    string name = Login.Characters[slot];
                    // As CharacterSelectionGump asks before deleting.
                    D.Confirm($"Permanently delete {name}?", () => Login.DeleteCharacter((uint) slot));
                }

                return true;
        }

        return false;
    }
}
