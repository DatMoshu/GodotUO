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
        internal sealed class ZoneEntry
        {
            public int X0, Y0, X1, Y1, Z;
            public Color Color;
        }

        private int _facet = int.MinValue;
        private int _x0, _y0, _x1, _y1, _z;
        private List<ZoneEntry> _many;
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
            _many = null;
            _until = DateTime.UtcNow.AddSeconds(seconds <= 0 ? 20.0 : seconds);
        }

        /// <summary>Many tile rects at once (the gump's Show Zones), each in its own color.</summary>
        public void ShowMany(int facet, List<ZoneEntry> zones, double seconds = 20.0)
        {
            _facet = facet;
            _many = zones;
            _until = DateTime.UtcNow.AddSeconds(seconds <= 0 ? 20.0 : seconds);
        }

        public void Hide()
        {
            _facet = int.MinValue;
            _many = null;
            _until = DateTime.MinValue;
        }

        public bool ShowingSingle => _many == null && _facet != int.MinValue && DateTime.UtcNow <= _until;

        public bool ShowingMany => _many != null && _many.Count != 0 && _facet != int.MinValue && DateTime.UtcNow <= _until;

        public bool Showing => ShowingSingle || ShowingMany;

        /// <summary>A stable translucent color per zone name (FNV-1a name hash to hue).</summary>
        internal static Color ColorFor(string name)
        {
            uint h = 2166136261u;
            string s = string.IsNullOrEmpty(name) ? "zone" : name;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619u;
            }

            return HsvToRgb((h % 360u), 0.65f, 1f);
        }

        private static Color HsvToRgb(float h, float s, float v)
        {
            float c = v * s;
            float x = c * (1f - Math.Abs((h / 60f % 2f) - 1f));
            float m = v - c;
            float r = 0f, g = 0f, b = 0f;
            if (h < 60f)
            {
                r = c;
                g = x;
            }
            else if (h < 120f)
            {
                r = x;
                g = c;
            }
            else if (h < 180f)
            {
                g = c;
                b = x;
            }
            else if (h < 240f)
            {
                g = x;
                b = c;
            }
            else if (h < 300f)
            {
                r = x;
                b = c;
            }
            else
            {
                r = c;
                b = x;
            }

            return new Color((byte)((r + m) * 255f), (byte)((g + m) * 255f), (byte)((b + m) * 255f));
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            float layerDepth = layerDepthRef;
            GameScene scene = Client.Game.GetScene<GameScene>();
            int facet = Client.Game.UO.World?.MapIndex ?? -2;
            if (scene != null && facet == _facet)
            {
                if (ShowingSingle)
                {
                    Vector2[] pts = Project(scene, _x0, _y0, _x1, _y1, _z);
                    Godot.Texture2D dot = SolidColorTextureCache.GetTexture(new Color(255, 225, 50));
                    Godot.Texture2D hub = SolidColorTextureCache.GetTexture(new Color(255, 255, 255));
                    Vector3 hueVector = ShaderHueTranslator.GetHueVector(0, false, 1);
                    QueueDotted(renderLists, pts, dot, hub, hueVector, layerDepth);
                }
                else if (ShowingMany)
                {
                    var uv = new Rectangle(0, 0, 1, 1);
                    foreach (ZoneEntry e in _many)
                    {
                        int ex0 = Math.Min(e.X0, e.X1), ex1 = Math.Max(e.X0, e.X1);
                        int ey0 = Math.Min(e.Y0, e.Y1), ey1 = Math.Max(e.Y0, e.Y1);
                        Vector2[] pts = Project(scene, ex0, ey0, ex1, ey1, e.Z);
                        Godot.Texture2D dot = SolidColorTextureCache.GetTexture(e.Color);
                        Vector3 hueVector = ShaderHueTranslator.GetHueVector(0, false, 0.45f);
                        Vector2[] edge = pts;
                        renderLists.AddGumpWithAtlas
                        (
                            (batcher) =>
                            {
                                for (int k = 0; k < 4; k++)
                                {
                                    batcher.DrawLine(dot, edge[k], edge[(k + 1) % 4], hueVector, 3f, layerDepth);
                                }

                                for (int k = 0; k < 4; k++)
                                {
                                    Vector2 at = edge[k] - new Vector2(4, 4);
                                    batcher.Draw(dot, new Rectangle((int)at.X, (int)at.Y, 9, 9), uv, hueVector, layerDepth);
                                }

                                return true;
                            }
                        );
                    }
                }
            }

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }

        private static void QueueDotted(RenderLists renderLists, Vector2[] pts, Godot.Texture2D dot, Godot.Texture2D hub, Vector3 hueVector, float layerDepth)
        {
            var uv = new Rectangle(0, 0, 1, 1);
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

        /// <summary>
        /// Tile-rect corners to absolute client pixels. Subtracts the scene's
        /// player-follow offset first: Camera.WorldToScreen alone only zooms,
        /// so raw world pixels land tens of thousands of px off-screen (which
        /// is why Show Zone drew nothing). Draws at absolute coords: the gump
        /// pass has no per-gump transform at scale 1, so nothing is
        /// re-based onto the gump origin.
        /// </summary>
        private static Vector2[] Project(GameScene scene, int x0, int y0, int x1, int y1, int z)
        {
            float xc = (x0 + x1 + 1) / 2f, yc = (y0 + y1 + 1) / 2f;
            var corners = new Vector2[4];
            corners[0] = TileTop(xc, y0, z);
            corners[1] = TileTop(x1 + 1, yc, z) + new Vector2(22f, 22f);
            corners[2] = TileTop(xc, y1 + 1, z) + new Vector2(0f, 44f);
            corners[3] = TileTop(x0, yc, z) + new Vector2(-22f, 22f);
            Point off = scene.DrawOffset;
            var pts = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                var vp = new Point((int)corners[i].X - off.X, (int)corners[i].Y - off.Y);
                Point sp = scene.Camera.WorldToScreen(vp, true);
                pts[i] = new Vector2(sp.X, sp.Y);
            }

            return pts;
        }

        private static Vector2 TileTop(float x, float y, int z) => new Vector2((x - y) * 22f, (x + y) * 22f - (z << 2));
    }
}
