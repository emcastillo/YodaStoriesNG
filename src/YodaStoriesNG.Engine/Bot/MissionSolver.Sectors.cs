using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;

namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// The bot for worlds from <see cref="OriginalWorldGenerator"/> (Indiana Jones). Those
/// worlds know their whole chain, so instead of exploring and trying things the bot reads
/// the mission's current step, works out where in which zone that step's item is used, and
/// goes there: across the grid, in or out through doors, then uses the item on the spot.
/// Enemies and items lying around come first, as for Yoda.
/// </summary>
public partial class MissionSolver
{
    private const ZoneObjectType SpawnLocationHotspot = (ZoneObjectType)1;
    private const ZoneObjectType LockHotspot = (ZoneObjectType)12;
    private const int ThreatRange = 3;

    private string _lastInventory = "";
    private string _lastObjective = "";
    private int _sameObjectiveCount;

    /// <summary>
    /// True once the bot has been handed the same objective from the same spot several times
    /// running - its path is blocked, and on these worlds that is usually a monster sitting in
    /// a corridor. The bot then fights at any range until it moves again.
    /// </summary>
    public bool HuntEnemies => _sameObjectiveCount > 5;

    private BotObjective WithStuckCheck(BotObjective objective)
    {
        var key = $"{objective.Type}|{objective.Description}|{_state.CurrentZoneId}|{_state.PlayerX},{_state.PlayerY}";
        _sameObjectiveCount = key == _lastObjective ? _sameObjectiveCount + 1 : 0;
        _lastObjective = key;

        if (HuntEnemies && FindNearestEnemy() is { } blocker)
            return new BotObjective
            {
                Type = ObjectiveType.KillEnemy, Description = $"Clear {GetNpcName(blocker)} out of the way",
                TargetNpc = blocker, TargetX = blocker.X, TargetY = blocker.Y,
            };

        // Nothing to fight: this zone is probably a puzzle room whose way on opens from a
        // floor switch (Indy's ledge-and-switch rooms). Step on one we have not tried and can reach.
        if (HuntEnemies && UntriedSwitch() is { } sw)
        {
            _triedSwitches.Add((_state.CurrentZoneId, sw.x, sw.y));
            _sameObjectiveCount = 0;
            return new BotObjective { Type = ObjectiveType.WalkToPosition, Description = "Try the floor switch", TargetX = sw.x, TargetY = sw.y };
        }

        // Still stuck: blocks are in the way (a row of boulders over a pressure plate, crates
        // in a corridor). Make the one push or pull that opens up the most ground.
        if (HuntEnemies && Unjam(objective) is { } unjam)
        {
            _sameObjectiveCount = 0;
            return unjam;
        }
        return objective;
    }

    /// <summary>
    /// Greedy block-shifting: try every push and pull of a block next to where the hero can
    /// walk, and pick the one that most enlarges that area - or reaches a switch. Repeated,
    /// it pulls boulders out of a row one at a time. Not a Sokoban solver; it will not undo.
    /// </summary>
    private BotObjective? Unjam(BotObjective stuckOn)
    {
        var zone = _state.CurrentZone;
        if (zone == null) return null;
        var grid = new ushort[zone.Width, zone.Height];
        for (int y = 0; y < zone.Height; y++)
            for (int x = 0; x < zone.Width; x++)
                grid[x, y] = zone.GetTile(x, y, 1);

        bool Solid(ushort t) => t != 0xFFFF && (t >= _gameData.Tiles.Count || _gameData.Tiles[t].IsObject);
        bool Block(ushort t) => t != 0xFFFF && t < _gameData.Tiles.Count && _gameData.Tiles[t].IsDraggable;
        bool In(int x, int y) => x >= 0 && y >= 0 && x < zone.Width && y < zone.Height;

        HashSet<(int, int)> Reach(ushort[,] g, int hx, int hy)
        {
            var seen = new HashSet<(int, int)> { (hx, hy) };
            var q = new Queue<(int, int)>(seen);
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue();
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    if (In(x + dx, y + dy) && !Solid(g[x + dx, y + dy]) && seen.Add((x + dx, y + dy)))
                        q.Enqueue((x + dx, y + dy));
            }
            return seen;
        }

        var here = Reach(grid, _state.PlayerX, _state.PlayerY);
        var switches = zone.Actions.SelectMany(a => a.Conditions)
            .Where(c => c.Opcode == ConditionOpcode.StandingOn && c.Arguments.Count >= 3)
            .Select(c => ((int)c.Arguments[0], (int)c.Arguments[1])).ToHashSet();
        // Aim at untried floor switches, and at whatever the stuck objective wanted to reach -
        // its square, or a square beside it when the thing itself is used or talked to.
        var targets = new List<(int, int)>(switches);
        if (stuckOn.Type is ObjectiveType.UseItemOnNpc or ObjectiveType.TalkToNpc)
            targets.AddRange(new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Select(o => (stuckOn.TargetX + o.Item1, stuckOn.TargetY + o.Item2)));
        else if (stuckOn.Type is ObjectiveType.EnterDoor or ObjectiveType.PickupItem or ObjectiveType.WalkToPosition)
            targets.Add((stuckOn.TargetX, stuckOn.TargetY));
        var goals = targets.Where(g => In(g.Item1, g.Item2) && !here.Contains(g)).ToList();

        // How many blocks stand between the walkable area and the nearest goal (0-1 BFS:
        // stepping onto a block costs one, open floor nothing, walls are walls).
        int BlocksInTheWay(ushort[,] g, HashSet<(int, int)> reach)
        {
            if (goals.Count == 0) return 0;
            var cost = new Dictionary<(int, int), int>();
            var dq = new LinkedList<(int, int)>();
            foreach (var cell in reach) { cost[cell] = 0; dq.AddLast(cell); }
            while (dq.Count > 0)
            {
                var (x, y) = dq.First!.Value;
                dq.RemoveFirst();
                if (goals.Contains((x, y))) return cost[(x, y)];
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (!In(nx, ny)) continue;
                    var t = g[nx, ny];
                    bool block = Block(t);
                    if (Solid(t) && !block) continue;
                    int c = cost[(x, y)] + (block ? 1 : 0);
                    if (cost.TryGetValue((nx, ny), out var old) && old <= c) continue;
                    cost[(nx, ny)] = c;
                    if (block) dq.AddLast((nx, ny)); else dq.AddFirst((nx, ny));
                }
            }
            return 99;
        }

        int Score(ushort[,] g, HashSet<(int, int)> reach) =>
            -1000 * BlocksInTheWay(g, reach) + reach.Count + (goals.Any(reach.Contains) ? 100000 : 0);

        BotObjective? best = null;
        int bestScore = Score(grid, here);
        var dirs = new[] { (Direction.Up, 0, -1), (Direction.Down, 0, 1), (Direction.Left, -1, 0), (Direction.Right, 1, 0) };
        for (int by = 0; by < zone.Height; by++)
            for (int bx = 0; bx < zone.Width; bx++)
            {
                if (!Block(grid[bx, by]) || _unreachablePositions.Contains((_state.CurrentZoneId, bx, by))) continue;
                foreach (var (dir, dx, dy) in dirs)
                {
                    // Push: hero behind (bx-dx, by-dy), block lands on (bx+dx, by+dy), hero ends on (bx, by).
                    if (here.Contains((bx - dx, by - dy)) && In(bx + dx, by + dy) && !Solid(grid[bx + dx, by + dy]))
                        Consider(bx, by, bx + dx, by + dy, bx, by, ObjectiveType.PushObject, dir);
                    // Pull: hero on (bx+dx, by+dy), steps to (bx+2dx, by+2dy), block follows onto (bx+dx, by+dy).
                    if (here.Contains((bx + dx, by + dy)) && In(bx + 2 * dx, by + 2 * dy) && !Solid(grid[bx + 2 * dx, by + 2 * dy]))
                        Consider(bx, by, bx + dx, by + dy, bx + 2 * dx, by + 2 * dy, ObjectiveType.PullObject, dir);
                }
            }
        return best;

        void Consider(int fromX, int fromY, int toX, int toY, int heroX, int heroY, ObjectiveType type, Direction dir)
        {
            var g = (ushort[,])grid.Clone();
            g[toX, toY] = g[fromX, fromY];
            g[fromX, fromY] = 0xFFFF;
            var after = Reach(g, heroX, heroY);
            int score = Score(g, after);
            if (score <= bestScore) return;
            bestScore = score;
            best = new BotObjective
            {
                Type = type, Description = $"{(type == ObjectiveType.PullObject ? "Pull" : "Push")} a block out of the way",
                TargetX = fromX, TargetY = fromY, Direction = dir,
            };
        }
    }

    private readonly HashSet<(int zone, int x, int y)> _triedSwitches = new();

    /// <summary>A StandingOn trigger in this zone whose tile is actually there now.</summary>
    private (int x, int y)? UntriedSwitch()
    {
        var zone = _state.CurrentZone;
        if (zone == null) return null;
        foreach (var c in zone.Actions.SelectMany(a => a.Conditions))
        {
            if (c.Opcode != ConditionOpcode.StandingOn || c.Arguments.Count < 3) continue;
            int x = c.Arguments[0], y = c.Arguments[1], t = c.Arguments[2];
            if (_triedSwitches.Contains((_state.CurrentZoneId, x, y))) continue;
            // Walkable now: the floor is the switch tile and nothing stands on it.
            if (zone.GetTile(x, y, 0) == t && zone.GetTile(x, y, 1) == 0xFFFF)
                return (x, y);
        }
        return null;
    }

    private BotObjective? GetSectorObjective(WorldMap world)
    {
        // Using or gaining an item is what opens blockades and gates; exits found shut before
        // may be open now.
        var inventory = string.Join(",", _state.Inventory);
        if (inventory != _lastInventory)
        {
            _lastInventory = inventory;
            _blockedExits.Clear();
        }

        // Fight what is close enough to be a threat; one stuck behind scenery across the
        // zone would otherwise hold the whole mission up.
        if (FindNearestEnemy() is { } enemy && enemy.DistanceTo(_state.PlayerX, _state.PlayerY) <= ThreatRange)
            return new BotObjective
            {
                Type = ObjectiveType.KillEnemy, Description = $"Defeat {GetNpcName(enemy)}",
                TargetNpc = enemy, TargetX = enemy.X, TargetY = enemy.Y,
            };

        if (FindTileItem() is { } loot)
            return new BotObjective
            {
                Type = ObjectiveType.PickupItem, Description = "Pick up item", TargetX = loot.X, TargetY = loot.Y,
            };

        if (world.Mission?.CurrentPuzzleStep is not { ZoneId: { } stepZone } step)
            return null;

        int item = step.RequiredItemId;
        // Not in hand yet: the last puzzle put it somewhere (an Offer's drop spot, maybe in
        // another room) - go and get it.
        if (item > 0 && !_state.HasItem(item))
            return FetchItem(world, item);

        if (FindUseSpot(world, stepZone, item) is not { } spot)
            return null;

        if (_state.CurrentZoneId != spot.zone)
        {
            // Not on the grid from here - an island, or behind a blockade: clear that first.
            if (!ZoneAndRooms(stepZone).Contains(_state.CurrentZoneId) && !CanReach(world, stepZone))
            {
                if (OpenTheWay(world, stepZone) is { } prerequisite)
                    return prerequisite;
                // Nothing left to open: the shut exits were probably only awkward terrain the
                // bot misjudged. Forget them and try the walk again.
                _blockedExits.Clear();
            }
            return NavigateToward(world, spot.zone, stepZone);
        }

        var target = new NPC { X = spot.x, Y = spot.y, IsEnabled = true, Health = 1 };
        return item > 0
            ? new BotObjective
            {
                Type = ObjectiveType.UseItemOnNpc, Description = $"Use item {item} ({step.Hint})",
                TargetNpc = target, TargetX = spot.x, TargetY = spot.y, RequiredItemId = item,
            }
            : new BotObjective
            {
                Type = ObjectiveType.TalkToNpc, Description = step.Hint,
                TargetNpc = target, TargetX = spot.x, TargetY = spot.y,
            };
    }

    /// <summary>
    /// Where in <paramref name="zoneId"/> or the rooms behind its doors <paramref name="item"/>
    /// is used: the trader standing on the sector's spawn hotspot, a Lock holding the item,
    /// a script that waits for it to be placed - or, for item 0, Marcus's desk.
    /// </summary>
    private (int zone, int x, int y)? FindUseSpot(WorldMap world, int zoneId, int item)
    {
        world.Sectors.TryGetValue(zoneId, out var sector);
        var rooms = ZoneAndRooms(zoneId).ToList();

        // A script that takes the item here and now comes first. Puzzle rooms roll a layout
        // on entry (which ledge the ladder goes on); only the matching script opens the way,
        // while the generator's Lock hotspot would accept the item anywhere and waste it.
        if (item > 0)
            foreach (var id in rooms)
            {
                var zone = _gameData.Zones[id];
                foreach (var action in zone.Actions)
                {
                    var placed = action.Conditions.FirstOrDefault(c => c.Opcode == ConditionOpcode.PlacedItemIs && c.Arguments.Count >= 5);
                    if (placed == null) continue;
                    var a = placed.Arguments;
                    if (!(a[4] == item || a[4] == -1 && sector?.RequiredItem == item)) continue;
                    if (zone.GetTile(a[0], a[1], a[2]) != a[3]) continue;
                    if (!StateAllows(id, action)) continue;
                    return (id, a[0], a[1]);
                }
            }

        foreach (var id in rooms)
        {
            var zone = _gameData.Zones[id];
            foreach (var h in zone.Objects)
            {
                if (item == 0 && h.Type == SpawnLocationHotspot && !HasBumpScript(zone, h.X, h.Y))
                    return (id, h.X, h.Y);
                // The generator wrote the trader into the hotspot; the tile only appears on a visit.
                if (item > 0 && h.Type == SpawnLocationHotspot && sector != null && sector.Npc >= 0 &&
                    h.Argument == sector.Npc)
                    return (id, h.X, h.Y);
                if (item > 0 && h.Type == LockHotspot && h.Argument == item)
                    return (id, h.X, h.Y);
            }
        }
        return null;
    }

    /// <summary>
    /// Whether an action's zone-state conditions hold: the zone's rolled random and counter.
    /// (A zone not yet visited has rolled nothing - accept, the visit will tell.)
    /// </summary>
    private bool StateAllows(int zoneId, Data.Action action)
    {
        bool visited = _state.ZoneRandom.ContainsKey(zoneId);
        int random = _state.ZoneRandom.GetValueOrDefault(zoneId);
        int counter = _state.GetCounter(zoneId);
        foreach (var c in action.Conditions)
        {
            if (c.Arguments.Count < 1) continue;
            int v = c.Arguments[0];
            bool ok = c.Opcode switch
            {
                ConditionOpcode.RandomIs => !visited || random == v,
                ConditionOpcode.RandomIsGreaterThan => !visited || random > v,
                ConditionOpcode.RandomIsLessThan => !visited || random < v,
                ConditionOpcode.CounterIs => counter == v,
                _ => true,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// The way to <paramref name="target"/> is shut: by water (an island only a vehicle reaches)
    /// or by a blockade. Either way a sector wants an item first, which a Find sector holds.
    /// Returns the next thing to do about it, or null if nothing obvious is left.
    /// </summary>
    private BotObjective? OpenTheWay(WorldMap world, int target)
    {
        var island = Component(world, target);
        var here = world.Sectors.TryGetValue(_state.CurrentZoneId, out var at) ? at.Zone : _state.CurrentZoneId;
        bool acrossWater = !island.Contains(here);

        // Across water: the vehicle that lands there. On land: the nearest blockade still standing.
        var gate = acrossWater
            ? world.Sectors.Values.Distinct().FirstOrDefault(s =>
                  s.Type == ZoneType.TravelStart && VehicleOf(s.Zone) is { } v && island.Contains(v.Argument))
            : world.Sectors.Values.Distinct()
                .Where(s => s.Type is ZoneType.BlockadeNorth or ZoneType.BlockadeSouth or ZoneType.BlockadeEast or ZoneType.BlockadeWest &&
                            !s.Solved && s.RequiredItem >= 0)
                .OrderBy(s => GridDistance(world, s.Zone)) // the one we are standing in first
                .FirstOrDefault();
        if (gate == null) return null;

        if (!gate.Solved && gate.RequiredItem >= 0)
        {
            if (!_state.HasItem(gate.RequiredItem))
                return FetchItem(world, gate.RequiredItem);
            if (FindUseSpot(world, gate.Zone, gate.RequiredItem) is { } use)
                return _state.CurrentZoneId == use.zone
                    ? UseAt(gate.RequiredItem, use.x, use.y, "Open the way")
                    : NavigateToward(world, use.zone, gate.Zone);
        }

        if (gate.Type == ZoneType.TravelStart && VehicleOf(gate.Zone) is { } vehicle)
            return _state.CurrentZoneId == gate.Zone
                ? new BotObjective { Type = ObjectiveType.WalkToPosition, Description = "Take the vehicle", TargetX = vehicle.X, TargetY = vehicle.Y }
                : NavigateToward(world, gate.Zone, gate.Zone);
        return null;
    }

    /// <summary>Go to where <paramref name="item"/> lies - a Find sector's hotspot, maybe in a room.</summary>
    private BotObjective? FetchItem(WorldMap world, int item)
    {
        foreach (var finder in world.Sectors.Values.Distinct().Where(s => s.FindItem == item))
            foreach (var id in ZoneAndRooms(finder.Zone))
                if (_gameData.Zones[id].Objects.FirstOrDefault(o => o.Argument == item) is { } spot)
                {
                    if (_state.CurrentZoneId != id)
                        return NavigateToward(world, id, finder.Zone);
                    // Only if it is actually lying there: Find zones hide items behind their own
                    // puzzles (a locked chest), which this bot does not solve yet.
                    if (_state.CurrentZone!.GetTile(spot.X, spot.Y, 1) == item &&
                        !_unreachablePositions.Contains((id, spot.X, spot.Y)))
                        return new BotObjective { Type = ObjectiveType.PickupItem, Description = $"Fetch item {item}", TargetX = spot.X, TargetY = spot.Y };
                    // Under a crate: push it off, from a side where the square beyond is free.
                    if (PushOff(spot.X, spot.Y) is { } push)
                        return push;
                    Console.WriteLine($"[BOT] Item {item} is at ({spot.X},{spot.Y}) in zone {id} but not reachable yet (tile {_state.CurrentZone.GetTile(spot.X, spot.Y, 1)})");
                    return null;
                }
        Console.WriteLine($"[BOT] Nowhere to fetch item {item} from: no hotspot holds it");
        return null;
    }

    private BotObjective? PushOff(int x, int y)
    {
        var zone = _state.CurrentZone!;
        var tile = zone.GetTile(x, y, 1);
        if (tile == 0xFFFF || tile >= _gameData.Tiles.Count || !_gameData.Tiles[tile].IsDraggable) return null;
        if (_unreachablePositions.Contains((_state.CurrentZoneId, x, y))) return null;

        foreach (var (dir, dx, dy) in new[] { (Direction.Up, 0, -1), (Direction.Down, 0, 1), (Direction.Left, -1, 0), (Direction.Right, 1, 0) })
        {
            int bx = x + dx, by = y + dy;      // where the crate lands
            int fx = x - dx, fy = y - dy;      // where the hero pushes from
            if (bx < 0 || by < 0 || bx >= zone.Width || by >= zone.Height) continue;
            if (fx < 0 || fy < 0 || fx >= zone.Width || fy >= zone.Height) continue;
            var landing = zone.GetTile(bx, by, 1);
            if (landing != 0xFFFF && (landing >= _gameData.Tiles.Count || _gameData.Tiles[landing].IsObject)) continue;
            var from = zone.GetTile(fx, fy, 1);
            if (from != 0xFFFF && (from >= _gameData.Tiles.Count || _gameData.Tiles[from].IsObject)) continue;
            return new BotObjective
            {
                Type = ObjectiveType.PushObject, Description = "Push the crate off the item",
                TargetX = x, TargetY = y, Direction = dir,
            };
        }

        // Wedged: pull it instead - stand on an open side and step away from it.
        bool Open(int px, int py) => px >= 0 && py >= 0 && px < zone.Width && py < zone.Height &&
            zone.GetTile(px, py, 1) is var t && (t == 0xFFFF || t < _gameData.Tiles.Count && !_gameData.Tiles[t].IsObject);
        foreach (var (dir, dx, dy) in new[] { (Direction.Up, 0, -1), (Direction.Down, 0, 1), (Direction.Left, -1, 0), (Direction.Right, 1, 0) })
        {
            if (!Open(x + dx, y + dy) || !Open(x + 2 * dx, y + 2 * dy)) continue;
            return new BotObjective
            {
                Type = ObjectiveType.PullObject, Description = "Pull the crate off the item",
                TargetX = x, TargetY = y, Direction = dir,
            };
        }
        return null;
    }

    private static BotObjective UseAt(int item, int x, int y, string why) => new()
    {
        Type = ObjectiveType.UseItemOnNpc, Description = $"{why}: use item {item}",
        TargetNpc = new NPC { X = x, Y = y, IsEnabled = true, Health = 1 }, TargetX = x, TargetY = y, RequiredItemId = item,
    };

    private int GridDistance(WorldMap world, int zoneId)
    {
        var here = world.Sectors.TryGetValue(_state.CurrentZoneId, out var s) ? s.Zone : _state.CurrentZoneId;
        if (world.Grid == null) return 0;
        var (hx, hy) = FindCell(world, here);
        var (tx, ty) = FindCell(world, zoneId);
        return Math.Abs(hx - tx) + Math.Abs(hy - ty);
    }

    private static (int x, int y) FindCell(WorldMap world, int zoneId)
    {
        for (int y = 0; y < world.Grid!.GetLength(0); y++)
            for (int x = 0; x < world.Grid.GetLength(1); x++)
                if (world.Grid[y, x] == zoneId) return (x, y);
        return (100, 100);
    }

    private ZoneObject? VehicleOf(int zoneId) =>
        _gameData.Zones[zoneId].Objects.FirstOrDefault(o => o.Type == ZoneObjectType.VehicleToSecondary);

    /// <summary>Grid zones joined to <paramref name="zoneId"/> by walking.</summary>
    private static HashSet<int> Component(WorldMap world, int zoneId)
    {
        var seen = new HashSet<int> { zoneId };
        var pending = new Queue<int>(seen);
        while (pending.Count > 0 && world.Connections.TryGetValue(pending.Dequeue(), out var c))
            foreach (var next in new[] { c.North, c.South, c.East, c.West })
                if (next is { } n && seen.Add(n)) pending.Enqueue(n);
        return seen;
    }

    private bool CanReach(WorldMap world, int zoneId)
    {
        var here = world.Sectors.TryGetValue(_state.CurrentZoneId, out var s) ? s.Zone : _state.CurrentZoneId;
        if (!Component(world, here).Contains(zoneId)) return false; // across water
        return here == zoneId || GetDirectionTowardZone(zoneId) != null;
    }

    private static bool HasBumpScript(Zone zone, int x, int y) =>
        zone.Actions.Any(a => a.Conditions.Any(c =>
            c.Opcode == ConditionOpcode.Bump && c.Arguments.Count > 1 && c.Arguments[0] == x && c.Arguments[1] == y));

    /// <summary>One step toward <paramref name="targetZone"/>: out of this room, into the right door, or across the grid.</summary>
    private BotObjective NavigateToward(WorldMap world, int targetZone, int outdoor)
    {
        var current = _state.CurrentZone!;
        var inside = ZoneAndRooms(_state.CurrentZoneId).ToHashSet();

        // The target is behind one of our doors: take the door that leads towards it.
        if (inside.Contains(targetZone))
        {
            foreach (var door in current.Objects.Where(o => o.Type == ZoneObjectType.DoorEntrance))
                if (ZoneAndRooms(door.Argument).Contains(targetZone))
                    return new BotObjective
                    {
                        Type = ObjectiveType.EnterDoor, Description = $"Door toward zone {targetZone}",
                        TargetX = door.X, TargetY = door.Y,
                    };
        }

        // In a room that is not on the way: leave it.
        if (!world.Connections.ContainsKey(_state.CurrentZoneId) &&
            current.Objects.FirstOrDefault(o => o.Type == ZoneObjectType.DoorExit) is { } exit)
            return new BotObjective
            {
                Type = ObjectiveType.WalkToPosition, Description = "Leave the room", TargetX = exit.X, TargetY = exit.Y,
            };

        // Outdoors: head for the grid zone the target belongs to. (Passed in rather than
        // looked up: a room behind several zones' doors cannot say which one it belongs to.)
        return new BotObjective
        {
            Type = ObjectiveType.ChangeZone, Description = $"Travel to zone {outdoor}", TargetZoneId = outdoor,
        };
    }

    private IEnumerable<int> ZoneAndRooms(int zoneId)
    {
        var seen = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(zoneId);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id < 0 || id >= _gameData.Zones.Count || !seen.Add(id)) continue;
            yield return id;
            foreach (var door in _gameData.Zones[id].Objects.Where(o => o.Type == ZoneObjectType.DoorEntrance))
                pending.Push(door.Argument);
        }
    }
}
