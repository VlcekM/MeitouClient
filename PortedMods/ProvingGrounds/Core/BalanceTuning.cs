// Port of upstream/ProvingGrounds/plugin/src/BalanceTuning.h and BalanceTuning.cpp (Proving Grounds by Shutori, GPLv3).
using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>One home for the experimentally tunable balance constants. C++: BalanceTuning.h:1-197, BalanceTuning.cpp:1-59 (PA §2.14,
/// TW §2.15). Compiled values, not configurable: <see cref="Get"/> returns the one mutable instance, built from the shipped values.
/// <para>The lever-by-lever rationale lives in BalanceTuning.h's comments (kept short here). Note the header comment still says ring
/// 150/160/140 (BalanceTuning.h:171); the .cpp values (145/147.5/142.5) are what ships and what this port copies.</para></summary>
public static class BalanceTuning
{
    /// <summary>C++: BalanceTuning.h:30-190 (<c>struct Values</c>). A class: the C++ hands out a reference to one mutable global
    /// (<c>Values&amp; Get()</c>), so every reader sees a change.</summary>
    public sealed class Values
    {
        /// <summary>Ability -> power exponent in TownMatchmakingPolicy::FighterPower. Shipped 2.5.</summary>
        public double AbilityExponent;
        /// <summary>MMR correction strength (the "full" model). Shipped 1.0.</summary>
        public double RatingInfluence;
        /// <summary>R1: conserve total rating per bout (MmrRating.ApplyMatch). Shipped true.</summary>
        public bool ZeroSumRating;
        /// <summary>R4: percent of a team that must be down before a team bout ends. 0 = a FULL team wipe.</summary>
        public int DownedPercent;
        /// <summary>R3: NPC bout announcement lead, in game hours: minimum plus up to the spread of hashed spread.</summary>
        public double NpcLeadHoursMin;
        public double NpcLeadHoursSpread;
        /// <summary>R2a: percent of ambient bouts authored as a deliberate mismatch. 0 = parity only.</summary>
        public int ShowcasePercent;
        /// <summary>Lever C, the ambient band. The spans are EXPLICIT literals, deliberately not derived as (cap - base): deriving
        /// them shifts the drawn target by one ULP on some draws (BalanceTuning.h:87-90).</summary>
        public double AmbientCloseCap;
        public double AmbientCloseSpan;
        public double AmbientRatioCap;
        public double AmbientUnderdogSpan;
        /// <summary>Lever B, the favourite-pricing transform (read only by TownBettingPolicy). pricingKnee 0 restores the shipped
        /// square. Not to be confused with <see cref="AbilityExponent"/>, which drives pairing selection.</summary>
        public double PricingExponent;
        public double PricingKnee;
        /// <summary>Ring guard (town bouts only), scaled off the arena building's origin. Setting ringNearRadius equal to
        /// ringOutRadius disables both corrections.</summary>
        public double RingNearRadius;
        public double RingOutRadius;
        public double RingReturnRadius;

        // C++: BalanceTuning.cpp:6-24
        public Values()
        {
            AbilityExponent = 2.5;
            RatingInfluence = 1.0;
            ZeroSumRating = true;
            DownedPercent = 0;
            NpcLeadHoursMin = 1.0;
            NpcLeadHoursSpread = 1.0;
            ShowcasePercent = 0;
            AmbientCloseCap = 1.10;
            AmbientCloseSpan = 0.10;
            AmbientRatioCap = 1.15;
            AmbientUnderdogSpan = 0.05;
            PricingExponent = 6.65;
            PricingKnee = 1.10;
            RingNearRadius = 145.0;
            RingOutRadius = 147.5;
            RingReturnRadius = 142.5;
        }
    }

    // C++: BalanceTuning.cpp:26-33 (namespace-scope g_values, built during static init)
    static readonly Values g_values = new Values();

    // Test seam (no C++ counterpart): an override visible only to the flow that set it, so tests running in parallel never see
    // each other's values. Production code never sets it.
    static readonly AsyncLocal<Values?> s_testOverride = new AsyncLocal<Values?>();

    // C++: BalanceTuning.cpp:35-38
    public static Values Get()
    {
        return s_testOverride.Value ?? g_values;
    }

    /// <summary>Test hook: <see cref="Get"/> returns <paramref name="values"/> in the calling flow until the result is disposed, which
    /// restores what was there before. Tests must never assign through <see cref="Get"/>: without an override it is the real global,
    /// as in C++ <c>Values&amp; Get()</c>. Nested overrides must be disposed in reverse order (use <c>using</c>).</summary>
    internal static IDisposable UseForTest(Values values)
    {
        var previous = s_testOverride.Value;
        s_testOverride.Value = values;
        return new Restore(previous);
    }

    sealed class Restore : IDisposable
    {
        readonly Values? _previous;
        bool _done;
        public Restore(Values? previous) { _previous = previous; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s_testOverride.Value = _previous;
        }
    }

    /// <summary>Compact JSON fragment WITHOUT enclosing braces, for the combat log (the C++ writes it into a caller buffer of at least
    /// 512 bytes; the text is shorter than that at any sane lever value, so the string here is the same text).</summary>
    // C++: BalanceTuning.cpp:40-58. %.4f / %.3f / %.1f (msvcr100 sprintf_s) -> CMath.FormatF, %d -> the int.
    public static string WriteJson()
    {
        Values v = Get();
        CultureInfo ci = CultureInfo.InvariantCulture;
        return
            "\"ability_exponent\":" + CMath.FormatF(v.AbilityExponent, 4) +
            ",\"rating_influence\":" + CMath.FormatF(v.RatingInfluence, 4) +
            ",\"zero_sum_rating\":" + (v.ZeroSumRating ? 1 : 0).ToString(ci) +
            ",\"downed_percent\":" + v.DownedPercent.ToString(ci) +
            ",\"npc_lead_min\":" + CMath.FormatF(v.NpcLeadHoursMin, 3) +
            ",\"npc_lead_spread\":" + CMath.FormatF(v.NpcLeadHoursSpread, 3) +
            ",\"showcase_percent\":" + v.ShowcasePercent.ToString(ci) +
            ",\"pricing_exponent\":" + CMath.FormatF(v.PricingExponent, 4) +
            ",\"pricing_knee\":" + CMath.FormatF(v.PricingKnee, 4) +
            ",\"ambient_close_cap\":" + CMath.FormatF(v.AmbientCloseCap, 4) +
            ",\"ambient_close_span\":" + CMath.FormatF(v.AmbientCloseSpan, 4) +
            ",\"ambient_ratio_cap\":" + CMath.FormatF(v.AmbientRatioCap, 4) +
            ",\"ambient_underdog_span\":" + CMath.FormatF(v.AmbientUnderdogSpan, 4) +
            ",\"ring_near_radius\":" + CMath.FormatF(v.RingNearRadius, 1) +
            ",\"ring_out_radius\":" + CMath.FormatF(v.RingOutRadius, 1) +
            ",\"ring_return_radius\":" + CMath.FormatF(v.RingReturnRadius, 1);
    }
}
