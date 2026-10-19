namespace YodaStoriesNG.Engine.Game;

/// <summary>A reserved tile step. Logical positions are committed only at arrival.</summary>
internal sealed class MovementStep
{
    public int FromX { get; }
    public int FromY { get; }
    public int ToX { get; }
    public int ToY { get; }
    public double Duration { get; }
    public double Elapsed { get; private set; }
    public (int X, int Y)? BlockDestination { get; init; }
    public bool Pull { get; }
    public Direction Facing { get; }
    public double X => FromX + (ToX - FromX) * Math.Min(1, Elapsed / Duration);
    public double Y => FromY + (ToY - FromY) * Math.Min(1, Elapsed / Duration);

    public MovementStep(int x, int y, int dx, int dy, double cardinalDuration, Direction facing, bool pull = false)
    {
        FromX = x; FromY = y; ToX = x + dx; ToY = y + dy;
        Duration = Math.Max(0.001, cardinalDuration) * Math.Sqrt(dx * dx + dy * dy);
        Facing = facing; Pull = pull;
    }

    public bool Advance(double seconds)
    {
        Elapsed += Math.Max(0, seconds);
        return Elapsed + 1e-10 >= Duration;
    }

    public bool Occupies(int x, int y) => (FromX == x && FromY == y) || (ToX == x && ToY == y) || BlockDestination == (x, y);

    // With one-tile steps, disjoint endpoints can intersect only as crossing diagonals.
    public bool Conflicts(MovementStep other) => Occupies(other.FromX, other.FromY) ||
        Occupies(other.ToX, other.ToY) ||
        (FromX != ToX && FromY != ToY && other.FromX != other.ToX && other.FromY != other.ToY &&
         FromX + ToX == other.FromX + other.ToX && FromY + ToY == other.FromY + other.ToY);
}
