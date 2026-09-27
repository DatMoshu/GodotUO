#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The live world-objects role (<c>--guo-editor-live objects</c>, ADR-0014):
/// driven by tools/editor_objects_proof --live through trigger files in the
/// output folder. <c>put</c> places an anvil and a Horse spawner through the
/// World tab's object layer, <c>move</c> moves the anvil, <c>delete</c>
/// deletes both. Each step waits for the shard's acknowledgements and
/// answers with <c>&lt;step&gt;.done</c>, holding them.
/// </summary>
public partial class EditorSmoke
{
    public const int LiveItemX = 1167, LiveItemY = 1666;
    public const int LiveMovedX = 1166, LiveMovedY = 1670;
    public const int LiveSpawnerX = 1162, LiveSpawnerY = 1669;

    private int _objStep;
    private int _acksBefore;
    private int _acksWanted;
    private Guid _liveItem, _liveSpawner;
    private readonly List<object> _objectSteps = new();

    private void StepLiveObjects()
    {
        var map = _world.Host.World.Map;
        string[] steps = { "put", "move", "delete" };
        if (_objStep >= steps.Length)
        {
            _live["object_steps"] = _objectSteps;
            _live["ok"] = true;
            Finish();
            return;
        }

        string step = steps[_objStep];
        if (_acksWanted == 0)
        {
            if (!File.Exists(Path.Combine(_out, step)))
            {
                return;
            }

            _acksBefore = _shard.ObjectAcks.Count;
            ObjectLayer layer = _world.Objects;
            switch (step)
            {
                case "put":
                    _liveItem = layer.PlaceItem(_liveFacet, LiveItemX, LiveItemY, map.GetTileZ(LiveItemX, LiveItemY), ObjectsItem, 0).Id;
                    _liveSpawner = layer.PlaceSpawner(_liveFacet, LiveSpawnerX, LiveSpawnerY,
                        map.GetTileZ(LiveSpawnerX, LiveSpawnerY), ObjectsSpawn).Id;
                    _acksWanted = 2;
                    break;
                case "move":
                    layer.Move(_liveItem, _liveFacet, LiveMovedX, LiveMovedY, map.GetTileZ(LiveMovedX, LiveMovedY));
                    _acksWanted = 1;
                    break;
                default:
                    layer.Delete(_liveItem);
                    layer.Delete(_liveSpawner);
                    _acksWanted = 2;
                    break;
            }

            _elapsed = 0;
            return;
        }

        if (_shard.ObjectAcks.Count - _acksBefore >= _acksWanted)
        {
            var acks = _shard.ObjectAcks.Skip(_acksBefore).Take(_acksWanted).Select(a => JsonNode.Parse(a.ToJsonString())).ToArray();
            _objectSteps.Add(new Dictionary<string, object> { ["step"] = step, ["acks"] = acks.Select(a => a.ToJsonString()).ToArray() });
            File.WriteAllText(Path.Combine(_out, step + ".done"), new JsonArray(acks).ToJsonString());
            _acksWanted = 0;
            _objStep++;
        }
        else if (_elapsed > 60)
        {
            _failures.Add($"live objects: no acknowledgement for {step} within 60 s");
            Finish();
        }
    }
}
#endif
