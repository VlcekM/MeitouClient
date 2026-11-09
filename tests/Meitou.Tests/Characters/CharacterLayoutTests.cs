using System.Runtime.CompilerServices;
using Meitou.Game.Rendering.Characters;

namespace Meitou.Tests.Characters;

/// <summary>
/// The C# structs the character shaders read must match the GLSL layouts byte for byte (std430 / vertex rows / push constants).
/// </summary>
public class CharacterLayoutTests
{
    [Fact]
    public void Instance_is_144_bytes_and_the_material_record_256()
    {
        Assert.Equal(CharInstance.Size, Unsafe.SizeOf<CharInstance>());
        Assert.Equal(CharacterMaterialRecord.Size, Unsafe.SizeOf<CharacterMaterialRecord>());
    }
}
