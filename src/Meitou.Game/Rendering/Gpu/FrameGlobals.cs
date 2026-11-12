using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// Frame-global resources by GLSL name (docs/renderer-native.md 4.3): the textures, uniform blocks and loose uniform values that reach every program
/// of the frame (the atmosphere, the shadow maps and blocks, the terrain heights). Owners publish a getter once; consumers read the current value
/// when they draw (<see cref="LegacyProgram"/> takes every name it finds in its reflection and was not given explicitly). While an owner is on VkGl
/// its getters go through the interop's export calls; after its port they return native objects. Render thread only.
/// </summary>
public sealed class FrameGlobals
{
    #region Publishing

    /// <summary>
    /// The published texture getters, by GLSL sampler name.
    /// </summary>
    readonly Dictionary<string, Func<SampledTexture>> _textures = [];
    /// <summary>
    /// The published uniform block getters, by GLSL block name.
    /// </summary>
    readonly Dictionary<string, Func<GpuBufferBinding>> _blocks = [];
    /// <summary>
    /// The published loose uniforms, by GLSL uniform name.
    /// </summary>
    readonly Dictionary<string, Uniform> _uniforms = [];
    /// <summary>
    /// The block getters that replace published ones while set (<see cref="OverrideBlock"/>).
    /// </summary>
    readonly Dictionary<string, Func<GpuBufferBinding>> _blockOverrides = [];

    /// <summary>
    /// Counts <see cref="LegacyProgram.ApplyGlobals"/> calls. Nothing an owner reads changes during one call, so an owner whose getters share one
    /// computation (<c>SkyRenderer</c>'s atmosphere values) may keep its result for as long as this number stays the same.
    /// </summary>
    public int ApplyCount { get; internal set; }

    /// <summary>
    /// Bumped by every publish (consumers re-resolve their names).
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Publishes a texture getter under a sampler name (replacing an earlier one).
    /// </summary>
    public void Publish(string name, Func<SampledTexture> texture)
    {
        _textures[name] = texture;
        Version++;
    }

    /// <summary>
    /// Publishes a uniform block getter under a block name (replacing an earlier one).
    /// </summary>
    public void Publish(string name, Func<GpuBufferBinding> block)
    {
        _blocks[name] = block;
        Version++;
    }

    /// <summary>
    /// A loose uniform: float, int, Vector2, Vector3, Vector4 or Matrix4x4. With <paramref name="when"/>, written only while it is true.
    /// </summary>
    public void PublishUniform<T>(string name, Func<T> value, Func<bool>? when = null) where T : unmanaged
    {
        if (typeof(T) != typeof(float) && typeof(T) != typeof(int) && typeof(T) != typeof(Vector2) && typeof(T) != typeof(Vector3) &&
            typeof(T) != typeof(Vector4) && typeof(T) != typeof(Matrix4x4))
            throw new NotSupportedException($"frame-global uniform of type {typeof(T).Name}");

        _uniforms[name] = new Uniform<T>(value, when);
        Version++;
    }

    /// <summary>
    /// A loose <c>vec4</c> array uniform (<c>uniform vec4 name[storage.Length]</c>): its first <paramref name="count"/>() elements are written, the
    /// rest keeps whatever it held (the shaders read only the used part).
    /// </summary>
    public void PublishUniformArray(string name, Vector4[] storage, Func<int> count)
    {
        _uniforms[name] = new ArrayUniform(storage, count);
        Version++;
    }

    /// <summary>
    /// A reflected-sampler description for publishing by unit: 2D (or cube, or 2D array), float, optionally a shadow (compare) sampler.
    /// </summary>
    public static SamplerInfo Sampler2D(string name, bool cube = false, bool shadow = false, bool arrayed = false) =>
        new(name, 0, 0, cube ? SamplerDimension.Cube : SamplerDimension.Dim2D, arrayed, false, shadow, ScalarKind.Float, 0);

    #endregion

    #region Lookup

    /// <summary>
    /// The texture getter published under <paramref name="name"/>, or null.
    /// </summary>
    public Func<SampledTexture>? Texture(string name) => _textures.GetValueOrDefault(name);

    /// <summary>
    /// A getter for the block published under <paramref name="name"/> that applies its override while one is set, or null when none is published.
    /// </summary>
    public Func<GpuBufferBinding>? Block(string name)
    {
        var published = _blocks.GetValueOrDefault(name);
        if (published is null) return null;

        // An override (OverrideBlock) is looked up at each read, so it applies to the segments prepared while it is set.
        return () => _blockOverrides.Count > 0 && _blockOverrides.TryGetValue(name, out var over) ? over() : published();
    }

    /// <summary>
    /// Replaces a published block's value for the segments prepared until it is cleared (<c>null</c>): a pass that needs the frame's blocks
    /// otherwise, as the water reflection's unshadowed receivers. Does not change <see cref="Version"/>; the name must be published.
    /// </summary>
    public void OverrideBlock(string name, Func<GpuBufferBinding>? getter)
    {
        if (getter is null) _blockOverrides.Remove(name);
        else _blockOverrides[name] = getter;
    }

    /// <summary>
    /// The loose uniform published under <paramref name="name"/>, or null.
    /// </summary>
    public Uniform? UniformValue(string name) => _uniforms.GetValueOrDefault(name);

    /// <summary>
    /// The published texture names.
    /// </summary>
    public IEnumerable<string> TextureNames => _textures.Keys;

    /// <summary>
    /// The published block names.
    /// </summary>
    public IEnumerable<string> BlockNames => _blocks.Keys;

    /// <summary>
    /// The published loose uniform names.
    /// </summary>
    public IEnumerable<string> UniformNames => _uniforms.Keys;

    #endregion

    #region Uniform values

    /// <summary>
    /// A loose uniform's getter, typed by what <see cref="LegacyProgram.Set(UniformHandle, float)"/> and its overloads take.
    /// </summary>
    public abstract class Uniform
    {
        /// <summary>
        /// Sets the value on <paramref name="program"/>'s uniform, unless the owner would not set it now.
        /// </summary>
        internal abstract void Write(LegacyProgram program, UniformHandle handle);

        /// <summary>
        /// The value's bytes into <paramref name="destination"/> (its exact size), for a native constant block; false (nothing written) while the
        /// owner would not set it, as <see cref="Write"/> skips it then.
        /// </summary>
        internal abstract bool TryRead(Span<byte> destination);
    }

    /// <summary>
    /// A loose uniform of one value type, with an optional condition for writing it.
    /// </summary>
    sealed class Uniform<T>(Func<T> get, Func<bool>? when) : Uniform where T : unmanaged
    {
        internal override void Write(LegacyProgram program, UniformHandle handle)
        {
            if (when is not null && !when()) return;   // the owner would not have set it (as SkyRenderer.Apply before the sky is valid)

            var v = get();

            // By type test on T, not a switch on the value (which would box it on every write).
            if (typeof(T) == typeof(float)) program.Set(handle, Unsafe.As<T, float>(ref v));
            else if (typeof(T) == typeof(int)) program.Set(handle, Unsafe.As<T, int>(ref v));
            else if (typeof(T) == typeof(Vector2)) program.Set(handle, Unsafe.As<T, Vector2>(ref v));
            else if (typeof(T) == typeof(Vector3)) program.Set(handle, Unsafe.As<T, Vector3>(ref v));
            else if (typeof(T) == typeof(Vector4)) program.Set(handle, Unsafe.As<T, Vector4>(ref v));
            else if (typeof(T) == typeof(Matrix4x4)) program.Set(handle, in Unsafe.As<T, Matrix4x4>(ref v));
            else throw new NotSupportedException($"frame-global uniform of type {typeof(T).Name}");
        }

        internal override bool TryRead(Span<byte> destination)
        {
            if (when is not null && !when()) return false;

            if (destination.Length != Unsafe.SizeOf<T>())
                throw new ArgumentException($"frame-global uniform of type {typeof(T).Name} is {Unsafe.SizeOf<T>()} bytes, not {destination.Length}");

            var v = get();
            System.Runtime.InteropServices.MemoryMarshal.Write(destination, in v);
            return true;
        }
    }

    /// <summary>
    /// A loose <c>vec4</c> array: the first <c>count()</c> elements of <paramref name="storage"/> (the rest is never read by its shaders).
    /// </summary>
    sealed class ArrayUniform(Vector4[] storage, Func<int> count) : Uniform
    {
        internal override void Write(LegacyProgram program, UniformHandle handle)
        {
            int n = Math.Clamp(count(), 0, storage.Length);
            if (n > 0) program.Set(handle, System.Runtime.InteropServices.MemoryMarshal.Cast<Vector4, float>(storage.AsSpan(0, n)), 4);
        }

        internal override bool TryRead(Span<byte> destination)
        {
            if (destination.Length != storage.Length * 16)
                throw new ArgumentException($"frame-global vec4[{storage.Length}] is {storage.Length * 16} bytes, not {destination.Length}");

            int n = Math.Clamp(count(), 0, storage.Length);
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(storage.AsSpan(0, n)).CopyTo(destination);
            return true;
        }
    }

    #endregion
}
