#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using GUO.Game.Scenes;
using System.Collections.Generic;
using GUO.Input;
using Rectangle = GUO.Compat.Rectangle;
using CPoint = GUO.Compat.Point;

/// <summary>
/// The game's own world renderer, running inside the editor: an embedded
/// <c>GameController</c> (ADR-0015) with a <c>World</c>, a <c>GameScene</c>
/// and a stand-in player the camera follows. Nothing here draws on its own;
/// every frame goes through <c>GameScene.Draw</c>, as in the client.
/// </summary>
/// <remarks>
/// <para>
/// What the client would get from a server it does not get here: there is no
/// socket, so no mobiles, no items, and no server-placed multis or decoration.
/// The view shows the map files (land, statics, the client's own patches)
/// and, once a world project is open, its overlay. <c>GameObject.CanBeDrawn</c>
/// applies as in the game, because <c>Client.Game</c> is this controller.
/// </para>
/// <para>
/// The stand-in player is a <c>PlayerMobile</c> with no body: GameScene
/// centres on the player, measures roofs against its height, and gates the
/// whole draw on <c>World.InGame</c>. Moving it is how the camera moves.
/// </para>
/// </remarks>
internal sealed class WorldHost : IDisposable
{
    private const uint PlayerSerial = 0x0000_0001;

    private GameController _game;
    private GameScene _scene;
    private Node2D _canvas;
    private WorldProject _project;

    /// <summary>The world project laid over the map, or null.</summary>
    public WorldProject Project => _project;

    public bool IsBooted => _scene != null;
    public string Error { get; private set; }
    public World World => _game?.UO.World;
    public GameScene Scene => _scene;
    public long BootMilliseconds { get; private set; }

    public int Facet => World?.MapIndex ?? -1;
    public int X => World?.Player?.X ?? 0;
    public int Y => World?.Player?.Y ?? 0;
    public int Z => World?.Player?.Z ?? 0;

    /// <summary>
    /// Loads the install through the game's own path and builds the world.
    /// <paramref name="canvas"/> is the node whose canvas item the batcher
    /// draws into; its render targets are made under it.
    /// </summary>
    public bool Boot(Node2D canvas, int facet, int x, int y)
    {
        if (_scene != null)
        {
            return true;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _canvas = canvas;
        try
        {
            // Settings and the profile are the client's own; they point at a
            // scratch folder under build/ so nothing the editor does touches
            // the player's settings.json or profiles.
            string scratch = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "..", "build", "editor_world"));
            Directory.CreateDirectory(scratch);

            // CUOEnviroment.ExecutablePath is a static read once from the
            // working directory, and the client writes its default tables
            // (Data/Client/*.txt) under it. The game moves the working
            // directory first for this reason (Bootstrap/Main.StartClient); in
            // the editor it would otherwise be the Godot project folder. Read
            // it here, while the directory is the scratch folder, then put the
            // editor's directory back.
            string editorCwd = System.Environment.CurrentDirectory;
            System.Environment.CurrentDirectory = scratch;
            try
            {
                _ = CUOEnviroment.ExecutablePath;
            }
            finally
            {
                System.Environment.CurrentDirectory = editorCwd;
            }
            Settings s = Settings.GlobalSettings;
            s.UltimaOnlineDirectory = EditorData.Setting("UO_CLIENT_DATA", "");
            s.ClientVersion = EditorData.Setting("UO_CLIENT_VERSION", "7.0.107.76");
            s.Language = EditorData.Setting("UO_LANGUAGE", "enu").ToUpperInvariant();
            s.ProfilesPath = Path.Combine(scratch, "profiles");
            ProfileManager.Load("editor", "editor", "editor");

            Profile p = ProfileManager.CurrentProfile;
            p.HighlightGameObjects = false;
            p.UseCircleOfTransparency = false;
            p.DrawRoofs = true;

            _game = new GameController(embedded: true);
            Client.AttachEmbedded(_game);
            _game.LoadEmbedded(canvas);

            World world = _game.UO.World;
            world.MapIndex = facet;
            world.CreatePlayer(PlayerSerial);
            world.Player.Graphic = 0;

            _scene = new GameScene(world);
            _game.SetEmbeddedScene(_scene);
            _scene.Camera.Zoom = 1f;

            GoTo(facet, x, y);
        }
        catch (Exception ex)
        {
            Error = $"{ex.GetType().Name}: {ex.Message}";
            GD.PrintErr($"[GUO editor] world view failed to start: {Error}\n{ex.StackTrace}");
            Dispose();
            return false;
        }

        BootMilliseconds = sw.ElapsedMilliseconds;
        GD.Print($"[GUO editor] world view up in {BootMilliseconds} ms");
        return true;
    }

    /// <summary>Moves the view to a cell, on a facet, standing on the land there.</summary>
    public bool GoTo(int facet, int x, int y)
    {
        World world = World;
        if (world == null)
        {
            return false;
        }

        if (facet != world.MapIndex)
        {
            world.MapIndex = facet;
            ApplyOverlay();
        }

        if (world.Map == null)
        {
            return false;
        }

        var maps = _game.UO.FileManager.Maps;
        x = Math.Clamp(x, 0, maps.MapsDefaultSize[facet, 0] - 1);
        y = Math.Clamp(y, 0, maps.MapsDefaultSize[facet, 1] - 1);
        sbyte z = world.Map.GetTileZ(x, y);

        PlayerMobile player = world.Player;
        player.RemoveFromTile();
        player.SetInWorldTile((ushort)x, (ushort)y, z);
        return true;
    }

    /// <summary>
    /// One frame: the camera sized to the viewport, the mouse where the
    /// editor's pointer is (so the game's own picking runs), then the draw.
    /// </summary>
    public void Draw(Node host, Vector2I size, Vector2I? mouse)
    {
        if (_scene == null || size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        GUO.Time.Ticks = (uint)Godot.Time.GetTicksMsec();
        _scene.Camera.Bounds = new Rectangle(0, 0, size.X, size.Y);

        CPoint m = mouse.HasValue ? new CPoint(mouse.Value.X, mouse.Value.Y) : new CPoint(-1, -1);
        Mouse.Position = m;
        _scene.Camera.Update(true, 0.016f, m);
        SelectedObject.TranslatedMousePositionByViewport = _scene.Camera.MouseToWorldPosition();

        _game.DrawEmbedded(host, new Rectangle(0, 0, size.X, size.Y));
    }

    /// <summary>
    /// Puts a multi into the world the way a server does: the calls, in
    /// order, that <c>PacketHandlers.UpdateGameObject</c> makes for a world
    /// object of type 2 (0x1A / 0xF3). Offline nothing sends those packets;
    /// this is the entry the live tier feeds from the shard, and what shows a
    /// house or boat is drawn exactly as the game draws one.
    /// </summary>
    public Item PlaceServerMulti(uint serial, ushort graphic, ushort x, ushort y, sbyte z, ushort hue = 0)
    {
        World world = World;
        Item item = world?.GetOrCreateItem(serial);
        if (item == null)
        {
            return null;
        }

        item.IsMulti = true;
        item.WantUpdateMulti = true;
        item.Graphic = (ushort)(graphic & 0x3FFF);
        item.X = x;
        item.Y = y;
        item.Z = z;
        item.FixHue(hue);
        item.Amount = 1;
        item.CheckGraphicChange(item.AnimIndex);
        if (item.OnGround)
        {
            item.SetInWorldTile(item.X, item.Y, item.Z);
        }

        return item;
    }

    /// <summary>
    /// Takes an item out of the world the way a server's delete-object packet
    /// (0x1D) does in <c>PacketHandlers.DeleteObject</c>: a multi's house
    /// first, then the item.
    /// </summary>
    public bool RemoveServerObject(uint serial)
    {
        World world = World;
        if (world?.Get(serial) is not Item item)
        {
            return false;
        }

        if (item.IsMulti)
        {
            world.HouseManager.Remove(serial);
        }

        return world.RemoveItem(serial, forceRemove: true);
    }

    /// <summary>
    /// Opens (or creates) a world project and lays it over the current
    /// facet. Replaces any project already open.
    /// </summary>
    public WorldProject OpenProject(string root)
    {
        CloseProject();
        _project = WorldProject.OpenOrCreate(
            root,
            EditorData.Setting("UO_CLIENT_DATA", ""),
            EditorData.Setting("UO_CLIENT_VERSION", "7.0.107.76")
        );
        ApplyOverlay();
        return _project;
    }

    /// <summary>Re-reads the project from disk and lays it over the current facet.</summary>
    public int ApplyOverlay()
    {
        if (_project == null || World?.Map == null)
        {
            return 0;
        }

        var maps = _game.UO.FileManager.Maps;
        int facet = World.MapIndex;
        List<int> changed = _project.Apply(maps, facet);
        ReloadBlocks(facet, changed);
        return changed.Count;
    }

    /// <summary>Takes the overlay off and puts the install's blocks back.</summary>
    public void CloseProject()
    {
        if (_project == null)
        {
            return;
        }

        if (World?.Map != null)
        {
            int facet = World.MapIndex;
            ReloadBlocks(facet, _project.Restore(_game.UO.FileManager.Maps, facet));
        }

        _project.Dispose();
        _project = null;
    }

    /// <summary>
    /// Reloads loaded chunks whose block entry changed, as UltimaLive does:
    /// set aside what is not terrain (the stand-in player, multis), clear,
    /// load from the new entry, put them back. Unloaded chunks read the new
    /// entry when they are first needed.
    /// </summary>
    private void ReloadBlocks(int facet, List<int> blocks)
    {
        Map map = World?.Map;
        if (map == null || blocks.Count == 0)
        {
            return;
        }

        int height = _game.UO.FileManager.Maps.MapBlocksSize[facet, 1];
        foreach (int number in blocks)
        {
            Chunk chunk = map.GetChunk2(number / height, number % height, load: false);
            if (chunk == null)
            {
                continue;
            }

            var keep = new List<GameObject>();
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (GameObject o = chunk.GetHeadObject(x, y); o != null;)
                    {
                        GameObject next = o.TNext;
                        if (o is not Land && o is not Static)
                        {
                            keep.Add(o);
                            o.RemoveFromTile();
                        }

                        o = next;
                    }
                }
            }

            chunk.ClearForReload();
            chunk.Load(facet);
            foreach (GameObject o in keep)
            {
                chunk.AddGameObject(o, o.X % 8, o.Y % 8);
            }
        }
    }

    /// <summary>What the game's picking found under the pointer on the last draw.</summary>
    public BaseGameObject Picked => SelectedObject.Object;

    public void Dispose()
    {
        try
        {
            CloseProject();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] closing the world project: {ex.GetType().Name}: {ex.Message}");
        }

        if (_game != null)
        {
            try
            {
                _game.UnloadEmbedded();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GUO editor] world view teardown: {ex.GetType().Name}: {ex.Message}");
            }

            Client.DetachEmbedded(_game);
            _game.Free();
            _game = null;
        }

        _scene = null;
        _canvas = null;
    }
}
#endif
