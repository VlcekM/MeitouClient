using Meitou.Engine.Time;

namespace Meitou.Tests.Engine;

public class GameClockTests
{
    const double Hour = GameClock.SecondsPerGameHour;

    [Fact]
    public void A_game_hour_is_1200_over_11_game_seconds()
    {
        Assert.Equal(109.0909090909, Hour, 9);
        var game = new GameClock(startHour: 0, startDay: 1);
        game.Advance(Hour);
        Assert.Equal(1, game.HoursSinceStart, 9);
        // A day at speed 1 is 24 x 109.09 s = 43.64 real minutes (game-loop.md).
        game.Advance(23 * Hour);
        Assert.Equal(2, game.Day);
        Assert.Equal(0, game.HourOfDay, 6);
    }

    [Fact]
    public void Start_values_and_wrapping()
    {
        var game = new GameClock();
        Assert.Equal(13, game.HourOfDay, 9);
        Assert.Equal(1, game.Day);
        Assert.Equal(0, game.HoursSinceStart);
        Assert.Equal("13:00", game.TimeText);
        Assert.Equal("Day: 1", game.DayText);
        game.Advance(11 * Hour + 0.01);   // 13:00 + 11 h = 00:00 of day 2
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

    [Fact]
    public void Minutes_are_the_floor_of_the_fraction()
    {
        var game = new GameClock(startHour: 13 + 39.9 / 60);
        Assert.Equal("13:39", game.TimeText);
    }

    [Fact]
    public void Daytime_is_strictly_between_sunrise_and_sunset()
    {
        var game = new GameClock(startHour: 5);   // exactly sunrise 5
        Assert.False(game.IsDaytime);
        Assert.Equal(0, game.DaylightFactor);
        game.Advance(0.1);
        Assert.True(game.IsDaytime);
        Assert.False(new GameClock(startHour: 23).IsDaytime);
        Assert.True(new GameClock(startHour: 22.99).IsDaytime);
        Assert.False(new GameClock(startHour: 3).IsDaytime);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 0)]
    [InlineData(5.5, 0.25)]
    [InlineData(6, 0.5)]
    [InlineData(7, 1)]
    [InlineData(12, 1)]
    [InlineData(21, 1)]
    [InlineData(22, 0.5)]
    [InlineData(22.5, 0.25)]
    [InlineData(23, 0)]
    [InlineData(23.5, 0)]
    public void Daylight_ramps_over_two_hours_after_sunrise_and_before_sunset(double hour, double factor)
    {
        Assert.Equal(factor, new GameClock(startHour: hour).DaylightFactor, 9);
    }

    [Fact]
    public void Sunset_not_after_sunrise_gives_six_and_twenty_and_days_per_year_is_kept()
    {
        var bad = new GameClock(sunrise: 10, sunset: 10);
        Assert.Equal(6, bad.Sunrise);
        Assert.Equal(20, bad.Sunset);
        var game = new GameClock(sunrise: 5, sunset: 23, daysPerYear: 100);
        Assert.Equal(5, game.Sunrise);
        Assert.Equal(100, game.DaysPerYear);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameClock(daysPerYear: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => game.Advance(-1));
    }
}
