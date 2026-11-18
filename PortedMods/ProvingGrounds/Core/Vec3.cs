// Ogre 1.9 Vector3 / Quaternion operations the original's ArenaIngress.cpp formation maths uses (task M4.3a), so Core holds no engine type.
// Bodies follow OGRE v1-9-0 (github.com/OGRECave/ogre tag v1-9-0, MIT; the same bodies as golden/stubs/ogre_formation_stub.h):
// Real is float, every operation is per component in float, in Ogre's order.
namespace ProvingGrounds.Core;

/// <summary>Ogre::Vector3 (x, y, z floats; Kenshi's y is up). Copied by value like the C++. Equality is exact per component
/// (float.Equals: NaN equals NaN, 0 equals -0), for tests and lookups; the C++ formation code never compares vectors.</summary>
public struct Vec3 : IEquatable<Vec3>
{
    public float X, Y, Z;

    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

    // Ogre OgreVector3.h:163-169
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    // Ogre OgreVector3.h:171-177
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    // Ogre OgreVector3.h:179-185
    public static Vec3 operator *(Vec3 v, float s) => new(v.X * s, v.Y * s, v.Z * s);

    // Ogre OgreVector3.h:492-498
    public readonly Vec3 Cross(Vec3 v) => new(
        Y * v.Z - Z * v.Y,
        Z * v.X - X * v.Z,
        X * v.Y - Y * v.X);

    public readonly bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override readonly bool Equals(object? obj) => obj is Vec3 other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);

    public static bool operator ==(Vec3 a, Vec3 b) => a.Equals(b);

    public static bool operator !=(Vec3 a, Vec3 b) => !a.Equals(b);

    public override readonly string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({X:R}, {Y:R}, {Z:R})");
}

/// <summary>Ogre::Quaternion (w, x, y, z floats). A building's orientation in the C++ (<c>Building::getOrientation</c>).</summary>
public struct Quat
{
    public float W, X, Y, Z;

    public Quat(float w, float x, float y, float z) { W = w; X = x; Y = y; Z = z; }

    /// <summary><c>Ogre::Quaternion(Radian(yaw), Vector3::UNIT_Y)</c>: a turn of <paramref name="yaw"/> radians about +Y, which takes
    /// local +Z towards world +X. The C++ reads a building's full <c>getOrientation()</c>; in the port, task 4.1's ArenaBuilding exposes
    /// only a <c>Yaw</c> (Meitou town buildings carry a heading, not a full rotation: docs/divergences.md "ArenaIdentity"), so the glue
    /// (task 4.3b) builds the site orientation with <c>Quat.FromYaw(building.Yaw)</c>. That this equals Kenshi's orientation assumes
    /// Meitou's heading convention ("0 along +Z, turning towards +X", the same sense as this turn) and no tilt: an ASSUMPTION until
    /// 4.3b checks it live (a formation drawn mirrored or turned 90 degrees means the convention differs).</summary>
    // Ogre OgreQuaternion.h:78-81 -> OgreQuaternion.cpp:116-130 (FromAngleAxis), OgreMath.h:319-321, 381-383, 747-750
    public static Quat FromYaw(float yaw)
    {
        float fHalfAngle = 0.5f * yaw;   // Radian(0.5 * rfAngle): Real * Radian, the double 0.5 converted to float
        float fSin = MathF.Sin(fHalfAngle);
        float w = MathF.Cos(fHalfAngle);
        // axis UNIT_Y = (0, 1, 0): x = fSin * 0, y = fSin * 1, z = fSin * 0
        return new Quat(w, fSin * 0.0f, fSin * 1.0f, fSin * 0.0f);
    }

    /// <summary><c>Quaternion * Vector3</c>: rotates <paramref name="v"/> (Ogre's "nVidia SDK implementation"; no normalisation).</summary>
    // Ogre OgreQuaternion.cpp:399-411
    public static Vec3 operator *(Quat q, Vec3 v)
    {
        var qvec = new Vec3(q.X, q.Y, q.Z);
        Vec3 uv = qvec.Cross(v);
        Vec3 uuv = qvec.Cross(uv);
        uv = uv * (2.0f * q.W);
        uuv = uuv * 2.0f;

        return v + uv + uuv;
    }
}
