using Meitou.Rendering;
using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Tests.Vulkan;

/// <summary>The merged post kernels (<c>post-merge</c>, docs/render-post.md "Merged passes"): storage images in the reflection, and the kernels' bindings as <c>PostProcess.Merged.cs</c> pushes them.</summary>
public class MergedKernelTests
{
    static readonly GlslProgramCompiler Compiler = new(new ShaderCompileOptions { UseDiskCache = false });

    [Fact]
    [Slow]
    public void Storage_images_are_reflected_with_their_bindings()
    {
        const string glsl = "#version 450\nlayout(local_size_x = 8) in;\nlayout(set = 0, binding = 3, rgba16f) writeonly uniform image2D a;\nlayout(set = 0, binding = 5, r32f) uniform image2D b;\nlayout(set = 0, binding = 1) uniform sampler2D s;\nlayout(set = 0, binding = 7, r32f) uniform image2D unused;\nvoid main() { imageStore(a, ivec2(0), texelFetch(s, ivec2(0), 0)); imageStore(b, ivec2(1), imageLoad(b, ivec2(0))); }\n";
        var r = SpirvReflection.Parse(Compiler.CompileCompute(glsl));
        Assert.Equal([(3, "a"), (5, "b")], r.StorageImages.OrderBy(i => i.Binding).Select(i => (i.Binding, i.Name)).ToArray());
        Assert.Equal(["s"], r.Samplers.Select(s => s.Name).ToArray());
        Assert.Contains("unused", r.Inactive);
    }

    [Fact]
    [Slow]
    public void The_velocity_kernel_has_two_depths_three_images_and_a_parameter_block()
    {
        var r = SpirvReflection.Parse(Compiler.CompileCompute(MergedShaders.Velocity));
        Assert.Equal([0, 1], r.Samplers.Select(s => s.Binding).Order().ToArray());
        Assert.Equal([2, 3, 4], r.StorageImages.Select(i => i.Binding).Order().ToArray());
        Assert.Contains(r.Blocks, b => b.Binding == 5 && b.Kind == BlockKind.StorageBuffer);
        Assert.Equal(8, r.Blocks.Single(b => b.Kind == BlockKind.PushConstant).Size);
    }

    [Fact]
    [Slow]
    public void The_exposure_kernel_reads_the_scene_and_the_last_value_and_writes_the_adapted_one()
    {
        var r = SpirvReflection.Parse(Compiler.CompileCompute(MergedShaders.Exposure));
        Assert.Equal([0], r.Samplers.Select(s => s.Binding).ToArray());
        Assert.Equal([1, 2], r.StorageImages.Select(i => i.Binding).Order().ToArray());
        Assert.Contains(r.Blocks, b => b.Binding == 3 && b.Kind == BlockKind.StorageBuffer);
        Assert.Equal(12, r.Blocks.Single(b => b.Kind == BlockKind.PushConstant).Size);
    }
}
