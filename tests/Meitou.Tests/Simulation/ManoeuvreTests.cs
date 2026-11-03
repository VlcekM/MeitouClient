using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Tests.Simulation;

/// <summary>The AI's area test by manoeuvre mode (FUN_140597790) and the range term's reach (AI::scoreTargetRange).</summary>
public class ManoeuvreTests
{
    [Fact]
    public void Area_follows_the_manoeuvre_mode()
    {
        var me = new Vector3(0, 5, 0);
        var centre = new Vector3(1000, 0, 0);
        // ROAM_FAR: around the looker itself, under 6517.
        Assert.True(Orders.InArea(ManoeuvreMode.RoamFar, me, centre, new Vector3(6500, 5, 0)));
        Assert.False(Orders.InArea(ManoeuvreMode.RoamFar, me, centre, new Vector3(6600, 5, 0)));
        // STAY_CLOSE: 223 around the centre of movement at the looker's height; HOLD_GROUND: 63.
        Assert.True(Orders.InArea(ManoeuvreMode.StayClose, me, centre, new Vector3(1220, 5, 0)));
        Assert.False(Orders.InArea(ManoeuvreMode.StayClose, me, centre, new Vector3(1230, 5, 0)));
        Assert.True(Orders.InArea(ManoeuvreMode.HoldGround, me, centre, new Vector3(1060, 5, 0)));
        Assert.False(Orders.InArea(ManoeuvreMode.HoldGround, me, centre, new Vector3(1070, 5, 0)));
        // A zero point is always in.
        Assert.True(Orders.InArea(ManoeuvreMode.HoldGround, me, centre, Vector3.Zero));
        Assert.Equal(46f, TargetScore.RangeOf(ManoeuvreMode.HoldGround));
        Assert.Equal(223f, TargetScore.RangeOf(ManoeuvreMode.StayClose));
        Assert.Equal(200f, TargetScore.RangeOf(ManoeuvreMode.RoamFar));
    }
}

/// <summary>The turret goals' finder factor by distance and combat mode (FUN_1408ccc70).</summary>
public class TurretFactorTests
{
    [Fact]
    public void Factor_follows_distance_and_melee_mode()
    {
        Assert.Equal(1f, Meitou.Simulation.Ai.AiSystem.TurretFactor(1500 * 1500, melee: false));
        Assert.Equal(0.3f, Meitou.Simulation.Ai.AiSystem.TurretFactor(1500 * 1500 + 1, melee: false));
        Assert.Equal(0.8f, Meitou.Simulation.Ai.AiSystem.TurretFactor(600 * 600, melee: true));
        Assert.Equal(0.05f, Meitou.Simulation.Ai.AiSystem.TurretFactor(600 * 600 + 1, melee: true));
    }
}
