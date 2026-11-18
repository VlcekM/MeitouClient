// Port of upstream/ProvingGrounds/plugin/src/TownLimbShopPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: TownLimbShopPolicy.h (header-only, namespace <c>TownLimbShopPolicy</c>): the after-bout robot-limb shop (TW §2.10):
/// the grade a fighter's replacement limbs get (from a seed made of the fighter's identity, town match count and missing-limb
/// mask), the item quality per grade, the trader's and fighter's lines, and the fighter's approach-stall tracker.
/// <see cref="Price"/> is the shipped price table; it has no caller in the original (no money changes hands) and is ported anyway.
/// <c>unsigned</c> is 32 bits: <c>uint</c>, all arithmetic wraps.</summary>
public static class TownLimbShopPolicy
{
    /// <summary>C++: TownLimbShopPolicy.h:9-17</summary>
    public enum Grade
    {
        Shoddy = 0,
        Standard,
        High,
        Specialist,
        Masterwork,
        GradeCount
    }

    /// <summary>C++: TownLimbShopPolicy.h:19-25</summary>
    public enum OrderKind
    {
        ArmOrder = 0,
        LegOrder,
        MultipleOrder,
        OrderKindCount
    }

    /// <summary>Division 0 or less (Easy, and unknown fighters): 70 / 25 / 5 % shoddy / standard / high; division 1: 15 / 75 / 10 %
    /// standard / high / specialist; 2 and above: 10 / 80 / 10 % high / specialist / masterwork; legendary: 80 / 20 % specialist /
    /// masterwork. The roll is taken modulo 100 (unsigned).</summary>
    /// <remarks>The glue's queue step (TownLimbShop.cpp:418-431) the C# glue must copy: division = the catalog tier when
    /// TownFighterCatalog.Find(role) has the role, else TownChallengePolicy.NamedRoleDivision(role ?? "") when that is &gt;= 0, else 0;
    /// legendary only when the role is NOT in the catalog and its named division is 2; identity =
    /// TownMatchmakingRuntime::Identity(fighter), or the fighter handle's toString() when that is empty; seed =
    /// Seed(identity, town match count, limb mask); grade = GradeFor(division, legendary, seed); variant = (seed &gt;&gt; 8) % 3u;
    /// the lines are TraderLine / FighterLine(grade, variant, (int)KindFor(mask)) (the C++ passes the enum as an int).
    /// The reference copy is LimbShopGoldenTests.Fighter_class_orders.</remarks>
    // C++: TownLimbShopPolicy.h:27-36
    public static Grade GradeFor(int division, bool legendary, uint roll)
    {
        roll %= 100u;
        if (legendary) return roll < 80u ? Grade.Specialist : Grade.Masterwork;
        if (division <= 0)
            return roll < 70u ? Grade.Shoddy : roll < 95u ? Grade.Standard : Grade.High;
        if (division == 1)
            return roll < 15u ? Grade.Standard : roll < 90u ? Grade.High : Grade.Specialist;
        return roll < 10u ? Grade.High : roll < 90u ? Grade.Specialist : Grade.Masterwork;
    }

    static readonly int[] QualityValues = { 20, 40, 60, 80, 95 };

    /// <summary>Out-of-range grades read as Shoddy.</summary>
    // C++: TownLimbShopPolicy.h:38-42
    public static int QualityLevel(Grade grade)
    {
        return grade >= Grade.Shoddy && grade < Grade.GradeCount ? QualityValues[(int)grade] : QualityValues[(int)Grade.Shoddy];
    }

    static readonly string[] GradeNames =
    {
        "shoddy", "standard", "high-grade", "specialist", "masterwork"
    };

    // C++: TownLimbShopPolicy.h:44-50
    public static string GradeName(Grade grade)
    {
        return grade >= Grade.Shoddy && grade < Grade.GradeCount ? GradeNames[(int)grade] : GradeNames[(int)Grade.Shoddy];
    }

    /// <summary>The same function as <see cref="Hashing.LimbShopMix"/> (task 1.1), which this calls.</summary>
    // C++: TownLimbShopPolicy.h:52-61
    public static uint Mix(uint hash, uint value) => Hashing.LimbShopMix(hash, value);

    /// <summary>The same function as <see cref="Hashing.LimbShopSeed(string?, int, uint)"/> (task 1.1), which this calls: FNV-1a
    /// of the identity's UTF-8 bytes, then Mix(matches clamped to &gt;= 0), Mix(limbMask &amp; 15); 0 becomes 0x9e3779b9.</summary>
    // C++: TownLimbShopPolicy.h:63-74
    public static uint Seed(string identity, int matches, uint limbMask) => Hashing.LimbShopSeed(identity, matches, limbMask);

    /// <summary>Over the raw bytes of the identity (the engine's narrow string), for a name that is not ASCII.</summary>
    // C++: TownLimbShopPolicy.h:63-74
    public static uint Seed(ReadOnlySpan<byte> identity, int matches, uint limbMask) => Hashing.LimbShopSeed(identity, matches, limbMask);

    static readonly int[] PerLimb = { 800, 2500, 6000, 12000, 25000 };

    /// <summary>No caller in the original (TW §2.10): ported as shipped. Saturates at int.MaxValue.</summary>
    // C++: TownLimbShopPolicy.h:76-83
    public static int Price(Grade grade, int limbCount)
    {
        int safeGrade = grade >= Grade.Shoddy && grade < Grade.GradeCount ? (int)grade : (int)Grade.Shoddy;
        if (limbCount <= 0) return 0;
        if (limbCount > int.MaxValue / PerLimb[safeGrade]) return int.MaxValue;
        return PerLimb[safeGrade] * limbCount;
    }

    /// <summary>Mask bits: 1 / 2 arms, 4 / 8 legs (higher bits count toward "several").</summary>
    // C++: TownLimbShopPolicy.h:85-91
    public static OrderKind KindFor(uint limbMask)
    {
        unchecked
        {
            bool arms = (limbMask & 3u) != 0;
            bool legs = (limbMask & 12u) != 0;
            bool several = limbMask != 0 && (limbMask & (limbMask - 1u)) != 0;
            return several || (arms && legs) ? OrderKind.MultipleOrder : legs ? OrderKind.LegOrder : OrderKind.ArmOrder;
        }
    }

    // C++: TownLimbShopPolicy.h:93-96
    public static string OrderName(OrderKind kind)
    {
        return kind == OrderKind.LegOrder ? "leg" : kind == OrderKind.MultipleOrder ? "set" : "arm";
    }

    /// <summary>The trader's quote formats, [grade][variant], <c>%s</c> = the order name. Player text, byte for byte.</summary>
    // C++: TownLimbShopPolicy.h:103-109
    // Do not mutate: shipped player text (internal only so tests can read it).
    internal static readonly string[][] Openings =
    {
        new[] { "It's ugly, but this shoddy %s will move.", "Budget %s. It clanks, it works.", "Cheapest %s in the shop. No promises on the paint." },
        new[] { "Standard %s. Reliable enough for the pit.", "A solid standard %s, tested this morning.", "Standard-grade %s. Nothing fancy, nothing loose." },
        new[] { "High-grade %s. Fast response and reinforced joints.", "This high-grade %s can take a beating.", "High-grade %s. Better than what most fighters can afford." },
        new[] { "Specialist %s. Balanced servos, reinforced fittings. Not cheap.", "Specialist-grade %s. Arena quality, through and through.", "A specialist %s. Keep it oiled and it'll outlast you." },
        new[] { "Masterwork %s. Best piece in Scratch.", "Masterwork %s. Perfect calibration, not a fraction of play.", "This masterwork %s belongs on a champion." }
    };

    /// <summary>The fighter's replies, [grade][variant]; some have no <c>%s</c> (the extra printf argument is ignored).</summary>
    // C++: TownLimbShopPolicy.h:120-126
    // Do not mutate: shipped player text (internal only so tests can read it).
    internal static readonly string[][] Replies =
    {
        new[] { "Cheap gets me back in the ring.", "If it bends, I'll bend it back.", "Bolt it on. I've fought with worse." },
        new[] { "Standard is fine. My hits do the talking.", "Good. I just need it ready for the next bout.", "That'll do. Fit the %s and make it tight." },
        new[] { "Now that's an upgrade.", "High grade? The next opponent is in trouble.", "Fit it. I want to feel what this %s can do." },
        new[] { "Good. Cheap wasn't going to put me on top.", "Specialist? Now we're talking.", "With that %s, nobody in my division is safe." },
        new[] { "Oh man, this masterwork %s is gonna put me on top.", "Masterwork. Worth every Cat.", "Fit it now. I want the whole arena to see it." }
    };

    /// <summary>The C++ prints into <c>char text[192]</c> with <c>sprintf_s</c>; every format has at most one <c>%s</c> and no other
    /// conversion, and the longest result (64 characters) is far below 192, so neither MSVC's overflow error nor a truncation can
    /// happen (TownLimbShopPolicyTests pins both facts). A plain replacement gives the same text.</summary>
    static string FormatItem(string format, string item) => format.Replace("%s", item, StringComparison.Ordinal);

    /// <summary>Out-of-range kind values read as an arm, grades as Shoddy; <paramref name="variant"/> % 3 (unsigned).</summary>
    // C++: TownLimbShopPolicy.h:98-113
    public static string TraderLine(Grade grade, uint variant, int kindValue)
    {
        OrderKind kind = kindValue >= (int)OrderKind.ArmOrder && kindValue < (int)OrderKind.OrderKindCount ?
            (OrderKind)kindValue : OrderKind.ArmOrder;
        string item = OrderName(kind);
        return FormatItem(Openings[grade >= Grade.Shoddy && grade < Grade.GradeCount ? (int)grade : (int)Grade.Shoddy][variant % 3u], item);
    }

    // C++: TownLimbShopPolicy.h:115-131
    public static string FighterLine(Grade grade, uint variant, int kindValue)
    {
        OrderKind kind = kindValue >= (int)OrderKind.ArmOrder && kindValue < (int)OrderKind.OrderKindCount ?
            (OrderKind)kindValue : OrderKind.ArmOrder;
        string item = OrderName(kind);
        string format = Replies[grade >= Grade.Shoddy && grade < Grade.GradeCount ? (int)grade : (int)Grade.Shoddy][variant % 3u];
        return FormatItem(format, item);
    }

    /// <summary>C++: TownLimbShopPolicy.h:133-139. A value type with the C++ constructor (0, 1e30, 0).
    /// <para>WARNING: <c>default(ApproachProgress)</c> (and a fresh array element) has BestDistance 0, NOT the C++ default 1e30:
    /// use <c>new ApproachProgress()</c>.</para></summary>
    public struct ApproachProgress
    {
        public float Elapsed;
        public float BestDistance;
        public float Stalled;

        // C++: TownLimbShopPolicy.h:138
        public ApproachProgress() { Elapsed = 0.0f; BestDistance = 1.0e30f; Stalled = 0.0f; }
    }

    // C++: TownLimbShopPolicy.h:141-146
    public static void ResetApproach(ref ApproachProgress progress, float distance)
    {
        progress.Elapsed = 0.0f;
        progress.BestDistance = distance;
        progress.Stalled = 0.0f;
    }

    /// <summary><paramref name="dt"/> is clamped to 0..1; progress means more than 1 unit nearer than the best so far.</summary>
    // C++: TownLimbShopPolicy.h:148-159
    public static void UpdateApproach(ref ApproachProgress progress, float distance, float dt)
    {
        if (dt < 0.0f) dt = 0.0f;
        if (dt > 1.0f) dt = 1.0f;
        progress.Elapsed += dt;
        if (distance + 1.0f < progress.BestDistance)
        {
            progress.BestDistance = distance;
            progress.Stalled = 0.0f;
        }
        else progress.Stalled += dt;
    }

    // C++: TownLimbShopPolicy.h:161-164
    public static bool ApproachStalled(in ApproachProgress progress)
    {
        return progress.Stalled >= 20.0f;
    }
}
