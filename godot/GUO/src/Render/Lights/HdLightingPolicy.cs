// SPDX-License-Identifier: BSD-2-Clause
// Adapted from MeesaJarJar 2259b5f7; optional lighting policy (ADR-0027).
using System.Collections.Generic;

namespace GUO.Renderer.Lights;

internal static class HdLightingPolicy
{
    internal static bool Enabled(bool requested, bool packReady, bool dead) => requested && packReady && !dead;
        // PORT DEVIATION (GUO): unlit fixtures must emit nothing. Tiledata
        // flags both states of some fixtures as LightSource (candle 0x0A28
        // unlit + 0x0A0F lit, wall sconce 0x09FB unlit + 0x09FD lit, heating
        // stand 0x1849 unlit + 0x184A lit), and ModernUO keeps Light=Circle...
        // even when Burning=false (HeatingStand alone clears to Empty), so
        // neither IsLight nor the server's LightID distinguishes lit vs unlit.
        // Upstream ClassicUO checks IsLight only and glows unlit candles too.
        // The graphics below are ModernUO BaseLight UnlitItemIDs: they never
        // emit, even when flagged IsLight. Lit ones still glow via the normal
        // Layer/LightID/LightIndex path. Day visibility is untouched: this only
        // gates the light buffer (classic + realistic + HD share _lights via
        // AddLight), never drawing.
        private static readonly HashSet<ushort> _unlitFixtureGraphics = new()
        {
            0x142F, 0x1433, 0x1849, 0x24BC, 0x24BE, 0x24CA,
            0x24C2, 0x24C4, 0x24C6, 0x24C8,
            0x0A1D, 0x0A26, 0x0A27, 0x0A28, 0x0A29,
            0x0A18, 0x0A25, 0x0B21, 0x0B23, 0x0B25, 0x0F6B,
            0x09FB, 0x0A00, 0x0A05, 0x0A0A, 0x1853, 0x1857,
        };

        internal static bool IsUnlitFixture(ushort graphic) => _unlitFixtureGraphics.Contains(graphic);

}
