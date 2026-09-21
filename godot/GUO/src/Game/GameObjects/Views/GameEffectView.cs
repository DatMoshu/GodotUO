using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Game.Scenes;
using GUO.Assets;
using GUO.Renderer;
using GUO.Compat;

namespace GUO.Game.GameObjects
{
    partial class GameEffect
    {
        private static readonly Lazy<BlendState> _multiplyBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Blend.Zero,
                    ColorDestinationBlend = Blend.SourceColor
                };

                return state;
            }
        );

        private static readonly Lazy<BlendState> _screenBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Blend.One,
                    ColorDestinationBlend = Blend.One
                };

                return state;
            }
        );

        private static readonly Lazy<BlendState> _screenLessBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Blend.DestinationColor,
                    ColorDestinationBlend = Blend.InverseSourceAlpha
                };

                return state;
            }
        );

        private static readonly Lazy<BlendState> _normalHalfBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Blend.DestinationColor,
                    ColorDestinationBlend = Blend.SourceColor
                };

                return state;
            }
        );

        private static readonly Lazy<BlendState> _shadowBlueBlendState = new Lazy<BlendState>
        (
            () =>
            {
                BlendState state = new BlendState
                {
                    ColorSourceBlend = Blend.SourceColor,
                    ColorDestinationBlend = Blend.InverseSourceColor,
                    ColorBlendFunction = BlendFunction.ReverseSubtract
                };

                return state;
            }
        );


        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (IsDestroyed || !AllowedToDraw)
            {
                return false;
            }

            if (AnimationGraphic == 0xFFFF)
            {
                return false;
            }

            ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[Graphic];

            posX += (int)Offset.X;
            posY += (int)(Offset.Z + Offset.Y);

            ushort hue = Hue;

            if (ProfileManager.CurrentProfile.NoColorObjectsOutOfRange && Distance > World.ClientViewRange)
            {
                hue = Constants.OUT_RANGE_COLOR;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
            }

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, data.IsPartialHue, data.IsTranslucent ? .5f : 1f, effect: true);

            if (Source != null)
            {
                depth = Source.CalculateDepthZ() + 1f;
            }

            switch (Blend)
            {
                case GraphicEffectBlendMode.Multiply:
                    batcher.SetBlendState(_multiplyBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        AnimationGraphic,
                        posX,
                        posY,
                        AngleToTarget,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                case GraphicEffectBlendMode.Screen:
                case GraphicEffectBlendMode.ScreenMore:
                    batcher.SetBlendState(_screenBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        AnimationGraphic,
                        posX,
                        posY,
                        AngleToTarget,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                case GraphicEffectBlendMode.ScreenLess:
                    batcher.SetBlendState(_screenLessBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        AnimationGraphic,
                        posX,
                        posY,
                        AngleToTarget,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                case GraphicEffectBlendMode.NormalHalfTransparent:
                    batcher.SetBlendState(_normalHalfBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        AnimationGraphic,
                        posX,
                        posY,
                        AngleToTarget,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                case GraphicEffectBlendMode.ShadowBlue:
                    batcher.SetBlendState(_shadowBlueBlendState.Value);

                    DrawStaticRotated
                    (
                        batcher,
                        AnimationGraphic,
                        posX,
                        posY,
                        AngleToTarget,
                        hueVec,
                        depth
                    );

                    batcher.SetBlendState(null);

                    break;

                default:
                    //if (Graphic == 0x36BD)
                    //{
                    //    hueVector = ShaderHueTranslator.GetHueVector(0);
                    //    HueVector.X = 0;
                    //    HueVector.Y = ShaderHueTranslator.SHADER_LIGHTS;
                    //    HueVector.Z = 0;
                    //    batcher.SetBlendState(BlendState.Additive);
                    //    base.Draw(batcher, posX, posY);
                    //    batcher.SetBlendState(null);
                    //}
                    //else

                    DrawStaticRotated
                    (
                        batcher,
                        AnimationGraphic,
                        posX,
                        posY,
                        AngleToTarget,
                        hueVec,
                        depth
                    );

                    break;
            }

            //Engine.DebugInfo.EffectsRendered++;


            if (data.IsLight && Source != null)
            {
                Client.Game.GetScene<GameScene>().AddLight(Source, Source, posX + 22, posY + 22);
            }

            return true;
        }

        public override bool CheckMouseSelection()
        {
            return false;
        }

    }
}
