using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

public class ParticleSliderTests
{
    [Fact]
    public void Weather_particles_slider_reads_the_renderer_state()
    {
        var o = PostOptions.Create("meitou");
        Assert.Equal(3, WorldFrame.WeatherParticlesSlider(true, o));   // Meitou default: by sprite size
        Assert.Equal(0, WorldFrame.WeatherParticlesSlider(false, o));
        o.ParticleDivisor = 4;
        Assert.Equal(1, WorldFrame.WeatherParticlesSlider(true, o));
        o.ParticleDivisor = 2;
        Assert.Equal(2, WorldFrame.WeatherParticlesSlider(true, o));
        o.ParticleDivisor = 1;
        Assert.Equal(4, WorldFrame.WeatherParticlesSlider(true, o));
        (o.ParticleDivisor, o.LowResParticles) = (0, false);
        Assert.Equal(4, WorldFrame.WeatherParticlesSlider(true, o));   // Faithful: all full size
    }
}
