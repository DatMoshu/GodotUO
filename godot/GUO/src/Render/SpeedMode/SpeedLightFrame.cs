// SPDX-License-Identifier: BSD-2-Clause
using Godot;
using GUO.Renderer.Lights;

namespace GUO.Renderer.SpeedMode;

// Reused only after the Depth World ticket retires (same-thread admission).
internal sealed class SpeedLightFrame
{
    internal struct Entry
    {
        internal RealtimeLightField.Source Source;
        internal RealtimeLightField.SpeedReach Reach;
        internal Vector2 PixelCentre;
        internal float PixelRadius, Flicker;
        internal int ShadowRow;
    }
    internal readonly Entry[] Lights=new Entry[RealtimeLightEffects.FrameLightCap];
    internal int Count, Soften;
    internal Vector3 Ambient;
    internal float RoofShare;
    internal Vector2I Origin;
    // Borrowed RD views; default means no contribution from that optional pass.
    internal Rid LampShadowAtlas,SunMask;
    internal float ShadowSoftnessTenths=3;
    internal System.Action<RenderingDevice,SpeedLightFrame> RenderInputs;
    // Additive window patches run after the lamp list and before soften.
    internal System.Action<RenderingDevice,Rid,Rid,Vector2I,Vector2I> RenderPatches;
    internal UltimaBatcher2D Batcher;
    internal System.Action<UltimaBatcher2D,Texture2D,Vector4,Vector3> BindLightTarget;
    internal System.Action<UltimaBatcher2D> ClearLightTarget;
    internal static long GridTicks,ReachTicks;
}
