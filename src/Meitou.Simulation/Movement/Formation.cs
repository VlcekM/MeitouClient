using System.Numerics;

namespace Meitou.Simulation;

/// <summary>The FACTION <c>squad formation</c> values (<c>fcs_enums.def</c> SquadFormation).</summary>
public enum SquadFormation { Random = 0, Caravan = 1, Military = 2 }

/// <summary>One character in a squad's formation: its role (<see cref="SquadRole"/>), whether it is an animal, and its radius (1.5 x the footprint radius, as the original).</summary>
public readonly record struct FormationMember(CharacterId Id, int Role, bool Animal, float Radius);

/// <summary>
/// Where a squad's followers keep station (<see cref="Layout"/>, the original's <c>Blackboard::buildFormation</c>, docs/game/pathfinding.md "Squads travelling together")
/// and where the characters of a move order end up (<see cref="Scatter"/>, the original's ground-click spread).
/// </summary>
public static class Formation
{
    /// <summary>The original's formation radius per footprint radius (<c>getRadius() * 1.5</c>).</summary>
    public const float RadiusScale = 1.5f;

    /// <summary>
    /// The slot of each character relative to the <paramref name="head"/>, in the original's frame: X behind the head, Y (the world's Z after <see cref="ToWorld"/>) across.
    /// <paramref name="others"/> are the squad's other members in squad order, <paramref name="slaves"/> those walking with the head (the original attaches each slave to its
    /// owner; Meitou's slave owner is a faction, so a squad's slaves go with its head, an engine choice). <paramref name="draw"/> gives the uniform draws in [0, 1).
    /// </summary>
    public static Dictionary<CharacterId, Vector2> Layout(SquadFormation type, FormationMember head, IReadOnlyList<FormationMember> others, IReadOnlyList<FormationMember> slaves, Func<float> draw)
    {
        var slots = new Dictionary<CharacterId, Vector2> { [head.Id] = Vector2.Zero };
        if (others.Count == 0 && slaves.Count == 0) return slots;
        float Range(float a, float b) => a + draw() * (b - a);
        switch (type)
        {
            case SquadFormation.Military: Military(slots, others, slaves); break;
            case SquadFormation.Caravan: Caravan(slots, head, others, slaves, Range, draw); break;
            default: Scatter(slots, head, others, slaves, draw); break;
        }
        return slots;
    }

    /// <summary>A slot of <see cref="Layout"/> in the world, for a head heading along <paramref name="heading"/> (unit, X/Z): the original turns −X onto the heading.</summary>
    public static Vector2 ToWorld(Vector2 slot, Vector2 heading) => -slot.X * heading + slot.Y * new Vector2(heading.Y, -heading.X);

    /// <summary>MILITARY: blocks of rows behind the head, rows across, c to a row (c from the number of humans); slaves, then small and big animals, then the humans.</summary>
    static void Military(Dictionary<CharacterId, Vector2> slots, IReadOnlyList<FormationMember> others, IReadOnlyList<FormationMember> slaves)
    {
        var humans = others.Where(m => !m.Animal).ToList();
        int n = Math.Max(2, humans.Count);
        int perRow = (int)MathF.Ceiling(n / MathF.Round(MathF.Sqrt(n), MidpointRounding.AwayFromZero));
        float start = 25;
        foreach (var block in new[] { slaves.ToList(), others.Where(m => m.Animal && m.Radius < 10).ToList(), others.Where(m => m.Animal && m.Radius >= 10).ToList(), humans })
        {
            if (block.Count == 0) continue;
            float r = block.Max(m => m.Radius), step = 2 * r + 10, half = step / 2;
            float x = start + r, across = (Math.Min(perRow, block.Count) - 1) * half, z = -across;
            int inRow = 0;
            for (int k = 0; k < block.Count; k++)
            {
                slots[block[k].Id] = new Vector2(x, z);
                z += step;
                if (++inRow == perRow && k < block.Count - 1)
                {
                    inRow = 0;
                    int left = block.Count - k - 1;
                    if (left < perRow) across = (left - 1) * half;   // a short last row is centred
                    x += step;
                    z = -across;
                }
            }
            start = r + x + 20;
        }
    }

    /// <summary>CARAVAN: SQUAD_1 members in single file behind the head, slaves behind it; everyone else out to the sides near the front.</summary>
    static void Caravan(Dictionary<CharacterId, Vector2> slots, FormationMember head, IReadOnlyList<FormationMember> others, IReadOnlyList<FormationMember> slaves,
        Func<float, float, float> range, Func<float> draw)
    {
        var circles = new List<(Vector2 At, float R)> { (Vector2.Zero, head.Radius) };
        float edge = range(10, 20) + head.Radius;
        void InFile(FormationMember m, float gapMin, float gapMax)
        {
            float x = range(gapMin, gapMax) + edge + m.Radius;
            edge = x + m.Radius;
            slots[m.Id] = new Vector2(x, 0);
            circles.Add((new Vector2(x, 0), m.Radius));
        }
        foreach (var s in slaves) InFile(s, 5, 10);
        var side = new List<FormationMember>();
        var sideAnimals = new List<FormationMember>();
        foreach (var m in others)
            if (m.Role == (int)SquadRole.Squad1) InFile(m, 10, 20);
            else (m.Animal ? sideAnimals : side).Add(m);
        // The original places the side members only when one of them is SQUAD_2 (the rest keep no slot); Meitou always does (engine choice).
        bool Free(Vector2 at, float r) => circles.All(c => (c.R + r) * (c.R + r) <= Vector2.DistanceSquared(at, c.At));
        float frontMin = range(5, 10) + head.Radius, frontMax = range(20, 40);
        Vector2 Beside(FormationMember m)
        {
            var at = new Vector2(range(frontMin, frontMax), 0);
            float sign = draw() > 0.5f ? 1 : -1;
            for (int tries = 0; tries < 256; tries++)
            {
                at.Y += range(10, 50) * sign;
                if (Free(at, m.Radius)) break;
            }
            return at;
        }
        int file = circles.Count;
        foreach (var m in side)
        {
            var at = Beside(m);
            slots[m.Id] = at;
            circles.Add((at, m.Radius));
        }
        // Animals stand by the side members in turn (diagonally off one, then outward), the rest like them.
        int sides = circles.Count;
        for (int k = 0; k < sideAnimals.Count; k++)
        {
            var m = sideAnimals[k];
            Vector2 at;
            if (file + k < sides)
            {
                var (by, r) = circles[file + k];
                var sign = new Vector2(draw() > 0.5f ? 1 : -1, draw() > 0.5f ? 1 : -1);
                at = by + sign * (r + m.Radius);
                for (int tries = 0; tries < 256 && !Free(at, m.Radius); tries++)
                    at += sign * new Vector2(range(2, 5), range(2, 5));
            }
            else at = Beside(m);
            slots[m.Id] = at;
            circles.Add((at, m.Radius));
        }
    }

    /// <summary>The gap the RANDOM layout keeps between circles.</summary>
    const float ScatterGap = 5;

    /// <summary>RANDOM: everyone scattered round the head, pushed apart; slaves in a cluster behind, and then a fifth of the members as a rear guard.</summary>
    static void Scatter(Dictionary<CharacterId, Vector2> slots, FormationMember head, IReadOnlyList<FormationMember> others, IReadOnlyList<FormationMember> slaves, Func<float> draw)
    {
        var all = new List<FormationMember>(others.Count + 1) { head };
        all.AddRange(others);
        var circles = new List<(Vector2 At, float R)>();
        Vector2 Polar(float radius)
        {
            float u = draw(), angle = draw() * MathF.Tau;
            return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (u * radius);
        }
        float front = 0;
        float spread = MathF.Sqrt(all.Count * 100f);
        foreach (var m in all)
        {
            var at = Polar(spread);
            if (at.X > front) front = at.X;
            at = PushApart(circles, at, m.Radius, draw);
            if (m.Id != head.Id) slots[m.Id] = at;
            circles.Add((at, m.Radius));
        }
        if (slaves.Count == 0) return;   // the original forms the rear guard only after placing slaves (Observed)
        float cluster = MathF.Sqrt(slaves.Count * 50f);
        var centre = new Vector2(cluster + front, 0);
        foreach (var s in slaves)
        {
            var at = centre + Polar(cluster);
            front = MathF.Max(front, at.X);
            at = PushApart(circles, at, s.Radius, draw);
            slots[s.Id] = at;
            circles.Add((at, s.Radius));
        }
        int last = all.Count - 1;
        for (int guard = (int)MathF.Ceiling(all.Count / 5f); guard > 0; guard--)
        {
            while (last > 0 && all[last].Animal) last--;
            if (last == 0) break;
            var at = PushApart(circles, new Vector2(front + ScatterGap + 20 + circles[last].R, 0), circles[last].R, draw, last);
            circles[last] = (at, circles[last].R);
            slots[all[last].Id] = at;
            last--;
        }
    }

    /// <summary>
    /// <paramref name="at"/> moved out of every circle it overlaps (with <see cref="ScatterGap"/>), one at a time, starting over after each move; a later move keeps the
    /// first one's side (it reverses onto it rather than back into an earlier circle). <paramref name="self"/> is its own circle, skipped.
    /// </summary>
    static Vector2 PushApart(List<(Vector2 At, float R)> circles, Vector2 at, float radius, Func<float> draw, int self = -1)
    {
        var first = Vector2.Zero;
        int pushes = 0;
        for (int j = 0; j < circles.Count && pushes < 64; j++)
        {
            if (j == self) continue;
            var (c, r) = circles[j];
            float reach = r + radius + ScatterGap;
            var v = at - c;
            if (v.LengthSquared() >= reach * reach) continue;
            var d = v == Vector2.Zero ? new Vector2(draw() - 0.5f, draw() - 0.5f) : v;
            if (pushes == 0) first = d;
            else if (Vector2.Dot(d, first) < 0) d = first;
            d = Vector2.Normalize(d);
            float b = Vector2.Dot(v, d);
            at += d * (-b + MathF.Sqrt(MathF.Max(0, b * b - v.LengthSquared() + reach * reach)));
            pushes++;
            j = -1;
        }
        return at;
    }

    /// <summary>
    /// The targets of a ground-click move order of <paramref name="count"/> characters (order 0x1d in <c>FUN_1407f9e20</c>, <b>Verified</b>): the C runtime's rand is seeded with the
    /// count; the first character goes to the point, every other one to point + (sin a, cos a) x r on X/Z, with r = u x sqrt(60 x count) + 5 and then a = u x 2 pi, each u a fresh
    /// rand() / 32768 (<c>DAT_141742b48</c>). Meitou replays the MSVC generator (state x 214013 + 2531011, output bits 16 to 30), so the spread is a pure function of the count and the point.
    /// </summary>
    public static Vector2[] Scatter(int count, Vector2 target)
    {
        var result = new Vector2[count];
        uint state = (uint)count;
        float Next()
        {
            state = state * 214013 + 2531011;
            return ((state >> 16) & 0x7fff) / 32768f;
        }
        float reach = MathF.Sqrt(ScatterArea * count);
        for (int i = 0; i < count; i++)
        {
            if (i == 0) { result[i] = target; continue; }
            float r = Next() * reach + ScatterMin;
            float a = Next() * 2 * MathF.PI;
            result[i] = target + new Vector2(MathF.Sin(a), MathF.Cos(a)) * r;
        }
        return result;
    }

    /// <summary>The ground-click spread's area per character (sqrt(60 x count)) and least distance from the point (<c>DAT_141685340</c> = 5).</summary>
    const float ScatterArea = 60, ScatterMin = 5;
}
