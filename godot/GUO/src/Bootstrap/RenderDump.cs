// SPDX-License-Identifier: BSD-2-Clause

// Compiled into GUO, and into the out-of-tree ClassicUO build that
// tools\ab_compare makes (with RENDER_DUMP_CUO defined), so both clients dump
// the same things the same way. The two trees differ only in their root
// namespace, which is all the #if below swaps. sources\ is never edited: the
// ClassicUO build picks this file up through an MSBuild import.

#if RENDER_DUMP_CUO
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Map;
using ClassicUO.Game.Scenes;
using ClientRoot = ClassicUO.Client;
#else
using GUO.Game;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.Map;
using GUO.Game.Scenes;
using ClientRoot = GUO.Client;
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

#if RENDER_DUMP_CUO
namespace ClassicUO.RenderDumpInjected;
#else
namespace GUO.Host;
#endif

/// <summary>
/// Writes what the client drew last frame, and the map under it, to JSON.
/// </summary>
/// <remarks>
/// Off unless GUO_RENDER_DUMP_DIR is set. Then any speech heard containing
/// "renderdump" -- the player's own, echoed by the shard, or another
/// player's nearby -- dumps the last frame to
/// <c>$GUO_RENDER_DUMP_DIR\&lt;label&gt;\&lt;client&gt;.json</c>, where the label is
/// the word after "renderdump" (or a timestamp). Two clients standing together
/// both hear one line, so one "say" dumps both at nearly the same moment.
///
/// Speech arrives on the main thread in both clients, between frames, so the
/// render lists still hold the frame just drawn. A timer only finds the world
/// and subscribes; it never touches game state itself.
///
/// For each object, "drawn" says how it reached the screen: the name of the
/// render list it was queued in, or "mesh" for land and statics drawn from a
/// chunk mesh (ClassicUO's GPU path, GUO's baked one), whose per-frame
/// visibility flag is read back. Objects in the tile dump that were not drawn
/// have no "drawn" at all.
/// </remarks>
internal static class RenderDump
{
    private const string Keyword = "renderdump";
    private const int Radius = 24;

#if RENDER_DUMP_CUO
    private const string ClientName = "cuo";
#else
    private const string ClientName = "guo";
#endif

    private static string _dir;
    private static Timer _timer;
    private static object _attached;
    private static DateTime _last;

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [ModuleInitializer]
    internal static void Start()
    {
        _dir = Environment.GetEnvironmentVariable("GUO_RENDER_DUMP_DIR");

        if (string.IsNullOrWhiteSpace(_dir))
        {
            return;
        }

        _timer = new Timer(_ => Attach(), null, 1000, 1000);
    }

    private static void Attach()
    {
        try
        {
            if (ClientRoot.Game?.Scene is not GameScene scene)
            {
                return;
            }

            if (Field(scene, "_world") is not World world || ReferenceEquals(world, _attached))
            {
                return;
            }

            world.MessageManager.MessageReceived += OnMessage;
            _attached = world;
            Console.WriteLine($"[render_dump] {ClientName}: listening for \"{Keyword}\"");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[render_dump] attach failed: {e.Message}");
        }
    }

    private static void OnMessage(object sender, MessageEventArgs e)
    {
        string text = e.Text ?? "";
        int at = text.IndexOf(Keyword, StringComparison.OrdinalIgnoreCase);

        // The shard echoes the speaker's own line, and a second client nearby
        // hears it too: one dump per line, not one per copy of it.
        if (at < 0 || (DateTime.UtcNow - _last).TotalSeconds < 2)
        {
            return;
        }

        _last = DateTime.UtcNow;

        string rest = text[(at + Keyword.Length)..].Trim();
        string label = rest.Length > 0 ? rest.Split(' ')[0] : DateTime.Now.ToString("yyyyMMdd-HHmmss");

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            label = label.Replace(c, '_');
        }

        try
        {
            string path = Write(label);
            Console.WriteLine($"[render_dump] {ClientName}: wrote {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[render_dump] {ClientName}: failed: {ex}");
        }
    }

    private static string Write(string label)
    {
        var scene = (GameScene) ClientRoot.Game.Scene;
        var world = (World) Field(scene, "_world");
        object lists = Field(scene, "_renderLists");

        string dir = Path.Combine(_dir, label);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, ClientName + ".json");

        // Which list each object was queued in, and where in it: the draw
        // order, as far as the lists decide it.
        var queued = new Dictionary<GameObject, (string list, int index, float? depth)>(ReferenceEqualityComparer.Instance);

        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        json.WriteStartObject();
        json.WriteString("client", ClientName);
        json.WriteString("label", label);
        json.WriteString("time", DateTime.Now.ToString("o"));

        var player = world.Player;
        json.WriteStartObject("player");
        json.WriteNumber("x", player.X);
        json.WriteNumber("y", player.Y);
        json.WriteNumber("z", player.Z);
        json.WriteEndObject();

        json.WriteNumber("map", world.MapIndex);
        json.WriteNumber("max_ground_z", Convert.ToInt32(Field(scene, "_maxGroundZ")));

        json.WriteStartObject("lists");

        foreach (FieldInfo field in lists.GetType().GetFields(Any))
        {
            if (field.GetValue(lists) is not IList list)
            {
                continue;
            }

            string name = field.Name.TrimStart('_');
            bool any = false;

            for (int i = 0; i < list.Count; i++)
            {
                object entry = list[i];
                float? depth = null;

                // GUO's world list holds (object, depth, seq); upstream's hold
                // the objects themselves.
                if (entry is not GameObject obj)
                {
                    FieldInfo inner = entry?.GetType().GetField("Object", Any);

                    if (inner?.GetValue(entry) is not GameObject wrapped)
                    {
                        break;
                    }

                    obj = wrapped;
                    depth = entry.GetType().GetField("Depth", Any)?.GetValue(entry) as float?;
                }

                if (!any)
                {
                    json.WriteStartArray(name);
                    any = true;
                }

                queued[obj] = (name, i, depth);
                WriteObject(json, obj, name, i, null);
            }

            if (any)
            {
                json.WriteEndArray();
            }
        }

        json.WriteEndObject();

        // Every object on every tile around the player, in the map's own order
        // (the tile's linked list, which is what the scene walks).
        json.WriteStartArray("tiles");

        var map = world.Map;

        for (int ty = player.Y - Radius; ty <= player.Y + Radius; ty++)
        {
            for (int tx = player.X - Radius; tx <= player.X + Radius; tx++)
            {
                if (tx < 0 || ty < 0)
                {
                    continue;
                }

                Chunk chunk = map.GetChunk(tx, ty, false);

                if (chunk == null)
                {
                    continue;
                }

                GameObject head = chunk.GetHeadObject(tx % 8, ty % 8);

                if (head == null)
                {
                    continue;
                }

                json.WriteStartObject();
                json.WriteNumber("x", tx);
                json.WriteNumber("y", ty);
                json.WriteStartArray("objects");

                for (GameObject obj = head; obj != null; obj = obj.TNext)
                {
                    string drawn = null;
                    int index = -1;

                    if (queued.TryGetValue(obj, out var q))
                    {
                        drawn = q.list;
                        index = q.index;
                    }
                    else if (MeshVisible(chunk, obj))
                    {
                        drawn = "mesh";
                    }

                    WriteObject(json, obj, drawn, index, chunk);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }
        }

        json.WriteEndArray();
        json.WriteEndObject();

        return path;
    }

    private static bool MeshVisible(Chunk chunk, GameObject obj)
    {
        if (!obj.InChunkMesh || obj.MeshSpriteIndex < 0)
        {
            return false;
        }

        var layer = obj is Land ? chunk.Mesh.Land : chunk.Mesh.Statics;

        return obj.MeshSpriteIndex < layer.Count && layer.Visible[obj.MeshSpriteIndex];
    }

    private static void WriteObject(Utf8JsonWriter json, GameObject obj, string drawn, int index, Chunk chunk)
    {
        json.WriteStartObject();
        json.WriteString("kind", obj.GetType().Name);

        if (obj is Entity entity)
        {
            json.WriteNumber("serial", (long) (uint) entity.Serial);
        }

        json.WriteNumber("graphic", obj.Graphic);
        json.WriteNumber("hue", obj.Hue);
        json.WriteNumber("x", obj.X);
        json.WriteNumber("y", obj.Y);
        json.WriteNumber("z", obj.Z);
        json.WriteNumber("priority_z", obj.PriorityZ);
        json.WriteNumber("depth", obj.CalculateDepthZ());
        json.WriteNumber("alpha", obj.AlphaHue);
        json.WriteBoolean("allowed", obj.AllowedToDraw);
        json.WriteNumber("sx", obj.RealScreenPosition.X);
        json.WriteNumber("sy", obj.RealScreenPosition.Y);

        if (obj is Land land)
        {
            json.WriteBoolean("stretched", land.IsStretched);
        }

        if (chunk != null)
        {
            json.WriteBoolean("in_mesh", obj.InChunkMesh);
        }

        if (drawn != null)
        {
            json.WriteString("drawn", drawn);

            if (index >= 0)
            {
                json.WriteNumber("index", index);
            }
        }

        json.WriteEndObject();
    }

    private static object Field(object target, string name)
    {
        for (Type t = target.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);

            if (f != null)
            {
                return f.GetValue(target);
            }
        }

        return null;
    }
}
