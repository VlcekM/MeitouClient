using System.Numerics;
using System.Text;

namespace ProvingGrounds.Core;

/// <summary>printf of a double exactly as the shipped original prints it: msvcr100.dll 10.00.40219.325 (VS2010 CRT, x64), the runtime
/// ProvingGrounds.dll imports <c>sprintf</c> / <c>sprintf_s</c> / <c>vsprintf_s</c> from, and that msvcp100's
/// <c>ostream &lt;&lt; double</c> calls (<c>setprecision(n)</c> = <c>%.ng</c>, <c>fixed</c> = <c>%.nf</c>, default precision 6).
/// <see cref="G"/> = <c>%.*g</c>, <see cref="F"/> = <c>%.*f</c>, <see cref="E"/> = <c>%.*e</c>.
///
/// How it differs from glibc (measured 2026-11-11 against the DLL, probe <c>MeitouMods/_crtprobe</c>): (1) exponents have THREE digits
/// (<c>1e+010</c>; nothing in the game process calls <c>_set_output_format</c>); (2) digits past the 17th significant digit print as
/// <c>0</c>; (3) an exact tie at the asked precision rounds half away from zero (<c>%.2f</c> 1.125 -> <c>1.13</c>, <c>%.0f</c> 2.5 ->
/// <c>3</c>); (4) a tie at the 17th digit is decided by the 80-bit arithmetic below; (5) a value just under a tie can be pre-rounded to
/// 17 digits and then rounded up again (double rounding). Integers (<c>%d %u %ld %x</c>) print as everywhere; <c>long</c> is 32-bit.
///
/// The model, from the disassembly of msvcr100's <c>$I10_OUTPUT</c> (RVA 0x7f558, called by <c>_fltout2</c> with 17 digits) and its
/// power-of-ten tables <c>_pow10pos</c> / <c>_pow10neg</c> (.data RVA 0xb3ca0, 29 + 29 entries of 12 bytes, the second block right after
/// the first):
/// <list type="number">
/// <item>the double is widened to an x87 80-bit value held in a 12-byte buffer (16-bit sign/exponent + 64-bit mantissa);</item>
/// <item><c>nten = ((expn*0x4D10 + ((expn&gt;&gt;8) + 2*(manhi&gt;&gt;24))*0x4D) - 0x134312F4) &gt;&gt; 16</c>; the value is multiplied by
/// 10^-nten with <c>__ld12mul</c> (16x16-bit partial products, the low ones dropped, round half even at 80 bits) using the tables, one
/// multiply per base-8 digit of nten (10^(n%8), 10^(8*..), 10^(64*..)), then once more by 0.1 (0xCCCC..CC, exponent 0x3FFB) when the
/// result is still &gt;= 1;</item>
/// <item>18 decimal digits are produced exactly from the 80-bit result, the 18th rounds the first 17 half up (&gt;= '5');</item>
/// <item>%e / %f / %g round that 17-digit string half up at the asked precision and print '0' past the 17th digit.</item>
/// </list>
/// Non-finite values: the digit string is <c>1#INF</c>, <c>1#IND</c> (the x87 "indefinite" NaN, sign set, quiet, no payload: .NET's
/// <c>double.NaN</c>), <c>1#QNAN</c> or <c>1#SNAN</c> with exponent 0, and goes through the same rounding character by character, which
/// gives the CRT's spellings <c>1.#INF</c>, <c>-1.#IND</c>, <c>1.#INF00</c> (%.6f), <c>1.#J</c> (%.2f: 'I' rounded up by the 'N' after it),
/// <c>1.$</c> (%.1f), <c>1</c> (%.0f), <c>1.#QNB</c> (%.4f). Pinned against the DLL by Tests/Core/Msvcr100FormatDllTests.cs.
///
/// Translated from the probe's standalone emulation (0 mismatches over ~580,000 doubles x 9 formats); the tables are data read from the
/// DLL for interoperability (NOTICE.md), no Microsoft code is copied.</summary>
public static class Msvcr100Format
{
    /// <summary>3, the CRT default; 2 only if the process called <c>_set_output_format(_TWO_DIGIT_EXPONENT)</c>, which the game never does.</summary>
    public const int ExponentDigits = 3;

    /// <summary>Largest precision the CRT honours: a bigger one prints as 512 (measured: %.513f and %.1000f of 0.1 print 512 decimals,
    /// %.1000e 512 digits). For %g the cap cannot change the text (a double's decimal exponent is at most 308, so a precision of 512 or
    /// more always means fixed form with every significant digit already shown); it is applied the same way.</summary>
    public const int MaxPrecision = 512;

    /// <summary><c>printf("%.*e", precision, v)</c>; a negative precision is taken as omitted (6), as C does, and capped at 512.</summary>
    public static string E(double v, int precision)
    {
        if (precision < 0) precision = 6;
        if (precision > MaxPrecision) precision = MaxPrecision;
        return Sign(v) + EBody(Digits(v), precision + 1, precision);
    }

    /// <summary><c>printf("%.*g", precision, v)</c>: 0 counts as 1, a negative precision is taken as omitted (6), capped at 512.</summary>
    public static string G(double v, int precision)
    {
        int p = precision < 0 ? 6 : precision == 0 ? 1 : precision;
        if (p > MaxPrecision) p = MaxPrecision;
        if (v == 0) return Sign(v) + "0";
        var (d0, x0) = Digits(v);
        var (d, x) = RoundTo(d0!, x0, p);
        if (x < -4 || x >= p)
        {
            string fr = d.Substring(1).TrimEnd('0');
            return Sign(v) + d[0] + (fr.Length > 0 ? "." + fr : "") + "e" + Exp(x);
        }
        string ip, f;
        if (x >= 0) { ip = d.Substring(0, x + 1); f = d.Substring(x + 1); } else { ip = "0"; f = new string('0', -x - 1) + d; }
        f = f.TrimEnd('0');
        return Sign(v) + ip + (f.Length > 0 ? "." + f : "");
    }

    /// <summary><c>printf("%.*f", decimals, v)</c>; a negative precision is taken as omitted (6), capped at 512.</summary>
    public static string F(double v, int decimals)
    {
        if (decimals < 0) decimals = 6;
        if (decimals > MaxPrecision) decimals = MaxPrecision;
        if (v == 0) return Sign(v) + "0" + (decimals > 0 ? "." + new string('0', decimals) : "");
        var (s, x) = Digits(v);
        int k = x + 1 + decimals;
        string d; int xx;
        if (k > 0) (d, xx) = RoundTo(s!, x, k);
        else if (k == 0 && s![0] >= '5') { d = "1"; xx = x + 1; }
        else return Sign(v) + "0" + (decimals > 0 ? "." + new string('0', decimals) : "");
        int top = Math.Max(xx, 0);
        var sb = new StringBuilder();
        for (int pos = top; pos >= -decimals; pos--) { int i = xx - pos; sb.Append(i >= 0 && i < d.Length ? d[i] : '0'); }
        string all = sb.ToString();
        return Sign(v) + all.Substring(0, top + 1) + (decimals > 0 ? "." + all.Substring(top + 1) : "");
    }

    static string Sign(double v) => double.IsNegative(v) ? "-" : "";
    static string Exp(int x) => (x < 0 ? "-" : "+") + Math.Abs(x).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(ExponentDigits, '0');

    static string EBody((string? d, int x) r, int digits, int p)
    {
        string d; int x;
        if (r.d == null) { d = new string('0', digits); x = 0; } else (d, x) = RoundTo(r.d, r.x, digits);
        return d[0] + (p > 0 ? "." + d.Substring(1) : "") + "e" + Exp(x);
    }

    // Round the digit string to k digits, half up on the next character ('5' or more), and pad with '0' past its end. Works on the
    // non-finite strings too: a character is "incremented" like a digit ('I' -> 'J', '#' -> '$'), as the CRT does.
    static (string d, int x) RoundTo(string s, int x, int k)
    {
        if (k >= s.Length) return (s + new string('0', k - s.Length), x);
        var r = s.Substring(0, k).ToCharArray();
        if (s[k] >= '5')
        {
            int i = k - 1;
            while (i >= 0 && r[i] == '9') { r[i] = '0'; i--; }
            if (i < 0) return ("1" + new string(r, 0, k - 1), x + 1);
            r[i]++;
        }
        return (new string(r), x);
    }

    /// <summary>The 17 significant digits of |v| that <c>_fltout2</c> gets from <c>$I10_OUTPUT(ld, 17)</c>, and the decimal exponent of the
    /// first; <c>(null, 0)</c> for zero; the CRT's special strings (exponent 0) for infinity and NaN.</summary>
    internal static (string? d, int x) Digits(double v)
    {
        if (double.IsInfinity(v)) return ("1#INF", 0);
        if (double.IsNaN(v))
        {
            ulong nanBits = (ulong)BitConverter.DoubleToInt64Bits(v) & 0xFFFFFFFFFFFFFUL;
            if ((nanBits & 0x8000000000000UL) == 0) return ("1#SNAN", 0);
            if (double.IsNegative(v) && nanBits == 0x8000000000000UL) return ("1#IND", 0);
            return ("1#QNAN", 0);
        }
        double a = Math.Abs(v);
        if (a == 0) return (null, 0);
        long bits = BitConverter.DoubleToInt64Bits(a); int e = (int)((bits >> 52) & 0x7FF); ulong m = (ulong)bits & 0xFFFFFFFFFFFFFUL;
        ulong man; int exp;
        if (e == 0) { int lz = BitOperations.LeadingZeroCount(m); man = m << lz; exp = 15372 - lz; }
        else { man = (m | (1UL << 52)) << 11; exp = e + 15360; }
        var xw = Words((UInt128)man << 16); int xe = exp;
        int nten = (short)(((((exp >> 8) + (int)(man >> 56) * 2) * 0x4d) + exp * 0x4d10 - 0x134312f4) >> 16);
        int pow = -nten; bool neg = pow < 0; pow = Math.Abs(pow); int group = 0;
        while (pow != 0)
        {
            int l3 = pow & 7; pow >>= 3;
            if (l3 != 0) { var (w, ex) = Entry(neg, group * 7 + l3 - 1); (xw, xe) = Mul(xw, xe, w, ex); }
            group++;
        }
        if (xe >= 0x3fff) { nten++; (xw, xe) = Mul(xw, xe, new ushort[] { 0xcccc, 0xcccc, 0xcccc, 0xcccc, 0xcccc }, 0x3ffb); }
        UInt128 frac = Man(xw) << 8; int shift = 0x3ffe - xe; if (shift > 0) frac >>= shift; else if (shift < 0) frac <<= -shift;
        UInt128 mask = ((UInt128)1 << 88) - 1;
        var d = new char[18];
        for (int i = 0; i < 18; i++) { frac *= 10; d[i] = (char)('0' + (int)(frac >> 88)); frac &= mask; }
        int x = nten - 1;
        if (d[17] >= '5')
        {
            int i = 16; while (i >= 0 && d[i] == '9') { d[i] = '0'; i--; }
            if (i < 0) { d[0] = '1'; x++; } else d[i]++;
        }
        return (new string(d, 0, 17), x);
    }

    static UInt128 Man(ushort[] w) { UInt128 m = 0; for (int i = 4; i >= 0; i--) m = (m << 16) | w[i]; return m; }
    static ushort[] Words(UInt128 m) { var w = new ushort[5]; for (int i = 0; i < 5; i++) { w[i] = (ushort)(m & 0xFFFF); m >>= 16; } return w; }

    // __ld12mul: 16x16 partial products of the two 80-bit mantissas (the low ones dropped), normalise, round half even at 80 bits.
    static (ushort[], int) Mul(ushort[] X, int ex, ushort[] Y, int ey)
    {
        var T = new ushort[8];
        for (int i = 0; i < 5; i++)
            for (int t = 0; t <= 4 - i; t++)
            {
                ulong s = ((ulong)T[i] | ((ulong)T[i + 1] << 16)) + (uint)X[i + t] * Y[4 - t];
                T[i] = (ushort)s; T[i + 1] = (ushort)(s >> 16);
                if ((s >> 32) != 0) T[i + 2]++;
            }
        int expsum = ex + ey - 0x3ffe;
        UInt128 tm = 0; for (int i = 5; i >= 0; i--) tm = (tm << 16) | T[i];
        UInt128 top = (UInt128)1 << 95, mask96 = ((UInt128)1 << 96) - 1;
        while (expsum > 0 && (tm & top) == 0) { tm = (tm << 1) & mask96; expsum--; }
        ushort w0 = (ushort)tm, w1 = (ushort)(tm >> 16);
        UInt128 hi = tm >> 16;
        if (w0 > 0x8000 || (w0 == 0x8000 && (w1 & 1) != 0)) { hi += 1; if (hi >> 80 != 0) { hi = (UInt128)1 << 79; expsum++; } }
        return (Words(hi), expsum);
    }

    // One 12-byte table entry as (mantissa words, exponent); an entry whose low word is >= 0x8000 is "unrounded" first, as the CRT does.
    static (ushort[], int) Entry(bool neg, int idx)
    {
        var b = new byte[12]; Array.Copy(TabBytes, (neg ? 29 : 0) * 12 + idx * 12, b, 0, 12);
        if (BitConverter.ToUInt16(b, 0) >= 0x8000) BitConverter.GetBytes(BitConverter.ToUInt32(b, 2) - 1).CopyTo(b, 2);
        var w = new ushort[5]; for (int i = 0; i < 5; i++) w[i] = BitConverter.ToUInt16(b, 2 * i);
        return (w, BitConverter.ToUInt16(b, 10));
    }

    // _pow10pos then _pow10neg, 29 entries of 12 bytes each, read from msvcr100.dll 10.00.40219.325 (.data RVA 0xb3ca0, VA 0x797e3ca0
    // at the DLL's preferred base 0x79730000).
    static readonly byte[] TabBytes = Convert.FromHexString(
        "0000000000000000" + "00a00240" + "0000000000000000" + "00c80540" + "0000000000000000" + "00fa0840" +
        "0000000000000000" + "409c0c40" + "0000000000000000" + "50c30f40" + "0000000000000000" + "24f41240" +
        "0000000000000080" + "96981640" + "0000000000000020" + "bcbe1940" + "000000000004bfc9" + "1b8e3440" +
        "000000a1edccce1b" + "c2d34e40" + "20f09eb5702ba8ad" + "c59d6940" + "d05dfd25e51a8e4f" + "19eb8340" +
        "7196d795430e058d" + "29af9e40" + "f9bfa044ed81128f" + "8182b940" + "bf3cd5a6cfff491f" + "78c2d340" +
        "6fc6e08ce980c947" + "ba93a841" + "bc856b5527398df7" + "70e07c42" + "bcdd8edef99dfbeb" + "7eaa5143" +
        "a1e676e3ccf2292f" + "84812644" + "281017aaf8ae10e3" + "c5c4fa44" + "eba7d4f3f7ebe14a" + "7a95cf45" +
        "65ccc7910ea6aea0" + "19e3a346" + "0d65170c75818675" + "76c9484d" + "5842e4a793393b35" + "b8b2ed53" +
        "4da7e55d3dc55d3b" + "8b9e925a" + "ff5da6f0a120c054" + "a58c3761" + "d1fd8b5a8bd8255d" + "89f9db67" +
        "aa95f8f327bfa2c8" + "5ddd806e" + "4cc99b97208a0252" + "60c42575" +
        // _pow10neg
        "cdcccdcccccccccc" + "ccccfb3f" + "713d0ad7a3703d0a" + "d7a3f83f" + "5a643bdf4f8d976e" + "1283f53f" +
        "c3d32c6519e25817" + "b7d1f13f" + "d00f2384471b47ac" + "c5a7ee3f" + "40a6b6696caf05bd" + "3786eb3f" +
        "333dbc427ae5d594" + "bfd6e73f" + "c2fdfdce61841177" + "ccabe43f" + "2f4c5be14dc4be94" + "95e6c93f" +
        "92c4533b7544cd14" + "be9aaf3f" + "de67ba943945ad1e" + "b1cf943f" + "2423c6e2bcba3b31" + "618b7a3f" +
        "615559c17eb1537c" + "12bb5f3f" + "d7ee2f8d06be9285" + "15fb443f" + "243fa5e939a527ea" + "7fa82a3f" +
        "7daca1e4bc647c46" + "d0dd553e" + "637b06cc23547783" + "ff91813d" + "91fa3a197a632543" + "31c0ac3c" +
        "2189d138824797b8" + "00fdd73b" + "dc8858081bb1e8e3" + "86a6033b" + "c684454207b69975" + "37db2e3a" +
        "33711cd223db32ee" + "49905a39" + "a687bec057daa582" + "a6a2b532" + "e268b211a7529f44" + "59b7102c" +
        "2549e42d36344f53" + "aece6b25" + "8f5904a4c0dec27d" + "fbe8c61e" + "9ee7885a57913cbf" + "50832218" +
        "4e4b6562fd838faf" + "06947d11" + "e42dde9fced2c804" + "dda6d80a");
}
