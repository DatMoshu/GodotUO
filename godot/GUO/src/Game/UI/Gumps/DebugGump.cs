// SPDX-License-Identifier: BSD-2-Clause

using GUO.Configuration;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Game.UI.Controls;
using GUO.Input;
using GUO.Renderer;
using GUO.Utility;
using GUO.Compat;
using System;
using System.Xml;

namespace GUO.Game.UI.Gumps
{
    internal class DebugGump : Gump
    {
        private const string DEBUG_STRING_0 = "- FPS: {0} (Min={1}, Max={2}), Zoom: {3:0.00}, Total Objs: {4}\n";
        private const string DEBUG_STRING_1 = "- Mobiles: {0}   Items: {1}   Statics: {2}   Multi: {3}   Lands: {4}   Effects: {5}\n";
        private const string DEBUG_STRING_2 = "- CharPos: {0}\n- Mouse: {1}\n- InGamePos: {2}\n";
        private const string DEBUG_STRING_3 = "- Selected: {0}\n";

        private const string DEBUG_STRING_SMALL = "FPS: {0}\nZoom: {1:0.00}";
        private const string DEBUG_STRING_SMALL_NO_ZOOM = "FPS: {0}";
        private static Point _last_position = new Point(-1, -1);

        private uint _timeToUpdate;
        private readonly AlphaBlendControl _alphaBlendControl;
        private string _cacheText = string.Empty;

        public DebugGump(World world, int x, int y) : base(world, 0, 0)
        {
            CanMove = true;
            CanCloseWithEsc = false;
            CanCloseWithRightClick = true;
            AcceptMouseInput = true;
            AcceptKeyboardInput = false;

            Width = 100;
            Height = 50;
            X = _last_position.X <= 0 ? x : _last_position.X;
            Y = _last_position.Y <= 0 ? y : _last_position.Y;

            Add
            (
                _alphaBlendControl = new AlphaBlendControl(.7f)
                {
                    Width = Width, Height = Height
                }
            );

            LayerOrder = UILayer.Over;

            WantUpdateSize = true;
        }

        public bool IsMinimized { get; set; }

        private long _statsAt, _rdDrawn, _rdFallback, _rdCalls, _rdQuads, _residentDraws;
        private ulong _godotFrames;
        private string _rdLine = "";

        /// <summary>Labs: the renderer's own counters, averaged over the last second.</summary>
        private void AppendLabsRenderStats(ref ValueStringBuilder sb)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            ulong frames = Godot.Engine.GetProcessFrames();
            if (now - _statsAt >= System.Diagnostics.Stopwatch.Frequency)
            {
                double seconds = (now - _statsAt) / (double)System.Diagnostics.Stopwatch.Frequency;
                double n = Math.Max(1, frames - _godotFrames);
                var capture = typeof(GUO.Renderer.DepthWorld.OrderedWorldCapture);
                long drawn = GUO.Renderer.DepthWorld.OrderedWorldCapture.FramesDrawn, fallback = GUO.Renderer.DepthWorld.OrderedWorldCapture.FramesFallback;
                long calls = GUO.Renderer.DepthWorld.OrderedWorldCapture.DrawCalls, quads = GUO.Renderer.DepthWorld.OrderedWorldCapture.ReferencedQuads;
                long resident = GUO.Renderer.DepthWorld.DepthWorldResident.ResidentDraws;
                _rdLine =
                    $"- Render ({(DepthWorldProbe.Enabled ? "Depth World" : GUO.Renderer.DepthWorld.OrderedWorldCapture.Enabled ? "RD ordered" : "canvas")})\n" +
                    $"    - Frame:{1000d * seconds / n:0.00}ms ({n / seconds:0} FPS) cap:{(Godot.Engine.MaxFps == 0 ? "none" : Godot.Engine.MaxFps.ToString())} vsync:{Godot.DisplayServer.WindowGetVsyncMode()}\n" +
                    $"    - RD frames:{(drawn - _rdDrawn) / n * 100:0}% fallback:{fallback - _rdFallback} last:{GUO.Renderer.DepthWorld.OrderedWorldCapture.LastFallback ?? "-"}\n" +
                    $"    - RD draws/frame:{(calls - _rdCalls) / n:0} resident:{(resident - _residentDraws) / n:0} ordered quads:{(quads - _rdQuads) / n:0}\n" +
                    $"    - Godot draws:{Godot.RenderingServer.GetRenderingInfo(Godot.RenderingServer.RenderingInfo.TotalDrawCallsInFrame)} prims:{Godot.RenderingServer.GetRenderingInfo(Godot.RenderingServer.RenderingInfo.TotalPrimitivesInFrame)} vram:{Godot.RenderingServer.GetRenderingInfo(Godot.RenderingServer.RenderingInfo.VideoMemUsed) >> 20}MB\n";
                _statsAt = now; _godotFrames = frames;
                _rdDrawn = drawn; _rdFallback = fallback; _rdCalls = calls; _rdQuads = quads; _residentDraws = resident;
            }
            sb.Append(_rdLine);
        }

        public override GumpType GumpType => GumpType.Debug;

        protected override bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
        {
            if (button == MouseButtonType.Left)
            {
                IsMinimized = !IsMinimized;

                return true;
            }

            return false;
        }

        public override void Update()
        {
            base.Update();

            if (IsDisposed)
            {
                return;
            }

            if (Time.Ticks > _timeToUpdate)
            {
                _timeToUpdate = Time.Ticks + 100;

                GameScene scene = Client.Game.GetScene<GameScene>();
                Span<char> span = stackalloc char[256];
                ValueStringBuilder sb = new ValueStringBuilder(span);

                if (IsMinimized && scene != null)
                {
                    sb.Append
                    (string.Format(
                         DEBUG_STRING_0,
                         CUOEnviroment.CurrentRefreshRate,
                         0,
                         0,
                         !World.InGame ? 1f : scene.Camera.Zoom,
                         scene.RenderedObjectsCount
                         )
                     );

                    sb.Append($"- CUO version: {CUOEnviroment.Version}, Client version: {Settings.GlobalSettings.ClientVersion}\n");

                    //_sb.AppendFormat(DEBUG_STRING_1, Engine.DebugInfo.MobilesRendered, Engine.DebugInfo.ItemsRendered, Engine.DebugInfo.StaticsRendered, Engine.DebugInfo.MultiRendered, Engine.DebugInfo.LandsRendered, Engine.DebugInfo.EffectsRendered);
                    sb.Append(string.Format(DEBUG_STRING_2, World.InGame ? $"{World.Player.X}, {World.Player.Y}, {World.Player.Z}" : "0xFFFF, 0xFFFF, 0", Mouse.Position, SelectedObject.Object is GameObject gobj ? $"{gobj.X}, {gobj.Y}, {gobj.Z}" : "0xFFFF, 0xFFFF, 0"));

                    sb.Append(string.Format(DEBUG_STRING_3, ReadObject(SelectedObject.Object)));

                    if (Profiler.Enabled)
                    {
                        double timeDraw = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME).TimeInContext;
                        double timeDrawWorld = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD).TimeInContext;
                        double timeDrawWorldPrepare = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE).TimeInContext;
                        double timeDrawUi = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME_UI).TimeInContext;

                        double timeUpdate = Profiler.GetContext(Profiler.ProfilerContext.UPDATE_WORLD).TimeInContext;

                        double timeOutOfContext = Profiler.GetContext(Profiler.ProfilerContext.OUT_OF_CONTEXT).TimeInContext;

                        //double timeTotalCheck = timeOutOfContext + timeDraw + timeUpdate;
                        double timeTotal = Profiler.TrackedTime;

                        double avgDrawMs = Profiler.GetContext(Profiler.ProfilerContext.RENDER_FRAME).AverageTime;

                        sb.Append("- Profiling\n");

                        sb.Append
                        (
                            $"    - Draw:{100d * (timeDraw / timeTotal):0.0}% (Prepare:{100d * (timeDrawWorldPrepare / timeTotal):0.0}% World:{100d * (timeDrawWorld / timeTotal):0.0}%, UI:{100d * (timeDrawUi / timeTotal):0.0}%)\n" +
                            $"    - Update:{100d * (timeUpdate / timeTotal):0.0}%\n" +
                            $"    - AvgDraw:{avgDrawMs:0.0}ms {CUOEnviroment.CurrentRefreshRate} FPS\n"
                        );
                    }
                    AppendLabsRenderStats(ref sb);
                }
                else
                {
                    var cameraZoomCount = (int)((scene.Camera.ZoomMax - scene.Camera.ZoomMin) / scene.Camera.ZoomStep);
                    var cameraZoomIndex = cameraZoomCount - (int)((scene.Camera.ZoomMax - scene.Camera.Zoom) / scene.Camera.ZoomStep);

                    if (scene != null && cameraZoomIndex != 5)
                    {
                        sb.Append(string.Format(DEBUG_STRING_SMALL, CUOEnviroment.CurrentRefreshRate, !World.InGame ? 1f : scene.Camera.Zoom));
                    }
                    else
                    {
                        sb.Append(string.Format(DEBUG_STRING_SMALL_NO_ZOOM, CUOEnviroment.CurrentRefreshRate));
                    }
                }


                _cacheText = sb.ToString();

                sb.Dispose();

                Vector2 size = Fonts.Bold.MeasureString(_cacheText);

                _alphaBlendControl.Width = Width = (int) (size.X + 20);
                _alphaBlendControl.Height = Height = (int) (size.Y + 20);

                WantUpdateSize = true;
            }
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            if (!base.AddToRenderLists(renderLists, x, y, ref layerDepthRef))
            {
                return false;
            }
            float layerDepth = layerDepthRef;

            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);

            renderLists.AddGumpNoAtlas(
                batcher =>
                {
                    batcher.DrawString
                    (
                        Fonts.Bold,
                        _cacheText,
                        x + 10,
                        y + 10,
                        hueVector,
                        layerDepth
                    );
                    return true;
                }
            );
            return true;
        }

        private string ReadObject(BaseGameObject obj)
        {
            if (obj != null && IsMinimized)
            {
                switch (obj)
                {
                    case Mobile mob: return $"Mobile (0x{mob.Serial:X8})  graphic: 0x{mob.Graphic:X4}  flags: {mob.Flags}  noto: {mob.NotorietyFlag}";

                    case Item item: return $"Item (0x{item.Serial:X8})  graphic: 0x{item.Graphic:X4}  flags: {item.Flags}  amount: {item.Amount} itemdata: {item.ItemData.Flags}";

                    case Static st: return $"Static (0x{st.Graphic:X4})  height: {st.ItemData.Height}  flags: {st.ItemData.Flags}  Alpha: {st.AlphaHue}";

                    case Multi multi: return $"Multi (0x{multi.Graphic:X4})  height: {multi.ItemData.Height}  flags: {multi.ItemData.Flags}";

                    case GameEffect effect: return "GameEffect";

                    case TextObject overhead: return $"TextOverhead type: {overhead.Type}  hue: 0x{overhead.Hue:X4}";

                    case Land land: return $"Land (0x{land.Graphic:X4})  flags: {land.TileData.Flags} stretched: {land.IsStretched}  avgZ: {land.AverageZ} minZ: {land.MinZ}";
                }
            }

            return string.Empty;
        }


        public override void Save(XmlTextWriter writer)
        {
            base.Save(writer);

            writer.WriteAttributeString("minimized", IsMinimized.ToString());
        }

        public override void Restore(XmlElement xml)
        {
            base.Restore(xml);

            bool.TryParse(xml.GetAttribute("minimized"), out bool b);
            IsMinimized = b;
        }

        protected override void OnDragEnd(int x, int y)
        {
            base.OnDragEnd(x, y);
            _last_position.X = ScreenCoordinateX;
            _last_position.Y = ScreenCoordinateY;
        }

        protected override void OnMove(int x, int y)
        {
            base.OnMove(x, y);

            _last_position.X = ScreenCoordinateX;
            _last_position.Y = ScreenCoordinateY;
        }
    }
}