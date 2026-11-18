// Port of rapidjson v1.1.0 internal/strtod.h, biginteger.h, ieee754.h, pow10.h (MIT, see NOTICE.md); part of Core/RapidJson.cs (its header says what is ported and why).
using System.Globalization;

namespace ProvingGrounds.Core;

internal static partial class RapidJson
{
    // ---- internal/strtod.h, biginteger.h, ieee754.h, pow10.h: the double value of a number --------------------------------------

    // C++: pow10.h:28-50 (the table of 1e0..1e308; each literal is the correctly rounded double)
    static readonly double[] Pow10Doubles = MakePow10();
    static double[] MakePow10()
    {
        var e = new double[309];
        for (int n = 0; n <= 308; ++n) e[n] = double.Parse("1e" + n.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return e;
    }
    static double Pow10(int n) => Pow10Doubles[n];

    // C++: ieee754.h Double (the queries strtod.h uses)
    const int DSignificandSize = 52;
    const int DExponentBias = 0x3FF;
    const int DDenormalExponent = 1 - DExponentBias;
    const ulong DSignMask = 0x8000000000000000UL;
    const ulong DExponentMask = 0x7FF0000000000000UL;
    const ulong DSignificandMask = 0x000FFFFFFFFFFFFFUL;
    const ulong DHiddenBit = 0x0010000000000000UL;
    static ulong Bits(double d) => unchecked((ulong)BitConverter.DoubleToInt64Bits(d));
    static double FromBits(ulong u) => BitConverter.Int64BitsToDouble(unchecked((long)u));
    static ulong DSignificand(double d) => Bits(d) & DSignificandMask;
    static int DExponent(double d) => (int)((Bits(d) & DExponentMask) >> DSignificandSize) - DExponentBias;
    static bool DIsNormal(double d) => (Bits(d) & DExponentMask) != 0 || DSignificand(d) == 0;
    static ulong DIntegerSignificand(double d) => DIsNormal(d) ? DSignificand(d) | DHiddenBit : DSignificand(d);
    static int DIntegerExponent(double d) => (DIsNormal(d) ? DExponent(d) : DDenormalExponent) - DSignificandSize;
    // ieee754.h:32-35 NextPositiveDouble (asserts !Sign(), an assert compiled out of the release build)
    static double NextPositiveDouble(double d) => FromBits(unchecked(Bits(d) + 1));
    // ieee754.h:51-58 EffectiveSignificandSize
    static uint EffectiveSignificandSize(int order)
    {
        if (order >= -1021)
            return 53;
        else if (order <= -1074)
            return 0;
        else
            return (uint)order + 1074;
    }

    // C++: strtod.h:26-33 FastPath
    static double FastPath(double significand, int exp)
    {
        if (exp < -308)
            return 0.0;
        else if (exp >= 0)
            return significand * Pow10(exp);
        else
            return significand / Pow10(-exp);
    }

    // C++: strtod.h:35-44 StrtodNormalPrecision
    // not reached by the snapshot codec; kept as a faithful copy (the codec always parses with kParseFullPrecisionFlag)
    static double StrtodNormalPrecision(double d, int p)
    {
        if (p < -308)
        {
            // Prevent expSum < -308, making Pow10(p) = 0
            d = FastPath(d, -308);
            d = FastPath(d, p + 308);
        }
        else
            d = FastPath(d, p);
        return d;
    }

    // C++: strtod.h:46-52 Min3
    static int Min3(int a, int b, int c)
    {
        int m = a;
        if (m > b) m = b;
        if (m > c) m = c;
        return m;
    }

    // C++: biginteger.h:28-284 BigInteger (64-bit digits, kCapacity = 3328 / sizeof(Type) = 416 of them)
    sealed class BigInteger
    {
        const int Capacity = 3328 / 8;
        const int TypeBit = 64;
        // zeroed, where the C++ array is uninitialized: only the shift bug below ever reads past count_ (see there)
        readonly ulong[] _digits = new ulong[Capacity];
        int _count;

        public BigInteger(BigInteger rhs) { _count = rhs._count; System.Array.Copy(rhs._digits, _digits, _count); }

        public BigInteger(ulong u) { _count = 1; _digits[0] = u; }

        // C++: biginteger.h:40-53
        public BigInteger(byte[] decimals, int start, int length)
        {
            _count = 1;
            _digits[0] = 0;
            int i = 0;
            const int maxDigitPerIteration = 19; // 2^64 = 18446744073709551616 > 10^19
            while (length >= maxDigitPerIteration)
            {
                AppendDecimal64(decimals, start + i, start + i + maxDigitPerIteration);
                length -= maxDigitPerIteration;
                i += maxDigitPerIteration;
            }
            if (length > 0)
                AppendDecimal64(decimals, start + i, start + i + length);
        }

        void Assign(ulong u) { _digits[0] = u; _count = 1; }

        // C++: biginteger.h:70-85 operator+=(uint64_t)
        public BigInteger Add(ulong u)
        {
            ulong backup = _digits[0];
            _digits[0] = unchecked(_digits[0] + u);
            for (int i = 0; i < _count - 1; i++)
            {
                if (_digits[i] >= backup)
                    return this; // no carry
                backup = _digits[i + 1];
                _digits[i + 1] = unchecked(_digits[i + 1] + 1);
            }
            // Last carry
            if (_digits[_count - 1] < backup)
                PushBack(1);
            return this;
        }

        bool IsOne() => _count == 1 && _digits[0] == 1;

        // C++: biginteger.h:87-103 operator*=(uint64_t)
        public BigInteger Mul64(ulong u)
        {
            if (u == 0) { Assign(0); return this; }
            if (u == 1) return this;
            if (IsOne()) { Assign(u); return this; }
            ulong k = 0;
            for (int i = 0; i < _count; i++)
            {
                _digits[i] = MulAdd64(_digits[i], u, k, out ulong hi);
                k = hi;
            }
            if (k > 0)
                PushBack(k);
            return this;
        }

        // C++: biginteger.h:105-126 operator*=(uint32_t)
        public BigInteger Mul32(uint u)
        {
            if (u == 0) { Assign(0); return this; }
            if (u == 1) return this;
            if (IsOne()) { Assign(u); return this; }
            ulong k = 0;
            for (int i = 0; i < _count; i++)
            {
                ulong c = _digits[i] >> 32;
                ulong d = _digits[i] & 0xFFFFFFFF;
                ulong uc = unchecked(u * c);
                ulong ud = unchecked(u * d);
                ulong p0 = unchecked(ud + k);
                ulong p1 = unchecked(uc + (p0 >> 32));
                _digits[i] = (p0 & 0xFFFFFFFF) | (p1 << 32);
                k = p1 >> 32;
            }
            if (k > 0)
                PushBack(k);
            return this;
        }

        // C++: biginteger.h:128-151 operator<<=. A shift by a whole number of digits (interShift 0) runs the 1.1.0 memmove as
        // written: it moves count_ digits from index count_-1 (not 0), reading past the value; those digits are zero here and
        // uninitialized stack in the C++ (docs/divergences.md).
        public BigInteger ShiftLeft(int shift)
        {
            if (IsZero() || shift == 0) return this;
            int offset = shift / TypeBit;
            int interShift = shift % TypeBit;
            if (interShift == 0)
            {
                System.Array.Copy(_digits, _count - 1, _digits, _count - 1 + offset, _count);
                _count += offset;
            }
            else
            {
                _digits[_count] = 0;
                for (int i = _count; i > 0; i--)
                    _digits[i + offset] = (_digits[i] << interShift) | (_digits[i - 1] >> (TypeBit - interShift));
                _digits[offset] = _digits[0] << interShift;
                _count += offset;
                if (_digits[_count] != 0)
                    _count++;
            }
            System.Array.Clear(_digits, 0, offset);
            return this;
        }

        // C++: biginteger.h:162-183 MultiplyPow5
        static readonly uint[] Pow5 = { 5, 5 * 5, 5 * 5 * 5, 5 * 5 * 5 * 5, 5 * 5 * 5 * 5 * 5, 5 * 5 * 5 * 5 * 5 * 5, 5 * 5 * 5 * 5 * 5 * 5 * 5,
            5 * 5 * 5 * 5 * 5 * 5 * 5 * 5, 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5, 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5,
            5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5, 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 * 5 };
        public BigInteger MultiplyPow5(uint exp)
        {
            if (exp == 0) return this;
            for (; exp >= 27; exp -= 27) Mul64(0x6765C793FA10079DUL); // 5^27
            for (; exp >= 13; exp -= 13) Mul32(1220703125u); // 5^13
            if (exp > 0) Mul32(Pow5[exp - 1]);
            return this;
        }

        // C++: biginteger.h:186-206 Difference (assumes this != rhs; out's count is only raised, as in the C++)
        public bool Difference(BigInteger rhs, BigInteger @out)
        {
            int cmp = Compare(rhs);
            BigInteger a, b; // Makes a > b
            bool ret;
            if (cmp < 0) { a = rhs; b = this; ret = true; }
            else { a = this; b = rhs; ret = false; }
            ulong borrow = 0;
            for (int i = 0; i < a._count; i++)
            {
                ulong d = unchecked(a._digits[i] - borrow);
                if (i < b._count)
                    d = unchecked(d - b._digits[i]);
                borrow = d > a._digits[i] ? 1UL : 0UL;
                @out._digits[i] = d;
                if (d != 0)
                    @out._count = i + 1;
            }
            return ret;
        }

        // C++: biginteger.h:208-217 Compare
        public int Compare(BigInteger rhs)
        {
            if (_count != rhs._count)
                return _count < rhs._count ? -1 : 1;
            for (int i = _count; i-- > 0;)
                if (_digits[i] != rhs._digits[i])
                    return _digits[i] < rhs._digits[i] ? -1 : 1;
            return 0;
        }

        bool IsZero() => _count == 1 && _digits[0] == 0;

        // C++: biginteger.h:224-232 AppendDecimal64
        void AppendDecimal64(byte[] decimals, int begin, int end)
        {
            ulong u = ParseUint64(decimals, begin, end);
            if (IsZero())
                Assign(u);
            else
            {
                uint exp = (uint)(end - begin);
                MultiplyPow5(exp).ShiftLeft((int)exp).Add(u); // *this = *this * 10^exp + u
            }
        }

        void PushBack(ulong digit) { _digits[_count++] = digit; }

        // C++: biginteger.h:239-246 ParseUint64
        static ulong ParseUint64(byte[] decimals, int begin, int end)
        {
            ulong r = 0;
            for (int p = begin; p != end; ++p)
                r = unchecked(r * 10u + (uint)(decimals[p] - '0'));
            return r;
        }

        // C++: biginteger.h:249-276 MulAdd64 (the 128-bit path, as MSVC x64 and GCC x86-64 compile it)
        static ulong MulAdd64(ulong a, ulong b, ulong k, out ulong outHigh)
        {
            UInt128 p = (UInt128)a * b;
            p += k;
            outHigh = (ulong)(p >> 64);
            return (ulong)p;
        }
    }

    // C++: strtod.h:54-109 CheckWithinHalfULP
    static int CheckWithinHalfULP(double b, BigInteger d, int dExp)
    {
        ulong bInt = DIntegerSignificand(b);
        int bExp = DIntegerExponent(b);
        int hExp = bExp - 1;

        int dS_Exp2 = 0, dS_Exp5 = 0, bS_Exp2 = 0, bS_Exp5 = 0, hS_Exp2 = 0, hS_Exp5 = 0;

        // Adjust for decimal exponent
        if (dExp >= 0)
        {
            dS_Exp2 += dExp;
            dS_Exp5 += dExp;
        }
        else
        {
            bS_Exp2 -= dExp;
            bS_Exp5 -= dExp;
            hS_Exp2 -= dExp;
            hS_Exp5 -= dExp;
        }

        // Adjust for binary exponent
        if (bExp >= 0)
            bS_Exp2 += bExp;
        else
        {
            dS_Exp2 -= bExp;
            hS_Exp2 -= bExp;
        }

        // Adjust for half ulp exponent
        if (hExp >= 0)
            hS_Exp2 += hExp;
        else
        {
            dS_Exp2 -= hExp;
            bS_Exp2 -= hExp;
        }

        // Remove common power of two factor from all three scaled values
        int common_Exp2 = Min3(dS_Exp2, bS_Exp2, hS_Exp2);
        dS_Exp2 -= common_Exp2;
        bS_Exp2 -= common_Exp2;
        hS_Exp2 -= common_Exp2;

        var dS = new BigInteger(d);
        dS.MultiplyPow5((uint)dS_Exp5).ShiftLeft(dS_Exp2);

        var bS = new BigInteger(bInt);
        bS.MultiplyPow5((uint)bS_Exp5).ShiftLeft(bS_Exp2);

        var hS = new BigInteger(1);
        hS.MultiplyPow5((uint)hS_Exp5).ShiftLeft(hS_Exp2);

        var delta = new BigInteger(0);
        dS.Difference(bS, delta);

        return delta.Compare(hS);
    }

    // C++: strtod.h:111-126 StrtodFast
    static bool StrtodFast(double d, int p, out double result)
    {
        // Use fast path for string-to-double conversion if possible
        // see http://www.exploringbinary.com/fast-path-decimal-to-floating-point-conversion/
        if (p > 22 && p < 22 + 16)
        {
            // Fast Path Cases In Disguise
            d *= Pow10(p - 22);
            p = 22;
        }
        if (p >= -22 && p <= 22 && d <= 9007199254740991.0)
        {
            // 2^53 - 1
            result = FastPath(d, p);
            return true;
        }
        result = 0;
        return false;
    }

    // C++: strtod.h:156-164 (the kPow10 table of StrtodDiyFp; `00000000` is an octal zero)
    static readonly DiyFp[] DiyPow10 =
    {
        new DiyFp(0xa000000000000000UL, -60), // 10^1
        new DiyFp(0xc800000000000000UL, -57), // 10^2
        new DiyFp(0xfa00000000000000UL, -54), // 10^3
        new DiyFp(0x9c40000000000000UL, -50), // 10^4
        new DiyFp(0xc350000000000000UL, -47), // 10^5
        new DiyFp(0xf424000000000000UL, -44), // 10^6
        new DiyFp(0x9896800000000000UL, -40), // 10^7
    };

    // C++: strtod.h:129-204 StrtodDiyFp (compute an approximation and see if it is within 1/2 ULP)
    static bool StrtodDiyFp(byte[] decimals, int start, int length, long decimalPosition, int exp, out double result)
    {
        ulong significand = 0;
        int i = 0; // 2^64 - 1 = 18446744073709551615, 1844674407370955161 = 0x1999999999999999
        for (; i < length; i++)
        {
            if (significand > 0x1999999999999999UL ||
                (significand == 0x1999999999999999UL && decimals[start + i] > '5'))
                break;
            significand = significand * 10u + (uint)(decimals[start + i] - '0');
        }

        if (i < length && decimals[start + i] >= '5') // Rounding
            significand++;

        int remaining = length - i;
        const int ulpShift = 3;
        const int ulp = 1 << ulpShift;
        long error = remaining == 0 ? 0 : ulp / 2;

        if (significand == 0)
        {
            // DiyFp::Normalize of 0 is __builtin_clzll(0) / _BitScanReverse64 of 0: undefined behaviour in the C++
            // (docs/divergences.md). A zero significand is the value zero.
            result = 0.0;
            return true;
        }
        DiyFp v = new DiyFp(significand, 0).Normalize();
        error <<= -v.E;

        int dExp = (int)decimalPosition - i + exp;

        // C++: diyfp.h:240-244 GetCachedPower10. An index outside the 87-entry table is an out-of-bounds read in 1.1.0 (undefined
        // behaviour; docs/divergences.md). It happens only for a value below 1e-330 here, whose correctly rounded value is zero.
        uint index = unchecked((uint)(dExp + 348)) / 8u;
        if (index >= CachedPowersF.Length)
        {
            result = 0.0;
            return true;
        }
        int actualExp = -348 + (int)index * 8;
        var cachedPower = new DiyFp(CachedPowersF[index], CachedPowersE[index]);
        if (actualExp != dExp)
        {
            int adjustment = dExp - actualExp - 1;
            v = v * DiyPow10[adjustment];
            if (length + adjustment > 19) // has more digits than decimal digits in 64-bit
                error += ulp / 2;
        }

        v = v * cachedPower;

        error += ulp + (error == 0 ? 0 : 1);

        int oldExp = v.E;
        v = v.Normalize();
        error <<= oldExp - v.E;

        uint effectiveSignificandSize = EffectiveSignificandSize(64 + v.E);
        uint precisionSize = 64 - effectiveSignificandSize;
        ulong vf = v.F;
        int ve = v.E;
        if (precisionSize + ulpShift >= 64)
        {
            uint scaleExp = precisionSize + ulpShift - 63;
            vf >>= (int)scaleExp;
            ve += (int)scaleExp;
            error = (error >> (int)scaleExp) + 1 + ulp;
            precisionSize -= scaleExp;
        }

        ulong roundedF = vf >> (int)precisionSize;
        int roundedE = ve + (int)precisionSize;
        ulong precisionBits = unchecked((vf & ((1UL << (int)precisionSize) - 1)) * ulp);
        ulong halfWay = unchecked((1UL << (int)(precisionSize - 1)) * ulp);
        uint uerror = unchecked((uint)error);
        if (precisionBits >= unchecked(halfWay + uerror))
        {
            roundedF++;
            if ((roundedF & (DiyFp.DpHiddenBit << 1)) != 0)
            {
                // rounding overflows mantissa (issue #340)
                roundedF >>= 1;
                roundedE++;
            }
        }

        result = DiyFpToDouble(roundedF, roundedE);

        return unchecked(halfWay - uerror) >= precisionBits || precisionBits >= unchecked(halfWay + uerror);
    }

    // C++: diyfp.h:139-148 DiyFp::ToDouble (a biased exponent past 0x7FF is shifted out as written: a value above DBL_MAX gives
    // the garbage double rapidjson 1.1.0 gives, e.g. 10e308 -> -3.09e-308)
    static double DiyFpToDouble(ulong f, int e)
    {
        const int dpExponentBias = 0x3FF + 52;
        const int dpDenormalExponent = -dpExponentBias + 1;
        ulong be = e == dpDenormalExponent && (f & DiyFp.DpHiddenBit) == 0 ? 0 : unchecked((ulong)(long)(e + dpExponentBias));
        return FromBits((f & DSignificandMask) | (be << 52));
    }

    // C++: strtod.h:206-222 StrtodBigInteger
    static double StrtodBigInteger(double approx, byte[] decimals, int start, int length, long decimalPosition, int exp)
    {
        var dInt = new BigInteger(decimals, start, length);
        int dExp = (int)decimalPosition - length + exp;
        int cmp = CheckWithinHalfULP(approx, dInt, dExp);
        if (cmp < 0)
            return approx; // within half ULP
        else if (cmp == 0)
        {
            // Round towards even
            if ((DSignificand(approx) & 1) != 0)
                return NextPositiveDouble(approx);
            else
                return approx;
        }
        else // adjustment
            return NextPositiveDouble(approx);
    }

    // C++: strtod.h:224-265 StrtodFullPrecision. decimalPosition is a size_t in the C++ (it can wrap below zero when leading zeros
    // are trimmed; every use casts it to int), a long here.
    static double StrtodFullPrecision(double d, int p, byte[] decimals, int length, long decimalPosition, int exp)
    {
        if (StrtodFast(d, p, out double result))
            return result;

        int start = 0;
        // Trim leading zeros
        while (decimals[start] == '0' && length > 1)
        {
            length--;
            start++;
            decimalPosition--;
        }

        // Trim trailing zeros
        while (decimals[start + length - 1] == '0' && length > 1)
        {
            length--;
            decimalPosition--;
            exp++;
        }

        // Trim right-most digits
        const int maxDecimalDigit = 780;
        if (length > maxDecimalDigit)
        {
            int delta = length - maxDecimalDigit;
            exp += delta;
            decimalPosition -= delta;
            length = maxDecimalDigit;
        }

        // If too small, underflow to zero
        if (length + exp < -324)
            return 0.0;

        if (StrtodDiyFp(decimals, start, length, decimalPosition, exp, out result))
            return result;

        // Use approximation from StrtodDiyFp and make adjustment with BigInteger comparison
        return StrtodBigInteger(result, decimals, start, length, decimalPosition, exp);
    }
}
