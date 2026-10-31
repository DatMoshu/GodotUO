#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Reflection;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;

public partial class EditorSmoke
{
    private void CheckPathfinder()
    {
        var results = new Dictionary<string, object>();
        _report["pathfinder_correctness"] = results;
        var world = _world.Host.World;
        var pf = world.Player.Pathfinder;
        var previousManager = world.CustomHouseManager;
        sbyte playerZ = world.Player.Z;
        Multi wall = null;
        void Check(bool ok, string name)
        {
            results[name] = ok;
            if (!ok) _failures.Add("Pathfinder: " + name);
        }
        try
        {
            // Repeated queries through the production map/tiledata readers must not retain obstacles.
            int open = 0, blocked = 0, queries = 0;
            for (int x = _world.Host.X - 4; x <= _world.Host.X + 4; x++)
            for (int y = _world.Host.Y - 4; y <= _world.Host.Y + 4; y++)
            for (int direction = 0; direction < 8; direction++)
            {
                sbyte firstZ = playerZ, secondZ = playerZ;
                bool first = pf.CalculateNewZ(x, y, ref firstZ, direction);
                bool second = pf.CalculateNewZ(x, y, ref secondZ, direction);
                if (first != second || firstZ != secondZ) throw new InvalidOperationException("repeated terrain result changed");
                if (first) open++; else blocked++;
                queries += 2;
            }
            results["terrain_queries"] = queries;
            Check(open > 0 && blocked > 0, "real terrain includes open and blocked cells");

            // Find an open target using the real map rather than assuming the installed map is flat.
            int tx = 0, ty = 0;
            sbyte targetZ = 0;
            for (int x = _world.Host.X - 4; x <= _world.Host.X + 4 && tx == 0; x++)
            for (int y = _world.Host.Y - 4; y <= _world.Host.Y + 4 && tx == 0; y++)
            {
                sbyte z = playerZ;
                if (pf.CalculateNewZ(x, y, ref z, 0)) { tx = x; ty = y; targetZ = z; }
            }
            if (tx == 0) throw new InvalidOperationException("no open custom-house fixture cell");
            ushort graphic = 0;
            var tiles = Client.Game.UO.FileManager.TileData.StaticData;
            for (int i = 1; i < Math.Min(tiles.Length, ushort.MaxValue); i++)
                if (tiles[i].IsImpassable && tiles[i].IsWall && tiles[i].Height >= 20 && !tiles[i].IsSurface) { graphic = (ushort)i; break; }
            if (graphic == 0) throw new InvalidOperationException("no installed blocking wall tile");
            world.Player.Z = targetZ;
            wall = new Multi(world) { Graphic = graphic, IsCustom = true };
            wall.SetInWorldTile((ushort)tx, (ushort)ty, targetZ);
            bool Pass() { sbyte z = targetZ; return pf.CalculateNewZ(tx, ty, ref z, 0); }
            Check(!Pass(), "custom wall blocks normal movement");
            wall.IsHousePreview = true;
            Check(Pass(), "preview wall is ignored");
            wall.IsHousePreview = false;
            world.CustomHouseManager = new HouseCustomizationManager(world, 0)
            {
                StartPos = new GUO.Compat.Point(tx - 2, ty - 2),
                EndPos = new GUO.Compat.Point(tx + 2, ty + 2)
            };
            Check(Pass(), "customization ignores non-internal custom wall");
            wall.State = CUSTOM_HOUSE_MULTI_OBJECT_FLAGS.CHMOF_GENERIC_INTERNAL;
            Check(!Pass(), "customization includes internal custom wall");
            wall.RemoveFromTile();
            Check(Pass(), "removing custom wall restores movement");
            world.CustomHouseManager.StartPos = new GUO.Compat.Point(tx + 1, ty + 1);
            Check(!Pass(), "customization bounds reject outside cell");

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (string field in new[] { "_currentPathObjects", "_targetPathObjects" })
            {
                object list = typeof(Pathfinder).GetField(field, flags).GetValue(pf);
                Check((int)list.GetType().GetProperty("Count").GetValue(list) == 0, field + " clears references");
                Check((int)list.GetType().GetProperty("Capacity").GetValue(list) <= 256, field + " retains bounded capacity");
            }
            Check(!(bool)typeof(Pathfinder).GetField("_pathListsInUse", flags).GetValue(pf), "query ownership released");
        }
        catch (Exception ex) { Check(false, ex.Message); }
        finally
        {
            wall?.RemoveFromTile();
            wall?.Destroy();
            world.CustomHouseManager = previousManager;
            world.Player.Z = playerZ;
        }
    }
}
#endif
