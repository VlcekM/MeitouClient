using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The weather's surface plumbing (docs/formats/weather.md "Rain and wetness", "Dust"): the shared values the shaders read.</summary>
public class WeatherSurfaceTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(25f, 0.5f)]
    [InlineData(50f, 1f)]
    [InlineData(100f, 1f)]
    [InlineData(-5f, 0f)]
    public void RainAmountIsRainOverFiftyClamped(float rain, float expected) =>
        Assert.Equal(expected, WeatherSurfaces.RainAmount(rain));

    [Fact]
    public void FrameConstantsCarryTheWeatherValues()
    {
        var uniforms = FrameConstants.Uniforms.ToDictionary(u => u.Name);
        Assert.Equal((240, 16), (uniforms["uWeatherWet"].Offset, uniforms["uWeatherWet"].Size));
        Assert.Equal((256, 16), (uniforms["uWeatherDust"].Offset, uniforms["uWeatherDust"].Size));
        var textures = FrameConstants.Textures.ToDictionary(t => t.Name);
        Assert.Equal(272, textures["uWeatherGround"].Offset);
        Assert.Equal(276, textures["uWeatherDustNoise"].Offset);
        // The block is a multiple of 16 bytes after its last member (std140).
        Assert.Equal(0, System.Runtime.CompilerServices.Unsafe.SizeOf<FrameConstants>() % 16);
        Assert.True(System.Runtime.CompilerServices.Unsafe.SizeOf<FrameConstants>() >= 280);
    }

    [Fact]
    public void MeshSurfaceBitsAreDistinct()
    {
        uint[] bits = [MeshSurface.Dust, MeshSurface.Foliage, MeshSurface.NoWeather, MeshSurface.Interior];
        Assert.Equal(bits.Length, bits.Distinct().Count());
        Assert.All(bits, b => Assert.Equal(1, System.Numerics.BitOperations.PopCount(b)));
    }
}
