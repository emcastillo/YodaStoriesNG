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

        // (Only one we can get at: from a sealed pocket every monster outside is out of reach,
        // and chasing them kept the bot from trying the trapdoor out.)
        if (HuntEnemies && FindNearestEnemy() is { } blocker && CanGetAt(blocker.X, blocker.Y))
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
            // Moving blocks can uncover a switch that was tried while it was buried.
            _triedSwitches.RemoveWhere(t => t.zone == _state.CurrentZoneId);
            return unjam;
        }
        return objective;
    }

    /// <summary>
    /// The blocks are in the way (a row of boulders over a pressure plate, crates in a
    /// corridor): search pushes and pulls breadth-first for the shortest sequence that lets
    /// the hero reach a goal - a floor switch, or whatever the stuck objective wanted - and
    /// return its first move. Called again after each move, so the plan follows the room.
    /// </summary>
    private BotObjective? Unjam(BotObjective stuckOn)
    {
        var zone = _state.CurrentZone;
        if (zone == null) return null;
        var grid = new ushort[zone.Width, zone.Height];
        for (int y = 0; y < zone.Height; y++)
            for (int x = 0; x < zone.Width; x++)
                grid[x, y] = zone.GetTile(x, y, 1);

        // Only switches whose tile is really there: rooms roll which of several trapdoors exists.
        var switches = zone.Actions.SelectMany(a => a.Conditions)
            .Where(c => c.Opcode == ConditionOpcode.StandingOn && c.Arguments.Count >= 3 &&
                        zone.GetTile(c.Arguments[0], c.Arguments[1], 0) == c.Arguments[2])
            .Select(c => ((int)c.Arguments[0], (int)c.Arguments[1]));
        // Aim at the floor switches, and at whatever the stuck objective wanted to reach -
        // its square, or a square beside it when the thing itself is used or talked to.
        var targets = new List<(int, int)>(switches);
        if (stuckOn.Type is ObjectiveType.UseItemOnNpc or ObjectiveType.TalkToNpc)
            targets.AddRange(new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Select(o => (stuckOn.TargetX + o.Item1, stuckOn.TargetY + o.Item2)));
        else if (stuckOn.Type is ObjectiveType.EnterDoor or ObjectiveType.PickupItem or ObjectiveType.WalkToPosition)
            targets.Add((stuckOn.TargetX, stuckOn.TargetY));

        var move = BlockPuzzle.FirstMove(grid, t => t < _gameData.Tiles.Count && _gameData.Tiles[t].IsDraggable,
            t => t >= _gameData.Tiles.Count || _gameData.Tiles[t].IsSolid,
            (_state.PlayerX, _state.PlayerY), targets.ToHashSet());
        if (move is not { } m) return null;
        return new BotObjective
        {
            Type = m.Pull ? ObjectiveType.PullObject : ObjectiveType.PushObject,
            Description = $"{(m.Pull ? "Pull" : "Push")} a block out of the way",
            TargetX = m.X, TargetY = m.Y,
            Direction = (m.Dx, m.Dy) switch { (0, -1) => Direction.Up, (0, 1) => Direction.Down, (-1, 0) => Direction.Left, _ => Direction.Right },
        };
    }

    public bool CanGetAt(int x, int y)
    {
        var reach = ReachableWithoutPushing(_state.CurrentZone!);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (reach.Contains((x + dx, y + dy))) return true;
        return false;
    }

    private readonly HashSet<(int zone, int x, int y)> _triedSwitches = new();

    private HashSet<(int x, int y)> ReachableWithoutPushing(Zone zone)
    {
        var grid = new ushort[zone.Width, zone.Height];
        for (int y = 0; y < zone.Height; y++)
            for (int x = 0; x < zone.Width; x++)
                grid[x, y] = zone.GetTile(x, y, 1);
        return BlockPuzzle.Reach(grid, t => t < _gameData.Tiles.Count && _gameData.Tiles[t].IsDraggable,
            t => t >= _gameData.Tiles.Count || _gameData.Tiles[t].IsSolid, (_state.PlayerX, _state.PlayerY));
    }

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
            // Walkable now: the floor is the switch tile, nothing stands on it, and the hero
            // can get there without shoving a block (which is how switches get buried again).
            if (zone.GetTile(x, y, 0) == t && zone.GetTile(x, y, 1) == 0xFFFF &&
                ReachableWithoutPushing(zone).Contains((x, y)))
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
            return _state.IsGameWon ? null : FinishGame(world);

        int item = step.RequiredItemId;

        // In the step's rooms, bump whatever a script answers with more than words - the
        // barrier that brings Van Loon over, the cage, Pacal himself (who wins the game).
        // (The goal only: elsewhere townsfolk's hint scripts would be bumped for ever.)
        if (world.Sectors.TryGetValue(stepZone, out var goal) && goal.Type == ZoneType.Goal &&
            ZoneAndRooms(stepZone).Contains(_state.CurrentZoneId) && UsefulBump() is { } bump)
            return bump;
        // Not in hand yet: the last puzzle put it somewhere (an Offer's drop spot, maybe in
        // another room) - go and get it.
        if (item > 0 && !_state.HasItem(item))
            return FetchItem(world, item);

        if (FindUseSpot(world, stepZone, item) is not { } spot)
            return null;

        if (_state.CurrentZoneId != spot.zone)
        {
            // Standing in a blockade that faces the way we are going (the stuck Olmec head):
            // the grid says walk on, the head says otherwise.
            // Kept until solved: its item is often fetched from the next zone, where the grid
            // would otherwise send the bot straight back.
            if (world.Sectors.TryGetValue(_state.CurrentZoneId, out var block) && block.Zone == _state.CurrentZoneId &&
                !block.Solved && block.RequiredItem >= 0 && Faces(world, block, stepZone))
                _clearing = block;
            if (_clearing is { Solved: false } gate && ClearGate(world, gate) is { } clear)
                return clear;
            _clearing = null;

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
                    // The tile it is used on may only appear on the first visit (Van Loon walks
                    // in when you do): trust an unvisited room, check a visited one.
                    // (In the goal's rooms, also whoever is in the room but not in place yet -
                    // Van Loon walks over when you bump the barrier. Not elsewhere: a ledge
                    // room's other rocks are the same tile as the one the ladder replaced.)
                    if (_visitedZones.Contains(id) && zone.GetTile(a[0], a[1], a[2]) != a[3] &&
                        !(sector?.Type == ZoneType.Goal && HasTile(zone, a[3]))) continue;
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

        // Across water: a vehicle on our side that lands on theirs - out from the mainland, or
        // back from an island. On land: the nearest blockade still standing.
        var ourSide = Component(world, here);
        var gate = acrossWater
            ? FirstVehicleToward(world, ourSide, island)
            : world.Sectors.Values.Distinct()
                .Where(s => s.Type is ZoneType.BlockadeNorth or ZoneType.BlockadeSouth or ZoneType.BlockadeEast or ZoneType.BlockadeWest &&
                            !s.Solved && s.RequiredItem >= 0)
                .OrderBy(s => GridDistance(world, s.Zone)) // the one we are standing in first
                .FirstOrDefault();
        if (gate == null)
        {
            Console.WriteLine($"[BOT] No way across to {target}: here {here} side [{string.Join(",", ourSide)}], there [{string.Join(",", island)}]; " +
                string.Join(" ", world.Sectors.Values.Distinct().Where(s => VehicleOf(s.Zone) != null).Select(s => $"{s.Zone}:{s.Type}->{VehicleOf(s.Zone)!.Argument}")));
            return null;
        }

        // The fare is paid on the way out; the ride home is free.
        if (!gate.Solved && gate.RequiredItem >= 0 && gate.Type != ZoneType.TravelEnd &&
            ClearGate(world, gate) is { } cleared)
            return cleared;

        if (gate.Type is ZoneType.TravelStart or ZoneType.TravelEnd && VehicleOf(gate.Zone) is { } vehicle)
        {
            if (_state.CurrentZoneId != gate.Zone)
                return NavigateToward(world, gate.Zone, gate.Zone);
            if (_state.CurrentZone!.GetTile(vehicle.X, vehicle.Y, 1) == 0xFFFF)
                return new BotObjective { Type = ObjectiveType.WalkToPosition, Description = "Take the vehicle", TargetX = vehicle.X, TargetY = vehicle.Y };
            // Something sits on it (the plane itself): a bump script is what flies you - the
            // pilot's, or the airship's gangway once the Baron has been paid.
            if (UsefulBump() is { } ask)
                return ask;
        }
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
                    var pickup = new BotObjective { Type = ObjectiveType.PickupItem, Description = $"Fetch item {item}", TargetX = spot.X, TargetY = spot.Y };
                    if (_state.CurrentZone!.GetTile(spot.X, spot.Y, 1) == item)
                    {
                        // Walled in by push-blocks (a column of crates in front of it): shift them.
                        if (!ReachableWithoutPushing(_state.CurrentZone).Contains((spot.X, spot.Y)) && Unjam(pickup) is { } shift)
                            return shift;
                        if (!_unreachablePositions.Contains((id, spot.X, spot.Y)))
                            return pickup;
                    }
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
            if (zone.GetTile(bx, by, 1) != 0xFFFF) continue; // a block only moves into an empty square
            var from = zone.GetTile(fx, fy, 1);
            if (from != 0xFFFF && (from >= _gameData.Tiles.Count || _gameData.Tiles[from].IsSolid)) continue;
            return new BotObjective
            {
                Type = ObjectiveType.PushObject, Description = "Push the crate off the item",
                TargetX = x, TargetY = y, Direction = dir,
            };
        }

        // Wedged: pull it instead - stand on an open side and step away from it.
        bool Open(int px, int py) => px >= 0 && py >= 0 && px < zone.Width && py < zone.Height &&
            zone.GetTile(px, py, 1) is var t && (t == 0xFFFF || t < _gameData.Tiles.Count && !_gameData.Tiles[t].IsSolid);
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

    /// <summary>
    /// Walk into (x, y) from a side the hero can reach: what Bump scripts answer to. (Talking
    /// swings the weapon at a tile, which no script listens for.) Done as a push that the
    /// tile refuses - the engine bumps it instead.
    /// </summary>
    private BotObjective? BumpInto(int x, int y, string why)
    {
        var reach = ReachableWithoutPushing(_state.CurrentZone!);
        foreach (var (dir, dx, dy) in new[] { (Direction.Up, 0, -1), (Direction.Down, 0, 1), (Direction.Left, -1, 0), (Direction.Right, 1, 0) })
            if (reach.Contains((x - dx, y - dy)))
                return new BotObjective { Type = ObjectiveType.PushObject, Description = why, TargetX = x, TargetY = y, Direction = dir };
        return null;
    }

    /// <summary>
    /// Chain done but not won: the artefact is handed over, and what remains is the goal
    /// room's own ending - open the cage, talk to Pacal - which is the script with WinGame.
    /// </summary>
    private BotObjective? FinishGame(WorldMap world)
    {
        var goal = world.Mission?.PuzzleChain.LastOrDefault()?.ZoneId;
        if (goal is not { } goalZone) return null;
        var rooms = ZoneAndRooms(goalZone).ToList();
        if (rooms.Contains(_state.CurrentZoneId) && UsefulBump() is { } bump) return bump;
        var ending = rooms.FirstOrDefault(id =>
            _gameData.Zones[id].Actions.Any(a => a.Instructions.Any(n => n.Opcode == InstructionOpcode.WinGame)), -1);
        if (ending < 0 || ending == _state.CurrentZoneId) return null;
        return NavigateToward(world, ending, goalZone);
    }

    private static bool HasTile(Zone zone, int tile)
    {
        for (int y = 0; y < zone.Height; y++)
            for (int x = 0; x < zone.Width; x++)
                if (zone.GetTile(x, y, 1) == tile) return true;
        return false;
    }

    /// <summary>
    /// A bump script here that does something - moves tiles, sets a counter, wins - whose
    /// tile is in place and whose state conditions hold, and that the hero can reach.
    /// Winning ones first. Scripts that only talk are left alone.
    /// </summary>
    private BotObjective? UsefulBump()
    {
        var zone = _state.CurrentZone!;
        var talk = new[] { InstructionOpcode.SpeakHero, InstructionOpcode.SpeakNpc, InstructionOpcode.SpeakNpc2,
            InstructionOpcode.SetTileNeedsDisplay, InstructionOpcode.SetRectNeedsDisplay, InstructionOpcode.Wait,
            InstructionOpcode.Redraw, InstructionOpcode.PlaySound };
        var found = new List<(int x, int y, bool wins)>();
        for (int i = 0; i < zone.Actions.Count; i++)
        {
            var action = zone.Actions[i];
            if (_state.DisabledActions.Contains((_state.CurrentZoneId, i))) continue;
            var b = action.Conditions.FirstOrDefault(c => c.Opcode == ConditionOpcode.Bump && c.Arguments.Count >= 3);
            if (b == null || zone.GetTile(b.Arguments[0], b.Arguments[1], 1) != b.Arguments[2]) continue;
            if (!StateAllows(_state.CurrentZoneId, action)) continue;
            if (action.Instructions.All(n => talk.Contains(n.Opcode) || !Enum.IsDefined(n.Opcode))) continue;
            found.Add((b.Arguments[0], b.Arguments[1], action.Instructions.Any(n => n.Opcode == InstructionOpcode.WinGame)));
        }
        foreach (var (x, y, _) in found.OrderByDescending(f => f.wins))
            if (BumpInto(x, y, "Bump it") is { } bump)
                return bump;
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

    private Sector? _clearing;

    /// <summary>Use the gate's item on it - fetching the item first if need be.</summary>
    private BotObjective? ClearGate(WorldMap world, Sector gate)
    {
        if (!_state.HasItem(gate.RequiredItem))
            return FetchItem(world, gate.RequiredItem);
        if (FindUseSpot(world, gate.Zone, gate.RequiredItem) is not { } use) return null;
        return _state.CurrentZoneId == use.zone
            ? UseAt(gate.RequiredItem, use.x, use.y, "Open the way")
            : NavigateToward(world, use.zone, gate.Zone);
    }

    /// <summary>Whether <paramref name="target"/> lies on the far side of blockade <paramref name="block"/>.</summary>
    private static bool Faces(WorldMap world, Sector block, int target)
    {
        var (bx, by) = FindCell(world, block.Zone);
        var (tx, ty) = FindCell(world, target);
        return block.Type switch
        {
            ZoneType.BlockadeEast => tx > bx,
            ZoneType.BlockadeWest => tx < bx,
            ZoneType.BlockadeNorth => ty < by,
            ZoneType.BlockadeSouth => ty > by,
            _ => false,
        };
    }

    /// <summary>
    /// The vehicle to take first to get from one landmass to another - maybe via a third:
    /// islands are reached from the mainland, so island to island goes by way of it.
    /// </summary>
    private Sector? FirstVehicleToward(WorldMap world, HashSet<int> from, HashSet<int> to)
    {
        var vehicles = world.Sectors.Values.Distinct()
            .Where(s => s.Type is ZoneType.TravelStart or ZoneType.TravelEnd && VehicleOf(s.Zone) != null).ToList();
        var seen = new List<HashSet<int>> { from };
        var pending = new Queue<(HashSet<int> Side, Sector? First)>();
        pending.Enqueue((from, null));
        while (pending.Count > 0)
        {
            var (side, first) = pending.Dequeue();
            foreach (var s in vehicles.Where(v => side.Contains(v.Zone)))
            {
                int landing = VehicleOf(s.Zone)!.Argument;
                if (to.Contains(landing)) return first ?? s;
                if (seen.Any(c => c.Contains(landing))) continue;
                var next = Component(world, landing);
                seen.Add(next);
                pending.Enqueue((next, first ?? s));
            }
        }
        return null;
    }

    private ZoneObject? VehicleOf(int zoneId) =>
        _gameData.Zones[zoneId].Objects.FirstOrDefault(o => o.Type is ZoneObjectType.VehicleToSecondary or ZoneObjectType.VehicleToPrimary);

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

        // Came into this room for a key: take it before going on.
        if (_wantedKey >= 0)
        {
            if (_state.HasItem(_wantedKey)) _wantedKey = -1;
            else if (FetchHere(_wantedKey) is { } key) return key;
        }

        // The target is behind one of our doors: take the door that leads towards it.
        if (inside.Contains(targetZone))
        {
            foreach (var door in current.Objects.Where(o => o.Type == ZoneObjectType.DoorEntrance))
                if (ZoneAndRooms(door.Argument).Contains(targetZone))
                    return UnlockDoor(world, door, outdoor) ?? new BotObjective
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

    /// <summary>
    /// A door whose script takes a key ("Locked again!"): use the key, or go and get it -
    /// from a crate hotspot in this room, or in a room behind another door (its own key
    /// first, and so on). Null when the door is open or no key is known.
    /// </summary>
    private BotObjective? UnlockDoor(WorldMap world, ZoneObject door, int outdoor)
    {
        var zone = _state.CurrentZone!;
        var tile = zone.GetTile(door.X, door.Y, 1);
        if (tile == 0xFFFF) return OpenGateToward(world, door.X, door.Y, outdoor);
        var key = zone.Actions.SelectMany(a => a.Conditions)
            .Where(c => c.Opcode == ConditionOpcode.PlacedItemIs && c.Arguments.Count >= 5 &&
                        c.Arguments[0] == door.X && c.Arguments[1] == door.Y && c.Arguments[3] == tile && c.Arguments[4] >= 0)
            .Select(c => (int)c.Arguments[4]).FirstOrDefault(-1);
        if (key < 0) return OpenGateToward(world, door.X, door.Y, outdoor);
        return UnlockAt(world, door.X, door.Y, key, ZoneAndRooms(door.Argument).ToHashSet(), outdoor);
    }

    /// <summary>
    /// (x, y) is walled off inside this room: find a gate - a tile a script removes when a
    /// key is used on it (room 65's "Van Loon's got to be in here somewhere...") - whose
    /// removal opens the way, and unlock that.
    /// </summary>
    private BotObjective? OpenGateToward(WorldMap world, int x, int y, int outdoor)
    {
        var zone = _state.CurrentZone!;
        bool Near(HashSet<(int x, int y)> r)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (r.Contains((x + dx, y + dy))) return true;
            return false;
        }
        var reach = ReachableWithoutPushing(zone);
        if (Near(reach)) return null;

        foreach (var action in zone.Actions)
            foreach (var c in action.Conditions.Where(c => c.Opcode == ConditionOpcode.PlacedItemIs && c.Arguments.Count >= 5 && c.Arguments[4] >= 0))
            {
                int gx = c.Arguments[0], gy = c.Arguments[1];
                if (zone.GetTile(gx, gy, 1) != c.Arguments[3]) continue;
                if (!action.Instructions.Any(i => i.Opcode == InstructionOpcode.RemoveTile && i.Arguments.Count >= 2 &&
                                                  i.Arguments[0] == gx && i.Arguments[1] == gy)) continue;
                var grid = new ushort[zone.Width, zone.Height];
                for (int yy = 0; yy < zone.Height; yy++)
                    for (int xx = 0; xx < zone.Width; xx++)
                        grid[xx, yy] = zone.GetTile(xx, yy, 1);
                grid[gx, gy] = 0xFFFF;
                var opened = BlockPuzzle.Reach(grid, t => t < _gameData.Tiles.Count && _gameData.Tiles[t].IsDraggable,
                    t => t >= _gameData.Tiles.Count || _gameData.Tiles[t].IsSolid, (_state.PlayerX, _state.PlayerY));
                if (Near(opened))
                    return UnlockAt(world, gx, gy, c.Arguments[4], new HashSet<int>(), outdoor);
            }
        return null;
    }

    /// <summary>Use <paramref name="key"/> on (x, y), or go and get it first - here, or in another room.</summary>
    private BotObjective? UnlockAt(WorldMap world, int x, int y, int key, HashSet<int> beyond, int outdoor)
    {
        if (_state.HasItem(key)) return UseAt(key, x, y, "Unlock the way");
        if (FetchHere(key) is { } fetch) return fetch;
        _wantedKey = key; // remembered for when we reach the room it is in

        // In another room: go there (through its own locked door if need be).
        if (_unlockDepth > 4) return null; // keys that lead in a circle
        foreach (var id in ZoneAndRooms(_state.CurrentZoneId).Skip(1))
            if (_gameData.Zones[id].Objects.Any(o => o.Argument == key && o.Enabled) && !beyond.Contains(id))
            {
                _unlockDepth++;
                try { return NavigateToward(world, id, outdoor); }
                finally { _unlockDepth--; }
            }
        return null;
    }

    private int _unlockDepth;
    private int _wantedKey = -1;

    /// <summary>Pick up <paramref name="item"/> if it lies in this zone - under a crate, or behind blocks.</summary>
    private BotObjective? FetchHere(int item)
    {
        var zone = _state.CurrentZone!;
        foreach (var spot in zone.Objects.Where(o => o.Argument == item && o.Enabled))
        {
            var pickup = new BotObjective { Type = ObjectiveType.PickupItem, Description = $"Fetch key {item}", TargetX = spot.X, TargetY = spot.Y };
            if (zone.GetTile(spot.X, spot.Y, 1) == item)
                return ReachableWithoutPushing(zone).Contains((spot.X, spot.Y)) ? pickup : Unjam(pickup) ?? pickup;
        }
        foreach (var spot in zone.Objects.Where(o => o.Argument == item && o.Enabled))
            if (PushOff(spot.X, spot.Y) is { } uncover)
                return uncover;
        return null;
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
