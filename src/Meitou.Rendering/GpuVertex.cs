using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The vertex layouts of the mesh buffers: <see cref="Vertex"/> packed at upload (the CPU keeps the float layout). Normals, tangents and weights lie
/// in [-1, 1] (or [0, 1]) in every base-game mesh (docs/formats/ogre-mesh.md, "Vertex sharing"), so 16-bit normalized keeps them to 1/32767.
/// Object: position float3 @0, normal snorm16x4 @12, uv float2 @20, tangent snorm16x4 @28, colour half4 @36 (44 bytes; half because distant
/// buildings brighten their colours past 1 and limbs keep their curvature in Z). Character: the object's first four, colour float4 @36 (its X
/// carries the morph slot as raw bits, <see cref="Characters.CharacterMorphs"/>), bones uint8x4 @52, weights unorm16x4 @56 (64 bytes).
/// </summary>
public static class GpuVertex
{
    public const int ObjectSize = 44, CharacterSize = 64;
    /// <summary>Where the texture coordinates sit in both layouts (the GI shaders read them by address).</summary>
    public const uint UvOffset = 20;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Snorm4(Vector4 v)
    {
        public short X = S(v.X), Y = S(v.Y), Z = S(v.Z), W = S(v.W);
        static short S(float f) => (short)MathF.Round(Math.Clamp(f, -1f, 1f) * 32767f);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Half4(Vector4 v)
    {
        public Half X = (Half)v.X, Y = (Half)v.Y, Z = (Half)v.Z, W = (Half)v.W;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Unorm4(Vector4 v)
    {
        public ushort X = U(v.X), Y = U(v.Y), Z = U(v.Z), W = U(v.W);
        static ushort U(float f) => (ushort)MathF.Round(Math.Clamp(f, 0f, 1f) * 65535f);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct ObjectVertex
    {
        public Vector3 Position;
        public Snorm4 Normal;
        public Vector2 Uv;
        public Snorm4 Tangent;
        public Half4 Colour;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct CharacterVertex
    {
        public Vector3 Position;
        public Snorm4 Normal;
        public Vector2 Uv;
        public Snorm4 Tangent;
        public Vector4 Colour;
        public uint Bones;
        public Unorm4 Weights;
    }

    public static byte[] PackObject(ReadOnlySpan<Vertex> vertices)
    {
        var bytes = new byte[vertices.Length * ObjectSize];
        var packed = MemoryMarshal.Cast<byte, ObjectVertex>(bytes.AsSpan());
        for (int i = 0; i < vertices.Length; i++)
        {
            ref readonly var v = ref vertices[i];
            packed[i] = new ObjectVertex { Position = v.Position, Normal = new(new Vector4(v.Normal, 0)), Uv = v.Uv, Tangent = new(v.Tangent), Colour = new(v.Colour) };
        }
        return bytes;
    }

    public static byte[] PackCharacter(ReadOnlySpan<Vertex> vertices)
    {
        var bytes = new byte[vertices.Length * CharacterSize];
        var packed = MemoryMarshal.Cast<byte, CharacterVertex>(bytes.AsSpan());
        for (int i = 0; i < vertices.Length; i++)
        {
            ref readonly var v = ref vertices[i];
            packed[i] = new CharacterVertex
            {
                Position = v.Position, Normal = new(new Vector4(v.Normal, 0)), Uv = v.Uv, Tangent = new(v.Tangent), Colour = v.Colour, Bones = v.Bones, Weights = new(v.Weights),
            };
        }
        return bytes;
    }

    /// <summary>The object layout's attributes at locations 0 to 4 (position, normal, uv, tangent, colour) over <paramref name="vertices"/>.</summary>
    public static LegacyProgram.Attribute?[] ObjectAttributes(DeviceBuffer vertices) =>
    [
        Attribute(vertices, 0, InternalFormat.Rgb32f, ObjectSize), Attribute(vertices, 12, InternalFormat.Rgba16Snorm, ObjectSize),
        Attribute(vertices, 20, InternalFormat.RG32f, ObjectSize), Attribute(vertices, 28, InternalFormat.Rgba16Snorm, ObjectSize),
        Attribute(vertices, 36, InternalFormat.Rgba16f, ObjectSize),
    ];

    /// <summary>The character layout's attributes at locations 0 to 6 (the object's five, bones, weights) over <paramref name="vertices"/>.</summary>
    public static LegacyProgram.Attribute?[] CharacterAttributes(DeviceBuffer vertices) =>
    [
        Attribute(vertices, 0, InternalFormat.Rgb32f, CharacterSize), Attribute(vertices, 12, InternalFormat.Rgba16Snorm, CharacterSize),
        Attribute(vertices, 20, InternalFormat.RG32f, CharacterSize), Attribute(vertices, 28, InternalFormat.Rgba16Snorm, CharacterSize),
        Attribute(vertices, 36, InternalFormat.Rgba32f, CharacterSize), Attribute(vertices, 52, InternalFormat.Rgba8ui, CharacterSize),
        Attribute(vertices, 56, InternalFormat.Rgba16, CharacterSize),
    ];

    static LegacyProgram.Attribute Attribute(DeviceBuffer vertices, int offset, InternalFormat format, int stride) =>
        new(new GpuBufferBinding(vertices.Handle, (ulong)offset), format, (uint)stride, false);
}
