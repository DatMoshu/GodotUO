#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GUO.Game;

/// <summary>
/// Phase 6's smoke stage (ADR-0014): the World tab's world-objects layer.
/// In a project of its own, places one decoration item and one spawner,
/// moves the item, places and deletes a third object, checks the embedded
/// world and <c>shard/objects.json</c> after each step, then reopens the
/// project from disk and checks both are drawn again. The project is left
/// for tools/editor_smoke to export.
/// </summary>
public partial class EditorSmoke
{
    public const ushort ObjectsItem = 0x0FAF;          // an anvil
    public const string ObjectsSpawn = "Horse";
    public const int ObjectsItemX = 1167, ObjectsItemY = 1666;
    public const int ObjectsMovedX = 1168, ObjectsMovedY = 1667;
    public const int ObjectsSpawnerX = 1165, ObjectsSpawnerY = 1668;

    private readonly Dictionary<string, object> _objectsReport = new();

    private void ObjectsFail(string why)
    {
        _objectsReport["ok"] = false;
        WorldFail($"objects: {why}");
    }

    private bool ItemDrawnAt(ushort graphic, int x, int y)
    {
        var chunk = _world.Host.World.Map.GetChunk2(x >> 3, y >> 3, load: true);
        for (var o = chunk?.GetHeadObject(x & 7, y & 7); o != null; o = o.TNext)
        {
            if (o is GUO.Game.GameObjects.Item && o.Graphic == graphic)
            {
                return true;
            }
        }

        return false;
    }

    private void RunObjects()
    {
        if (!_world.IsBooted)
        {
            return;
        }

        _worldReport["objects"] = _objectsReport;
        string root = Path.Combine(_out, $"objects_project{Suffix}");
        _objectsReport["project"] = root;

        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            _world.OpenProject(root);
            _world.GoTo(0, ObjectsItemX, ObjectsItemY);
            ObjectLayer layer = _world.Objects;
            var map = _world.Host.World.Map;
            if (layer.Objects == null || layer.Objects.Count != 0)
            {
                ObjectsFail("a new project's object layer is not empty");
                return;
            }

            ShardItem item = layer.PlaceItem(0, ObjectsItemX, ObjectsItemY, map.GetTileZ(ObjectsItemX, ObjectsItemY), ObjectsItem, 0);
            ShardSpawner spawner = layer.PlaceSpawner(0, ObjectsSpawnerX, ObjectsSpawnerY,
                map.GetTileZ(ObjectsSpawnerX, ObjectsSpawnerY), ObjectsSpawn);
            _objectsReport["placed_drawn"] = layer.DrawnCount;
            _objectsReport["item_drawn"] = ItemDrawnAt(ObjectsItem, ObjectsItemX, ObjectsItemY);
            _objectsReport["spawner_drawn"] = ItemDrawnAt(ObjectLayer.SpawnerGraphic, ObjectsSpawnerX, ObjectsSpawnerY);
            if (item == null || spawner == null || layer.DrawnCount != 2
                || !(bool)_objectsReport["item_drawn"] || !(bool)_objectsReport["spawner_drawn"])
            {
                ObjectsFail($"placing: {layer.DrawnCount} drawn, item {_objectsReport["item_drawn"]}, spawner {_objectsReport["spawner_drawn"]}");
            }

            // Move the item; the old cell loses it.
            layer.Move(item.Id, 0, ObjectsMovedX, ObjectsMovedY, map.GetTileZ(ObjectsMovedX, ObjectsMovedY));
            bool moved = ItemDrawnAt(ObjectsItem, ObjectsMovedX, ObjectsMovedY) && !ItemDrawnAt(ObjectsItem, ObjectsItemX, ObjectsItemY);
            _objectsReport["moved"] = moved;
            if (!moved)
            {
                ObjectsFail("the moved item is not at its new cell only");
            }

            // A third object, deleted again.
            ShardItem extra = layer.PlaceItem(0, ObjectsItemX, ObjectsItemY, map.GetTileZ(ObjectsItemX, ObjectsItemY), 0x0E75, 0);
            bool deleted = extra != null && layer.Delete(extra.Id) && layer.DrawnCount == 2
                           && !ItemDrawnAt(0x0E75, ObjectsItemX, ObjectsItemY);
            _objectsReport["deleted"] = deleted;
            if (!deleted)
            {
                ObjectsFail("deleting the third object did not take it out");
            }

            // The file holds exactly the result, one object per line.
            string file = layer.Objects.Path;
            string text = File.ReadAllText(file);
            _objectsReport["file"] = file;
            _objectsReport["file_lines_with_ids"] = text.Split('\n').Count(l => l.Contains("\"id\""));
            if (!text.Contains(item.Id.ToString()) || !text.Contains(spawner.Id.ToString())
                || text.Contains(extra?.Id.ToString() ?? "-") || (int)_objectsReport["file_lines_with_ids"] != 2)
            {
                ObjectsFail("shard/objects.json does not hold exactly the moved item and the spawner");
            }

            // Reopen from disk: the same two, drawn again.
            _world.OpenProject(root);
            _objectsReport["reopened_count"] = layer.Objects.Count;
            _objectsReport["reopened_drawn"] = layer.DrawnCount;
            if (layer.Objects.Count != 2 || layer.DrawnCount != 2 || !ItemDrawnAt(ObjectsItem, ObjectsMovedX, ObjectsMovedY))
            {
                ObjectsFail($"reopened: {layer.Objects.Count} objects, {layer.DrawnCount} drawn");
            }

            _objectsReport["item_id"] = item.Id.ToString();
            _objectsReport["spawner_id"] = spawner.Id.ToString();
        }
        catch (Exception ex)
        {
            ObjectsFail($"{ex.GetType().Name}: {ex.Message}");
        }

        _objectsReport.TryAdd("ok", true);
    }
}
#endif
