// SPDX-License-Identifier: BSD-2-Clause

using GUO.Assets;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.IO;
using GUO.Resources;
using GUO.Utility;
using GUO.Utility.Logging;
using Godot;
using System;
using System.Diagnostics;
using System.IO;

namespace GUO
{
    sealed class UltimaOnline
    {
        public Renderer.Animations.Animations Animations { get; private set; }
        public Renderer.Arts.Art Arts { get; private set; }
        public Renderer.Gumps.Gump Gumps { get; private set; }
        public Renderer.Texmaps.Texmap Texmaps { get; private set; }
        public Renderer.Lights.Light Lights { get; private set; }
        public Renderer.MultiMaps.MultiMap MultiMaps { get; private set; }
        public Renderer.Sounds.Sound Sounds { get; private set; }
        public Renderer.FontGlyphAtlas FontGlyphAtlas { get; private set; }
        public World World { get; private set; }
        public GameCursor GameCursor { get; private set; }

        public ClientVersion Version { get; private set; }
        public ClientFlags Protocol { get; set; }
        public string ClientPath { get; private set; }
        public UOFileManager FileManager { get; private set; }


        public UltimaOnline()
        {

        }

        public void Load(GameController game)
        {
            LoadUOFiles();

            const int TEXTURE_WIDTH = 512;
            const int TEXTURE_HEIGHT = 1024;
            const int LIGHTS_TEXTURE_WIDTH = 32;
            const int LIGHTS_TEXTURE_HEIGHT = 63;

            // PORT DEVIATION (GUO): upstream allocates two XNA textures, fills
            // one uint[] twice, and uploads each through SetDataPointerEXT --
            // FNA's "here is a pointer, do not copy it again" path -- then
            // binds them to sampler slots 1 and 2 on the device, where the hue
            // shader reads them. Godot has no global sampler slots: a shader
            // parameter is set on the material, so the two textures go to the
            // batcher, which owns the materials. See ADR-0002.
            //
            // The pixel format works out by itself. A uint here is written by
            // HuesLoader as 0xAABBGGRR, so its four bytes in memory already
            // read R, G, B, A -- which is Image.Format.Rgba8.
            var buffer = new uint[Math.Max(
                LIGHTS_TEXTURE_WIDTH * LIGHTS_TEXTURE_HEIGHT,
                TEXTURE_WIDTH * TEXTURE_HEIGHT
            )];

            FileManager.Hues.CreateShaderColors(buffer);
            Texture2D hueTexture = TextureFromPixels(buffer, TEXTURE_WIDTH, TEXTURE_HEIGHT);

            LightColors.CreateLightTextures(buffer, LIGHTS_TEXTURE_HEIGHT);
            Texture2D lightTexture = TextureFromPixels(
                buffer, LIGHTS_TEXTURE_WIDTH, LIGHTS_TEXTURE_HEIGHT
            );

            game.SetHueTextures(hueTexture, lightTexture);

            Animations = new Renderer.Animations.Animations(FileManager.Animations);
            Arts = new Renderer.Arts.Art(FileManager.Arts, FileManager.Hues);
            Gumps = new Renderer.Gumps.Gump(FileManager.Gumps);
            Texmaps = new Renderer.Texmaps.Texmap(FileManager.Texmaps);
            Lights = new Renderer.Lights.Light(FileManager.Lights);
            MultiMaps = new Renderer.MultiMaps.MultiMap(FileManager.MultiMaps);
            Sounds = new Renderer.Sounds.Sound(FileManager.Sounds);
            FontGlyphAtlas = new Renderer.FontGlyphAtlas(FileManager.Fonts);

            LightColors.LoadLights();

            World = new World();
            GameCursor = new GameCursor(World, game.DpiScale);
        }

        /// <summary>
        /// The first <paramref name="width"/> * <paramref name="height"/>
        /// entries of <paramref name="pixels"/>, as a texture. The buffer is
        /// longer than the texture for the light table, which shares the
        /// larger hue buffer.
        /// </summary>
        private static Texture2D TextureFromPixels(uint[] pixels, int width, int height)
        {
            var bytes = new byte[width * height * sizeof(uint)];

            Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);

            return ImageTexture.CreateFromImage(
                Image.CreateFromData(width, height, false, Image.Format.Rgba8, bytes)
            );
        }

        public void Unload()
        {
            FileManager.Dispose();
            World?.Map?.Destroy();
        }


        private void LoadUOFiles()
        {
            string clientPath = Settings.GlobalSettings.UltimaOnlineDirectory;
            Log.Trace($"Ultima Online installation folder: {clientPath}");

            Log.Trace("Loading files...");

            if (!string.IsNullOrWhiteSpace(Settings.GlobalSettings.ClientVersion))
            {
                // sanitize client version
                Settings.GlobalSettings.ClientVersion = Settings.GlobalSettings.ClientVersion.Replace(",", ".").Replace(" ", "").ToLower();
            }

            string clientVersionText = Settings.GlobalSettings.ClientVersion;

            // check if directory is good
            if (!Directory.Exists(clientPath))
            {
                Log.Error("Invalid client directory: " + clientPath);
                Client.ShowErrorMessage(string.Format(ResErrorMessages.ClientPathIsNotAValidUODirectory, clientPath));

                throw new InvalidClientDirectory($"'{clientPath}' is not a valid directory");
            }

            // try to load the client version
            if (!ClientVersionHelper.IsClientVersionValid(clientVersionText, out ClientVersion clientVersion))
            {
                Log.Warn($"Client version [{clientVersionText}] is invalid, let's try to read the client.exe");

                // mmm something bad happened, try to load from client.exe
                if (!ClientVersionHelper.TryParseFromFile(Path.Combine(clientPath, "client.exe"), out clientVersionText) || !ClientVersionHelper.IsClientVersionValid(clientVersionText, out clientVersion))
                {
                    Log.Error("Invalid client version: " + clientVersionText);
                    Client.ShowErrorMessage(string.Format(ResGumps.ImpossibleToDefineTheClientVersion0, clientVersionText));

                    throw new InvalidClientVersion($"Invalid client version: '{clientVersionText}'");
                }

                Log.Trace($"Found a valid client.exe [{clientVersionText} - {clientVersion}]");

                // update the wrong/missing client version in settings.json
                Settings.GlobalSettings.ClientVersion = clientVersionText;
            }

            Version = clientVersion;
            ClientPath = clientPath;

            Protocol = ClientFlags.CF_T2A;

            if (Version >= ClientVersion.CV_200)
            {
                Protocol |= ClientFlags.CF_RE;
            }

            if (Version >= ClientVersion.CV_300)
            {
                Protocol |= ClientFlags.CF_TD;
            }

            if (Version >= ClientVersion.CV_308)
            {
                Protocol |= ClientFlags.CF_LBR;
            }

            if (Version >= ClientVersion.CV_308Z)
            {
                Protocol |= ClientFlags.CF_AOS;
            }

            if (Version >= ClientVersion.CV_405A)
            {
                Protocol |= ClientFlags.CF_SE;
            }

            if (Version >= ClientVersion.CV_60144)
            {
                Protocol |= ClientFlags.CF_SA;
            }

            Log.Trace($"Client path: '{clientPath}'");
            Log.Trace($"Client version: {clientVersion}");
            Log.Trace($"Protocol: {Protocol}");

            var filesOverride = new UOFilesOverrideMap(Settings.GlobalSettings.OverrideFile);
            filesOverride.Load();
            FileManager = new UOFileManager(clientVersion, clientPath, filesOverride);
            FileManager.Load(Settings.GlobalSettings.UseVerdata, Settings.GlobalSettings.Language, Settings.GlobalSettings.MapsLayouts);

            StaticFilters.Load(FileManager.TileData);
            BuffTable.Load();
            ChairTable.Load();

            //ATTENTION: you will need to enable ALSO ultimalive server-side, or this code will have absolutely no effect!
            UltimaLive.Enable();
        }
    }


    internal static class Client
    {
        public static GameController Game { get; private set; }

        // PORT DEVIATION (GUO): the editor's World tab runs an embedded
        // controller (GameController.LoadEmbedded, ADR-0015) that the ported
        // code must find here exactly as it finds the game's, because it
        // reaches the renderer through Client.Game.UO. Run() does that and
        // also adds the controller to the root window, which the editor must
        // not do. DetachEmbedded clears it when the World tab closes.
        internal static void AttachEmbedded(GameController game)
        {
            if (Game != null && Game != game)
            {
                throw new InvalidOperationException("Client.AttachEmbedded called while a game is already running");
            }

            Game = game;
        }

        internal static void DetachEmbedded(GameController game)
        {
            if (Game == game)
            {
                Game = null;
            }
        }
        // END PORT DEVIATION (GUO)

        public static void Run(IPluginHost pluginHost)
        {
            // PORT DEVIATION (GUO): upstream asserts, which is compiled out
            // of Release. Its Run() never returns while the game lives, so a
            // second call cannot happen there; here Run() returns at once
            // (below), and a second call would silently replace Game.
            if (Game != null)
            {
                throw new InvalidOperationException("Client.Run called while a game is already running");
            }

            Log.Trace("Running game...");

            // PORT DEVIATION (GUO): upstream wraps the controller in a using
            // and calls Game.Run(), FNA's loop, which does not return until
            // the client exits -- so Run() is also where the game is disposed.
            // Godot owns the loop: this hands the controller to the scene
            // tree and returns immediately, and _ExitTree does what the using
            // did. The caller must therefore not treat Run() as "the client
            // has finished".
            Game = new GameController(pluginHost);

            // FNA read FNA_GRAPHICS_ENABLE_HIGHDPI to decide whether to ask
            // SDL for a high-DPI window. Godot always makes one and reports
            // the scale, so the only question left is whether the scale is 1.
            CUOEnviroment.IsHighDPI = Game.DpiScale > 1f;

            if (CUOEnviroment.IsHighDPI)
            {
                Log.Trace("HIGH DPI - ENABLED");
            }

            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            {
                throw new InvalidOperationException(
                    "Client.Run was called outside a running Godot scene tree"
                );
            }

            // The scene tree is still building its children when the host's
            // _Ready runs, and Godot refuses an AddChild from inside that.
            // Deferring puts the controller in at the end of the frame, which
            // is soon enough: nothing here touches the node again.
            tree.Root.CallDeferred(Node.MethodName.AddChild, Game);
        }

        /// <summary>
        /// PORT DEVIATION (GUO): upstream shows an SDL message box.
        /// <c>OS.Alert</c> is the same modal, native box.
        /// </summary>
        public static void ShowErrorMessage(string msg)
        {
            OS.Alert(msg, "ERROR");
        }
    }
}