using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The AI's area test by manoeuvre mode (FUN_140597790) and the range term's reach (AI::scoreTargetRange).
/// </summary>
public class ManoeuvreTests
{
}

/// <summary>
/// The turret goals' finder factor by distance and combat mode (FUN_1408ccc70).
/// </summary>
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
