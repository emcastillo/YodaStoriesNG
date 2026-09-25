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
            _isRunning = false;
        }
    }
}
