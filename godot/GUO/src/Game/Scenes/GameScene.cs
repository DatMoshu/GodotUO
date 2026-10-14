// SPDX-License-Identifier: BSD-2-Clause

using GUO.Assets;
using GUO.Configuration;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input;
using GUO.Network;
using GUO.Renderer;
using GUO.Renderer.Lights;
using GUO.Resources;
using GUO.Utility;
using GUO.Utility.Logging;
using GUO.Compat;
using Color = GUO.Compat.Color;
using Godot;
using GUO.Platform.Sdl;
using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace GUO.Game.Scenes
{
    internal partial class GameScene : Scene
    {
        private static readonly Func<BlendState> _darknessBlend = new(() =>
        {
            return new BlendState
            {
                ColorSourceBlend = Blend.Zero,
                ColorDestinationBlend = Blend.SourceColor,
                ColorBlendFunction = BlendFunction.Add
            };
        });

        private static readonly Func<BlendState> _altLightsBlend = new(() =>
        {
            return new BlendState
            {
                ColorSourceBlend = Blend.DestinationColor,
                ColorDestinationBlend = Blend.One,
                ColorBlendFunction = BlendFunction.Add
            };
        });

        private const float MAX_LAYER_DEPTH = 0x8000;
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

        private uint _time_cleanup = Time.Ticks + 5000;
        private bool _alphaChanged;
        private long _alphaTimer;
        private bool _forceStopScene;
        private HealthLinesManager _healthLinesManager;

        private Point _lastSelectedMultiPositionInHouseCustomization;
        private int _lightCount;
        private readonly LightData[] _lights = new LightData[
            LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT
        ];

        // PORT DEVIATION (GUO): realistic lights (PointLight2D + shadows in a
        // parallel world-sized viewport). Null until the toggle flips on;
        // off-state never builds it (pixel-identical). Hot-applies: built and
        // torn down here, no restart.
        private RealisticLightViewport _realisticLights;
        private readonly RealisticLightViewport.LightOffer[] _realisticOffers =
            new RealisticLightViewport.LightOffer[RealisticLightViewport.MaxLights];
        private readonly RealisticLightViewport.OccluderOffer[] _realisticOccluders =
            new RealisticLightViewport.OccluderOffer[RealisticLightViewport.MaxOccluders];
        private long _realisticLogAt;
        private int _lastRealisticOccluders;
        // PORT DEVIATION (GUO): HD light Phase 3 (sun + height buffer). The
        // buffer is never built unless an enabled pass declares height_tex
        // (hd_sun, or Phase 4 hd_gi for cascade occlusion);
        // off-state tears it down, pixel-identical.
        private HdHeightBuffer _hdHeight;
        private int _hdHeightFrame;
        private bool _hdSunBooted;
        private long _hdSunLastTicks;
        private long _hdSunLogAt;
        private int _lastHdStamps;
        private Item _multi;
        private Rectangle _rectangleObj = Rectangle.Empty,
            _rectanglePlayer;
        private long _timePing;

        private uint _timeToPlaceMultiInHouseCustomization;
        private readonly UseItemQueue _useItemQueue;
        private bool _useObjectHandles;
        private AnimatedStaticsManager _animatedStaticsManager;

        private readonly World _world;

        // Track the previously highlighted mesh sprite so we can restore its hue
        private GameObject _prevMeshHighlight;

        public GameScene(World world)
        {
            _world = world;
            _useItemQueue = new UseItemQueue(world);
        }

        public bool UpdateDrawPosition { get; set; }
        public bool DisconnectionRequested { get; set; }
        public bool UseLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseCustomLightLevel
                ? _world.Light.Personal < _world.Light.Overall
                : _world.Light.RealPersonal < _world.Light.RealOverall;
        public bool UseAltLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseAlternativeLights;
        public bool UseRealisticLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseRealisticLights;
        // PORT DEVIATION (GUO): HD light Phase 1 (HDR + flicker + hd_* PostFx).
        // A superset of the realistic viewport path; off never builds anything.
        // --hd-sun forces the foundation on for its run (scripted proof).
        public bool UseHDLight =>
            (ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseHDLight)
            || HdSun.ForceHd;
        // PORT DEVIATION (GUO): HD sun can darken the world (dusk/night)
        // while the custom light override says bright day (Absolute 0:
        // Personal 0 < Overall 0 is false, so UseLights is false and
        // AddLight culls every torch in ALL paths). The sun pass runs
        // before hd_tonemap folds the torch buffer back in, so a dark sun
        // still needs the torch offers even when the ambient says day.
        // Day sun (energy ~1.0+) needs nothing; dusk/night does.
        private bool HdSunNeedsLights =>
            UseHDLight
            && !HdSun.BootSilent
            && HdSun.Strength > 0f
            && HdSun.Evaluate(HdSun.Hour).Energy < 1.0f;
        private bool LightsNeeded => UseLights || UseAltLights || HdSunNeedsLights;

        public void DoubleClickDelayed(uint serial)
        {
            _useItemQueue.Add(serial);
        }

        public override void Load()
        {
            base.Load();

            Client.Game.Window.AllowUserResizing = true;

            Camera.Zoom = ProfileManager.CurrentProfile.DefaultScale;
            Camera.Bounds.X = Math.Max(0, ProfileManager.CurrentProfile.GameWindowPosition.X);
            Camera.Bounds.Y = Math.Max(0, ProfileManager.CurrentProfile.GameWindowPosition.Y);
            Camera.Bounds.Width = Math.Max(0, ProfileManager.CurrentProfile.GameWindowSize.X);
            Camera.Bounds.Height = Math.Max(0, ProfileManager.CurrentProfile.GameWindowSize.Y);

            Client.Game.UO.GameCursor.ItemHold.Clear();

            _world.Macros.Clear();
            _world.Macros.Load();
            _animatedStaticsManager = new AnimatedStaticsManager();
            _animatedStaticsManager.Initialize();
            _world.InfoBars.Load();
            _healthLinesManager = new HealthLinesManager(_world);

            _world.CommandManager.Initialize();
            // PORT DEVIATION (GUO): the -translate command.
            _world.CommandManager.Register("translate", args => Localization.JournalTranslationCommand.Run(_world, args));
            // PORT DEVIATION (GUO): HD light Phase 3, the -hd_sun clock command.
            _world.CommandManager.Register("hd_sun", HdSun.RunCommand);
            // PORT DEVIATION (GUO): HD light Phase 5, the -hd_weather test
            // command (rain/storm/snow/clear/flash overrides for screenshots
            // and probes; null follows the world).
            _world.CommandManager.Register("hd_weather", HdWeather.RunCommand);

            WorldViewportGump viewport = new WorldViewportGump(_world, this);
            UIManager.Add(viewport, false);

            if (!ProfileManager.CurrentProfile.TopbarGumpIsDisabled)
            {
                TopBarGump.Create(_world);
            }

            NetClient.Socket.Disconnected += SocketOnDisconnected;
            _world.MessageManager.MessageReceived += ChatOnMessageReceived;
            UIManager.ContainerScale = ProfileManager.CurrentProfile.ContainersScale / 100f;
            Data.MovementSpeed.FastRotation = ProfileManager.CurrentProfile.FastRotation;

            // PORT DEVIATION (GUO): SDL_SetWindowMinimumSize takes the window
            // handle; Godot's DisplayServer already knows which window.
            Godot.DisplayServer.WindowSetMinSize(new Godot.Vector2I(
                Client.Game.ScaleWithDpi(640), Client.Game.ScaleWithDpi(480)));

            // PORT DEVIATION (GUO): a size from the command line wins; see
            // GameController.PinnedWindowSize.
            if (Client.Game.SetPinnedWindowSize())
            {
            }
            else if (ProfileManager.CurrentProfile.WindowBorderless)
            {
                Client.Game.SetWindowBorderless(true);
            }
            else if (Settings.GlobalSettings.IsWindowMaximized)
            {
                Client.Game.MaximizeWindow();
            }
            else if (Settings.GlobalSettings.WindowSize.HasValue)
            {
                int w = Settings.GlobalSettings.WindowSize.Value.X;
                int h = Settings.GlobalSettings.WindowSize.Value.Y;

                w = Math.Max(Client.Game.ScaleWithDpi(640), w);
                h = Math.Max(Client.Game.ScaleWithDpi(480), h);

                Client.Game.SetWindowSize(w, h);
            }

            // Full-size also applies when login did not change the OS window
            // dimensions, so there was no resize notification to fill it.
            if (ProfileManager.CurrentProfile.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(Client.Game.Window.ClientBounds.Width, Client.Game.Window.ClientBounds.Height));
                viewport.SetGameWindowPosition(new Point(-WorldViewportGump.BORDER_WIDTH, -WorldViewportGump.BORDER_WIDTH));
            }

            Plugin.OnConnected();
        }

        private void ChatOnMessageReceived(object sender, MessageEventArgs e)
        {
            if (e.Type == MessageType.Command)
            {
                return;
            }

            string name;
            string text;

            ushort hue = e.Hue;

            switch (e.Type)
            {
                case MessageType.Regular:
                case MessageType.Limit3Spell:

                    if (e.Parent == null || !SerialHelper.IsValid(e.Parent.Serial))
                    {
                        name = ResGeneral.System;
                    }
                    else
                    {
                        name = e.Name;
                    }

                    text = e.Text;

                    break;

                case MessageType.System:
                case MessageType.GmChat:
                    name =
                        string.IsNullOrEmpty(e.Name)
                        || string.Equals(
                            e.Name,
                            "system",
                            StringComparison.InvariantCultureIgnoreCase
                        )
                            ? ResGeneral.System
                            : e.Name;

                    text = e.Text;

                    break;

                case MessageType.Emote:
                    name = e.Name;
                    text = $"{e.Text}";

                    if (e.Hue == 0)
                    {
                        hue = ProfileManager.CurrentProfile.EmoteHue;
                    }

                    break;

                case MessageType.Label:

                    if (e.Parent == null || !SerialHelper.IsValid(e.Parent.Serial))
                    {
                        name = string.Empty;
                    }
                    else if (string.IsNullOrEmpty(e.Name))
                    {
                        name = ResGeneral.YouSee;
                    }
                    else
                    {
                        name = e.Name;
                    }

                    text = e.Text;

                    break;

                case MessageType.Spell:
                    name = e.Name;
                    text = e.Text;

                    break;

                case MessageType.Party:
                    text = e.Text;
                    name = string.Format(ResGeneral.Party0, e.Name);
                    hue = ProfileManager.CurrentProfile.PartyMessageHue;

                    break;

                case MessageType.Alliance:
                    text = e.Text;
                    name = string.Format(ResGeneral.Alliance0, e.Name);
                    hue = ProfileManager.CurrentProfile.AllyMessageHue;

                    break;

                case MessageType.Guild:
                    text = e.Text;
                    name = string.Format(ResGeneral.Guild0, e.Name);
                    hue = ProfileManager.CurrentProfile.GuildMessageHue;

                    break;

                default:
                    text = e.Text;
                    name = e.Name;
                    hue = e.Hue;

                    Log.Warn($"Unhandled text type {e.Type}  -  text: '{e.Text}'");

                    break;
            }

            if (!string.IsNullOrEmpty(text))
            {
                // PORT DEVIATION (GUO): public mobile speech only. Private channels, labels,
                // system/cliloc text, spells and commands never reach the provider.
                bool translate = Localization.JournalTranslationPolicy.CanTranslate(e.Type, text,
                    e.TranslationEligible, e.Parent is Mobile, _world.IgnoreManager.IgnoredCharsList.Contains(name));
                _world.Journal.Add(text, hue, name, e.Parent?.Serial, e.TextType, e.IsUnicode, e.Type, translate);
            }
        }

        public override void Unload()
        {
            if (IsDestroyed)
            {
                return;
            }

            ProfileManager.CurrentProfile.GameWindowPosition = new Point(
                Camera.Bounds.X,
                Camera.Bounds.Y
            );
            ProfileManager.CurrentProfile.GameWindowSize = new Point(
                Camera.Bounds.Width,
                Camera.Bounds.Height
            );
            ProfileManager.CurrentProfile.DefaultScale = Camera.Zoom;

            Client.Game.Audio?.StopMusic();
            Client.Game.Audio?.StopSounds();

            Client.Game.SetWindowTitle(string.Empty);
            Client.Game.UO.GameCursor.ItemHold.Clear();

            try
            {
                Plugin.OnDisconnected();
            }
            catch { }

            _world.TargetManager.Reset();

            // special case for wmap. this allow us to save settings
            UIManager.GetGump<WorldMapGump>()?.SaveSettings();

            ProfileManager.CurrentProfile?.Save(_world, ProfileManager.ProfilePath);

            _world.Macros.Save();
            _world.Macros.Clear();
            _world.InfoBars.Save();
            ProfileManager.UnLoadProfile();

            StaticFilters.CleanCaveTextures();
            StaticFilters.CleanTreeTextures();

            NetClient.Socket.Disconnected -= SocketOnDisconnected;
            NetClient.Socket.Disconnect();

            _world.CommandManager.UnRegisterAll();
            _world.Weather.Reset();
            UIManager.Clear();
            _world.Clear();
            _world.ChatManager.Clear();
            _world.DelayedObjectClickManager.Clear();

            _useItemQueue?.Clear();
            _world.MessageManager.MessageReceived -= ChatOnMessageReceived;

            // PORT DEVIATION (GUO): a pinned size is not saved back.
            if (!GameController.PinnedWindowSize.HasValue)
            {
                Settings.GlobalSettings.WindowSize = new Point(
                    Client.Game.ClientBounds.Width,
                    Client.Game.ClientBounds.Height
                );

                Settings.GlobalSettings.IsWindowMaximized = Client.Game.IsWindowMaximized();
            }

            Client.Game.SetWindowBorderless(false);

            // PORT DEVIATION (GUO): realistic lights own Godot nodes; free
            // them with the scene so no viewport outlives it.
            _realisticLights?.Dispose();
            _realisticLights = null;

            // PORT DEVIATION (GUO): HD light Phase 3 owns a viewport too.
            _hdHeight?.Dispose();
            _hdHeight = null;
            GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex = null;

            base.Unload();
        }

        private void SocketOnDisconnected(object sender, SocketError e)
        {
            if (Settings.GlobalSettings.Reconnect)
            {
                _forceStopScene = true;
            }
            else
            {
                UIManager.Add(
                    new MessageBoxGump(
                        _world,
                        200,
                        200,
                        string.Format(
                            ResGeneral.ConnectionLost0,
                            StringHelper.AddSpaceBeforeCapital(e.ToString())
                        ),
                        s =>
                        {
                            if (s)
                            {
                                Client.Game.SetScene(new LoginScene(_world));
                            }
                        }
                    )
                );
            }
        }

        public void RequestQuitGame()
        {
            // PORT DEVIATION (GUO): the Thor saw this question appear with no
            // tap on LOG OUT (playability pass 2, bug 3); name the caller.
            if (GUO.Input.Touch.TouchInput.Enabled)
            {
                Log.Warn($"quit prompt requested by:{System.Environment.NewLine}{System.Environment.StackTrace}");
            }

            UIManager.Add(
                new QuestionGump(
                    _world,
                    ResGeneral.QuitPrompt,
                    s =>
                    {
                        if (s)
                        {
                            if (
                                (
                                    _world.ClientFeatures.Flags
                                    & CharacterListFlags.CLF_OWERWRITE_CONFIGURATION_BUTTON
                                ) != 0
                            )
                            {
                                DisconnectionRequested = true;
                                NetClient.Socket.Send_LogoutNotification();
                            }
                            else
                            {
                                NetClient.Socket.Disconnect();
                                Client.Game.SetScene(new LoginScene(_world));
                            }
                        }
                    }
                )
            );
        }

        public void AddLight(GameObject obj, GameObject lightObject, int x, int y)
        {
            if (
                _lightCount >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT
                || !LightsNeeded
                || obj == null
            )
            {
                return;
            }

            bool canBeAdded = true;

            int testX = obj.X + 1;
            int testY = obj.Y + 1;

            GameObject tile = _world.Map.GetTile(testX, testY);

            sbyte z5 = (sbyte)(obj.Z + 5);

            if (tile != null)
            {
                for (GameObject o = tile; o != null; o = o.TNext)
                {
                    if (
                        (!(o is Static s) || s.ItemData.IsTransparent)
                            && (!(o is Multi m) || m.ItemData.IsTransparent)
                        || !o.AllowedToDraw
                        // PORT DEVIATION (GUO): a hidden roof must not
                        // occlude. Roofs over the player fade to AlphaHue 0
                        // (ProcessAlpha, via _maxZ or _noDrawRoofs) yet stay
                        // AllowedToDraw, so without this every torch under a
                        // hidden roof reads as "under opaque roof" and is
                        // culled: rooms stay dark at night while the street
                        // outside lights up. Invisible occludes nothing.
                        || o.AlphaHue == 0
                    )
                    {
                        continue;
                    }

                    if (o.Z < _maxZ && o.Z >= z5)
                    {
                        canBeAdded = false;

                        break;
                    }
                }
            }

            if (canBeAdded)
            {
                // PORT DEVIATION (GUO): staged splat walls/roofs occlude like
                // statics (anchor tile, same Z window). Hidden victims stay
                // skipped above, so without this light leaks through MoGe
                // walls. Preview overlays never hide anything, so they never
                // block. Other facets never draw, so they never block.
                // Volumetric cover: one placement spans tiles past its
                // anchor, so the light's screen point inside its queued pick
                // rect covers those tiles too (empty rects fall back to the
                // anchor alone).
                foreach (SplatObject splat in _splatObjects.Values)
                {
                    // Same hidden rule as the static loop above: a faded
                    // splat (AlphaHue 0) is not drawn and occludes nothing.
                    if (splat == null || splat.IsDestroyed || !splat.AllowedToDraw || splat.AlphaHue == 0)
                    {
                        continue;
                    }

                    bool anchor = splat.X == testX && splat.Y == testY;
                    bool inside = false;
                    if (!anchor)
                    {
                        try
                        {
                            Rectangle r = splat.PickRect;
                            if (r.Width > 0 && r.Height > 0
                                && x >= r.X && x < r.X + r.Width
                                && y >= r.Y && y < r.Y + r.Height)
                            {
                                inside = true;
                            }
                        }
                        catch
                        {
                        }

                        if (!inside)
                        {
                            continue;
                        }
                    }

                    if (splat.Placement == null || splat.Placement.Facet != _world.MapIndex)
                    {
                        continue;
                    }

                    if (splat.Placement.Name != null && splat.Placement.Name.Contains("preview"))
                    {
                        continue;
                    }

                    bool transparent = false;

                    try
                    {
                        var statics = Client.Game.UO.FileManager.TileData.StaticData;

                        if (
                            splat.TryGetVictimGraphic(out ushort blockerGraphic)
                            && blockerGraphic != 0
                            && blockerGraphic < statics.Length
                        )
                        {
                            transparent = statics[blockerGraphic].IsTransparent;
                        }
                    }
                    catch
                    {
                    }

                    if (transparent)
                    {
                        continue;
                    }

                    if (splat.Z < _maxZ && splat.Z >= z5)
                    {
                        canBeAdded = false;

                        break;
                    }
                }
            }

            if (canBeAdded)
            {
                ref LightData light = ref _lights[_lightCount];

                ushort graphic = lightObject.Graphic;

                // PORT DEVIATION (GUO): splats carry Graphic 0x0001; the
                // victim's graphic decides the light shape below and the tint
                // after it, so a splat replacing a lamp keeps its lamp light.
                ushort lightGraphic = graphic;

                if (
                    lightObject is SplatObject splatGraphic
                    && splatGraphic.TryGetVictimGraphic(out ushort victimGraphic)
                    && victimGraphic != 0
                )
                {
                    lightGraphic = victimGraphic;
                }

                // PORT DEVIATION (GUO): unlit fixtures emit nothing in all
                // paths (classic + realistic + HD share _lights via this
                // method). See _unlitFixtureGraphics: IsLight is true on some
                // unlit graphics (0x0A28, 0x09FB, 0x1849) and the server keeps
                // LightID non-zero when doused, so neither gate distinguishes.
                if (IsUnlitFixture(lightGraphic))
                {
                    return;
                }

                if (
                    lightGraphic >= 0x3E02 && lightGraphic <= 0x3E0B
                    || lightGraphic >= 0x3914 && lightGraphic <= 0x3929
                    || lightGraphic == 0x0B1D
                )
                {
                    light.ID = 2;
                }
                else
                {
                    if (obj == lightObject && obj is Item item)
                    {
                        light.ID = item.LightID;
                    }
                    else if (lightObject is Item it)
                    {
                        light.ID = (byte)it.ItemData.LightIndex;

                        if (obj is Mobile mob)
                        {
                            switch (mob.Direction)
                            {
                                case Direction.Right:
                                    y += 33;
                                    x += 22;

                                    break;

                                case Direction.Left:
                                    y += 33;
                                    x -= 22;

                                    break;

                                case Direction.East:
                                    x += 22;
                                    y += 55;

                                    break;

                                case Direction.Down:
                                    y += 55;

                                    break;

                                case Direction.South:
                                    x -= 22;
                                    y += 55;

                                    break;
                            }
                        }
                    }
                    else if (obj is SplatObject splatObj)
                    {
                        // PORT DEVIATION (GUO): splats have no IsLight tiledata
                        // of their own, so the victim's tiledata decides. The
                        // victim's lamp light when it has one, else the
                        // standard point light (mobiles use 1): without this
                        // the static path below reads tile 0x0001's layer,
                        // which is no light at all, and lamp replacements
                        // would emit nothing.
                        byte id = 1;

                        try
                        {
                            var statics = Client.Game.UO.FileManager.TileData.StaticData;

                            if (
                                splatObj.TryGetVictimGraphic(out ushort splatVictim)
                                && splatVictim != 0
                                && splatVictim < statics.Length
                            )
                            {
                                byte v = statics[splatVictim].Layer;

                                if (v != 0 && v < LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT)
                                {
                                    id = v;
                                }
                            }
                        }
                        catch
                        {
                        }

                        light.ID = id;
                    }
                    else if (obj is Mobile _)
                    {
                        light.ID = 1;
                    }
                    else
                    {
                        ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[obj.Graphic];
                        light.ID = data.Layer;
                    }
                }

                light.Color = 0;
                light.IsHue = false;

                if (ProfileManager.CurrentProfile.UseColoredLights)
                {
                    if (light.ID > 200)
                    {
                        light.Color = (ushort)(light.ID - 200);
                        light.ID = 1;
                    }

                    if (LightColors.GetHue(lightGraphic, out ushort color, out bool ishue))
                    {
                        light.Color = color;
                        light.IsHue = ishue;
                    }
                }

                if (light.ID >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT)
                {
                    return;
                }

                if (light.Color != 0)
                {
                    light.Color++;
                }

                light.DrawX = x;
                light.DrawY = y;
                _lightCount++;
            }
        }

        private void FillGameObjectList()
        {
            _renderLists.Clear();
            _visibleChunks.Clear();

            _foliageCount = 0;

            if (!_world.InGame)
            {
                return;
            }

            _alphaChanged = _alphaTimer < Time.Ticks;

            if (_alphaChanged)
            {
                _alphaTimer = Time.Ticks + Constants.ALPHA_TIME;
            }

            if (ProfileManager.CurrentProfile.UseCircleOfTransparency)
            {
                float r = ProfileManager.CurrentProfile.CircleOfTransparencyRadius;
                _cotRadiusSq = r * r;
                _cotPlayerScreenPos = _world.Player.GetScreenPosition();
                _cotGradientMode = ProfileManager.CurrentProfile.CircleOfTransparencyType == 1;
            }
            else
            {
                _cotRadiusSq = 0;
                _cotGradientMode = false;
            }

            FoliageIndex++;

            if (FoliageIndex >= 100)
            {
                FoliageIndex = 1;
            }

            GetViewPort();

            var ctrlShiftHeld = Keyboard.Ctrl && Keyboard.Shift;
            var useObjectHandles = _world.NameOverHeadManager.IsToggled || ctrlShiftHeld;
            if (useObjectHandles != _useObjectHandles)
            {
                _useObjectHandles = useObjectHandles;
                if (_useObjectHandles)
                {
                    _world.NameOverHeadManager.Open();
                    if (_world.NameOverHeadManager.IsToggled && !ctrlShiftHeld)
                    {
                        _world.NameOverHeadManager.SetMenuVisible(false);
                    }
                }
                else
                {
                    _world.NameOverHeadManager.Close();
                }
            }
            else if (_useObjectHandles && _world.NameOverHeadManager.IsToggled)
            {
                _world.NameOverHeadManager.SetMenuVisible(ctrlShiftHeld);
            }

            _rectanglePlayer.X = (int)(
                _world.Player.RealScreenPosition.X
                - _world.Player.FrameInfo.X
                + 22
                + _world.Player.Offset.X
            );
            _rectanglePlayer.Y = (int)(
                _world.Player.RealScreenPosition.Y
                - _world.Player.FrameInfo.Y
                + 22
                + (_world.Player.Offset.Y - _world.Player.Offset.Z)
            );
            _rectanglePlayer.Width = _world.Player.FrameInfo.Width;
            _rectanglePlayer.Height = _world.Player.FrameInfo.Height;

            int minX = _minTile.X;
            int minY = _minTile.Y;
            int maxX = _maxTile.X;
            int maxY = _maxTile.Y;
            Map.Map map = _world.Map;
            bool use_handles = _useObjectHandles;
            (var minChunkX, var minChunkY) = (minX >> 3, minY >> 3);
            (var maxChunkX, var maxChunkY) = (maxX >> 3, maxY >> 3);

            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                for (var chunkY = minChunkY; chunkY <= maxChunkY; chunkY++)
                {
                    var chunk = map.GetChunk2(chunkX, chunkY, true);
                    if (chunk == null || chunk.IsDestroyed)
                        continue;

                    // Build chunk mesh if dirty
                    if (chunk.Mesh.IsDirty)
                    {
                        chunk.Mesh.Build(chunk, _world);
                    }

                    // Reset visibility and alpha for this frame
                    chunk.Mesh.Land.ResetVisibility();
                    chunk.Mesh.Land.ResetAlpha();
                    chunk.Mesh.Statics.ResetVisibility();
                    chunk.Mesh.Statics.ResetAlpha();

                    _visibleChunks.Add(chunk);

                    for (var x = 0; x < 8; x++)
                    {
                        for (var y = 0; y < 8; y++)
                        {
                            var firstObj = chunk.GetHeadObject(x, y);
                            if (firstObj == null || firstObj.IsDestroyed)
                                continue;

                            AddTileToRenderList(
                                firstObj,
                                use_handles,
                                150,
                                chunk
                            );
                        }
                    }
                }
            }

            if (_alphaChanged)
            {
                for (int i = 0; i < _foliageCount; i++)
                {
                    GameObject f = _foliages[i];

                    if (f.FoliageIndex == FoliageIndex)
                    {
                        CalculateAlpha(ref f.AlphaHue, Constants.FOLIAGE_ALPHA);
                    }
                    else if (f.Z < _maxZ)
                    {
                        CalculateAlpha(ref f.AlphaHue, 0xFF);
                    }
                }
            }

            UpdateTextServerEntities(_world.Mobiles.Values, true);
            UpdateTextServerEntities(_world.Items.Values, false);

            UpdateDrawPosition = false;
        }

        private void UpdateTextServerEntities<T>(IEnumerable<T> entities, bool force)
            where T : Entity
        {
            foreach (T e in entities)
            {
                if (
                    e.TextContainer != null
                    && !e.TextContainer.IsEmpty
                    && (force || e.Graphic == 0x2006)
                )
                {
                    e.UpdateRealScreenPosition(_offset.X, _offset.Y);
                }
            }
        }

        public override void Update()
        {
            Profile currentProfile = ProfileManager.CurrentProfile;

            SelectedObject.TranslatedMousePositionByViewport = Camera.MouseToWorldPosition();

            base.Update();

            if (_time_cleanup < Time.Ticks)
            {
                _world.Map?.ClearUnusedBlocks();
                _time_cleanup = Time.Ticks + 500;
            }

            PacketHandlers.SendMegaClilocRequests(_world);

            if (_forceStopScene)
            {
                LoginScene loginScene = new LoginScene(_world);
                Client.Game.SetScene(loginScene);
                loginScene.Reconnect = true;

                return;
            }

            if (!_world.InGame)
            {
                return;
            }

            if (Time.Ticks > _timePing)
            {
                NetClient.Socket.Statistics.SendPing();
                _timePing = (long)Time.Ticks + 1000;
            }

            _world.Update();
            // PORT DEVIATION (GUO): generated ambient music zones tick here.
            IO.Audio.MusicZones.Update(_world);
            IO.Audio.SfxZones.Update(_world);
            _animatedStaticsManager.Process();
            _world.BoatMovingManager.Update();
            _world.Player.Pathfinder.ProcessAutoWalk();
            _world.DelayedObjectClickManager.Update();

            if (!MoveCharacterByMouseInput() && !currentProfile.DisableArrowBtn)
            {
                Direction dir = DirectionHelper.DirectionFromKeyboardArrows(
                    _flags[0],
                    _flags[2],
                    _flags[1],
                    _flags[3]
                );

                if (_world.InGame && !_world.Player.Pathfinder.AutoWalking && dir != Direction.NONE)
                {
                    _world.Player.Walk(dir, currentProfile.AlwaysRun);
                }
            }

            if (
                _followingMode && SerialHelper.IsMobile(_followingTarget) && !_world.Player.Pathfinder.AutoWalking
            )
            {
                Mobile follow = _world.Mobiles.Get(_followingTarget);

                if (follow != null)
                {
                    int distance = follow.Distance;

                    if (distance > _world.ClientViewRange)
                    {
                        StopFollowing();
                    }
                    else if (distance > 3)
                    {
                        _world.Player.Pathfinder.WalkTo(follow.X, follow.Y, follow.Z, 1);
                    }
                }
                else
                {
                    StopFollowing();
                }
            }

            _world.Macros.Update();

            if (
                (currentProfile.CorpseOpenOptions == 1 || currentProfile.CorpseOpenOptions == 3)
                    && _world.TargetManager.IsTargeting
                || (currentProfile.CorpseOpenOptions == 2 || currentProfile.CorpseOpenOptions == 3)
                    && _world.Player.IsHidden
            )
            {
                _useItemQueue.ClearCorpses();
            }

            _useItemQueue.Update();

            if (!UIManager.IsMouseOverWorld)
            {
                SelectedObject.Object = null;
            }

            if (
                _world.TargetManager.IsTargeting
                && _world.TargetManager.TargetingState == CursorTarget.MultiPlacement
                && _world.CustomHouseManager == null
                && _world.TargetManager.MultiTargetInfo != null
            )
            {
                if (_multi == null)
                {
                    _multi = Item.Create(_world, 0);
                    _multi.Graphic = _world.TargetManager.MultiTargetInfo.Model;
                    _multi.Hue = _world.TargetManager.MultiTargetInfo.Hue;
                    _multi.IsMulti = true;
                }

                if (GameObjects.SplatObject.Resolve(SelectedObject.Object) is GameObject gobj)
                {
                    ushort x,
                        y;
                    sbyte z;

                    int cellX = gobj.X % 8;
                    int cellY = gobj.Y % 8;

                    GameObject o = _world.Map.GetChunk(gobj.X, gobj.Y)?.Tiles[cellX, cellY];

                    if (o != null)
                    {
                        x = o.X;
                        y = o.Y;
                        z = o.Z;
                    }
                    else
                    {
                        x = gobj.X;
                        y = gobj.Y;
                        z = gobj.Z;
                    }

                    _world.Map.GetMapZ(x, y, out sbyte groundZ, out sbyte _);

                    if (gobj is Static st && st.ItemData.IsWet)
                    {
                        groundZ = gobj.Z;
                    }

                    x = (ushort)(x - _world.TargetManager.MultiTargetInfo.XOff);
                    y = (ushort)(y - _world.TargetManager.MultiTargetInfo.YOff);
                    z = (sbyte)(groundZ - _world.TargetManager.MultiTargetInfo.ZOff);

                    _multi.SetInWorldTile(x, y, z);
                    _multi.CheckGraphicChange();

                    _world.HouseManager.TryGetHouse(_multi.Serial, out House house);

                    foreach (Multi s in house.Components)
                    {
                        s.IsHousePreview = true;
                        s.SetInWorldTile(
                            (ushort)(_multi.X + s.MultiOffsetX),
                            (ushort)(_multi.Y + s.MultiOffsetY),
                            (sbyte)(_multi.Z + s.MultiOffsetZ)
                        );
                    }
                }
            }
            else if (_multi != null)
            {
                _world.HouseManager.RemoveMultiTargetHouse();
                _multi.Destroy();
                _multi = null;
            }

            if (_isMouseLeftDown && !Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                if (
                    _world.CustomHouseManager != null
                    && _world.CustomHouseManager.SelectedGraphic != 0
                    && !_world.CustomHouseManager.SeekTile
                    && !_world.CustomHouseManager.Erasing
                    && Time.Ticks > _timeToPlaceMultiInHouseCustomization
                )
                {
                    if (
                        GameObjects.SplatObject.Resolve(SelectedObject.Object) is GameObject obj
                        && (
                            obj.X != _lastSelectedMultiPositionInHouseCustomization.X
                            || obj.Y != _lastSelectedMultiPositionInHouseCustomization.Y
                        )
                    )
                    {
                        _world.CustomHouseManager.OnTargetWorld(obj);
                        _timeToPlaceMultiInHouseCustomization = Time.Ticks + 50;
                        _lastSelectedMultiPositionInHouseCustomization.X = obj.X;
                        _lastSelectedMultiPositionInHouseCustomization.Y = obj.Y;
                    }
                }
                else if (Time.Ticks - _holdMouse2secOverItemTime >= 1000)
                {
                    if (GameObjects.SplatObject.Resolve(SelectedObject.Object) is Item it && GameActions.PickUp(_world, it.Serial, 0, 0))
                    {
                        _isMouseLeftDown = false;
                        _holdMouse2secOverItemTime = 0;
                    }
                }
            }
        }

        public override bool Draw(UltimaBatcher2D batcher, RenderTargets renderTargets)
        {
            if (!_world.InGame)
            {
                return false;
            }

            if (CheckDeathScreen(batcher))
            {
                return true;
            }

            // PORT DEVIATION (GUO): upstream saves the device viewport, sets
            // it to the camera's rectangle around the world draw, and restores
            // it. There is nothing to set: the world is drawn into a render
            // target that IS the camera's rectangle -- RenderTargets sizes it
            // from the same numbers -- and RenderTargets.Draw places it on
            // screen afterwards. Camera.GetViewportRect() is what the saved
            // value would have been set to, and nothing else reads it.
            Transform2D matrix = Camera.ViewTransform;

            bool can_draw_lights = false;

            can_draw_lights = PrepareLightsRendering(batcher, ref matrix, renderTargets);

            DrawWorld(batcher, ref matrix, renderTargets);

            // PORT DEVIATION (GUO): realistic snapshot is current-frame (after
            // DrawWorld fills _lights with statics + worn/held mobiles and
            // _visibleChunks for occluders). Prepare (before) only clears the
            // classic target so it adds nothing; the override is set here from
            // this frame's offers, including torch direction offsets.
            // PORT DEVIATION (GUO): HD light Phase 1 rides the same snapshot
            // (HDR viewport + flicker); either toggle builds the override.
            // LightsNeeded (not just UseLights): an HD dusk/night sun darkens
            // the world while Absolute 0 says bright day, and the torch must
            // still be offered or the holder goes dark.
            bool wantOverride = (UseRealisticLights || UseHDLight)
                && LightsNeeded
                && !(_world.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect);
            // The render targets + PostFx stack learn the HD flag every frame
            // so a hot-applied toggle recreates them once; off stays LDR.
            renderTargets.HdEnabled = UseHDLight;
            GUO.Renderer.PostFx.PostFxStack.Instance.ForceHdr = UseHDLight;
            if (wantOverride)
            {
                UpdateRealisticLights(renderTargets, ref matrix);
                // PORT DEVIATION (GUO): HD light Phase 3 rides the same frame
                // (sun clock + height buffer into the PostFx stack).
                UpdateHdSun(renderTargets, ref matrix);
                _lightCount = 0;
            }
            else if (UseHDLight
                && !(_world.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect))
            {
                // Sun still runs (clock + height + shadows) even when no
                // point light is needed (bright noon, Absolute 0): the
                // realistic override stays null (bright) from Prepare, but
                // the height buffer must still refill or day loses shadows
                // and the cycle freezes.
                UpdateHdSun(renderTargets, ref matrix);
                _lightCount = 0;
            }

            return base.Draw(batcher, renderTargets);
        }

        private void DrawWorld(UltimaBatcher2D batcher, ref Transform2D matrix, RenderTargets renderTargets)
        {
            batcher.SetRenderTarget(renderTargets.WorldRenderTarget);
            SelectedObject.Object = null;
            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE);
            FillGameObjectList();

            // PORT DEVIATION (GUO): while the pad's interact radar is up, the
            // thing it has chosen is what the world highlights, as if the
            // mouse were over it (Input.Gamepad.PadRadar); upstream has no pad.
            if (GUO.Input.Gamepad.PadRadar.Highlight is BaseGameObject padChoice)
            {
                SelectedObject.Object = padChoice;
            }

            // PORT DEVIATION (GUO): upstream highlights the selected object by
            // drawing it again on top with the highlight hue. A meshed object
            // (ADR-0004) is not drawn one by one, so its hue is written into
            // the chunk mesh's vertices instead and restored the next frame.
            //
            // Restore previous highlight's original hue before applying new one
            if (_prevMeshHighlight != null
                && _prevMeshHighlight.InChunkMesh
                && _prevMeshHighlight.MeshSpriteIndex >= 0)
            {
                var prevChunk = _world.Map.GetChunk(_prevMeshHighlight.X, _prevMeshHighlight.Y);
                if (prevChunk?.Mesh != null)
                {
                    if (_prevMeshHighlight.IsDestroyed)
                    {
                        // Its sprite index may already belong to something
                        // else, so do not write through it: rebuild the mesh,
                        // which writes every sprite's own hue.
                        prevChunk.Mesh.IsDirty = true;
                    }
                    else
                    {
                        var prevLayer = _prevMeshHighlight is Land ? prevChunk.Mesh.Land : prevChunk.Mesh.Statics;
                        ApplyMeshHue(_prevMeshHighlight, prevLayer);
                    }
                }
            }
            _prevMeshHighlight = null;

            // Apply highlight hue to mesh vertex for selected meshed object
            // (instead of redrawing it on top, which breaks z-order for overlapping objects)
            if (ProfileManager.CurrentProfile.HighlightGameObjects
                && SelectedObject.Object is GameObject selObj
                && selObj.InChunkMesh && selObj.MeshSpriteIndex >= 0)
            {
                var chunk = _world.Map.GetChunk(selObj.X, selObj.Y);
                if (chunk?.Mesh != null)
                {
                    var layer = selObj is Land ? chunk.Mesh.Land : chunk.Mesh.Statics;
                    float shaderType = selObj is Land land && land.IsStretched
                        ? ShaderHueTranslator.SHADER_LAND_HUED
                        : ShaderHueTranslator.SHADER_HUED;
                    layer.SetHue(
                        selObj.MeshSpriteIndex,
                        Constants.HIGHLIGHT_CURRENT_OBJECT_HUE - 1,
                        shaderType
                    );
                    _prevMeshHighlight = selObj;
                }
            }

            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE);
            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD);
            batcher.SetSampler(SamplerState.PointClamp);

            batcher.Begin(matrix);
            // PORT DEVIATION (GUO): realistic replaces (not stacks with) the
            // classic overlays. Terrain brightlight is the classic slope
            // shading in the world target; with the realistic viewport's
            // occluder shadows compositing as the light target, leaving it on
            // double-shadows every wall. Off while realistic or HD lights.
            batcher.SetBrightlight((UseRealisticLights || UseHDLight)
                ? 0f
                : ProfileManager.CurrentProfile.TerrainShadowsLevel * 0.1f);

            if (ProfileManager.CurrentProfile.UseCircleOfTransparency
                && ProfileManager.CurrentProfile.CircleOfTransparencyType != 1) // gradient mode uses CPU alpha, not shader
            {
                batcher.SetCircleOfTransparencyRadius(
                    (float)ProfileManager.CurrentProfile.CircleOfTransparencyRadius / Camera.Zoom
                );
                // PORT DEVIATION (GUO): upstream's shader measures from the
                // middle of the world render target through clip space; the
                // Godot shader measures from a uniform, so the middle is
                // handed over here, in the target's own pixels.
                var worldSize = renderTargets.GameWorldSceneAfterDPI;
                batcher.CircleOfTransparencyCenter = new Vector2(worldSize.Width * 0.5f, worldSize.Height * 0.5f);
            }
            else
            {
                batcher.SetCircleOfTransparencyRadius(0f);
            }

            // GUO: SetStencil is a no-op on the canvas batcher; draw order does
            // the depth work (ADR-0002). The call is kept to match upstream.
            // https://shawnhargreaves.com/blog/depth-sorting-alpha-blended-objects.html
            batcher.SetStencil(DepthStencilState.Default);

            // PORT DEVIATION (GUO): staged gaussian splats (ComfyUI multis),
            // queued in GameScene.Splats.cs so they sort, occlude and pick
            // with everything else. Nothing without GUO_SPLAT_STAGE.
            QueueStagedSplats();

            // PORT DEVIATION (GUO): generated terrain layers (ComfyUI
            // underlays/overlays). Overlays join the sorted stream at their
            // centre depth; underlays draw here, below even the land.
            QueueTerrainLayers();
            DrawUnderlays(batcher);

            RenderedObjectsCount = _renderLists.DrawRenderLists(
                batcher,
                _maxGroundZ,
                _visibleChunks,
                _offset.X,
                _offset.Y
            );



            if (
                _multi != null
                && _world.TargetManager.IsTargeting
                && _world.TargetManager.TargetingState == CursorTarget.MultiPlacement
            )
            {
                _multi.Draw(
                    batcher,
                    _multi.RealScreenPosition.X,
                    _multi.RealScreenPosition.Y,
                    _multi.CalculateDepthZ()
                );
            }

            // draw weather
            _world.Weather.Draw(batcher, 0, 0, MAX_LAYER_DEPTH - 1);

            DrawSelection(batcher, MAX_LAYER_DEPTH);

            batcher.SetSampler(null);
            batcher.SetStencil(null);
            // PORT DEVIATION (GUO): upstream clears the radius here, and an
            // XNA effect parameter only reaches the draws after it. A Godot
            // material parameter has one value per frame, read when the frame
            // renders, so clearing it here cleared it for the world drawn
            // above too, and the circle never showed. Nothing outside the
            // world sets the circle bit, so leaving the radius set is safe;
            // the branch above clears it when the circle is off.
            batcher.End();

            int flushes = batcher.FlushesDone;
            int switches = batcher.TextureSwitches;
            batcher.SetRenderTarget(null);
            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD);
        }

        private bool PrepareLightsRendering(UltimaBatcher2D batcher, ref Transform2D matrix, RenderTargets renderTargets)
        {
            InitializeRenderTargets(renderTargets);

            if (
                !LightsNeeded
                || _world.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect
            )
            {
                batcher.SetRenderTarget(renderTargets.LightRenderTarget);
                batcher.Clear(new Godot.Color(0, 0, 0, 0));
                batcher.SetRenderTarget(null);

                // Toggle still on but nothing to darken: stay bright like the
                // classic path. The viewport stays built; the flip owns that.
                if (_realisticLights != null)
                {
                    renderTargets.RealisticLightOverride = null;
                }

                return false;
            }

            // PORT DEVIATION (GUO): realistic/HD path. The classic target is
            // cleared so it adds nothing and the parallel viewport's texture
            // composites instead, with the same blend and hue. The snapshot
            // itself runs after DrawWorld (in Draw, above) so worn/held
            // mobiles (added during the sorted draw) and current _visibleChunks
            // are this frame's, not last frame's. Previous _lights are
            // discarded here so DrawWorld starts fresh.
            // HD shares the viewport (HDR + flicker); either toggle builds it.
            if (UseRealisticLights || UseHDLight)
            {
                batcher.SetRenderTarget(renderTargets.LightRenderTarget);
                batcher.Clear(new Godot.Color(0, 0, 0, 0));
                batcher.SetRenderTarget(null);

                _lightCount = 0;

                return true;
            }

            // Both toggles flipped off: tear the viewport down so the off-state
            // never builds it (pixel-identical to before this feature).
            if (_realisticLights != null)
            {
                _realisticLights.Teardown();
                _realisticLights = null;
                renderTargets.RealisticLightOverride = null;
            }

            // PORT DEVIATION (GUO): HD light Phase 3. Same rule for the
            // height buffer: off builds nothing, and the stack forgets it.
            if (_hdHeight != null)
            {
                _hdHeight.Dispose();
                _hdHeight = null;
            }

            GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex = null;
            _hdSunBooted = false;

            batcher.SetRenderTarget(renderTargets.LightRenderTarget);
            batcher.Clear(Godot.Colors.Black);

            if (!UseAltLights)
            {
                float lightColor = _world.Light.IsometricLevel;

                if (ProfileManager.CurrentProfile.UseDarkNights)
                {
                    lightColor -= 0.04f;
                }

                batcher.Clear(
                    new Godot.Color(lightColor, lightColor, lightColor, 1)
                );
            }

            batcher.Begin(matrix);
            batcher.SetBlendState(BlendState.Additive);

            Vector3 hue = Vector3.Zero;

            hue.Z = 1f;

            for (int i = 0; i < _lightCount; i++)
            {
                ref LightData l = ref _lights[i];
                ref readonly var lightInfo = ref Client.Game.UO.Lights.GetLight(l.ID);

                if (lightInfo.Texture == null)
                {
                    continue;
                }

                hue.X = l.Color;
                hue.Y =
                    hue.X > 1.0f
                        ? l.IsHue
                            ? ShaderHueTranslator.SHADER_HUED
                            : ShaderHueTranslator.SHADER_LIGHTS
                        : ShaderHueTranslator.SHADER_NONE;

                batcher.Draw(
                    lightInfo.Texture,
                    new Vector2(
                        l.DrawX - lightInfo.UV.Width * 0.5f,
                        l.DrawY - lightInfo.UV.Height * 0.5f
                    ),
                    lightInfo.UV,
                    hue,
                    0f
                );
            }

            _lightCount = 0;

            batcher.SetBlendState(null);
            batcher.End();

            batcher.SetRenderTarget(null);

            return true;
        }

        private void InitializeRenderTargets(RenderTargets renderTargets)
        {
            renderTargets.SetLightsConfiguration(
                UseAltLights ? _altLightsBlend : (UseLights ? _darknessBlend : () => null),
                () =>
                {
                    Vector3 v = Vector3.Zero;
                    v.Z = UseAltLights ? 0.5f : 1f;
                    return v;
                }
            );
        }

        /// <summary>
        /// One frame of the realistic/HD path: same offers as the classic loop
        /// above (same order, same colors), ambient from IsometricLevel.
        /// HD adds HDR (UseHdr2D) + per-light TIME flicker; realistic stays LDR.
        /// </summary>
        private void UpdateRealisticLights(RenderTargets renderTargets, ref Transform2D matrix)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (renderTargets.LightRenderTarget == null)
                {
                    renderTargets.RealisticLightOverride = null;

                    return;
                }

                int width = renderTargets.LightRenderTarget.Width;
                int height = renderTargets.LightRenderTarget.Height;

                if (width <= 0 || height <= 0)
                {
                    renderTargets.RealisticLightOverride = null;

                    return;
                }

                bool hd = UseHDLight;
                _realisticLights ??= new RealisticLightViewport(Client.Game.UO.FileManager.Lights);
                _realisticLights.EnsureSize(Client.Game, width, height, hd);
                _realisticLights.HdFlicker = hd;
                _realisticLights.TimeSeconds = GUO.Time.Ticks / 1000.0;
                // PORT DEVIATION (GUO): HD light Phase 5. Thunder timing drives
                // real ambient/flash energy (see RealisticLightViewport).
                _realisticLights.WeatherFlash = HdWeather.Flash;
                _realisticLights.WeatherDim = HdWeather.Dim;

                if (!_realisticLights.IsBuilt)
                {
                    renderTargets.RealisticLightOverride = null;

                    return;
                }

                int count = Math.Min(_lightCount, RealisticLightViewport.MaxLights);

                for (int i = 0; i < count; i++)
                {
                    ref LightData l = ref _lights[i];
                    _realisticOffers[i] = new RealisticLightViewport.LightOffer(
                        l.ID, l.Color, l.IsHue, l.DrawX, l.DrawY);
                }

                float ambient = _world.Light.IsometricLevel;

                if (ProfileManager.CurrentProfile.UseDarkNights)
                {
                    ambient -= 0.04f;
                }

                int shadowBudget = GetRealisticShadowBudget(count);
                int occluderBudget = GetRealisticOccluderBudget();
                int occluderCount = CollectRealisticOccluders(occluderBudget);
                _lastRealisticOccluders = occluderCount;

                _realisticLights.Update(
                    _realisticOffers, count,
                    _realisticOccluders, occluderCount,
                    ambient, matrix, shadowBudget);

                renderTargets.RealisticLightOverride = _realisticLights.Texture;
            }
            finally
            {
                watch.Stop();

                // Measured cost, one line every 10 s while the toggle is on:
                // lights, shadows allowed, occluders, and the CPU update time.
                // The viewport itself renders on the GPU; this is the offer +
                // occluder scan on the main thread.
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (now - _realisticLogAt > 10000)
                {
                    _realisticLogAt = now;
                    Log.Trace(
                        $"[realistic-lights] hd={UseHDLight} lights={Math.Min(_lightCount, RealisticLightViewport.MaxLights)} " +
                        $"shadows<={GetRealisticShadowBudget(Math.Min(_lightCount, RealisticLightViewport.MaxLights))} " +
                        $"occluders={_lastRealisticOccluders} " +
                        $"update={watch.Elapsed.TotalMilliseconds:F2}ms");
                }
            }
        }

        /// <summary>
        /// Zoom/device governor: how many of the pooled lights may cast
        /// shadows this frame. Mobile and web tiers allow fewer; extreme
        /// zooms halve the budget either way (large light rects cost more).
        /// Offer order decides who keeps a shadow: first N, same as _lights.
        /// </summary>
        private int GetRealisticShadowBudget(int lightCount)
        {
            bool mobile = PlatformDefaults.Platform != ProfilePlatform.Desktop;
            int budget = mobile ? 12 : 32;

            try
            {
                float zoom = Camera.Zoom;

                if (zoom < 0.6f || zoom > 1.0f)
                {
                    budget /= 2;
                }
            }
            catch
            {
            }

            return Math.Max(4, Math.Min(budget, RealisticLightViewport.MaxLights));
        }

        private int GetRealisticOccluderBudget()
        {
            bool mobile = PlatformDefaults.Platform != ProfilePlatform.Desktop;

            return mobile ? 128 : RealisticLightViewport.MaxOccluders;
        }

        /// <summary>
        /// Rect occluders from tiledata, reusing AddLight's occlusion
        /// predicates: skip transparent and undrawable victims, and keep the
        /// same Z window (below _maxZ) so eaves and floors above stop behaving
        /// like walls. Wall/Door/Roof/NoShoot tiles qualify; Height sizes the
        /// rect (1 Z = 4 px) AND the window: a wall's base sits at the light's
        /// own level, so the lower bound tests its top (Z + Height), not its
        /// base — testing the base like AddLight's diagonal-tile check does
        /// excludes every same-level wall and the night stays shadowless
        /// (measured: Britain street, 0 occluders before, full walls after).
        /// </summary>
        private int CollectRealisticOccluders(int budget)
        {
            int count = 0;

            try
            {
                if (_visibleChunks.Count == 0 || _world.Player == null)
                {
                    return 0;
                }

                int cap = Math.Min(budget, RealisticLightViewport.MaxOccluders);
                sbyte z5 = (sbyte)(_world.Player.Z + 5);
                var statics = Client.Game.UO.FileManager.TileData.StaticData;

                foreach (var chunk in _visibleChunks)
                {
                    if (chunk == null || chunk.IsDestroyed)
                    {
                        continue;
                    }

                    for (int x = 0; x < 8 && count < cap; x++)
                    {
                        for (int y = 0; y < 8 && count < cap; y++)
                        {
                            for (
                                GameObject obj = chunk.GetHeadObject(x, y);
                                obj != null && count < cap;
                                obj = obj.TNext)
                            {
                                // Invisible casts no shadow: hidden roofs (AlphaHue
                                // 0 via _maxZ/_noDrawRoofs) would otherwise
                                // shadow the very room the torch lights, the
                                // realistic form of the classic cull above.
                                if (obj.IsDestroyed || !obj.AllowedToDraw || obj.AlphaHue == 0)
                                {
                                    continue;
                                }

                                if (obj is Land || obj is Mobile)
                                {
                                    continue;
                                }

                                if (!(obj is Static) && !(obj is Multi) && !(obj is Item))
                                {
                                    continue;
                                }

                                ushort graphic = obj.Graphic;

                                if (graphic >= statics.Length)
                                {
                                    continue;
                                }

                                ref StaticTiles data = ref statics[graphic];

                                // Same Z window as AddLight's blockers, tested
                                // against the tile's top: a wall rises Height
                                // above its base, and its base is at the
                                // light's own level.
                                if (obj.Z + data.Height < z5 || obj.Z >= _maxZ)
                                {
                                    continue;
                                }

                                if (data.IsTransparent)
                                {
                                    continue;
                                }

                                if (!data.IsWall && !data.IsDoor && !data.IsRoof && !data.IsNoShoot && !data.IsWindow)
                                {
                                    continue;
                                }

                                // A wall that hosts its own torch must not
                                // shadow it: the PointLight2D sits inside the
                                // wall's rect occluder (same tile, torch
                                // Z-elevated so screen-distance checks miss)
                                // and Godot eclipses the whole room. Classic
                                // has no shadows, so carving this hole keeps
                                // the room lit like the classic path.
                                // Worn/held torches need the same hole: the
                                // light lives on the mobile's tile (floor),
                                // but torch direction offsets (+22/+55) can
                                // push it inside the neighbouring wall's rect.
                                // Same-tile statics alone miss that, so any
                                // current light offer inside this occluder's
                                // rect also carves it out.
                                bool hostsLight = false;

                                for (
                                    GameObject o = chunk.GetHeadObject(x, y);
                                    o != null;
                                    o = o.TNext)
                                {
                                    if (o.IsDestroyed || !o.AllowedToDraw || o.AlphaHue == 0)
                                    {
                                        continue;
                                    }

                                    ushort g = o.Graphic;
                                    bool isLight = false;

                                    // PORT DEVIATION (GUO): unlit fixtures host
                                    // no light (see _unlitFixtureGraphics), so a
                                    // wall sharing their tile must still occlude.
                                    if (!IsUnlitFixture(g))
                                    {
                                        if (g < statics.Length && statics[g].IsLight)
                                        {
                                            isLight = true;
                                        }
                                        else if (
                                            g >= 0x3E02 && g <= 0x3E0B
                                            || g >= 0x3914 && g <= 0x3929
                                            || g == 0x0B1D)
                                        {
                                            isLight = true;
                                        }
                                    }

                                    if (isLight)
                                    {
                                        hostsLight = true;

                                        break;
                                    }
                                }

                                if (!hostsLight && _lightCount > 0)
                                {
                                    float occlX = obj.RealScreenPosition.X + 22;
                                    float occlY = obj.RealScreenPosition.Y + 22;
                                    float wallPx = data.Height * 4f;
                                    int n = Math.Min(_lightCount, LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT);

                                    for (int li = 0; li < n; li++)
                                    {
                                        ref LightData l = ref _lights[li];
                                        float dx = l.DrawX - occlX;
                                        float dy = l.DrawY - occlY;

                                        if (dx >= -22 && dx <= 22 && dy >= -22 - wallPx && dy <= 22)
                                        {
                                            hostsLight = true;

                                            break;
                                        }
                                    }
                                }

                                if (hostsLight)
                                {
                                    continue;
                                }

                                _realisticOccluders[count++] = new RealisticLightViewport.OccluderOffer(
                                    obj.RealScreenPosition.X + 22,
                                    obj.RealScreenPosition.Y + 22,
                                    data.Height);
                            }
                        }
                    }

                    if (count >= cap)
                    {
                        break;
                    }
                }
            }
            catch
            {
            }

            return count;
        }

        /// <summary>
        /// HD light Phase 3, one frame: advance the sun clock (profile hour,
        /// opt-in cycle, --hd-sun wins), set the march governors, and refill
        /// the quarter-res height buffer on its throttle. The hd_sun uniforms
        /// themselves are pushed by the PostFx stack from HdSun statics, so
        /// the sun swings and recolors even on frames the buffer skips.
        /// </summary>
        private void UpdateHdSun(RenderTargets renderTargets, ref Transform2D matrix)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                Profile currentProfile = ProfileManager.CurrentProfile;

                if (!_hdSunBooted)
                {
                    _hdSunBooted = true;

                    if (HdSun.BootHour.HasValue)
                    {
                        HdSun.Hour = Math.Clamp(HdSun.BootHour.Value, 0f, 23.99f);
                    }
                    else if (currentProfile != null)
                    {
                        HdSun.Hour = Math.Clamp(currentProfile.HdSunHour, 0f, 23.99f);
                    }

                    if (HdSun.BootSilent)
                    {
                        HdSun.Strength = 0f;
                    }

                    _hdSunLastTicks = GUO.Time.Ticks;
                }

                bool auto = HdSun.BootAuto || (HdSun.AutoSet ?? currentProfile?.HdSunAuto ?? false);
                HdSun.Auto = auto;

                long nowTicks = GUO.Time.Ticks;
                double dt = (nowTicks - _hdSunLastTicks) / 1000.0;
                _hdSunLastTicks = nowTicks;

                // PORT DEVIATION (GUO): HD light Phase 5. Weather follows the
                // world on every frame the HD foundation runs (both Draw
                // branches call here, even the sun-only noon one), feeding the
                // viewport ambient, the sun dim and the hd_wet/hd_flash/gi_wet
                // uniforms. Same dt as the sun clock; Classic never calls it.
                HdWeather.Update(_world, dt);

                if (dt > 0 && dt < 5.0)
                {
                    HdSun.Advance(dt);
                }

                if (auto)
                {
                    if (currentProfile != null && !HdSun.BootHour.HasValue)
                    {
                        currentProfile.HdSunHour = HdSun.Hour;
                    }
                }
                else if (!HdSun.BootHour.HasValue && currentProfile != null)
                {
                    // Two-way manual sync: the Options slider writes the
                    // profile (adopt it), the -hd_sun command writes the
                    // static (persist it).
                    if (Math.Abs(currentProfile.HdSunHour - HdSun.Hour) > 0.001f)
                    {
                        HdSun.Hour = Math.Clamp(currentProfile.HdSunHour, 0f, 23.99f);
                    }
                    else
                    {
                        currentProfile.HdSunHour = HdSun.Hour;
                    }
                }

                // Zoom/mobile governors: fewer march taps and a coarser,
                // lazier buffer off the desktop and at extreme zooms.
                bool mobile = PlatformDefaults.Platform != ProfilePlatform.Desktop;
                float zoom = 1f;

                try
                {
                    zoom = Camera.Zoom;
                }
                catch
                {
                }

                HdSun.Steps = mobile || zoom < 0.6f || zoom > 1.5f ? 6 : 10;
                HdSun.PxPerZ = 4f * Math.Clamp(zoom, 0.25f, 4f);
                // PORT DEVIATION (GUO): HD light Phase 4. Same tier rule for
                // the cascade rings (desktop 10: all 3, mobile/zoom 6:
                // near+mid); the shader gates rings on gi_steps, no recompile.
                HdGi.Steps = HdGi.For(mobile, zoom);

                bool sunPass = GUO.Renderer.PostFx.PostFxStack.Instance.Preset.Passes.Exists(
                    p => p.Enabled && string.Equals(p.Shader, "hd_sun", StringComparison.OrdinalIgnoreCase));
                // Phase 4: hd_gi marches the same height buffer (occlusion),
                // so it keeps the buffer alive even with the sun pass off.
                bool giPass = GUO.Renderer.PostFx.PostFxStack.Instance.Preset.Passes.Exists(
                    p => p.Enabled && string.Equals(p.Shader, "hd_gi", StringComparison.OrdinalIgnoreCase));

                if (!sunPass && !giPass)
                {
                    if (_hdHeight != null)
                    {
                        _hdHeight.Dispose();
                        _hdHeight = null;
                    }

                    GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex = null;

                    return;
                }

                if (renderTargets.LightRenderTarget == null)
                {
                    return;
                }

                int width = renderTargets.LightRenderTarget.Width;
                int height = renderTargets.LightRenderTarget.Height;

                if (width <= 0 || height <= 0)
                {
                    return;
                }

                _hdHeight ??= new HdHeightBuffer();
                _hdHeight.EnsureSize(Client.Game, width, height, mobile);

                if (!_hdHeight.IsBuilt)
                {
                    GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex = null;

                    return;
                }

                int interval = mobile ? 6 : 3;
                _hdHeightFrame++;

                if ((_hdHeightFrame % interval) != 1
                    && GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex != null)
                {
                    return;
                }

                _hdHeight.BeginFill();
                CollectHdHeight(matrix);
                _lastHdStamps = _hdHeight.LastStamps;
                GUO.Renderer.PostFx.PostFxStack.Instance.HdHeightTex = _hdHeight.Texture;
            }
            finally
            {
                watch.Stop();

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (now - _hdSunLogAt > 10000)
                {
                    _hdSunLogAt = now;
                    HdSun.SunSample s = HdSun.Evaluate(HdSun.Hour);
                    Log.Trace(
                        $"[hd-sun] hour={HdSun.Hour:0.00}{(HdSun.Auto ? " auto" : "")} " +
                        $"rgb=({s.Color.R:0.00},{s.Color.G:0.00},{s.Color.B:0.00}) x{s.Energy:0.00} " +
                        $"steps={HdSun.Steps} stamps={_lastHdStamps} " +
                        $"gi={HdGi.Steps} " +
                        $"wet={HdWeather.Wetness:0.00} dim={HdWeather.Dim:0.00} flash={HdWeather.Flash:0.00} " +
                        $"update={watch.Elapsed.TotalMilliseconds:F2}ms");
                }
            }
        }

        /// <summary>
        /// Stamp the height buffer from what the frame already walks: land
        /// tiles as the base (their Z), then statics/multis/items with
        /// tiledata Height (Z + Height, wall-anchored rects like the
        /// occluders), splats last inside their PickRects (victim height).
        /// Hidden (AlphaHue 0) and transparent things stamp nothing, and
        /// mobiles are skipped: the buffer is terrain + structures.
        /// </summary>
        private void CollectHdHeight(Transform2D view)
        {
            if (_hdHeight == null || !_hdHeight.IsBuilt)
            {
                return;
            }

            try
            {
                float inv = 1f / _hdHeight.Scale;
                var statics = Client.Game.UO.FileManager.TileData.StaticData;

                foreach (var chunk in _visibleChunks)
                {
                    if (chunk == null || chunk.IsDestroyed)
                    {
                        continue;
                    }

                    for (int x = 0; x < 8; x++)
                    {
                        for (int y = 0; y < 8; y++)
                        {
                            for (
                                GameObject obj = chunk.GetHeadObject(x, y);
                                obj != null;
                                obj = obj.TNext)
                            {
                                if (obj.IsDestroyed || !obj.AllowedToDraw || obj.AlphaHue == 0)
                                {
                                    continue;
                                }

                                if (obj is Land)
                                {
                                    StampHdWorld(
                                        view, inv,
                                        obj.RealScreenPosition.X, obj.RealScreenPosition.Y,
                                        obj.RealScreenPosition.X + 44f, obj.RealScreenPosition.Y + 44f,
                                        obj.Z);

                                    continue;
                                }

                                if (obj is Mobile)
                                {
                                    continue;
                                }

                                if (!(obj is Static) && !(obj is Multi) && !(obj is Item))
                                {
                                    continue;
                                }

                                ushort graphic = obj.Graphic;

                                if (graphic >= statics.Length)
                                {
                                    continue;
                                }

                                ref StaticTiles data = ref statics[graphic];

                                if (data.IsTransparent || data.Height == 0 || obj.Z >= _maxZ)
                                {
                                    continue;
                                }

                                float cx = obj.RealScreenPosition.X + 22f;
                                float cy = obj.RealScreenPosition.Y + 22f;
                                float wallPx = data.Height * 4f;

                                StampHdWorld(
                                    view, inv,
                                    cx - 22f, cy - 22f - wallPx,
                                    cx + 22f, cy + 22f,
                                    obj.Z + data.Height);
                            }
                        }
                    }
                }

                foreach (SplatObject splat in _splatObjects.Values)
                {
                    if (splat == null || splat.IsDestroyed || !splat.AllowedToDraw || splat.AlphaHue == 0)
                    {
                        continue;
                    }

                    if (splat.Placement == null || splat.Placement.Facet != _world.MapIndex)
                    {
                        continue;
                    }

                    if (splat.Placement.Name != null && splat.Placement.Name.Contains("preview"))
                    {
                        continue;
                    }

                    Rectangle r = splat.PickRect;

                    if (r.Width <= 0 || r.Height <= 0)
                    {
                        continue;
                    }

                    int top = splat.Z + 8;

                    try
                    {
                        if (splat.TryGetVictimGraphic(out ushort victim) && victim != 0 && victim < statics.Length)
                        {
                            top = splat.Z + Math.Max((int)statics[victim].Height, 8);
                        }
                    }
                    catch
                    {
                    }

                    StampHdWorld(view, inv, r.X, r.Y, r.X + r.Width, r.Y + r.Height, top);
                }
            }
            catch
            {
            }
        }

        /// <summary>One world-pixel rect into buffer pixels through the view.</summary>
        private void StampHdWorld(Transform2D view, float inv, float x0, float y0, float x1, float y1, int topZ)
        {
            if (_hdHeight == null)
            {
                return;
            }

            Vector2 a = view * new Vector2(x0, y0);
            Vector2 b = view * new Vector2(x1, y1);

            _hdHeight.Stamp(
                new Rect2(
                    Math.Min(a.X, b.X) * inv, Math.Min(a.Y, b.Y) * inv,
                    Math.Abs(b.X - a.X) * inv, Math.Abs(b.Y - a.Y) * inv),
                topZ);
        }

        public override void DrawUI(UltimaBatcher2D batcher)
        {
            _healthLinesManager.Draw(batcher, 0f);

            if (!UIManager.IsMouseOverWorld)
            {
                SelectedObject.Object = null;
            }

            _world.WorldTextManager.ProcessWorldText(true);
            _world.WorldTextManager.Draw(batcher, Camera.Bounds.X, Camera.Bounds.Y, 0);
        }

        public void DrawSelection(UltimaBatcher2D batcher, float layerDepth)
        {
            if (_isSelectionActive)
            {
                Vector3 selectionHue = new()
                {
                    Z = 0.7f
                };

                Point upperLeftInWorld = Camera.ScreenToWorld(new Point(
                    Math.Min(_selectionStart.X, Mouse.Position.X) - Camera.Bounds.X,
                    Math.Min(_selectionStart.Y, Mouse.Position.Y) - Camera.Bounds.Y
                ));

                Point lowerRightInWorld = Camera.ScreenToWorld(new Point(
                    Math.Max(_selectionStart.X, Mouse.Position.X) - Camera.Bounds.X,
                    Math.Max(_selectionStart.Y, Mouse.Position.Y) - Camera.Bounds.Y
                ));

                Rectangle selectionRect = new Rectangle(
                    upperLeftInWorld.X,
                    upperLeftInWorld.Y,
                    lowerRightInWorld.X - upperLeftInWorld.X,
                    lowerRightInWorld.Y - upperLeftInWorld.Y
                );

                batcher.Draw(
                    SolidColorTextureCache.GetTexture(Color.Black),
                    selectionRect,
                    selectionHue,
                    layerDepth
                );

                selectionHue.Z = 0.3f;

                batcher.DrawRectangle(
                    SolidColorTextureCache.GetTexture(Color.DeepSkyBlue),
                    selectionRect.X,
                    selectionRect.Y,
                    selectionRect.Width,
                    selectionRect.Height,
                    selectionHue,
                    layerDepth
                );
            }
        }

        private static readonly RenderedText _youAreDeadText = RenderedText.Create(
            ResGeneral.YouAreDead,
            0xFFFF,
            3,
            false,
            FontStyle.BlackBorder,
            TEXT_ALIGN_TYPE.TS_LEFT
        );

        private bool CheckDeathScreen(UltimaBatcher2D batcher)
        {
            if (
                ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.EnableDeathScreen
            )
            {
                if (_world.InGame)
                {
                    if (_world.Player.IsDead && _world.Player.DeathScreenTimer > Time.Ticks)
                    {
                        batcher.Begin();
                        _youAreDeadText.Draw(
                            batcher,
                            Camera.Bounds.X + (Camera.Bounds.Width / 2 - _youAreDeadText.Width / 2),
                            Camera.Bounds.Bottom / 2,
                            0f
                        );
                        batcher.End();

                        return true;
                    }
                }
            }

            return false;
        }

        private void StopFollowing()
        {
            if (_followingMode)
            {
                _followingMode = false;
                _followingTarget = 0;
                _world.Player.Pathfinder.StopAutoWalk();

                _world.MessageManager.HandleMessage(
                    _world.Player,
                    ResGeneral.StoppedFollowing,
                    string.Empty,
                    0,
                    MessageType.Regular,
                    3,
                    TextType.CLIENT
                );
            }
        }

        private struct LightData
        {
            public byte ID;
            public ushort Color;
            public bool IsHue;
            public int DrawX,
                DrawY;
        }
    }
}
