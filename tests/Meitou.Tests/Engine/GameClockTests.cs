using Meitou.Engine.Time;

namespace Meitou.Tests.Engine;

public class GameClockTests
{
    const double Hour = GameClock.SecondsPerGameHour;

    [Fact]
    public void Start_values_and_wrapping()
    {
        var game = new GameClock();
        Assert.Equal(10, game.HourOfDay, 9);
        Assert.Equal(1, game.Day);
        Assert.Equal(0, game.HoursSinceStart);
        Assert.Equal("10:00", game.TimeText);
        Assert.Equal("Day: 1", game.DayText);
        game.Advance(14 * Hour + 0.01);   // 10:00 + 14 h = 00:00 of day 2
        Assert.Equal(2, game.Day);
        Assert.Equal("00:00", game.TimeText);
        game.Advance(1.5 * Hour);
        Assert.Equal("01:30", game.TimeText);
        game.SetHourOfDay(6);
        Assert.Equal(6, game.HourOfDay, 9);
        Assert.Equal(2, game.Day);
        Assert.Equal(7, new GameClock(7.99).Hour);
        Assert.Equal(59, new GameClock(7.9999).Minute);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5.5, 0.25)]
    [InlineData(12, 1)]
    [InlineData(22.5, 0.25)]
    [InlineData(23.5, 0)]
    public void Daylight_ramps_over_two_hours_after_sunrise_and_before_sunset(double hour, double factor)
    {
        Assert.Equal(factor, new GameClock(startHour: hour).DaylightFactor, 9);
    }
}
