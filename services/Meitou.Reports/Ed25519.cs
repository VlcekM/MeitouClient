using System.Numerics;
using System.Security.Cryptography;

namespace Meitou.Reports;

/// <summary>
/// Ed25519 signature verification as RFC 8032 §5.1 specifies it, on <see cref="BigInteger"/>: a few milliseconds per check, plenty
/// for Discord's interaction signatures, and no cryptography package. The strict equation [S]B = R + [k]A is checked (no cofactor),
/// as libsodium's verifier does. Checked against the RFC 8032 test vectors and BouncyCastle-made signatures when it was written.
/// </summary>
static class Ed25519
{
    /// <summary>
    /// The field prime 2^255 - 19.
    /// </summary>
    static readonly BigInteger P = (BigInteger.One << 255) - 19;

    /// <summary>
    /// The order of the base point's group, 2^252 + 27742317777372353535851937790883648493.
    /// </summary>
    static readonly BigInteger L = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");

    /// <summary>
    /// The curve constant d = -121665 / 121666 mod p.
    /// </summary>
    static readonly BigInteger D = Mod(-121665 * Inverse(121666));

    /// <summary>
    /// 2d, used by the addition formulas.
    /// </summary>
    static readonly BigInteger D2 = Mod(D * 2);

    /// <summary>
    /// A square root of -1 mod p, for decoding x from y.
    /// </summary>
    static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);

    /// <summary>
    /// The base point B.
    /// </summary>
    static readonly Point B = Point.Affine(
        BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202"),
        BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960"));

    /// <summary>
    /// Whether <paramref name="signature"/> (R and S, 64 bytes) is a valid Ed25519 signature of <paramref name="message"/> under the 32-byte
    /// <paramref name="publicKey"/>; false for malformed input.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        if (Decode(publicKey) is not { } a || Decode(signature[..32]) is not { } r) return false;
        var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
        if (s >= L) return false;

        var hashed = new byte[64 + message.Length];
        signature[..32].CopyTo(hashed);
        publicKey.CopyTo(hashed.AsSpan(32));
        message.CopyTo(hashed.AsSpan(64));
        var k = new BigInteger(SHA512.HashData(hashed), isUnsigned: true, isBigEndian: false) % L;

        return Multiply(B, s).SameAs(Add(r, Multiply(a, k)));
    }

    /// <summary>
    /// A point from its 32-byte encoding: y little-endian with the sign of x in the top bit; null when it is not on the curve.
    /// </summary>
    static Point? Decode(ReadOnlySpan<byte> bytes)
    {
        Span<byte> copy = stackalloc byte[32];
        bytes.CopyTo(copy);
        bool xOdd = (copy[31] & 0x80) != 0;
        copy[31] &= 0x7f;
        var y = new BigInteger(copy, isUnsigned: true, isBigEndian: false);
        if (y >= P) return null;

        var y2 = Mod(y * y);
        var u = Mod((y2 - 1) * Inverse(D * y2 + 1));          // x^2 = (y^2 - 1) / (d y^2 + 1)
        var x = BigInteger.ModPow(u, (P + 3) / 8, P);         // a square root of u, or of -u
        var x2 = Mod(x * x);
        if (x2 != u)
        {
            if (Mod(x2 + u) != 0) return null;
            x = Mod(x * SqrtMinusOne);
        }
        if (x.IsZero && xOdd) return null;
        if (!x.IsEven != xOdd) x = P - x;
        return Point.Affine(x, y);
    }

    /// <summary>
    /// n times p by double and add, highest bit first.
    /// </summary>
    static Point Multiply(Point p, BigInteger n)
    {
        var bits = n.ToByteArray(isUnsigned: true, isBigEndian: false);
        var result = Point.Identity;
        for (int i = bits.Length * 8 - 1; i >= 0; i--)
        {
            result = Double(result);
            if ((bits[i >> 3] >> (i & 7) & 1) != 0) result = Add(result, p);
        }
        return result;
    }

    // Extended twisted Edwards coordinates (x = X/Z, y = Y/Z, T = XY/Z) with a = -1: the complete formulas of Hisil, Wong, Carter
    // and Dawson (2008), "add-2008-hwcd-3" and "dbl-2008-hwcd".
    /// <summary>
    /// p + q.
    /// </summary>
    static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(p.T * D2 * q.T);
        var d = Mod(p.Z * 2 * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    /// <summary>
    /// 2p.
    /// </summary>
    static Point Double(Point p)
    {
        var a = Mod(p.X * p.X);
        var b = Mod(p.Y * p.Y);
        var c = Mod(2 * p.Z * p.Z);
        var d = P - a;                                        // a = -1
        var e = Mod((p.X + p.Y) * (p.X + p.Y) - a - b);
        var g = d + b;
        var f = g - c;
        var h = d - b;
        return new(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    /// <summary>
    /// <paramref name="x"/> reduced into 0 to p - 1.
    /// </summary>
    static BigInteger Mod(BigInteger x)
    {
        x %= P;
        return x.Sign < 0 ? x + P : x;
    }

    /// <summary>
    /// The inverse of <paramref name="x"/> mod p (Fermat: x^(p-2)).
    /// </summary>
    static BigInteger Inverse(BigInteger x) => BigInteger.ModPow(Mod(x), P - 2, P);

    /// <summary>
    /// A curve point in extended coordinates (x = X/Z, y = Y/Z, T = XY/Z).
    /// </summary>
    readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T)
    {
        /// <summary>
        /// The neutral point (0, 1).
        /// </summary>
        public static readonly Point Identity = new(0, 1, 1, 0);

        /// <summary>
        /// The point with affine coordinates (<paramref name="x"/>, <paramref name="y"/>).
        /// </summary>
        public static Point Affine(BigInteger x, BigInteger y) => new(x, y, 1, Mod(x * y));

        /// <summary>
        /// The same curve point, whatever the projective scale.
        /// </summary>
        public bool SameAs(Point o) => Mod(X * o.Z - o.X * Z).IsZero && Mod(Y * o.Z - o.Y * Z).IsZero;
    }
}
