// SPDX-License-Identifier: BSD-2-Clause
using Godot;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    // PORT DEVIATION (GUO): ADR-0048. Match the camera's existing walking and
    // peek offsets, then select the integer tile containing its centre.
    // Snapshot once before capture; every consumer gets that same origin.
    private Vector2I SpeedModeCameraOrigin()
    {
        var player=_world.Player;
        var peek=Camera.Offset;
        return Renderer.SpeedMode.SpeedModeBindings.CameraOrigin(player.X,player.Y,
            player.Offset.X,player.Offset.Y,peek.X,peek.Y,Camera.Zoom);
    }
}
