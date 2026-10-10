#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;

/// <summary>
/// Live mobiles and items in the editor's World tab: the bridge's "mobiles"
/// and "items" feeds become real game objects in the editor world's own
/// dictionaries, so its GameScene draws them through the normal path (depth,
/// light, anims) beside the map files. Sync runs on every feed reply;
/// anything unseen for two syncs is destroyed (the world's update loop drops
/// destroyed objects from its dictionaries). Nothing here touches the shard:
/// the feed is read-only and the objects die with the session.
/// </summary>
internal static class LiveObjects
{
    private static readonly Dictionary<uint, Mobile> _mobs = new();
    private static readonly Dictionary<uint, Item> _items = new();
    private static readonly Dictionary<uint, int> _seenMob = new();
    private static readonly Dictionary<uint, int> _seenItem = new();
    private static int _tick;

    public static void Sync(World world, int facet, IReadOnlyList<LiveMobile> mobs, IReadOnlyList<LiveItem> items)
    {
        if (world == null)
        {
            return;
        }

        _tick++;
        if (mobs != null)
        {
            foreach (LiveMobile l in mobs)
            {
                if (l.Facet != facet || l.Serial == 0)
                {
                    continue;
                }

                if (!_mobs.TryGetValue(l.Serial, out Mobile m))
                {
                    m = new Mobile(world, l.Serial);
                    world.Mobiles[l.Serial] = m;
                    _mobs[l.Serial] = m;
                }

                m.X = (ushort)System.Math.Max(0, l.X);
                m.Y = (ushort)System.Math.Max(0, l.Y);
                m.Z = (sbyte)System.Math.Max(-128, System.Math.Min(127, l.Z));
                // The scene draws from chunks, not dictionaries: join this
                // tile like a packet arrival does (re-registers on the move).
                m.AddToTile();
                m.Graphic = (ushort)l.Body;
                m.Name = l.Name;
                m.Hits = (ushort)System.Math.Max(0, l.Hits);
                m.HitsMax = (ushort)System.Math.Max(1, l.MaxHits);
                // The shard's own facing (WeesaMap reads the same field),
                // not derived: standing turns show too.
                m.Direction = (Direction)(l.Direction & 7);
                m.FixHue((ushort)l.Hue);
                SyncEquipment(m, l);
                _seenMob[l.Serial] = _tick;
            }
        }

        if (items != null)
        {
            foreach (LiveItem l in items)
            {
                if (l.Facet != facet || l.Serial == 0)
                {
                    continue;
                }

                if (!_items.TryGetValue(l.Serial, out Item item))
                {
                    item = new Item(world);
                    item.Serial = l.Serial;
                    world.Items[l.Serial] = item;
                    _items[l.Serial] = item;
                }

                item.X = (ushort)System.Math.Max(0, l.X);
                item.Y = (ushort)System.Math.Max(0, l.Y);
                item.Z = (sbyte)System.Math.Max(-128, System.Math.Min(127, l.Z));
                item.AddToTile();
                item.Graphic = (ushort)l.Id;
                item.FixHue((ushort)l.Hue);
                item.Amount = (ushort)l.Amount;
                _seenItem[l.Serial] = _tick;
            }
        }

        Sweep(world, _mobs, _seenMob, DropEquipment);
        Sweep(world, _items, _seenItem);
    }

    /// <summary>Destroys a mobile's worn items with it (they ride the mobile, not the world).</summary>
    private static void DropEquipment(uint serial)
    {
        var gone = new List<(uint, int)>();
        foreach (var kv in _equip)
        {
            if (kv.Key.Mob == serial)
            {
                gone.Add(kv.Key);
            }
        }

        foreach (var key in gone)
        {
            if (_equip.Remove(key, out Item item) && item != null)
            {
                item.Destroy();
            }
        }
    }

    private static readonly Dictionary<(uint Mob, int Layer), Item> _equip = new();

    /// <summary>
    /// Worn items ride the mobile's own item list (what MobileView draws),
    /// keyed by layer: created, updated, and unlinked as layers change. Never
    /// in the world's dictionaries or on a tile: they are not on the ground.
    /// </summary>
    private static void SyncEquipment(Mobile m, LiveMobile l)
    {
        var want = new HashSet<(uint, int)>();
        if (l.Equip != null)
        {
            foreach (LiveEquip w in l.Equip)
            {
                var key = (l.Serial, w.Layer);
                want.Add(key);
                if (!_equip.TryGetValue(key, out Item item))
                {
                    item = new Item(m.World);
                    item.Serial = w.Serial;
                    _equip[key] = item;
                }

                item.Graphic = (ushort)w.Id;
                item.FixHue((ushort)w.Hue);
                item.Layer = (GUO.Game.Data.Layer)(byte)w.Layer;
                if (item.Container != m.Serial)
                {
                    item.Container = m.Serial;
                    m.PushToBack(item);
                }
            }
        }

        var gone = new List<(uint, int)>();
        foreach (var kv in _equip)
        {
            if (kv.Key.Mob == l.Serial && !want.Contains(kv.Key))
            {
                gone.Add(kv.Key);
            }
        }

        foreach (var key in gone)
        {
            _equip.Remove(key, out Item item);
            if (item != null)
            {
                m.Remove(item);
                item.Container = 0xFFFF_FFFF;
                item.Destroy();
            }
        }
    }

    private static void Sweep<T>(World world, Dictionary<uint, T> objects, Dictionary<uint, int> seen,
        System.Action<uint> onDestroy = null) where T : Entity
    {
        var gone = new List<uint>();
        foreach (var kv in seen)
        {
            if (kv.Value < _tick - 1)
            {
                gone.Add(kv.Key);
            }
        }

        foreach (uint serial in gone)
        {
            seen.Remove(serial);
            onDestroy?.Invoke(serial);
            if (objects.TryGetValue(serial, out T o))
            {
                objects.Remove(serial);
                o.Destroy();
            }
        }
    }

    /// <summary>Destroys every live object (disconnect, facet change).</summary>
    public static void Clear(World world)
    {
        foreach (Mobile m in _mobs.Values)
        {
            DropEquipment(m.Serial);
            m.Destroy();
        }

        foreach (Item item in _items.Values)
        {
            item.Destroy();
        }

        _mobs.Clear();
        _items.Clear();
        _seenMob.Clear();
        _seenItem.Clear();
    }
}
#endif
