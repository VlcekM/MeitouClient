using System.Numerics;
using Meitou.Rendering.Vulkan.Shaders;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// Frame-global resources by GLSL name (docs/renderer-native.md 4.3): the textures, uniform blocks and loose uniform values that reach every
/// program of the frame (the atmosphere, the shadow maps and blocks, the terrain heights). Owners publish a getter once; consumers read the
/// current value when they draw (<see cref="LegacyProgram"/> takes every name it finds in its reflection and was not given explicitly).
/// While an owner is on VkGl its getters go through the interop's export calls; after its port they return native objects. Render thread only.
/// </summary>
public sealed class FrameGlobals
{
    readonly Dictionary<string, Func<SampledTexture>> textures = [];
    readonly Dictionary<string, Func<BufferBinding>> blocks = [];
    readonly Dictionary<string, Uniform> uniforms = [];

    /// <summary>A loose uniform's getter, typed by what <see cref="LegacyProgram.Set(UniformHandle, float)"/> and its overloads take.</summary>
    public abstract class Uniform
    {
        internal abstract void Write(LegacyProgram program, UniformHandle handle);
    }

    sealed class Uniform<T>(Func<T> get, Func<bool>? when) : Uniform where T : unmanaged
    {
        internal override void Write(LegacyProgram program, UniformHandle handle)
        {
            if (when is not null && !when()) return;   // the owner would not have set it (as SkyRenderer.Apply before the sky is valid)
            var v = get();
            switch (v)
            {
                case float f: program.Set(handle, f); break;
                case int i: program.Set(handle, i); break;
                case Vector2 v2: program.Set(handle, v2); break;
                case Vector3 v3: program.Set(handle, v3); break;
                case Vector4 v4: program.Set(handle, v4); break;
                case Matrix4x4 m: program.Set(handle, in m); break;
                default: throw new NotSupportedException($"frame-global uniform of type {typeof(T).Name}");
            }
        }
    }

    /// <summary>Bumped by every publish (consumers re-resolve their names).</summary>
    public int Version { get; private set; }

    public void Publish(string name, Func<SampledTexture> texture) { textures[name] = texture; Version++; }
    public void Publish(string name, Func<BufferBinding> block) { blocks[name] = block; Version++; }

    /// <summary>A loose uniform: float, int, Vector2, Vector3, Vector4 or Matrix4x4. With <paramref name="when"/>, written only while it is true.</summary>
    public void PublishUniform<T>(string name, Func<T> value, Func<bool>? when = null) where T : unmanaged
    {
        if (typeof(T) != typeof(float) && typeof(T) != typeof(int) && typeof(T) != typeof(Vector2) && typeof(T) != typeof(Vector3) &&
            typeof(T) != typeof(Vector4) && typeof(T) != typeof(Matrix4x4))
            throw new NotSupportedException($"frame-global uniform of type {typeof(T).Name}");
        uniforms[name] = new Uniform<T>(value, when);
        Version++;
    }

    /// <summary>A reflected-sampler description for publishing by unit: 2D (or cube), float, optionally a shadow (compare) sampler.</summary>
    public static SamplerInfo Sampler2D(string name, bool cube = false, bool shadow = false) =>
        new(name, 0, 0, cube ? SamplerDimension.Cube : SamplerDimension.Dim2D, false, false, shadow, ScalarKind.Float, 0);

    public Func<SampledTexture>? Texture(string name) => textures.GetValueOrDefault(name);
    public Func<BufferBinding>? Block(string name) => blocks.GetValueOrDefault(name);
    public Uniform? UniformValue(string name) => uniforms.GetValueOrDefault(name);

    public IEnumerable<string> TextureNames => textures.Keys;
    public IEnumerable<string> BlockNames => blocks.Keys;
    public IEnumerable<string> UniformNames => uniforms.Keys;
}
