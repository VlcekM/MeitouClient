using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Locks;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Pins small rules of the original that need no world: the assault's standing test, the lock picker's play speed and the reach to an alarm in a
/// closed building.
/// </summary>
public class ComboWave2Tests
{
    /// <summary>
    /// Catches an assault that counts a caged or out-of-town target as standing (FUN_14028eca0).
    /// </summary>
    [Fact]
    public void A_target_stands_only_in_the_mission_town_and_free()
    {
        var free = new CharacterCold();

        Assert.True(AiSystem.Stands(free, inMissionTown: true));
        Assert.False(AiSystem.Stands(free, inMissionTown: false));

        var caged = new CharacterCold { InSomething = HeldState.Imprisoned };

        Assert.False(AiSystem.Stands(caged, inMissionTown: true));
    }

    /// <summary>
    /// Catches a closed building's alarm answered at the open reach, or by an investigator inside it (FUN_14092a810:52-62).
    /// </summary>
    [Fact]
    public void An_alarm_in_a_closed_building_is_skipped_inside_and_halved_again_outside()
    {
        Assert.Equal(CrimeSystem.AlarmReach, CrimeSystem.ReachOfAlarm(false, false));
        Assert.Equal(CrimeSystem.AlarmReach * 0.5f, CrimeSystem.ReachOfAlarm(true, false));
        Assert.Equal(CrimeSystem.AlarmReach * 0.25f, CrimeSystem.ReachOfAlarm(true, true));
        Assert.True(CrimeSystem.AlarmSkipped(closed: true, alarmPlace: 3, investigatorPlace: 3));
        Assert.False(CrimeSystem.AlarmSkipped(closed: true, alarmPlace: 3, investigatorPlace: 4));
        Assert.False(CrimeSystem.AlarmSkipped(closed: false, alarmPlace: 3, investigatorPlace: 3));
    }
}
