namespace YodaStoriesNG.Engine.Data;

/// <summary>
/// Represents a game zone/map area.
/// </summary>
public class Zone
{
    public int Id { get; set; }
    public int Width { get; set; }  // 9 or 18
    public int Height { get; set; } // 9 or 18
    public Planet Planet { get; set; }     // Yoda only; Indiana Jones zones are all None
    public ZoneType Type { get; set; }
    public string? Name { get; set; }      // Indiana Jones ZNAM; Yoda Stories has none

    // Tile layers: [y, x, layer] - 3 layers per cell
    public ushort[,,] TileGrid { get; set; } = null!;

    // Objects placed in this zone
    public List<ZoneObject> Objects { get; set; } = new();

    // Action scripts for this zone
    public List<Action> Actions { get; set; } = new();

    // IZAX: monsters placed here, the items the zone's puzzle needs, and (Yoda) the goal
    // items it can hold. IZX2: items the zone can hand out. IZX3: NPCs that can appear.
    // These are what world generation matches puzzles against.
    public List<ZoneMonster> Monsters { get; set; } = new();
    public List<ushort> RequiredItems { get; set; } = new();
    public List<ushort> GoalItems { get; set; } = new();
    public List<ushort> ProvidedItems { get; set; } = new();
    public List<ushort> Npcs { get; set; } = new();

    /// <summary>
    /// Gets the tile ID at the specified position and layer.
    /// </summary>
    public ushort GetTile(int x, int y, int layer)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height || layer < 0 || layer >= 3)
            return 0xFFFF;
        return TileGrid[y, x, layer];
    }

    /// <summary>
    /// Sets the tile ID at the specified position and layer.
    /// </summary>
    public void SetTile(int x, int y, int layer, ushort tileId)
    {
        if (x >= 0 && x < Width && y >= 0 && y < Height && layer >= 0 && layer < 3)
            TileGrid[y, x, layer] = tileId;
    }
}

public enum Planet : byte
{
    None = 0,
    Desert = 1,     // Tatooine
    Snow = 2,       // Hoth
    Forest = 3,     // Endor
    Swamp = 5,      // Dagobah
}

/// <summary>The zone type as stored in the file (WebFun objects/zone-type.ts, knownTypes order).</summary>
public enum ZoneType
{
    None = 0,
    Empty = 1,
    BlockadeNorth = 2,
    BlockadeSouth = 3,
    BlockadeEast = 4,
    BlockadeWest = 5,
    TravelStart = 6,
    TravelEnd = 7,
    Room = 8,
    Load = 9,
    Goal = 10,
    Town = 11,
    UnknownIndyOnly = 12,
    Win = 13,
    Lose = 14,
    Use = 15,
    Trade = 16,
    Find = 17,
    FindUniqueWeapon = 18,
}

/// <summary>
/// An object placed within a zone.
/// </summary>
public class ZoneObject
{
    public ZoneObjectType Type { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public ushort Argument { get; set; }  // Context-dependent (item ID, destination zone, etc.)

    /// <summary>The argument as the data file has it. World generation writes items and NPCs
    /// into <see cref="Argument"/>; each new world starts again from this.</summary>
    public ushort FileArgument { get; set; }

    /// <summary>
    /// Whether the hotspot does anything. Drop-quest-item, spawn, unique-weapon and map
    /// hotspots start off and are switched on by world generation or a solved puzzle;
    /// everything else starts on (WebFun objects/hotspot.ts).
    /// </summary>
    public bool Enabled { get; set; } = true;

    public static bool EnabledByDefault(ZoneObjectType type) => (int)type is not (0 or 1 or 2 or 5);
}

public enum ZoneObjectType : ushort
{
    Trigger = 0x00,
    SpawnLocation = 0x01,
    ForceLocation = 0x02,
    VehicleToSecondary = 0x03,
    VehicleToPrimary = 0x04,
    LocatorItem = 0x05,
    CrateItem = 0x06,
    PuzzleNPC = 0x07,
    CrateWeapon = 0x08,
    DoorEntrance = 0x09,
    DoorExit = 0x0A,
    Unused0B = 0x0B,
    Lock = 0x0C,
    Teleporter = 0x0D,
    XWingFromDagobah = 0x0E,
    XWingToDagobah = 0x0F,
}

/// <summary>
/// A monster placed in a zone (IZAX). Indiana Jones stores only character and position.
/// </summary>
public class ZoneMonster
{
    public ushort CharacterId { get; set; }
    public ushort X { get; set; }
    public ushort Y { get; set; }

    /// <summary>
    /// What it drops when killed, if <see cref="DropsLoot"/>: tile <c>Loot - 1</c> when
    /// positive, otherwise the zone's quest item (WebFun bullet-move.ts).
    /// </summary>
    public short Loot { get; set; } = -1;
    public bool DropsLoot { get; set; }

    /// <summary>Yoda only: eight patrol waypoints, raw.</summary>
    public int[] Waypoints { get; set; } = Array.Empty<int>();
}
