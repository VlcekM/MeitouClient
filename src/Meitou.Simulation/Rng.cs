using System.Runtime.CompilerServices;

namespace Meitou.Simulation;

/// <summary>What a random roll is for; part of the stream key so two systems never share a stream (docs/simulation.md, "Threading": randomness).</summary>
public enum RngPurpose : uint
{
    Test = 0,
    /// <summary>The body rolls (production stats, medical rolls, hits) use this; it has the value of <see cref="Test"/> on purpose, as a distinct value would re-roll every body (changes every state hash).</summary>
    Body = 0,
    Wander = 1,
    Think = 2,
    Combat = 3,
    Spawn = 4,
    Loot = 5,
    Slow = 6,
    Animation = 7,
    Flee = 8,
    Sense = 9,
    /// <summary>Crossbow shots: the deviation cone, the damage roll and the friendly-fire pass (docs/simulation.md "Combat", ranged).</summary>
    Ranged = 10,
    /// <summary>The economy: town market factors, shop stock, shop purses, fencing (docs/simulation.md "Trade").</summary>
    Trade = 11,
    /// <summary>Which towns a map item reveals (<see cref="Items.MapItems"/>).</summary>
    MapItem = 12,
    /// <summary>The stealth knock-out roll (<see cref="Carrying.CarrySystem.StealthKoChance"/>).</summary>
    StealthKo = 13,
    /// <summary>Lock levels at creation and the pick rolls (<see cref="Locks.FixtureRegistry"/>, <see cref="Locks.Lockpicking"/>), keyed by the fixture's id.</summary>
    Lock = 14,
    /// <summary>The refund counts of a dismantled site (<see cref="Construction.ConstructionSystem"/>), keyed by the site's id and the material entry.</summary>
    Build = 15,
    /// <summary>The faction campaigns (<c>PopulationCampaigns.cs</c>): rolls, follow-ups, request delays and the campaign squads' places, keyed by the faction.</summary>
    Campaign = 16,
    /// <summary>Dialogue: line and text picks, chance rolls, interjectors, personalities (<see cref="Dialogue.DialogueSystem"/>).</summary>
    Dialogue = 17,
    /// <summary>Farms: the planting draws and the harvest rolls (<see cref="Production.ProductionSystem"/>), keyed by the building.</summary>
    Farm = 18,
    /// <summary>Crafting: the critical (one tier better) roll of each craft, keyed by the bench.</summary>
    Craft = 19,
    /// <summary>The random name of a <c>named</c> character at its spawn, keyed like its spawn rolls.</summary>
    Name = 20,
    /// <summary>The blood trail's drips (<see cref="BodySystem"/>; display only).</summary>
    BloodTrail = 21,
    /// <summary>luckyren Mode: where a burst character's gear falls (<see cref="BodySystem"/>).</summary>
    Luckyren = 22,
    /// <summary>A <c>malnourished</c> squad's hunger floor (<see cref="BodySystem"/>), keyed by the squad id.</summary>
    Malnourished = 23,
    /// <summary>A chaser's hearing roll on its fleeing target (<see cref="AggroSystem"/>, the chase give-up).</summary>
    ChaseSense = 24,
    /// <summary>Squad formations: a layout's draws (keyed by the squad) and a plain follower's distance (keyed by the follower) (<see cref="MovementSystem"/>).</summary>
    Formation = 25,
    /// <summary>A stealth KO target's hearing roll on the creeping attacker's last step (<see cref="Carrying.CarrySystem"/>).</summary>
    KnockoutHear = 26,
    /// <summary>The kidnap roll (<see cref="Carrying.CarrySystem.KidnapChance"/>).</summary>
    Kidnap = 27,
    /// <summary>The steal roll (<see cref="Crime.CrimeSystem.StealChance"/>) and its in-bed coin flip.</summary>
    Steal = 28,
    /// <summary>Advanced ragdolls: the spin a downing blow gives each arm and leg (<see cref="Ragdolls.RagdollSystem"/>), keyed by the character, counter the body.</summary>
    RagdollSpin = 29,
}

/// <summary>
/// Seeded random numbers with no state to share: a roll is a hash of (world seed, entity, purpose, counter) with SplitMix64's
/// finalizer, so the order threads finish in, or which thread handles a character, never changes a roll. The original's own random
/// sequence is not reproduced (docs/simulation.md).
/// </summary>
public static class Rng
{
    const ulong Golden = 0x9E3779B97F4A7C15UL;

    /// <summary>SplitMix64's output function: a bijective, well-mixed 64 bit hash.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong x)
    {
        x += Golden;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>The roll for a (seed, entity key, purpose, counter) key.</summary>
    public static ulong Hash(ulong seed, ulong entity, RngPurpose purpose, ulong counter = 0)
    {
        ulong h = Mix(seed);
        h = Mix(h ^ entity);
        h = Mix(h ^ ((ulong)purpose << 32 | 0x5bd1e995UL));
        return Mix(h ^ counter);
    }

    /// <summary>The key of a character for its streams: slot and generation, so a reused slot gets new rolls.</summary>
    public static ulong Key(CharacterId id) => (uint)id.Slot | ((ulong)(uint)id.Generation << 32);

    /// <summary>A float in [0, 1) from a roll (24 bits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Float(ulong roll) => (roll >> 40) * (1f / (1 << 24));

    /// <summary>An integer in [0, <paramref name="bound"/>) from a roll (the tiny modulo bias is irrelevant at these bounds).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Int(ulong roll, int bound) => (int)((roll >> 11) % (ulong)bound);

    /// <summary>A stable 64 bit hash of a string (FNV-1a over its UTF-16 chars, the same basis and prime as <see cref="StateHasher"/>), for keys made from record ids and names.</summary>
    public static ulong StableHash(string s)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (char c in s) h = (h ^ c) * 0x100000001B3UL;
        return h;
    }

    /// <summary>
    /// A point in the disc around <paramref name="centre"/>, uniform in area, from one roll: the angle from the roll, the radius from its mix.
    /// The radius is <c>sqrt(u) * radius / divisor</c>, multiplied and then divided in that order (the player's start spot uses a divisor of 3).
    /// </summary>
    public static System.Numerics.Vector2 PointInDisc(ulong roll, System.Numerics.Vector2 centre, float radius, float divisor = 1)
    {
        float angle = Float(roll) * MathF.Tau, r = MathF.Sqrt(Float(Mix(roll))) * radius / divisor;
        return centre + new System.Numerics.Vector2(MathF.Sin(angle), MathF.Cos(angle)) * r;
    }
}

/// <summary>
/// One (seed, entity, purpose) stream: <see cref="Next"/> walks a counter, <see cref="At"/> reads a roll by a counter of the
/// caller's choosing (a tick number, say), which is what systems use when the same roll must be reproducible from the key alone.
/// </summary>
public struct RandomStream(ulong seed, ulong entity, RngPurpose purpose)
{
    ulong counter;

    public ulong NextRoll() => Rng.Hash(seed, entity, purpose, counter++);
    public float NextFloat() => Rng.Float(NextRoll());
    public int NextInt(int bound) => Rng.Int(NextRoll(), bound);
    public readonly ulong At(ulong index) => Rng.Hash(seed, entity, purpose, index);
}

/// <summary>An order-sensitive 64 bit hash of the values added, for <see cref="World.StateHash"/>. Written here, no packages.</summary>
public struct StateHasher
{
    ulong h;

    public StateHasher() => h = 0xCBF29CE484222325UL;

    public void Add(ulong v) => h = Rng.Mix(h ^ v) * 0x100000001B3UL + 0x1B873593UL;
    public void Add(long v) => Add((ulong)v);
    public void Add(int v) => Add((ulong)(uint)v);
    public void Add(bool v) => Add(v ? 1UL : 0UL);
    public void Add(float v) => Add((ulong)(uint)BitConverter.SingleToInt32Bits(v == 0 ? 0f : v));
    public void Add(double v) => Add((ulong)BitConverter.DoubleToInt64Bits(v == 0 ? 0d : v));
    public void Add(System.Numerics.Vector2 v) { Add(v.X); Add(v.Y); }
    public void Add(System.Numerics.Vector3 v) { Add(v.X); Add(v.Y); Add(v.Z); }
    public void Add(CharacterId id) { Add(id.Slot); Add(id.Generation); }

    public readonly ulong Value => Rng.Mix(h);
}
