using System.Numerics;
using System.Text;

namespace Meitou.Data.Characters;

/// <summary>
/// Reads the attachment points of a Scythe physics file (<c>.phs</c>), e.g. RACE <c>attachment points</c>
/// (<c>data/ragdoll/attachments-weapons.phs</c>). Only files whose actors carry no collision shapes are supported
/// (attachment files; ragdolls have shapes whose record size is not known). Layout: docs/characters.md.
/// </summary>
public sealed class PhysicsAttachmentFile
{
    public float Version { get; private init; }
    public List<PhysicsActor> Actors { get; } = [];
    /// <summary>Attachment points stored outside any actor (version 1.4+), e.g. the preview skeleton mesh.</summary>
    public List<PhysicsAttachmentPoint> LoosePoints { get; } = [];

    public static PhysicsAttachmentFile ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static PhysicsAttachmentFile Read(Stream stream)
    {
        var r = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        try
        {
            var file = new PhysicsAttachmentFile { Version = r.ReadSingle() };
            float v = file.Version;
            int actors = r.ReadInt32();
            if (actors < 0 || actors > 10_000) throw new FormatException($"Bad actor count {actors}.");
            for (int i = 0; i < actors; i++) file.Actors.Add(ReadActor(r, v));
            if (v >= 1.4f)
                for (int n = Count(r); n > 0; n--) file.LoosePoints.Add(ReadPoint(r));
            Skip(r, Count(r), v >= 1.2f ? 0x264 : 0x138); // joints
            Skip(r, Count(r), 0x38c);
            if (Count(r) > 0) throw new NotSupportedException("Physics file has loose shapes (record size unknown).");
            for (int n = Count(r); n > 0; n--) file.LoosePoints.Add(ReadPoint(r));
            Skip(r, Count(r), 0x7c);
            return file;
        }
        catch (EndOfStreamException e)
        {
            throw new FormatException("Unexpected end of physics file.", e);
        }
    }

    static PhysicsActor ReadActor(BinaryReader r, float v)
    {
        int shapes = r.ReadInt32(), points = r.ReadInt32(), extras = r.ReadInt32();
        if (shapes < 0 || points < 0 || extras < 0 || points > 10_000 || extras > 10_000) throw new FormatException("Bad actor counts.");
        r.ReadInt32();
        var position = ReadVector(r);
        r.ReadBytes(4 + 4 + 1 + 4 + 4 + 4 + 32 + 4 + 8 + 12 + 36); // physical properties, not needed here
        if (v >= 1.1f) r.ReadByte();
        string name = "Actor";
        if (v >= 1.2f)
        {
            name = ReadString(r);
            r.ReadBytes(2);
        }
        if (v >= 1.3f) r.ReadInt32();
        string? bone = null;
        var bonePosition = Vector3.Zero;
        var boneRotation = Quaternion.Identity;
        if (v >= 1.4f)
        {
            r.ReadInt32();
            bone = ReadString(r);
            bonePosition = ReadVector(r);
            boneRotation = FromColumns(ReadVector(r), ReadVector(r), ReadVector(r));
            r.ReadByte();
        }
        if (shapes > 0) throw new NotSupportedException($"Actor '{name}' has {shapes} collision shapes (record size unknown).");
        var actor = new PhysicsActor(name, position, bone is { Length: > 0 } ? bone : null, bonePosition, boneRotation);
        for (int i = 0; i < points; i++) actor.Points.Add(ReadPoint(r));
        Skip(r, extras, 0x7c);
        return actor;
    }

    static PhysicsAttachmentPoint ReadPoint(BinaryReader r)
    {
        var rotation = FromColumns(ReadVector(r), ReadVector(r), ReadVector(r));
        var position = ReadVector(r);
        r.ReadBytes(4 + 32);
        string mesh = ReadString(r);
        var scale = ReadVector(r);
        return new PhysicsAttachmentPoint(position, rotation, mesh, scale);
    }

    static int Count(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 100_000) throw new FormatException($"Bad count {n}.");
        return n;
    }

    static void Skip(BinaryReader r, int count, int size)
    {
        if (count > 0) r.ReadBytes(checked(count * size));
    }

    static string ReadString(BinaryReader r)
    {
        int length = r.ReadInt32();
        if (length < 0 || length > 4096) throw new FormatException($"Bad string length {length}.");
        var bytes = r.ReadBytes(length);
        if (bytes.Length < length) throw new EndOfStreamException();
        int nul = Array.IndexOf(bytes, (byte)0);
        return Encoding.Latin1.GetString(bytes, 0, nul < 0 ? bytes.Length : nul);
    }

    static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    /// <summary>Rotation whose matrix has the three vectors as columns (column-vector convention, as Ogre's Matrix3).</summary>
    public static Quaternion FromColumns(Vector3 x, Vector3 y, Vector3 z)
    {
        // System.Numerics uses row vectors: the rotation matrix there is the transpose, i.e. rows = the columns above.
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }
}

/// <summary>An actor of a physics file; for attachment files the name is the attachment slot ("hip", "back").</summary>
public sealed record PhysicsActor(string Name, Vector3 Position, string? Bone, Vector3 BonePosition, Quaternion BoneRotation)
{
    public List<PhysicsAttachmentPoint> Points { get; } = [];

    /// <summary>
    /// Offset of <paramref name="point"/> from the bone, as Kenshi computes it when it loads attachment points: both
    /// relative to the bone transform stored in the file (model space at export), not the skeleton's.
    /// </summary>
    public (Vector3 Position, Quaternion Rotation) Offset(PhysicsAttachmentPoint point)
    {
        var inverse = Quaternion.Inverse(BoneRotation);
        return (Vector3.Transform(point.Position - BonePosition, inverse), Quaternion.Normalize(inverse * point.Rotation));
    }
}

/// <summary>A point in model space (binding pose), with the mesh the editor previews there.</summary>
public sealed record PhysicsAttachmentPoint(Vector3 Position, Quaternion Rotation, string Mesh, Vector3 Scale);
