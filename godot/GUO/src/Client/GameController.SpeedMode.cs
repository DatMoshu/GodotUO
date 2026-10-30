// SPDX-License-Identifier: BSD-2-Clause
namespace GUO;

internal sealed partial class GameController
{
    // PORT DEVIATION (GUO): ADR-0048 scene lifetime, without exposing the shared batcher.
    internal void RetireSpeedScene() => _uoSpriteBatch?.RetireSpeedScene();
}
