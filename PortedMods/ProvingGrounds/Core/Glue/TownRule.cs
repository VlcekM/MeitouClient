namespace ProvingGrounds.Core.Glue;

/// <summary>What a town is to the membership rules: a placed town of a given TOWN type, or a player outpost.</summary>
public enum TownKind
{
    /// <summary>A placed town (any TOWN type but a nest marker or Null).</summary>
    Placed,
    /// <summary>A placed TOWN of type NestMarker (radius 0).</summary>
    NestMarker,
    /// <summary>A placed TOWN of type Null (radius 0).</summary>
    Null,
    /// <summary>A player town (outpost), Meitou <c>world.Buildings.Towns</c>.</summary>
    Player,
}

/// <summary>A town as the rules see it: centre on the map (X, Z), its radius, its kind.</summary>
public readonly record struct TownCircle(float X, float Z, float Radius, TownKind Kind);

/// <summary>
/// The "which town" rules of the original's <c>getCurrentTownLocation()</c> and <c>Building::getTown()</c>, as Meitou 0.17.3 implements them, over
/// plain circles (the glue, src/Engine/Towns.cs, builds the list: placed sites in <c>PopulationData.Sites</c> order, then player towns in
/// <c>world.Buildings.Towns</c> order). Indexes into that list; -1 = no town. Heights are ignored (X/Z distance), as in Meitou.
/// </summary>
public static class TownRule
{
    /// <summary>Meitou TownBuildingBuilder.cs:17 <c>InteriorReach</c>.</summary>
    public const float InteriorReach = 10000;

    /// <summary>
    /// The town a point is in by Meitou's <c>AiSystem.TownAt</c> (src/Meitou.Simulation/Ai/AiSystem.Packages.cs:66-92, "the original's
    /// FUN_1402688d0", the AI blackboard's town): the nearest town (placed ones skip nest markers and Null; player towns count), ties keep the
    /// first; it is the answer only when the point is within that town's radius. NOT the original's <c>getCurrentTownLocation()</c> (that is the
    /// status town, <see cref="CharacterTown{T}"/>); the glue keeps it as the last fallback of that rule and for points.
    /// </summary>
    public static int CurrentTown(float x, float z, IReadOnlyList<TownCircle> towns)
    {
        int best = -1;
        float bestD = float.MaxValue, bestR = 0;
        for (int i = 0; i < towns.Count; i++)
        {
            var t = towns[i];
            if (t.Kind is TownKind.NestMarker or TownKind.Null) continue;
            float d = Dist2(x, z, t);
            if (d < bestD) (best, bestD, bestR) = (i, d, t.Radius);
        }
        return best >= 0 && bestD <= bestR * bestR ? best : -1;
    }

    /// <summary>The original's hold of the current town (FUN_1405a51e0 step 2, <c>DAT_14168ad18</c>, Meitou AiSystem.StayReach): 1.5 x radius.</summary>
    public const float StayReach = 1.5f;

    /// <summary>Whether a point is strictly within <see cref="StayReach"/> x the town's radius of its centre (X/Z; Meitou AiSystem.Reaches).</summary>
    public static bool Holds(float x, float z, TownCircle town)
    {
        float r = town.Radius * StayReach;
        return Dist2(x, z, town) < r * r;
    }

    /// <summary>
    /// The town a character is in, the original's <c>Character::getCurrentTownLocation()</c> (vfunc 0x78 = <c>FUN_1405c89b0</c>: the status
    /// object's current town <c>+0x88</c>, Meitou docs/game/economy.md "DA_ARREST_TARGET"), which <c>FUN_1405a51e0</c> writes in five steps
    /// (docs/game/carry-loot.md "The status town": 1 the player faction's town within 1.25 x radius, 2 the current town within 1.5 x radius or, with
    /// none, the nearest town within 1 x, 3 a walled town's point within 300, 4 the nearest player town with step 2's factor, 5 the town of the
    /// building the character stands in), and whatever it finds becomes the current town of the next update. Meitou keeps it as
    /// <c>CharacterCold.StatusTown</c> (<paramref name="status"/>: steps 1, 2, 4 only; none at load until its first update, and never a step-5 town).
    /// <para>Order: a kept <paramref name="status"/> wins and drops the memo. Otherwise <paramref name="statusTownAt"/> (Meitou's
    /// <c>AiSystem.StatusTownAt</c>: steps 1, 2, 4) is asked with the port's memo as the current town, then <paramref name="building"/> (step 5);
    /// the answer becomes the memo (none clears it). <paramref name="memo"/> is the port's stand-in for <c>+0x88</c> while Meitou's own is none, so a
    /// town step 5 gave is held to 1.5 x its radius by step 2 on the next read, as in the original, and dropped past it (or when Meitou's
    /// StatusTown gives a town).</para>
    /// </summary>
    public static T CharacterTown<T>(T status, Func<T, T> statusTownAt, Func<T> building, Func<T, bool> isNone, ref T memo, T none)
    {
        if (!isNone(status)) { memo = none; return status; }
        T town = statusTownAt(memo);
        if (isNone(town)) town = building();
        memo = town;
        return town;
    }

    /// <summary>
    /// The town a level-data building belongs to, CAP 6's rule: Meitou <c>TownBuildingBuilder.MemberOf</c>
    /// (src/Meitou.Data/World/TownBuildingBuilder.cs:164-187): the nearest town, nest markers left out, whose radius holds it; else, for a record with
    /// <c>interior</c> parts, the nearest town of any kind strictly within <see cref="InteriorReach"/>; else none. (Meitou applies it to placed
    /// towns at build; the player's buildings carry their town number instead.)
    /// </summary>
    public static int MemberOf(float x, float z, IReadOnlyList<TownCircle> towns, bool hasInterior)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < towns.Count; i++)
        {
            var t = towns[i];
            if (t.Kind == TownKind.NestMarker) continue;
            float d = Dist2(x, z, t);
            if (d <= t.Radius * t.Radius && d < bestD) (best, bestD) = (i, d);
        }
        if (best >= 0 || !hasInterior) return best;

        bestD = InteriorReach * InteriorReach;
        for (int i = 0; i < towns.Count; i++)
        {
            float d = Dist2(x, z, towns[i]);
            if (d < bestD) (best, bestD) = (i, d);
        }
        return best;
    }

    /// <summary>The original's "same town" test (<c>building-&gt;getTown() == character-&gt;getCurrentTownLocation()</c>, TownArena.cpp:368-380,
    /// TW §2.1) on two indexes: equal and not "no town". (In the C++ two null towns compare equal, but every use first requires a town.)</summary>
    public static bool SameTown(int a, int b) => a >= 0 && a == b;

    static float Dist2(float x, float z, in TownCircle t)
    {
        float dx = x - t.X, dz = z - t.Z;
        return dx * dx + dz * dz;
    }
}

/// <summary>The port's per-character memo of the status town while Meitou's <c>CharacterCold.StatusTown</c> is none (<see
/// cref="TownRule.CharacterTown{T}"/>): one per world (src/Engine/Towns.cs, which the system builds again for every world, so a world change
/// clears it); <see cref="Clear"/> empties it. A key no longer used (a character gone) stays until the world ends.</summary>
public sealed class StatusTownMemo<TId, TTown> where TId : notnull
{
    readonly Dictionary<TId, TTown> memo = new();
    readonly TTown none;

    public StatusTownMemo(TTown none) => this.none = none;

    /// <summary>The memo of <paramref name="id"/> (none when it has none).</summary>
    public TTown Get(TId id) => memo.TryGetValue(id, out var t) ? t : none;

    /// <summary>Stores a memo; <paramref name="isNone"/> true removes it.</summary>
    public void Set(TId id, TTown town, bool isNone)
    {
        if (isNone) memo.Remove(id);
        else memo[id] = town;
    }

    public int Count => memo.Count;
    public void Clear() => memo.Clear();
}

