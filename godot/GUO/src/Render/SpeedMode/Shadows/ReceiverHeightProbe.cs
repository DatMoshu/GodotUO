// SPDX-License-Identifier: BSD-2-Clause
// Headless pure-C# proof for the receiver helpers and the cloud daylight cycle.
using System;
using Godot;
using GUO.Renderer.SpeedMode.Weather;

namespace GUO.Renderer.SpeedMode.Shadows;

public partial class ReceiverHeightProbe : Node
{
    public override void _Ready()
    {
        try
        {
            int geometry = ReceiverGeometryProbe.Run();
            int structure = ReceiverStructureProbe.Run();
            CloudLighting.CheckCycle();
            int reference = 0;
#if DEBUG
            reference = ReceiverReferenceProbe.Run();
#endif
            var card = new NativeShadowQuad(null, new(10, 20), new(18, 20), new(10, 36),
                new(0, 0), new(1, 0), new(0, 1));
            if (!card.TryKeepAbove(40, out var whole) || whole.C != card.C)
                throw new InvalidOperationException("Fully exposed upright must stay whole");
            if (card.TryKeepAbove(12, out _))
                throw new InvalidOperationException("Fully sheltered upright must not stamp");
            if (!card.TryKeepAbove(28, out var crown) || Math.Abs(crown.C.Y - 28) > 0.001f)
                throw new InvalidOperationException("Partial upright must end on the cover plane");
            GD.Print($"[receiver-height] PASS geometry={geometry} structure={structure} reference={reference} clip=pass daylight=pass");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("[receiver-height] FAIL " + error);
            GetTree().Quit(1);
        }
    }
}
