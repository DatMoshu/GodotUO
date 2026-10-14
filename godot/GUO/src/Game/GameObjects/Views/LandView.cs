// SPDX-License-Identifier: BSD-2-Clause

using GUO.Configuration;
using GUO.Assets;
using GUO.Renderer;
using GUO.Compat;
using System;

namespace GUO.Game.GameObjects
{
    internal sealed partial class Land
    {
        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (!AllowedToDraw || IsDestroyed)
            {
                return false;
            }

            // PORT DEVIATION (GUO): pick up theme changes (see
            // GameObject.ResolveTheme); themed land draws the variant PNG.
            ResolveTheme();

            //Engine.DebugInfo.LandsRendered++;

            ushort hue = Hue;

            if (ProfileManager.CurrentProfile.HighlightGameObjects && SelectedObject.Object == this)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
            }
            else if (
                ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                && Distance > World.ClientViewRange
            )
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }

            Vector3 hueVec;
            if (hue != 0)
            {
                hueVec.X = hue - 1;
                hueVec.Y = IsStretched
                    ? ShaderHueTranslator.SHADER_LAND_HUED
                    : ShaderHueTranslator.SHADER_HUED;
            }
            else
            {
                hueVec.X = 0;
                hueVec.Y = IsStretched
                    ? ShaderHueTranslator.SHADER_LAND
                    : ShaderHueTranslator.SHADER_NONE;
            }
            hueVec.Z = 1f;

            if (IsStretched)
            {
                posY += Z << 2;

                ref readonly var texmapInfo = ref Client.Game.UO.Texmaps.GetTexmap(
                    Client.Game.UO.FileManager.TileData.LandData[Graphic].TexID
                );

                // PORT DEVIATION (GUO): stretched land is drawn by the chunk
                // mesh (ADR-0004), and UltimaBatcher2D.DrawStretchedLand
                // throws: a canvas quad cannot carry per-vertex normals. A
                // stretched tile that reaches here outside a mesh -- changed
                // without its chunk being rebuilt -- is drawn flat instead of
                // crashing the frame.
                if (texmapInfo.Texture != null && InChunkMesh)
                {
                    batcher.DrawStretchedLand(
                        texmapInfo.Texture,
                        new Vector2(posX, posY),
                        texmapInfo.UV,
                        ref YOffsets,
                        ref NormalTop,
                        ref NormalRight,
                        ref NormalLeft,
                        ref NormalBottom,
                        hueVec,
                        depth + 0.5f
                    );
                }
                else
                {
                    DrawStatic(
                        batcher,
                        Graphic,
                        posX,
                        posY,
                        hueVec,
                        depth,
                        ProfileManager.CurrentProfile.AnimatedWaterEffect && TileData.IsWet,
                        ThemedVariant
                    );
                }
            }
            else
            {
                ref readonly var artInfo = ref Client.Game.UO.Arts.GetLand(Graphic);

                // PORT DEVIATION (GUO): themed land draws the variant PNG.
                Godot.Texture2D texture = ThemedVariant?.Texture;
                Rectangle uv = default;
                if (texture != null)
                {
                    uv = new Rectangle(0, 0, ThemedVariant.Width, ThemedVariant.Height);
                }
                else
                {
                    if (artInfo.Texture == null)
                    {
                        return true;
                    }

                    texture = artInfo.Texture;
                    uv = artInfo.UV;
                }

                {
                    // PORT DEVIATION (GUO): Enhanced HD flat land. Native
                    // dest footprint at 1/k, chosen filter for HD only.
                    // Native art keeps Nearest; Auto uses nearest mipmaps
                    // unless HD scale equals the current zoom.
                    if (ThemedVariant == null && Client.Game.UO.Arts.TryGetHd(true, Graphic, out Godot.Texture2D hdTex, out Rectangle hdUv, out _, out _))
                    {
                        var pos = new Vector2(posX, posY);
                        var hdScale = new Vector2((float)uv.Width / hdUv.Width, (float)uv.Height / hdUv.Height);
                        if (ProfileManager.CurrentProfile.AnimatedWaterEffect && TileData.IsWet)
                        {
                            batcher.Draw(hdTex, pos, hdUv, hueVec, 0f, Vector2.Zero, hdScale, SpriteEffects.None, depth + 0.5f);
                            var sin = (float)Math.Sin(Time.Ticks / 1000f);
                            var cos = (float)Math.Cos(Time.Ticks / 1000f);
                            var wet = new Vector2(hdScale.X * (1.1f + sin * 0.1f), hdScale.Y * (1.1f + cos * 0.5f * 0.1f));
                            batcher.Draw(hdTex, pos, hdUv, hueVec, 0f, Vector2.Zero, wet, SpriteEffects.None, depth + 0.5f);
                        }
                        else
                        {
                            batcher.Draw(hdTex, pos, hdUv, hueVec, 0f, Vector2.Zero, hdScale, SpriteEffects.None, depth + 0.5f);
                        }

                        return true;
                    }

                    var pos2 = new Vector2(posX, posY);
                    var scale = Vector2.One;

                    if (ProfileManager.CurrentProfile.AnimatedWaterEffect && TileData.IsWet)
                    {
                        batcher.Draw(
                            texture,
                            pos2,
                            uv,
                            hueVec,
                            0f,
                            Vector2.Zero,
                            scale,
                            SpriteEffects.None,
                            depth + 0.5f
                        );

                        var sin = (float)Math.Sin(Time.Ticks / 1000f);
                        var cos = (float)Math.Cos(Time.Ticks / 1000f);
                        scale = new Vector2(1.1f + sin * 0.1f, 1.1f + cos * 0.5f * 0.1f);
                    }

                    batcher.Draw(
                        texture,
                        pos2,
                        uv,
                        hueVec,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        depth + 0.5f
                    );
                }
            }

            return true;
        }

        public override bool CheckMouseSelection()
        {
            if (IsStretched)
            {
                return SelectedObject.IsPointInStretchedLand(
                    ref YOffsets,
                    RealScreenPosition.X,
                    RealScreenPosition.Y + (Z << 2)
                );
            }

            return SelectedObject.IsPointInLand(RealScreenPosition.X, RealScreenPosition.Y);
        }
    }
}
