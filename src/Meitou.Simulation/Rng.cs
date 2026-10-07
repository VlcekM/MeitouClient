using System.Runtime.CompilerServices;

namespace Meitou.Simulation;

/// <summary>What a random roll is for; part of the stream key so two systems never share a stream (docs/simulation.md, "Threading": randomness).</summary>
public enum RngPurpose : uint
{
    Test = 0,
    Wander = 1,
    Think = 2,
    Combat = 3,
    Spawn = 4,
    Loot = 5,
    Slow = 6,
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

    public readonly ulong Value => Rng.Mix(h);
}
