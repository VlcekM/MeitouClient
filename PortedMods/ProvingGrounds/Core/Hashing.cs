using System.Text;

namespace ProvingGrounds.Core;

/// <summary>The original's own random-number and hash helpers, bit for bit (task 1.1). Never System.Random.
///
/// Every variant in the PURE files (port-map kind `pure`), found by grepping `1664525`, `1013904223`, `0x9e3779b9`, `2166136261`,
/// `16777619`, `0x85ebca6b`, `&lt;&lt; 13`, `^=`:
/// - <see cref="Xorshift32"/>: TownMatchmakingPolicy.h:111-115 (`Detail::Next`), the only xorshift in pure code.
/// - <see cref="Fnv1a32(uint, string)"/>: TownMatchmakingPolicy.h:116-120 (`Detail::Hash`). With seed 0 it is plain FNV-1a
///   (`0 ^ 2166136261u == 2166136261u`), which is exactly the loop in TownLimbShopPolicy.h:65-70 (`Seed`) and the `first` hash of
///   TownMatchmakingRuntime.cpp:168-170 (glue): same basis, same byte order (xor then multiply), same `unsigned char` cast. So one
///   function stands for all three.
/// - <see cref="Lcg"/> / <see cref="LcgNext"/>: TownChallengePolicy.h:159-163 (`Draw`, which advances `card.seed` and returns it);
///   the same step is TownDiagnosticRun.cpp:65 (`prior*1664525u+1013904223u`).
/// - <see cref="ChallengeMix"/>: TownChallengePolicy.h:344-346 and :403, the same three statements written twice inline.
/// - <see cref="LimbShopMix"/>, <see cref="LimbShopSeed"/>: TownLimbShopPolicy.h:52-61 and :63-74.
///
/// The headers build no named wrappers on top ("next float", "next below n"): every derived draw is an inline expression at its
/// call site (e.g. `Detail::Next(random) % 10000`, `(Draw(card) &gt;&gt; 8) % 6`, `Next(v) % 10001 / 10000.0` in double) and is ported
/// with the file that writes it.
///
/// The C runtime's <c>rand()</c> (<see cref="CRand"/>) is here too: its only caller is glue (PrisonerUtil.cpp:765, task 6.2).
///
/// Variants in GLUE files, not ported here (each goes with its file): TownSpectators.cpp:140-146 `ChatterRandom` (the same three
/// xorshift shifts, but its zero fix is `GetTickCount() ^ 0x9e3779b9u`, so it is not <see cref="Xorshift32"/>; with a nonzero state
/// it gives the same value); TownMatchmakingRuntime.cpp:168-172 `second = (second * 33u) ^ byte` from 5381; TownArena.cpp:247
/// `card.seed = card.seed * 33u + byte`; ArenaNativePersistence.cpp:168 `(GetTickCount() * 1664525u) | 1u`; TownArena.cpp:528
/// `card.seed ^ (0x9e3779b9u * nonce)`.
///
/// Strings: the C++ hashes the bytes of a `std::string` (each `static_cast&lt;unsigned char&gt;`). Record ids and names reach the
/// plugin as the engine's narrow strings; the glue will give Core C# strings, and the string overloads here hash their UTF-8 bytes.
/// For ASCII (every record id, every string the code hashes itself) that is identical to the C++. A name with non-ASCII letters
/// hashes like the C++ only if the original engine's string held UTF-8 too; the span overloads take raw bytes when that matters.
/// `unsigned` in the MSVC x64 build is 32 bits, so <c>uint</c>; all arithmetic wraps (<c>unchecked</c>).</summary>
public static class Hashing
{
    public const uint ZeroSeedFix = 0x9e3779b9u;
    public const uint FnvOffsetBasis = 2166136261u;
    public const uint FnvPrime = 16777619u;
    public const uint LcgMultiplier = 1664525u;
    public const uint LcgIncrement = 1013904223u;

    /// <summary>Advances <paramref name="seed"/> and returns the new value; a zero seed is first replaced by 0x9e3779b9.</summary>
    // C++: TownMatchmakingPolicy.h:111-115
    public static uint Xorshift32(ref uint seed)
    {
        unchecked
        {
            if (seed == 0) seed = ZeroSeedFix;
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            return seed;
        }
    }

    /// <summary>FNV-1a over the bytes, starting from <c>seed ^ 2166136261</c>.</summary>
    // C++: TownMatchmakingPolicy.h:116-120
    public static uint Fnv1a32(uint seed, ReadOnlySpan<byte> value)
    {
        unchecked
        {
            uint hash = seed ^ FnvOffsetBasis;
            for (int i = 0; i < value.Length; ++i) { hash ^= value[i]; hash *= FnvPrime; }
            return hash;
        }
    }

    /// <summary><see cref="Fnv1a32(uint, ReadOnlySpan{byte})"/> over the UTF-8 bytes of <paramref name="value"/>; null hashes as
    /// the empty string (pass it typed, <c>Fnv1a32(seed, (string?)null)</c>: a bare <c>null</c> is ambiguous with the span overload).</summary>
    // C++: TownMatchmakingPolicy.h:116-120
    public static uint Fnv1a32(uint seed, string? value) => Fnv1a32(seed, Encoding.UTF8.GetBytes(value ?? ""));

    /// <summary>Plain FNV-1a (seed 0): TownLimbShopPolicy.h:65-70, TownMatchmakingRuntime.cpp:168-170. Null = empty, passed as
    /// <c>(string?)null</c> (a bare <c>null</c> is ambiguous with the span overload).</summary>
    // C++: TownMatchmakingPolicy.h:116-120 with seed 0 (== TownLimbShopPolicy.h:65-70)
    public static uint Fnv1a32(string? value) => Fnv1a32(0u, value);

    /// <summary>Plain FNV-1a over raw bytes (seed 0).</summary>
    // C++: TownMatchmakingPolicy.h:116-120 with seed 0
    public static uint Fnv1a32(ReadOnlySpan<byte> value) => Fnv1a32(0u, value);

    /// <summary>One LCG step: <c>seed * 1664525 + 1013904223</c>.</summary>
    // C++: TownChallengePolicy.h:161 (and TownDiagnosticRun.cpp:65)
    public static uint Lcg(uint seed) => unchecked(seed * LcgMultiplier + LcgIncrement);

    /// <summary>`TownChallengePolicy::Draw`: advances the stored state one LCG step and returns it (call as
    /// <c>Hashing.LcgNext(ref card.Seed)</c>). <c>ref</c> needs a variable or a FIELD: a port of the C++ Card must keep
    /// <c>seed</c> a field, not a property (a property cannot be passed by ref and would not compile).</summary>
    // C++: TownChallengePolicy.h:159-163
    public static uint LcgNext(ref uint state)
    {
        state = Lcg(state);
        return state;
    }

    /// <summary>The challenge preview's seed scramble: <c>x ^= x &gt;&gt; 16; x *= 0x85ebca6b; x ^= x &gt;&gt; 13</c>.</summary>
    // C++: TownChallengePolicy.h:344-346 (identical text at TownChallengePolicy.h:403)
    public static uint ChallengeMix(uint seed)
    {
        unchecked
        {
            seed ^= seed >> 16;
            seed *= 0x85ebca6bu;
            seed ^= seed >> 13;
            return seed;
        }
    }

    /// <summary>`TownLimbShopPolicy::Mix`: FNV-1a steps over the four bytes of <paramref name="value"/>, low byte first.</summary>
    // C++: TownLimbShopPolicy.h:52-61
    public static uint LimbShopMix(uint hash, uint value)
    {
        unchecked
        {
            for (int b = 0; b < 4; ++b)
            {
                hash ^= value & 0xffu;
                hash *= FnvPrime;
                value >>= 8;
            }
            return hash;
        }
    }

    /// <summary>`TownLimbShopPolicy::Seed` over raw identity bytes.</summary>
    // C++: TownLimbShopPolicy.h:63-74
    public static uint LimbShopSeed(ReadOnlySpan<byte> identity, int matches, uint limbMask)
    {
        unchecked
        {
            uint hash = FnvOffsetBasis;
            for (int i = 0; i < identity.Length; ++i)
            {
                hash ^= identity[i];
                hash *= FnvPrime;
            }
            hash = LimbShopMix(hash, (uint)(matches < 0 ? 0 : matches));
            hash = LimbShopMix(hash, limbMask & 15u);
            return hash != 0 ? hash : ZeroSeedFix;
        }
    }

    /// <summary>`TownLimbShopPolicy::Seed` over the UTF-8 bytes of <paramref name="identity"/>; null = empty, passed
    /// as <c>(string?)null</c> (a bare <c>null</c> is ambiguous with the span overload).</summary>
    // C++: TownLimbShopPolicy.h:63-74
    public static uint LimbShopSeed(string? identity, int matches, uint limbMask) =>
        LimbShopSeed(Encoding.UTF8.GetBytes(identity ?? ""), matches, limbMask);

    /// <summary>The C runtime's <c>rand()</c> (Microsoft CRT): <c>holdrand = holdrand * 214013 + 2531011; return (holdrand &gt;&gt; 16) &amp;
    /// 0x7fff</c>, 0..32767 (RAND_MAX 0x7fff). <see cref="CRandSeed"/> is the state of a thread that never called <c>srand</c>. The original's one
    /// <c>rand()</c> (PrisonerUtil.cpp:765, the prisoner's release line; glue, task 6.2) shares the runtime's per-thread state with anything else on
    /// that thread; the port keeps one state of its own (docs/divergences.md "PrisonerUtil (task 6.2").</summary>
    public static int CRand(ref uint holdrand)
    {
        unchecked
        {
            holdrand = holdrand * 214013u + 2531011u;
            return (int)((holdrand >> 16) & 0x7fffu);
        }
    }

    /// <summary>The CRT's <c>rand()</c> state before any <c>srand</c> call (1).</summary>
    public const uint CRandSeed = 1u;
}
