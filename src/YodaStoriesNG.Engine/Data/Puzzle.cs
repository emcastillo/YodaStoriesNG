namespace YodaStoriesNG.Engine.Data;

/// <summary>
/// Represents a puzzle definition from the PUZ2 section.
/// </summary>
public class Puzzle
{
    public int Id { get; set; }
    public PuzzleType Type { get; set; }

    // Items involved in this puzzle
    public ushort Item1 { get; set; }
    public ushort? Item2 { get; set; }   // Yoda only, and only some puzzles

    // Text strings for the puzzle
    public List<string> Strings { get; set; } = new();

    // Raw data for unknown fields
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}

/// <summary>The puzzle type as stored in the file (WebFun objects/puzzle-type.ts).</summary>
public enum PuzzleType
{
    None = -1,         // disabled
    Transaction = 0,   // an NPC trades one item for another; placed in Trade zones
    Offer = 1,         // placed in Use zones
    Goal = 2,          // the puzzles just before the end
    Mission = 3,       // the end puzzle: what the whole world is built around
    Unknown = 4,
}
