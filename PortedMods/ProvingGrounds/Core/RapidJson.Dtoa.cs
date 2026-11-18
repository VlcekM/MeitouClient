// Port of rapidjson v1.1.0 internal/diyfp.h, internal/dtoa.h (Grisu2) (MIT, see NOTICE.md); part of Core/RapidJson.cs (its header says what is ported and why).
using System.Globalization;

namespace ProvingGrounds.Core;

internal static partial class RapidJson
{
    // ---- internal/diyfp.h, internal/dtoa.h: Grisu2 ------------------------------------------------------------------------------

    // C++: diyfp.h:43-160 DiyFp
    readonly struct DiyFp
    {
        public readonly ulong F;
        public readonly int E;

        public DiyFp(ulong fp, int exp) { F = fp; E = exp; }

        const int DiySignificandSize = 64;
        const int DpSignificandSize = 52;
        const int DpExponentBias = 0x3FF + DpSignificandSize;
        const int DpMinExponent = -DpExponentBias;
        const ulong DpExponentMask = 0x7FF0000000000000UL;
        const ulong DpSignificandMask = 0x000FFFFFFFFFFFFFUL;
        public const ulong DpHiddenBit = 0x0010000000000000UL;

        // C++: diyfp.h:48-63 DiyFp(double)
        public static DiyFp FromDouble(double d)
        {
            ulong u64 = (ulong)BitConverter.DoubleToInt64Bits(d);
            int biasedE = (int)((u64 & DpExponentMask) >> DpSignificandSize);
            ulong significand = u64 & DpSignificandMask;
            return biasedE != 0
                ? new DiyFp(significand + DpHiddenBit, biasedE - DpExponentBias)
                : new DiyFp(significand, DpMinExponent + 1);
        }

        public static DiyFp operator -(DiyFp a, DiyFp b) => new DiyFp(a.F - b.F, a.E);

        // C++: diyfp.h:70-99 operator* (the 128-bit product path, as MSVC x64 and GCC x86-64 build it: round on bit 63 of the low half)
        public static DiyFp operator *(DiyFp a, DiyFp b)
        {
            ulong h = Math.BigMul(a.F, b.F, out ulong l);
            if ((l & (1UL << 63)) != 0) // rounding
                h++;
            return new DiyFp(h, a.E + b.E + 64);
        }

        // C++: diyfp.h:101-117 Normalize
        public DiyFp Normalize()
        {
            int s = System.Numerics.BitOperations.LeadingZeroCount(F);
            return new DiyFp(F << s, E - s);
        }

        // C++: diyfp.h:119-128 NormalizeBoundary
        DiyFp NormalizeBoundary()
        {
            ulong f = F;
            int e = E;
            while ((f & (DpHiddenBit << 1)) == 0)
            {
                f <<= 1;
                e--;
            }
            f <<= DiySignificandSize - DpSignificandSize - 2;
            e -= DiySignificandSize - DpSignificandSize - 2;
            return new DiyFp(f, e);
        }

        // C++: diyfp.h:130-137 NormalizedBoundaries
        public void NormalizedBoundaries(out DiyFp minus, out DiyFp plus)
        {
            DiyFp pl = new DiyFp((F << 1) + 1, E - 1).NormalizeBoundary();
            DiyFp mi = F == DpHiddenBit ? new DiyFp((F << 2) - 1, E - 2) : new DiyFp((F << 1) - 1, E - 1);
            plus = pl;
            minus = new DiyFp(mi.F << (mi.E - pl.E), pl.E);
        }
    }

    // C++: diyfp.h:164-224 kCachedPowers_F / kCachedPowers_E (10^-348, 10^-340, ..., 10^340)
    static readonly ulong[] CachedPowersF =
    {
        0xfa8fd5a0081c0288, 0xbaaee17fa23ebf76, 0x8b16fb203055ac76, 0xcf42894a5dce35ea, 0x9a6bb0aa55653b2d, 0xe61acf033d1a45df,
        0xab70fe17c79ac6ca, 0xff77b1fcbebcdc4f, 0xbe5691ef416bd60c, 0x8dd01fad907ffc3c, 0xd3515c2831559a83, 0x9d71ac8fada6c9b5,
        0xea9c227723ee8bcb, 0xaecc49914078536d, 0x823c12795db6ce57, 0xc21094364dfb5637, 0x9096ea6f3848984f, 0xd77485cb25823ac7,
        0xa086cfcd97bf97f4, 0xef340a98172aace5, 0xb23867fb2a35b28e, 0x84c8d4dfd2c63f3b, 0xc5dd44271ad3cdba, 0x936b9fcebb25c996,
        0xdbac6c247d62a584, 0xa3ab66580d5fdaf6, 0xf3e2f893dec3f126, 0xb5b5ada8aaff80b8, 0x87625f056c7c4a8b, 0xc9bcff6034c13053,
        0x964e858c91ba2655, 0xdff9772470297ebd, 0xa6dfbd9fb8e5b88f, 0xf8a95fcf88747d94, 0xb94470938fa89bcf, 0x8a08f0f8bf0f156b,
        0xcdb02555653131b6, 0x993fe2c6d07b7fac, 0xe45c10c42a2b3b06, 0xaa242499697392d3, 0xfd87b5f28300ca0e, 0xbce5086492111aeb,
        0x8cbccc096f5088cc, 0xd1b71758e219652c, 0x9c40000000000000, 0xe8d4a51000000000, 0xad78ebc5ac620000, 0x813f3978f8940984,
        0xc097ce7bc90715b3, 0x8f7e32ce7bea5c70, 0xd5d238a4abe98068, 0x9f4f2726179a2245, 0xed63a231d4c4fb27, 0xb0de65388cc8ada8,
        0x83c7088e1aab65db, 0xc45d1df942711d9a, 0x924d692ca61be758, 0xda01ee641a708dea, 0xa26da3999aef774a, 0xf209787bb47d6b85,
        0xb454e4a179dd1877, 0x865b86925b9bc5c2, 0xc83553c5c8965d3d, 0x952ab45cfa97a0b3, 0xde469fbd99a05fe3, 0xa59bc234db398c25,
        0xf6c69a72a3989f5c, 0xb7dcbf5354e9bece, 0x88fcf317f22241e2, 0xcc20ce9bd35c78a5, 0x98165af37b2153df, 0xe2a0b5dc971f303a,
        0xa8d9d1535ce3b396, 0xfb9b7cd9a4a7443c, 0xbb764c4ca7a44410, 0x8bab8eefb6409c1a, 0xd01fef10a657842c, 0x9b10a4e5e9913129,
        0xe7109bfba19c0c9d, 0xac2820d9623bf429, 0x80444b5e7aa7cf85, 0xbf21e44003acdd2d, 0x8e679c2f5e44ff8f, 0xd433179d9c8cb841,
        0x9e19db92b4e31ba9, 0xeb96bf6ebadf77d9, 0xaf87023b9bf0ee6b,
    };

    static readonly short[] CachedPowersE =
    {
        -1220, -1193, -1166, -1140, -1113, -1087, -1060, -1034, -1007, -980,
        -954, -927, -901, -874, -847, -821, -794, -768, -741, -715,
        -688, -661, -635, -608, -582, -555, -529, -502, -475, -449,
        -422, -396, -369, -343, -316, -289, -263, -236, -210, -183,
        -157, -130, -103, -77, -50, -24, 3, 30, 56, 83,
        109, 136, 162, 189, 216, 242, 269, 295, 322, 348,
        375, 402, 428, 455, 481, 508, 534, 561, 588, 614,
        641, 667, 694, 720, 747, 774, 800, 827, 853, 880,
        907, 933, 960, 986, 1013, 1039, 1066,
    };

    // C++: diyfp.h:226-238 GetCachedPower
    static DiyFp GetCachedPower(int e, out int k10)
    {
        double dk = (-61 - e) * 0.30102999566398114 + 347; // dk must be positive, so can do ceiling in positive
        int k = (int)dk;
        if (dk - k > 0.0)
            k++;
        uint index = (uint)((k >> 3) + 1);
        k10 = -(-348 + (int)(index << 3)); // decimal exponent no need lookup table
        return new DiyFp(CachedPowersF[index], CachedPowersE[index]);
    }

    // C++: dtoa.h:35-42 GrisuRound
    static void GrisuRound(char[] buffer, int len, ulong delta, ulong rest, ulong tenKappa, ulong wpW)
    {
        while (rest < wpW && delta - rest >= tenKappa &&
            (rest + tenKappa < wpW || /// closer
             wpW - rest > rest + tenKappa - wpW))
        {
            buffer[len - 1]--;
            rest += tenKappa;
        }
    }

    // C++: dtoa.h:44-58 CountDecimalDigit32
    static uint CountDecimalDigit32(uint n)
    {
        if (n < 10) return 1;
        if (n < 100) return 2;
        if (n < 1000) return 3;
        if (n < 10000) return 4;
        if (n < 100000) return 5;
        if (n < 1000000) return 6;
        if (n < 10000000) return 7;
        if (n < 100000000) return 8;
        // Will not reach 10 digits in DigitGen()
        return 9;
    }

    static readonly uint[] Pow10Table = { 1, 10, 100, 1000, 10000, 100000, 1000000, 10000000, 100000000, 1000000000 };

    // C++: dtoa.h:60-110 DigitGen
    static void DigitGen(DiyFp w, DiyFp mp, ulong delta, char[] buffer, out int len, ref int k)
    {
        var one = new DiyFp(1UL << -mp.E, mp.E);
        DiyFp wpW = mp - w;
        uint p1 = (uint)(mp.F >> -one.E);
        ulong p2 = mp.F & (one.F - 1);
        uint kappa = CountDecimalDigit32(p1); // kappa in [0, 9]
        len = 0;

        while (kappa > 0)
        {
            uint d = 0;
            switch (kappa)
            {
            case 9: d = p1 / 100000000; p1 %= 100000000; break;
            case 8: d = p1 / 10000000; p1 %= 10000000; break;
            case 7: d = p1 / 1000000; p1 %= 1000000; break;
            case 6: d = p1 / 100000; p1 %= 100000; break;
            case 5: d = p1 / 10000; p1 %= 10000; break;
            case 4: d = p1 / 1000; p1 %= 1000; break;
            case 3: d = p1 / 100; p1 %= 100; break;
            case 2: d = p1 / 10; p1 %= 10; break;
            case 1: d = p1; p1 = 0; break;
            }
            if (d != 0 || len != 0)
                buffer[len++] = (char)('0' + d);
            kappa--;
            ulong tmp = ((ulong)p1 << -one.E) + p2;
            if (tmp <= delta)
            {
                k += (int)kappa;
                GrisuRound(buffer, len, delta, tmp, (ulong)Pow10Table[kappa] << -one.E, wpW.F);
                return;
            }
        }

        // kappa = 0
        for (;;)
        {
            p2 *= 10;
            delta *= 10;
            char d = (char)(p2 >> -one.E);
            if (d != 0 || len != 0)
                buffer[len++] = (char)('0' + d);
            p2 &= one.F - 1;
            kappa--;
            if (p2 < delta)
            {
                k += (int)kappa;
                int index = -(int)kappa;
                GrisuRound(buffer, len, delta, p2, one.F, wpW.F * (index < 9 ? Pow10Table[-(int)kappa] : 0));
                return;
            }
        }
    }

    // C++: dtoa.h:112-124 Grisu2
    static void Grisu2(double value, char[] buffer, out int length, out int k)
    {
        DiyFp v = DiyFp.FromDouble(value);
        v.NormalizedBoundaries(out DiyFp wM, out DiyFp wP);

        DiyFp cMk = GetCachedPower(wP.E, out k);
        DiyFp w = v.Normalize() * cMk;
        DiyFp wp = wP * cMk;
        DiyFp wm = wM * cMk;
        wm = new DiyFp(wm.F + 1, wm.E);
        wp = new DiyFp(wp.F - 1, wp.E);
        DigitGen(w, wp, wp.F - wm.F, buffer, out length, ref k);
    }

    static readonly string DigitsLut =
        "00010203040506070809101112131415161718192021222324252627282930313233343536373839" +
        "40414243444546474849505152535455565758596061626364656667686970717273747576777879" +
        "8081828384858687888990919293949596979899";

    // C++: dtoa.h:126-148 WriteExponent
    static int WriteExponent(int kExp, char[] buffer, int at)
    {
        if (kExp < 0)
        {
            buffer[at++] = '-';
            kExp = -kExp;
        }
        if (kExp >= 100)
        {
            buffer[at++] = (char)('0' + kExp / 100);
            kExp %= 100;
            buffer[at++] = DigitsLut[kExp * 2];
            buffer[at++] = DigitsLut[kExp * 2 + 1];
        }
        else if (kExp >= 10)
        {
            buffer[at++] = DigitsLut[kExp * 2];
            buffer[at++] = DigitsLut[kExp * 2 + 1];
        }
        else
            buffer[at++] = (char)('0' + kExp);
        return at;
    }

    // C++: dtoa.h:150-214 Prettify (buffer[b..] holds the digits; returns the end index)
    static int Prettify(char[] buffer, int b, int length, int k, int maxDecimalPlaces)
    {
        int kk = length + k; // 10^(kk-1) <= v < 10^kk

        if (0 <= k && kk <= 21)
        {
            // 1234e7 -> 12340000000
            for (int i = length; i < kk; i++)
                buffer[b + i] = '0';
            buffer[b + kk] = '.';
            buffer[b + kk + 1] = '0';
            return b + kk + 2;
        }
        else if (0 < kk && kk <= 21)
        {
            // 1234e-2 -> 12.34
            Array.Copy(buffer, b + kk, buffer, b + kk + 1, length - kk);
            buffer[b + kk] = '.';
            if (0 > k + maxDecimalPlaces)
            {
                // not reached by the snapshot codec; kept as a faithful copy (the writer's maxDecimalPlaces is always 324)
                // Remove extra trailing zeros (at least one) after truncation.
                for (int i = kk + maxDecimalPlaces; i > kk + 1; i--)
                    if (buffer[b + i] != '0')
                        return b + i + 1;
                return b + kk + 2; // Reserve one zero
            }
            return b + length + 1;
        }
        else if (-6 < kk && kk <= 0)
        {
            // 1234e-6 -> 0.001234
            int offset = 2 - kk;
            Array.Copy(buffer, b, buffer, b + offset, length);
            buffer[b] = '0';
            buffer[b + 1] = '.';
            for (int i = 2; i < offset; i++)
                buffer[b + i] = '0';
            if (length - kk > maxDecimalPlaces)
            {
                // not reached by the snapshot codec; kept as a faithful copy (maxDecimalPlaces 324)
                // Remove extra trailing zeros (at least one) after truncation.
                for (int i = maxDecimalPlaces + 1; i > 2; i--)
                    if (buffer[b + i] != '0')
                        return b + i + 1;
                return b + 3; // Reserve one zero
            }
            return b + length + offset;
        }
        else if (kk < -maxDecimalPlaces)
        {
            // not reached by the snapshot codec; kept as a faithful copy (maxDecimalPlaces 324)
            // Truncate to zero
            buffer[b] = '0';
            buffer[b + 1] = '.';
            buffer[b + 2] = '0';
            return b + 3;
        }
        else if (length == 1)
        {
            // 1e30
            buffer[b + 1] = 'e';
            return WriteExponent(kk - 1, buffer, b + 2);
        }
        else
        {
            // 1234e30 -> 1.234e33
            Array.Copy(buffer, b + 1, buffer, b + 2, length - 1);
            buffer[b + 1] = '.';
            buffer[b + length + 1] = 'e';
            return WriteExponent(kk - 1, buffer, b + length + 2);
        }
    }

    /// <summary>C++: dtoa.h:216-236 dtoa(value, buffer, 324): the text rapidjson's Writer gives a finite double.</summary>
    internal static string Dtoa(double value, int maxDecimalPlaces = 324)
    {
        var buffer = new char[40];
        int b = 0;
        if (value == 0.0)
        {
            if (double.IsNegative(value))
                buffer[b++] = '-'; // -0.0, Issue #289
            buffer[b] = '0';
            buffer[b + 1] = '.';
            buffer[b + 2] = '0';
            return new string(buffer, 0, b + 3);
        }
        if (value < 0)
        {
            buffer[b++] = '-';
            value = -value;
        }
        var digits = new char[26];
        Grisu2(value, digits, out int length, out int k);
        Array.Copy(digits, 0, buffer, b, length);
        int end = Prettify(buffer, b, length, k, maxDecimalPlaces);
        return new string(buffer, 0, end);
    }
}
