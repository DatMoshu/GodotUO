// SPDX-License-Identifier: BSD-2-Clause
using GUO.Game.Data;
using GUO.Game.Managers;
using GUO.Game.UI.Controls;
using GUO.Game.GameObjects;
using GUO.Input;
using GUO.Utility;

namespace GUO.Game.UI.Gumps;

// PORT DEVIATION (GUO): UX-03. Only presentation changes; the original
// PaperDollInteractable, EquipmentSlot and equip/drop handlers own item actions.
internal partial class PaperDollGump
{
    internal bool IsTouchLayout { get; private set; }
    private readonly System.Collections.Generic.List<Control> _touchActions = new();
    private bool _moreTouchActions;

    private static string TouchSlotName(Layer layer) => layer switch
    {
        Layer.Helmet => "Head", Layer.Earrings => "Ears", Layer.Necklace => "Neck",
        Layer.Bracelet => "Wrist", Layer.OneHanded => "Hand", Layer.TwoHanded => "Offhand",
        Layer.Talisman => "Charm", Layer.Backpack => "Pack", Layer.Torso => "Chest",
        _ => layer.ToString()
    };

    private void BuildTouch()
    {
        IsTouchLayout = true;
        PresentationScale = System.Math.Max(1f, PresentationScale);
        Width = 392;
        Height = 396;
        WantUpdateSize = false;
        Add(new AlphaBlendControl(0.96f) { Width = Width, Height = Height });
        Add(_titleLabel = new Label("", true, 0x0481, 340, 1) { X = 8, Y = 8 });
        Add(new NiceButton(348, 0, 44, 44, ButtonAction.Activate, "Close")
            { ButtonParameter = 100, IsSelectable = false });
        _paperDollInteractable = new PaperDollInteractable(20, 32, LocalSerial, this);
        Add(_paperDollInteractable);

        Layer[] layers = { Layer.Helmet, Layer.Earrings, Layer.Necklace, Layer.Ring,
            Layer.Bracelet, Layer.Tunic, Layer.OneHanded, Layer.TwoHanded,
            Layer.Talisman, Layer.Robe, Layer.Gloves, Layer.Pants, Layer.Arms,
            Layer.Cloak, Layer.Shoes, Layer.Backpack,
            Layer.Shirt, Layer.Waist, Layer.Torso, Layer.Legs, Layer.Skirt };
        for (int i = 0; i < layers.Length; i++)
        {
            var slot = new EquipmentSlot(0, 8 + i % 8 * 48, 252 + i / 8 * 48, layers[i], this, 44);
            _slots.Add(slot);
            Add(slot);
        }
        BuildTouchActions();
        RequestUpdateContents();
    }

    private void BuildTouchActions()
    {
        foreach (Control control in _touchActions) control.Dispose();
        _touchActions.Clear();
        (string, int)[] actions = LocalSerial != World.Player
            ? new[] { ("Status", (int)Buttons.Status), ("Profile", 102) }
            : _moreTouchActions
            ? new[] { ("Profile", 102), ("Party", 103), ("Virtues", 104), ("Abilities", 105),
                ("Racial", 106), ("Guild", (int)Buttons.Guild), ("Log out", (int)Buttons.LogOut), ("Back", 101) }
            : new[] { ("Status", (int)Buttons.Status), ("Backpack", 107), ("Skills", (int)Buttons.Skills),
                ("Options", (int)Buttons.Options), ("Quests", (int)Buttons.Quests), ("War/peace", (int)Buttons.PeaceWarToggle),
                ("Help", (int)Buttons.Help), ("More", 101) };
        for (int i = 0; i < actions.Length; i++)
        {
            if (LocalSerial != World.Player && actions[i].Item2 != (int)Buttons.Status && actions[i].Item2 != 102) continue;
            var button = new NiceButton(212 + i % 2 * 92, 44 + i / 2 * 44, 88, 44,
                ButtonAction.Activate, actions[i].Item1, hue: 0x0386, font: 1)
                { ButtonParameter = actions[i].Item2, IsSelectable = false };
            var frame = new ResizePic(0x0BB8) { X = button.X, Y = button.Y, Width = button.Width - 2, Height = button.Height - 2, AcceptMouseInput = false };
            Add(frame);
            _touchActions.Add(frame);
            Add(button);
            _touchActions.Add(button);
        }
    }

    private bool TouchAction(int id)
    {
        switch (id)
        {
            case 100: Dispose(); break;
            case 101: _moreTouchActions = !_moreTouchActions; BuildTouchActions(); break;
            case 102: GameActions.RequestProfile(LocalSerial); break;
            case 103: PartyManifest_MouseDoubleClickEvent(this, new MouseDoubleClickEventArgs(0, 0, GUO.Input.MouseButtonType.Left)); break;
            case 104: GameActions.ReplyGump(World.Player, 0x000001CD, 1, new[] { LocalSerial }, System.Array.Empty<System.Tuple<ushort, string>>()); break;
            case 105: GameActions.OpenAbilitiesBook(World); break;
            case 106: UIManager.Add(new RacialAbilitiesBookGump(World, X, Y)); break;
            case 107:
                var bag = World.Mobiles.Get(LocalSerial)?.FindItemByLayer(Layer.Backpack);
                if (bag != null) GameActions.DoubleClick(World, bag.Serial);
                break;
            default: return false;
        }
        return true;
    }
}
