// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Input;
using GUO.Resources;
using GUO.Utility.Logging;

namespace GUO.Game.Managers
{
    internal sealed class CommandManager
    {
        private readonly Dictionary<string, Action<string[]>> _commands = new Dictionary<string, Action<string[]>>();
        private readonly World _world;

        public CommandManager(World world)
        {
            _world = world;
        }

        public void Initialize()
        {
            // PORT DEVIATION (GUO): explicit local UI authoring capture/apply, preserving live control behavior.
            Register("gumpcapture", _ => GUO.UI.Authoring.GumpCapture.SaveOpen(_world));
            Register("gumpapply", args => GUO.UI.Authoring.GumpCapture.ApplyFile(_world, args));
            Register("gumpopen", args => Input.Touch.Modern.ModernAuthoredGump.PreviewFile(_world, args));
            // PORT DEVIATION (GUO): native scripting panel; no external assistant.
            Register("scripts", s => Input.Touch.Modern.ModernScripts.Show(_world));
            Register("stopscript", s => _world.StopScripts());
            // PORT DEVIATION (GUO): in-game splat placer (staged splats, live
            // preview + manifest save). Upstream has no generated content.
            Register("splat", s => UIManager.Add(new UI.Gumps.SplatPlacerGump(_world)));
            // PORT DEVIATION (GUO): in-game image repaint (ComfyUI img2img
            // into the override art folder, never the install).
            Register("repaint", s => UIManager.Add(new UI.Gumps.RepaintGump(_world)));
            // PORT DEVIATION (GUO): NPC voice casting (enrolled voices,
            // rules, design). Upstream mobiles are silent data.
            Register("voice", s => UIManager.Add(new UI.Gumps.VoiceGump(_world)));
            // PORT DEVIATION (GUO): ambient music zones (generated tracks,
            // tile rects, round-robin). Upstream music is data indices.
            Register("audiozones", s => UIManager.Add(new UI.Gumps.MusicZoneGump(_world)));
            Register("musiczone", s => UIManager.Add(new UI.Gumps.MusicZoneGump(_world)));
            Register("sfxzone", s => UIManager.Add(new UI.Gumps.MusicZoneGump(_world, true)));
            // PORT DEVIATION (GUO): terrain layers (ComfyUI underlays and
            // overlays) are read at boot; this re-reads layers.json live so
            // the Layers dock's edits show without restarting the client.
            Register("relayers", s =>
            {
                GameScene scene = Client.Game.GetScene<GameScene>();
                if (scene != null)
                {
                    scene.ReloadTerrainLayers();
                    GameActions.Print(_world, "Terrain layers reloaded.");
                }
            });
            Register
            (
                "info",
                s =>
                {
                    if (_world.TargetManager.IsTargeting)
                    {
                        _world.TargetManager.CancelTarget();
                    }

                    _world.TargetManager.SetTargeting(CursorTarget.SetTargetClientSide, CursorType.Target, TargetType.Neutral);
                }
            );

            Register
            (
                "datetime",
                s =>
                {
                    if (_world.Player != null)
                    {
                        GameActions.Print(_world, string.Format(ResGeneral.CurrentDateTimeNowIs0, DateTime.Now));
                    }
                }
            );

            Register
            (
                "hue",
                s =>
                {
                    if (_world.TargetManager.IsTargeting)
                    {
                        _world.TargetManager.CancelTarget();
                    }

                    _world.TargetManager.SetTargeting(CursorTarget.HueCommandTarget, CursorType.Target, TargetType.Neutral);
                }
            );


            Register
            (
                "debug",
                s =>
                {
                    CUOEnviroment.Debug = !CUOEnviroment.Debug;

                }
            );
        }


        public void Register(string name, Action<string[]> callback)
        {
            name = name.ToLower();

            if (!_commands.ContainsKey(name))
            {
                _commands.Add(name, callback);
            }
            else
            {
                Log.Error($"Attempted to register command: '{name}' twice.");
            }
        }

        public void UnRegister(string name)
        {
            name = name.ToLower();

            if (_commands.ContainsKey(name))
            {
                _commands.Remove(name);
            }
        }

        public void UnRegisterAll()
        {
            _commands.Clear();
        }

        public void Execute(string name, params string[] args)
        {
            name = name.ToLower();

            if (_commands.TryGetValue(name, out Action<string[]> action))
            {
                action.Invoke(args);
            }
            else
            {
                Log.Warn($"Command: '{name}' not exists");
            }
        }

        public void OnHueTarget(Entity entity)
        {
            if (entity != null)
            {
                _world.TargetManager.Target(entity);
                Mouse.LastLeftButtonClickTime = 0;
                GameActions.Print(_world, string.Format(ResGeneral.ItemID0Hue1, entity.Graphic, entity.Hue));
            }
            else
            {
                Mouse.LastLeftButtonClickTime = 0;
            }
        }
    }
}