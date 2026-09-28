// Authored multis on the private shard (tools/multi, ADR-0022).
//
// {"op":"multi","action":"place","id":16128,"map":0,"x":1500,"y":1600,"z":0,"tag":"cottage",
//  "doors":[{"x":1,"y":3,"z":7,"facing":"WestCW","type":"DarkWoodHouseDoor"}, ...]}
//   puts a GUOAuthoredMulti of that multi id at x,y,z (z omitted: the land's average z),
//   then a real door per entry, at the multi's centre plus x,y and its z plus z, as
//   BaseHouse.AddSouthDoor/AddEastDoor do. A multi already carrying the tag is
//   removed first, so placing again replaces it.
// {"op":"multi","action":"remove","tag":"cottage"}
//   deletes the multi with that tag and its doors.
//
// The reply is {"op":"multi_ack","action":...,"ok":true,"tag":...,"serial":...,"doors":n}
// or ok false with an error.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Server;
using Server.Items;

namespace GUO.EditorBridge;

// A plain multi: its components come from the multi data (a staged MultiCollection.uop
// the shard lists first), and it owns the doors placed with it.
public class GUOAuthoredMulti : BaseMulti
{
    public string Tag { get; set; }
    public List<Item> Doors { get; } = new();

    public GUOAuthoredMulti(int multiID) : base(multiID)
    {
    }

    public GUOAuthoredMulti(Serial serial) : base(serial)
    {
    }

    public override void Serialize(IGenericWriter writer)
    {
        base.Serialize(writer);
        writer.WriteEncodedInt(0); // version
        writer.Write(Tag ?? "");
        writer.WriteEncodedInt(Doors.Count);
        foreach (Item d in Doors)
        {
            writer.Write(d);
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        base.Deserialize(reader);
        reader.ReadEncodedInt(); // version
        Tag = reader.ReadString();
        int n = reader.ReadEncodedInt();
        for (int i = 0; i < n; i++)
        {
            Item d = reader.ReadEntity<Item>();
            if (d != null)
            {
                Doors.Add(d);
            }
        }
    }

    public override void OnAfterDelete()
    {
        base.OnAfterDelete();
        foreach (Item d in Doors)
        {
            d?.Delete();
        }
    }
}

public static class AuthoredMultis
{
    public static void Handle(JsonNode msg, Action<JsonObject> send)
    {
        string action = (string)msg["action"] ?? "place";
        string tag = (string)msg["tag"] ?? "";
        var reply = new JsonObject { ["op"] = "multi_ack", ["action"] = action, ["tag"] = tag };
        try
        {
            int removed = Remove(tag);
            if (action == "remove")
            {
                reply["ok"] = removed > 0;
                reply["removed"] = removed;
                send(reply);
                return;
            }

            Map map = Map.Maps[(int?)msg["map"] ?? 0];
            int id = (int)msg["id"];
            int x = (int)msg["x"], y = (int)msg["y"];
            int z = (int?)msg["z"] ?? map.GetAverageZ(x, y);
            var multi = new GUOAuthoredMulti(id) { Tag = tag };
            if (multi.Components.List.Length == 0)
            {
                multi.Delete();
                throw new InvalidOperationException($"the shard has no multi {id:X4}: is the stage first in its data directories?");
            }

            multi.MoveToWorld(new Point3D(x, y, z), map);
            foreach (JsonNode d in (JsonArray)msg["doors"] ?? new JsonArray())
            {
                string typeName = (string)d["type"] ?? "DarkWoodHouseDoor";
                Type t = AssemblyHandler.FindTypeByName(typeName) ?? throw new InvalidOperationException($"no door type {typeName}");
                var facing = Enum.Parse<DoorFacing>((string)d["facing"] ?? "WestCW");
                var door = (Item)Activator.CreateInstance(t, facing);
                door.MoveToWorld(new Point3D(x + (int)d["x"], y + (int)d["y"], z + (int)d["z"]), map);
                multi.Doors.Add(door);
            }

            reply["ok"] = true;
            reply["serial"] = multi.Serial.Value;
            reply["replaced"] = removed;
            reply["doors"] = multi.Doors.Count;
            reply["at"] = new JsonArray(x, y, z);
            reply["components"] = multi.Components.List.Length;
        }
        catch (Exception ex)
        {
            reply["ok"] = false;
            reply["error"] = ex.Message;
        }

        send(reply);
    }

    private static int Remove(string tag)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return 0;
        }

        var found = World.Items.Values.OfType<GUOAuthoredMulti>().Where(m => m.Tag == tag).ToList();
        foreach (GUOAuthoredMulti m in found)
        {
            m.Delete();
        }

        return found.Count;
    }
}
