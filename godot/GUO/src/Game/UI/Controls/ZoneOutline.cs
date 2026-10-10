// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using GUO.Game.Scenes;
using GUO.Renderer;
using GUO.Utility;
using GUO.Compat;

namespace GUO.Game.UI.Controls
{
    /// <summary>
    /// A transient dotted outline of a tile rectangle, reprojected every
    /// frame so it tracks the camera (the music zone gump's Show Zone).
    /// Self-expiring; dies with its gump.
    /// </summary>
    internal class ZoneOutline : Control
    {
        private int _facet = int.MinValue;
        private int _x0, _y0, _x1, _y1, _z;
        private DateTime _until = DateTime.MinValue;

        /// <summary>Tile rect, inclusive, at a flat height; shown for secs.</summary>
        public void Show(int facet, int x0, int y0, int x1, int y1, int z, double seconds = 20.0)
        {
            _facet = facet;
            _x0 = Math.Min(x0, x1);
            _y0 = Math.Min(y0, y1);
            _x1 = Math.Max(x0, x1);
            _y1 = Math.Max(y0, y1);
            _z = z;
            _until = DateTime.UtcNow.AddSeconds(seconds <= 0 ? 20.0 : seconds);
        }

        public void Hide()
        {
            _facet = int.MinValue;
            _until = DateTime.MinValue;
        }

        public bool Showing => _facet != int.MinValue && DateTime.UtcNow <= _until;

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            GameScene scene = Client.Game.GetScene<GameScene>();
            int facet = Client.Game.UO.World?.MapIndex ?? -2;
            if (Showing && scene != null && facet == _facet)
            {
                var corners = new Vector2[4];
                float xc = (_x0 + _x1 + 1) / 2f, yc = (_y0 + _y1 + 1) / 2f;
                corners[0] = TileTop(xc, _y0);
                corners[1] = TileRight(_x1 + 1, yc);
                corners[2] = TileBottom(xc, _y1 + 1);
                corners[3] = TileLeft(_x0, yc);
                bool ok = true;
                var pts = new Vector2[4];
                for (int i = 0; i < 4; i++)
                {
                    var wp = new Point((int)corners[i].X, (int)corners[i].Y);
                    Point sp = scene.Camera.WorldToScreen(wp, true);
                    pts[i] = new Vector2(sp.X - x, sp.Y - y);
                    if (sp.X < -100 || sp.Y < -100 || sp.X > 10000 || sp.Y > 10000)
                    {
                        ok = false;
                    }
                }

                if (ok)
                {
                    Godot.Texture2D dot = SolidColorTextureCache.GetTexture(new Color(255, 225, 50));
                    Godot.Texture2D hub = SolidColorTextureCache.GetTexture(new Color(255, 255, 255));
                    var uv = new Rectangle(0, 0, 1, 1);
                    Vector3 hueVector = ShaderHueTranslator.GetHueVector(0, false, 1);
                    renderLists.AddGumpWithAtlas
                    (
                        (batcher) =>
                        {
                            for (int e = 0; e < 4; e++)
                            {
                                Vector2 a = pts[e], b = pts[(e + 1) % 4];
                                float len = (b - a).Length();
                                int steps = Math.Max(1, (int)(len / 14f));
                                for (int s = 0; s <= steps; s++)
                                {
                                    Vector2 at = a + (b - a) * (s / (float)steps) - new Vector2(2, 2);
                                    batcher.Draw(dot, new Rectangle((int)at.X, (int)at.Y, 5, 5), uv, hueVector, layerDepth);
                                }

                                Vector2 c = pts[e] - new Vector2(4, 4);
                                batcher.Draw(hub, new Rectangle((int)c.X, (int)c.Y, 9, 9), uv, hueVector, layerDepth);
                            }

                            return true;
                        }
                    );
                }
            }

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }

        private Vector2 TileTop(float x, float y) => new Vector2((x - y) * 22f, (x + y) * 22f - (_z << 2));
        private Vector2 TileRight(float x, float y) => TileTop(x, y) + new Vector2(22f, 22f);
        private Vector2 TileBottom(float x, float y) => TileTop(x, y) + new Vector2(0f, 44f);
        private Vector2 TileLeft(float x, float y) => TileTop(x, y) + new Vector2(-22f, 22f);
    }
}
