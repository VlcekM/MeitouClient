using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Meitou.Data.Characters;

namespace Meitou.Data.Physics;

/// <summary>
/// Shape kinds of a Scythe physics file (the shape record's type; docs/formats/phs.md, "Building the PhysX objects").
/// </summary>
public enum PhysicsShapeType
{
    /// <summary>
    /// Any type the game does not name (3, and 6 in two lantern files): a box of <see cref="PhysicsShape.Size"/> full extents.
    /// </summary>
    Box = 3,
    /// <summary>
    /// A sphere; size.x is the diameter.
    /// </summary>
    Sphere = 4,
    /// <summary>
    /// A capsule along the shape's local Y; size.x is the diameter, size.y the length between the cap centres.
    /// </summary>
    Capsule = 5,
    /// <summary>
    /// A cooked PhysX mesh (<see cref="PhysicsShape.CookedFile"/>), convex when <see cref="PhysicsShape.Convex"/>.
    /// </summary>
    Mesh = 9,
}

/// <summary>
/// Joint kinds (the joint record's type; 1 to 7 as PhysX 2.8's joint types; others are skipped by the game).
/// </summary>
public enum PhysicsJointType
{
    /// <summary>
    /// A hinge: rotation about the joint axis, limited from low to high degrees.
    /// </summary>
    Revolute = 1,

    /// <summary>
    /// A ball joint: twist about the axis between low and high, swing up to the swing limit.
    /// </summary>
    Spherical = 2,

    /// <summary>
    /// PhysX 2.8's cylindrical joint.
    /// </summary>
    Cylindrical = 3,

    /// <summary>
    /// PhysX 2.8's prismatic (sliding) joint.
    /// </summary>
    Prismatic = 4,

    /// <summary>
    /// PhysX 2.8's fixed joint.
    /// </summary>
    Fixed = 5,

    /// <summary>
    /// PhysX 2.8's six-degree-of-freedom joint; which of the record's fields it reads is Unknown (no ragdoll uses it).
    /// </summary>
    D6 = 6,

    /// <summary>
    /// PhysX 2.8's point-on-line joint.
    /// </summary>
    PointOnLine = 7
}

/// <summary>
/// A Scythe physics file (<c>.phs</c>): ragdolls (RACE <c>male ragdoll</c>), physics props and attachment points. Everything is in model space
/// (Kenshi units, Y up); the game multiplies positions and sizes by the race's scale when it builds the bodies. Layout and field
/// meanings: docs/formats/phs.md.
/// </summary>
public sealed class PhysicsFile
{
    /// <summary>
    /// The file's version float; later fields are read only from the version that added them.
    /// </summary>
    public float Version { get; private init; }
    /// <summary>
    /// The actors in file order; joints refer to them by index.
    /// </summary>
    public List<PhysicsBodyActor> Actors { get; } = [];
    /// <summary>
    /// The joints in file order.
    /// </summary>
    public List<PhysicsJoint> Joints { get; } = [];

    /// <summary>
    /// <see cref="Read"/> of the file at <paramref name="path"/>, its case resolved as on disk.
    /// </summary>
    public static PhysicsFile ReadFile(string path) => Read(File.ReadAllBytes(Content.GameFiles.Resolve(path)));

    /// <summary>
    /// Reads a whole file; throws <see cref="FormatException"/> when the data is cut short, has bad counts or does not end where the layout does.
    /// </summary>
    public static PhysicsFile Read(byte[] data)
    {
        var r = new Reader(data);
        try
        {
            float v = r.Float();
            var file = new PhysicsFile { Version = v };
            for (int n = r.Count(10_000); n > 0; n--) file.Actors.Add(ReadActor(ref r, v));
            if (v >= 1.4f) for (int n = r.Count(); n > 0; n--) ReadPoint(ref r);   // models
            int jointSize = v >= 1.2f ? 0x264 : 0x138;
            for (int n = r.Count(); n > 0; n--) file.Joints.Add(ReadJoint(r.Bytes(jointSize), v));
            r.Skip(r.Count(), 0x38c);
            for (int n = r.Count(); n > 0; n--) ReadShape(ref r, v);   // loose shapes
            for (int n = r.Count(); n > 0; n--) ReadPoint(ref r);      // loose points
            r.Skip(r.Count(), 0x7c);                                   // loose lights
            if (r.Position != data.Length) throw new FormatException($"Physics file has {data.Length - r.Position} bytes past its end.");
            return file;
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new FormatException("Unexpected end of physics file.", e);
        }
    }

    /// <summary>
    /// Reads one actor of a version <paramref name="v"/> file with its shapes; its points and lights are skipped, and the angular damping is scaled
    /// by 10.
    /// </summary>
    static PhysicsBodyActor ReadActor(ref Reader r, float v)
    {
        int shapes = r.Int(), points = r.Int(), lights = r.Int();
        if (shapes is < 0 or > 10_000 || points is < 0 or > 10_000 || lights is < 0 or > 10_000) throw new FormatException("Bad actor counts.");
        r.Int();
        var position = r.Vector();
        r.Float(); r.Float();
        bool isStatic = r.Byte() != 0;
        float density = r.Float(), angularDamping = r.Float() * 10, linearDamping = r.Float();
        r.Skip(1, 32);
        float mass = r.Float();
        r.Float(); r.Float();   // sleep velocities (-1: the SDK's)
        var centreOfMass = r.Vector();
        r.Skip(1, 36);
        bool useCentreOfMass = v >= 1.1f && r.Byte() != 0;
        string name = "Actor";
        bool noGravity = false;
        if (v >= 1.2f)
        {
            name = r.String();
            r.Byte();
            noGravity = r.Byte() != 0;
        }
        if (v >= 1.3f) r.Int();
        string? bone = null;
        if (v >= 1.4f)
        {
            r.Int();
            bone = r.String();
            r.Vector(); r.Vector(); r.Vector(); r.Vector();   // the bone's binding pose: only the attachment loader reads it
            r.Byte();
        }
        var actor = new PhysicsBodyActor(name, position, isStatic, mass, density, linearDamping, angularDamping,
            useCentreOfMass ? centreOfMass : null, noGravity, bone is { Length: > 0 } ? bone : null);
        for (int i = 0; i < shapes; i++) actor.Shapes.Add(ReadShape(ref r, v));
        for (int i = 0; i < points; i++) ReadPoint(ref r);
        r.Skip(lights, 0x7c);
        return actor;
    }

    /// <summary>
    /// Reads one shape record of a version <paramref name="v"/> file.
    /// </summary>
    static PhysicsShape ReadShape(ref Reader r, float v)
    {
        var rotation = PhysicsAttachmentFile.FromColumns(r.Vector(), r.Vector(), r.Vector());
        var position = r.Vector();
        int type = r.Int();
        r.Skip(1, 32);
        r.Int();
        var size = r.Vector();
        r.Float();
        float mass = r.Float(), density = r.Float(), skinWidth = r.Float();
        r.String();
        string cooked = v >= 1.2f ? r.String() : "";
        r.Byte();
        bool hasMesh = r.Byte() != 0;
        bool convex = false;
        if (v >= 1.2f ? type == (int)PhysicsShapeType.Mesh : hasMesh)
        {
            convex = r.Byte() != 0;
            int vertices = r.Count(1_000_000), triangles = r.Count(1_000_000);
            r.Skip(vertices, 24);   // vertices and a second array (normals?); the game loads the cooked file instead
            r.Skip(triangles, 6);
            if (v >= 1.2f) r.Skip(1, 2);
        }
        if (v >= 1.2f) r.Skip(1, 4);
        var kind = type switch { 4 => PhysicsShapeType.Sphere, 5 => PhysicsShapeType.Capsule, 9 => PhysicsShapeType.Mesh, _ => PhysicsShapeType.Box };
        return new PhysicsShape(kind, position, rotation, size, mass, density, skinWidth, cooked, convex);
    }

    /// <summary>
    /// Skips a point record (its fixed part, a name and a vector); nothing of it is kept.
    /// </summary>
    static void ReadPoint(ref Reader r)
    {
        r.Skip(1, 48 + 4 + 32);
        r.String();
        r.Vector();
    }

    /// <summary>
    /// Reads a joint record from its fixed-size bytes <paramref name="b"/>; the three springs exist only from version 1.2.
    /// </summary>
    static PhysicsJoint ReadJoint(ReadOnlySpan<byte> b, float v)
    {
        static float F(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b[at..]);
        static int I(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b[at..]);
        static Vector3 V(ReadOnlySpan<byte> b, int at) => new(F(b, at), F(b, at + 4), F(b, at + 8));
        static PhysicsSpring? Spring(ReadOnlySpan<byte> b, int at) => b[at] != 0 ? new PhysicsSpring(F(b, at + 4), F(b, at + 8), F(b, at + 12)) : null;
        bool full = v >= 1.2f;
        return new PhysicsJoint((PhysicsJointType)I(b, 0x30), I(b, 0x6c), I(b, 0x70), V(b, 0x00), V(b, 0x0c), V(b, 0x18),
            b[0x68] != 0, F(b, 0x5c), F(b, 0x58), F(b, 0x60), b[0x7c] != 0,
            full ? Spring(b, 0x108) : null, full ? Spring(b, 0x118) : null, full ? Spring(b, 0x128) : null);
    }

    /// <summary>
    /// A bounds-checked little-endian cursor over the file.
    /// </summary>
    ref struct Reader(byte[] data)
    {
        /// <summary>
        /// The whole file.
        /// </summary>
        readonly byte[] _data = data;
        /// <summary>
        /// The read position in the file; reading past the end throws <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        public int Position;

        /// <summary>
        /// The next <paramref name="n"/> bytes.
        /// </summary>
        public ReadOnlySpan<byte> Bytes(int n)
        {
            var s = _data.AsSpan(Position, n);
            Position += n;
            return s;
        }

        /// <summary>
        /// Skips <paramref name="count"/> records of <paramref name="size"/> bytes.
        /// </summary>
        public void Skip(int count, int size) => Bytes(checked(count * size));
        /// <summary>
        /// Reads one byte.
        /// </summary>
        public byte Byte() => Bytes(1)[0];
        /// <summary>
        /// Reads a little-endian int32.
        /// </summary>
        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        /// <summary>
        /// Reads a little-endian float.
        /// </summary>
        public float Float() => BinaryPrimitives.ReadSingleLittleEndian(Bytes(4));
        /// <summary>
        /// Reads three floats as x, y, z.
        /// </summary>
        public Vector3 Vector() => new(Float(), Float(), Float());
        /// <summary>
        /// Reads a record count; <see cref="FormatException"/> when it is negative or above <paramref name="max"/>.
        /// </summary>
        public int Count(int max = 100_000)
        {
            int n = Int();
            if (n < 0 || n > max) throw new FormatException($"Bad count {n}.");
            return n;
        }

        /// <summary>
        /// Reads an int32-length-prefixed Latin-1 string (at most 4096 bytes), cut at its first NUL.
        /// </summary>
        public string String()
        {
            int length = Int();
            if (length < 0 || length > 4096) throw new FormatException($"Bad string length {length}.");
            var bytes = Bytes(length);
            int nul = bytes.IndexOf((byte)0);
            return Encoding.Latin1.GetString(nul < 0 ? bytes : bytes[..nul]);
        }
    }
}

/// <summary>
/// An actor: one rigid body (none when <see cref="Static"/>), at <see cref="Position"/> in model space with no rotation of its own. In a ragdoll the
/// name is the bone's and the position the bone's binding position. <see cref="AngularDamping"/> is the value the game uses (the file stores a tenth
/// of it).
/// </summary>
public sealed record PhysicsBodyActor(string Name, Vector3 Position, bool Static, float Mass, float Density, float LinearDamping, float AngularDamping,
    Vector3? CentreOfMass, bool NoGravity, string? Bone)
{
    /// <summary>
    /// The actor's collision shapes in file order.
    /// </summary>
    public List<PhysicsShape> Shapes { get; } = [];
}

/// <summary>
/// A collision shape in model space: its centre and rotation, and its size as <see cref="PhysicsShapeType"/> reads it.
/// </summary>
public sealed record PhysicsShape(PhysicsShapeType Type, Vector3 Position, Quaternion Rotation, Vector3 Size, float Mass, float Density,
    float SkinWidth, string CookedFile, bool Convex);

/// <summary>
/// A joint spring: target (degrees for a revolute joint), spring and damper.
/// </summary>
public readonly record struct PhysicsSpring(float Target, float Spring, float Damper);

/// <summary>
/// A joint between two actors (indices into <see cref="PhysicsFile.Actors"/>; -1 or a static actor is the world), anchored at <see cref="Anchor"/> in
/// model space. Revolute: rotation about <see cref="Axis"/> from <see cref="Low"/> to <see cref="High"/> degrees. Spherical: twist about
/// <see cref="Axis"/> from <see cref="Low"/> to
/// <see cref="High"/>, swing up to <see cref="Swing"/> degrees. The limits apply only when <see cref="LimitsOn"/>.
/// </summary>
public sealed record PhysicsJoint(PhysicsJointType Type, int Actor0, int Actor1, Vector3 Anchor, Vector3 Normal, Vector3 Axis, bool LimitsOn, float Low,
    float High, float Swing, bool Collide, PhysicsSpring? Spring1, PhysicsSpring? Spring2, PhysicsSpring? Spring3);