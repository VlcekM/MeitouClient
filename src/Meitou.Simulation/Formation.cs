using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// Where the characters of a move order end up: a block around the clicked point, its rows across the direction of travel, the
/// characters nearest the target in the front row. The original has formations (FACTION <c>squad formation</c> and a "Follow
/// Formation" order) but the slot rules are <b>Unknown</b> (docs/game/pathfinding.md "Local avoidance and formation"), so this is an
/// engine choice: a compact grid about as wide as it is deep, spaced so that neighbours do not push each other (footprint radius 4).
/// </summary>
public static class Formation
{
    /// <summary>Distance between neighbouring slots: twice the human footprint radius plus a margin.</summary>
    public const float Spacing = 9;

    /// <summary>
    /// One target per character, in the order of <paramref name="positions"/> (X/Z where they stand now). One character goes to the
    /// point itself. Ties (equal positions) break by the order given, so the result is a pure function of the input.
    /// </summary>
    public static Vector2[] Place(IReadOnlyList<Vector2> positions, Vector2 target, float spacing = Spacing)
    {
        int n = positions.Count;
        var result = new Vector2[n];
        if (n == 0) return result;
        if (n == 1)
        {
            result[0] = target;
            return result;
        }
        var centre = Vector2.Zero;
        foreach (var p in positions) centre += p;
        centre /= n;
        var forward = target - centre;
        forward = forward.LengthSquared() < 1e-4f ? new Vector2(0, 1) : Vector2.Normalize(forward);
        var right = new Vector2(forward.Y, -forward.X);   // 90 degrees clockwise seen from above (X right, Z up on the map)

        int columns = (int)MathF.Ceiling(MathF.Sqrt(n));
        int rows = (n + columns - 1) / columns;
        // Characters by how far ahead they are (the front row takes the leaders), then each row left to right.
        var order = Enumerable.Range(0, n).OrderByDescending(i => Vector2.Dot(positions[i] - centre, forward)).ThenBy(i => i).ToArray();
        int next = 0;
        for (int row = 0; row < rows; row++)
        {
            int inRow = Math.Min(columns, n - next);
            var members = order.Skip(next).Take(inRow).OrderBy(i => Vector2.Dot(positions[i] - centre, right)).ThenBy(i => i).ToArray();
            next += inRow;
            // The block is centred on the click: rows go back from the front, a short last row stays centred.
            float depth = ((rows - 1) / 2f - row) * spacing;
            for (int c = 0; c < inRow; c++)
            {
                float lateral = (c - (inRow - 1) / 2f) * spacing;
                result[members[c]] = target + forward * depth + right * lateral;
            }
        }
        return result;
    }
}
