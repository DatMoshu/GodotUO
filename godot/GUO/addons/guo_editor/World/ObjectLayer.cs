#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GUO.Game.GameObjects;

/// <summary>
/// The World tab's world-objects layer (docs/editor_plan.md phase 6,
/// ADR-0014): the project's spawners and placed items, drawn in the embedded
/// world as the server-side items a client would see, and edited in place.
/// </summary>
/// <remarks>
/// Placed items are drawn with their own art and hue. A spawner is drawn as
/// ModernUO's spawner item (0x1F13), hued so it stands out from ordinary
/// items. Each object gets a serial from a range no shard uses in the editor,
/// and the layer maps serials back to the object ids the model keeps. Every
/// change writes <c>shard/objects.json</c>; there is no unsaved state.
/// Object edits are not in the World tab's block undo.
/// </remarks>
internal sealed class ObjectLayer
{
    public const ushort SpawnerGraphic = 0x1F13;
    public const ushort SpawnerHue = 0x0026;
    private const uint FirstSerial = 0x7E00_0000;

    private readonly WorldHost _host;
    private readonly Dictionary<uint, Guid> _bySerial = new();
    private uint _next = FirstSerial;
    private bool _visible = true;

    public ShardObjects Objects { get; private set; }

    /// <summary>Raised after every change, with a line describing it.</summary>
    public event Action<string> Changed;

    /// <summary>
    /// A local put (kind "spawner" or "item", the object as JSON) or delete
    /// (kind, id): what the live tier sends to the shard. Not raised for
    /// another editor's changes.
    /// </summary>
    public event Action<string, JsonObject> Put;

    public event Action<string, Guid> Deleted;

    public ObjectLayer(WorldHost host)
    {
        _host = host;
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            Redraw();
        }
    }

    /// <summary>Reads the project's objects and draws them. Null closes the layer.</summary>
    public void Open(string projectRoot)
    {
        Objects = projectRoot != null ? new ShardObjects(projectRoot) : null;
        Redraw();
    }

    /// <summary>Takes every drawn object out of the world and draws the model again (the current facet only).</summary>
    public void Redraw()
    {
        foreach (uint serial in _bySerial.Keys)
        {
            _host.RemoveServerObject(serial);
        }

        _bySerial.Clear();
        _next = FirstSerial;
        if (Objects == null || !_visible || _host.World == null)
        {
            return;
        }

        string map = ShardObjects.MapNames[Math.Clamp(_host.Facet, 0, ShardObjects.MapNames.Length - 1)];
        foreach (ShardSpawner s in Objects.Spawners)
        {
            if (s.Map == map)
            {
                Draw(s.Id, SpawnerGraphic, s.X, s.Y, s.Z, SpawnerHue);
            }
        }

        foreach (ShardItem i in Objects.Items)
        {
            if (i.Map == map)
            {
                Draw(i.Id, i.ItemId, i.X, i.Y, i.Z, i.Hue);
            }
        }
    }

    private void Draw(Guid id, ushort graphic, int x, int y, int z, ushort hue)
    {
        uint serial = _next++;
        if (_host.PlaceServerItem(serial, graphic, (ushort)x, (ushort)y, (sbyte)z, hue) != null)
        {
            _bySerial[serial] = id;
        }
    }

    /// <summary>The object id behind a drawn world object, or null when it is not one of the layer's.</summary>
    public Guid? IdOf(object picked) =>
        picked is Item item && _bySerial.TryGetValue(item.Serial, out Guid id) ? id : null;

    /// <summary>One of the layer's drawn items with a graphic, or null (the tour aims at it).</summary>
    public Item DrawnItemWithGraphic(ushort graphic)
    {
        foreach (uint serial in _bySerial.Keys)
        {
            if (_host.World?.Get(serial) is Item item && item.Graphic == graphic)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>How many of the layer's objects are in the embedded world right now.</summary>
    public int DrawnCount
    {
        get
        {
            int n = 0;
            foreach (uint serial in _bySerial.Keys)
            {
                n += _host.World?.Get(serial) is Item ? 1 : 0;
            }

            return n;
        }
    }

    private bool Save(string what)
    {
        if (Objects == null)
        {
            return false;
        }

        Objects.Save();
        Redraw();
        Changed?.Invoke(what);
        return true;
    }

    public ShardItem PlaceItem(int facet, int x, int y, int z, ushort itemId, ushort hue)
    {
        if (Objects == null)
        {
            return null;
        }

        var item = new ShardItem { Map = ShardObjects.MapNames[facet], X = x, Y = y, Z = z, ItemId = itemId, Hue = hue };
        Objects.Items.Add(item);
        Save($"placed item 0x{itemId:X4} at {x},{y},{z}");
        Put?.Invoke("item", ShardObjects.ToJson(item));
        return item;
    }

    public ShardSpawner PlaceSpawner(int facet, int x, int y, int z, string entry, int homeRange = 2)
    {
        if (Objects == null || string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        var spawner = new ShardSpawner { Map = ShardObjects.MapNames[facet], X = x, Y = y, Z = z, HomeRange = homeRange };
        spawner.Entries.Add((entry.Trim(), 1, 100));
        Objects.Spawners.Add(spawner);
        Save($"placed spawner of {entry.Trim()} at {x},{y},{z}");
        Put?.Invoke("spawner", ShardObjects.ToJson(spawner));
        return spawner;
    }

    public bool Move(Guid id, int facet, int x, int y, int z)
    {
        switch (Objects?.Find(id))
        {
            case ShardSpawner s:
                (s.Map, s.X, s.Y, s.Z) = (ShardObjects.MapNames[facet], x, y, z);
                Save($"moved spawner to {x},{y},{z}");
                Put?.Invoke("spawner", ShardObjects.ToJson(s));
                return true;
            case ShardItem i:
                (i.Map, i.X, i.Y, i.Z) = (ShardObjects.MapNames[facet], x, y, z);
                Save($"moved item 0x{i.ItemId:X4} to {x},{y},{z}");
                Put?.Invoke("item", ShardObjects.ToJson(i));
                return true;
            default:
                return false;
        }
    }

    public bool Delete(Guid id)
    {
        string kind = Objects?.Find(id) is ShardSpawner ? "spawner" : "item";
        if (Objects == null || !Objects.Remove(id))
        {
            return false;
        }

        Save("deleted an object");
        Deleted?.Invoke(kind, id);
        return true;
    }

    /// <summary>
    /// Another editor's put or delete, from the shard: written to this
    /// project and drawn, as the last write wins. Not sent on again.
    /// </summary>
    public bool ApplyRemote(JsonNode msg)
    {
        if (Objects == null)
        {
            return false;
        }

        string kind = (string)msg["kind"];
        if ((string)msg["action"] == "delete")
        {
            Objects.Remove(Guid.Parse((string)msg["id"]));
        }
        else if (kind == "spawner")
        {
            Objects.Upsert(ShardObjects.ParseSpawner(msg["object"]));
        }
        else
        {
            Objects.Upsert(ShardObjects.ParseItem(msg["object"]));
        }

        return Save($"remote: {(string)msg["from"] ?? "?"} {(string)msg["action"]} {kind}");
    }

    public void Close()
    {
        Objects = null;
        Redraw();
    }
}
#endif
