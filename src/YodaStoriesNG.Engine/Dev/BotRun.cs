namespace YodaStoriesNG.Engine.Game;

/// <summary>
/// Headless-ish bot check: with YSNG_BOT_SECONDS=N set, skip the title, start a game, let
/// the bot play for N seconds, log how far through the mission it got, and quit.
///
///   YSNG_BOT_SECONDS=300 dotnet run --project src/YodaStoriesNG.Engine -- INDYDESK
/// </summary>
public partial class GameEngine
{
    private static readonly double BotRunSeconds =
        double.TryParse(Environment.GetEnvironmentVariable("YSNG_BOT_SECONDS"), out var s) ? s : 0;

    private double _botRunTime;
    private int _botRunLastStep = -1;

    private void BotRunTick(double deltaTime)
    {
        if (_botRunTime == 0)
        {
            _showingTitleScreen = false;
            _titleScreen?.Hide();
            StartNewGame();
            EnableBot();
        }
        _botRunTime += deltaTime;

        var mission = _worldGenerator?.CurrentWorld?.Mission;
        if (mission != null && mission.CurrentStep != _botRunLastStep)
        {
            _botRunLastStep = mission.CurrentStep;
            Console.WriteLine($"[BOTRUN] {_botRunTime,6:F1}s  step {mission.CurrentStep}/{mission.PuzzleChain.Count}" +
                              (mission.CurrentPuzzleStep is { } step ? $"  {step.Hint}" : "  chain done"));
        }

        if (_state.IsGameWon || _botRunTime >= BotRunSeconds)
        {
            Console.WriteLine($"[BOTRUN] finished at {_botRunTime:F1}s: step {mission?.CurrentStep}/{mission?.PuzzleChain.Count}, won={_state.IsGameWon}");
            DumpZoneForBotRun(_state.CurrentZoneId);
            // YSNG_DUMP_ZONES=70,238 adds those zones too, as they stand now.
            foreach (var extra in (Environment.GetEnvironmentVariable("YSNG_DUMP_ZONES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(extra, out var id) && id >= 0 && id < _gameData!.Zones.Count)
                    DumpZoneForBotRun(id);
            _isRunning = false;
        }
    }

    /// <summary>
    /// Where the bot ended up, as it stands now (scripts change tiles): @ hero, # object,
    /// B draggable, D door/hotspot, e enemy, . open. Then the zone's hotspots.
    /// </summary>
    private void DumpZoneForBotRun(int zoneId)
    {
        var zone = _gameData!.Zones[zoneId];
        bool here = zoneId == _state.CurrentZoneId;
        Console.WriteLine($"[BOTRUN] zone {zoneId} {zone.Width}x{zone.Height} type {zone.Type}, random={_state.ZoneRandom.GetValueOrDefault(zoneId)} counter={_state.GetCounter(zoneId)}");
        for (int y = 0; y < zone.Height; y++)
        {
            var row = new System.Text.StringBuilder("  ");
            for (int x = 0; x < zone.Width; x++)
            {
                var t = zone.GetTile(x, y, 1);
                char c = t != 0xFFFF && t < _gameData!.Tiles.Count
                    ? (_gameData.Tiles[t].IsDraggable ? 'B' : _gameData.Tiles[t].IsObject ? '#' : '.') : '.';
                if (zone.Objects.Any(o => o.X == x && o.Y == y)) c = 'D';
                if (here && _state.ZoneNPCs.Any(n => n.IsEnabled && n.Health > 0 && n.X == x && n.Y == y)) c = 'e';
                if (here && _state.PlayerX == x && _state.PlayerY == y) c = '@';
                row.Append(c);
            }
            Console.WriteLine(row);
        }
        foreach (var o in zone.Objects)
            Console.WriteLine($"[BOTRUN]   hotspot {o.Type} at ({o.X},{o.Y}) arg {o.Argument} enabled={o.Enabled} tile {zone.GetTile(o.X, o.Y, 1)} flags {(zone.GetTile(o.X, o.Y, 1) is var ht && ht < _gameData!.Tiles.Count ? _gameData.Tiles[ht].Flags.ToString() : "-")}");
        for (int i = 0; i < zone.Actions.Count; i++)
        {
            var a = zone.Actions[i];
            var off = _state.DisabledActions.Contains((zoneId, i)) ? " (disabled)" : "";
            Console.WriteLine($"[BOTRUN]   action {i}{off}: if " +
                string.Join(" & ", a.Conditions.Select(c => $"{c.Opcode}({string.Join(",", c.Arguments)})")));
            foreach (var n in a.Instructions)
                Console.WriteLine($"[BOTRUN]       {n.Opcode}({string.Join(",", n.Arguments)}) {n.Text?.ReplaceLineEndings(" ")}");
        }
    }
}
