namespace YodaStoriesNG.Engine.Bot;

/// <summary>
/// Push-and-pull block puzzles, solved breadth-first. The hero walks in eight directions
/// (the original has no corner check), pushes a block one square into an empty square, or
/// pulls one by stepping away from it. A state is where the blocks are plus which open
/// region the hero is in, so walking around costs nothing and only block moves count.
/// </summary>
public static class BlockPuzzle
{
    public readonly record struct Move(int X, int Y, int Dx, int Dy, bool Pull);

    private static readonly (int dx, int dy)[] Straight = { (0, -1), (0, 1), (-1, 0), (1, 0) };
    private static readonly (int dx, int dy)[] Around =
        { (0, -1), (0, 1), (-1, 0), (1, 0), (-1, -1), (1, -1), (-1, 1), (1, 1) };

    /// <summary>
    /// The first block move of a shortest plan that lets the hero stand on one of
    /// <paramref name="goals"/>, or null if a goal is reachable already or no plan exists.
    /// <paramref name="grid"/> is the object layer, 0xFFFF for empty.
    /// </summary>
    // ponytail: plain BFS with a state cap - fine for the handful of blocks a room holds,
    // an A* on blocks-in-the-way if a room ever needs deeper plans.
    public static Move? FirstMove(ushort[,] grid, Func<ushort, bool> isBlock, Func<ushort, bool> isSolid,
        (int x, int y) hero, HashSet<(int x, int y)> goals, int maxStates = 20000)
    {
        int w = grid.GetLength(0), h = grid.GetLength(1);
        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;
        goals = goals.Where(g => In(g.x, g.y)).ToHashSet();
        if (goals.Count == 0) return null;

        var blocks = new List<(int x, int y)>();
        var walls = new bool[w, h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var t = grid[x, y];
                if (t == 0xFFFF) continue;
                if (isBlock(t)) blocks.Add((x, y));
                else if (isSolid(t)) walls[x, y] = true;
            }
        // Non-solid tiles (items, decorations) can be walked over but not pushed onto.
        var start = (Blocks: blocks.ToHashSet(), Hero: hero);
        var seen = new HashSet<string>();
        var queue = new Queue<(HashSet<(int x, int y)> Blocks, (int x, int y) Hero, Move? First)>();
        queue.Enqueue((start.Blocks, start.Hero, null));

        while (queue.Count > 0 && seen.Count < maxStates)
        {
            var (bs, at, first) = queue.Dequeue();
            var reach = Reach(bs, at);
            if (goals.Any(reach.Contains))
                return first; // null when already reachable: nothing to move
            var key = Key(bs, reach);
            if (!seen.Add(key)) continue;

            foreach (var b in bs)
                foreach (var (dx, dy) in Straight)
                {
                    // Push: hero behind the block, block lands one further on.
                    if (reach.Contains((b.x - dx, b.y - dy)) && Free(b.x + dx, b.y + dy, bs))
                        Enqueue(bs, b, (b.x + dx, b.y + dy), b, new Move(b.x, b.y, dx, dy, false));
                    // Pull: hero beside it steps away, block follows onto the hero's square.
                    if (reach.Contains((b.x + dx, b.y + dy)) && Free(b.x + 2 * dx, b.y + 2 * dy, bs))
                        Enqueue(bs, b, (b.x + dx, b.y + dy), (b.x + 2 * dx, b.y + 2 * dy), new Move(b.x, b.y, dx, dy, true));
                }

            void Enqueue(HashSet<(int x, int y)> from, (int x, int y) b, (int x, int y) to, (int x, int y) heroAfter, Move m)
            {
                var next = new HashSet<(int x, int y)>(from);
                next.Remove(b);
                next.Add(to);
                queue.Enqueue((next, heroAfter, first ?? m));
            }
        }
        return null;

        // A block only ever moves into a truly empty square.
        bool Free(int x, int y, HashSet<(int x, int y)> bs) =>
            In(x, y) && !bs.Contains((x, y)) && (grid[x, y] == 0xFFFF || isBlock(grid[x, y]));

        HashSet<(int x, int y)> Reach(HashSet<(int x, int y)> bs, (int x, int y) from)
        {
            var seenCells = new HashSet<(int x, int y)> { from };
            var q = new Queue<(int x, int y)>(seenCells);
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue();
                foreach (var (dx, dy) in Around)
                {
                    int nx = x + dx, ny = y + dy;
                    if (In(nx, ny) && !walls[nx, ny] && !bs.Contains((nx, ny)) && seenCells.Add((nx, ny)))
                        q.Enqueue((nx, ny));
                }
            }
            return seenCells;
        }

        static string Key(HashSet<(int x, int y)> bs, HashSet<(int x, int y)> reach) =>
            string.Join(";", bs.OrderBy(b => b).Select(b => $"{b.x},{b.y}")) + "|" + reach.Min();
    }

    /// <summary>Where the hero can walk from <paramref name="hero"/> without moving any block.</summary>
    public static HashSet<(int x, int y)> Reach(ushort[,] grid, Func<ushort, bool> isBlock, Func<ushort, bool> isSolid,
        (int x, int y) hero)
    {
        int w = grid.GetLength(0), h = grid.GetLength(1);
        var seen = new HashSet<(int x, int y)> { hero };
        var q = new Queue<(int x, int y)>(seen);
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            foreach (var (dx, dy) in Around)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                var t = grid[nx, ny];
                if (t != 0xFFFF && (isBlock(t) || isSolid(t))) continue;
                if (seen.Add((nx, ny))) q.Enqueue((nx, ny));
            }
        }
        return seen;
    }

    /// <summary>
    /// The one check this needs: a boulder row over a pressure plate, as in Indiana Jones'
    /// zone 104. Run by --self-test.
    /// </summary>
    public static void SelfTest()
    {
        // # wall, B block, . floor; the plate is under the far boulder, at (1,1)
        string[] map =
        {
            "###..####",
            "#BBB....#",
            "###..####",
            "#.......#",
            "###..####",
        };
        int w = map[0].Length, h = map.Length;
        var grid = new ushort[w, h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                grid[x, y] = map[y][x] switch { '#' => (ushort)1, 'B' => (ushort)2, _ => (ushort)0xFFFF };

        var goals = new HashSet<(int, int)> { (1, 1) };
        (int, int) hero = (1, 3);
        int moves = 0;
        for (; moves < 30; moves++)
        {
            var m = FirstMove(grid, t => t == 2, t => t == 1, hero, goals);
            if (m == null) break;
            var v = m.Value;
            // apply it: push moves the block on and the hero into its square; pull moves both back
            var to = (v.X + v.Dx, v.Y + v.Dy);
            grid[to.Item1, to.Item2] = 2;
            grid[v.X, v.Y] = 0xFFFF;
            hero = v.Pull ? (v.X + 2 * v.Dx, v.Y + 2 * v.Dy) : (v.X, v.Y);
        }
        if (moves == 0 || moves >= 30 || grid[1, 1] != 0xFFFF)
            throw new Exception($"BlockPuzzle: boulder row not solved ({moves} moves)");
    }
}
