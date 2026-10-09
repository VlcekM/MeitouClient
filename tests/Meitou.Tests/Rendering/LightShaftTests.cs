using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

public class LightShaftTests
{
    static readonly SkyClock Clock = new(54, 5, 23);

    [Fact]
    public void Air_is_strongest_at_sunrise_and_sunset_and_eases_to_the_day_value()
    {
        var o = new PostOptions();
        float At(float hour) => WorldFrame.ShaftAirByTime(o, Clock, hour);
        Assert.Equal(o.ShaftAirDawn, At(5), 4);
        Assert.Equal(o.ShaftAirDawn, At(23), 4);
        Assert.Equal(o.ShaftAirDay, At(5 + o.ShaftAirRamp), 4);
        Assert.Equal(o.ShaftAirDay, At(14), 4);
        Assert.Equal(o.ShaftAirDay, At(23 - o.ShaftAirRamp), 4);
        // Halfway through the ramp, halfway between (smoothstep), the same after sunrise as before sunset.
        float half = (o.ShaftAirDawn + o.ShaftAirDay) / 2;
        Assert.Equal(half, At(5 + o.ShaftAirRamp / 2), 4);
        Assert.Equal(half, At(23 - o.ShaftAirRamp / 2), 4);
        Assert.True(At(6) > At(7) && At(7) > At(9));
        Assert.True(At(22) > At(21) && At(21) > At(19));
    }
}
