using YodaStoriesNG.Engine.Data;
using YodaStoriesNG.Engine.Game;
using YodaStoriesNG.Engine.Parsing;

namespace YodaStoriesNG.Engine.Dev;

/// <summary>
/// Runs <see cref="OriginalWorldGenerator"/> over many seeds against a real data file and
/// reports the success rate, why failures failed, and one world's puzzle chain. No window.
///
///   dotnet run --project src/YodaStoriesNG.Engine -- --generate INDYDESK/DESKTOP.DAW 500
/// </summary>
public static class GenerateWorlds
{
    public static int Run(string dataFile, int count)
    {
        var data = new DtaParser().Parse(dataFile);
        var planet = data.GameType == GameType.IndianaJones ? Planet.None : Planet.Desert;
        var failures = new Dictionary<string, int>();
        OriginalWorldGenerator? sample = null;

        var output = Console.Out;
        Console.SetOut(TextWriter.Null); // the map generator is chatty
        for (int seed = 0; seed < count; seed++)
        {
            var size = (WorldSize)(seed % 3 + 1);
            var generator = new OriginalWorldGenerator(data, seed);
            try
            {
                generator.Generate(planet, size);
                sample ??= generator;
            }
            catch (WorldGenerationException ex)
            {
                failures[ex.Message] = failures.GetValueOrDefault(ex.Message) + 1;
            }
        }
        Console.SetOut(output);

        int failed = failures.Values.Sum();
        Console.WriteLine($"{count - failed}/{count} worlds generated");
        foreach (var (message, n) in failures.OrderByDescending(f => f.Value))
            Console.WriteLine($"  {n,5}  {message}");

        if (sample != null)
        {
            Console.WriteLine($"\nFirst world: goal \"{Name(data, sample.GoalPuzzle)}\" ({sample.GoalPuzzle.Id})");
            foreach (var s in sample.Sectors.Where(s => s.Zone >= 0).OrderBy(s => s.PuzzleIndex < 0 ? 99 : s.PuzzleIndex))
                Console.WriteLine($"  {s.Type,-14} zone {s.Zone,3} {data.Zones[s.Zone].Name,-14} puzzle {s.PuzzleIndex,2}  " +
                                  $"needs {s.RequiredItem,5}  gives {s.FindItem,5}  npc {s.Npc,5}");
        }
        return failed == count ? 1 : 0;
    }

    private static string Name(GameData data, Puzzle p) => p.Strings.FirstOrDefault(s => s.Length > 0) ?? $"#{p.Id}";
}
