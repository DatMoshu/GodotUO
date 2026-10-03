namespace CentrED.MapGen.IR;

// Logical zone for each tile inside a procedural rooms-dungeon. Stamped by
// RoomsCarvePass into GenIR.DungeonZone (byte[]) and consumed by downstream
// rooms passes (RoomsStampPass, DungeonDecorScatterPass, DungeonSpawnerEmitPass).
//
// Stored as byte so the IR field is a flat byte[] indexed via GenIR.Index(x,y).
public enum DungeonZone : byte
{
    Outside = 0,    // not part of the dungeon (or wall fill — treated as solid stone)
    Wall = 1,       // explicit wall tile (room border or carved-corridor wall)
    Floor = 2,      // generic floor inside a room or corridor
    Corridor = 3,   // tunnel floor between rooms (differentiated for decor density)
    RoomTrash = 4,  // floor inside a Trash-kind room
    RoomElite = 5,  // floor inside an Elite-kind room
    RoomBoss = 6,   // floor inside a Boss-kind room
    RoomTreasure = 7, // floor inside a Treasure-kind room (chest spawner)
    RoomEmpty = 8,  // floor inside a no-spawner room (decoration only)
}

public enum RoomKind : byte
{
    Trash = 0,
    Elite = 1,
    Boss = 2,
    Treasure = 3,
    Empty = 4,
}

// One generated room. World-absolute coordinates. The bounds are inclusive on
// both axes; an N x M room covers (X1..X1+N-1, Y1..Y1+M-1).
public readonly record struct DungeonRoomRect(
    ushort X1,
    ushort Y1,
    ushort X2,
    ushort Y2,
    RoomKind Kind,
    int Id);
