using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The vertex layouts of the mesh buffers: <see cref="Vertex"/> packed at upload (the CPU keeps the float layout). Normals, tangents and weights lie
/// in [-1, 1] (or [0, 1]) in every base-game mesh (docs/formats/ogre-mesh.md, "Vertex sharing"), so 16-bit normalized keeps them to 1/32767.
/// Object: position float3 @0, normal snorm16x4 @12, uv float2 @20, tangent snorm16x4 @28, colour half4 @36 (44 bytes; half because distant
/// buildings brighten their colours past 1 and limbs keep their curvature in Z). Character: the object's first four, colour float4 @36 (its X
/// carries the morph slot as raw bits, <see cref="Characters.CharacterMorphs"/>), bones uint8x4 @52, weights unorm16x4 @56 (64 bytes).
/// </summary>
public static class GpuVertex
{
    /// <summary>
    /// The bytes of one vertex in the object layout and in the character layout.
    /// </summary>
    public const int ObjectSize = 44, CharacterSize = 64;

    /// <summary>
    /// Where the texture coordinates sit in both layouts (the GI shaders read them by address).
    /// </summary>
    public const uint UvOffset = 20;

    /// <summary>
    /// Four values in [-1, 1] as 16-bit signed normalized integers (a normal or tangent).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Snorm4(Vector4 v)
    {
        /// <summary>
        /// The components, each rounded to the nearest of -32767 .. 32767 after clamping to [-1, 1].
        /// </summary>
        public short X = S(v.X), Y = S(v.Y), Z = S(v.Z), W = S(v.W);

        /// <summary>
        /// <paramref name="f"/> clamped to [-1, 1] and scaled to a 16-bit signed normalized integer.
        /// </summary>
        static short S(float f) => (short)MathF.Round(Math.Clamp(f, -1f, 1f) * 32767f);
    }

    /// <summary>
    /// Four values as half floats (the object layout's colour).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Half4(Vector4 v)
    {
        /// <summary>
        /// The components as half floats.
        /// </summary>
        public Half X = (Half)v.X, Y = (Half)v.Y, Z = (Half)v.Z, W = (Half)v.W;
    }

    /// <summary>
    /// Four values in [0, 1] as 16-bit unsigned normalized integers (the bone weights).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Unorm4(Vector4 v)
    {
        /// <summary>
        /// The components, each rounded to the nearest of 0 .. 65535 after clamping to [0, 1].
        /// </summary>
        public ushort X = U(v.X), Y = U(v.Y), Z = U(v.Z), W = U(v.W);

        /// <summary>
        /// <paramref name="f"/> clamped to [0, 1] and scaled to a 16-bit unsigned normalized integer.
        /// </summary>
        static ushort U(float f) => (ushort)MathF.Round(Math.Clamp(f, 0f, 1f) * 65535f);
    }

    /// <summary>
    /// One vertex in the object layout (<see cref="ObjectSize"/> bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct ObjectVertex
    {
        /// <summary>
        /// The position, float3 at byte 0.
        /// </summary>
        public Vector3 Position;

        /// <summary>
        /// The normal, snorm16x4 at byte 12 (w 0).
        /// </summary>
        public Snorm4 Normal;

        /// <summary>
        /// The texture coordinates, float2 at byte 20.
        /// </summary>
        public Vector2 Uv;

        /// <summary>
        /// The tangent and its handedness, snorm16x4 at byte 28.
        /// </summary>
        public Snorm4 Tangent;

        /// <summary>
        /// The vertex colour, half4 at byte 36.
        /// </summary>
        public Half4 Colour;
    }

    /// <summary>
    /// One vertex in the character layout (<see cref="CharacterSize"/> bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct CharacterVertex
    {
        /// <summary>
        /// The position, float3 at byte 0.
        /// </summary>
        public Vector3 Position;

        /// <summary>
        /// The normal, snorm16x4 at byte 12 (w 0).
        /// </summary>
        public Snorm4 Normal;

        /// <summary>
        /// The texture coordinates, float2 at byte 20.
        /// </summary>
        public Vector2 Uv;

        /// <summary>
        /// The tangent and its handedness, snorm16x4 at byte 28.
        /// </summary>
        public Snorm4 Tangent;

        /// <summary>
        /// The vertex colour, float4 at byte 36 (its X carries the morph slot as raw bits).
        /// </summary>
        public Vector4 Colour;

        /// <summary>
        /// Four bone indices, one byte each, at byte 52.
        /// </summary>
        public uint Bones;

        /// <summary>
        /// The four bone weights, unorm16x4 at byte 56.
        /// </summary>
        public Unorm4 Weights;
    }

    /// <summary>
    /// <paramref name="vertices"/> packed in the object layout, for upload.
    /// </summary>
    public static byte[] PackObject(ReadOnlySpan<Vertex> vertices)
    {
        var bytes = new byte[vertices.Length * ObjectSize];
        var packed = MemoryMarshal.Cast<byte, ObjectVertex>(bytes.AsSpan());
        for (int i = 0; i < vertices.Length; i++)
        {
            ref readonly var v = ref vertices[i];
            packed[i] = new ObjectVertex
            {
                Position = v.Position,
                Normal = new(new Vector4(v.Normal, 0)),
                Uv = v.Uv,
                Tangent = new(v.Tangent),
                Colour = new(v.Colour),
            };
        }
        return bytes;
    }

    /// <summary>
    /// <paramref name="vertices"/> packed in the character layout, for upload.
    /// </summary>
    public static byte[] PackCharacter(ReadOnlySpan<Vertex> vertices)
    {
        var bytes = new byte[vertices.Length * CharacterSize];
        var packed = MemoryMarshal.Cast<byte, CharacterVertex>(bytes.AsSpan());
        for (int i = 0; i < vertices.Length; i++)
        {
            ref readonly var v = ref vertices[i];
            packed[i] = new CharacterVertex
            {
                Position = v.Position,
                Normal = new(new Vector4(v.Normal, 0)),
                Uv = v.Uv,
                Tangent = new(v.Tangent),
                Colour = v.Colour,
                Bones = v.Bones,
                Weights = new(v.Weights),
            };
        }
        return bytes;
    }

    /// <summary>
    /// The object layout's attributes at locations 0 to 4 (position, normal, uv, tangent, colour) over <paramref name="vertices"/>.
    /// </summary>
    public static LegacyProgram.Attribute?[] ObjectAttributes(DeviceBuffer vertices) =>
    [
        Attribute(vertices, 0, InternalFormat.Rgb32F, ObjectSize), Attribute(vertices, 12, InternalFormat.Rgba16Snorm, ObjectSize),
        Attribute(vertices, 20, InternalFormat.Rg32F, ObjectSize), Attribute(vertices, 28, InternalFormat.Rgba16Snorm, ObjectSize),
        Attribute(vertices, 36, InternalFormat.Rgba16F, ObjectSize),
    ];

    /// <summary>
    /// The character layout's attributes at locations 0 to 6 (the object's five, bones, weights) over <paramref name="vertices"/>.
    /// </summary>
    public static LegacyProgram.Attribute?[] CharacterAttributes(DeviceBuffer vertices) =>
    [
        Attribute(vertices, 0, InternalFormat.Rgb32F, CharacterSize), Attribute(vertices, 12, InternalFormat.Rgba16Snorm, CharacterSize),
        Attribute(vertices, 20, InternalFormat.Rg32F, CharacterSize), Attribute(vertices, 28, InternalFormat.Rgba16Snorm, CharacterSize),
        Attribute(vertices, 36, InternalFormat.Rgba32F, CharacterSize), Attribute(vertices, 52, InternalFormat.Rgba8Ui, CharacterSize),
        Attribute(vertices, 56, InternalFormat.Rgba16, CharacterSize),
    ];

    /// <summary>
    /// A per-vertex attribute at byte <paramref name="offset"/> of each <paramref name="stride"/>-byte vertex in <paramref name="vertices"/>.
    /// </summary>
    static LegacyProgram.Attribute Attribute(DeviceBuffer vertices, int offset, InternalFormat format, int stride) =>
        new(new GpuBufferBinding(vertices.Handle, (ulong)offset), format, (uint)stride, false);
}