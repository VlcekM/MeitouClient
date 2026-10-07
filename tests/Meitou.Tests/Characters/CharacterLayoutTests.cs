using System.Runtime.CompilerServices;
using Meitou.Rendering.Characters;

namespace Meitou.Tests.Characters;

/// <summary>The C# structs the character shaders read must match the GLSL layouts byte for byte (std430 / vertex rows / push constants).</summary>
public class CharacterLayoutTests
{
    [Fact]
    public void Instance_is_144_bytes() => Assert.Equal(CharInstance.Size, Unsafe.SizeOf<CharInstance>());

    [Fact]
    public void Material_record_is_256_bytes() => Assert.Equal(CharacterMaterialRecord.Size, Unsafe.SizeOf<CharacterMaterialRecord>());

    [Fact]
    public void Push_block_is_16_bytes() => Assert.Equal(16, Unsafe.SizeOf<CharacterPush>());

    [Fact]
    public void Texture_slots_cover_the_used_slots_and_the_shader_array()
    {
        Assert.True(CharacterShaders.Slots <= CharacterShaders.TextureSlotCount);
        Assert.Equal(CharacterShaders.TextureSlotCount, Unsafe.SizeOf<TextureSlots>() / sizeof(uint));
        Assert.Contains($"uint tex[{CharacterShaders.TextureSlotCount}];", CharacterShaders.Fragment());
    }

    [Fact]
    public void Storage_buffers_sit_at_their_named_bindings()
    {
        Assert.Contains($"set = 0, binding = {CharacterShaders.BonesBinding}) readonly buffer Skin", CharacterShaders.Vertex());
        Assert.Contains($"set = 0, binding = {CharacterShaders.MorphsBinding}) readonly buffer Morphs", CharacterShaders.Vertex());
        Assert.Contains($"set = 0, binding = {CharacterShaders.MaterialsBinding}) readonly buffer Materials", CharacterShaders.Fragment());
        Assert.Contains($"set = 0, binding = {CharacterShaders.PreviousBonesBinding}) readonly buffer PreviousSkin", CharacterShaders.MotionVertex());
        Assert.DoesNotContain("PreviousSkin", CharacterShaders.Vertex());
    }
}
