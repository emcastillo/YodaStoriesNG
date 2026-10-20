using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.UI;

namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Executes zone action scripts (IACT).
/// </summary>
public class ActionExecutor
{
    private readonly GameData _gameData;
    private readonly GameState _state;

    // Action execution context
    // The zone's own random value: a zone rolls its layout once and checks it for ever after,
    // so one value shared by every zone let other zones' dice scramble it.
    private int _lastRandomValue
    {
        get => _state.ZoneRandom.GetValueOrDefault(_state.CurrentZoneId);
        set => _state.ZoneRandom[_state.CurrentZoneId] = value;
    }
    private Zone? _currentZone;

    /// <summary>
    /// A die roll that depends only on the world seed, the zone and how many times that zone
    /// has rolled - not on frame timing or the order zones were visited - so a seed replays.
    /// </summary>
    private int NextZoneRoll(int sides)
    {
        var zone = _state.CurrentZoneId;
        var n = _state.ZoneRolls.GetValueOrDefault(zone);
        _state.ZoneRolls[zone] = n + 1;
        return new Random(unchecked(_state.WorldSeed * 486187739 + zone * 7919 + n)).Next(sides);
    }
    private ActionTrigger _currentTrigger;
    private int _currentActionIndex = -1;
    private bool _enteredByPlane;
    private bool _zoneWasUninitialized;
    private int _pendingZoneId = -1;
    private int _pendingActionIndex = -1;
    private ActionTrigger _pendingTrigger;
    private bool _pendingEnteredByPlane;
    private bool _pendingZoneWasUninitialized;

    /// <summary>
    /// When true, dialogue instructions are suppressed.
    /// </summary>
    public bool SuppressDialogue { get; set; }

    // Current interaction context
    public int? PlacedItemId { get; set; }
    public int PlacedX { get; set; } = -1;   // the tile the item was used on
    public int PlacedY { get; set; } = -1;
    public int? DroppedItemId { get; set; }
    public int? InteractingNpcId { get; set; }
    /// <summary>A script put the hero on a vehicle, which should take them (WebFun "Stand").</summary>
    public ZoneObject? PlacedOnVehicle { get; set; }
    public int BumpX { get; set; }
    public int BumpY { get; set; }

    // World data for puzzle conditions (RequiredItemIs, EndingIs, FindItemIs, etc.)
    public WorldMap? CurrentWorld { get; set; }
    public Mission? CurrentMission { get; set; }

    /// <summary>
    /// The sector holding the current zone or room, when the world came from
    /// OriginalWorldGenerator. Scripts ask it what this place's puzzle needs and gives;
    /// worlds without sectors keep the older mission-step answers below.
    /// </summary>
    private Sector? CurrentSector => CurrentWorld?.Sectors.GetValueOrDefault(_state.CurrentZoneId);

    // Wait instruction support - remaining instructions to resume next tick
    public List<Instruction>? PendingInstructions { get; set; }
    public int PendingInstructionIndex { get; set; }

    public bool IsBusy => PendingInstructions != null;
    public (int ZoneId, int X, int Y)? RequestedZoneChange { get; private set; }

    /// <summary>Cancel a suspended script when leaving its zone.</summary>
    public void ResetForZone()
    {
        PendingInstructions = null;
        PendingInstructionIndex = 0;
        _pendingZoneId = -1;
        _pendingActionIndex = -1;
        _currentActionIndex = -1;
        RequestedZoneChange = null;
    }

    // Event for displaying dialogue
    public event Action<string, string>? OnDialogue;
    public event Action<string>? OnMessage;
    public event Action<int>? OnPlaySound;

    public ActionExecutor(GameData gameData, GameState state)
    {
        _gameData = gameData;
        _state = state;
    }

    /// <summary>
    /// Executes all applicable actions in the current zone.
    /// </summary>
    public void ExecuteZoneActions(ActionTrigger trigger, bool enteredByPlane = false)
    {
        if (RequestedZoneChange.HasValue)
            return;
        _currentZone = _state.CurrentZone;
        _currentTrigger = trigger;
        if (_currentZone == null)
            return;

        if (IsBusy && _pendingZoneId != _currentZone.Id)
            ResetForZone();

        _enteredByPlane = trigger == ActionTrigger.ZoneEnter && enteredByPlane;
        _zoneWasUninitialized = !_state.Variables.ContainsKey(_currentZone.Id + 1000);
        try
        {
            int nextActionIndex = 0;
            // Resume pending instructions from a previous Wait
            if (PendingInstructions != null)
            {
                var remaining = PendingInstructions;
                var startIdx = PendingInstructionIndex;
                _currentActionIndex = _pendingActionIndex;
                _currentTrigger = trigger = _pendingTrigger;
                _enteredByPlane = _pendingEnteredByPlane;
                _zoneWasUninitialized = _pendingZoneWasUninitialized;
                PendingInstructions = null;
                PendingInstructionIndex = 0;
                ExecuteInstructions(remaining, startIdx);
                if (IsBusy || RequestedZoneChange.HasValue)
                    return;
                nextActionIndex = _currentActionIndex + 1;
            }

            for (int i = nextActionIndex; i < _currentZone.Actions.Count; i++)
            {
                if (_state.DisabledActions.Contains((_currentZone.Id, i)))
                    continue;
                var action = _currentZone.Actions[i];
                if (EvaluateConditions(action.Conditions, trigger))
                {
                    _currentActionIndex = i;
                    ExecuteInstructions(action.Instructions);
                    // A paused action owns the interpreter until it finishes. Continuing
                    // here let another action overwrite its animation and continuation.
                    if (IsBusy || RequestedZoneChange.HasValue)
                        return;
                }
            }
        }
        finally
        {
            _enteredByPlane = false;
        }
    }

    /// <summary>
    /// Returns true if the current trigger is player-initiated (NpcTalk, UseItem).
    /// Dialogue should only show for these triggers, not for ZoneEnter, Walk, Bump, etc.
    /// </summary>
    private bool IsPlayerInitiatedTrigger()
    {
        return _currentTrigger == ActionTrigger.NpcTalk ||
               _currentTrigger == ActionTrigger.UseItem;
    }

    private bool EvaluateConditions(List<Condition> conditions, ActionTrigger trigger)
    {
        // Arriving is its own pass, as in WebFun's JustEntered/Initialize/ByPlane modes: only
        // actions that ask about arriving run then; the rest wait for the hero's first step.
        // Otherwise "standing on the landing pad -> clear the shrubs" runs before "landed ->
        // plant the shrubs", both disable themselves, and Luke is boxed in on Dagobah for good.
        if (trigger == ActionTrigger.ZoneEnter && !conditions.Any(c =>
                c.Opcode is ConditionOpcode.ZoneEntered or ConditionOpcode.ZoneNotInitialized or ConditionOpcode.EnterByPlane))
            return false;

        foreach (var condition in conditions)
        {
            if (!EvaluateCondition(condition, trigger))
                return false;
        }
        return true;
    }

    private bool EvaluateCondition(Condition condition, ActionTrigger trigger)
    {
        var result = EvaluateConditionInternal(condition, trigger);

        // Debug: Log NpcIs condition evaluations (most relevant for R2D2 interaction)
        if (condition.Opcode == ConditionOpcode.NpcIs)
        {
            var args = condition.Arguments;
            var expectedNpc = args.Count > 0 ? args[0] : -1;
            Console.WriteLine($"  Condition NpcIs: expected={expectedNpc}, actual={InteractingNpcId}, result={result}");
        }

        return result;
    }

    private bool EvaluateConditionInternal(Condition condition, ActionTrigger trigger)
    {
        var args = condition.Arguments;

        switch (condition.Opcode)
        {
            case ConditionOpcode.ZoneNotInitialized:
                // True on first entry to zone
                return trigger == ActionTrigger.ZoneEnter && _zoneWasUninitialized;

            case ConditionOpcode.ZoneEntered:
                return trigger == ActionTrigger.ZoneEnter;

            // The tile walked INTO is (x, y) and holds tile arg 2 on some layer (WebFun
            // conditions/bump.ts). This used to compare the hero's own position, which can
            // never be a tile that blocks him - so no bump script ever ran.
            case ConditionOpcode.Bump:
                if (trigger != ActionTrigger.Bump || args.Count < 3 || _currentZone == null)
                    return false;
                return BumpX == args[0] && BumpY == args[1] &&
                       (_currentZone.GetTile(args[0], args[1], 0) == args[2] ||
                        _currentZone.GetTile(args[0], args[1], 1) == args[2] ||
                        _currentZone.GetTile(args[0], args[1], 2) == args[2]);

            // Hero at (x, y) and the floor tile there is arg 2 (WebFun conditions/standing-on.ts).
            case ConditionOpcode.Standing:
                if (args.Count < 3 || _currentZone == null)
                    return false;
                return _state.PlayerX == args[0] && _state.PlayerY == args[1] &&
                       _currentZone.GetTile(args[0], args[1], 0) == args[2];

            case ConditionOpcode.CounterIs:
                if (args.Count < 1)
                    return false;
                return _state.GetCounter(_state.CurrentZoneId) == args[0];

            case ConditionOpcode.CounterIsNot:
                if (args.Count < 1)
                    return false;
                return _state.GetCounter(_state.CurrentZoneId) != args[0];

            case ConditionOpcode.CounterIsGreaterThan:
                if (args.Count < 1)
                    return false;
                return _state.GetCounter(_state.CurrentZoneId) > args[0];

            case ConditionOpcode.CounterIsLessThan:
                if (args.Count < 1)
                    return false;
                return _state.GetCounter(_state.CurrentZoneId) < args[0];

            case ConditionOpcode.RandomIs:
                if (args.Count < 1)
                    return false;
                return _lastRandomValue == args[0];

            case ConditionOpcode.RandomIsNot:
                if (args.Count < 1)
                    return false;
                return _lastRandomValue != args[0];

            case ConditionOpcode.RandomIsGreaterThan:
                if (args.Count < 1)
                    return false;
                return _lastRandomValue > args[0];

            case ConditionOpcode.RandomIsLessThan:
                if (args.Count < 1)
                    return false;
                return _lastRandomValue < args[0];

            case ConditionOpcode.HasItem:
                if (args.Count < 1)
                    return false;
                // -1 asks about the item this sector's puzzle provides
                if (args[0] == -1 && CurrentSector is { FindItem: >= 0 } findSector)
                    return _state.HasItem(findSector.FindItem);
                // Dagobah has no generated puzzle sector. Its departure gate asks
                // whether Luke has collected the item handed out with Yoda's briefing.
                if (args[0] == -1 && CurrentWorld is { StartingItemId: { } startingItem } world &&
                    world.DagobahZones.Contains(_state.CurrentZoneId))
                    return _state.HasItem(startingItem);
                return _state.HasItem(args[0]);

            // Note the argument order: the tile ID comes FIRST and the position after it,
            // which is the reverse of every other tile opcode. Matches WebFun's
            // conditions/tile-at-is.ts, whose own doc comment states it the wrong way round.
            case ConditionOpcode.TileAtIs:
                if (args.Count < 4 || _currentZone == null)
                    return false;
                return _currentZone.GetTile(args[1], args[2], args[3]) == args[0];

            // Takes no arguments - it asks whether THIS zone is solved.
            case ConditionOpcode.ZoneIsSolved:
                return _state.IsZoneSolved(_state.CurrentZoneId);

            case ConditionOpcode.HealthIsLessThan:
                if (args.Count < 1)
                    return false;
                return _state.Health < args[0];

            case ConditionOpcode.HealthIsGreaterThan:
                if (args.Count < 1)
                    return false;
                return _state.Health > args[0];

            case ConditionOpcode.GamesWonIs:
                if (args.Count < 1)
                    return false;
                return _state.GamesWon == args[0];

            // (x, y, layer, target tile, item): the item was used on (x, y) and that cell
            // holds the target tile. Item -1 means this sector's required item. WebFun
            // conditions/placed-item-is.ts, which also rejects negative coordinates.
            case ConditionOpcode.PlacedItemIs:
            {
                if (args.Count < 5 || !PlacedItemId.HasValue || _currentZone == null)
                    return false;
                if (args[0] < 0 || args[1] < 0 || args[2] < 0 || args[3] < 0)
                    return false;
                int item = args[4] >= 0 ? args[4] : CurrentSector?.RequiredItem ?? -1;
                return PlacedItemId == item && PlacedX == args[0] && PlacedY == args[1] &&
                       _currentZone.GetTile(args[0], args[1], args[2]) == args[3];
            }

            case ConditionOpcode.NoItemPlaced:
                return !PlacedItemId.HasValue;

            // 0x12 is WebFun's HasGoalItem: the hero holds the item the whole world is about.
            // Worlds from the older generator keep the looser "an item was placed" reading.
            case ConditionOpcode.HasGoalItem:
                if (CurrentMission?.GoalPuzzle is { } goal)
                    return _state.HasItem(goal.Item1);
                return PlacedItemId.HasValue;

            case ConditionOpcode.DroppedItemIs:
                if (args.Count < 1)
                    return false;
                return DroppedItemId == args[0];

            case ConditionOpcode.NpcIs:
                if (args.Count < 1)
                    return false;
                return InteractingNpcId == args[0];

            case ConditionOpcode.HasNpc:
                return InteractingNpcId.HasValue;

            case ConditionOpcode.MonsterIsDead:
                // Check if specific monster is dead (by index in zone NPCs)
                if (args.Count < 1)
                    return false;
                var npcIndex = args[0];
                if (npcIndex >= 0 && npcIndex < _state.ZoneNPCs.Count)
                    return _state.ZoneNPCs[npcIndex].Health <= 0;
                return true; // If NPC doesn't exist, consider it "dead"

            case ConditionOpcode.HasNoActiveMonsters:
                // Check if all hostile NPCs in zone are dead
                return _state.ZoneNPCs.All(n => !n.IsHostile || n.Health <= 0);

            case ConditionOpcode.RequiredItemIs:
                // Check if current sector's required item matches arg
                if (args.Count < 1)
                    return false;
                if (CurrentSector is { } requiredSector)
                    return args[0] >= 0 && (args[0] == requiredSector.RequiredItem || args[0] == requiredSector.AdditionalRequiredItem);
                if (CurrentMission?.CurrentPuzzleStep != null)
                    return CurrentMission.CurrentPuzzleStep.RequiredItemId == args[0];
                return _state.HasItem(args[0]);  // Fallback

            case ConditionOpcode.FindItemIs:
                // Check if zone's find/provided item matches arg
                if (args.Count < 1)
                    return false;
                if (CurrentSector is { } findItemSector)
                    return args[0] >= 0 && args[0] == findItemSector.FindItem;
                if (CurrentMission?.CurrentPuzzleStep != null)
                    return CurrentMission.CurrentPuzzleStep.RewardItemId == args[0];
                return _state.HasItem(args[0]);  // Fallback

            // Only run landing cutscenes when the engine explicitly loaded this zone by plane.
            case ConditionOpcode.EnterByPlane:
                return trigger == ActionTrigger.ZoneEnter && (_enteredByPlane || _state.ArrivingByVehicle);

            case ConditionOpcode.HeroIsAt:
                // Check if hero is at position
                if (args.Count < 2)
                    return false;
                return _state.PlayerX == args[0] && _state.PlayerY == args[1];

            case ConditionOpcode.PlacedItemIsNot:
                // Check if placed item does NOT match
                if (args.Count < 1)
                    return false;
                return PlacedItemId != args[0];

            case ConditionOpcode.EndingIs:
                // Ending identifies the mission itself. The final sector's required
                // item is a prerequisite used to obtain the mission item, not its identity.
                if (args.Count < 1)
                    return false;
                if (CurrentMission?.GoalPuzzle is { } ending)
                    return ending.Item1 == args[0];
                if (CurrentMission != null && CurrentMission.PuzzleChain.Count > 0)
                {
                    var lastStep = CurrentMission.PuzzleChain[^1];
                    return lastStep.RequiredItemId == args[0];
                }
                return _state.HasItem(args[0]);  // Fallback

            // Note: SectorCounterIs (0x19) shares value with NpcIs - handled above
            // Note: SectorCounterIsLessThan (0x1A) shares value with HasNpc - handled above
            // Note: HasGoalItem (0x12) shares value with ItemIsPlaced - handled above

            case ConditionOpcode.SectorCounterIsGreaterThan:
                if (args.Count < 2)
                    return false;
                return _state.GetVariable(args[0] + 3000) > args[1];

            case ConditionOpcode.SectorCounterIsNot:
                if (args.Count < 2)
                    return false;
                return _state.GetVariable(args[0] + 3000) != args[1];

            case ConditionOpcode.DropsQuestItemAt:
                // Check if quest item dropped at position
                if (args.Count < 2)
                    return false;
                return BumpX == args[0] && BumpY == args[1] && DroppedItemId.HasValue;

            case ConditionOpcode.HasAnyRequiredItem:
                // Check if inventory contains any of the current sector's required items
                if (CurrentSector is { } anySector)
                    return _state.HasItem(anySector.RequiredItem) || _state.HasItem(anySector.AdditionalRequiredItem);
                if (CurrentWorld?.RequiredItems != null && CurrentWorld.RequiredItems.Count > 0)
                {
                    return CurrentWorld.RequiredItems.Any(itemId => _state.HasItem(itemId));
                }
                return _state.Inventory.Count > 0;  // Fallback

            case ConditionOpcode.GamesWonIsGreaterThan:
                if (args.Count < 1)
                    return false;
                return _state.GamesWon > args[0];

            // Shares TileAtIs's layout: value first, then the (x, y, layer) it lives at.
            case ConditionOpcode.IsVariable:
                if (args.Count < 4 || _currentZone == null)
                    return false;
                return _currentZone.GetTile(args[1], args[2], args[3]) == args[0];

            default:
                // Unknown condition - assume true to allow script to continue
                return true;
        }
    }

    private void ExecuteInstructions(List<Instruction> instructions, int startIndex = 0)
    {
        if (instructions.Count > 0 && startIndex == 0)
        {
            Console.WriteLine($"  Executing {instructions.Count} instructions");
        }

        for (int i = startIndex; i < instructions.Count; i++)
        {
            if (instructions[i].Opcode == InstructionOpcode.Wait)
            {
                // Save remaining instructions to resume next tick
                PendingInstructions = instructions;
                PendingInstructionIndex = i + 1;
                _pendingZoneId = _currentZone!.Id;
                _pendingActionIndex = _currentActionIndex;
                _pendingTrigger = _currentTrigger;
                _pendingEnteredByPlane = _enteredByPlane;
                _pendingZoneWasUninitialized = _zoneWasUninitialized;
                return;
            }
            ExecuteInstruction(instructions[i]);
            if (RequestedZoneChange.HasValue)
                return;
        }
    }

    private void ExecuteInstruction(Instruction instruction)
    {
        var args = instruction.Arguments;

        switch (instruction.Opcode)
        {
            case InstructionOpcode.PlaceTile:
                if (args.Count >= 4 && _currentZone != null)
                    _currentZone.SetTile(args[0], args[1], args[2], (ushort)args[3]);
                break;

            case InstructionOpcode.RemoveTile:
                if (args.Count >= 3 && _currentZone != null)
                    _currentZone.SetTile(args[0], args[1], args[2], 0xFFFF);
                break;

            case InstructionOpcode.RollDice:
                if (args.Count >= 1)
                    _lastRandomValue = 1 + NextZoneRoll(Math.Max(1, (int)args[0])); // 1..max, as WebFun roll-dice.ts
                break;

            case InstructionOpcode.SetCounter:
                // One counter per zone, set to arg 0 (WebFun instructions/set-counter.ts)
                if (args.Count >= 1)
                    _state.SetCounter(_state.CurrentZoneId, args[0]);
                break;

            case InstructionOpcode.AddToCounter:
                if (args.Count >= 1)
                    _state.AddToCounter(_state.CurrentZoneId, args[0]);
                break;

            case InstructionOpcode.SetVariable:
                // Variables live in zone tile slots, exactly where IsVariable reads.
                if (args.Count >= 4 && _currentZone != null)
                    _currentZone.SetTile(args[0], args[1], args[2], (ushort)args[3]);
                break;

            case InstructionOpcode.AddItem:
                if (args.Count >= 1)
                    _state.AddItem(args[0]);
                break;

            case InstructionOpcode.RemoveItem:
                // A negative item means this sector's required item
                if (args.Count >= 1)
                    _state.RemoveItem(args[0] < 0 && CurrentSector is { } removeSector ? removeSector.RequiredItem : args[0]);
                break;

            case InstructionOpcode.MarkAsSolved:
                _state.MarkZoneSolved(_state.CurrentZoneId);
                break;

            case InstructionOpcode.WinGame:
                _state.IsGameWon = true;
                _state.GamesWon++;
                break;

            case InstructionOpcode.LoseGame:
                _state.IsGameOver = true;
                break;

            case InstructionOpcode.ChangeZone:
                if (args.Count >= 3)
                {
                    // The engine must load the destination's tiles, actors and scripts
                    // together. Changing only the ID left the old map and a hidden hero.
                    RequestedZoneChange = (args[0], args[1], args[2]);
                }
                break;

            case InstructionOpcode.MoveHeroTo:
                if (args.Count >= 2)
                {
                    _state.PlayerX = args[0];
                    _state.PlayerY = args[1];
                    // The last placement wins: a flight ends by setting the hero on the plane.
                    PlacedOnVehicle = _state.CurrentZone?.Objects.FirstOrDefault(o => o.Enabled &&
                        o.X == args[0] && o.Y == args[1] &&
                        o.Type is ZoneObjectType.VehicleToSecondary or ZoneObjectType.VehicleToPrimary);
                }
                break;

            case InstructionOpcode.MoveHeroBy:
                if (args.Count >= 2)
                {
                    _state.PlayerX += args[0];
                    _state.PlayerY += args[1];
                }
                break;

            case InstructionOpcode.AddHealth:
                if (args.Count >= 1)
                    _state.Health = Math.Min(_state.Health + args[0], _state.MaxHealth);
                break;

            case InstructionOpcode.SubtractHealth:
                if (args.Count >= 1)
                    _state.Health = Math.Max(_state.Health - args[0], 0);
                break;

            case InstructionOpcode.SetHealth:
                if (args.Count >= 1)
                    _state.Health = Math.Clamp(args[0], 0, _state.MaxHealth);
                break;

            case InstructionOpcode.SpeakHero:
                // Only show dialogue if not suppressed AND trigger is player-initiated
                if (!string.IsNullOrEmpty(instruction.Text) && !SuppressDialogue && IsPlayerInitiatedTrigger())
                {
                    OnDialogue?.Invoke("Luke", instruction.Text);
                }
                break;

            case InstructionOpcode.SpeakNpc:
            case InstructionOpcode.SpeakNpc2:
                // Only show dialogue if not suppressed AND trigger is player-initiated
                if (!string.IsNullOrEmpty(instruction.Text) && !SuppressDialogue && IsPlayerInitiatedTrigger())
                {
                    // Get NPC name from argument if provided
                    string npcName = "NPC";
                    if (args.Count >= 1 && args[0] >= 0 && args[0] < _gameData.Characters.Count)
                    {
                        var character = _gameData.Characters[args[0]];
                        if (!string.IsNullOrEmpty(character.Name))
                            npcName = character.Name;
                    }
                    OnDialogue?.Invoke(npcName, instruction.Text);
                }
                break;

            case InstructionOpcode.PlaySound:
                if (args.Count >= 1)
                {
                    OnPlaySound?.Invoke(args[0]);
                }
                break;

            case InstructionOpcode.Wait:
                // Pause script execution for one tick - remaining instructions resume next frame
                // This is handled by saving remaining instructions in PendingInstructions
                break;  // Caller (ExecuteInstructions) checks for Wait

            case InstructionOpcode.DropItem:
                // (item, x, y) - item first, like WebFun instructions/drop-item.ts. Item -1
                // drops this sector's find item.
                if (args.Count >= 3 && _currentZone != null)
                {
                    int tileId = args[0] == -1 && CurrentSector is { } dropSector ? dropSector.FindItem : args[0];
                    int x = args[1];
                    int y = args[2];
                    if (tileId < 0)
                        break;
                    // Place the item tile on layer 1 (object layer)
                    _currentZone.SetTile(x, y, 1, (ushort)tileId);
                    Console.WriteLine($"Dropped item {tileId} at ({x},{y})");
                }
                break;

            case InstructionOpcode.EnableMonster:
                if (args.Count >= 1)
                {
                    int npcIdx = args[0];
                    if (npcIdx >= 0 && npcIdx < _state.ZoneNPCs.Count)
                        _state.ZoneNPCs[npcIdx].IsEnabled = true;
                }
                break;

            case InstructionOpcode.DisableMonster:
                if (args.Count >= 1)
                {
                    int npcIdx = args[0];
                    if (npcIdx >= 0 && npcIdx < _state.ZoneNPCs.Count)
                        _state.ZoneNPCs[npcIdx].IsEnabled = false;
                }
                break;

            case InstructionOpcode.EnableAllMonsters:
                foreach (var npc in _state.ZoneNPCs)
                    npc.IsEnabled = true;
                break;

            case InstructionOpcode.DisableAllMonsters:
                foreach (var npc in _state.ZoneNPCs)
                    npc.IsEnabled = false;
                break;

            case InstructionOpcode.HideHero:
                _state.HeroVisible = false;
                break;

            case InstructionOpcode.ShowHero:
                _state.HeroVisible = true;
                break;

            case InstructionOpcode.SetZoneType:
                // Change the zone's type
                if (args.Count >= 1 && _currentZone != null)
                    _currentZone.Type = (ZoneType)args[0];
                break;

            // Turns off the action that is running, for good (WebFun action.enabled = false).
            // This used to set a variable nothing read, so every "do this once" script ran
            // every frame its conditions held - scripts undoing each other included.
            case InstructionOpcode.DisableAction:
                if (_currentZone != null && _currentActionIndex >= 0)
                    _state.DisabledActions.Add((_currentZone.Id, _currentActionIndex));
                break;

            case InstructionOpcode.Redraw:
                // Request a redraw - handled by renderer
                break;

            case InstructionOpcode.MoveTile:
                // Move a tile from one position to another
                if (args.Count >= 5 && _currentZone != null)
                {
                    int fromX = args[0];
                    int fromY = args[1];
                    int layer = args[2];
                    int toX = args[3];
                    int toY = args[4];
                    var tile = _currentZone.GetTile(fromX, fromY, layer);
                    _currentZone.SetTile(fromX, fromY, layer, 0xFFFF);
                    _currentZone.SetTile(toX, toY, layer, tile);
                }
                break;

            case InstructionOpcode.DrawTile:
                // Draw a tile at position (same as PlaceTile for us)
                if (args.Count >= 4 && _currentZone != null)
                    _currentZone.SetTile(args[0], args[1], args[2], (ushort)args[3]);
                break;

            case InstructionOpcode.SetTileNeedsDisplay:
                // Redraw tile at position - handled by renderer automatically
                break;

            case InstructionOpcode.SetRectNeedsDisplay:
                // Redraw rectangle area - handled by renderer automatically
                break;

            case InstructionOpcode.StopSound:
                // Stop currently playing sounds
                // TODO: Implement sound stopping
                break;

            case InstructionOpcode.EnableHotspot:
                // Enable a hotspot (zone object) at specified index
                if (args.Count >= 1 && _currentZone != null)
                {
                    int idx = args[0];
                    if (idx >= 0 && idx < _currentZone.Objects.Count)
                    {
                        var obj = _currentZone.Objects[idx];
                        obj.Enabled = true;
                    }
                }
                break;

            case InstructionOpcode.DisableHotspot:
                // Disable a hotspot (zone object) at specified index
                if (args.Count >= 1 && _currentZone != null)
                {
                    int idx = args[0];
                    if (idx >= 0 && idx < _currentZone.Objects.Count)
                    {
                        var obj = _currentZone.Objects[idx];
                        obj.Enabled = false;
                    }
                }
                break;

            // Note: SetSectorCounter (0x22) shares value with SetZoneType - handled above

            case InstructionOpcode.AddToSectorCounter:
                // Add to zone sector counter
                if (args.Count >= 2)
                {
                    int current = _state.GetVariable(args[0] + 3000);
                    _state.SetVariable(args[0] + 3000, current + args[1]);
                }
                break;

            case InstructionOpcode.SetRandom:
                // Set zone random to specific value
                if (args.Count >= 1)
                    _lastRandomValue = args[0];
                break;

            default:
                // Unknown instruction - log and continue
                Console.WriteLine($"Unknown instruction: {instruction.Opcode}");
                break;
        }
    }
}

public enum ActionTrigger
{
    ZoneEnter,
    ZoneLeave,
    Bump,
    Walk,
    UseItem,
    Attack,
    NpcTalk,
}
