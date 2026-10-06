using System.Runtime.InteropServices;

using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Vulkan.Shaders;

namespace Meitou.Tests.Vulkan;

/// <summary>The native model's shader text (docs/renderer-native.md 3.3, <see cref="NativeShaders"/>): the ported shared text compiles with
/// strict rules, keeps every body, and its blocks match the C# structs.</summary>
public class NativeShaderTests
{
    static VulkanDevice? TryCreate()
    {
        try { return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true }); }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException) { return null; }
    }

    static readonly Dictionary<string, string> Model = new() { ["uModel"] = "mat4(1.0)" };

    /// <summary>Each shared native variant with a vertex or fragment partner, as a consumer pairs them.</summary>
    static IEnumerable<(string Name, string Vertex, string Fragment, Type Push)> Variants()
    {
        string mesh = NativeShaders.MeshVertex(Model);
        yield return ("mesh", mesh, NativeShaders.MeshFragment(), typeof(MeshPush));
        yield return ("mesh depth", mesh, NativeShaders.MeshDepthFragment(), typeof(MeshPush));
        yield return ("mesh plain depth", mesh, NativeShaders.DepthFragment, typeof(MeshPush));
        const string fragment = """
            layout(location = 0) in vec2 vUv;
            layout(location = 0) out vec4 fragColour;
            void main() { fragColour = vec4(atmoApply(vec3(vUv, 0.0), view.eye, vec3(1.0)), kenshiShadow(vec3(vUv, 1.0), vec3(0.0, 1.0, 0.0))); }
            """;
        yield return ("post vertex + atmosphere", NativeShaders.PostProcessVertex,
            "#version 450\n" + NativeShaders.Prelude(NativeShaders.MeshPushMembers) + NativeShaders.AtmosphereFunctions + fragment, typeof(MeshPush));
        // The first consumer, the foliage (step O).
        yield return ("foliage mesh", FoliageShaders.MeshVertexNative(), FoliageShaders.MeshFragmentNative(), typeof(MeshPush));
        yield return ("foliage mesh depth", FoliageShaders.MeshVertexNative(), FoliageShaders.MeshDepthNative(), typeof(MeshPush));
        yield return ("foliage grass", FoliageShaders.GrassVertexNative(), FoliageShaders.GrassFragmentNative(), typeof(GrassPush));
        yield return ("foliage grass motion", FoliageShaders.GrassMotionVertexNative(), FoliageShaders.GrassMotionFragmentNative(), typeof(GrassPush));
        // Objects (step O): the buildings' dithered cross-fade on the shared mesh text, with their own push block.
        yield return ("objects", BuildingLodShaders.VertexNative(), BuildingLodShaders.FragmentNative(), typeof(ObjectPush));
        yield return ("objects depth", BuildingLodShaders.VertexNative(), BuildingLodShaders.DepthNative(), typeof(ObjectPush));
        // The terrain (step O): the patches, the TERRAIN-mode meshes, and their shadow depth.
        yield return ("terrain patch", TerrainShaders.PatchVertexNative(), TerrainShaders.FragmentNative(), typeof(TerrainPush));
        yield return ("terrain patch depth", TerrainShaders.PatchVertexNative(), TerrainShaders.DepthFragmentNative(), typeof(TerrainPush));
        yield return ("terrain mesh", TerrainShaders.MeshVertexNative(), TerrainShaders.MeshFragmentNative(), typeof(TerrainPush));
        yield return ("terrain mesh depth", TerrainShaders.MeshInstancedDepthVertexNative(), TerrainShaders.DepthFragmentNative(), typeof(TerrainPush));
    }

    [Fact]
    public void Port_keeps_bodies_and_maps_every_uniform()
    {
        string legacy = Shaders.MeshFragment, native = NativeShaders.MeshFragment();
        Assert.DoesNotContain(native.Split('\n'), l => l.TrimStart().StartsWith("uniform ", StringComparison.Ordinal));   // no loose uniform left
        Assert.Contains($"layout(std140, set = {NativeShaders.FrameSet}, binding = 1) uniform KenshiShadowReceiver", native);
        Assert.Contains("#define uDiffuse textures2D[pc.diffuse]", native);
        // Every line of the legacy text that is not a declaration appears unchanged, in order.
        int at = 0;
        foreach (var line in legacy.Split('\n'))
        {
            string t = line.TrimStart();
            if (t.StartsWith("uniform ", StringComparison.Ordinal) || t.StartsWith("layout(std140)", StringComparison.Ordinal) || t.StartsWith("#version", StringComparison.Ordinal)) continue;
            int found = native.IndexOf(line, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"line missing or out of order: {line}");
            at = found + line.Length;
        }
        Assert.Throws<InvalidOperationException>(() => NativeShaders.Port("#version 330 core\nuniform float uUnknown;\nvoid main() {}\n"));
    }

    [Fact]
    [Slow]
    public void Native_variants_compile_and_their_blocks_match_the_CSharp_structs()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            using var frame = new NativeFrame(gl.Context);
            foreach (var (name, v, f, pushType) in Variants())
            {
                using var p = frame.Program(v, f, name);
                foreach (var r in new[] { p.VertexReflection!, p.FragmentReflection! })
                    foreach (var b in r.Blocks)
                    {
                        Type? type = b.Name switch
                        {
                            "FrameConstants" => typeof(FrameConstants), "ViewConstants" => typeof(ViewConstants), "Push" => pushType,
                            "TerrainConstants" => typeof(TerrainConstants), _ => null,
                        };
                        if (b.Name is "FrameConstants" or "ViewConstants" or "MeshBones" or "KenshiShadowReceiver" or "KenshiShadowCaster" or "MeitouShadowReceiver")
                            Assert.Equal(NativeShaders.FrameSet, b.Set);
                        if (b.Name is "TerrainConstants") Assert.Equal(TerrainShaders.ConstantsSet, b.Set);
                        if (type is null) continue;
                        foreach (var m in b.Members)
                        {
                            string field = char.ToUpperInvariant(m.Name[0]) + m.Name[1..];
                            Assert.True(type.GetField(field) is not null, $"{name}: {b.Name}.{m.Name} has no C# field {field}");
                            Assert.True((int)Marshal.OffsetOf(type, field) == m.Offset, $"{name}: {b.Name}.{m.Name} at {m.Offset}, C# {Marshal.OffsetOf(type, field)}");
                        }
                        Assert.True(b.Size <= Marshal.SizeOf(type), $"{name}: {b.Name} is {b.Size} bytes, C# {Marshal.SizeOf(type)}");
                    }
                foreach (var s in p.VertexReflection!.Samplers.Concat(p.FragmentReflection!.Samplers)) Assert.Equal(NativeShaders.BindlessSet, s.Set);
            }
        }
        Assert.True(d!.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));
    }
}
