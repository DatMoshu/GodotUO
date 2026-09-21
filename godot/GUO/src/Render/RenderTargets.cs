// SPDX-License-Identifier: BSD-2-Clause
//
// Ported from ClassicUO's ClassicUO.Renderer/RenderTargets.cs (BSD-2-Clause).
// The structure, the field names and the composition order are upstream's.
// Two things could not come across and are marked PORT DEVIATION below.

using System;
using Godot;
using GUO.Compat;

namespace GUO.Renderer
{
    public class RenderTargets
    {
        private RenderTarget2D _uiRenderTarget;
        private RenderTarget2D _lightRenderTarget;
        private RenderTarget2D _worldRenderTarget;

        private Rectangle _gameWindowOnScreen;
        private Rectangle _gameWindowAfterDPI;
        private Rectangle _gameWorldSceneOnScreen;
        private Rectangle _gameWorldSceneAfterDPI;

        private Func<Vector3> _lightsHue;
        private Func<BlendState> _lightsBlendState;

        private Texture2D _background;
        private SamplerState _defaultSamplerState;

        public RenderTarget2D UiRenderTarget { get => _uiRenderTarget; }
        public RenderTarget2D LightRenderTarget { get => _lightRenderTarget; }
        public RenderTarget2D WorldRenderTarget { get => _worldRenderTarget; }

        public void SetLightsConfiguration(Func<BlendState> lightsBlendState, Func<Vector3> lightsHue)
        {
            _lightsBlendState = lightsBlendState;
            _lightsHue = lightsHue;
        }

        /// <remarks>
        /// PORT DEVIATION (GUO): upstream's first parameter is a
        /// <c>GraphicsDevice</c>, which is what an XNA render target is created
        /// against. A <see cref="RenderTarget2D"/> here is a
        /// <c>SubViewport</c>, which has to be in the scene tree to render at
        /// all, so what it needs instead is the node to hang under.
        /// </remarks>
        public void EnsureSizes(Node host, Rectangle gameWindowOnScreen, Rectangle gameWorldSceneAfterDPI, float dpiScale)
        {
            _gameWindowOnScreen = gameWindowOnScreen;
            _gameWindowAfterDPI = ScaleRectangle(gameWindowOnScreen, dpiScale);
            _gameWorldSceneOnScreen = ScaleRectangle(gameWorldSceneAfterDPI, 1/dpiScale);
            _gameWorldSceneAfterDPI = gameWorldSceneAfterDPI;

            EnsureSize(host, ref _uiRenderTarget, _gameWindowAfterDPI.Width, _gameWindowAfterDPI.Height);
            EnsureSize(host, ref _lightRenderTarget, _gameWorldSceneAfterDPI.Width, _gameWorldSceneAfterDPI.Height);
            EnsureSize(host, ref _worldRenderTarget, _gameWorldSceneAfterDPI.Width, _gameWorldSceneAfterDPI.Height);

            if (dpiScale == Math.Floor(dpiScale))
            {
                // Use PointClamp for integer DPI scaling to avoid blurriness
                _defaultSamplerState = SamplerState.PointClamp;
            }
            else
            {
                // Use LinearClamp for non-integer DPI scaling for smoother results
                _defaultSamplerState = SamplerState.LinearClamp;
            }
        }

        private static Rectangle ScaleRectangle(Rectangle gameWindowOnScreen, float dpiScale) => new(
                (int)(gameWindowOnScreen.X / dpiScale),
                (int)(gameWindowOnScreen.Y / dpiScale),
                (int)(gameWindowOnScreen.Width / dpiScale),
                (int)(gameWindowOnScreen.Height / dpiScale)
            );

        private static void EnsureSize(Node host, ref RenderTarget2D renderTarget, int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            if (renderTarget == null || renderTarget.IsDisposed || renderTarget.Width != width || renderTarget.Height != height)
            {
                renderTarget?.Dispose();

                // Upstream copies format, depth, multisample and usage off the
                // device's PresentationParameters. A SubViewport has none of
                // those to copy: it is always the canvas format, there is no
                // depth buffer on the 2D canvas, and the client asks for no
                // multisampling. Size is the whole of what varies.
                renderTarget = new RenderTarget2D(host, width, height);
            }
        }

        /// <summary>
        /// Frees the targets and the background art.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO): upstream's render targets die with the
        /// graphics device. These are Godot objects with RIDs behind them, and
        /// anything still holding one when the rendering server shuts down is
        /// reported as a leak, which puts noise in the log where real errors
        /// go. Called from the controller's teardown.
        /// </remarks>
        public void Dispose()
        {
            _uiRenderTarget?.Dispose();
            _uiRenderTarget = null;
            _lightRenderTarget?.Dispose();
            _lightRenderTarget = null;
            _worldRenderTarget?.Dispose();
            _worldRenderTarget = null;

            _background?.Dispose();
            _background = null;
        }

        public void InitializeBackground(Texture2D background)
        {
            _background = background ?? throw new ArgumentNullException(nameof(background));
        }

        public void Draw(UltimaBatcher2D batcher)
        {
            // draw world
            Vector3 fullAlphaNoColor = new Vector3(0, 0, 1);

            batcher.Begin();

            // PORT DEVIATION (GUO): upstream clears the back buffer to black
            // here, through batcher.GraphicsDevice. There is no device to reach
            // for, and nothing to clear: the tiled background immediately below
            // covers the whole window, which is why upstream draws it first.

            var rect = new Rectangle(
                0,
                0,
                _gameWindowOnScreen.Width,
                _gameWindowOnScreen.Height
            );
            batcher.DrawTiled(
                _background,
                rect,
                new Rectangle(0, 0, _background.GetWidth(), _background.GetHeight()),
                new Vector3(0, 0, 0.1f),
                0f
            );

            batcher.SetSampler(_defaultSamplerState);

            batcher.Draw(
                WorldRenderTarget,
                _gameWorldSceneOnScreen,
                fullAlphaNoColor,
                0f
            );

            // draw lights
            batcher.SetBlendState(_lightsBlendState?.Invoke());

            batcher.Draw(
                LightRenderTarget,
                _gameWorldSceneOnScreen,
                _lightsHue?.Invoke() ?? Vector3.Up,
                0f
            );

            batcher.SetBlendState(null);

            // Draw UI at original window size (render target is DPI-scaled but destination is not)
            batcher.Draw(
                UiRenderTarget,
                _gameWindowOnScreen,
                fullAlphaNoColor,
                0f
            );

            // Reset sampler to default
            batcher.SetSampler(null);
            batcher.End();
        }
    }
}
