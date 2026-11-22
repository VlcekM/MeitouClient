namespace Meitou.Simulation;

/// <summary>
/// The phases of the slow world's whole-system periodic jobs (<see cref="World.Beat"/>), engine choice: each fires on its own tick of its period, so
/// the 15, 20, 30 and 60-tick jobs never all land on the same tick (they did on every 60th, a heavy tick every few frames at 5x). The 15-tick group
/// takes distinct phases mod 15; the 30-tick group phases that none of them share mod 15. The 20-tick jobs cannot miss every 15-tick phase (gcd 5),
/// so each meets one 15-tick job once in 60 ticks.
/// </summary>
static class TickPhases
{
    /// <summary>
    /// <see cref="Population.PopulationSystem"/>'s town and squad check (every 15).
    /// </summary>
    public const int PopulationCheck = 0;

    /// <summary>
    /// <see cref="Aggro.FleeSystem"/>'s check (every 15).
    /// </summary>
    public const int FleeCheck = 3;

    /// <summary>
    /// <see cref="Trading.TradeSystem"/>'s check (every 15).
    /// </summary>
    public const int TradeCheck = 6;

    /// <summary>
    /// <see cref="Crime.ArrestSystem"/>'s spotting (every 15).
    /// </summary>
    public const int ArrestSpot = 9;

    /// <summary>
    /// <see cref="Production.ProductionSystem"/>'s errands (every 15).
    /// </summary>
    public const int Errands = 12;

    /// <summary>
    /// <see cref="Carrying.CarrySystem"/>'s rescue look (every 15, stretched by the speed steps).
    /// </summary>
    public const int RescueLook = 13;

    /// <summary>
    /// <see cref="Carrying.CarrySystem"/>'s carry link restore (every 20).
    /// </summary>
    public const int CarryRestore = 8;

    /// <summary>
    /// <see cref="Kidnap.CaptiveSystem"/>'s slaver restore (every 20).
    /// </summary>
    public const int SlaverRestore = 18;

    /// <summary>
    /// <see cref="Ai.AiSystem"/>'s package cadence (every 30, stretched by the speed steps).
    /// </summary>
    public const int AiPackages = 1;

    /// <summary>
    /// <see cref="Perception.SmellSystem"/>'s pass (every 30).
    /// </summary>
    public const int Smell = 2;

    /// <summary>
    /// <see cref="Items.FeedSystem"/>'s check (every 30).
    /// </summary>
    public const int Feed = 4;

    /// <summary>
    /// The lock system's guard rounds (every 30).
    /// </summary>
    public const int LockRounds = 5;

    /// <summary>
    /// The lock system's door bash check (every 30).
    /// </summary>
    public const int BashCheck = 7;

    /// <summary>
    /// The population's roaming check (every 60). It runs inside the population check's step, so it takes the check's phase (any other never fires);
    /// no 30 or 20-tick job shares it.
    /// </summary>
    public const int RoamCheck = PopulationCheck;
}
