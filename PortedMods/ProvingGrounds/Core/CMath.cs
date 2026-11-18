namespace ProvingGrounds.Core;

/// <summary>The rounding the original writes out by hand (task 1.1), one helper per distinct pattern found in the PURE files by
/// grepping `floor(`, `floorf(`, `ceil(`, `ceilf(`, `round(`, `roundf(`, `lround`, `+ 0.5f`, `+ 0.5)`, `+ .5`. None of them is
/// banker's rounding: never Math.Round / MathF.Round with the default midpoint mode.
///
/// Not helpers (ported inline with their file): plain floors/ceilings that are not a rounding to nearest, i.e.
/// TownArenaPolicy.h:12,16,20 (`std::floor((hours - 6.0) / n)` epochs), PrisonerRecruitmentLogic.cpp:17 (`ceilf(scaled / step)`),
/// TownChallengeBuyInPolicy.h:35 (`std::floor(x + 1e-8)`); and the integer half-up `(fullCost + 1) / 2` at ArenaRewards.cpp:466
/// (`ReplaceCost`, ported with ArenaRewards). No pure file calls `round`/`roundf`/`lround`. Clamp: there is no shared
/// clamp in the C++ (RosterStatusPolicy.h:14 and others define their own local ones); each is ported with its file.
///
/// Float stays float and double stays double: the float helpers use MathF, the double ones Math, exactly like floorf/floor.
/// The int conversions are the C++ `static_cast&lt;int&gt;`, which truncates toward zero like the C# cast; every call site range-checks
/// first or is bounded (an out-of-range float->int is undefined in C++ and saturates in .NET, so the two must never be reached).</summary>
public static class CMath
{
    /// <summary><c>floorf(x + 0.5f)</c> (the F names the float precision, so an int argument cannot silently pick it): half up toward +infinity, in float (1.5 -> 2, 2.5 -> 3, -0.5 -> 0, -1.5 -> -1).</summary>
    // C++: ArenaMarks.cpp:101 (positive branch), ArenaMarks.cpp:203
    public static float RoundHalfUpF(float x) => MathF.Floor(x + 0.5f);

    /// <summary><c>floor(x + 0.5)</c>: half up toward +infinity, in double.</summary>
    // C++: ArenaMarks.cpp:130, ArenaMarks.cpp:132, TownArenaPolicy.h:28, TownChallengeBuyInPolicy.h:33 (written `+ .5`)
    public static double RoundHalfUp(double x) => Math.Floor(x + 0.5);

    /// <summary><c>x &gt;= 0.0f ? floorf(x + 0.5f) : ceilf(x - 0.5f)</c>: half away from zero, in float (-0.5 -> -1, -1.5 -> -2).
    /// Note the test is <c>&gt;=</c>: a NaN takes the ceilf branch and stays NaN.</summary>
    // C++: ArenaMarks.cpp:100-102
    public static float RoundHalfAwayFromZero(float x) => x >= 0.0f ? MathF.Floor(x + 0.5f) : MathF.Ceiling(x - 0.5f);

    /// <summary><c>static_cast&lt;int&gt;(x + 0.5)</c>: add a half then truncate toward zero, in double. Equals half-up for
    /// x &gt;= -0.5 only (the call sites pass positive values); -1.5 gives -1 and -2.5 gives -2 (truncation, not floor).
    /// Range: PGConfig.cpp:938-941 checks <c>scaled</c> against INT_MAX and &lt;= 0 before :942. PGConfig.cpp:1108 has no check of its
    /// own: it is bounded indirectly, <c>g_bookie.maximum</c> &lt;= kMaxEditableCost (1,000,000, PGConfig.cpp:24/225) times
    /// kBookOddsCeiling (5.0, PGConfig.cpp:29) = at most 5,000,000.</summary>
    // C++: PGConfig.cpp:942, PGConfig.cpp:1108
    public static int TruncatePlusHalf(double x) => (int)(x + 0.5);

    /// <summary>C <c>printf("%g", value)</c> (precision 6) as the shipped original prints it: msvcr100's <c>%g</c>
    /// (<see cref="Msvcr100Format.G"/>: three-digit exponent <c>1e+010</c>, 17 significant digits then zeros, ties half away from zero).
    /// Under <see cref="UseGlibcForTest"/> it is glibc's <c>%g</c> instead (the golden vectors were printed by g++/glibc).</summary>
    // C++: PGConfig.cpp:468 (FormatNumber), :144 ("expected %g to %g"), :867-871 (the load summary), ArenaSnapshotJson.cpp:143-147 (`s << hi`)
    public static string FormatG(double value) => FormatG(value, 6);

    /// <summary>C <c>printf("%.*g", precision, value)</c>: as <see cref="FormatG(double)"/> with <paramref name="precision"/> significant
    /// digits (C treats 0 as 1 and a negative precision as omitted, 6). <c>precision</c> 17 is msvcp100's <c>ostream &lt;&lt; std::setprecision(17)</c> on a double in the default
    /// float format (TownChallengePolicy.h:261-263), 10 is CombatBalanceLog.cpp:46-49.</summary>
    // C++: TownChallengePolicy.h:261-263 (EncodeLegacy's `out << std::setprecision(17) << ... << card.skarnLastEnd`), CombatBalanceLog.cpp:46-49
    public static string FormatG(double value, int precision) =>
        s_glibc.Value ? GlibcFormatG(value, precision) : Msvcr100Format.G(value, precision);

    /// <summary>C <c>printf("%.*f", decimals, value)</c> (and msvcp100's <c>std::fixed &lt;&lt; std::setprecision(decimals)</c>, which calls
    /// it) as the shipped original prints it: msvcr100's <c>%f</c> (<see cref="Msvcr100Format.F"/>): the 17-digit value rounded half away
    /// from zero (<c>0.125</c> at 2 -> "0.13", <c>2.5</c> at 0 -> "3"), digits past the 17th significant digit print as 0, <c>-0</c> and a
    /// negative value that rounds to zero keep the sign ("-0"). A negative <paramref name="decimals"/> is taken as omitted (6).
    /// Under <see cref="UseGlibcForTest"/> it is glibc's <c>%f</c> (exact value, half to even), which the golden vectors hold.</summary>
    // C++: ArenaCombatProfile.cpp:104-113 (%.0f via iostream: setprecision at :104, the Append calls :105-113), TownBookie.cpp:125 (%.0f),
    // :174 (%.2f), BalanceTuning.cpp:40-58 (%.4f/%.3f/%.1f), TownDiagnosticRun.cpp:66-67 (%.6f), :137-145 (%.1f), TownArena.cpp (%.1f/%.2f/%.3f)
    public static string FormatF(double value, int decimals)
    {
        if (decimals < 0) decimals = 6;   // C: a negative precision is taken as omitted
        return s_glibc.Value ? GlibcFormatF(value, decimals) : Msvcr100Format.F(value, decimals);
    }

    // Test seam (no C++ counterpart): golden vectors were printed by g++/glibc, so golden tests switch the flow they run in to glibc's
    // printf and keep comparing byte for byte. Production never sets it.
    static readonly AsyncLocal<bool> s_glibc = new AsyncLocal<bool>();

    /// <summary>Test hook: <see cref="FormatG(double, int)"/> and <see cref="FormatF"/> print as glibc in the calling flow until the result
    /// is disposed, which restores what was there before. Golden tests only; hand tests that state the original's text keep msvcr100.</summary>
    internal static IDisposable UseGlibcForTest()
    {
        bool previous = s_glibc.Value;
        s_glibc.Value = true;
        return new RestoreGlibc(previous);
    }

    sealed class RestoreGlibc : IDisposable
    {
        readonly bool _previous;
        bool _done;
        public RestoreGlibc(bool previous) { _previous = previous; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s_glibc.Value = _previous;
        }
    }

    // glibc %.*g (test mode only): the exact binary value rounded half to even at P significant digits; exponent form (two-digit minimum
    // exponent) when the decimal exponent is below -4 or at least P, else fixed; trailing zeros and a bare point removed; -0 keeps its sign;
    // NaN/infinity as glibc ("nan", "-inf"). Pinned by Tests/Golden/pgconfig_text.json and challenge.json.
    static string GlibcFormatG(double value, int precision)
    {
        int P = precision < 0 ? 6 : precision == 0 ? 1 : precision;   // C: 0 counts as 1, a negative precision is taken as omitted
        if (double.IsNaN(value)) return double.IsNegative(value) ? "-nan" : "nan";
        string sign = double.IsNegative(value) ? "-" : "";
        if (double.IsInfinity(value)) return sign + "inf";
        double a = Math.Abs(value);
        string digits;
        int x;
        if (a == 0.0)
        {
            digits = new string('0', P);
            x = 0;
        }
        else
        {
            // exact value a = mantissa * 2^e2
            long bits = BitConverter.DoubleToInt64Bits(a);
            int rawExp = (int)((bits >> 52) & 0x7FF);
            long mantissa = bits & 0xFFFFFFFFFFFFFL;
            int e2;
            if (rawExp == 0) e2 = -1074;
            else { mantissa |= 1L << 52; e2 = rawExp - 1075; }
            System.Numerics.BigInteger num = mantissa, den = 1;
            if (e2 >= 0) num <<= e2; else den <<= -e2;
            // decimal exponent x: 10^x <= a < 10^(x+1)
            x = (int)Math.Floor(Math.Log10(a));
            while (Compare(num, den, x) < 0) --x;
            while (Compare(num, den, x + 1) >= 0) ++x;
            System.Numerics.BigInteger q = RoundHalfEven(num, den, P - 1 - x);
            if (q == System.Numerics.BigInteger.Pow(10, P)) { q /= 10; ++x; }
            digits = q.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        string body;
        if (x < -4 || x >= P)
        {
            string frac = digits.Substring(1).TrimEnd('0');
            string exp = Math.Abs(x).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(2, '0');
            body = digits.Substring(0, 1) + (frac.Length > 0 ? "." + frac : "") + "e" + (x < 0 ? "-" : "+") + exp;
        }
        else
        {
            string intPart, frac;
            if (x >= 0) { intPart = digits.Substring(0, x + 1); frac = digits.Substring(x + 1); }
            else { intPart = "0"; frac = new string('0', -x - 1) + digits; }
            frac = frac.TrimEnd('0');
            body = intPart + (frac.Length > 0 ? "." + frac : "");
        }
        return sign + body;
    }

    // glibc %.*f (test mode only): the exact binary value rounded half to even; NaN/infinity as glibc. Pinned by Tests/Golden/gluetext.json.
    static string GlibcFormatF(double value, int decimals)
    {
        if (double.IsNaN(value)) return double.IsNegative(value) ? "-nan" : "nan";
        string sign = double.IsNegative(value) ? "-" : "";
        if (double.IsInfinity(value)) return sign + "inf";
        double a = Math.Abs(value);
        System.Numerics.BigInteger q = 0;
        if (a != 0.0)
        {
            long bits = BitConverter.DoubleToInt64Bits(a);
            int rawExp = (int)((bits >> 52) & 0x7FF);
            long mantissa = bits & 0xFFFFFFFFFFFFFL;
            int e2;
            if (rawExp == 0) e2 = -1074;
            else { mantissa |= 1L << 52; e2 = rawExp - 1075; }
            System.Numerics.BigInteger num = mantissa, den = 1;
            if (e2 >= 0) num <<= e2; else den <<= -e2;
            q = RoundHalfEven(num, den, decimals);
        }
        string digits = q.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(decimals + 1, '0');
        if (decimals == 0) return sign + digits;
        return sign + digits.Substring(0, digits.Length - decimals) + "." + digits.Substring(digits.Length - decimals);
    }

    // sign of num/den - 10^p
    static int Compare(System.Numerics.BigInteger num, System.Numerics.BigInteger den, int p)
    {
        if (p >= 0) return num.CompareTo(den * System.Numerics.BigInteger.Pow(10, p));
        return (num * System.Numerics.BigInteger.Pow(10, -p)).CompareTo(den);
    }

    // round(num/den * 10^k), half to even
    static System.Numerics.BigInteger RoundHalfEven(System.Numerics.BigInteger num, System.Numerics.BigInteger den, int k)
    {
        if (k >= 0) num *= System.Numerics.BigInteger.Pow(10, k); else den *= System.Numerics.BigInteger.Pow(10, -k);
        System.Numerics.BigInteger q = System.Numerics.BigInteger.DivRem(num, den, out System.Numerics.BigInteger rem);
        int c = (rem * 2).CompareTo(den);
        if (c > 0 || (c == 0 && !q.IsEven)) ++q;
        return q;
    }
}
