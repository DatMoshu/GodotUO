// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: the steps the classic client shows its
// LoadingGump for (LoginScene.GetLoadingScreen), as the chest.

using Godot;
using GUO.Game.Scenes;
using GUO.Input.Touch;
using GUO.Resources;

namespace GUO.Pregame3D;

/// <summary>
/// Connecting, verifying, logging into the shard, entering Britannia,
/// creating a character, and a server's message: the lid half closes, the
/// candles gutter, and the status reads as the LoadingGump would.
/// </summary>
internal sealed class StatusStage : Stage
{
    private PanelContainer _card;
    private Label _text;
    private LoginSteps _step;
    private string _message;

    public override string Hints => _step switch
    {
        LoginSteps.PopUpMessage => "A  OK",
        LoginSteps.Connecting or LoginSteps.VerifyingAccount => "B  Cancel",
        _ => "",
    };

    public override void Enter()
    {
        _card = Overlay.Card(Overlay.Parchment);
        _text = Overlay.Text("", UoTheme.Ink, wrap: true);
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        _text.CustomMinimumSize = new Vector2(280, 0);
        _card.AddChild(_text);
        D.OverlayRoot.AddChild(_card);
        _card.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _card.GrowHorizontal = Control.GrowDirection.Both;
        _card.GrowVertical = Control.GrowDirection.Begin;
        _card.OffsetBottom = -40;

        StepChanged(Login.CurrentLoginStep);
    }

    public override void StepChanged(LoginSteps step)
    {
        _step = step;
        bool waiting = step != LoginSteps.PopUpMessage;
        D.LidTo(waiting ? 0.5f : 0.85f, 0.7);
        D.Scene.SetGutter(waiting ? 0.55f : 0.8f);

        if (step == LoginSteps.PopUpMessage)
        {
            _message = Login.PopupMessage;
        }

        _text.Text = Text(step);
    }

    public override void Update(double delta)
    {
        // A reconnect rewrites the message in place (LoginScene.OnNetClientDisconnected).
        if (_step == LoginSteps.PopUpMessage && !string.IsNullOrEmpty(Login.PopupMessage) && Login.PopupMessage != _message)
        {
            _message = Login.PopupMessage;
            _text.Text = _message;
        }
    }

    public override void Exit()
    {
        _card?.QueueFree();
        _card = null;
        D.Scene.SetGutter(1f);
    }

    public override bool Command(PadCmd cmd)
    {
        switch (cmd)
        {
            case PadCmd.A or PadCmd.Start when _step == LoginSteps.PopUpMessage:
            case PadCmd.B when _step is LoginSteps.PopUpMessage or LoginSteps.Connecting or LoginSteps.VerifyingAccount:
                // As the LoadingGump's OK / Cancel.
                Login.StepBack();
                return true;
        }

        return true; // nothing to focus here
    }

    private string Text(LoginSteps step)
    {
        var clilocs = Client.Game.UO.FileManager.Clilocs;

        return step switch
        {
            LoginSteps.PopUpMessage => string.IsNullOrEmpty(_message) ? "No Text" : _message,
            LoginSteps.Connecting => clilocs.GetString(3000002, ResGeneral.Connecting),
            LoginSteps.VerifyingAccount => clilocs.GetString(3000003, ResGeneral.VerifyingAccount),
            LoginSteps.LoginInToServer => clilocs.GetString(3000053, ResGeneral.LoggingIntoShard),
            LoginSteps.EnteringBritania => clilocs.GetString(3000001, ResGeneral.EnteringBritannia),
            LoginSteps.CharacterCreationDone => ResGeneral.CreatingCharacter,
            _ => "",
        };
    }
}
