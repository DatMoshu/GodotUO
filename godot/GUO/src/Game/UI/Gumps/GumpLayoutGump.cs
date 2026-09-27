// SPDX-License-Identifier: BSD-2-Clause
using GUO.Game.UI.Controls;
using GUO.Input.Touch;
using GUO.Platform.Android;

namespace GUO.Game.UI.Gumps;

/// <summary>Unscaled, touch-sized alternatives to pinch; also works with an ordinary mouse.</summary>
internal sealed class GumpLayoutGump : Gump
{
    private readonly Gump _owner;
    private readonly Label _title;
    private readonly NiceButton _lock, _screen;
    public GumpLayoutGump(Gump owner) : base(owner.World, 0, 0)
    {
        _owner = owner;
        CanMove = true;
        AcceptMouseInput = true;
        CanCloseWithEsc = true;
        Width = 240; Height = 244;
        X = owner.X; Y = owner.Y;
        Add(new AlphaBlendControl(0.95f) { Width = Width, Height = Height });
        Add(_title = new Label("Window size / screen", true, 0x03b2, 220, 1) { X = 10, Y = 8 });
        AddButton(1, "Smaller", 10, 36, 105);
        AddButton(2, "Larger", 125, 36, 105);
        AddButton(3, "Reset size (100%)", 10, 76, 220);
        _lock = AddButton(4, "Lock pinch size", 10, 116, 220);
        _screen = AddButton(5, "Send to second screen", 10, 156, 220);
        AddButton(6, "Done", 10, 196, 220);
        GumpPresentation.Clamp(this);
        Sync();
    }

    private NiceButton AddButton(int id, string label, int x, int y, int width)
        => Add(new NiceButton(x, y, width, 36, ButtonAction.Activate, label, 1) { ButtonParameter = id });

    private void Sync()
    {
        _title.Text = $"Window size: {_owner.PresentationScale:P0}";
        _lock.TextLabel.Text = _owner.PresentationLocked ? "Unlock pinch size" : "Lock pinch size";
        _screen.TextLabel.Text = DualScreen.ShelfOn
            ? (GumpPresentation.OnSecond(_owner) ? "Send to main screen" : "Send to second screen")
            : "Second screen unavailable";
        _screen.IsEnabled = DualScreen.ShelfOn;
    }

    public override void Update()
    {
        if (_owner.IsDisposed) { Dispose(); return; }
        Sync();
        base.Update();
    }

    public override void OnButtonClick(int id)
    {
        if (_owner.IsDisposed) { Dispose(); return; }
        var anchor = new GUO.Compat.Point(_owner.X, _owner.Y);
        switch (id)
        {
            case 1: Resize(-0.25f, anchor); break;
            case 2: Resize(0.25f, anchor); break;
            case 3: GumpPresentation.Reset(_owner); break;
            case 4: _owner.PresentationLocked = !_owner.PresentationLocked; break;
            case 5: GumpPresentation.Transfer(_owner); break;
            case 6: Dispose(); break;
        }
        Sync();
    }

    private void Resize(float delta, GUO.Compat.Point anchor)
    {
        bool locked = _owner.PresentationLocked;
        _owner.PresentationLocked = false;
        GumpPresentation.SetScale(_owner, _owner.PresentationScale + delta, anchor);
        _owner.PresentationLocked = locked;
    }
}
