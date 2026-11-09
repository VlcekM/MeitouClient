using System.Numerics;
using Meitou.Game.Rendering.Impostors;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The billboards' depth prepass and tight quad (<see cref="ImpostorPrepass"/>, <see cref="ImpostorQuad"/>): the texts must keep the prepass and the
/// colour pass that tests depth EQUAL in agreement.
/// </summary>
public class ImpostorPrepassTests
{
    [Theory]
    [InlineData(100f, 60f, 10f, 112f)]
    [InlineData(1f, 1f, 1f, 1.7f)]
    [InlineData(0f, 0f, 0f, 1f)]
    public void The_packed_half_extents_never_shrink_the_box(float x, float y, float z, float radius)
    {
        uint word = ImpostorQuad.Pack(new Vector3(x, y, z), radius);

        Assert.Equal(1u, word >> 30);

        float[] half = [x, y, z];
        for (int axis = 0; axis < 3; axis++)
        {
            float unpacked = ((word >> (10 * axis)) & 1023u) * (radius / 1023f);

            Assert.True(unpacked >= half[axis] - 1e-4f, $"axis {axis}: {unpacked} < {half[axis]}");
            Assert.True(unpacked <= half[axis] + radius / 1023f + 1e-4f, $"axis {axis}: {unpacked} is more than a step over {half[axis]}");
        }
    }
}
