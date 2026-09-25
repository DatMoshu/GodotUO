// SPDX-License-Identifier: BSD-2-Clause

using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.GameObjects;

namespace GUO.Host;

/// <summary>
/// Hover a meshed tile or static, leave, come back, and check its highlight is gone.
/// </summary>
/// <remarks>
/// The highlight on land or a static drawn by the chunk mesh is written into the
/// mesh's vertices and restored the next frame (see GameScene.DrawWorld). The
/// restore used to be skipped when the highlighted object had been destroyed
/// in the meantime, which is what happens to everything in a chunk the
/// client unloads. Land is always meshed; statics that need sorting against
/// mobiles are not, so whichever meshed object is found first is used. This walks that path: highlight one, "[go" far enough for
/// its chunk to be dropped, "[go" back, and look through the reloaded chunk's
/// mesh for any sprite still wearing the highlight hue.
/// </remarks>
internal static class HighlightProbe
{
    public static bool Passed { get; private set; }

    public static async System.Threading.Tasks.Task Run(Node host)
    {
        // Already in when --shard-command ran first: a probe can be sent
        // somewhere before it starts, which a character made a moment ago
        // in an empty corner of the map needs.
        if (!Client.Game.UO.World.InGame)
        {
            await InputProbe.EnterTheWorld(host, 200);
        }

        World world = Client.Game.UO.World;

        if (!world.InGame)
        {
            GD.PrintErr("[GUO] highlight probe: never got into the world.");

            return;
        }

        ProfileManager.CurrentProfile.HighlightGameObjects = true;

        (GameObject target, Vector2 at) = await FindMeshed(host);

        if (target == null)
        {
            Check("a meshed object is under the mouse", false, "none found near the character");

            return;
        }

        var chunk = world.Map.GetChunk(target.X, target.Y);
        bool lit = HighlightIn(chunk) > 0;
        Check("the hovered object wears the highlight in the mesh", lit,
            $"{target.GetType().Name} 0x{target.Graphic:X4} at {target.X},{target.Y} (mouse {at.X},{at.Y})");

        int homeX = world.Player.X, homeY = world.Player.Y;
        sbyte homeZ = world.Player.Z;

        // Off the world and onto the UI, so nothing new is highlighted while
        // the character is away or once it is back.
        Move(new Vector2(4, 4));

        // Far enough for the old chunk to leave the view and be dropped.
        await InputProbe.Say(host, $"[go {(homeX > 3000 ? homeX - 1500 : homeX + 1500)} {homeY}");
        await InputProbe.Wait(host, 600);

        bool dropped = target.IsDestroyed;
        Check("its chunk was dropped while away", dropped,
            dropped ? "destroyed" : "still alive -- the chunk was not unloaded");

        await InputProbe.Say(host, $"[go {homeX} {homeY} {homeZ}");
        await InputProbe.Wait(host, 300);
        Move(new Vector2(4, 4));
        await InputProbe.Wait(host, 10);

        var back = world.Map.GetChunk(homeX, homeY);
        var again = world.Map.GetChunk(target.X, target.Y);
        int stray = HighlightIn(again);
        bool home = world.Player.X == homeX && world.Player.Y == homeY;

        Check("the character is back where it hovered", home && back != null,
            $"at {world.Player.X},{world.Player.Y}");
        Check("no sprite in the reloaded chunk wears the highlight", again?.Mesh != null && stray == 0,
            again?.Mesh == null ? "chunk not loaded" : $"{stray} highlighted sprites");
    }

    private static async System.Threading.Tasks.Task<(GameObject, Vector2)> FindMeshed(Node host)
    {
        // The character sits in the middle of the world view, which on a fresh
        // profile is 640x480 in the corner of the window, not the window.
        Game.UI.Gumps.WorldViewportGump view =
            Game.Managers.UIManager.GetGump<Game.UI.Gumps.WorldViewportGump>();
        Vector2 centre = view != null
            ? new Vector2(view.X + view.Width / 2f, view.Y + view.Height / 2f)
            : host.GetViewport().GetVisibleRect().Size / 2;

        // Outwards from the character, which the camera keeps in the middle.
        for (int r = 40; r <= 400; r += 20)
        {
            for (int a = 0; a < 360; a += 15)
            {
                var at = centre + new Vector2(r, 0).Rotated(Mathf.DegToRad(a));

                Move(at);
                // Four frames: what is under the cursor is worked out while the
                // world is drawn (see InputProbe.FindOnScreen).
                await InputProbe.Wait(host, 4);

                if (SelectedObject.Object is GameObject s and (Static or Land)
                    && s.InChunkMesh && s.MeshSpriteIndex >= 0)
                {
                    await InputProbe.Wait(host, 4);

                    if (ReferenceEquals(SelectedObject.Object, s))
                    {
                        return (s, at);
                    }
                }
            }
        }

        return (null, default);
    }

    /// <summary>How many sprites in a chunk's meshes carry the highlight hue.</summary>
    private static int HighlightIn(Game.Map.Chunk chunk)
    {
        if (chunk?.Mesh == null)
        {
            return 0;
        }

        int n = 0;

        foreach (var layer in new[] { chunk.Mesh.Land, chunk.Mesh.Statics })
        {
            for (int i = 0; i < layer.Count; i++)
            {
                if (layer.Vertices[i].Hue0.X == Constants.HIGHLIGHT_CURRENT_OBJECT_HUE - 1)
                {
                    n++;
                }
            }
        }

        return n;
    }

    private static void Move(Vector2 at) =>
        Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = at });

    private static bool _failed;

    private static void Check(string what, bool ok, string detail)
    {
        GD.Print($"[GUO] highlight check: {(ok ? "ok  " : "FAIL")} {what} -- {detail}");
        _failed |= !ok;
        Passed = !_failed;
    }
}
