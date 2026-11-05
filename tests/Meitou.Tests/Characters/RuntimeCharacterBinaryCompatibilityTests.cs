using Meitou.Data.Ogre;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;

namespace Meitou.Tests.Characters;

public sealed class RuntimeCharacterBinaryCompatibilityTests
{
    delegate byte[] Pack(ReadOnlySpan<Vertex> vertices);

    [Fact]
    public void Existing_model_and_character_upload_CLR_signatures_remain_available_to_compiled_mods()
    {
        Func<OgreMesh, int?, bool, bool, Model> build = Model.Build;
        Pack pack = GpuVertex.PackCharacter;
        Assert.Equal(4, build.Method.GetParameters().Length);
        Assert.Single(pack.Method.GetParameters());
        Assert.NotNull(typeof(GpuVertex).GetMethod(nameof(GpuVertex.CharacterAttributes), [typeof(DeviceBuffer)]));
        Assert.Equal(GpuVertex.PackCharacter(ReadOnlySpan<Vertex>.Empty, false), pack(ReadOnlySpan<Vertex>.Empty));
    }
}
